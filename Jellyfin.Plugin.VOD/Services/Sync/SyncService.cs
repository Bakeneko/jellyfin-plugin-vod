using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Orchestrates the full sync of a single source: enumerate entries, fetch metadata,
    /// assign episode numbers, write .strm files, upsert the database and clean up orphans.
    /// </summary>
    public sealed partial class SyncService
    {
        private const int MetadataParallelism = 4;

        private readonly IVodDatabase _db;
        private readonly YtDlpService _ytDlp;
        private readonly LibraryWriter _library;
        private readonly IVodLocalizer _localizer;
        private readonly ILogger<SyncService> _logger;

        /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
        public SyncService(
            IVodDatabase db,
            YtDlpService ytDlp,
            LibraryWriter library,
            IVodLocalizer localizer,
            ILogger<SyncService> logger)
        {
            _db = db;
            _ytDlp = ytDlp;
            _library = library;
            _localizer = localizer;
            _logger = logger;
        }

        /// <summary>
        /// Performs a full sync for one source. Updates <see cref="SourceRecord"/> in the database
        /// with the outcome (success or error) regardless of whether the sync succeeds.
        /// </summary>
        public async Task SyncSourceAsync(SourceDefinition source, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || string.IsNullOrWhiteSpace(config.LibraryBasePath))
            {
                LogSkippedNoConfig(source.Url);
                return;
            }

            var syncStart = DateTimeOffset.UtcNow;
            var sourceRecord = _db.GetSource(source.Url) ?? new SourceRecord { Url = source.Url };

            try
            {
                await SyncCoreAsync(source, config, syncStart, sourceRecord, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sourceRecord.LastError = ex.Message;
                _db.UpsertSource(sourceRecord);
                LogSyncFailed(source.Url, ex);
            }
        }

        private async Task SyncCoreAsync(
            SourceDefinition source,
            PluginConfiguration config,
            DateTimeOffset syncStart,
            SourceRecord sourceRecord,
            CancellationToken ct)
        {
            LogSyncStarted(source.Url);

            // Step 1: detect source type
            var sourceType = SourceTypeDetector.Detect(source.Url);

            // Step 2: enumerate entries (or treat single video as one-entry playlist)
            var (playlist, preloaded) = await FetchPlaylistAsync(source, sourceType, ct).ConfigureAwait(false);
            if (playlist is null)
                throw new InvalidOperationException($"yt-dlp failed to enumerate {source.Url}");

            // Step 3: resolve series name
            var seriesName = !string.IsNullOrWhiteSpace(source.Name)
                ? source.Name
                : (playlist.Title ?? source.Url);

            // Ensure the FK parent row exists before inserting videos
            sourceRecord.Name = seriesName;
            _db.UpsertSource(sourceRecord);

            // Step 4: per-video metadata
            // Pre-filter: skip entries already in DB — their metadata is already stored.
            // yt-dlp returns entries newest-first, so iterating in order allows early stop
            // on RetentionDays: once we hit a video older than the cutoff, everything after
            // it is also too old.
            var metadataMap = new Dictionary<string, VideoMetadata>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in preloaded)
                metadataMap[k] = v;

            var existingVideos = _db.GetVideosBySource(source.Url);

            var knownNativeIds = existingVideos
                .Where(v => v.NativeId is not null)
                .Select(v => v.NativeId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A stored entry's metadata is immutable except while it is live: a stream that has
            // since ended would keep IsLive set and its scheduled-broadcast synopsis forever.
            var skipMetadataIds = existingVideos
                .Where(v => v.NativeId is not null && !v.IsLive)
                .Select(v => v.NativeId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var retentionCutoff = source.RetentionDays > 0
                ? syncStart.AddDays(-source.RetentionDays)
                : (DateTimeOffset?)null;

            // Touch existing videos that are still in the playlist so CleanupOrphans does not
            // delete them (their synced_at would otherwise remain from the previous sync).
            var seenExistingIds = playlist.Entries
                .Select(e => e.Id)
                .Where(knownNativeIds.Contains)
                .ToList();
            _db.TouchVideos(source.Url, seenExistingIds, syncStart);

            var newEntries = playlist.Entries
                .Where(e => !metadataMap.ContainsKey(e.Id) && !skipMetadataIds.Contains(e.Id))
                .ToList();

            if (retentionCutoff.HasValue)
            {
                // Sequential fetch with early stop: entries are newest-first, so break as soon
                // as we find a video published before the retention cutoff.
                foreach (var entry in newEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var meta = await _ytDlp.GetVideoMetadataAsync(entry.VideoUrl, ct)
                            .ConfigureAwait(false);
                        if (meta is null) continue;
                        var publishedAt = ParseDate(meta);
                        if (publishedAt.HasValue && publishedAt.Value < retentionCutoff.Value)
                            break;
                        metadataMap[entry.Id] = meta;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogMetadataFailed(entry.VideoUrl, ex);
                    }
                }
            }
            else
            {
                // No retention cutoff: fetch all new entries in parallel.
                var semaphore = new SemaphoreSlim(MetadataParallelism);
                var metaTasks = newEntries.Select(async entry =>
                {
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var meta = await _ytDlp.GetVideoMetadataAsync(entry.VideoUrl, ct)
                            .ConfigureAwait(false);
                        if (meta is not null)
                            lock (metadataMap)
                                metadataMap[entry.Id] = meta;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogMetadataFailed(entry.VideoUrl, ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(metaTasks).ConfigureAwait(false);
            }

            // Step 5 + 6 + 7: assign episode numbers, write .strm, upsert DB
            ProcessEntries(
                source, sourceType, playlist.Entries, metadataMap, existingVideos,
                seriesName, config, syncStart, ct);

            // Step 8: cleanup orphans — videos no longer in the playlist (deleted from source)
            // or older than RetentionDays.
            CleanupOrphans(source, config.LibraryBasePath, syncStart, retentionCutoff, playlist.Entries.Count);

            // Step 9: update source record
            var videoCount = _db.GetVideosBySource(source.Url).Count;
            sourceRecord.LastSyncedAt = DateTimeOffset.UtcNow;
            sourceRecord.LastError = null;
            sourceRecord.VideoCount = videoCount;
            _db.UpsertSource(sourceRecord);

            LogSyncFinished(source.Url, videoCount);
        }

        private async Task<(PlaylistInfo? info, Dictionary<string, VideoMetadata> preloaded)> FetchPlaylistAsync(
            SourceDefinition source,
            SourceType sourceType,
            CancellationToken ct)
        {
            var preloaded = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase);

            // Single-video sources: fetch full metadata and wrap it as a one-entry playlist
            if (sourceType is SourceType.YoutubeVideo
                or SourceType.ArteVideo
                or SourceType.FranceTvVideo
                or SourceType.Generic)
            {
                var meta = await _ytDlp.GetVideoMetadataAsync(source.Url, ct).ConfigureAwait(false);
                if (meta is null)
                    return (null, preloaded);

                preloaded[meta.Id] = meta;
                var info = new PlaylistInfo
                {
                    Title = meta.Title,
                    ThumbnailUrl = meta.Thumbnail,
                    Description = meta.Description,
                    Entries =
                    [
                        new PlaylistEntry
                        {
                            Id = meta.Id,
                            VideoUrl = source.Url,
                            Title = meta.Title,
                            IeKey = meta.ExtractorKey ?? string.Empty,
                            Duration = meta.Duration
                        }
                    ]
                };
                return (info, preloaded);
            }

            // Playlist / channel sources: flat-playlist listing
            int? maxEntries = source.MaxVideosPerSource > 0 ? source.MaxVideosPerSource : null;
            DateTimeOffset? dateAfter = source.RetentionDays > 0
                ? DateTimeOffset.UtcNow.AddDays(-source.RetentionDays)
                : null;
            var playlist = await _ytDlp.GetPlaylistEntriesAsync(source.Url, maxEntries, dateAfter, ct)
                .ConfigureAwait(false);
            return (playlist, preloaded);
        }

        private void ProcessEntries(
            SourceDefinition source,
            SourceType sourceType,
            IReadOnlyList<PlaylistEntry> entries,
            Dictionary<string, VideoMetadata> metadataMap,
            IReadOnlyList<VideoRecord> existingVideos,
            string seriesName,
            PluginConfiguration config,
            DateTimeOffset syncStart,
            CancellationToken ct)
        {
            // Build a map of existing videos by NativeId for update detection
            var existingByNativeId = existingVideos
                .Where(v => v.NativeId is not null)
                .ToDictionary(v => v.NativeId!, StringComparer.OrdinalIgnoreCase);

            // Compute episode numbers for NEW entries grouped by date
            // Key: nativeId → (season, episode)
            var assignments = AssignEpisodeNumbers(entries, metadataMap, existingByNativeId, existingVideos);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (!metadataMap.TryGetValue(entry.Id, out var meta))
                    continue;

                var publishedAt = ParseDate(meta);
                var existing = existingByNativeId.GetValueOrDefault(entry.Id);

                // Resolve effective title (Arte: use alt_title when title == series name)
                var effectiveTitle = ResolveTitle(meta, seriesName);

                // Build or update VideoRecord
                var record = existing ?? new VideoRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceUrl = source.Url,
                    NativeId = meta.Id,
                    ExtractorKey = meta.ExtractorKey,
                    VideoUrl = entry.VideoUrl
                };

                record.Title = effectiveTitle;
                // Upcoming streams: prepend scheduled broadcast date to description
                // The localized prefix is resolved via ILocalizationManager (see Task 1b)
                record.Description = BuildDescription(meta, publishedAt, _localizer);
                record.ThumbnailUrl = meta.Thumbnail;
                record.DurationSeconds = meta.Duration.HasValue ? (int)meta.Duration.Value : null;
                record.AudioLangs = meta.AudioLanguages.Count > 0
                    ? JsonSerializer.Serialize(meta.AudioLanguages) : null;
                record.SubtitleLangs = meta.SubtitleLanguages.Count > 0
                    ? JsonSerializer.Serialize(meta.SubtitleLanguages) : null;
                record.SourceType = sourceType.ToString();
                record.IsLive = meta.IsLive;
                record.SyncedAt = syncStart;

                // Preserve existing publication date (immutable once set)
                if (!record.PublishedAt.HasValue)
                {
                    if (publishedAt.HasValue)
                    {
                        record.PublishedAt = publishedAt;
                        record.DateIsFallback = false;
                    }
                    else
                    {
                        record.PublishedAt = syncStart;
                        record.DateIsFallback = true;
                    }
                }

                // Prefer native season/episode over date-based fallback, always overwriting
                // a previously stored date-based value when a better source becomes available.
                // Priority: (1) yt-dlp native fields, (2) title parsing, (3) date-based formula.
                if (meta.SeasonNumber.HasValue && meta.EpisodeNumber.HasValue)
                {
                    record.SeasonNumber = meta.SeasonNumber.Value;
                    record.EpisodeNumber = meta.EpisodeNumber.Value;
                }
                else if (TitleSeasonEpisodeParser.TryParse(effectiveTitle, out var parsedSeason, out var parsedEpisode))
                {
                    record.SeasonNumber = parsedSeason;
                    record.EpisodeNumber = parsedEpisode;
                }
                else if (!record.SeasonNumber.HasValue && assignments.TryGetValue(entry.Id, out var ep))
                {
                    record.SeasonNumber = ep.season;
                    record.EpisodeNumber = ep.episode;
                }

                // Write .strm
                var streamUrl = BuildStreamUrl(config.LocalBaseUrl, record.Id);
                try
                {
                    var strmPath = _library.WriteStrm(
                        record, source.Mode, seriesName, config.LibraryBasePath, streamUrl);
                    record.StrmPath = strmPath;
                }
                catch (Exception ex)
                {
                    LogStrmWriteFailed(entry.VideoUrl, ex);
                    continue;
                }

                _db.UpsertVideo(record);
            }
        }

        private Dictionary<string, (int season, int episode)> AssignEpisodeNumbers(
            IReadOnlyList<PlaylistEntry> entries,
            Dictionary<string, VideoMetadata> metadataMap,
            Dictionary<string, VideoRecord> existingByNativeId,
            IReadOnlyList<VideoRecord> existingVideos)
        {
            var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

            // Group new entries (no existing episode number) by calendar date
            var newByDate = new Dictionary<DateOnly, List<(PlaylistEntry entry, DateTimeOffset date)>>();
            foreach (var entry in entries)
            {
                if (existingByNativeId.TryGetValue(entry.Id, out var existing)
                    && existing.SeasonNumber.HasValue)
                    continue; // Already has a number — skip

                if (!metadataMap.TryGetValue(entry.Id, out var meta))
                    continue;

                var date = ParseDate(meta);
                if (!date.HasValue)
                    continue;

                var key = DateOnly.FromDateTime(date.Value.UtcDateTime.Date);
                if (!newByDate.TryGetValue(key, out var list))
                    newByDate[key] = list = [];
                list.Add((entry, date.Value));
            }

            // Rank continues after the entries already numbered on that same UTC day.
            var numberedPerDay = existingVideos
                .Where(v => v.EpisodeNumber.HasValue && v.PublishedAt.HasValue)
                .GroupBy(v => DateOnly.FromDateTime(v.PublishedAt!.Value.UtcDateTime.Date))
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var (date, items) in newByDate)
            {
                var rank = numberedPerDay.GetValueOrDefault(date) + 1;
                // Sort new entries for this date by NativeId for determinism
                foreach (var (entry, date2) in items.OrderBy(x => x.entry.Id, StringComparer.Ordinal))
                {
                    var assignment = EpisodeNumberCalculator.TryCalculate(date2, rank);
                    if (assignment is null)
                    {
                        LogRankOverflow(entry.VideoUrl, date, rank);
                        break;
                    }
                    result[entry.Id] = assignment.Value;
                    rank++;
                }
            }

            return result;
        }

        private void CleanupOrphans(SourceDefinition source, string libraryBasePath, DateTimeOffset syncStart, DateTimeOffset? retentionCutoff, int enumeratedCount)
        {
            if (enumeratedCount == 0)
            {
                LogSkippingCleanup(source.Url);
                return;
            }

            var orphans = _db.GetOrphanedVideoUuids(source.Url, syncStart, retentionCutoff);
            foreach (var uuid in orphans)
            {
                var video = _db.GetVideoByUuid(uuid);
                if (video?.StrmPath is not null)
                    _library.DeleteStrm(video.StrmPath, libraryBasePath);
                _db.DeleteVideo(uuid);
                LogDeletedOrphan(uuid);
            }
        }

        private static string? BuildDescription(
            VideoMetadata meta,
            DateTimeOffset? publishedAt,
            IVodLocalizer localizer)
        {
            if (!meta.IsUpcoming || !publishedAt.HasValue)
                return meta.Description;

            var formatted = publishedAt.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            // Key "UpcomingBroadcast" → e.g. "Diffusion prévue : {0} UTC"
            var prefix = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                localizer.Get("UpcomingBroadcast"),
                formatted);
            return string.IsNullOrEmpty(meta.Description)
                ? prefix
                : $"{prefix}\n\n{meta.Description}";
        }

        private static string ResolveTitle(VideoMetadata meta, string seriesName)
        {
            // Arte stores the series name in `title` and the episode title in `alt_title`
            if (!string.IsNullOrWhiteSpace(meta.AltTitle)
                && string.Equals(meta.Title, seriesName, StringComparison.OrdinalIgnoreCase))
            {
                return meta.AltTitle;
            }
            return meta.Title ?? meta.Id;
        }

        private static DateTimeOffset? ParseDate(VideoMetadata meta)
        {
            if (meta.UploadDate is { Length: 8 } ud
                && int.TryParse(ud[..4], out var year)
                && int.TryParse(ud[4..6], out var month)
                && int.TryParse(ud[6..8], out var day))
            {
                try { return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero); }
                catch (ArgumentOutOfRangeException) { }
            }
            // Scheduled start time for upcoming streams
            if (meta.ReleaseTimestamp.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(meta.ReleaseTimestamp.Value);
            if (meta.Timestamp.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(meta.Timestamp.Value);
            return null;
        }

        private static string BuildStreamUrl(string localBaseUrl, string uuid)
            => $"{localBaseUrl.TrimEnd('/')}/VOD/stream/{uuid}.m3u8";

        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting sync for {SourceUrl}")]
        private partial void LogSyncStarted(string sourceUrl);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Sync finished for {SourceUrl}: {VideoCount} videos")]
        private partial void LogSyncFinished(string sourceUrl, int videoCount);

        [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Sync failed for {SourceUrl}")]
        private partial void LogSyncFailed(string sourceUrl, Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to fetch metadata for {VideoUrl}")]
        private partial void LogMetadataFailed(string videoUrl, Exception exception);

        [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Failed to write .strm for {VideoUrl}")]
        private partial void LogStrmWriteFailed(string videoUrl, Exception exception);

        [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Deleted orphan {Uuid}")]
        private partial void LogDeletedOrphan(string uuid);

        [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Too many videos on {Date} for {VideoUrl}: rank {Rank} > 999 — skipping remaining entries for this date")]
        private partial void LogRankOverflow(string videoUrl, DateOnly date, int rank);

        [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Skipping sync for {SourceUrl}: LibraryBasePath not configured")]
        private partial void LogSkippedNoConfig(string sourceUrl);

        [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
            Message = "Skipping orphan cleanup for {SourceUrl} — no entries were enumerated (possible transient failure)")]
        private partial void LogSkippingCleanup(string sourceUrl);
    }
}

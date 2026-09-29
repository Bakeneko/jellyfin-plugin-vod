using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Orchestrates the full sync of a single source: enumerate entries, fetch metadata,
    /// assign episode numbers, upsert the database and clean up orphans.
    /// </summary>
    public sealed partial class SyncService
    {
        private const int MetadataParallelism = 4;

        private readonly IVodDatabase _db;
        private readonly YtDlpService _ytDlp;
        private readonly LibraryPublisher _publisher;
        private readonly IVodLocalizer _localizer;
        private readonly ILogger<SyncService> _logger;

        /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
        public SyncService(
            IVodDatabase db,
            YtDlpService ytDlp,
            LibraryPublisher publisher,
            IVodLocalizer localizer,
            ILogger<SyncService> logger)
        {
            _db = db;
            _ytDlp = ytDlp;
            _publisher = publisher;
            _localizer = localizer;
            _logger = logger;
        }

        /// <summary>
        /// Makes sure the configured library can anchor published items, letting Jellyfin index its
        /// location the first time. Does nothing while the plugin has no library configured.
        /// </summary>
        /// <remarks>
        /// Belongs before anything reaches the network. Resolving the anchor per source instead costs
        /// a yt-dlp enumeration for each one before they all fail on the same error, and because a
        /// failed sync leaves <see cref="SourceRecord.LastSyncedAt"/> untouched, the per-source
        /// throttle never engages and that cost repeats on every wake-up.
        /// </remarks>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A task that completes once the library is ready to publish into.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the library cannot be resolved or indexed.
        /// </exception>
        public async Task PrepareLibraryAsync(
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            foreach (var (role, name) in LibraryRouting.NeededLibraries(config))
            {
                // Named by role rather than by value: there is no name to quote, and
                // "No library named ''." tells the administrator nothing about which of the two
                // settings is blank.
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException($"No {role} library is configured.");

                await _publisher.PrepareTargetAsync(name, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Removes every trace of the sources the configuration no longer lists: their library
        /// items, the containers holding them, and their rows.
        /// </summary>
        /// <remarks>
        /// Runs ahead of the per-source loop, because that loop only ever reads the configuration and
        /// would never look at a source no longer in it. A source with <c>Enabled = false</c> is NOT
        /// retired — it is paused, and its items stay: deletion cascades to the user's watch history,
        /// which recreating the item does not bring back, so a cleared checkbox must not destroy data.
        /// <para>
        /// Synchronous: nothing it does awaits. Keeping it <c>async</c> without an <c>await</c> would
        /// be CS1998, which this project turns into a build error.
        /// </para>
        /// </remarks>
        /// <param name="config">The plugin configuration this pass runs against.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The number of sources retired.</returns>
        public int RetireRemovedSources(PluginConfiguration config, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            // Every configured source, enabled or not — only absence from the configuration retires.
            var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in config.Sources)
                configured.Add(definition.Url);

            var retired = new List<SourceRecord>();
            foreach (var record in _db.GetAllSources())
            {
                if (!configured.Contains(record.Url))
                    retired.Add(record);
            }

            if (retired.Count == 0)
                return 0;

            // Both configured libraries: a removed source's mode is gone from the configuration, so
            // its items can be in either one. Best effort — one that no longer resolves is skipped.
            var configuredAnchors = new List<Folder>();
            foreach (var name in LibraryRouting.ConfiguredNames(config))
            {
                try
                {
                    configuredAnchors.Add(_publisher.ResolveTarget(name).Anchor);
                }
                catch (InvalidOperationException ex)
                {
                    LogSweepAnchorUnavailable(name, ex.Message);
                }
            }

            var count = 0;
            foreach (var record in retired)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read BEFORE the row is deleted: it is the only thing that knows where a source
                // published when that library is neither of the two currently configured.
                var anchors = new List<Folder>(configuredAnchors);
                if (record.PublishedAnchorId is { } anchorId
                    && _publisher.FindAnchorById(anchorId) is { } stored)
                {
                    anchors.Add(stored);
                }

                if (anchors.Count == 0)
                {
                    LogRetirementSkipped(record.Url);
                    continue;
                }

                // Rows first: the purge spares any video the database still knows, so purging before
                // the row is gone would leave every item exactly where it is.
                _db.DeleteSource(record.Url);

                _publisher.PurgeSource(
                    anchors, record, _db.GetAllVideoIds().ToHashSet(StringComparer.OrdinalIgnoreCase));

                LogRetiredSource(record.Url);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Performs a full sync for one source. Updates <see cref="SourceRecord"/> in the database
        /// with the outcome (success or error) regardless of whether the sync succeeds.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="source">The source to sync.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        public async Task SyncSourceAsync(
            PluginConfiguration config, SourceDefinition source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(source);

            if (string.IsNullOrWhiteSpace(LibraryRouting.NameFor(config, source.Mode)))
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
            var seriesName = ResolveSeriesName(source, playlist);

            // Ensure the FK parent row exists before inserting videos
            sourceRecord.Name = seriesName;
            // Keep what the platform last published; an empty field must not erase a good value.
            if (!string.IsNullOrWhiteSpace(playlist.Description))
                sourceRecord.SeriesSynopsis = playlist.Description;
            if (!string.IsNullOrWhiteSpace(playlist.ThumbnailUrl))
                sourceRecord.SeriesThumbnailUrl = playlist.ThumbnailUrl;
            if (!string.IsNullOrWhiteSpace(playlist.BannerUrl))
                sourceRecord.SeriesBannerUrl = playlist.BannerUrl;
            sourceRecord.Tags = playlist.Tags.Count > 0 ? JsonSerializer.Serialize(playlist.Tags) : null;
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

            // Step 5 + 6 + 7: assign episode numbers, upsert DB
            ProcessEntries(
                source, playlist.Entries, metadataMap, existingVideos,
                seriesName, syncStart, ct);

            // Step 8: cleanup orphans — videos no longer in the playlist (deleted from source)
            // or older than RetentionDays.
            // Runs BEFORE publication, not after: the reconciliation reads the database to decide what
            // the library should hold, so leaving the dead rows in place made every removal take two
            // passes — one to drop the row, the next to notice the item was unaccounted for. This only
            // touches the database, so nothing here depends on the library being up to date.
            CleanupOrphans(source, syncStart, retentionCutoff, playlist.Entries.Count);

            // Step 7b: publish items to the Jellyfin library.
            // Resolved per sync: fails loudly when the library is missing or unscanned.
            var target = _publisher.ResolveTarget(LibraryRouting.NameFor(config, source.Mode));
            var otherAnchors = SweepAnchorsFor(config, source, sourceRecord);

            // Publication is driven by what the database holds, not by what this pass happened
            // to re-fetch: entries already known are deliberately skipped for metadata, and
            // would otherwise never reach the library.
            await _publisher.ReconcileAsync(
                    target, otherAnchors, sourceRecord, source.Mode, seriesName,
                    _db.GetVideosBySource(source.Url),
                    _db.GetAllVideoIds().ToHashSet(StringComparer.OrdinalIgnoreCase),
                    config.LocalBaseUrl, config.UseReleaseDateAsDateAdded, ct)
                .ConfigureAwait(false);

            // Step 9: update source record
            var videoCount = _db.GetVideosBySource(source.Url).Count;
            sourceRecord.LastSyncedAt = DateTimeOffset.UtcNow;
            sourceRecord.LastError = null;
            sourceRecord.VideoCount = videoCount;
            sourceRecord.PublishedAnchorId = target.Anchor.Id;
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
                    // A single-video source has no channel of its own to describe, so its tags are
                    // the video's; the series it may produce is that one video.
                    Tags = meta.Tags,
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
            IReadOnlyList<PlaylistEntry> entries,
            Dictionary<string, VideoMetadata> metadataMap,
            IReadOnlyList<VideoRecord> existingVideos,
            string seriesName,
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
                // FirstSeenAt is the one property never refreshed below: it must keep the date of the
                // FIRST sync that saw this video, which is what makes the published date added
                // recomputable in both directions. UpsertVideo's COALESCE enforces the same invariant
                // on the database side.
                var record = existing ?? new VideoRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceUrl = source.Url,
                    NativeId = meta.Id,
                    ExtractorKey = meta.ExtractorKey,
                    VideoUrl = entry.VideoUrl,
                    FirstSeenAt = syncStart
                };

                record.Title = effectiveTitle;
                // Upcoming streams: prepend scheduled broadcast date to description
                // The localized prefix is resolved via IVodLocalizer
                record.Description = BuildDescription(meta, publishedAt, _localizer);
                record.ThumbnailUrl = meta.Thumbnail;
                record.Tags = meta.Tags.Count > 0 ? JsonSerializer.Serialize(meta.Tags) : null;
                record.Categories = meta.Categories.Count > 0
                    ? JsonSerializer.Serialize(meta.Categories) : null;
                record.Channels = meta.Channels.Count > 0
                    ? JsonSerializer.Serialize(meta.Channels) : null;
                record.DurationSeconds = meta.Duration.HasValue ? (int)meta.Duration.Value : null;
                record.AudioLangs = meta.AudioLanguages.Count > 0
                    ? JsonSerializer.Serialize(meta.AudioLanguages) : null;
                record.SubtitleLangs = meta.SubtitleLanguages.Count > 0
                    ? JsonSerializer.Serialize(meta.SubtitleLanguages) : null;
                record.IsLive = meta.IsLive;
                record.SyncedAt = syncStart;

                // Preserve existing publication date (immutable once set)
                record.PublishedAt ??= publishedAt ?? syncStart;

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

        private void CleanupOrphans(SourceDefinition source, DateTimeOffset syncStart, DateTimeOffset? retentionCutoff, int enumeratedCount)
        {
            if (enumeratedCount == 0)
            {
                LogSkippingCleanup(source.Url);
                return;
            }

            var orphans = _db.GetOrphanedVideoUuids(source.Url, syncStart, retentionCutoff);
            foreach (var uuid in orphans)
            {
                _db.DeleteVideo(uuid);
                LogDeletedOrphan(uuid);
            }
        }

        /// <summary>
        /// Picks the name a source's series carries.
        /// </summary>
        /// <remarks>
        /// When <c>source.Name</c> is not set, delegates the platform-specific rule to
        /// <see cref="SeriesNaming.Resolve"/>, where the channel-vs-playlist distinction and its
        /// rationale are documented.
        /// </remarks>
        private static string ResolveSeriesName(SourceDefinition source, PlaylistInfo playlist)
            => !string.IsNullOrWhiteSpace(source.Name)
                ? source.Name
                : SeriesNaming.Resolve(source.Url, playlist);

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

        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting sync for {SourceUrl}")]
        private partial void LogSyncStarted(string sourceUrl);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Sync finished for {SourceUrl}: {VideoCount} videos")]
        private partial void LogSyncFinished(string sourceUrl, int videoCount);

        [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Sync failed for {SourceUrl}")]
        private partial void LogSyncFailed(string sourceUrl, Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to fetch metadata for {VideoUrl}")]
        private partial void LogMetadataFailed(string videoUrl, Exception exception);

        [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Deleted orphan {Uuid}")]
        private partial void LogDeletedOrphan(string uuid);

        [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Too many videos on {Date} for {VideoUrl}: rank {Rank} > 999 — skipping remaining entries for this date")]
        private partial void LogRankOverflow(string videoUrl, DateOnly date, int rank);

        [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Skipping sync for {SourceUrl}: LibraryName not configured")]
        private partial void LogSkippedNoConfig(string sourceUrl);

        [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
            Message = "Skipping orphan cleanup for {SourceUrl} — no entries were enumerated (possible transient failure)")]
        private partial void LogSkippingCleanup(string sourceUrl);

        [LoggerMessage(EventId = 10, Level = LogLevel.Information,
            Message = "Retired {SourceUrl}: no longer in the configuration, its items and rows are gone")]
        private partial void LogRetiredSource(string sourceUrl);

        [LoggerMessage(EventId = 12, Level = LogLevel.Warning,
            Message = "Sync: cannot retire {SourceUrl} — no configured library resolves")]
        private partial void LogRetirementSkipped(string sourceUrl);

        /// <summary>
        /// The anchors other than the target that may still hold this source's items.
        /// </summary>
        /// <remarks>
        /// Best effort by design: an anchor that no longer resolves is simply absent, because it only
        /// ever serves cleanup. The stored anchor covers the one case the other configured library
        /// cannot — changing a source's mode AND its library in the same edit leaves the old target as
        /// neither of the two currently configured.
        /// </remarks>
        private List<Folder> SweepAnchorsFor(
            PluginConfiguration config, SourceDefinition source, SourceRecord record)
        {
            var anchors = new List<Folder>();

            var otherMode = source.Mode == SourceMode.Movies ? SourceMode.Series : SourceMode.Movies;
            var otherName = LibraryRouting.NameFor(config, otherMode);
            if (!string.IsNullOrWhiteSpace(otherName))
            {
                try
                {
                    anchors.Add(_publisher.ResolveTarget(otherName).Anchor);
                }
                catch (InvalidOperationException ex)
                {
                    LogSweepAnchorUnavailable(otherName, ex.Message);
                }
            }

            if (record.PublishedAnchorId is { } anchorId
                && _publisher.FindAnchorById(anchorId) is { } stored)
            {
                anchors.Add(stored);
            }

            return anchors;
        }

        [LoggerMessage(EventId = 14, Level = LogLevel.Debug,
            Message = "Sync: not sweeping library '{LibraryName}', it does not resolve: {Reason}")]
        private partial void LogSweepAnchorUnavailable(string libraryName, string reason);
    }
}

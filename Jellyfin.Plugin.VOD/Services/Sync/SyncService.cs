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
using MediaBrowser.Model.Tasks;
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
        private readonly ITaskManager _tasks;

        /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
        /// <param name="db">The VOD database.</param>
        /// <param name="ytDlp">The yt-dlp wrapper used to enumerate and fetch metadata.</param>
        /// <param name="publisher">The library publisher that reconciles items in Jellyfin.</param>
        /// <param name="localizer">Provides localized strings for published items.</param>
        /// <param name="logger">The logger for this service.</param>
        /// <param name="tasks">Used to wake the stream description task when a pass created something.</param>
        public SyncService(
            IVodDatabase db,
            YtDlpService ytDlp,
            LibraryPublisher publisher,
            IVodLocalizer localizer,
            ILogger<SyncService> logger,
            ITaskManager tasks)
        {
            _db = db;
            _ytDlp = ytDlp;
            _publisher = publisher;
            _localizer = localizer;
            _logger = logger;
            _tasks = tasks;
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
        /// <returns>The number of videos the pass created; zero when it was skipped or failed.</returns>
        public async Task<int> SyncSourceAsync(
            PluginConfiguration config, SourceDefinition source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(source);

            if (string.IsNullOrWhiteSpace(LibraryRouting.NameFor(config, source.Mode)))
            {
                LogSkippedNoConfig(source.Url);
                return 0;
            }

            var syncStart = DateTimeOffset.UtcNow;
            var sourceRecord = _db.GetSource(source.Url) ?? new SourceRecord { Url = source.Url };

            // Hold the per-source lock for the full write path: SyncCoreAsync writes to the
            // database and then reconciles (which deletes library items). Concurrent passes over
            // the same source would fight over what exists.
            using var hold = await SourceLock.AcquireAsync(source.Url, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return await SyncCoreAsync(source, config, syncStart, sourceRecord, cancellationToken)
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
                return 0;
            }
        }

        private async Task<int> SyncCoreAsync(
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

            // A stored entry's metadata is immutable except while it is live or scheduled: a stream
            // that has since ended would keep its live_status and broadcast synopsis forever.
            // Rows that have never been given a payload (written before the extras column existed)
            // are excluded from the skip set, so they are probed on EVERY pass until a fetch
            // answers — one yt-dlp process per such row per sync. That recurring cost is accepted
            // deliberately; see HasPayload for why remembering a failure instead is worse.
            var skipMetadataIds = existingVideos
                .Where(v => v.NativeId is not null && !MustRefetch(v.LiveStatus) && HasPayload(v.Extras))
                .Select(v => v.NativeId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // One entry per video before anything reads the listing: a listing can repeat an
            // entry (measured in production), and two occurrences of an unknown dead entry would
            // otherwise mint two rows for one native id — which the UNIQUE index of v8's
            // predecessor refuses, failing the whole pass.
            var listing = DeduplicateEntries(playlist.Entries, metadataMap);

            // What the listing itself says, for free, before a single call is made. On YouTube
            // this is most of the work: a dead entry is a template of nulls, and every one of them
            // used to cost a failing call on every pass.
            var verdict = ApplyListingVerdict(listing, existingVideos, syncStart);
            foreach (var row in verdict.Updated)
            {
                row.SourceUrl = source.Url;
                _db.UpsertVideo(row);
            }

            if (verdict.Unavailable.Count > 0)
                LogListingDeclaredUnavailable(source.Url, verdict.Unavailable.Count);

            // The rank belongs to the listing, so every row gets it — including the ones this pass
            // will not fetch, which are exactly the rows a resolver orders by it.
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < listing.Count; i++)
                positions[listing[i].Id] = i;
            _db.SetListingPositions(source.Url, positions);

            // A video the listing just brought back keeps the payload of its earlier life, so the
            // skip set above would skip it and it would stay pending for ever.
            foreach (var reopened in verdict.Reopened)
                skipMetadataIds.Remove(reopened);

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
                .Where(e => !metadataMap.ContainsKey(e.Id)
                            && !skipMetadataIds.Contains(e.Id)
                            && !verdict.Unavailable.Contains(e.Id))
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
            var created = ProcessEntries(
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
            var otherAnchors = _publisher.SweepAnchorsFor(config, source, sourceRecord);

            // Publication is driven by what the database holds, not by what this pass happened
            // to re-fetch: entries already known are deliberately skipped for metadata, and
            // would otherwise never reach the library.
            // Only resolved rows are publishable: a pending one has no date, no season and no
            // episode number, and an unavailable one is exactly what must leave the library.
            // BOTH arguments have to agree on that: the orphan sweep spares any item whose video
            // the database still knows, so handing it every id would leave an excluded row's item
            // in the library for ever and the retirement would never happen.
            await _publisher.ReconcileAsync(
                    target, otherAnchors, sourceRecord, source.Mode, seriesName,
                    _db.GetPublishableVideosBySource(source.Url),
                    _db.GetPublishableVideoIds().ToHashSet(StringComparer.OrdinalIgnoreCase),
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
            return created;
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

        /// <summary>
        /// The entries of a listing with at most one entry per native id, keeping the first
        /// occurrence so the listing's order survives.
        /// </summary>
        /// <param name="entries">The flat-playlist entries returned by yt-dlp.</param>
        /// <param name="metadataMap">Full metadata keyed by the id the listing reported.</param>
        /// <returns>The entries, with later repeats of a stored id removed.</returns>
        /// <remarks>
        /// A listing can repeat a video: a YouTube playlist has been observed returning the same
        /// id twice in one flat listing. Each repeat used to create its own row — same native id,
        /// a fresh <c>Guid</c> each time — and the next sync of that source then threw on the
        /// native-id map, permanently.
        /// <para>
        /// The key is the id that will be <b>stored</b> — the metadata's, falling back to the
        /// listing's when no metadata was fetched — and not the listing's own. The two are equal
        /// on every platform supported today, but <see cref="ExistingFor"/> exists precisely
        /// because that is an invariant rather than a guarantee, and keying here on the listing id
        /// would let two entries resolving to one stored id both look new: two <c>Guid</c>s for
        /// one native id, the second insert refused by the UNIQUE index of schema v7, and the
        /// source failing every sync from then on.
        /// </para>
        /// <para>
        /// The comparison is case-insensitive, which is stricter than the database's (binary)
        /// UNIQUE index: anything this method lets through is something the index also accepts.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<PlaylistEntry> DeduplicateEntries(
            IReadOnlyList<PlaylistEntry> entries,
            Dictionary<string, VideoMetadata> metadataMap)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(metadataMap);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<PlaylistEntry>(entries.Count);
            foreach (var entry in entries)
            {
                var storedId = metadataMap.TryGetValue(entry.Id, out var meta) ? meta.Id : entry.Id;
                if (seen.Add(storedId))
                {
                    unique.Add(entry);
                }
            }

            return unique;
        }

        /// <summary>
        /// Every stored video of a source keyed by native id, keeping the first row of each id.
        /// </summary>
        /// <param name="existingVideos">Every video the database already holds for the source.</param>
        /// <returns>The map used to tell an update from a creation.</returns>
        /// <remarks>
        /// Deliberately not <c>ToDictionary</c>: a database written before the UNIQUE index of
        /// migration v7 can hold two rows for one native id, and <c>ToDictionary</c> threw
        /// <see cref="ArgumentException"/> on them, which failed the source's sync for good. The
        /// duplicates themselves are removed by that migration and prevented by the index; this
        /// method only makes sure they can never again cost a whole source.
        /// </remarks>
        internal static Dictionary<string, VideoRecord> BuildNativeIdMap(
            IReadOnlyList<VideoRecord> existingVideos)
        {
            ArgumentNullException.ThrowIfNull(existingVideos);

            var map = new Dictionary<string, VideoRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var video in existingVideos)
            {
                if (video.NativeId is { } nativeId)
                {
                    map.TryAdd(nativeId, video);
                }
            }

            return map;
        }

        /// <summary>
        /// The stored row an entry stands for, matched on the id that was written to it.
        /// </summary>
        /// <param name="existingByNativeId">The map from <see cref="BuildNativeIdMap"/>.</param>
        /// <param name="entryId">The id the listing reported for this entry.</param>
        /// <param name="metaId">The id the full metadata reports, which is what gets stored.</param>
        /// <returns>The stored row, or null when this entry is new to the source.</returns>
        /// <remarks>
        /// <c>native_id</c> is written from the metadata's id, so that is the id to match on first.
        /// The two are equal on every platform supported today — the Arte id derived from a listing
        /// URL is deliberately the id a full fetch reports, so rows re-match — and the listing id
        /// stays as a fallback for the rows written before that was true. Matching on the listing
        /// id alone would take a video for new the day they diverged, and the UNIQUE index added by
        /// schema v7 would then refuse the second row and fail that source's every sync.
        /// </remarks>
        internal static VideoRecord? ExistingFor(
            Dictionary<string, VideoRecord> existingByNativeId, string entryId, string metaId)
        {
            ArgumentNullException.ThrowIfNull(existingByNativeId);

            return existingByNativeId.GetValueOrDefault(metaId)
                ?? existingByNativeId.GetValueOrDefault(entryId);
        }

        /// <summary>
        /// Upserts database records for every entry in the playlist, and returns the count of
        /// records that were created (as opposed to updated) this pass.
        /// </summary>
        /// <param name="source">The source being synced.</param>
        /// <param name="entries">The flat-playlist entries returned by yt-dlp.</param>
        /// <param name="metadataMap">Full metadata keyed by native video id, fetched in parallel.</param>
        /// <param name="existingVideos">Every video the database already holds for this source.</param>
        /// <param name="seriesName">The resolved series name, used for Arte episode title selection.</param>
        /// <param name="syncStart">The timestamp recorded as the sync time for new records.</param>
        /// <param name="ct">A token to cancel the operation.</param>
        /// <returns>The number of video records created (not updated) by this pass.</returns>
        internal int ProcessEntries(
            SourceDefinition source,
            IReadOnlyList<PlaylistEntry> entries,
            Dictionary<string, VideoMetadata> metadataMap,
            IReadOnlyList<VideoRecord> existingVideos,
            string seriesName,
            DateTimeOffset syncStart,
            CancellationToken ct)
        {
            // One entry per video, before anything counts, ranks or upserts them.
            var uniqueEntries = DeduplicateEntries(entries, metadataMap);

            // Build a map of existing videos by NativeId for update detection
            var existingByNativeId = BuildNativeIdMap(existingVideos);

            // Compute episode numbers for NEW entries grouped by date
            // Key: nativeId → (season, episode)
            var assignments = AssignEpisodeNumbers(
                uniqueEntries, metadataMap, existingByNativeId, existingVideos);

            var created = 0;

            foreach (var entry in uniqueEntries)
            {
                ct.ThrowIfCancellationRequested();

                if (!metadataMap.TryGetValue(entry.Id, out var meta))
                    continue;

                var publishedAt = ParseDate(meta);
                var existing = ExistingFor(existingByNativeId, entry.Id, meta.Id);

                if (existing is null)
                {
                    created++;
                }

                // Resolve effective title.
                // Twitch extractors use their own field selection (TitleFor) and must never run
                // through ResolveTitle: Arte's alt_title logic would fire on any Twitch video whose
                // title happens to equal the series name, and value equality on the title cannot
                // express that invariant safely (e.g. a TwitchVod with no fulltitle falls back to
                // meta.Title, making them equal even when the Twitch rule applied).
                var effectiveTitle = PlatformRules.IsTwitchExtractor(meta)
                    ? (PlatformRules.TitleFor(meta) ?? meta.Id)
                    : ResolveTitle(meta, seriesName);

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

                FillVideoRecord(record, meta, source.Url, effectiveTitle, syncStart, publishedAt);

                // This pass has the metadata in hand, so the row is resolved. (The plan that
                // follows is what stops the sync from fetching it at all.)
                record.ResolutionState = ResolutionState.Resolved;
                record.ResolutionError = null;
                record.ResolutionAttempts = 0;
                record.ResolutionAttemptedAt = syncStart;
                AssignNumbers(
                    record,
                    meta,
                    effectiveTitle,
                    assignments.TryGetValue(entry.Id, out var ep) ? ep : null);

                _db.UpsertVideo(record);
            }

            return created;
        }

        /// <summary>
        /// Queues the <see cref="StreamDescriptionTask"/> when <paramref name="created"/> is
        /// positive, and does nothing otherwise.
        /// </summary>
        /// <param name="created">The number of video records the whole sync created.</param>
        /// <remarks>
        /// Called once per sync, after its last source: queued per source, the task started on the
        /// first creation, overlapped the remaining sources' yt-dlp work, and the creations made
        /// while it ran were dropped by <c>QueueIfNotRunning</c>. Queued, not run: the description
        /// task paces itself, and a sync that created nothing has nothing for it to do.
        /// </remarks>
        internal void QueueDescriptionIfCreated(int created)
        {
            if (created > 0)
            {
                _tasks.QueueIfNotRunning<StreamDescriptionTask>();
            }
        }

        /// <summary>
        /// Writes a video's season and episode numbers, from the best source available.
        /// </summary>
        /// <param name="record">The record to number.</param>
        /// <param name="meta">The metadata yt-dlp returned.</param>
        /// <param name="effectiveTitle">The title already resolved by the caller.</param>
        /// <param name="dateBased">
        /// The date-and-rank number to use when nothing better exists, or null when the video has no
        /// date to compute one from.
        /// </param>
        /// <remarks>
        /// Shared with the live probe, which numbers one video at a time. There is deliberately only
        /// one implementation of this decision: two could disagree, and a video renumbered after
        /// publication loses its watch state, since an episode's user-data key is built from these
        /// two numbers.
        /// <para>
        /// Priority: yt-dlp's own fields, then the title, then the date formula. The first two
        /// overwrite a stored date-based number, because they are better answers that may only have
        /// become available now; the third never overwrites, so a number once published stays.
        /// </para>
        /// </remarks>
        internal static void AssignNumbers(
            VideoRecord record,
            VideoMetadata meta,
            string effectiveTitle,
            (int season, int episode)? dateBased)
        {
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
            else if (!record.SeasonNumber.HasValue && dateBased is { } ep)
            {
                record.SeasonNumber = ep.season;
                record.EpisodeNumber = ep.episode;
            }
        }

        /// <summary>
        /// How many videos of a source already carry a number, per UTC day of publication.
        /// </summary>
        /// <param name="existingVideos">Every video the database holds for the source.</param>
        /// <returns>A mutable tally, to be advanced by <see cref="NextDateBasedNumber"/>.</returns>
        internal static Dictionary<DateOnly, int> NumberedPerDay(IReadOnlyList<VideoRecord> existingVideos)
            => existingVideos
                .Where(v => v.EpisodeNumber.HasValue && v.PublishedAt.HasValue)
                .GroupBy(v => DateOnly.FromDateTime(v.PublishedAt!.Value.UtcDateTime.Date))
                .ToDictionary(g => g.Key, g => g.Count());

        /// <summary>
        /// The next date-based number for a video published on <paramref name="publishedAt"/>,
        /// advancing <paramref name="numberedPerDay"/> so the following video of the same day ranks
        /// after it.
        /// </summary>
        /// <param name="numberedPerDay">The tally from <see cref="NumberedPerDay"/>.</param>
        /// <param name="publishedAt">The video's publication date, or null when it has none.</param>
        /// <returns>
        /// The number, or null when the video has no date or the day is already full (rank > 999).
        /// </returns>
        internal static (int season, int episode)? NextDateBasedNumber(
            Dictionary<DateOnly, int> numberedPerDay, DateTimeOffset? publishedAt)
        {
            ArgumentNullException.ThrowIfNull(numberedPerDay);

            if (publishedAt is not { } date)
                return null;

            var day = DateOnly.FromDateTime(date.UtcDateTime.Date);
            var rank = numberedPerDay.GetValueOrDefault(day) + 1;
            var assignment = EpisodeNumberCalculator.TryCalculate(date, rank);
            if (assignment is not null)
                numberedPerDay[day] = rank;

            return assignment;
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
                if (!metadataMap.TryGetValue(entry.Id, out var meta))
                    continue;

                if (ExistingFor(existingByNativeId, entry.Id, meta.Id) is { SeasonNumber: not null })
                    continue; // Already has a number — skip, and do not spend a rank of its day

                var date = ParseDate(meta);
                if (!date.HasValue)
                    continue;

                var key = DateOnly.FromDateTime(date.Value.UtcDateTime.Date);
                if (!newByDate.TryGetValue(key, out var list))
                    newByDate[key] = list = [];
                list.Add((entry, date.Value));
            }

            // Rank continues after the entries already numbered on that same UTC day.
            var numberedPerDay = NumberedPerDay(existingVideos);

            foreach (var (date, items) in newByDate)
            {
                // Sort new entries for this date by NativeId for determinism
                foreach (var (entry, date2) in items.OrderBy(x => x.entry.Id, StringComparer.Ordinal))
                {
                    var assignment = NextDateBasedNumber(numberedPerDay, date2);
                    if (assignment is null)
                    {
                        LogRankOverflow(entry.VideoUrl, date, numberedPerDay.GetValueOrDefault(date) + 1);
                        break;
                    }

                    result[entry.Id] = assignment.Value;
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
        /// Whether this row has ever been given a payload, whatever that payload turned out to hold.
        /// </summary>
        /// <remarks>
        /// A row that HAS one is never probed again, including when its payload is empty, because empty
        /// is a real answer: the platform offered no formats, tags, categories, channels or subtitles.
        /// Deciding this from the payload's content instead made that case indistinguishable from a row
        /// that was never given a payload at all, so such a video cost one yt-dlp process per sync for
        /// ever. The column being NULL is what carries the distinction; <c>VodDatabase.ReadExtras</c>
        /// turns it into <see cref="VideoExtras.NoPayload"/>, and nothing else answers version 0.
        /// <para>
        /// A row that has none — written before the extras column existed, or holding a value that no
        /// longer parses — is probed on every pass until a fetch answers, and that recurring cost is
        /// accepted deliberately. There is no safe way to remember the failure: a fetch that resolves
        /// nothing reports it as a null result, and so does every transient failure — a bot check, an
        /// HTTP 429, expired cookies, a broken extractor, a DNS blip, unparseable output
        /// (<c>YtDlpService.RunProcessAsync</c> returns null on ANY non-zero exit). Remembering that
        /// would mark a perfectly good video as having an empty payload, permanently: migration 5 added
        /// the extras column with no backfill, so every pre-existing row is payload-less, and one
        /// throttling wave would stamp them all at once. The empty payload published on that pass then
        /// wipes each item's tags, genres and studios, since <c>ApplyTags</c> and <c>ApplyStudios</c>
        /// assign unconditionally — and the row is never probed again, so they never come back.
        /// Probing repeatedly costs CPU and self-corrects; remembering costs the item's metadata and
        /// does not.
        /// </para>
        /// </remarks>
        internal static bool HasPayload(VideoExtras extras) => extras.Version > 0;

        /// <summary>
        /// Whether a stored row's metadata must be fetched again even though it is already known.
        /// </summary>
        /// <remarks>
        /// A broadcasting stream's synopsis and duration change under it, and a scheduled one may be
        /// rescheduled or start, so neither can be trusted from a previous pass.
        /// </remarks>
        internal static bool MustRefetch(LiveStatus status) => status != LiveStatus.NotLive;

        /// <summary>What a listing said about the entries it carries, before any call is made.</summary>
        /// <param name="Unavailable">
        /// The native ids the listing itself declares gone. They must not be fetched.
        /// </param>
        /// <param name="Updated">
        /// The rows whose state changed on the strength of the listing alone — a row to write
        /// <c>unavailable</c>, or one to bring back to <c>pending</c> because its title reappeared.
        /// A row built here for an entry the database did not know carries <b>no source url</b>:
        /// the caller owns that and stamps it before upserting. SQLite refuses the row otherwise,
        /// so a caller that forgets finds out immediately.
        /// </param>
        /// <param name="Reopened">
        /// The native ids whose title reappeared. They keep the payload of their earlier life, so
        /// the caller has to lift them out of its skip set or they are never fetched again.
        /// </param>
        internal readonly record struct ListingVerdict(
            IReadOnlySet<string> Unavailable,
            IReadOnlyList<VideoRecord> Updated,
            IReadOnlySet<string> Reopened);

        /// <summary>What <c>resolution_error</c> says when the listing itself called a video gone.</summary>
        internal const string ListingAbsenceError = "absent from the listing";

        /// <summary>
        /// Reads what a listing says about its own entries, platform by platform, and spends no
        /// call doing it.
        /// </summary>
        /// <param name="entries">The listing's entries.</param>
        /// <param name="existing">Every row the database holds for this source.</param>
        /// <param name="now">The pass's timestamp.</param>
        /// <returns>The ids to skip, and the rows to write.</returns>
        /// <remarks>
        /// On YouTube this is the difference between a source that works and one that does not: of
        /// the 43 entries of the playlist under diagnosis, 33 are dead, and each of them used to
        /// cost one yt-dlp call on every pass — all failing, writing nothing, and asking again next
        /// time. That is how a session earns an hour of rate limiting.
        /// <para>
        /// A live or upcoming entry is left entirely alone: its title is not stable in a listing
        /// either, and <see cref="MustRefetch"/> already has it fetched again every pass.
        /// </para>
        /// </remarks>
        internal static ListingVerdict ApplyListingVerdict(
            IReadOnlyList<PlaylistEntry> entries,
            IReadOnlyList<VideoRecord> existing,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(existing);

            var byNativeId = BuildNativeIdMap(existing);
            var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reopened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updated = new List<VideoRecord>();

            foreach (var entry in entries)
            {
                if (entry.LiveStatus != LiveStatus.NotLive)
                    continue;

                var stored = byNativeId.GetValueOrDefault(entry.Id);
                if (stored is not null && MustRefetch(stored.LiveStatus))
                    continue;

                if (!PlatformRules.ListingTellsUnavailable(entry.IeKey))
                    continue;

                if (string.IsNullOrWhiteSpace(entry.Title))
                {
                    unavailable.Add(entry.Id);

                    // A row only needs writing when this is news: an entry already known gone
                    // would otherwise be rewritten on every pass for nothing.
                    if (stored is null || stored.ResolutionState != ResolutionState.Unavailable)
                    {
                        var row = stored ?? NewRowFor(entry, now);
                        row.ResolutionState = ResolutionState.Unavailable;
                        row.ResolutionError = ListingAbsenceError;
                        row.ResolutionAttemptedAt = now;
                        updated.Add(row);
                    }

                    continue;
                }

                // Titled again, and we had called it gone on the listing's word: that word is now
                // the other way, so it goes back in the queue. Free, and immediate — the seven-day
                // window only governs a verdict a yt-dlp call produced.
                if (stored is { ResolutionState: ResolutionState.Unavailable }
                    && string.Equals(stored.ResolutionError, ListingAbsenceError, StringComparison.Ordinal))
                {
                    stored.ResolutionState = ResolutionState.Pending;
                    stored.ResolutionError = null;
                    stored.ResolutionAttempts = 0;
                    updated.Add(stored);
                    reopened.Add(entry.Id);
                }
            }

            return new ListingVerdict(unavailable, updated, reopened);
        }

        private static VideoRecord NewRowFor(PlaylistEntry entry, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid().ToString(),
            NativeId = entry.Id,
            ExtractorKey = entry.IeKey,
            VideoUrl = entry.VideoUrl,
            Title = entry.Title,
            SyncedAt = now,
            FirstSeenAt = now,
            LiveStatus = entry.LiveStatus,
        };

        /// <summary>The published date to store for a row.</summary>
        /// <remarks>
        /// A scheduled stream's date is its airing time, read from release_timestamp and rewritten on
        /// every fetch because a schedule can move. Two reasons it cannot come from the normal path:
        /// ParseDate prefers upload_date, which for a scheduled stream is the day it was created
        /// (spec F18), and PublishedAt is otherwise frozen once set, which would pin a stale
        /// schedule. Every other video keeps the existing behaviour.
        /// </remarks>
        internal static DateTimeOffset? PublishedAtFor(
            LiveStatus status,
            long? releaseTimestamp,
            DateTimeOffset? parsed,
            DateTimeOffset? stored,
            DateTimeOffset syncStart)
        {
            if (status == LiveStatus.Upcoming && releaseTimestamp.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(releaseTimestamp.Value);

            return stored ?? parsed ?? syncStart;
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

        /// <summary>
        /// Applies yt-dlp metadata to a <see cref="VideoRecord"/>, shared between the full sync path
        /// and the live probe path so neither duplicates the field-mapping logic.
        /// </summary>
        /// <param name="record">The record to fill (already created or loaded from the database).</param>
        /// <param name="meta">The metadata returned by yt-dlp.</param>
        /// <param name="sourceUrl">The source URL the video belongs to.</param>
        /// <param name="effectiveTitle">The title already resolved by the caller (platform-specific).</param>
        /// <param name="syncedAt">The timestamp to record as the sync time.</param>
        /// <param name="publishedAt">The video's parsed publication date, or null when absent.</param>
        internal static void FillVideoRecord(
            VideoRecord record,
            VideoMetadata meta,
            string sourceUrl,
            string effectiveTitle,
            DateTimeOffset syncedAt,
            DateTimeOffset? publishedAt)
        {
            record.Title = effectiveTitle;
            record.Description = meta.Description;
            record.ThumbnailUrl = PlatformRules.LiveThumbnailFor(meta, sourceUrl);
            record.Extras = record.Extras with
            {
                Tags = meta.Tags,
                Categories = meta.Categories,
                Channels = meta.Channels,
                SubtitleLanguages = meta.SubtitleLanguages,
                Qualities = meta.Qualities,
                AudioTracks = meta.AudioTracks
            };
            record.DurationSeconds = meta.Duration.HasValue ? (int)meta.Duration.Value : null;
            record.LiveStatus = meta.LiveStatus;
            record.SyncedAt = syncedAt;
            record.PublishedAt = PublishedAtFor(
                meta.LiveStatus, meta.ReleaseTimestamp, publishedAt, record.PublishedAt, syncedAt);
        }

        internal static DateTimeOffset? ParseDate(VideoMetadata meta)
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

        [LoggerMessage(EventId = 30, Level = LogLevel.Information,
            Message = "{SourceUrl}: the listing declares {Count} entr(ies) unavailable; "
                + "they cost no yt-dlp call")]
        private partial void LogListingDeclaredUnavailable(string sourceUrl, int count);

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

        [LoggerMessage(EventId = 14, Level = LogLevel.Debug,
            Message = "Sync: not sweeping library '{LibraryName}', it does not resolve: {Reason}")]
        private partial void LogSweepAnchorUnavailable(string libraryName, string reason);
    }
}

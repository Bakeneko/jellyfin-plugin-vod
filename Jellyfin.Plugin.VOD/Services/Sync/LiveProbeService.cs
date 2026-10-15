using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>What one probe pass concluded about a source.</summary>
    /// <param name="Succeeded">
    /// Whether the pass reached a conclusion at all. False when a yt-dlp call failed: the caller must
    /// then change nothing, because a failure is not an answer and treating it as "no live" would
    /// retract a broadcast that is still running.
    /// </param>
    /// <param name="Found">The videos the pass found broadcasting or scheduled.</param>
    public sealed record ProbeOutcome(bool Succeeded, IReadOnlyList<VideoMetadata> Found)
    {
        /// <summary>A pass that could not conclude.</summary>
        public static ProbeOutcome Failed { get; } = new(false, []);

        /// <summary>A pass that concluded the source has nothing live.</summary>
        public static ProbeOutcome Nothing { get; } = new(true, []);
    }

    /// <summary>The per-platform probe strategies, as pure functions over their yt-dlp calls.</summary>
    /// <remarks>
    /// YtDlpService is sealed with non-virtual methods, so the calls arrive as delegates: that is
    /// what makes the strategies testable without a live network.
    /// </remarks>
    public static class LiveProbe
    {
        /// <summary>Probes a Twitch source.</summary>
        /// <param name="sourceUrl">The source URL, either channel root or /videos.</param>
        /// <param name="root">Fetches the channel root's metadata, or says why there is none.</param>
        /// <param name="firstEntry">Fetches the first /videos entry in full.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>What the pass concluded.</returns>
        public static async Task<ProbeOutcome> TwitchAsync(
            string sourceUrl,
            Func<string, Task<MetadataResult>> root,
            Func<string, Task<VideoMetadata?>> firstEntry,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(firstEntry);
            ct.ThrowIfCancellationRequested();

            var channel = TwitchChannelRoot(sourceUrl);
            var answer = await root(channel).ConfigureAwait(false);

            // An off-air channel is the one non-zero exit that carries meaning: yt-dlp read the
            // channel and said there is no broadcast. It is also the ONLY way a real Twitch channel
            // reports being off air — it prints no JSON, so there is no NotLive status to read — and
            // without this branch the live-only source form could never retire its item.
            if (answer.Outcome == MetadataOutcome.ChannelOffAir)
                return ProbeOutcome.Nothing;

            if (answer.Metadata is not { } stream)
                return ProbeOutcome.Failed;

            // Conclude only on an answer Twitch itself gave. A host that merely resembles Twitch is
            // read by yt-dlp's generic extractor, which reports no live_status at all; the "nothing
            // live" that would follow is what deletes every item of the source.
            if (!PlatformRules.IsTwitchExtractor(stream))
                return ProbeOutcome.Failed;

            if (stream.LiveStatus != LiveStatus.Live)
                return ProbeOutcome.Nothing;

            // A channel-root source declares "the live, nothing else", and the sync publishes the
            // channel stream for it. Preferring the VOD here published a second item for the same
            // broadcast, measured 2026-10-08 — and that one stuttered at every segment while the
            // stream played smoothly. Not even asked for: its answer would go unused.
            if (SourceTypeDetector.Detect(sourceUrl) == SourceType.TwitchChannelLive)
                return new ProbeOutcome(true, [stream]);

            // The in-progress VOD plays from the start of the broadcast and carries no ads, so it is
            // preferred whenever it can be read. It is unreadable on a subscriber-only channel, and
            // absent when the channel does not record, and then the channel's own stream stands in.
            var vod = await firstEntry($"{channel}/videos").ConfigureAwait(false);
            return vod is { LiveStatus: LiveStatus.Live }
                ? new ProbeOutcome(true, [vod])
                : new ProbeOutcome(true, [stream]);
        }

        /// <summary>Probes a YouTube source.</summary>
        /// <param name="sourceUrl">The channel tab or playlist URL.</param>
        /// <param name="listing">Fetches the flat listing head.</param>
        /// <param name="detail">Fetches one video's full metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>What the pass concluded.</returns>
        public static async Task<ProbeOutcome> YoutubeAsync(
            string sourceUrl,
            Func<string, Task<PlaylistInfo?>> listing,
            Func<string, Task<VideoMetadata?>> detail,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(listing);
            ArgumentNullException.ThrowIfNull(detail);
            ct.ThrowIfCancellationRequested();

            var head = await listing(sourceUrl).ConfigureAwait(false);
            if (head is null)
                return ProbeOutcome.Failed;

            var found = new List<VideoMetadata>();
            foreach (var entry in head.Entries)
            {
                if (entry.LiveStatus == LiveStatus.NotLive)
                    continue;

                ct.ThrowIfCancellationRequested();
                var meta = await detail(entry.VideoUrl).ConfigureAwait(false);
                if (meta is not null)
                    found.Add(meta);
            }

            return new ProbeOutcome(true, found);
        }

        private static string TwitchChannelRoot(string sourceUrl)
        {
            // A configured source may carry a query string ("/videos?filter=archives" is a form the
            // detector accepts). Both calls below are built from this value, so the query has to go:
            // kept, it sends the root call to the archive playlist — which outlives the metadata
            // timeout — and makes the second URL ".../videos?filter=archives/videos".
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
                return (sourceUrl ?? string.Empty).TrimEnd('/');

            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/videos", StringComparison.OrdinalIgnoreCase))
                path = path[..^"/videos".Length];

            return string.Concat(uri.Scheme, "://", uri.Authority, path);
        }
    }

    /// <summary>
    /// Probes a single source for a broadcast in progress and republishes the source when it finds one.
    /// </summary>
    public sealed partial class LiveProbeService
    {
        private readonly YtDlpService _ytDlp;
        private readonly IVodDatabase _db;
        private readonly LibraryPublisher _publisher;
        private readonly StreamDescriber _describer;
        private readonly VideoDescriptionStep _description;
        private readonly ILogger<LiveProbeService> _logger;

        /// <summary>Initializes a new instance of the <see cref="LiveProbeService"/> class.</summary>
        /// <param name="ytDlp">Asks the platform what is broadcasting.</param>
        /// <param name="db">The VOD database.</param>
        /// <param name="publisher">Publishes a source to the Jellyfin library.</param>
        /// <param name="describer">Applies a stored stream description at publication.</param>
        /// <param name="description">Describes a broadcast that has ended.</param>
        /// <param name="logger">The logger.</param>
        public LiveProbeService(
            YtDlpService ytDlp,
            IVodDatabase db,
            LibraryPublisher publisher,
            StreamDescriber describer,
            VideoDescriptionStep description,
            ILogger<LiveProbeService> logger)
        {
            _ytDlp = ytDlp;
            _db = db;
            _publisher = publisher;
            _describer = describer;
            _description = description;
            _logger = logger;
        }

        /// <summary>
        /// Probes <paramref name="source"/> for a broadcast in progress and republishes the source
        /// when the probe concludes.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="source">The source to probe.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task that completes once the probe pass and any publication are done.</returns>
        public Task ProbeSourceAsync(
            PluginConfiguration config,
            SourceDefinition source,
            CancellationToken ct)
            => ProbeSourceAsync(config, source, RunProbeAsync, ct);

        /// <summary>
        /// Same as <see cref="ProbeSourceAsync(PluginConfiguration, SourceDefinition, CancellationToken)"/>,
        /// with the yt-dlp calls injected so the gates before them can be tested without a network.
        /// </summary>
        internal async Task ProbeSourceAsync(
            PluginConfiguration config,
            SourceDefinition source,
            Func<SourceDefinition, SourceType, CancellationToken, Task<ProbeOutcome>> runProbe,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(runProbe);

            // Arte and france.tv list no broadcast: probing them spends a yt-dlp call per pass to
            // learn nothing.
            var type = SourceTypeDetector.Detect(source.Url);
            if (!PlatformRules.SupportsLiveProbe(source.Url))
            {
                LogProbeSkippedUnsupported(source.Url, type.ToString());
                return;
            }

            // The series name the publication hangs from. A live-only source may never have synced
            // successfully — an off-air channel fails the playlist call too, so no pass ever stored
            // a name — and requiring one skipped the source for ever, which is exactly the case this
            // source form exists for. The URL names the channel, so derive it.
            var sourceRecord = _db.GetSource(source.Url) ?? new SourceRecord { Url = source.Url };
            if (string.IsNullOrWhiteSpace(sourceRecord.Name))
            {
                var derived = DeriveSeriesName(source);
                if (string.IsNullOrWhiteSpace(derived))
                {
                    LogProbeSkippedUnnamed(source.Url);
                    return;
                }

                sourceRecord.Name = derived;
            }

            var outcome = await runProbe(source, type, ct).ConfigureAwait(false);

            await ProbeSourceCoreAsync(config, source, type, sourceRecord, outcome, ct)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Applies a probe outcome: upserts found videos, retires stale ones, and republishes.
        /// Separated from
        /// <see cref="ProbeSourceAsync(PluginConfiguration, SourceDefinition, CancellationToken)"/>
        /// so it can be tested with an injected outcome, without going through yt-dlp.
        /// </summary>
        internal Task ProbeSourceCoreAsync(
            PluginConfiguration config,
            SourceDefinition source,
            SourceType type,
            SourceRecord sourceRecord,
            ProbeOutcome outcome,
            CancellationToken ct)
            => ProbeSourceCoreAsync(
                config, source, type, sourceRecord, outcome,
                (url, token) => _ytDlp.GetVideoMetadataAsync(url, token), ct);

        /// <summary>
        /// Same as <see cref="ProbeSourceCoreAsync(PluginConfiguration, SourceDefinition, SourceType, SourceRecord, ProbeOutcome, CancellationToken)"/>,
        /// with the re-fetch of a video no longer reported as broadcasting injected, so that path
        /// can be tested without yt-dlp.
        /// </summary>
        internal async Task ProbeSourceCoreAsync(
            PluginConfiguration config,
            SourceDefinition source,
            SourceType type,
            SourceRecord sourceRecord,
            ProbeOutcome outcome,
            Func<string, CancellationToken, Task<VideoMetadata?>> refresh,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(refresh);

            // A failed probe is not an answer: change nothing.
            if (!outcome.Succeeded)
            {
                LogProbeFailed(source.Url);
                return;
            }

            // Hold the per-source lock for every write: the database upserts, the deletion of
            // stale live items, and the reconciliation that follows. Reconciling deletes library
            // items, so a concurrent sync pass over the same source would fight over what exists.
            using var hold = await SourceLock.AcquireAsync(source.Url, ct).ConfigureAwait(false);

            // Re-read the source record under the lock to pick up any mutations that happened
            // during the yt-dlp probe — particularly PublishedAnchorId and last_resolved_at
            // written by a concurrent ResolutionService wave. Without this re-read, the stale
            // record written by UpsertSource below would silently clobber the resolver's anchor.
            // The caller may have derived a name that is not yet in the database (a channel URL
            // name for a source that was never synced); keep it when the database still has none.
            var freshRecord = _db.GetSource(source.Url) ?? new SourceRecord { Url = source.Url };
            if (!string.IsNullOrWhiteSpace(sourceRecord.Name) && string.IsNullOrWhiteSpace(freshRecord.Name))
                freshRecord.Name = sourceRecord.Name;
            sourceRecord = freshRecord;

            // The videos reference this row, so it has to exist before they are written — and the
            // probe may be the first pass ever to write it, for a source whose every sync failed.
            _db.UpsertSource(sourceRecord);

            // One record per video, whatever the listing repeated. A flat listing can return the
            // same id twice — observed in production — and YoutubeAsync fetches detail per entry,
            // so the pass carries that video's metadata twice. Since the existing rows are read
            // once, before the loop, the second copy found nothing in that snapshot: it minted a
            // second Guid for the same native id and took a second rank of its day. Under the
            // UNIQUE index of schema v7 it now fails the whole pass instead, so the broadcast
            // would wait for the next one. Same defect, same fix as the sync's DeduplicateEntries.
            var found = DeduplicateFound(outcome.Found);

            // Build or update a record for every video the pass found broadcasting.
            var syncedAt = DateTimeOffset.UtcNow;
            var existingVideos = _db.GetVideosBySource(source.Url);

            // Whether the resolver still had something to wait for before this pass. Read from the
            // same snapshot the numbering works from, so it describes the source as found.
            var settledBefore = ResolutionService.SourceIsSettled(existingVideos);

            // The day-by-day tally the sync's numbering rule works from. Advanced as numbers are
            // handed out, so two broadcasts found in the same pass cannot both take the same rank.
            var numberedPerDay = SyncService.NumberedPerDay(existingVideos);

            foreach (var meta in found)
            {
                var existing = meta.Id is { Length: > 0 }
                    ? FindExistingByNativeId(existingVideos, meta.Id)
                    : null;

                var record = existing ?? new VideoRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceUrl = source.Url,
                    NativeId = meta.Id,
                    ExtractorKey = meta.ExtractorKey,
                    VideoUrl = meta.WebpageUrl ?? source.Url,
                    FirstSeenAt = syncedAt
                };

                var publishedAt = SyncService.ParseDate(meta);
                var effectiveTitle = PlatformRules.TitleFor(meta) ?? meta.Id;

                SyncService.FillVideoRecord(record, meta, source.Url, effectiveTitle, syncedAt, publishedAt);

                // This pass resolved the broadcast itself, so its row is resolved. Left pending it
                // would be published here — the probe reconciles its own list — and then retired by
                // the next sync, which offers the library only resolved rows.
                record.ResolutionState = ResolutionState.Resolved;
                record.ResolutionError = null;
                record.ResolutionAttempts = 0;
                record.ResolutionAttemptedAt = syncedAt;

                // The sync's own rule, not a second one. An unnumbered episode is filed under
                // Specials and renumbered by the next sync, which loses the watch state: an
                // episode's user-data key is built from its season and episode numbers.
                // The date-based number is only asked for when this row has none: computing it
                // advances the day's tally as a side effect, so spending a rank on a broadcast
                // this source already numbered pushes the next new one past the number the sync
                // would have given it — and no later pass corrects that, because the date-based
                // branch of AssignNumbers never overwrites a number a row already carries.
                var dateBased = record.SeasonNumber.HasValue
                    ? null
                    : SyncService.NextDateBasedNumber(numberedPerDay, publishedAt);

                SyncService.AssignNumbers(record, meta, effectiveTitle, dateBased);

                _db.UpsertVideo(record);
            }

            // What becomes of a video this pass no longer reports as broadcasting.
            //
            // TwitchChannelLive is the live-only source form: the probe asks the channel root
            // directly, so a successful answer is complete — off air, or airing exactly what it
            // found. Every video it was following beyond that is gone.
            //
            // All other cases keep their videos: a Twitch /videos item becomes the archive VOD when
            // the broadcast ends (same id), and a YouTube listing head is only a sample, so absence
            // from what was read does not prove absence from the full source. Re-fetching each one is
            // what ends its live state, and only then can the sync's orphan sweep collect it — the
            // sweep spares anything still broadcasting. A failed re-fetch means we change nothing for
            // that video, for the same reason a failed probe changes nothing overall.
            if (type == SourceType.TwitchChannelLive)
            {
                // A live-only source holds the broadcast the root reports and nothing else, and a
                // successful root answer is complete: off air, or airing THIS stream. Every other
                // row is a past broadcast — Twitch gives a restarted broadcast a new stream id
                // (measured 2026-10-08), and refreshing the old row by asking the channel root kept
                // it "live" for as long as the channel aired anything.
                var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in found)
                    current.Add(m.Id);

                // Checked once, before the first delete: the reconcile below is what removes the
                // library items these rows stood for, so stopping between two deletions would leave
                // items without rows. The deletion commits as a whole or not at all.
                ct.ThrowIfCancellationRequested();

                foreach (var video in _db.GetVideosBySource(source.Url))
                {
                    if (video.NativeId is not null && current.Contains(video.NativeId))
                        continue;

                    _db.DeleteVideo(video.Id);
                    LogDeletedStaleVideo(video.Id, source.Url);
                }
            }
            else
            {
                var foundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in found)
                    foundIds.Add(m.Id);

                foreach (var video in _db.GetVideosBySource(source.Url))
                {
                    if (video.LiveStatus == LiveStatus.NotLive)
                        continue;

                    // Already in the found set — just updated above, skip
                    if (video.NativeId is not null && foundIds.Contains(video.NativeId))
                        continue;

                    ct.ThrowIfCancellationRequested();

                    var refreshed = await refresh(video.VideoUrl, ct).ConfigureAwait(false);
                    if (refreshed is null)
                    {
                        LogRefreshFailed(video.VideoUrl);
                        continue;
                    }

                    var refreshedTitle = PlatformRules.TitleFor(refreshed) ?? refreshed.Id;
                    SyncService.FillVideoRecord(video, refreshed, source.Url, refreshedTitle, syncedAt, SyncService.ParseDate(refreshed));

                    // This branch resolved the video too — the ex-broadcast whose stream has
                    // ended. Left in whatever state it was read with, a migrated payload-less row
                    // would stay pending while now carrying a payload, so the sync's skip set
                    // would never fetch it again either.
                    video.ResolutionState = ResolutionState.Resolved;
                    video.ResolutionError = null;
                    video.ResolutionAttempts = 0;
                    video.ResolutionAttemptedAt = syncedAt;

                    // Stored before describing: the describer probes through our own stream
                    // route, which reads this row and refuses it while it is still Upcoming.
                    _db.UpsertVideo(video);

                    // The archive is described now, while the manifest URL is fresh, rather than
                    // on its first play. A failure here leaves the row resolved: the probe has no
                    // retry of its own, and the playback failsafe describes it on first play.
                    if (await DescribeEndedLiveAsync(config, source, video, refreshed.ManifestUrl, syncedAt, ct)
                            .ConfigureAwait(false))
                    {
                        _db.UpsertVideo(video);
                    }
                }
            }

            // The probe never runs the resolver's numbering pass, so when this pass resolved the
            // source's last blocking row, its dated unnumbered rows wait for the resolver — which
            // has no due row left to serve the source for. Clearing last_resolved_at gives it the
            // same one free wave as a listing verdict or an orphan cleanup: resolve nothing,
            // number, publish, stamp. Only on the transition, or every probe pass would hand a
            // settled source a free wave.
            var rowsAfter = _db.GetVideosBySource(source.Url);
            if (!settledBefore && ResolutionService.SourceIsSettled(rowsAfter))
                _db.ClearLastResolvedAt(source.Url);

            var libraryName = LibraryRouting.NameFor(config, source.Mode);
            if (string.IsNullOrWhiteSpace(libraryName))
            {
                LogProbeSkippedNoLibrary(source.Url);
                return;
            }

            var target = _publisher.ResolveTarget(libraryName);

            // The same filter as the resolver: retention, and no dated row without a number. The
            // probe numbers the broadcasts it writes, but never runs the resolver's numbering pass,
            // so a settled source it reaches first keeps its unnumbered rows for the resolver.
            var publishable = ResolutionService.Publishable(
                rowsAfter,
                DateTimeOffset.UtcNow,
                source.RetentionDays);

            var report = await _publisher.ReconcileAsync(
                    target,
                    _publisher.SweepAnchorsFor(config, source, sourceRecord),
                    sourceRecord,
                    source.Mode,
                    sourceRecord.Name!,
                    publishable,
                    _db.GetPublishableVideoIds().ToHashSet(StringComparer.OrdinalIgnoreCase),
                    config.LocalBaseUrl,
                    config.UseReleaseDateAsDateAdded,
                    ct)
                .ConfigureAwait(false);

            // Items this reconcile created receive the description stored on their row — an
            // archive this pass just described among them — as the resolver's publication does.
            // Only the videos the reconcile created an item for are visited: the probe publishes
            // every few minutes, and looking every described row up again each time would cost a
            // library query per row for nothing. ApplyStored still skips a member that already
            // has a video stream.
            foreach (var video in publishable)
            {
                if (report.CreatedVideoIds.Contains(video.Id) && video.StreamDescriptions is { Length: > 0 })
                    _describer.ApplyStored(video.Id, video.StreamDescriptions, ct);
            }
        }

        /// <summary>Describes a broadcast the probe found ended, without ever failing its row.</summary>
        /// <returns>Whether a description was stored on <paramref name="video"/>.</returns>
        private async Task<bool> DescribeEndedLiveAsync(
            PluginConfiguration config,
            SourceDefinition source,
            VideoRecord video,
            string? manifestUrl,
            DateTimeOffset now,
            CancellationToken ct)
        {
            var before = video.StreamDescriptions;
            string? error;
            try
            {
                error = await _description
                    .DescribeAsync(config, video, manifestUrl, source.RetentionDays, now, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // a description is a bonus here; it must not abort the probe pass
            catch (Exception ex)
#pragma warning restore CA1031
            {
                error = "describe: " + ex.Message;
            }

            if (error is not null)
            {
                LogDescribeFailed(video.VideoUrl, error);
                return false;
            }

            return !string.Equals(before, video.StreamDescriptions, StringComparison.Ordinal);
        }

        private async Task<ProbeOutcome> RunProbeAsync(
            SourceDefinition source,
            SourceType type,
            CancellationToken ct)
        {
            if (IsTwitchStrategy(type))
            {
                return await LiveProbe.TwitchAsync(
                    source.Url,
                    root: url => _ytDlp.GetMetadataAsync(url, ct),
                    firstEntry: url => _ytDlp.GetFirstEntryMetadataAsync(url, ct),
                    ct: ct).ConfigureAwait(false);
            }

            // YoutubeChannel / YoutubePlaylist
            return await LiveProbe.YoutubeAsync(
                source.Url,
                listing: url => _ytDlp.GetPlaylistEntriesAsync(url, 5, null, ct),
                detail: url => _ytDlp.GetVideoMetadataAsync(url, ct),
                ct: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The name to publish the source under when the database holds none.
        /// </summary>
        /// <remarks>
        /// What the user typed wins; failing that, a Twitch URL names its channel. Anything else
        /// returns null and the source waits for a sync to name it, because inventing a name here
        /// would rename the series the day the sync finally succeeds.
        /// </remarks>
        private static string? DeriveSeriesName(SourceDefinition source)
            => !string.IsNullOrWhiteSpace(source.Name)
                ? source.Name
                : TwitchUrls.ChannelFrom(source.Url);

        /// <summary>Whether a source kind is answered by the Twitch strategy.</summary>
        /// <remarks>
        /// The two Twitch source forms share one strategy: both ask the channel root first, and
        /// <c>/videos</c> only differs in preferring the in-progress VOD it can then reach.
        /// </remarks>
        internal static bool IsTwitchStrategy(SourceType type)
            => type is SourceType.TwitchChannelLive or SourceType.TwitchVideos;

        /// <summary>
        /// The metadata a pass found, with at most one entry per native id — the first occurrence.
        /// </summary>
        /// <param name="found">What the probe strategy reported as broadcasting.</param>
        /// <returns>The same metadata, with later repeats of an id removed.</returns>
        private static List<VideoMetadata> DeduplicateFound(
            IReadOnlyList<VideoMetadata> found)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<VideoMetadata>(found.Count);
            foreach (var meta in found)
            {
                if (seen.Add(meta.Id))
                {
                    unique.Add(meta);
                }
            }

            return unique;
        }

        private static VideoRecord? FindExistingByNativeId(
            IReadOnlyList<VideoRecord> existingVideos, string nativeId)
        {
            foreach (var v in existingVideos)
            {
                if (string.Equals(v.NativeId, nativeId, StringComparison.OrdinalIgnoreCase))
                    return v;
            }

            return null;
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Probe skipped for {SourceUrl}: source type {SourceType} does not support live probing")]
        private partial void LogProbeSkippedUnsupported(string sourceUrl, string sourceType);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "Probe skipped for {SourceUrl}: nothing names this source yet")]
        private partial void LogProbeSkippedUnnamed(string sourceUrl);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Probe for {SourceUrl} could not conclude (yt-dlp call failed); not changing anything")]
        private partial void LogProbeFailed(string sourceUrl);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
            Message = "Probe: could not refresh live status for {VideoUrl}; leaving it unchanged")]
        private partial void LogRefreshFailed(string videoUrl);

        [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
            Message = "Probe skipped publication for {SourceUrl}: no library configured")]
        private partial void LogProbeSkippedNoLibrary(string sourceUrl);

        [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
            Message = "Probe: deleted stale live video {VideoId} from {SourceUrl} (channel is off air)")]
        private partial void LogDeletedStaleVideo(string videoId, string sourceUrl);

        [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
            Message = "Probe: describing the ended broadcast {VideoUrl} failed ({Reason}); it will be described on first play")]
        private partial void LogDescribeFailed(string videoUrl, string reason);
    }
}

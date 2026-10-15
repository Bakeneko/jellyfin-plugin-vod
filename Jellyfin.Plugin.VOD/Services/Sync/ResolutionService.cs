using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Resolves the videos an enumeration left pending, a wave at a time, and republishes the
    /// source it served.
    /// </summary>
    /// <remarks>
    /// The sync enumerates and nothing more, so this is where every per-video yt-dlp call lives.
    /// One wave serves ONE source of ONE platform: the source with the fewest videos due, which
    /// makes a source complete before the next one starts while still letting two new videos of a
    /// small source overtake a 300-video backlog. The platform's own lane in
    /// <see cref="YtDlpThrottle"/> is what paces the calls; this class decides only what to
    /// resolve and what may be published.
    /// </remarks>
    public sealed partial class ResolutionService
    {
        private readonly IVodDatabase _db;
        private readonly YtDlpService _ytDlp;
        private readonly StreamDescriber _describer;
        private readonly LibraryPublisher _publisher;
        private readonly VideoDescriptionStep _description;
        private readonly ILogger<ResolutionService> _logger;

        /// <summary>Initializes a new instance of the <see cref="ResolutionService"/> class.</summary>
        /// <param name="db">The VOD database.</param>
        /// <param name="ytDlp">Fetches a video's metadata and its HLS master in one call.</param>
        /// <param name="describer">Applies a stored stream description at publication.</param>
        /// <param name="publisher">Publishes a source to the Jellyfin library.</param>
        /// <param name="description">Describes a freshly resolved video's streams.</param>
        /// <param name="logger">The logger.</param>
        public ResolutionService(
            IVodDatabase db,
            YtDlpService ytDlp,
            StreamDescriber describer,
            LibraryPublisher publisher,
            VideoDescriptionStep description,
            ILogger<ResolutionService> logger)
        {
            _db = db;
            _ytDlp = ytDlp;
            _describer = describer;
            _publisher = publisher;
            _description = description;
            _logger = logger;
        }

        /// <summary>Whether no video of this source can still change a day's ranks.</summary>
        /// <param name="sourceRows">Every row of the source.</param>
        /// <returns>True when no row blocks, per <see cref="ResolutionSettlement.Blocks"/>.</returns>
        internal static bool SourceIsSettled(IReadOnlyList<VideoRecord> sourceRows)
        {
            ArgumentNullException.ThrowIfNull(sourceRows);

            foreach (var row in sourceRows)
            {
                if (ResolutionSettlement.Blocks(row.ResolutionState, row.ResolutionAttempts))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Gives every resolved, dated, unnumbered row its date-based number, in publication-time
        /// order, after the numbers its day already holds.
        /// </summary>
        /// <param name="sourceRows">Every row of a settled source.</param>
        /// <returns>The rows this call numbered, mutated in place, for the caller to persist.</returns>
        /// <remarks>
        /// Run only on a settled source: every video of every day then has its date, so ranks
        /// follow publication time whatever order the resolver served them in. A stored number is
        /// never recomputed — the watch state is keyed on it — so a video that only enters the
        /// database after its day was numbered takes the next rank (spec R-l).
        /// </remarks>
        internal static IReadOnlyList<VideoRecord> NumberByPublicationTime(IReadOnlyList<VideoRecord> sourceRows)
        {
            ArgumentNullException.ThrowIfNull(sourceRows);

            var numberedPerDay = SyncService.NumberedPerDay(sourceRows);
            var numbered = new List<VideoRecord>();

            var candidates = sourceRows
                .Where(r => r.ResolutionState == ResolutionState.Resolved
                            && r.EpisodeNumber is null
                            && r.PublishedAt.HasValue)
                .OrderBy(r => r.PublishedAt!.Value)
                .ThenBy(r => r.Id, StringComparer.Ordinal);

            foreach (var row in candidates)
            {
                if (SyncService.NextDateBasedNumber(numberedPerDay, row.PublishedAt) is not { } assignment)
                    continue;

                row.SeasonNumber = assignment.season;
                row.EpisodeNumber = assignment.episode;
                numbered.Add(row);
            }

            return numbered;
        }

        /// <summary>The rows of a source that may be published right now.</summary>
        /// <param name="sourceRows">Every row of the source.</param>
        /// <param name="now">The reference time for the retention window.</param>
        /// <param name="retentionDays">The source's retention in days; zero or less disables it.</param>
        /// <returns>The publishable rows.</returns>
        /// <remarks>
        /// A row is offered when it is resolved, inside the retention window, and either already
        /// numbered or — on a settled source — undated, since an undated row can never receive a
        /// number. A dated row without a number is never offered: the live probe publishes through
        /// this filter without running <see cref="NumberByPublicationTime"/>, and publishing such a
        /// row would file it under Specials and renumber it later, losing its watch state.
        /// Retention is a publication filter rather than a pruning rule because a pending video has
        /// no date to prune on, and deleting the row would only have it re-discovered and
        /// re-resolved by the next enumeration.
        /// </remarks>
        internal static IReadOnlyList<VideoRecord> Publishable(
            IReadOnlyList<VideoRecord> sourceRows, DateTimeOffset now, int retentionDays)
        {
            ArgumentNullException.ThrowIfNull(sourceRows);

            var settled = SourceIsSettled(sourceRows);
            var cutoff = retentionDays > 0 ? now.AddDays(-retentionDays) : (DateTimeOffset?)null;
            var publishable = new List<VideoRecord>();

            foreach (var row in sourceRows)
            {
                if (row.ResolutionState != ResolutionState.Resolved)
                    continue;

                if (cutoff is { } limit && row.PublishedAt is { } published && published < limit)
                    continue;

                if (row.EpisodeNumber.HasValue || (settled && row.PublishedAt is null))
                    publishable.Add(row);
            }

            return publishable;
        }

        /// <summary>
        /// Numbers a settled source and persists the numbers, then returns what may be published.
        /// </summary>
        /// <param name="db">The VOD database.</param>
        /// <param name="sourceUrl">The source about to be published.</param>
        /// <param name="now">The reference time for the retention window.</param>
        /// <param name="retentionDays">The source's retention in days; zero or less disables it.</param>
        /// <returns>The rows <see cref="Publishable"/> offers once numbering is done.</returns>
        internal static IReadOnlyList<VideoRecord> PrepareForPublication(
            IVodDatabase db, string sourceUrl, DateTimeOffset now, int retentionDays)
        {
            ArgumentNullException.ThrowIfNull(db);

            var rows = db.GetVideosBySource(sourceUrl);
            if (SourceIsSettled(rows))
            {
                foreach (var row in NumberByPublicationTime(rows))
                    db.UpsertVideo(row);
            }

            return Publishable(rows, now, retentionDays);
        }

        /// <summary>
        /// The maximum number of non-actionable candidates the wave skips before giving up.
        /// Each skip stamps the candidate's <c>last_resolved_at</c>, which makes it visible via the
        /// due-row arm rather than the never-served arm on subsequent calls, so the loop converges.
        /// </summary>
        private const int MaxSkipsPerWave = 10;

        /// <summary>Resolves one wave for one platform, and republishes the source it served.</summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="ieKey">The platform's extractor key; the empty string is the unknown lane.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>How many videos this wave resolved.</returns>
        public async Task<int> ResolveWaveAsync(
            PluginConfiguration config, string ieKey, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            var waveSize = Math.Max(1, config.ResolutionWaveSize);
            var retryMinutes = Math.Max(1, config.ResolutionRetryMinutes);
            var now = DateTimeOffset.UtcNow;

            // Skip unconfigured or disabled sources rather than burning the whole wave on one that
            // has been removed or paused. Each skip stamps last_resolved_at so the source exits the
            // never-served arm and is ordered among due-row candidates on the next call; the loop is
            // therefore bounded: at most MaxSkipsPerWave attempts, or until no candidate remains.
            // "Forward progress" — at least one video resolved — is the condition the task uses to
            // decide whether to requeue, so a wave that only stamps and skips returns 0 and does not
            // trigger another immediate pass.
            //
            // N2 fix: the skipped set is passed to SelectSourceToResolve so the SQL excludes those
            // URLs outright. A paused source with few due rows would otherwise rank first in the
            // due-row arm (ORDER BY due_count ASC) after being stamped, causing the loop to see
            // it again and return 0 — permanently starving every other source on the platform.
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? sourceUrl;
            SourceDefinition? definition;

            while (true)
            {
                sourceUrl = _db.SelectSourceToResolve(ieKey, now, retryMinutes, skipped);
                if (sourceUrl is null)
                    return 0;

                // Defensive fallback: SQL excludes the set, so this should never be true; if it
                // somehow is, continue rather than returning so the loop can find the next candidate.
                if (skipped.Contains(sourceUrl))
                    continue;

                definition = config.Sources?.FirstOrDefault(
                    s => string.Equals(s.Url, sourceUrl, StringComparison.OrdinalIgnoreCase));

                // A source removed from the configuration or disabled (paused) still has rows until
                // the next sync prunes them. Stamp it and try the next candidate.
                var shouldSkip = definition is null || !definition.Enabled;
                if (shouldSkip)
                {
                    _db.SetLastResolvedAt(sourceUrl, now);
                    skipped.Add(sourceUrl);
                    if (skipped.Count >= MaxSkipsPerWave)
                        return 0;
                    continue;
                }

                break;
            }

            // Exclusive with the sync and the live probe for this source: all three publish it,
            // and reconciliation deletes library items.
            using var hold = await SourceLock.AcquireAsync(sourceUrl, cancellationToken)
                .ConfigureAwait(false);

            var due = _db.GetDueVideos(sourceUrl, now, retryMinutes, waveSize);
            var resolved = 0;

            foreach (var video in due)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await ResolveOneAsync(config, video, definition!.RetentionDays, cancellationToken)
                        .ConfigureAwait(false))
                {
                    resolved++;
                }
            }

            // Stamp strategy:
            //   Success                      → SetLastResolvedAt: normal path; the source moves to
            //                                  the due-row arm for subsequent calls.
            //   Failure + resolved > 0       → ClearLastResolvedAt: we made forward progress this
            //                                  wave but the publish failed (e.g. transient library
            //                                  error); clear so the next wave retries publication
            //                                  without needing new due rows (one retry).
            //   Failure + resolved == 0 (N3) → SetLastResolvedAt: a retry-only or first-failure
            //                                  wave with no new rows to retry; clearing here would
            //                                  put a broken source at the head of the never-served
            //                                  arm on every subsequent wave, permanently starving
            //                                  other sources.  Stamp it so it competes on due_count
            //                                  rather than always ranking first.
            var publishSucceeded = false;
            try
            {
                // definition is not null here: the loop only exits via break when it is non-null and enabled.
                await PublishAsync(config, definition!, sourceUrl, cancellationToken).ConfigureAwait(false);
                publishSucceeded = true;
            }
            finally
            {
                if (publishSucceeded || resolved == 0)
                    // Success (normal path) or no-progress failure (N3): stamp so the source
                    // competes in the due-row arm and does not block the platform lane.
                    _db.SetLastResolvedAt(sourceUrl, DateTimeOffset.UtcNow);
                else
                    // resolved > 0 but publish failed: allow exactly one retry wave by clearing.
                    _db.ClearLastResolvedAt(sourceUrl);
            }

            LogWaveFinished(sourceUrl, resolved, due.Count);
            return resolved;
        }

        /// <summary>Resolves one video: one yt-dlp call, then stores its metadata.</summary>
        /// <returns>Whether the row came out resolved.</returns>
        private async Task<bool> ResolveOneAsync(
            PluginConfiguration config, VideoRecord video, int retentionDays, CancellationToken cancellationToken)
        {
            var attemptedAt = DateTimeOffset.UtcNow;

            try
            {
                var result = await _ytDlp
                    .GetMetadataAsync(video.VideoUrl, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Metadata is not { } meta)
                {
                    // Until the per-platform error whitelist lands (spec §7), an unrecognised
                    // failure is treated uniformly: every failure increments ResolutionAttempts.
                    // The distinction between a platform-caused failure and a video-caused one
                    // has an obvious home here (checking result.Outcome) but is not yet made —
                    // that classification is separate, later work.
                    MarkFailed(video, attemptedAt, result.Outcome.ToString());
                    return false;
                }

                var title = PlatformRules.IsTwitchExtractor(meta)
                    ? (PlatformRules.TitleFor(meta) ?? meta.Id)
                    : (meta.Title ?? meta.Id);

                SyncService.FillVideoRecord(
                    video, meta, video.SourceUrl, title, attemptedAt, SyncService.ParseDate(meta));

                // Only numbers that do not depend on order are given here: the platform's own, or
                // one parsed from the title. The date-based rank waits for the source to settle
                // (PrepareForPublication), when every video of the day has its date.
                SyncService.AssignNumbers(video, meta, title, dateBased: null);

                return await FinishResolutionAsync(
                        config, video, meta.ManifestUrl, retentionDays, attemptedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // one unlucky video must not end the wave; it is retried later
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogResolveFailed(ex, video.VideoUrl);
                MarkFailed(video, attemptedAt, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Describes a video whose metadata was just filled in, then stores the outcome.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="video">The row, already filled from its <c>-J</c> answer and numbered.</param>
        /// <param name="manifestUrl">The HLS master the <c>-J</c> answer named, or null.</param>
        /// <param name="retentionDays">The source's retention in days; zero or less disables it.</param>
        /// <param name="attemptedAt">When this resolution attempt started.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Whether the row came out resolved.</returns>
        internal async Task<bool> FinishResolutionAsync(
            PluginConfiguration config,
            VideoRecord video,
            string? manifestUrl,
            int retentionDays,
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(video);

            // Stored before describing, its resolution state untouched: the describer probes
            // through our own stream route, which reads the row from the database and refuses a
            // scheduled stream. An aired premiere or a scheduled stream that has run was listed
            // Upcoming, so without this write its first description always failed.
            _db.UpsertVideo(video);

            // A failure is the video's own: it is marked failed and retried on its backoff (spec E1).
            var describeError = await _description
                .DescribeAsync(config, video, manifestUrl, retentionDays, attemptedAt, cancellationToken)
                .ConfigureAwait(false);
            if (describeError is not null)
            {
                LogDescribeFailedAtResolution(video.VideoUrl, describeError);
                MarkFailed(video, attemptedAt, describeError);
                return false;
            }

            video.ResolutionState = ResolutionState.Resolved;
            video.ResolutionError = null;
            video.ResolutionAttempts = 0;
            video.ResolutionAttemptedAt = attemptedAt;
            _db.UpsertVideo(video);

            return true;
        }

        private void MarkFailed(VideoRecord video, DateTimeOffset attemptedAt, string reason)
        {
            video.ResolutionState = ResolutionState.Failed;
            video.ResolutionAttempts += 1;
            video.ResolutionAttemptedAt = attemptedAt;
            video.ResolutionError = reason.Length > 500 ? reason[..500] : reason;
            _db.UpsertVideo(video);
        }

        private async Task PublishAsync(
            PluginConfiguration config,
            SourceDefinition definition,
            string sourceUrl,
            CancellationToken cancellationToken)
        {
            var sourceRecord = _db.GetSource(sourceUrl);
            if (sourceRecord is null)
                return;

            var publishable = PrepareForPublication(
                _db, sourceUrl, DateTimeOffset.UtcNow, definition.RetentionDays);

            // A source with nothing resolved publishes nothing at all — not even an empty series.
            // The configuration page's status is the only feedback until its first wave lands.
            if (publishable.Count == 0)
                return;

            var libraryName = LibraryRouting.NameFor(config, definition.Mode);
            if (string.IsNullOrWhiteSpace(libraryName))
                return;

            // The series name comes from the source record, which a sync fills the moment it
            // enumerates the source. Publishing under an invented name here would rename the
            // series the day a sync finally names it (same reasoning as LiveProbeService), so
            // when the name is not yet known the publication step is skipped entirely — the next
            // sync will set it and the next wave will publish.
            var seriesName = sourceRecord.Name;
            if (string.IsNullOrWhiteSpace(seriesName))
                return;

            var target = _publisher.ResolveTarget(libraryName);
            var otherAnchors = _publisher.SweepAnchorsFor(config, definition, sourceRecord);

            var report = await _publisher.ReconcileAsync(
                    target, otherAnchors, sourceRecord, definition.Mode, seriesName,
                    publishable,
                    _db.GetPublishableVideoIds().ToHashSet(StringComparer.OrdinalIgnoreCase),
                    config.LocalBaseUrl, config.UseReleaseDateAsDateAdded, cancellationToken)
                .ConfigureAwait(false);

            // Items created by this reconcile receive the description stored at resolution, so a
            // tier is described before anyone plays it. Only the videos the reconcile created an
            // item for are visited: every other one was given its description by the pass that
            // created its items, and looking each of them up again on every publication costs a
            // library query per row for nothing. ApplyStored still skips a member that already
            // has a video stream.
            foreach (var video in publishable)
            {
                if (report.CreatedVideoIds.Contains(video.Id) && video.StreamDescriptions is { Length: > 0 })
                    _describer.ApplyStored(video.Id, video.StreamDescriptions, cancellationToken);
            }

            // Record where and in what shape this source published: the anchor so the retirement
            // path can find its items even after the library it used leaves the configuration, and
            // the mode so a sync can tell that the administrator has since flipped it.
            sourceRecord.PublishedAnchorId = target.Anchor.Id;
            sourceRecord.PublishedMode = definition.Mode;
            _db.UpsertSource(sourceRecord);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "Resolution wave on {SourceUrl}: {Resolved} of {Due} video(s) resolved")]
        private partial void LogWaveFinished(string sourceUrl, int resolved, int due);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
            Message = "Resolving {VideoUrl} failed; it will be retried after its backoff")]
        private partial void LogResolveFailed(Exception exception, string videoUrl);

        [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
            Message = "Describing {VideoUrl} failed ({Reason}); it will be retried after its backoff")]
        private partial void LogDescribeFailedAtResolution(string videoUrl, string reason);
    }
}

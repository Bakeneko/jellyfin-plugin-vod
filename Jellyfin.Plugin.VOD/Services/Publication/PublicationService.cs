using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>What one pass of the publication loop did.</summary>
    /// <param name="Turns">
    /// How many turns the loop ran, including the last one — the one that found nothing left and
    /// ended the pass.
    /// </param>
    /// <param name="SourcesListed">
    /// How many listings succeeded. A listing that failed is not counted: it enumerated nothing, so
    /// reporting it would overstate what the pass learned.
    /// </param>
    /// <param name="VideosFetched">How many videos the waves fetched metadata for.</param>
    /// <param name="VideosPublished">How many items the checkups added to the library.</param>
    /// <param name="SourcesRetired">
    /// How many sources left the configuration and were purged from the library.
    /// </param>
    /// <param name="RefusedLanes">
    /// The platform lanes abandoned during this pass. Work remaining on one of them is why a pass
    /// can end with videos still due, so the caller needs to see them to tell that apart from a
    /// pass that finished everything.
    /// </param>
    /// <param name="HitTurnCap">
    /// Whether the pass stopped because it reached <see cref="PublicationService.MaxTurnsPerPass"/>
    /// rather than because the work ran out. Always a defect: see the constant.
    /// </param>
    public sealed record PassReport(
        int Turns,
        int SourcesListed,
        int VideosFetched,
        int VideosPublished,
        int SourcesRetired,
        IReadOnlyCollection<string> RefusedLanes,
        bool HitTurnCap);

    /// <summary>
    /// The loop. It chains the five steps — listing selection, listing, cleanup, wave, checkup —
    /// until no source has work left.
    /// </summary>
    /// <remarks>
    /// It holds no rule of its own: what to list is <see cref="ListingSelector"/>'s, what is due is
    /// <see cref="IVodDatabase.GetDueVideos"/>'s, and each step owns its own writes. What belongs
    /// here is the control flow, and all of it exists for one of three reasons: not to run for
    /// ever, not to keep anything else waiting, and not to leave a source half-worked.
    /// </remarks>
    public sealed partial class PublicationService : IPublicationPass
    {
        private readonly IVodDatabase _db;
        private readonly ListingStep _listing;
        private readonly CleanupStep _cleanup;
        private readonly WaveStep _wave;
        private readonly CheckupStep _checkup;
        private readonly ILogger<PublicationService> _logger;

        /// <summary>Initializes a new instance of the <see cref="PublicationService"/> class.</summary>
        /// <param name="db">The VOD database: the source rows and the due-video query.</param>
        /// <param name="listing">The listing step.</param>
        /// <param name="cleanup">The cleanup step, in both its global and its per-source form.</param>
        /// <param name="wave">The wave step.</param>
        /// <param name="checkup">The checkup step.</param>
        /// <param name="logger">Logger.</param>
        public PublicationService(
            IVodDatabase db,
            ListingStep listing,
            CleanupStep cleanup,
            WaveStep wave,
            CheckupStep checkup,
            ILogger<PublicationService> logger)
        {
            _db = db;
            _listing = listing;
            _cleanup = cleanup;
            _wave = wave;
            _checkup = checkup;
            _logger = logger;
        }

        /// <summary>The most turns one pass may run.</summary>
        /// <remarks>
        /// A guard-rail, not a throttle. The measured first fill of the configured sources — 7449
        /// videos in waves of 20 — is about 370 turns, so this sits five times above the worst
        /// normal pass. A cap that bit in normal use would be a bug wearing a safety net's clothes:
        /// it would stop a legitimate first fill halfway and leave the library half-published, with
        /// nothing in the report to say why. That is why reaching it is a warning and a flag on the
        /// report rather than a silent return, and why it must never be lowered to "tidy up" a pass
        /// that runs long — a pass that needs more turns than this has a defect somewhere else.
        /// </remarks>
        internal const int MaxTurnsPerPass = 2000;

        /// <summary>Runs one pass: turns of the loop until no source has work left.</summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="progress">
        /// Where to report liveness, or null. See the report inside the loop for what the value
        /// means.
        /// </param>
        /// <param name="now">
        /// The reference time for the FIRST turn. Later turns read the clock again — a turn can
        /// last minutes.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the pass between two sources.</param>
        /// <returns>What the pass did.</returns>
        /// <remarks>
        /// Cancellation is not swallowed, and it is taken between two sources — never inside one,
        /// so no source carries clocks that disagree with what the library holds.
        /// <para>
        /// Inside a source it is taken in the wave and in the cleanup loop, so a shutdown landing
        /// mid-wave skips that source's checkup: the descriptions that wave produced do not survive
        /// the pass, and the videos it had just fetched wait for the next one. Self-healing, at
        /// worst by one scheduled interval — not the stronger "untouched or entirely worked" this
        /// remark used to claim.
        /// </para>
        /// </remarks>
        public async Task<PassReport> RunPassAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            // Refused lanes last for this pass and no longer. The retry is the next run of the
            // scheduled task, and YtDlpThrottle's own limiter paces it — holding the refusal any
            // longer would be a second, hidden backoff on top of the one that already exists.
            var refusedLanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // One listing attempt per source per pass. A listing that fails writes no clock, and a
            // configuration change is recomputed from a fingerprint only a completed checkup
            // stamps: in both cases the source comes back with the same reason on the next turn,
            // and the pass spends a full channel listing per turn until the turn cap. The retry is
            // the next run of the scheduled task, which is also where the throttle paces it.
            //
            // A source already listed this pass stays eligible for its DUE VIDEOS — only the
            // listing is bounded, which is why this set is consulted where listings are planned
            // and nowhere else.
            var listedThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var turns = 0;
            var sourcesListed = 0;
            var videosFetched = 0;
            var videosPublished = 0;
            var sourcesRetired = 0;
            var hitTurnCap = false;

            while (true)
            {
                // Between two turns, never inside a source's block.
                cancellationToken.ThrowIfCancellationRequested();

                if (turns >= MaxTurnsPerPass)
                {
                    hitTurnCap = true;
                    LogTurnCapReached(turns);
                    break;
                }

                turns++;

                // The parameter decides the first turn, so the loop can be tested at all; every
                // later turn reads the clock again. A first fill runs for hours, and comparing a
                // source's clocks against the time the pass STARTED would keep selecting the same
                // work long after it was done.
                var turnNow = turns == 1 ? now : DateTimeOffset.UtcNow;

                // Once per turn, not once per source: it is what keeps the wave from spending
                // yt-dlp calls on a source that is about to be discarded.
                var retirement = _cleanup.RetireSourcesGoneFromTheConfiguration(config, cancellationToken);
                sourcesRetired += retirement.SourcesRetired;

                var work = BuildWorkList(config, refusedLanes, listedThisPass, turnNow);
                if (work.Count == 0)
                {
                    // Said out loud, because the plugin's silence in this exact case is what made
                    // an operator conclude in production that the task was stuck when it had
                    // simply nothing to do.
                    LogNothingToDo(turns);
                    break;
                }

                for (var i = 0; i < work.Count; i++)
                {
                    // Between two sources. Never in the middle of one: the steps write as they go,
                    // and a source interrupted halfway would carry clocks that no longer describe
                    // what the library holds.
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = work[i].Source;
                    var lane = PlatformLane.For(source.Url);

                    using (_logger.BeginScope(new Dictionary<string, object>
                    {
                        ["SourceUrl"] = source.Url,
                    }))
                    {
                        // Tested again here, and not only when the list was built: a source earlier
                        // in THIS turn may have had its lane refused since.
                        if (refusedLanes.Contains(lane))
                        {
                            LogSkippingRefusedLane(source.Url, lane);
                        }
                        else
                        {
                            var tally = await RunOneSourceAsync(
                                work[i], config, turnNow, cancellationToken).ConfigureAwait(false);

                            // Marked on the attempt, not on success: a failure is exactly the case
                            // that must not be retried every turn. Marked here and not where the
                            // work was planned, because a lane refused earlier in this same turn
                            // skips the source without spending the call.
                            if (work[i].Listing is not null)
                                listedThisPass.Add(source.Url);

                            sourcesListed += tally.Listed;
                            videosFetched += tally.Fetched;
                            videosPublished += tally.Published;

                            if (tally.AbandonedForPlatform && refusedLanes.Add(lane))
                                LogLaneRefused(source.Url, lane);
                        }
                    }

                    // Reported per SOURCE and not per turn: one turn of a first fill is minutes
                    // long, and a pass of several hours that reports nothing is indistinguishable
                    // from a blocked one.
                    //
                    // The value is the fraction of THIS turn's sources that are done, so it starts
                    // again at every turn. That is deliberate: the real total cannot be known,
                    // because each listing discovers work as it goes. This is a liveness signal,
                    // not an estimate of completion — a bar that only ever went up would be lying.
                    // The real table is in the log line below.
                    progress?.Report((i + 1) * 100.0 / work.Count);
                }

                LogTurnFinished(turns, work.Count, sourcesListed, videosFetched, videosPublished);
            }

            progress?.Report(100);

            var report = new PassReport(
                turns,
                sourcesListed,
                videosFetched,
                videosPublished,
                sourcesRetired,
                [.. refusedLanes],
                hitTurnCap);

            LogPassFinished(
                report.Turns, report.SourcesListed, report.VideosFetched,
                report.VideosPublished, report.SourcesRetired, refusedLanes.Count);

            return report;
        }

        // ── One source ───────────────────────────────────────────────────────────────────────────

        /// <summary>What one source's block contributed to the pass.</summary>
        private sealed record SourceTally(
            int Listed, int Fetched, int Published, bool AbandonedForPlatform);

        /// <summary>A source to work this turn, with the listing planned for it, if any.</summary>
        private sealed record SourceWork(SourceDefinition Source, PlannedListing? Listing);

        /// <summary>Runs one source's whole block — listing, cleanup, wave, checkup — under its lock.</summary>
        /// <remarks>
        /// The lock spans the WHOLE block and nothing wider.
        /// <para>
        /// Not wider, because a first fill is ~370 turns and hours long: a lock held across the loop
        /// would keep the live probe out for all that time, and the probe is what makes a broadcast
        /// in progress appear at all.
        /// </para>
        /// <para>
        /// Not narrower, because the lock exists for <c>ReconcileAsync</c>, which DELETES the items
        /// a source no longer accounts for — and it is the checkup that reconciles, not the wave.
        /// A lock held around the wave alone would leave the deletion unprotected, and the loser of
        /// two concurrent passes loses its items.
        /// </para>
        /// </remarks>
        private async Task<SourceTally> RunOneSourceAsync(
            SourceWork work,
            PluginConfiguration config,
            DateTimeOffset turnNow,
            CancellationToken cancellationToken)
        {
            var source = work.Source;

            using var handle = await SourceLock
                .AcquireAsync(source.Url, cancellationToken)
                .ConfigureAwait(false);

            var listed = 0;
            var cleanupAllowed = false;

            if (work.Listing is { } planned)
            {
                // Before the listing, not after: the listing writes the clock this clears, so
                // clearing afterwards would undo a listing that actually ran and leave the source
                // looking as though it had never been listed.
                if (planned.Reason == ListingReason.ConfigurationChanged)
                    _db.ClearFullListedAt(source.Url);

                var outcome = await _listing
                    .RunAsync(planned, config, turnNow, cancellationToken)
                    .ConfigureAwait(false);

                cleanupAllowed = outcome.CleanupAllowed;
                if (outcome.Outcome == CallOutcome.Ok)
                    listed++;
            }

            // Only the step's own flag decides, and the timestamp handed over is the one the
            // listing just used: any other would make the videos it has just touched look absent,
            // and the cleanup would retire the entire source.
            if (cleanupAllowed)
                _cleanup.RemoveVideosGoneFromTheSource(source, turnNow, cancellationToken);

            // Selected here, after the listing has created rows and the cleanup has removed them,
            // and from the same query the loop's stop test uses. A separate predicate that could
            // disagree with this selection is how a pass reports work for a video it never
            // surfaces — and loops for ever.
            //
            // At least one video, whatever the configuration says: a wave of zero would leave the
            // stop test saying "there is work" with nothing able to act on it, which is one full
            // turn of the loop per nothing, for ever.
            var due = _db.GetDueVideos(
                source.Url,
                turnNow,
                config.RetryMinutes,
                config.MetadataMaxAgeDays,
                source.RetentionDays,
                Math.Max(1, config.WaveSize));

            var wave = await _wave
                .RunAsync(source, due, config, turnNow, cancellationToken)
                .ConfigureAwait(false);

            // The checkup runs even when the wave was abandoned: the wave hands over what it did
            // fetch before giving up, and a description does not survive the pass that produced it.
            // Skipping the checkup here would throw that work away.
            var checkup = await _checkup
                .RunAsync(source, wave.Descriptions, config, turnNow, cancellationToken)
                .ConfigureAwait(false);

            return new SourceTally(
                listed, wave.Fetched, checkup.Published, wave.AbandonedForPlatform);
        }

        // ── The work list, which is also the stop test ───────────────────────────────────────────

        /// <summary>
        /// The sources to work this turn: those with a listing planned, then those with videos due.
        /// </summary>
        /// <remarks>
        /// This list IS the stop test — the pass ends on the turn it comes back empty. One
        /// expression for both questions is deliberate: "is there work" and "what to work on"
        /// cannot then disagree, and a disagreement in that direction is an endless loop.
        /// <para>
        /// A lane refused earlier in this pass is left out entirely, which is what lets the pass
        /// end while work remains on a platform that is refusing everything. A rate limit and a
        /// timeout do not count against a video, so they leave no backoff behind: without this,
        /// the videos of a broken platform would stay due for ever and the loop would spin at one
        /// yt-dlp call per turn.
        /// </para>
        /// </remarks>
        private List<SourceWork> BuildWorkList(
            PluginConfiguration config,
            HashSet<string> refusedLanes,
            HashSet<string> listedThisPass,
            DateTimeOffset turnNow)
        {
            var known = new Dictionary<string, SourceRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in _db.GetAllSources())
                known[record.Url] = record;

            var work = new List<SourceWork>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The selector's order is kept: it puts a configuration change ahead of a first
            // listing, so an administrator's edit is not queued behind ~300 never-listed sources.
            foreach (var planned in ListingSelector.Select(config.Sources, known, config, turnNow))
            {
                if (refusedLanes.Contains(PlatformLane.For(planned.Source.Url)))
                    continue;

                // Left un-taken on purpose, so the loop below can still pick this source up for
                // its due videos: the listing is what is bounded, not the source.
                if (listedThisPass.Contains(planned.Source.Url))
                    continue;

                if (taken.Add(planned.Source.Url))
                    work.Add(new SourceWork(planned.Source, planned));
            }

            foreach (var source in config.Sources)
            {
                // Disabled is paused, not removed: the source keeps its items, but it must not
                // spend a yt-dlp call. The selector already skips it for listings; the due-video
                // query knows nothing about the configuration, so it is skipped here.
                if (!source.Enabled || taken.Contains(source.Url))
                    continue;

                if (refusedLanes.Contains(PlatformLane.For(source.Url)))
                    continue;

                // Limit 1: the question is only whether anything is due. The same query selects
                // the wave itself, which is the point — GetDueVideos already excludes a video
                // serving its backoff, so there is no predicate here to drift from it.
                var due = _db.GetDueVideos(
                    source.Url,
                    turnNow,
                    config.RetryMinutes,
                    config.MetadataMaxAgeDays,
                    source.RetentionDays,
                    limit: 1);

                if (due.Count > 0 && taken.Add(source.Url))
                    work.Add(new SourceWork(source, null));
            }

            return work;
        }

        // ── Log messages ─────────────────────────────────────────────────────────────────────────

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Publication pass: turn {Turn} found nothing to do, ending the pass")]
        private partial void LogNothingToDo(int turn);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Publication pass: turn {Turn} over {Sources} source(s) finished — "
                + "{Listed} listed, {Fetched} fetched, {Published} published so far")]
        private partial void LogTurnFinished(
            int turn, int sources, int listed, int fetched, int published);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Publication pass: {SourceUrl} was abandoned for its platform, so lane "
                + "'{Lane}' is refused for the rest of this pass; the next run of the task is the retry")]
        private partial void LogLaneRefused(string sourceUrl, string lane);

        [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
            Message = "Publication pass: skipping {SourceUrl}, lane '{Lane}' is refused for the rest of this pass")]
        private partial void LogSkippingRefusedLane(string sourceUrl, string lane);

        [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
            Message = "Publication pass: stopped at the turn cap after {Turns} turns with work "
                + "still left. A cap that bites in normal use is a defect, not a safety net — "
                + "report this rather than raising it")]
        private partial void LogTurnCapReached(int turns);

        [LoggerMessage(EventId = 6, Level = LogLevel.Information,
            Message = "Publication pass finished after {Turns} turn(s): {Listed} listed, "
                + "{Fetched} fetched, {Published} published, {Retired} retired, {Refused} lane(s) refused")]
        private partial void LogPassFinished(
            int turns, int listed, int fetched, int published, int retired, int refused);
    }
}

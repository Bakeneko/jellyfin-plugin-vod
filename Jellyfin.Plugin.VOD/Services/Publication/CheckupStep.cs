using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>What one checkup produced.</summary>
    /// <param name="Outcome">How the step ended overall.</param>
    /// <param name="Detail">Why, in plain text, or null on a clean success.</param>
    /// <param name="Published">Primary items the reconciliation added.</param>
    /// <param name="Updated">Primary items the reconciliation rewrote.</param>
    /// <param name="Unpublished">Items the reconciliation deleted, orphans and empty containers.</param>
    /// <param name="Renumbered">Rows whose season or episode changed and were written back.</param>
    /// <param name="MarkedForRedescribe">
    /// Items recreated without a description in hand, made eligible for describing again.
    /// </param>
    public sealed record CheckupOutcome(
        CallOutcome Outcome,
        string? Detail,
        int Published,
        int Updated,
        int Unpublished,
        int Renumbered,
        int MarkedForRedescribe);

    /// <summary>
    /// Recomputes a source's publishability and numbering, then reconciles the Jellyfin library
    /// with what it must hold. Runs after every wave, on every source the pass touched.
    /// </summary>
    /// <remarks>
    /// This is the only step that publishes, and therefore the only one that decides what the user
    /// sees — and what they lose, because an unpublished item takes its watch history with it.
    /// <para>
    /// It holds no rule of its own: publishability and numbering are computed by
    /// <see cref="Publishability"/> and <see cref="DateNumbering"/>, and the library work is
    /// <see cref="LibraryPublisher"/>'s. What belongs here is the order of those calls and the
    /// bookkeeping that follows a success. The live probe does the same work on a smaller scale
    /// (see the tail of <c>LiveProbeService.ProbeSourceCoreAsync</c>), and the two must agree:
    /// both reconcile the same sources, and a pass that behaved differently from the other would be
    /// undone by it a few minutes later.
    /// </para>
    /// </remarks>
    public sealed partial class CheckupStep
    {
        private readonly IVodDatabase _db;
        private readonly LibraryPublisher _publisher;
        private readonly StreamDescriber _describer;
        private readonly ILogger<CheckupStep> _logger;

        /// <summary>Initializes a new instance of the <see cref="CheckupStep"/> class.</summary>
        /// <param name="db">The VOD database.</param>
        /// <param name="publisher">The library publisher: resolves the target and reconciles it.</param>
        /// <param name="describer">
        /// Applies to a freshly created item the description the wave produced for it.
        /// </param>
        /// <param name="logger">Logger.</param>
        public CheckupStep(
            IVodDatabase db,
            LibraryPublisher publisher,
            StreamDescriber describer,
            ILogger<CheckupStep> logger)
        {
            _db = db;
            _publisher = publisher;
            _describer = describer;
            _logger = logger;
        }

        /// <summary>
        /// Recomputes what this source should publish, then makes the library say so.
        /// </summary>
        /// <param name="source">The source being checked up.</param>
        /// <param name="descriptions">
        /// The streams the wave just produced, per video id. A wave that produced none passes an
        /// empty dictionary rather than null: a description does not survive the pass that made it,
        /// so there is no other place to read one from.
        /// </param>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="now">The reference time for publishability and every clock write.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>What the checkup produced.</returns>
        public async Task<CheckupOutcome> RunAsync(
            SourceDefinition source,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>> descriptions,
            PluginConfiguration config,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(descriptions);
            ArgumentNullException.ThrowIfNull(config);

            var rows = _db.GetVideosBySource(source.Url);

            // Publishability is computed, never read: there is no state column any more. The rows
            // are the ones this checkup just reloaded, so the wave's writes are already in them.
            var publishable = new List<VideoRecord>();
            foreach (var row in rows)
            {
                if (Publishability.IsPublishable(row, source.RetentionDays, now))
                    publishable.Add(row);
            }

            var renumbered = Renumber(publishable);

            var libraryName = LibraryRouting.NameFor(config, source.Mode);
            if (string.IsNullOrWhiteSpace(libraryName))
            {
                // Not a breakdown: an incomplete configuration, which the configuration page already
                // reports. Recording a failure would advance this source's consecutive-failure count
                // every turn and make it look broken.
                const string NoLibrary = "No library is configured for this source's mode.";
                LogNoLibraryConfigured(source.Url, source.Mode);
                _db.SetStageResult(source.Url, SourceStage.Checkup, CallOutcome.Ok, NoLibrary);
                return Nothing(NoLibrary, renumbered);
            }

            var record = _db.GetSource(source.Url);
            if (record is null || string.IsNullOrWhiteSpace(record.Name))
            {
                // The series name is the one the listing resolved. Publishing under an invented name
                // would create an item the user cannot identify, and would rename the series the day
                // a listing finally names it.
                const string NoName = "The source has no name yet: it has never been listed successfully.";
                LogNoNameYet(source.Url);
                _db.SetStageResult(source.Url, SourceStage.Checkup, CallOutcome.Ok, NoName);
                return Nothing(NoName, renumbered);
            }

            try
            {
                // PrepareTargetAsync, not ResolveTarget: this is now the plugin's only publication
                // path, and on a fresh install whose library is still empty Jellyfin has never
                // indexed its folder — nothing would ever be published. The extra cost is one marker
                // write; the expensive re-validation only happens on the miss path.
                var target = await _publisher
                    .PrepareTargetAsync(libraryName, cancellationToken)
                    .ConfigureAwait(false);

                var report = await _publisher.ReconcileAsync(
                        target,
                        _publisher.SweepAnchorsFor(config, source, record),
                        record,
                        source.Mode,
                        record.Name!,
                        publishable,
                        // Read here rather than received from the caller, and deliberately AFTER
                        // the cleanup has run: the cleanup deletes rows, and it is this
                        // reconciliation that harvests their Jellyfin items — but only because
                        // their ids are no longer in this set. A set computed earlier in the turn
                        // would still carry them, and their items would survive a turn longer.
                        Publishability.ProtectedIds(
                            _db.GetVideoIdsWithMetadata(), rows, source.RetentionDays, now),
                        config.LocalBaseUrl,
                        config.UseReleaseDateAsDateAdded,
                        cancellationToken)
                    .ConfigureAwait(false);

                var marked = ApplyDescriptions(publishable, report, descriptions, cancellationToken);

                // Written only now, and only on a success. The anchor is the one pointer to this
                // source's items once its library leaves the configuration — nothing else writes it,
                // so without this the cleanup's stored-anchor path is dead code and such a source
                // would keep orphaned items for ever.
                _db.SetPublishedAnchorId(source.Url, target.Anchor.Id);

                // Same reason the other way round: stamping the fingerprint on a failure would mark
                // a configuration change as taken into account although nothing was published, and
                // the source would only become due again at the next full listing — up to seven days.
                _db.SetConfigFingerprint(source.Url, SourceFingerprint.Of(source));

                _db.SetStageResult(source.Url, SourceStage.Checkup, CallOutcome.Ok, null);
                LogCheckupFinished(
                    source.Url, report.Created, report.Updated, report.Removed, renumbered, marked);

                return new CheckupOutcome(
                    CallOutcome.Ok, null,
                    Published: report.Created,
                    Updated: report.Updated,
                    Unpublished: report.Removed,
                    Renumbered: renumbered,
                    MarkedForRedescribe: marked);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckupFailed(source.Url, ex.Message);
                _db.SetStageResult(source.Url, SourceStage.Checkup, CallOutcome.ExtractorError, ex.Message);

                return new CheckupOutcome(
                    CallOutcome.ExtractorError, ex.Message,
                    Published: 0, Updated: 0, Unpublished: 0,
                    Renumbered: renumbered,
                    MarkedForRedescribe: 0);
            }
        }

        // ── Private helpers ─────────────────────────────────────────────────────────────────────

        /// <summary>A checkup that computed everything but published nothing.</summary>
        private static CheckupOutcome Nothing(string detail, int renumbered)
            => new(
                CallOutcome.Ok, detail,
                Published: 0, Updated: 0, Unpublished: 0,
                Renumbered: renumbered,
                MarkedForRedescribe: 0);

        /// <summary>
        /// Recomputes every publishable row's number and writes back the ones that changed.
        /// </summary>
        /// <param name="publishable">The rows that may be published, in any order.</param>
        /// <returns>How many rows were written back.</returns>
        /// <remarks>
        /// The numbering is recomputed at every checkup and never read from the database as an
        /// acquired truth: that choice is what dissolved the "settled source" rule. A row is
        /// rewritten only when its number differs, or an unchanged number would cost one write per
        /// video per turn, for every source.
        /// </remarks>
        private int Renumber(IReadOnlyList<VideoRecord> publishable)
        {
            var numbers = DateNumbering.Assign(publishable);
            var renumbered = 0;

            foreach (var row in publishable)
            {
                int? season = null;
                int? episode = null;
                if (numbers.TryGetValue(row.Id, out var assigned))
                {
                    season = assigned.Season;
                    episode = assigned.Episode;
                }

                if (row.SeasonNumber == season && row.EpisodeNumber == episode)
                    continue;

                row.SeasonNumber = season;
                row.EpisodeNumber = episode;
                _db.UpsertVideo(row);
                renumbered++;
            }

            return renumbered;
        }

        /// <summary>
        /// Gives each item this reconciliation created the description the wave made for it, and
        /// makes the others eligible for describing again.
        /// </summary>
        /// <param name="publishable">The rows the reconciliation was given.</param>
        /// <param name="report">What the reconciliation changed.</param>
        /// <param name="descriptions">The streams the wave produced, per video id.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>How many rows were marked for describing again.</returns>
        /// <remarks>
        /// Only the created ids are visited. An existing item already has its streams, and
        /// <c>ApplyDescribed</c> would skip it anyway — but not filtering would cost one library
        /// query per row on every turn.
        /// <para>
        /// An item created WITHOUT a description in hand is marked for describing again. A
        /// description no longer outlives the pass that produced it, so an item recreated between
        /// two passes — after a purge, after a change of mode — has none; without this marking it
        /// would stay mute until the freshness clock expired, and Jellyfin decides an undescribed
        /// tier's codec blind, which means transcoding.
        /// </para>
        /// <para>
        /// The condition is "was described SUCCESSFULLY before", and the word carries weight: a
        /// stored <see cref="CallOutcome.None"/> means the describing concluded that there is
        /// nothing to describe — no HLS master, no quality tier — so re-describing would reach the
        /// same conclusion and the clock would be cleared again on the next recreation. Marking
        /// such a video costs an extra turn of the loop per video and buys no stream; measured on
        /// the test instance, roughly six percent of YouTube videos are in that state.
        /// </para>
        /// </remarks>
        private int ApplyDescriptions(
            IReadOnlyList<VideoRecord> publishable,
            ReconcileReport report,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>> descriptions,
            CancellationToken cancellationToken)
        {
            var marked = 0;

            foreach (var video in publishable)
            {
                if (!report.CreatedVideoIds.Contains(video.Id))
                    continue;

                if (descriptions.TryGetValue(video.Id, out var streams))
                {
                    _describer.ApplyDescribed(video.Id, streams, cancellationToken);
                }
                else if (video.DescribeResult == CallOutcome.Ok)
                {
                    _db.ClearDescribedAt(video.Id);
                    marked++;
                }
            }

            return marked;
        }

        // ── Log messages ─────────────────────────────────────────────────────────────────────────

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "Checkup for {SourceUrl}: {Published} published, {Updated} updated, "
                + "{Unpublished} unpublished, {Renumbered} renumbered, {Marked} to describe again")]
        private partial void LogCheckupFinished(
            string sourceUrl, int published, int updated, int unpublished, int renumbered, int marked);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
            Message = "Checkup for {SourceUrl} failed: {Reason}; neither the anchor nor the "
                + "configuration fingerprint was written")]
        private partial void LogCheckupFailed(string sourceUrl, string reason);

        [LoggerMessage(EventId = 3, Level = LogLevel.Information,
            Message = "Checkup for {SourceUrl}: no library is configured for mode {Mode}, "
                + "so nothing was published")]
        private partial void LogNoLibraryConfigured(string sourceUrl, SourceMode mode);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information,
            Message = "Checkup for {SourceUrl}: the listing has not resolved a name yet, "
                + "so nothing was published")]
        private partial void LogNoNameYet(string sourceUrl);
    }
}

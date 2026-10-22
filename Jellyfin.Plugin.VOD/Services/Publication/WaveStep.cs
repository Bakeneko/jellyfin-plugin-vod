using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// What one wave of metadata fetches and stream descriptions produced.
    /// </summary>
    /// <param name="Outcome">The overall stage outcome: Ok, the first failure, or the platform cause on abandon.</param>
    /// <param name="Detail">Why, in plain text, or null on clean success.</param>
    /// <param name="Fetched">Videos whose metadata arrived and were applied to the row.</param>
    /// <param name="Described">Videos that were described and have streams in <see cref="Descriptions"/>.</param>
    /// <param name="Failed">Videos whose metadata fetch did not return Ok.</param>
    /// <param name="AbandonedForPlatform">True when a rate-limit or timeout cut the wave short.</param>
    /// <param name="Descriptions">
    /// Streams per video id (case-insensitive key), only for videos that were actually described.
    /// The checkup applies these to the items it creates; descriptions travel in memory because the
    /// manifest URLs expire within hours and are not stored.
    /// </param>
    public sealed record WaveOutcome(
        CallOutcome Outcome,
        string? Detail,
        int Fetched,
        int Described,
        int Failed,
        bool AbandonedForPlatform,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>> Descriptions);

    /// <summary>
    /// Processes one wave: for each video, fetches metadata and then describes it in the same pass.
    /// </summary>
    /// <remarks>
    /// "Same pass" is not a convenience: the HLS manifest URL that the metadata answer carries
    /// expires within a few hours and is stored nowhere. Describing in a later pass would mean
    /// re-spending a yt-dlp call to recover it.
    /// </remarks>
    public sealed partial class WaveStep
    {
        private readonly IYtDlpService _ytDlp;
        private readonly StreamDescriptionStep _description;
        private readonly IVodDatabase _db;
        private readonly ILogger<WaveStep> _logger;

        /// <summary>Initializes a new instance of <see cref="WaveStep"/>.</summary>
        /// <param name="ytDlp">The yt-dlp seam. Must be substitutable for tests.</param>
        /// <param name="description">Describes a video's HLS streams from the manifest the metadata answer named.</param>
        /// <param name="db">The VOD database.</param>
        /// <param name="logger">Logger.</param>
        public WaveStep(
            IYtDlpService ytDlp,
            StreamDescriptionStep description,
            IVodDatabase db,
            ILogger<WaveStep> logger)
        {
            _ytDlp = ytDlp;
            _description = description;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Processes every video in <paramref name="wave"/>: fetches metadata and describes each one.
        /// </summary>
        /// <param name="source">The owning source, needed for field mapping and the description window.</param>
        /// <param name="wave">The videos due for a metadata fetch this turn.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="now">The reference time for all clock writes.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What the wave produced.</returns>
        public async Task<WaveOutcome> RunAsync(
            SourceDefinition source,
            IReadOnlyList<VideoRecord> wave,
            PluginConfiguration config,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(wave);
            ArgumentNullException.ThrowIfNull(config);

            // An empty wave means the selector found nothing due — not an error. Writing a stage
            // result here would advance the consecutive-failure counter for no work done, making a
            // source look more broken than it is.
            if (wave.Count == 0)
            {
                return new WaveOutcome(
                    CallOutcome.Ok, null,
                    Fetched: 0, Described: 0, Failed: 0,
                    AbandonedForPlatform: false,
                    Descriptions: new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>>(
                        StringComparer.OrdinalIgnoreCase));
            }

            var descriptions =
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>>(
                    StringComparer.OrdinalIgnoreCase);

            var fetched = 0;
            var described = 0;
            var failed = 0;
            CallOutcome? firstFailureOutcome = null;
            string? firstFailureDetail = null;

            foreach (var video in wave)
            {
                // Check cancellation between videos, never in the middle of writing a row.
                // Jellyfin may restart mid-pass; a video must end up either untouched or fully
                // written — never half-written.
                cancellationToken.ThrowIfCancellationRequested();

                using var scope = _logger.BeginScope(
                    new Dictionary<string, object>
                    {
                        ["SourceUrl"] = source.Url,
                        ["VideoId"] = video.Id,
                    });

                var result = await _ytDlp
                    .GetMetadataAsync(video.VideoUrl, cancellationToken)
                    .ConfigureAwait(false);

                var classified = OutcomeClassifier.Classify(result);

                // Rate-limit and timeout are platform-level faults: every remaining video in the
                // wave would fail the same way, and the throttler would make each one wait its full
                // timeout for nothing. Abandon the wave now — the video that triggered it is
                // recorded, the others are left untouched so they remain due next turn. Neither
                // outcome counts against the video (CountsAgainstTheVideo is false), so no video's
                // retry backoff moves on a platform window.
                if (classified.Outcome is CallOutcome.RateLimited or CallOutcome.Timeout)
                {
                    _db.SetMetadataOutcome(
                        video.Id, classified.Outcome, classified.Detail, now,
                        countFailure: classified.CountsAgainstTheVideo);

                    LogWaveAbandoned(source.Url, video.Id, classified.Outcome);

                    _db.SetStageResult(
                        source.Url, SourceStage.Wave, classified.Outcome, classified.Detail);

                    return new WaveOutcome(
                        classified.Outcome, classified.Detail,
                        Fetched: fetched, Described: described, Failed: failed + 1,
                        AbandonedForPlatform: true,
                        Descriptions: descriptions);
                }

                if (classified.Outcome != CallOutcome.Ok)
                {
                    _db.SetMetadataOutcome(
                        video.Id, classified.Outcome, classified.Detail, now,
                        countFailure: classified.CountsAgainstTheVideo);

                    LogMetadataFailed(source.Url, video.Id, classified.Outcome, classified.Detail);

                    failed++;

                    // Track the first failure: it is the most likely common cause, and a
                    // configuration page showing the last error across a mixed wave misleads.
                    if (firstFailureOutcome is null)
                    {
                        firstFailureOutcome = classified.Outcome;
                        firstFailureDetail = classified.Detail;
                    }

                    continue;
                }

                // Metadata succeeded — fill the row.
                fetched++;
                var meta = result.Metadata!;

                // Listing title wins over the metadata title: the listing honours youtube:lang
                // while the metadata call always returns the original. The NativeId fallback
                // covers Arte, which sends no listing title at all.
                var publishedTitle = PublishedTitle.For(
                    video.ListingTitle, meta, video.NativeId ?? video.Id);

                MetadataFill.Apply(video, meta, source.Url, publishedTitle, now);

                // Stored before describing: the describer probes through our own stream route,
                // which reads this row from the database and refuses a video still marked Upcoming.
                // A row not yet written would make the probe silently return nothing, losing the
                // description. The same order is kept in the live probe (LiveProbeService.cs, the
                // ended-broadcast refresh loop).
                _db.UpsertVideo(video);

                // Success resets the consecutive-failure counter regardless of the countFailure flag.
                _db.SetMetadataOutcome(video.Id, CallOutcome.Ok, null, now, countFailure: true);

                var description = await _description
                    .DescribeAsync(
                        config, video, meta.ManifestUrl, source.RetentionDays, now, cancellationToken)
                    .ConfigureAwait(false);

                // Attempted is false for live broadcasts, scheduled streams, missing manifests,
                // videos with no offered tier, and videos outside the retention window. None of
                // these are failures — writing a result here would create a false cause where there
                // is simply nothing to do, and would incorrectly advance describe_attempts.
                // Attempted OR conclusive: both are recorded, for opposite reasons. An attempt
                // has an outcome to report. A conclusive non-attempt — no manifest, no offered
                // tier — must stamp the clock or the video stays selected for ever: its metadata
                // fetch succeeds every turn, which resets its failure counter, so no backoff ever
                // starts and the pass cannot finish. Measured on the test instance, six percent of
                // YouTube videos are in that state. The thirty-day refresh is their retry.
                //
                // A non-conclusive non-attempt — a live stream, a video outside retention — writes
                // nothing: the selection query already excludes both, and the condition can change
                // without a new fetch.
                if (description.Attempted || description.Conclusive)
                {
                    _db.SetDescribeOutcome(video.Id, description.Outcome, description.Detail, now);

                    if (description.Streams is not null)
                    {
                        descriptions[video.Id] = description.Streams;
                        described++;
                    }
                }
            }

            // Wave stage result: the first failure is chosen over any later ones — it is the most
            // likely common cause of a bad batch. Last-error would mislead a reader looking at a
            // mixed wave.
            var stageOutcome = firstFailureOutcome ?? CallOutcome.Ok;
            var stageDetail = firstFailureOutcome is not null ? firstFailureDetail : null;

            _db.SetStageResult(source.Url, SourceStage.Wave, stageOutcome, stageDetail);

            return new WaveOutcome(
                stageOutcome, stageDetail,
                Fetched: fetched, Described: described, Failed: failed,
                AbandonedForPlatform: false,
                Descriptions: descriptions);
        }

        // ── Log messages ─────────────────────────────────────────────────────────────────────────

        [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
            Message = "Wave for {SourceUrl}: video {VideoId} metadata failed with {Outcome}: {Detail}")]
        private partial void LogMetadataFailed(
            string sourceUrl, string videoId, CallOutcome outcome, string? detail);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
            Message = "Wave for {SourceUrl}: video {VideoId} triggered {Outcome}, abandoning remaining wave")]
        private partial void LogWaveAbandoned(
            string sourceUrl, string videoId, CallOutcome outcome);
    }
}

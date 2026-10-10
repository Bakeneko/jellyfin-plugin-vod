using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Describes the stream of a few videos per pass, newest first, so no tier is ever played
    /// undescribed.
    /// </summary>
    /// <remarks>
    /// The work is paced rather than done at sync time on purpose: a freshly added channel would
    /// otherwise resolve and probe hundreds of videos in one burst — one yt-dlp call and up to 25 MB
    /// read per video — which is exactly the pattern that earns an anti-bot answer from YouTube.
    /// Newest first because that is what a viewer reaches for; the rest catches up pass by pass.
    /// </remarks>
    public sealed partial class StreamDescriptionService
    {
        private readonly IVodDatabase _db;
        private readonly StreamDescriber _describer;
        private readonly YtDlpService _ytDlp;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<StreamDescriptionService> _logger;

        /// <summary>
        /// When each video's description last failed. Held in memory only: losing it on a restart
        /// costs at most one extra attempt per video, while persisting it would mean a schema
        /// migration for a hint.
        /// </summary>
        private readonly ConcurrentDictionary<string, DateTimeOffset> _failedAt = new(StringComparer.Ordinal);

        /// <summary>Initializes a new instance of the <see cref="StreamDescriptionService"/> class.</summary>
        /// <param name="db">The VOD database, which holds the quality ladders.</param>
        /// <param name="describer">Describes one video's version group.</param>
        /// <param name="ytDlp">Resolves a video to its upstream master playlist.</param>
        /// <param name="httpClientFactory">Used to fetch the upstream master.</param>
        /// <param name="logger">The logger.</param>
        public StreamDescriptionService(
            IVodDatabase db,
            StreamDescriber describer,
            YtDlpService ytDlp,
            IHttpClientFactory httpClientFactory,
            ILogger<StreamDescriptionService> logger)
        {
            _db = db;
            _describer = describer;
            _ytDlp = ytDlp;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>The videos a pass may describe, newest first.</summary>
        /// <param name="now">The reference time, for the back-off window.</param>
        /// <param name="retryHours">How long a failed video is left alone.</param>
        /// <returns>The ordered candidates.</returns>
        /// <remarks>
        /// A video qualifies when it is not broadcasting — a live's description changes under it and
        /// its publication rules own its fields — and when it is published as a version group, that
        /// is when its ladder offers at least one tier. One tier still makes two items, the primary
        /// and the reference tier, and choosing the tier from the primary's page is not probed:
        /// Jellyfin only probes the requested item's <c>mediaSources[0]</c>. The playback route's
        /// background description then races the HLS request that fixes ffmpeg's arguments (won by
        /// 280 ms when measured on 2026-10-10), and track labels only appear once played. Such a
        /// group costs one probe, the reference tier being copied from the primary. A video with no
        /// tier is a lone primary, which Jellyfin probes itself. Whether a member still lacks a
        /// description is asked later, per video, because it takes a library query.
        /// <para>
        /// A video without a publication date sorts after every dated one: newest first serves the
        /// videos most likely to be played next, and an undated one gives no reason to jump the
        /// queue. A failed video is held back while less than <paramref name="retryHours"/> have
        /// passed, and released on the exact boundary.
        /// </para>
        /// </remarks>
        internal IReadOnlyList<VideoRecord> Candidates(DateTimeOffset now, int retryHours)
        {
            var candidates = new List<VideoRecord>();
            foreach (var source in _db.GetAllSources())
            {
                foreach (var video in _db.GetVideosBySource(source.Url))
                {
                    if (video.LiveStatus != LiveStatus.NotLive)
                    {
                        continue;
                    }

                    if (QualityLadder.OfferedTiers(video.Extras.Qualities).Count == 0)
                    {
                        continue;
                    }

                    if (_failedAt.TryGetValue(video.Id, out var failedAt)
                        && now - failedAt < TimeSpan.FromHours(retryHours))
                    {
                        continue;
                    }

                    candidates.Add(video);
                }
            }

            candidates.Sort((a, b) => Nullable.Compare(b.PublishedAt, a.PublishedAt));
            return candidates;
        }

        /// <summary>Records that a video's description failed, so a pass leaves it alone a while.</summary>
        /// <param name="videoId">The video's opaque VOD uuid.</param>
        /// <param name="failedAt">When it failed.</param>
        /// <remarks>
        /// Internal rather than private so the back-off can be tested without driving a whole pass
        /// through yt-dlp.
        /// </remarks>
        internal void RememberFailure(string videoId, DateTimeOffset failedAt)
            => _failedAt[videoId] = failedAt;

        /// <summary>Describes up to one batch of videos.</summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="progress">Progress reporter, driven by the batch's own size.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>How many videos this pass described.</returns>
        public async Task<int> DescribeNextBatchAsync(
            PluginConfiguration config,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(progress);

            var batchSize = config.StreamDescriptionBatchSize;
            if (batchSize <= 0)
            {
                progress.Report(100);
                return 0;
            }

            var now = DateTimeOffset.UtcNow;
            var described = 0;

            foreach (var video in Candidates(now, config.StreamDescriptionRetryHours))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (described >= batchSize)
                {
                    break;
                }

                if (!_describer.NeedsDescription(video.Id))
                {
                    continue;
                }

                progress.Report((double)described / batchSize * 100);

                if (await DescribeAsync(config, video, cancellationToken).ConfigureAwait(false))
                {
                    _failedAt.TryRemove(video.Id, out _);
                }
                else
                {
                    RememberFailure(video.Id, now);
                }

                described++;
            }

            progress.Report(100);
            return described;
        }

        /// <summary>Resolves one video's master and hands it to the describer.</summary>
        /// <returns><see langword="false"/> when nothing could be described, so it is retried later.</returns>
        /// <remarks>
        /// The route's own resolution path is not reused: it answers a caller with a redirect or a
        /// 503, decisions that mean nothing here. What matters is only whether a master came back.
        /// </remarks>
        private async Task<bool> DescribeAsync(
            PluginConfiguration config, VideoRecord video, CancellationToken cancellationToken)
        {
            try
            {
                var resolution = await _ytDlp
                    .ResolveAsync(video.VideoUrl, StreamCacheKey.DefaultFormat, cancellationToken)
                    .ConfigureAwait(false);

                if (resolution is null || !resolution.IsManifest)
                {
                    LogNoMaster(video.VideoUrl);
                    return false;
                }

                using var client = _httpClientFactory.CreateClient();
                using var response = await client
                    .GetAsync(new Uri(resolution.Url), cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogMasterFetchFailed(resolution.Url, (int)response.StatusCode);
                    return false;
                }

                var master = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                var outcome = await _describer
                    .DescribeGroupAsync(
                        config, video.Id, master, new Uri(resolution.Url), cancellationToken)
                    .ConfigureAwait(false);

                // Attempted == 0: every member was already described or already in flight, so
                // there was nothing to do and nothing went wrong — this is not a failure.
                return outcome.Attempted == 0 || outcome.Described > 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // one unlucky video must not end the pass; it is retried later
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogDescribeFailed(ex, video.VideoUrl);
                return false;
            }
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "No HLS master for {VideoUrl}; not describing it this pass")]
        private partial void LogNoMaster(string videoUrl);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
            Message = "Fetching the master {MasterUrl} returned {StatusCode}; not describing it this pass")]
        private partial void LogMasterFetchFailed(string masterUrl, int statusCode);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Describing {VideoUrl} failed; it will be retried after the configured delay")]
        private partial void LogDescribeFailed(Exception exception, string videoUrl);
    }
}

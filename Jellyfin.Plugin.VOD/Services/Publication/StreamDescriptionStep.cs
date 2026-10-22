using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// Describes a video's media streams from the HLS master its <c>-J</c> answer named.
    /// </summary>
    /// <remarks>
    /// Shared by the two places that fill a row from a fresh <c>-J</c> answer: the wave, and the
    /// live probe when it refreshes a broadcast that has ended. Each decides what a failure means
    /// for its row; this step only reports it.
    /// <para>
    /// It RETURNS the description rather than storing it. The manifest URL lives a few hours, so
    /// describing has to happen while the answer is in hand — but the description itself is applied
    /// to the library items a few steps later in the same pass, and no longer needs a column to
    /// travel in.
    /// </para>
    /// </remarks>
    public sealed class StreamDescriptionStep
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IStreamDescriber _describer;

        /// <summary>Initializes a new instance of the <see cref="StreamDescriptionStep"/> class.</summary>
        /// <param name="httpClientFactory">Fetches the HLS master's content.</param>
        /// <param name="describer">Describes a video's media streams.</param>
        public StreamDescriptionStep(IHttpClientFactory httpClientFactory, IStreamDescriber describer)
        {
            _httpClientFactory = httpClientFactory;
            _describer = describer;
        }

        /// <summary>
        /// Nothing to describe, for a reason only a fresh metadata answer could change: the caller
        /// stamps the describe clock so the video stops being selected until then.
        /// </summary>
        private static StreamDescription NothingToDescribe(string reason)
            => new(CallOutcome.None, reason, null, Attempted: false, Conclusive: true);

        /// <summary>
        /// Not a candidate yet, for a reason that changes on its own — and which the selection
        /// query already excludes. The caller writes nothing.
        /// </summary>
        private static StreamDescription NotYetApplicable { get; } =
            new(CallOutcome.None, null, null, Attempted: false, Conclusive: false);

        /// <summary>Describes a video whose metadata was just filled in.</summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="video">The filled row.</param>
        /// <param name="manifestUrl">The HLS master the <c>-J</c> answer named, or null.</param>
        /// <param name="retentionDays">The source's retention in days; zero or less disables it.</param>
        /// <param name="now">The reference time for the retention window.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What describing produced.</returns>
        /// <remarks>
        /// Four ways there is nothing to describe, none of them a failure: no HLS master, a live or
        /// scheduled stream (its streams change under us), no offered tier (Jellyfin probes a lone
        /// primary itself on first play), and a video outside retention (never published). The
        /// probe reaches our own stream route, which reads the row from the database, so the caller
        /// stores the filled row first.
        /// </remarks>
        public async Task<StreamDescription> DescribeAsync(
            PluginConfiguration config,
            VideoRecord video,
            string? manifestUrl,
            int retentionDays,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(video);

            // Conclusive: only a new metadata answer can bring a manifest or a tier into being, so
            // the caller records these and the thirty-day refresh is the retry. Without that record
            // the video stays selected for ever — its metadata fetch keeps succeeding, so no
            // backoff ever starts.
            if (string.IsNullOrEmpty(manifestUrl))
                return NothingToDescribe("describe: the platform offered no HLS manifest");
            if (QualityLadder.OfferedTiers(video.Extras.Qualities).Count == 0)
                return NothingToDescribe("describe: the platform offered no quality tier");

            // Not yet: a broadcast ends, and a retention window can be widened. The selection query
            // excludes both, so there is nothing to record and recording would be wrong.
            if (video.LiveStatus != LiveStatus.NotLive)
                return NotYetApplicable;
            if (retentionDays > 0 && video.PublishedAt is { } published && published < now.AddDays(-retentionDays))
                return NotYetApplicable;

            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var masterUri))
                return Failed("describe: manifest URL is not a valid absolute URI");

            string master;
            try
            {
                using var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(masterUri, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return Failed($"describe: the master returned {(int)response.StatusCode}");

                master = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                return new StreamDescription(CallOutcome.NetworkError, "describe: " + ex.Message, null, Attempted: true);
            }
            catch (TaskCanceledException)
            {
                return new StreamDescription(
                    CallOutcome.Timeout, "describe: fetching the master timed out", null, Attempted: true);
            }

            if (string.IsNullOrWhiteSpace(master))
                return Failed("describe: the master was empty");

            var described = await _describer
                .DescribeVideoAsync(config, video, master, masterUri, cancellationToken)
                .ConfigureAwait(false);

            return described is null
                ? Failed("describe: a stream probe failed or found no stream")
                : new StreamDescription(CallOutcome.Ok, null, described, Attempted: true);
        }

        private static StreamDescription Failed(string detail)
            => new(CallOutcome.ProbeFailed, detail, null, Attempted: true);
    }
}

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Describes a video's media streams from the HLS master its <c>-J</c> answer named, and
    /// stores the description on its row.
    /// </summary>
    /// <remarks>
    /// Shared by the two places that fill a row from a fresh <c>-J</c> answer: the resolver, and
    /// the live probe when it refreshes a broadcast that has ended. Each decides what a failure
    /// means for its row; this step only reports it.
    /// </remarks>
    public sealed class VideoDescriptionStep
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly StreamDescriber _describer;

        /// <summary>Initializes a new instance of the <see cref="VideoDescriptionStep"/> class.</summary>
        /// <param name="httpClientFactory">Fetches the HLS master's content.</param>
        /// <param name="describer">Describes a video's media streams.</param>
        public VideoDescriptionStep(IHttpClientFactory httpClientFactory, StreamDescriber describer)
        {
            _httpClientFactory = httpClientFactory;
            _describer = describer;
        }

        /// <summary>
        /// Describes a video whose metadata was just filled in and stores the result on its row.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="video">The filled row; its <c>StreamDescriptions</c> is set on success.</param>
        /// <param name="manifestUrl">The HLS master the <c>-J</c> answer named, or null.</param>
        /// <param name="retentionDays">The source's retention in days; zero or less disables it.</param>
        /// <param name="now">The reference time for the retention window.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Null when described or when there is nothing to describe; otherwise why it failed.</returns>
        /// <remarks>
        /// Done as soon as the row is filled rather than after publication: publication waits for
        /// the whole source to settle, and the manifest URL only lives a few hours. Nothing to
        /// describe — so no failure — for a video without HLS, a live (its streams change), a video
        /// without an offered tier (Jellyfin probes a lone primary itself on first play), or a
        /// video outside retention (never published). The probe goes through our own stream route,
        /// which reads the row from the database: the caller stores the filled row first.
        /// </remarks>
        public async Task<string?> DescribeAsync(
            PluginConfiguration config,
            VideoRecord video,
            string? manifestUrl,
            int retentionDays,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(video);

            if (string.IsNullOrEmpty(manifestUrl))
                return null;
            if (video.LiveStatus != LiveStatus.NotLive)
                return null;
            if (QualityLadder.OfferedTiers(video.Extras.Qualities).Count == 0)
                return null;
            if (retentionDays > 0 && video.PublishedAt is { } published && published < now.AddDays(-retentionDays))
                return null;

            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var masterUri))
                return "describe: manifest URL is not a valid absolute URI";

            string master;
            try
            {
                using var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(masterUri, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return $"describe: the master returned {(int)response.StatusCode}";

                master = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                return "describe: " + ex.Message;
            }
            catch (TaskCanceledException)
            {
                return "describe: fetching the master timed out";
            }

            if (string.IsNullOrWhiteSpace(master))
                return "describe: the master was empty";

            var described = await _describer
                .DescribeVideoAsync(config, video, master, masterUri, cancellationToken)
                .ConfigureAwait(false);
            if (described is null)
                return "describe: a stream probe failed or found no stream";

            video.StreamDescriptions = StoredStreams.Serialize(described);
            return null;
        }
    }
}

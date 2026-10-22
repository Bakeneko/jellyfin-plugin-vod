using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// The two yt-dlp calls the publication pass makes, as a seam its tests can replace.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the whole surface of <see cref="YtDlpService"/>: the streaming route and
    /// the live probe keep using the concrete class, and widening this interface to match it would
    /// force every one of them to be mocked.
    /// <para>
    /// This exists because <see cref="YtDlpService"/> is a sealed class with no virtual member, so
    /// a test of the pass had no way to avoid launching a real yt-dlp — and a test that calls a
    /// platform is forbidden here, after a rate-limit incident.
    /// </para>
    /// </remarks>
    public interface IYtDlpService
    {
        /// <summary>Fetches one video's metadata.</summary>
        /// <param name="url">The video page URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The verdict, carrying stderr, the timeout flag, and whatever metadata parsed — including
        /// on a failure, because that answer holds <see cref="VideoMetadata.Availability"/>.
        /// </returns>
        Task<MetadataResult> GetMetadataAsync(string url, CancellationToken cancellationToken);

        /// <summary>Enumerates a channel or playlist without fetching each video.</summary>
        /// <param name="url">The channel or playlist URL.</param>
        /// <param name="maxEntries">
        /// The cap, or null for none. Null is not zero: a source configured with
        /// <c>MaxVideosPerSource = 0</c> means unlimited, and zero would fetch nothing.
        /// </param>
        /// <param name="dateAfter">When set, stops enumerating entries older than this date.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="lane">The throttle lane to spend, when it is not the URL's own platform.</param>
        /// <param name="metadataLanguage">
        /// A BCP-47 prefix pinned as <c>--extractor-args "youtube:lang=XX"</c>, or null. Only the
        /// listing path can serve a localized title; a metadata call always serves the original.
        /// </param>
        /// <returns>The listing, or null when the call produced no usable answer.</returns>
        Task<PlaylistInfo?> GetPlaylistEntriesAsync(
            string url,
            int? maxEntries,
            DateTimeOffset? dateAfter,
            CancellationToken cancellationToken,
            string? lane = null,
            string? metadataLanguage = null);
    }
}

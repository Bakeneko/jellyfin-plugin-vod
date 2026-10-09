using System;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// The single place that decides whether a URL belongs to Twitch and which channel it names.
    /// </summary>
    /// <remarks>
    /// The rule used to be spelled three times — <see cref="SourceTypeDetector"/>'s host test,
    /// <see cref="VodNativeId"/>'s channel pattern and <see cref="PlatformRules"/>'s live-preview
    /// pattern — and they disagreed: a suffix test on "twitch.tv" also accepts "mytwitch.tv", and a
    /// Twitch source type is what admits a source to the live probe and its deletion branch. One
    /// predicate is what keeps them from drifting apart again.
    /// </remarks>
    internal static class TwitchUrls
    {
        private const string TwitchHost = "twitch.tv";

        /// <summary>Whether a host is twitch.tv itself or one of its subdomains.</summary>
        /// <param name="host">The host component of a URL.</param>
        /// <returns><see langword="true"/> for twitch.tv and *.twitch.tv, nothing else.</returns>
        internal static bool IsTwitchHost(string? host)
            => host is not null
               && (host.Equals(TwitchHost, StringComparison.OrdinalIgnoreCase)
                   || host.EndsWith("." + TwitchHost, StringComparison.OrdinalIgnoreCase));

        /// <summary>The channel a Twitch URL names.</summary>
        /// <param name="url">Any URL; a channel root or a channel tab for a match.</param>
        /// <returns>
        /// The channel handle, or <see langword="null"/> when the URL is not an http(s) Twitch
        /// channel URL. A single-VOD URL (<c>/videos/{id}</c>) names no channel and yields null.
        /// </returns>
        internal static string? ChannelFrom(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!IsTwitchHost(uri.Host))
                return null;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0
                || string.Equals(segments[0], "videos", StringComparison.OrdinalIgnoreCase))
                return null;

            return segments[0];
        }
    }
}

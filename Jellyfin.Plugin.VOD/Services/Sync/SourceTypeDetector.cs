using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>Detects the <see cref="SourceType"/> of a source URL from its shape.</summary>
    public static partial class SourceTypeDetector
    {
        [GeneratedRegex(@"^/(channel|c|user)/", RegexOptions.IgnoreCase)]
        private static partial Regex YoutubeChannelPath();

        [GeneratedRegex(@"/videos/RC-\d+/", RegexOptions.IgnoreCase)]
        private static partial Regex ArteCollectionPath();

        /// <summary>Infers the source type from a URL without making any network request.</summary>
        public static SourceType Detect(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return SourceType.Generic;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return SourceType.Generic;

            var host = uri.Host;
            var path = uri.AbsolutePath;
            var query = uri.Query;

            // ── YouTube ────────────────────────────────────────────────────────
            if (host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                // @handle channels
                if (path.StartsWith("/@", StringComparison.Ordinal))
                    return SourceType.YoutubeChannel;

                // /channel/, /c/, /user/ paths
                if (YoutubeChannelPath().IsMatch(path))
                    return SourceType.YoutubeChannel;

                // /playlist?list=
                if (path.Equals("/playlist", StringComparison.OrdinalIgnoreCase)
                    && query.Contains("list=", StringComparison.Ordinal))
                    return SourceType.YoutubePlaylist;

                // /watch?v=…&list=… → playlist context
                if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                    && query.Contains("list=", StringComparison.Ordinal))
                    return SourceType.YoutubePlaylist;

                // /watch?v=… (no list)
                if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
                    return SourceType.YoutubeVideo;

                return SourceType.YoutubeChannel; // /youtube.com/XYZ fallback
            }

            if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
                return SourceType.YoutubeVideo;

            // ── Arte ──────────────────────────────────────────────────────────
            if (host.EndsWith("arte.tv", StringComparison.OrdinalIgnoreCase))
            {
                return ArteCollectionPath().IsMatch(path)
                    ? SourceType.ArtePlaylist
                    : SourceType.ArteVideo;
            }

            // ── france.tv ─────────────────────────────────────────────────────
            if (host.EndsWith("france.tv", StringComparison.OrdinalIgnoreCase))
                return SourceType.FranceTvVideo;

            // ── Twitch ────────────────────────────────────────────────────────
            if (TwitchUrls.IsTwitchHost(host))
            {
                var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    return SourceType.Generic;

                // /videos/{id} is one VOD; {channel}/videos is a channel's archive. The leading
                // segment tells them apart.
                if (string.Equals(segments[0], "videos", StringComparison.OrdinalIgnoreCase))
                    return segments.Length > 1 ? SourceType.TwitchVideo : SourceType.Generic;

                if (segments.Length == 1)
                    return SourceType.TwitchChannelLive;

                return string.Equals(segments[1], "videos", StringComparison.OrdinalIgnoreCase)
                    ? SourceType.TwitchVideos
                    : SourceType.Generic;
            }

            return SourceType.Generic;
        }
    }
}

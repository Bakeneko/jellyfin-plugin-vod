using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Derives stable, human-readable identifiers from yt-dlp extractor keys and source URLs.
    /// These are used as fake IMDB IDs in the library path and in provider metadata so that
    /// Jellyfin's <c>GetUserDataKeys()</c> can recover watch history after a file rename.
    /// </summary>
    /// <remarks>
    /// Format: <c>youtube_v_{id}</c>, <c>youtube_ch_{handle}</c>, <c>youtube_pl_{id}</c>,
    /// <c>arte_v_{id}</c>, <c>arte_pl_{RC-id}</c>, <c>ftv_v_{uuid}</c>.
    /// </remarks>
    public static partial class VodNativeId
    {
        /// <summary>
        /// Returns the stable ID for a single video, derived from the yt-dlp extractor key
        /// and the native video ID (e.g. <c>"youtube_v_PM6lMoClzy8"</c>).
        /// Returns <see langword="null"/> when either argument is absent or the extractor is unknown.
        /// </summary>
        public static string? ForVideo(string? extractorKey, string? nativeId)
        {
            if (string.IsNullOrEmpty(nativeId)) return null;
            return extractorKey switch
            {
                "Youtube" or "YoutubeTab" => $"youtube_v_{nativeId}",
                "ArteTV" => $"arte_v_{nativeId}",
                "FranceTV" => $"ftv_v_{nativeId}",
                _ => null
            };
        }

        /// <summary>
        /// Returns the stable ID for a source (channel, playlist, or series URL),
        /// e.g. <c>"youtube_ch_deadwattsofficiel"</c> or <c>"arte_pl_RC-027708"</c>.
        /// Returns <see langword="null"/> when the URL doesn't match a known pattern.
        /// </summary>
        public static string? ForSource(string? sourceUrl)
        {
            if (string.IsNullOrEmpty(sourceUrl)) return null;

            // YouTube @handle: https://www.youtube.com/@deadwattsofficiel/videos
            var m = YoutubeHandleRegex().Match(sourceUrl);
            if (m.Success) return $"youtube_ch_{m.Groups[1].Value}";

            // YouTube channel ID: https://www.youtube.com/channel/UCxxxxxxxx
            m = YoutubeChannelIdRegex().Match(sourceUrl);
            if (m.Success) return $"youtube_ch_{m.Groups[1].Value}";

            // Legacy YouTube channel forms: /c/name and /user/name
            m = YoutubeLegacyChannelRegex().Match(sourceUrl);
            if (m.Success) return $"youtube_ch_{m.Groups[1].Value}";

            // YouTube playlist: https://www.youtube.com/playlist?list=PLxxxxxxxx
            m = YoutubePlaylistRegex().Match(sourceUrl);
            if (m.Success) return $"youtube_pl_{m.Groups[1].Value}";

            // Arte series/playlist: https://www.arte.tv/fr/videos/RC-027708/happy-valley/
            m = ArtePlRegex().Match(sourceUrl);
            if (m.Success) return $"arte_pl_{m.Groups[1].Value}";

            // Arte single video URL (when used as a source directly)
            m = ArteVideoRegex().Match(sourceUrl);
            if (m.Success) return $"arte_v_{m.Groups[1].Value}";

            return null;
        }

        // YouTube @handle — strips the leading @ so the ID is filename-safe
        [GeneratedRegex(@"youtube\.com/@([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex YoutubeHandleRegex();

        [GeneratedRegex(@"youtube\.com/channel/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex YoutubeChannelIdRegex();

        [GeneratedRegex(@"youtube\.com/(?:c|user)/([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex YoutubeLegacyChannelRegex();

        [GeneratedRegex(@"[?&]list=([A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
        private static partial Regex YoutubePlaylistRegex();

        // Arte RC-XXXXXX (series/playlist — must come before the video regex)
        [GeneratedRegex(@"arte\.tv/[a-z]*/videos/(RC-\d+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ArtePlRegex();

        // Arte NNNNNN-NNN-A (single video)
        [GeneratedRegex(@"arte\.tv/[a-z]*/videos/(\d+-\d+-[A-Z]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ArteVideoRegex();
    }
}

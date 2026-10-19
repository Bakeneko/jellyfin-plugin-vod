using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Derives stable, human-readable identifiers from yt-dlp extractor keys and source URLs.
    /// These identifiers are published as the plugin's own <c>VOD</c> provider id, and on a series
    /// also under the yt-dlp extractor key and <c>MetadataProvider.Custom</c> — the last of these
    /// being what keeps a series' presentation key independent of its name.
    /// </summary>
    /// <remarks>
    /// Format: <c>youtube_v_{id}</c>, <c>youtube_ch_{handle}</c>, <c>youtube_pl_{id}</c>,
    /// <c>arte_v_{id}</c>, <c>arte_pl_{RC-id}</c>, <c>ftv_v_{id}</c> (numeric for sources, UUID for videos).
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
                "Twitch" => $"twitch_v_{nativeId}",
                _ => null
            };
        }

        /// <summary>
        /// Returns <see langword="true"/> when a source key denotes a channel rather than a playlist
        /// or a single video.
        /// </summary>
        /// <remarks>
        /// Only a channel may take its name from yt-dlp's <c>channel</c> field. A playlist carries
        /// that field too, but it holds the playlist's OWNER — preferring it would rename
        /// "WORLDS 2025" to "OTP - VODs".
        /// </remarks>
        /// <param name="sourceKey">A key returned by <see cref="ForSource"/>.</param>
        /// <returns><see langword="true"/> for a channel key.</returns>
        public static bool IsChannel(string? sourceKey)
            => sourceKey is not null
               && sourceKey.Contains("_ch_", StringComparison.Ordinal);

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
            if (m.Success) return $"youtube_ch_{DecodeSegment(m.Groups[1].Value)}";

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

            // france.tv: https://www.france.tv/documentaires/doc/8808888-le-titre.html
            m = FranceTvVideoRegex().Match(sourceUrl);
            if (m.Success) return $"ftv_v_{m.Groups[1].Value}";

            // Twitch channel: both the root and the /videos tab derive the same channel key.
            // "twitch.tv/videos", which names no channel, deliberately derives none — IsChannel
            // would otherwise be true for a URL with no channel to be named after.
            if (TwitchUrls.ChannelFrom(sourceUrl) is { } twitchChannel)
                return $"twitch_ch_{twitchChannel}";

            return null;
        }

        /// <summary>
        /// Percent-decodes a captured URL segment and normalises it to composed form.
        /// </summary>
        /// <remarks>
        /// A handle may be any non-ASCII text, which reaches us percent-encoded. Decoding has to
        /// happen here because the key identifies a series: "@arte%C3%A9vasion" truncated at the
        /// escape yielded "arte", collided with the "@arte" source, and filed 597 episodes under
        /// the wrong series. Composed form matters because "é" and "e"+combining-accent are
        /// different strings for the same handle.
        /// <para>
        /// Case is deliberately NOT folded, although YouTube resolves handles case-insensitively:
        /// folding it would change the derived key of every source whose handle carries a capital,
        /// and the key computes the series GUID — 2143 videos would be re-created and lose their
        /// watch state. That normalisation waits for the rework, where the data is rebuilt anyway.
        /// </para>
        /// </remarks>
        private static string DecodeSegment(string segment)
            => Uri.UnescapeDataString(segment).Normalize(NormalizationForm.FormC);

        // YouTube @handle — the whole path segment, not a character class: a class stops at the
        // first character it does not list, and "%" is how every non-ASCII handle arrives.
        [GeneratedRegex(@"youtube\.com/@([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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

        // france.tv video id — anchored to the final path segment, so a digit-leading
        // intermediate segment cannot be mistaken for the video id.
        [GeneratedRegex(@"france\.tv/(?:[^/]+/)*(\d+)-[^/?#]*/?(?:[?#]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex FranceTvVideoRegex();
    }
}

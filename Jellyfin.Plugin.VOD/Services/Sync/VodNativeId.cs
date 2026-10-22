using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;

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
        /// Derives a video's primary key from the source it belongs to and the platform's own id
        /// for it, so that the same video always gets the same uuid.
        /// </summary>
        /// <param name="sourceIdentity">
        /// The source's derived key, or its URL when the URL is not a shape we recognise.
        /// </param>
        /// <param name="nativeId">The platform's own id for the video.</param>
        /// <returns>A uuid in canonical text form.</returns>
        /// <remarks>
        /// This is what Jellyfin's watch history hangs on, and the reason it may not be random.
        /// <c>GetNewItemId</c> derives an item's GUID from <c>"vod:{uuid}"</c>, and for a Movie that
        /// GUID is the only key Jellyfin writes user data under: <c>Video.GetUserDataKeys</c>
        /// inserts a provider-based key only for Imdb or Tmdb, and Jellyfin 12 accepts neither from
        /// this plugin. Deleting an item does not delete its user data — Jellyfin detaches the rows
        /// and keeps them 90 days — so a video that leaves the database and comes back can recover
        /// its played state, its resume position and its favourite mark, but only if it comes back
        /// under the same uuid. Drawn at random, every retirement and every re-listing silently
        /// threw that away.
        /// <para>
        /// The source's identity rather than its raw URL, so that two spellings of one channel —
        /// the case folding and Unicode normalisation <see cref="ForSource"/> applies — keep one
        /// video's identity. The separator is not decorative: without it ("ab", "c") and
        /// ("a", "bc") would be the same video, and two sources could claim one Jellyfin item.
        /// </para>
        /// <para>
        /// Version 8 is what RFC 9562 reserves for a uuid an application derived rather than drew,
        /// which is exactly what this is. SHA-256 truncated to 16 bytes: the uuid is an identity,
        /// never a secret, and the only property asked of the hash is that two different videos do
        /// not collide.
        /// </para>
        /// </remarks>
        public static string UuidForVideo(string sourceIdentity, string nativeId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sourceIdentity);
            ArgumentException.ThrowIfNullOrEmpty(nativeId);

            var hash = SHA256.HashData(
                Encoding.UTF8.GetBytes("vod-video\u0000" + sourceIdentity + "\u0000" + nativeId));

            var bytes = hash.AsSpan(0, 16).ToArray();

            // RFC 9562: version in the high nibble of byte 6, variant in the top two bits of byte 8.
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

            // Big-endian: the bytes are a hash, so the layout only has to be the same everywhere,
            // and Guid's own constructor reads the first three groups in the host's byte order.
            return new Guid(bytes, bigEndian: true).ToString();
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
            if (m.Success) return $"youtube_ch_{DecodeSegment(m.Groups[1].Value)}";

            // YouTube channel ID: https://www.youtube.com/channel/UCxxxxxxxx
            // Channel IDs are assigned by YouTube and are case-sensitive: UCxxx and ucxxx name
            // distinct channels. Folding would silently merge two series into one.
            m = YoutubeChannelIdRegex().Match(sourceUrl);
            if (m.Success) return $"youtube_ch_{m.Groups[1].Value}";

            // Legacy YouTube channel forms: /c/name and /user/name.
            // YouTube resolves these case-insensitively, so the same decode-and-fold that the
            // handle form uses applies here too.
            m = YoutubeLegacyChannelRegex().Match(sourceUrl);
            if (m.Success) return $"youtube_ch_{DecodeSegment(m.Groups[1].Value)}";

            // YouTube playlist: https://www.youtube.com/playlist?list=PLxxxxxxxx
            // Playlist IDs are case-sensitive; folding would silently merge two series into one.
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
            // Case is folded here, not in TwitchUrls.ChannelFrom: ChannelFrom also drives
            // SourceTypeDetector and PlatformRules, and widening a key-normalisation fix to type
            // detection and live-stream recognition would introduce risk this task does not cover.
            if (TwitchUrls.ChannelFrom(sourceUrl) is { } twitchChannel)
                return $"twitch_ch_{twitchChannel.ToLowerInvariant()}";

            return null;
        }

        /// <summary>
        /// Finds sets of source URLs that derive the same key, and are therefore the same series.
        /// </summary>
        /// <remarks>
        /// The key computes the series GUID, so two sources sharing one key share one series: each
        /// publication renames it and un-claims the other's episodes, indefinitely and without an
        /// error anywhere. That silence, not the derivation itself, is what made the Arte incident
        /// costly — so a collision is reported even though the derivation is now correct.
        /// URLs deriving no key are skipped: the publisher falls back to keying them on their URL,
        /// which cannot collide.
        /// </remarks>
        /// <param name="sourceUrls">The configured source URLs.</param>
        /// <returns>One entry per colliding key; empty when every key is distinct.</returns>
        public static IReadOnlyList<SourceKeyCollision> FindCollisions(IEnumerable<string?> sourceUrls)
        {
            ArgumentNullException.ThrowIfNull(sourceUrls);

            var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var url in sourceUrls)
            {
                if (ForSource(url) is not { } key)
                    continue;

                if (!byKey.TryGetValue(key, out var urls))
                    byKey[key] = urls = [];

                urls.Add(url!);
            }

            var collisions = new List<SourceKeyCollision>();
            foreach (var (key, urls) in byKey)
            {
                if (urls.Count > 1)
                    collisions.Add(new SourceKeyCollision(key, urls));
            }

            return collisions;
        }

        /// <summary>
        /// Percent-decodes a captured URL segment, normalises it to composed Unicode form, and
        /// folds its case.
        /// </summary>
        /// <remarks>
        /// A handle may be any non-ASCII text, which reaches us percent-encoded. Decoding has to
        /// happen here because the key identifies a series: "@arte%C3%A9vasion" truncated at the
        /// escape yielded "arte", collided with the "@arte" source, and filed 597 episodes under
        /// the wrong series. Composed form matters because "é" and "e"+combining-accent are
        /// different strings for the same handle.
        /// <para>
        /// Case is folded because YouTube resolves handles case-insensitively: "@Arte" and "@arte"
        /// name the same channel, so without folding they derive different series GUIDs and each
        /// publication renames the series and un-claims the other's episodes. <c>ToLowerInvariant</c>,
        /// not <c>ToLower</c>: the Turkish "I" makes the result locale-dependent and would let two
        /// servers derive different keys for the same handle. This normalisation was deferred until
        /// the orchestration rework, where the data is purged anyway — applied earlier it would have
        /// re-created 2143 videos and discarded their watch state.
        /// </para>
        /// <para>
        /// Order: NFC normalise first, then case-fold. Folding before normalising can recompose
        /// characters whose case the fold did not reach.
        /// </para>
        /// </remarks>
        private static string DecodeSegment(string segment)
            => Uri.UnescapeDataString(segment).Normalize(NormalizationForm.FormC).ToLowerInvariant();

        // YouTube @handle — the whole path segment, not a character class: a class stops at the
        // first character it does not list, and "%" is how every non-ASCII handle arrives.
        [GeneratedRegex(@"youtube\.com/@([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex YoutubeHandleRegex();

        [GeneratedRegex(@"youtube\.com/channel/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex YoutubeChannelIdRegex();

        // Legacy /c/ and /user/ forms — the whole path segment, not a character class: a class
        // stops at the first character it does not list, and "%" is how every non-ASCII name
        // arrives (the same defect that "@arte%C3%A9vasion" had before v3).
        [GeneratedRegex(@"youtube\.com/(?:c|user)/([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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

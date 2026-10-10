using System.Globalization;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>The key a filtered master playlist is cached under.</summary>
    /// <remarks>
    /// Shared because two callers must agree on it to the character: the stream route, which reads
    /// and writes these entries, and StreamDescriber, which primes one so that
    /// probing a sibling tier is served from memory instead of re-running yt-dlp. Computed twice,
    /// it would drift, and the only symptom would be a silent extra yt-dlp call per probe.
    /// </remarks>
    public static class StreamCacheKey
    {
        /// <summary>The yt-dlp format selector the route applies when a caller supplies none.</summary>
        public const string DefaultFormat = "bv*[protocol^=m3u8]";

        /// <summary>Builds the key identifying one served playlist.</summary>
        /// <param name="id">The video's opaque VOD uuid.</param>
        /// <param name="format">The yt-dlp format selector in force.</param>
        /// <param name="height">The served tier's height, or null for the primary.</param>
        /// <param name="codec">The served tier's codec, or null for the primary.</param>
        /// <param name="audioLangs">The effective comma-separated audio language prefixes.</param>
        /// <param name="subLangs">The effective comma-separated subtitle language prefixes.</param>
        /// <returns>The cache key.</returns>
        public static string For(
            string id, string format, int? height, string? codec, string audioLangs, string subLangs)
            => string.Create(
                CultureInfo.InvariantCulture,
                $"{id}|{format}|{height}|{codec}|{audioLangs}|{subLangs}");
    }
}

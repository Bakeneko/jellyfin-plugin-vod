namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Maps the codec identifiers HLS and yt-dlp use onto the names Jellyfin expects.
    /// </summary>
    /// <remarks>
    /// Shared between the yt-dlp metadata parser and <see cref="MediaStreamBuilder"/>: both read
    /// the same identifiers, and two copies would drift.
    /// </remarks>
    public static class CodecNames
    {
        /// <summary>Normalises one codec identifier, or returns null when there is nothing to map.</summary>
        /// <param name="codec">A codec identifier such as "avc1.640028" or "vp09.00.50.08".</param>
        public static string? Normalise(string? codec)
        {
            if (string.IsNullOrWhiteSpace(codec) || codec == "none")
            {
                return null;
            }

            return codec.Split('.')[0].ToLowerInvariant() switch
            {
                "avc1" or "avc3" => "h264",
                "hev1" or "hvc1" => "hevc",
                "vp09" or "vp9" => "vp9",
                "av01" => "av1",
                "mp4a" => "aac",
                "opus" => "opus",
                "ec-3" => "eac3",
                "ac-3" => "ac3",
                var other => other
            };
        }
    }
}

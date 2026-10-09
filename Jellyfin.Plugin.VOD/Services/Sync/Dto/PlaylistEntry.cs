namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>One entry from a yt-dlp flat-playlist listing.</summary>
    public sealed class PlaylistEntry
    {
        /// <summary>Gets the native video ID as reported by the extractor (e.g. YouTube video ID).</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Gets the full webpage URL of the video.</summary>
        public string VideoUrl { get; init; } = string.Empty;

        /// <summary>Gets the video title from the flat listing (may be incomplete for Arte).</summary>
        public string? Title { get; init; }

        /// <summary>Gets the yt-dlp extractor key (e.g. "Youtube", "ArteTV").</summary>
        public string IeKey { get; init; } = string.Empty;

        /// <summary>Gets the video duration in seconds, when available from the flat listing.</summary>
        public double? Duration { get; init; }

        /// <summary>Whether this entry is broadcasting now, scheduled, or neither.</summary>
        public LiveStatus LiveStatus { get; init; }
    }
}

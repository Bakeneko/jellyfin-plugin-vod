using System;

namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>A persisted video entry.</summary>
    public sealed class VideoRecord
    {
        /// <summary>Gets or sets the opaque UUID (primary key, used in stream URLs to avoid SSRF).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the parent source URL (FK → sources.url).</summary>
        public string SourceUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets the yt-dlp extractor key (e.g. "Youtube", "ArteTV").</summary>
        public string? ExtractorKey { get; set; }

        /// <summary>Gets or sets the native video ID from the source platform.</summary>
        public string? NativeId { get; set; }

        /// <summary>Gets or sets the video page URL as given by the flat-playlist listing.</summary>
        /// <remarks>Never store webpage_url for france.tv — it is rewritten to a smuggled form by yt-dlp.</remarks>
        public string VideoUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets the video title.</summary>
        public string? Title { get; set; }

        /// <summary>Gets or sets a value indicating whether <see cref="PublishedAt"/> is a sync-date fallback.</summary>
        public bool DateIsFallback { get; set; }

        /// <summary>Gets or sets the UTC publication date (frozen at first sync).</summary>
        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>Gets or sets the duration in seconds.</summary>
        public int? DurationSeconds { get; set; }

        /// <summary>Gets or sets the absolute path of the series folder.</summary>
        public string? SeriesPath { get; set; }

        /// <summary>Gets or sets the season number (calendar year for non-native Arte; real season for native Arte).</summary>
        public int? SeasonNumber { get; set; }

        /// <summary>Gets or sets the episode number within the season.</summary>
        public int? EpisodeNumber { get; set; }

        /// <summary>Gets or sets the absolute path of the generated .strm file.</summary>
        public string? StrmPath { get; set; }

        /// <summary>Gets or sets the UTC timestamp of the last sync of this entry.</summary>
        public DateTimeOffset SyncedAt { get; set; }

        /// <summary>Gets or sets the JSON array of available audio language codes.</summary>
        public string? AudioLangs { get; set; }

        /// <summary>Gets or sets the JSON array of available subtitle language codes.</summary>
        public string? SubtitleLangs { get; set; }

        /// <summary>Gets or sets the thumbnail URL.</summary>
        public string? ThumbnailUrl { get; set; }

        /// <summary>Gets or sets the description / synopsis.</summary>
        public string? Description { get; set; }

        /// <summary>Gets or sets the availability expiry date (UTC), when known.</summary>
        public DateTimeOffset? AvailableUntil { get; set; }

        /// <summary>Gets or sets the detected source type (e.g. "youtube_channel", "arte_series").</summary>
        public string? SourceType { get; set; }
    }
}

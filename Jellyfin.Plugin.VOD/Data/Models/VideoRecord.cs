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

        /// <summary>Gets or sets the UTC publication date (frozen at first sync).</summary>
        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>Gets or sets the duration in seconds.</summary>
        public int? DurationSeconds { get; set; }

        /// <summary>Gets or sets the season number (calendar year for non-native Arte; real season for native Arte).</summary>
        public int? SeasonNumber { get; set; }

        /// <summary>Gets or sets the episode number within the season.</summary>
        public int? EpisodeNumber { get; set; }

        /// <summary>Gets or sets the UTC timestamp of the last sync of this entry.</summary>
        public DateTimeOffset SyncedAt { get; set; }

        // Written by the sync, read by nothing yet: reserved for the remux path.
        // No provider may rely on these — tracks come from the filtered HLS master.
        /// <summary>Gets or sets the JSON array of available audio language codes.</summary>
        public string? AudioLangs { get; set; }

        // Written by the sync, read by nothing yet: reserved for the remux path.
        // No provider may rely on these — tracks come from the filtered HLS master.
        /// <summary>Gets or sets the JSON array of available subtitle language codes.</summary>
        public string? SubtitleLangs { get; set; }

        /// <summary>Gets or sets the thumbnail URL.</summary>
        public string? ThumbnailUrl { get; set; }

        /// <summary>The video's tags, as a JSON array, capped at the platform-agnostic limit.</summary>
        public string? Tags { get; set; }

        /// <summary>The video's categories, as a JSON array. Published as Jellyfin genres.</summary>
        public string? Categories { get; set; }

        /// <summary>
        /// The channels that own or uploaded the video, as a JSON array. Published as Jellyfin
        /// studios, qualified with the platform at publish time.
        /// </summary>
        /// <remarks>
        /// Holds the bare names yt-dlp reported, not the qualified studio strings. Keeping the
        /// platform suffix out of storage leaves the labelling free to change without a migration,
        /// and <see cref="ExtractorKey"/> already carries what the suffix is built from.
        /// </remarks>
        public string? Channels { get; set; }

        /// <summary>
        /// When the plugin first discovered this video. Set once, at the initial insert, and never
        /// updated afterwards — that is what distinguishes it from <see cref="SyncedAt"/>, which
        /// carries the latest sync.
        /// </summary>
        /// <remarks>
        /// Holding the discovery date separately is what makes the "use the release date as the date
        /// added" option reversible: neither date is ever the only copy of itself, so the published
        /// DateCreated can be recomputed from either one, in both directions.
        /// </remarks>
        public DateTimeOffset? FirstSeenAt { get; set; }

        /// <summary>
        /// The remote URL for the video thumbnail that was last published to Jellyfin. Compared against the
        /// current URL to decide whether the artwork must be re-pointed; Jellyfin caches the image
        /// locally and keeps no record of its origin.
        /// </summary>
        public string? PublishedImageUrl { get; set; }

        /// <summary>Gets or sets the description / synopsis.</summary>
        public string? Description { get; set; }

        /// <summary>Gets or sets a value indicating whether this is a live stream at the time of last sync.</summary>
        public bool IsLive { get; set; }
    }
}

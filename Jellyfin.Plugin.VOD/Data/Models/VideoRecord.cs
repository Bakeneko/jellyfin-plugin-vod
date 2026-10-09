using System;
using Jellyfin.Plugin.VOD.Services.Sync;

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

        /// <summary>Gets or sets the thumbnail URL.</summary>
        public string? ThumbnailUrl { get; set; }

        /// <summary>
        /// The fields that need no column of their own. Never null: an absent or unreadable payload
        /// reads back as an empty record, so a consumer never has to null-check it.
        /// </summary>
        public VideoExtras Extras { get; set; } = new();

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

        /// <summary>Whether this video is broadcasting now, scheduled, or neither.</summary>
        public LiveStatus LiveStatus { get; set; }
    }
}

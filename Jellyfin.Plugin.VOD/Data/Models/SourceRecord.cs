using System;

namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>A persisted sync source (channel, playlist or single-video URL).</summary>
    public sealed class SourceRecord
    {
        /// <summary>Gets or sets the source URL (primary key).</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Gets or sets the resolved display name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the absolute path of the series folder on disk (Series mode only).</summary>
        public string? SeriesPath { get; set; }

        /// <summary>Gets or sets the thumbnail URL for the series artwork.</summary>
        public string? SeriesThumbnailUrl { get; set; }

        /// <summary>Gets or sets the series synopsis as returned by yt-dlp.</summary>
        public string? SeriesSynopsis { get; set; }

        /// <summary>Gets or sets the UTC timestamp of the last successful sync.</summary>
        public DateTimeOffset? LastSyncedAt { get; set; }

        /// <summary>Gets or sets the error message from the last failed sync, or null when last sync succeeded.</summary>
        public string? LastError { get; set; }

        /// <summary>Gets or sets the count of videos currently synced for this source.</summary>
        public int VideoCount { get; set; }
    }
}

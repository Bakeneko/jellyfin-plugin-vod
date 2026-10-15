using System;
using Jellyfin.Plugin.VOD.Configuration;

namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>A persisted sync source (channel, playlist or single-video URL).</summary>
    public sealed class SourceRecord
    {
        /// <summary>Gets or sets the source URL (primary key).</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Gets or sets the resolved display name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the thumbnail URL for the series artwork.</summary>
        public string? SeriesThumbnailUrl { get; set; }

        /// <summary>
        /// The remote URL for the series image that was last published to Jellyfin. Compared against the
        /// current URL to decide whether the artwork must be re-pointed; Jellyfin caches the image
        /// locally and keeps no record of its origin.
        /// </summary>
        public string? PublishedImageUrl { get; set; }

        /// <summary>
        /// The wide image for the series, shown as its Thumb. On YouTube this is the channel banner;
        /// most sources have none.
        /// </summary>
        public string? SeriesBannerUrl { get; set; }

        /// <summary>The banner URL last published to Jellyfin, for the same reason as
        /// <see cref="PublishedImageUrl"/>.</summary>
        public string? PublishedBannerUrl { get; set; }

        /// <summary>The channel's tags, as a JSON array. Published on the series.</summary>
        public string? Tags { get; set; }

        /// <summary>Gets or sets the series synopsis as returned by yt-dlp.</summary>
        public string? SeriesSynopsis { get; set; }

        /// <summary>Gets or sets the UTC timestamp of the last successful sync.</summary>
        public DateTimeOffset? LastSyncedAt { get; set; }

        /// <summary>Gets or sets the error message from the last failed sync, or null when last sync succeeded.</summary>
        public string? LastError { get; set; }

        /// <summary>Gets or sets the count of videos currently synced for this source.</summary>
        public int VideoCount { get; set; }

        /// <summary>
        /// Gets or sets the id of the anchor folder this source last published into, or null when it
        /// has never published or predates the column.
        /// </summary>
        /// <remarks>
        /// Read at the start of a sync and of a retirement, to know where else this source's items may
        /// still be. It covers the one case the two configured libraries cannot: changing a source's
        /// mode AND its library in the same edit leaves the old target as neither of them. Null is not
        /// an error — the sweep then falls back to the configured libraries.
        /// </remarks>
        public Guid? PublishedAnchorId { get; set; }

        /// <summary>
        /// Gets or sets the mode this source last published in, or null when it has never published.
        /// </summary>
        /// <remarks>
        /// The configuration holds the mode the administrator wants; this holds the one the library
        /// actually reflects. A sync that finds the two disagree clears
        /// <see cref="LastResolvedAt"/>, which is what lets the resolver select a source whose videos
        /// are all resolved and republish them in the new shape. Without it, flipping the mode of a
        /// settled source changes nothing in the library.
        /// </remarks>
        public SourceMode? PublishedMode { get; set; }

        /// <summary>Gets or sets when this source's last resolution wave finished.</summary>
        /// <remarks>
        /// Diagnostic only — the status shown on the configuration page, and the logs. The order
        /// sources are served in is derived from how many videos each has due, so the scheduler
        /// keeps no persistent state of its own.
        /// </remarks>
        public DateTimeOffset? LastResolvedAt { get; set; }
    }
}

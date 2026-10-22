using System;

namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>A configured source, as the database knows it.</summary>
    /// <remarks>
    /// There is no field for "where this source is in its work". Everything the pass needs to
    /// schedule is a clock or a result, so the state the old model carried has no successor.
    /// </remarks>
    public sealed class SourceRecord
    {
        /// <summary>
        /// The configured URL, unchanged, as the administrator typed it. Primary key. Reaches the
        /// platform; NEVER the identity of a series — that is <see cref="Key"/>.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// The key derived from the URL, stored so collision detection and the series lookup need
        /// not recompute it. Null when the URL is not a shape we recognise.
        /// </summary>
        public string? Key { get; set; }

        /// <summary>The series name the listing resolved. Published as it stands.</summary>
        public string? Name { get; set; }

        /// <summary>The source's synopsis as the listing returned it. Published.</summary>
        public string? SeriesSynopsis { get; set; }

        /// <summary>
        /// The source's own image. Kept for information and NOT published: the series is dressed
        /// with its newest video's still, which reads better in Jellyfin's 16:9 slots.
        /// </summary>
        public string? SeriesThumbnailUrl { get; set; }

        /// <summary>The source's wide image, kept for the same reason as <see cref="SeriesThumbnailUrl"/>.</summary>
        public string? SeriesBannerUrl { get; set; }

        /// <summary>The source's tags, as a JSON array. Published on the series.</summary>
        public string? Tags { get; set; }

        /// <summary>
        /// The image URL actually posted on the series, so an unchanged image is not re-pointed.
        /// Jellyfin localises an image on first display and keeps no record of where it came from,
        /// which makes this the only way to tell that the upstream artwork changed.
        /// </summary>
        public string? PublishedImageUrl { get; set; }

        /// <summary>The banner URL actually posted, for the same reason.</summary>
        public string? PublishedBannerUrl { get; set; }

        /// <summary>
        /// The Jellyfin folder this source last published into, or null when it never has.
        /// </summary>
        /// <remarks>
        /// Read at retirement, to find this source's items even when its library has since left the
        /// configuration — the one case the two configured libraries cannot cover.
        /// </remarks>
        public Guid? PublishedAnchorId { get; set; }

        /// <summary>
        /// A fingerprint of the configuration the last successful checkup used. A disagreement with
        /// the current configuration clears <see cref="FullListedAt"/>, which makes a full listing
        /// due and so brings the source back to a checkup.
        /// </summary>
        /// <remarks>
        /// One fingerprint over every parameter, rather than one stored column per parameter: the
        /// latter multiplies special cases, and the v3 fix that compared the published mode alone
        /// is exactly the narrow version of this.
        /// </remarks>
        public string? ConfigFingerprint { get; set; }

        /// <summary>How many videos the database holds for this source. Displayed.</summary>
        public int VideoCount { get; set; }

        /// <summary>
        /// When the last short listing succeeded. The scheduling clock: it is compared against the
        /// source's own listing interval.
        /// </summary>
        public DateTimeOffset? ShortListedAt { get; set; }

        /// <summary>
        /// When the last full listing succeeded. Null means no listing has ever run, and a first
        /// listing is always full — on an empty source every short entry would be unknown and would
        /// escalate to a full one anyway.
        /// </summary>
        public DateTimeOffset? FullListedAt { get; set; }

        /// <summary>
        /// The last <c>playlist_count</c> the platform reported, where it reports one at all.
        /// </summary>
        /// <remarks>
        /// Only playlists return it; a channel does not. It is a better detector than a title,
        /// because a count that drops reveals a DELETION, which a glance at the head of the
        /// listing cannot see. Null must stay null and never become zero: the cleanup treats zero
        /// entries as a platform failure, not as an emptied source.
        /// </remarks>
        public int? ListingCount { get; set; }

        /// <summary>The result of the last short listing.</summary>
        public CallOutcome ShortListingResult { get; set; }

        /// <summary>Why the last short listing ended as it did, in plain text.</summary>
        public string? ShortListingDetail { get; set; }

        /// <summary>Consecutive short-listing failures. Reset to zero by a success.</summary>
        public int ShortListingFailures { get; set; }

        /// <summary>The result of the last full listing.</summary>
        public CallOutcome FullListingResult { get; set; }

        /// <summary>Why the last full listing ended as it did, in plain text.</summary>
        public string? FullListingDetail { get; set; }

        /// <summary>Consecutive full-listing failures. Reset to zero by a success.</summary>
        public int FullListingFailures { get; set; }

        /// <summary>The result of the last cleanup.</summary>
        public CallOutcome CleanupResult { get; set; }

        /// <summary>Why the last cleanup ended as it did, in plain text.</summary>
        public string? CleanupDetail { get; set; }

        /// <summary>Consecutive cleanup failures. Reset to zero by a success.</summary>
        public int CleanupFailures { get; set; }

        /// <summary>The result of the last wave.</summary>
        public CallOutcome WaveResult { get; set; }

        /// <summary>Why the last wave ended as it did, in plain text.</summary>
        public string? WaveDetail { get; set; }

        /// <summary>Consecutive wave failures. Reset to zero by a success.</summary>
        public int WaveFailures { get; set; }

        /// <summary>The result of the last checkup.</summary>
        public CallOutcome CheckupResult { get; set; }

        /// <summary>Why the last checkup ended as it did, in plain text.</summary>
        public string? CheckupDetail { get; set; }

        /// <summary>Consecutive checkup failures. Reset to zero by a success.</summary>
        public int CheckupFailures { get; set; }
    }
}

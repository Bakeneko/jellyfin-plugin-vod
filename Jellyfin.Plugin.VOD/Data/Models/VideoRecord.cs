using System;
using Jellyfin.Plugin.VOD.Services.Sync;

namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>A video one source knows about.</summary>
    /// <remarks>
    /// There is no state field. Whether a video may be published is COMPUTED from this row — see
    /// <see cref="Services.Publication.Publishability"/> — on the rows of a single source the
    /// checkup has already loaded, so the computation costs nothing.
    /// </remarks>
    public sealed class VideoRecord
    {
        /// <summary>
        /// Internal uuid. Primary key, this video's provider id in Jellyfin, and what appears in
        /// its streaming URLs — the platform's own id never does, which is what keeps the stream
        /// route from being an SSRF primitive.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>The owning source's URL. Foreign key.</summary>
        public string SourceUrl { get; set; } = string.Empty;

        /// <summary>The platform's own id. Unique together with <see cref="SourceUrl"/>.</summary>
        public string? NativeId { get; set; }

        /// <summary>The yt-dlp extractor that serves this video ("Youtube", "ArteTV").</summary>
        public string? ExtractorKey { get; set; }

        /// <summary>The video page's URL.</summary>
        /// <remarks>Never store france.tv's <c>webpage_url</c>: yt-dlp rewrites it to a smuggled form.</remarks>
        public string VideoUrl { get; set; } = string.Empty;

        /// <summary>
        /// The title that is PUBLISHED: the listing's localized title where there is one, otherwise
        /// the original from the metadata answer.
        /// </summary>
        public string? Title { get; set; }

        /// <summary>
        /// The title as the LISTING saw it, at the pinned language.
        /// </summary>
        /// <remarks>
        /// Serves ONLY as the comparison point for the retitle detector, and is never published.
        /// The comparison has to be listing-to-listing: a listing serves the localized title and
        /// obeys <c>youtube:lang</c>, while a metadata call always serves the original and ignores
        /// it, so comparing against the published title would mark every video as changed on every
        /// pass.
        /// </remarks>
        public string? ListingTitle { get; set; }

        /// <summary>The description. Published.</summary>
        public string? Description { get; set; }

        /// <summary>The duration in seconds. Published.</summary>
        public int? DurationSeconds { get; set; }

        /// <summary>The video's still. Published, and the newest one dresses the series.</summary>
        public string? ThumbnailUrl { get; set; }

        /// <summary>
        /// The still actually posted on the item, so an unchanged image is not re-pointed.
        /// </summary>
        public string? PublishedImageUrl { get; set; }

        /// <summary>
        /// The publication date. Published, and it also drives the numbering and the choice of the
        /// series image.
        /// </summary>
        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>
        /// Quality tiers, tracks and chapters — what publication needs to know about the streams.
        /// Never null: an absent or unreadable payload reads back as an empty record.
        /// </summary>
        public VideoExtras Extras { get; set; } = new();

        /// <summary>Broadcasting, scheduled, or neither.</summary>
        /// <remarks>Excludes the video from describing, and changes the shape it is published in.</remarks>
        public LiveStatus LiveStatus { get; set; }

        /// <summary>The published season number. RECOMPUTED at every checkup, never frozen.</summary>
        public int? SeasonNumber { get; set; }

        /// <summary>The published episode number. RECOMPUTED at every checkup, never frozen.</summary>
        public int? EpisodeNumber { get; set; }

        /// <summary>
        /// This video's rank in the last listing, 0 being the newest. Orders the waves, and is the
        /// only order available for a video that has never been fetched and so has no date.
        /// </summary>
        public int? ListingPosition { get; set; }

        /// <summary>
        /// When the source first saw this video. NEVER updated — it is the date added that gets
        /// published.
        /// </summary>
        public DateTimeOffset? FirstSeenAt { get; set; }

        /// <summary>
        /// The last time a listing carried this video. Its absence from the last FULL listing means
        /// it has left the source.
        /// </summary>
        /// <remarks>
        /// A short listing refreshes this for its 30 entries only, which is precisely why the
        /// cleanup never runs on one: everything past position 30 would look gone.
        /// </remarks>
        public DateTimeOffset? ListedAt { get; set; }

        /// <summary>
        /// When the metadata fetch last ran. Null, or older than the configured maximum age, means
        /// the video is due a fetch.
        /// </summary>
        public DateTimeOffset? MetadataAt { get; set; }

        /// <summary>How the last metadata fetch ended.</summary>
        public CallOutcome MetadataResult { get; set; }

        /// <summary>Why it ended that way, in plain text.</summary>
        public string? MetadataDetail { get; set; }

        /// <summary>Consecutive metadata failures. Drives the backoff; reset to zero by a success.</summary>
        /// <remarks>
        /// A failure the PLATFORM caused — a rate-limit window — must not advance this: one such
        /// window would otherwise push every video of a source a step further out.
        /// </remarks>
        public int MetadataAttempts { get; set; }

        /// <summary>
        /// When the stream description last ran. Null on a non-live video inside retention means it
        /// is due a description.
        /// </summary>
        /// <remarks>
        /// Deliberately a separate clock from <see cref="MetadataAt"/>: a failing ffprobe must not
        /// make the pass spend another yt-dlp call.
        /// </remarks>
        public DateTimeOffset? DescribedAt { get; set; }

        /// <summary>How the last description ended.</summary>
        public CallOutcome DescribeResult { get; set; }

        /// <summary>Why it ended that way, in plain text.</summary>
        public string? DescribeDetail { get; set; }

        /// <summary>Consecutive description failures. Reset to zero by a success.</summary>
        public int DescribeAttempts { get; set; }
    }
}

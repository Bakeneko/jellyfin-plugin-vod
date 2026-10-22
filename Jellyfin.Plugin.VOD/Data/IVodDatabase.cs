using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Data
{
    /// <summary>Provides access to the VOD SQLite database.</summary>
    /// <remarks>
    /// Split deliberately into readers, one upsert per table, and a targeted setter for every field
    /// the pass writes on its own. An upsert carrying the whole record would let a caller holding a
    /// record loaded earlier in the pass write a stale value back over one written since — the way
    /// the old model lost clears mid-pass.
    /// </remarks>
    public interface IVodDatabase : IDisposable
    {
        /// <summary>Runs migrations and ensures the schema is up to date.</summary>
        void EnsureCreated();

        /// <summary>Inserts or updates the fields of a source that a listing owns.</summary>
        /// <param name="source">The source to upsert.</param>
        /// <remarks>
        /// Writes the key, the name, the synopsis, the two source images, the tags and the video
        /// count. Every clock, every stage triplet, the fingerprint and the anchor have their own
        /// setter and are NOT written here.
        /// </remarks>
        void UpsertSource(SourceRecord source);

        /// <summary>Creates the source's row if it does not exist, and changes nothing if it does.</summary>
        /// <param name="sourceUrl">The source URL, which is the row's key.</param>
        /// <remarks>
        /// A video's <c>source_url</c> is a foreign key onto this table and foreign keys are
        /// enforced, so a source has to have a row before any of its videos can be written — and
        /// the listing learns a source's name and images from the same answer that carries its
        /// videos, so it cannot fill the row before it has ingested them. This writes the key and
        /// nothing else; every other column keeps its default or its current value.
        /// </remarks>
        void EnsureSource(string sourceUrl);

        /// <summary>Gets a source by its URL.</summary>
        /// <param name="url">The source URL (primary key).</param>
        /// <returns>The matching record, or <see langword="null"/>.</returns>
        SourceRecord? GetSource(string url);

        /// <summary>Gets every source.</summary>
        /// <returns>All source records, ordered by URL.</returns>
        IReadOnlyList<SourceRecord> GetAllSources();

        /// <summary>Inserts or updates a video.</summary>
        /// <param name="video">The video to upsert.</param>
        /// <remarks>
        /// <see cref="VideoRecord.FirstSeenAt"/> is written on insert only — the date already on the
        /// row always wins, because it is the date added that gets published.
        /// </remarks>
        void UpsertVideo(VideoRecord video);

        /// <summary>Gets a video by its uuid.</summary>
        /// <param name="uuid">The video uuid (primary key).</param>
        /// <returns>The matching record, or <see langword="null"/>.</returns>
        VideoRecord? GetVideoByUuid(string uuid);

        /// <summary>
        /// Gets every video of a source, undated rows first, then oldest publication date first.
        /// </summary>
        /// <param name="sourceUrl">The parent source URL.</param>
        /// <returns>The rows, which the caller filters for publishability itself.</returns>
        /// <remarks>
        /// There is no "get the publishable ones" query, because publishability is computed rather
        /// than stored — see <see cref="Services.Publication.Publishability"/>. The checkup has
        /// these rows in hand anyway, so the computation costs nothing.
        /// </remarks>
        IReadOnlyList<VideoRecord> GetVideosBySource(string sourceUrl);


        /// <summary>The uuid of every video the plugin knows, across every source.</summary>
        /// <returns>The uuids.</returns>
        IReadOnlyList<string> GetAllVideoIds();

        /// <summary>
        /// The ids of every video whose metadata fetch has succeeded, across every source.
        /// </summary>
        /// <returns>The ids.</returns>
        /// <remarks>
        /// What protects a library item from the orphan sweep, and deliberately WIDER than
        /// publishability: it omits the retention window and the offered-tier check, both of which
        /// are per-source publication filters the owning source's checkup applies itself. Wider is
        /// the safe direction here — an item is never wrongly reaped, where a narrower set would
        /// reap items another source still wants. Across all sources for the same reason.
        /// </remarks>
        IReadOnlyList<string> GetVideoIdsWithMetadata();

        /// <summary>Records each video's rank in the listing that was just read.</summary>
        /// <param name="sourceUrl">The source the listing belongs to.</param>
        /// <param name="positions">Native id to rank, 0 being the newest entry.</param>
        /// <remarks>
        /// Written for every row the listing carries, including the ones the pass will not fetch:
        /// the rank belongs to the listing, and it is the only order available for a video that has
        /// never been fetched and therefore has no date.
        /// </remarks>
        void SetListingPositions(string sourceUrl, IReadOnlyDictionary<string, int> positions);

        /// <summary>Marks videos as seen in the listing that just ran.</summary>
        /// <param name="sourceUrl">The source the listing belongs to.</param>
        /// <param name="nativeIds">The native ids the listing carried.</param>
        /// <param name="when">The time to record.</param>
        /// <remarks>
        /// A short listing touches its 30 entries only, which is exactly why the cleanup must never
        /// run on one: everything past position 30 would look gone.
        /// </remarks>
        void TouchListedAt(string sourceUrl, IReadOnlyCollection<string> nativeIds, DateTimeOffset when);

        /// <summary>
        /// The ids of a source's videos that the last full listing did not carry.
        /// </summary>
        /// <param name="sourceUrl">The source.</param>
        /// <param name="fullListedAt">When that full listing ran.</param>
        /// <returns>The ids to retire.</returns>
        /// <remarks>
        /// The single deletion rule. A full listing is capped at the source's maximum, so a video
        /// deleted from the platform and a video that fell out of the window are both simply
        /// absent — and both mean "retire". Distinguishing them would take three rules of differing
        /// freshness for an identical result.
        /// <para>
        /// A broadcasting or scheduled row is spared while it is fresh: the probe publishes ids no
        /// listing carries. Retention is NOT enforced here — it is a publication filter.
        /// </para>
        /// </remarks>
        IReadOnlyList<string> GetVideosGoneFromLastFullListing(string sourceUrl, DateTimeOffset fullListedAt);

        /// <summary>Records how one stage of a pass ended for a source.</summary>
        /// <param name="sourceUrl">The source.</param>
        /// <param name="stage">The stage.</param>
        /// <param name="outcome">How it ended.</param>
        /// <param name="detail">Why, in plain text; truncated on write.</param>
        /// <remarks>
        /// Always called, success or failure. The failure count is consecutive: a success zeroes it,
        /// anything else advances it, which is what makes an intermittent failure visible with no
        /// history table.
        /// </remarks>
        void SetStageResult(string sourceUrl, SourceStage stage, CallOutcome outcome, string? detail);

        /// <summary>Stamps a listing clock after a listing succeeded.</summary>
        /// <param name="sourceUrl">The source.</param>
        /// <param name="full">True for a full listing, false for a short one.</param>
        /// <param name="when">The time to record.</param>
        /// <remarks>
        /// A FAILED listing must stamp nothing: that is what leaves the source due on the next pass
        /// instead of looking up to date. A full listing stamps both clocks, having just seen
        /// everything a short listing would have.
        /// </remarks>
        void SetListingClock(string sourceUrl, bool full, DateTimeOffset when);

        /// <summary>Makes a full listing due again.</summary>
        /// <param name="sourceUrl">The source.</param>
        /// <remarks>
        /// The single response to a configuration change: the full listing becomes due, and since
        /// every listed source goes on to a checkup, republication follows.
        /// </remarks>
        void ClearFullListedAt(string sourceUrl);

        /// <summary>Records the entry count the platform reported, where it reports one.</summary>
        /// <param name="sourceUrl">The source.</param>
        /// <param name="count">The count, or null when the platform gave none.</param>
        /// <remarks>Null must stay null and never become zero: zero means an emptied source.</remarks>
        void SetListingCount(string sourceUrl, int? count);

        /// <summary>Records the configuration fingerprint a successful checkup used.</summary>
        /// <param name="sourceUrl">The source.</param>
        /// <param name="fingerprint">The fingerprint.</param>
        void SetConfigFingerprint(string sourceUrl, string? fingerprint);

        /// <summary>Records the Jellyfin folder this source published into.</summary>
        /// <param name="sourceUrl">The source.</param>
        /// <param name="anchorId">The folder id, or null.</param>
        void SetPublishedAnchorId(string sourceUrl, Guid? anchorId);

        /// <summary>Records the remote image URL last posted on a video.</summary>
        /// <param name="videoId">The video uuid.</param>
        /// <param name="url">The URL that was posted, or null.</param>
        /// <remarks>
        /// Jellyfin localises an image on first display and forgets where it came from, so this is
        /// the only way a later pass can tell the upstream artwork changed.
        /// </remarks>
        void SetPublishedImageUrl(string videoId, string? url);

        /// <summary>Same, for a source's series image.</summary>
        /// <param name="sourceUrl">The source URL.</param>
        /// <param name="url">The URL that was posted, or null.</param>
        void SetSourcePublishedImageUrl(string sourceUrl, string? url);

        /// <summary>Same, for the wide Thumb image.</summary>
        /// <param name="sourceUrl">The source URL.</param>
        /// <param name="url">The URL that was posted, or null.</param>
        void SetSourcePublishedBannerUrl(string sourceUrl, string? url);

        /// <summary>Records how a video's metadata fetch ended.</summary>
        /// <param name="videoId">The video uuid.</param>
        /// <param name="outcome">How it ended.</param>
        /// <param name="detail">Why, in plain text; truncated on write.</param>
        /// <param name="when">The time of the attempt.</param>
        /// <param name="countFailure">
        /// Whether a failure counts against this video. False for a failure the PLATFORM caused — a
        /// rate-limit window — because counting it would push every video of a source a step
        /// further out for something none of them did.
        /// </param>
        void SetMetadataOutcome(
            string videoId, CallOutcome outcome, string? detail, DateTimeOffset when, bool countFailure);

        /// <summary>Records how a video's stream description ended.</summary>
        /// <param name="videoId">The video uuid.</param>
        /// <param name="outcome">How it ended.</param>
        /// <param name="detail">Why, in plain text; truncated on write.</param>
        /// <param name="when">The time of the attempt.</param>
        /// <remarks>
        /// A separate clock from the metadata one, so a failing ffprobe does not make the pass spend
        /// another yt-dlp call.
        /// </remarks>
        void SetDescribeOutcome(string videoId, CallOutcome outcome, string? detail, DateTimeOffset when);

        /// <summary>Makes a video due for description again.</summary>
        /// <param name="videoId">The video uuid.</param>
        /// <remarks>
        /// Called by the checkup for an item it has just created without a description in hand.
        /// Descriptions no longer survive the pass that made them, so without this an item recreated
        /// between two passes would stay undescribed until the freshness clock expired — and
        /// Jellyfin decides an undescribed tier's codec blind, which means transcoding.
        /// </remarks>
        void ClearDescribedAt(string videoId);

        /// <summary>Marks a video's metadata as stale so the wave refetches it.</summary>
        /// <param name="videoId">The video uuid.</param>
        /// <remarks>
        /// Sets <c>metadata_at</c> to <see cref="System.DateTimeOffset.UnixEpoch"/> — a non-null value
        /// that is older than any freshness window, making the video due for a metadata fetch on the
        /// next wave.
        ///
        /// <para>
        /// <strong>Do NOT set <c>metadata_at</c> to NULL.</strong>
        /// <see cref="Services.Publication.Publishability"/> requires <c>MetadataAt</c> non-null to
        /// consider a video publishable. Nulling it would cause the checkup to un-publish the video at
        /// the same turn — destroying its Jellyfin item and its watch state — for the sake of a title
        /// change. <c>UnixEpoch</c> is older than any configured freshness window and therefore makes
        /// the video due for a refetch, while keeping it published until the wave has a fresh answer.
        /// </para>
        /// </remarks>
        void MarkMetadataStale(string videoId);

        /// <summary>How many videos carry each metadata outcome.</summary>
        /// <returns>The counts, keyed by outcome; an outcome with no rows is absent.</returns>
        /// <remarks>
        /// Feeds the authentication warning: one single video in
        /// <see cref="CallOutcome.NeedsAuth"/>, on an instance that has a cookie file configured,
        /// means the session has expired. That is the whole detector — no dedicated probe.
        /// </remarks>
        IReadOnlyDictionary<CallOutcome, int> GetOutcomeCounts();

        /// <summary>Clears a video's clocks and counters, making it eligible at once.</summary>
        /// <param name="videoId">The video uuid.</param>
        /// <remarks>
        /// The manual way out when the external cause is known to be fixed — re-exported cookies,
        /// typically. It never lifts the platform backoff the throttle holds: working around that
        /// would restart the rate limiting.
        /// </remarks>
        void ResetVideo(string videoId);

        /// <summary>Clears a source's clocks and stage counters, and every one of its videos.</summary>
        /// <param name="sourceUrl">The source URL.</param>
        /// <remarks>
        /// The stage counters go too: without them the source would still carry the failure count
        /// that made it look broken while its videos were already eligible again.
        /// </remarks>
        void ResetSource(string sourceUrl);

        /// <summary>
        /// Returns the videos of a source that need work, up to <paramref name="limit"/> rows,
        /// ordered by listing position ascending (0 = newest), with null positions last, then
        /// by id as a deterministic tiebreaker.
        /// </summary>
        /// <param name="sourceUrl">The source to scan.</param>
        /// <param name="now">The reference time for all clock comparisons.</param>
        /// <param name="retryMinutes">
        /// Base retry interval, doubled on each consecutive failure. Zero or less means no backoff.
        /// </param>
        /// <param name="metadataMaxAgeDays">
        /// How old a successful metadata fetch may be before it is considered stale.
        /// </param>
        /// <param name="retentionDays">
        /// How far back to consider videos for description. Zero or less means unlimited and every
        /// video passes, matching the convention throughout this project.
        /// </param>
        /// <param name="limit">
        /// Maximum rows to return. Pass the wave size for a normal selection, or <c>1</c> to ask
        /// whether the source still has any work at all (the pass's loop stop test). One query
        /// serves both uses: a second "has work" predicate that could diverge from this selection
        /// would risk reporting work for a video this query never surfaces, making the pass loop
        /// forever.
        /// </param>
        /// <returns>The due rows.</returns>
        /// <remarks>
        /// A video is due if any of three conditions holds:
        /// <list type="number">
        /// <item><description>
        /// <c>metadata_at</c> is null — never fetched.
        /// </description></item>
        /// <item><description>
        /// <c>metadata_at</c> is older than <paramref name="metadataMaxAgeDays"/>.
        /// </description></item>
        /// <item><description>
        /// <c>described_at</c> is null AND the video is <c>NotLive</c> AND it is inside retention.
        /// Live and upcoming videos are excluded HERE, not in the execution step: selecting one
        /// only to skip it would burn a yt-dlp call on every wave — the exact loop this rework was
        /// built to prevent.
        /// </description></item>
        /// </list>
        /// A video whose backoff has not yet elapsed is excluded regardless of the above.
        /// </remarks>
        IReadOnlyList<VideoRecord> GetDueVideos(
            string sourceUrl,
            DateTimeOffset now,
            int retryMinutes,
            int metadataMaxAgeDays,
            int retentionDays,
            int limit);

        /// <summary>Deletes a video.</summary>
        /// <param name="uuid">The video uuid.</param>
        void DeleteVideo(string uuid);

        /// <summary>Deletes a source and every video belonging to it.</summary>
        /// <param name="url">The source URL.</param>
        /// <remarks>
        /// Called after the library has been purged, never before: the row carries
        /// <see cref="SourceRecord.PublishedAnchorId"/>, which is the only way to find this
        /// source's items once its library has left the configuration.
        /// </remarks>
        void DeleteSource(string url);
    }
}

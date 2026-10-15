using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Data
{
    /// <summary>Provides access to the VOD SQLite database.</summary>
    public interface IVodDatabase : IDisposable
    {
        /// <summary>Runs migrations and ensures the schema is up to date.</summary>
        void EnsureCreated();

        /// <summary>Inserts or updates a source record.</summary>
        /// <param name="source">The source to upsert.</param>
        /// <remarks>
        /// <see cref="SourceRecord.LastResolvedAt"/> is written on insert only; on an existing row
        /// it is left to <see cref="SetLastResolvedAt"/> and <see cref="ClearLastResolvedAt"/>.
        /// </remarks>
        void UpsertSource(SourceRecord source);

        /// <summary>Gets a source by its URL.</summary>
        /// <param name="url">The source URL (primary key).</param>
        /// <returns>The matching <see cref="SourceRecord"/>, or <see langword="null"/> if not found.</returns>
        SourceRecord? GetSource(string url);

        /// <summary>Gets all sources, excluding the internal debug row.</summary>
        /// <returns>A read-only list of all <see cref="SourceRecord"/> entries.</returns>
        IReadOnlyList<SourceRecord> GetAllSources();

        /// <summary>Per-source resolution counts, the settled flag and the latest failure.</summary>
        /// <returns>One summary per source that has rows, keyed by source URL, case-insensitively.</returns>
        IReadOnlyDictionary<string, ResolutionSummary> GetResolutionSummaries();

        /// <summary>Inserts or updates a video record.</summary>
        /// <param name="video">The video to upsert.</param>
        void UpsertVideo(VideoRecord video);

        /// <summary>Gets a video by its UUID.</summary>
        /// <param name="uuid">The video UUID (primary key).</param>
        /// <returns>The matching <see cref="VideoRecord"/>, or <see langword="null"/> if not found.</returns>
        VideoRecord? GetVideoByUuid(string uuid);

        /// <summary>Gets all videos belonging to a source.</summary>
        /// <param name="sourceUrl">The parent source URL.</param>
        /// <returns>A read-only list of matching <see cref="VideoRecord"/> entries.</returns>
        IReadOnlyList<VideoRecord> GetVideosBySource(string sourceUrl);

        /// <summary>Every row of a source that may be published — those that are resolved.</summary>
        /// <param name="sourceUrl">The source's URL.</param>
        /// <returns>The resolved rows, oldest publication date first.</returns>
        /// <remarks>
        /// A pending row has no date, no season and no episode number, and an unavailable one is
        /// exactly what must leave the library. Pairs with <see cref="GetPublishableVideoIds"/>:
        /// the publisher needs both the rows it should hold and the ids it must not reap, and the
        /// two have to agree or an excluded row keeps its item for ever.
        /// </remarks>
        IReadOnlyList<VideoRecord> GetPublishableVideosBySource(string sourceUrl);

        /// <summary>Records each video's rank in the listing that was just read.</summary>
        /// <param name="sourceUrl">The source the listing belongs to.</param>
        /// <param name="positions">Native id to rank, 0 being the newest entry.</param>
        /// <remarks>
        /// The rank belongs to the listing, not to a fetch, so it is written for every row the
        /// listing carries — including the ones a pass does not resolve, which are exactly the
        /// rows a resolver has to order, since a video that has never been resolved has no date.
        /// </remarks>
        void SetListingPositions(string sourceUrl, IReadOnlyDictionary<string, int> positions);

        /// <summary>The ids of every publishable row, across every source.</summary>
        /// <returns>The resolved rows' ids.</returns>
        /// <remarks>
        /// What protects a library item from the orphan sweep. Across all sources on purpose: a
        /// video another source still resolves is not this pass's to delete.
        /// </remarks>
        IReadOnlyList<string> GetPublishableVideoIds();


        /// <summary>
        /// Returns the video for a given source and platform-native ID, or <see langword="null"/> when not found.
        /// Nothing in the plugin calls this: the sync path keys on the video UUID. It is kept as the
        /// only way to reach a row from the identifiers a platform hands out, which is what a
        /// migration or a diagnostic endpoint would need.
        /// </summary>
        VideoRecord? GetVideoByNativeId(string sourceUrl, string nativeId);

        /// <summary>
        /// Records the remote image URL last handed to Jellyfin for a video. Jellyfin localises the
        /// image on first display and forgets where it came from, so this is the only way a later
        /// sync can tell that the upstream artwork changed.
        /// </summary>
        /// <param name="videoId">The video UUID.</param>
        /// <param name="url">The remote image URL that was published, or <see langword="null"/>.</param>
        void SetPublishedImageUrl(string videoId, string? url);

        /// <summary>Same as <see cref="SetPublishedImageUrl"/>, for a source's series image.</summary>
        /// <param name="sourceUrl">The source URL (primary key).</param>
        /// <param name="url">The remote image URL that was published, or <see langword="null"/>.</param>
        void SetSourcePublishedImageUrl(string sourceUrl, string? url);

        /// <summary>Same as <see cref="SetSourcePublishedImageUrl"/>, for the wide Thumb image.</summary>
        /// <param name="sourceUrl">The source URL (primary key).</param>
        /// <param name="url">The remote banner URL that was published, or <see langword="null"/>.</param>
        void SetSourcePublishedBannerUrl(string sourceUrl, string? url);

        /// <summary>Deletes a video by its UUID.</summary>
        /// <param name="uuid">The video UUID to delete.</param>
        void DeleteVideo(string uuid);

        /// <summary>
        /// Deletes a source and every video belonging to it, for a source the configuration no
        /// longer lists.
        /// </summary>
        /// <param name="url">The source URL (primary key).</param>
        void DeleteSource(string url);

        /// <summary>Returns the UUIDs of every video the plugin knows, across all sources.</summary>
        IReadOnlyList<string> GetAllVideoIds();

        /// <summary>
        /// Returns the distinct extractor keys (platforms) that have at least one video due for
        /// resolution right now, so the resolver knows how many per-platform waves to launch.
        /// </summary>
        /// <param name="now">The point in time to evaluate "due now" against.</param>
        /// <param name="retryMinutes">Base retry window in minutes; the backoff doubles this.</param>
        /// <returns>
        /// A read-only list of distinct <c>extractor_key</c> values (or the empty string for rows
        /// that predate extractor keys), one per platform that has work.
        /// </returns>
        IReadOnlyList<string> GetPlatformsWithDueVideos(DateTimeOffset now, int retryMinutes);

        /// <summary>
        /// Picks the source the resolver should serve next for the given platform, preferring the
        /// source with the fewest due videos (§6.2 selection), with the source URL as a tiebreaker.
        /// </summary>
        /// <param name="ieKey">
        /// The extractor key identifying the platform lane (use the empty string for rows that
        /// predate extractor keys).
        /// </param>
        /// <param name="now">The point in time to evaluate "due now" against.</param>
        /// <param name="retryMinutes">Base retry window in minutes; the backoff doubles this.</param>
        /// <param name="excluded">
        /// Source URLs to skip unconditionally; when non-empty they are excluded from both arms of
        /// the UNION before the ORDER BY, so the query never returns them.  Pass the already-skipped
        /// set to prevent a stamped-but-still-ranked source from re-entering the loop (N2 fix).
        /// </param>
        /// <returns>The source URL to resolve next, or <see langword="null"/> when no work remains.</returns>
        string? SelectSourceToResolve(string ieKey, DateTimeOffset now, int retryMinutes, IReadOnlySet<string>? excluded = null);

        /// <summary>
        /// Returns the due videos for a source, ordered newest first by listing position, with
        /// rows that have no position sorted last, up to <paramref name="limit"/> entries.
        /// </summary>
        /// <param name="sourceUrl">The source whose videos to query.</param>
        /// <param name="now">The point in time to evaluate "due now" against.</param>
        /// <param name="retryMinutes">Base retry window in minutes; the backoff doubles this.</param>
        /// <param name="limit">Maximum number of rows to return (the wave size).</param>
        /// <returns>
        /// A read-only list of due <see cref="VideoRecord"/> entries, at most <paramref name="limit"/>
        /// long, ordered by <c>listing_position</c> ascending (0 = newest), <c>NULL</c> last, then
        /// by <c>id</c> as a deterministic tiebreaker.
        /// </returns>
        IReadOnlyList<VideoRecord> GetDueVideos(string sourceUrl, DateTimeOffset now, int retryMinutes, int limit);

        /// <summary>
        /// Writes the timestamp at which the resolver finished a wave for a source, so the next
        /// run can tell how recently each source was served.
        /// </summary>
        /// <param name="sourceUrl">The source URL (primary key).</param>
        /// <param name="when">The time the wave completed.</param>
        void SetLastResolvedAt(string sourceUrl, DateTimeOffset when);

        /// <summary>
        /// Clears <c>last_resolved_at</c> for a source, making it a candidate for the resolution
        /// task's "never-served" arm on the next pass.
        /// </summary>
        /// <param name="sourceUrl">The source URL (primary key).</param>
        /// <remarks>
        /// Used when the sync's listing verdict changes one or more rows of a source (a video
        /// transitioned to <c>Unavailable</c> or back to <c>Pending</c>): the changed state must
        /// be published, but a drained source has no due rows, so without this nudge the resolver
        /// would never select it again and the retirement would never reach the library.
        /// Clearing <c>last_resolved_at</c> re-enters the source in the never-served arm of
        /// <c>SelectSourceToResolve</c>, which gives it exactly one free wave: resolve nothing,
        /// publish (now with the updated rows), stamp — done.
        /// </remarks>
        void ClearLastResolvedAt(string sourceUrl);

        /// <summary>
        /// Bulk-updates <c>synced_at</c> for videos belonging to <paramref name="sourceUrl"/>
        /// whose <c>native_id</c> is in <paramref name="nativeIds"/>, marking them as seen
        /// in the current sync without re-fetching their full metadata.
        /// </summary>
        void TouchVideos(string sourceUrl, IReadOnlyCollection<string> nativeIds, DateTimeOffset syncedAt);

        /// <summary>
        /// Returns UUIDs of videos belonging to <paramref name="sourceUrl"/> that were last synced
        /// before <paramref name="syncedBefore"/> (i.e. disappeared from the source).
        /// </summary>
        /// <remarks>
        /// A video that is broadcasting or scheduled is spared while it is fresh: the probe publishes
        /// ids the sync's listing does not carry, so a sweep on <c>synced_at</c> alone would delete a
        /// live row, and its library item, in the middle of the broadcast. The exemption is bounded
        /// by age, because one such row can never return to <c>NotLive</c> — see the implementation
        /// for which, and for the bound.
        /// <para>
        /// Retention is deliberately NOT enforced here. A video still in the listing but older than
        /// <c>RetentionDays</c> must not be deleted: the listing carries no per-video date, so a
        /// deleted row would immediately come back as a <c>Pending</c> row on the next enumeration,
        /// driving a yt-dlp call on the next resolution pass — the exact call loop this design
        /// exists to remove. Retention is a publication filter; see
        /// <see cref="Services.Sync.ResolutionService.Publishable"/>.
        /// </para>
        /// </remarks>
        /// <param name="sourceUrl">The parent source URL.</param>
        /// <param name="syncedBefore">Videos synced strictly before this are considered orphaned.</param>
        /// <returns>A read-only list of orphaned video UUIDs.</returns>
        IReadOnlyList<string> GetOrphanedVideoUuids(string sourceUrl, DateTimeOffset syncedBefore);
    }
}

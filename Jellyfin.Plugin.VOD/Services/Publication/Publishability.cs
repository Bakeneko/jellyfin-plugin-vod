using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>Why a video is not published, or that it is.</summary>
    public enum PublishRefusal
    {
        /// <summary>Nothing refuses it.</summary>
        None = 0,

        /// <summary>Its metadata has never been fetched.</summary>
        NeverFetched,

        /// <summary>
        /// The platform itself refused the video: gone, private, age-restricted, geo-blocked, or
        /// offering no format at all. The detail on the row says which.
        /// </summary>
        PlatformRefused,

        /// <summary>The platform offers no quality tier for it.</summary>
        NoOfferedTier,

        /// <summary>It is older than the source's retention window.</summary>
        OutsideRetention,
    }

    /// <summary>
    /// Whether a video may be published right now, computed from its row rather than stored.
    /// </summary>
    /// <remarks>
    /// This is what replaces the state column. The three conditions are the spec's, and no more:
    /// metadata present, a tier offered, inside retention. Nothing here touches the database —
    /// retention is a publication FILTER, not an erase rule, because deleting a row that is still
    /// in the listing would have it rediscovered and refetched on the next pass, which is the
    /// exact call loop the rework exists to remove.
    /// </remarks>
    public static class Publishability
    {
        /// <summary>Whether this video may be published.</summary>
        /// <param name="video">The row.</param>
        /// <param name="retentionDays">The source's retention in days; zero or less means unlimited.</param>
        /// <param name="now">The reference time for the retention window.</param>
        /// <returns>True when nothing refuses it.</returns>
        public static bool IsPublishable(VideoRecord video, int retentionDays, DateTimeOffset now)
            => Refusal(video, retentionDays, now) == PublishRefusal.None;

        /// <summary>
        /// The ids a reconciliation must not reap, for one source's publication.
        /// </summary>
        /// <param name="allWithMetadata">
        /// Every id the database holds with a successful fetch, across all sources — what
        /// <c>IVodDatabase.GetVideoIdsWithMetadata</c> returns.
        /// </param>
        /// <param name="sourceRows">Every row of the source being published.</param>
        /// <param name="retentionDays">That source's retention in days; zero or less is unlimited.</param>
        /// <param name="now">The reference time.</param>
        /// <returns>The protected ids.</returns>
        /// <remarks>
        /// The global set is deliberately WIDER than publishability: it ignores the retention window
        /// and the offered-tier check, because those are per-source filters that only the owning
        /// source's publication can apply. This is where they get applied.
        /// <para>
        /// Leaving one of this source's rows in the set while refusing to publish it would protect
        /// the very item we have just decided to retract — so retention would never take anything
        /// out of the library. Rows of OTHER sources stay protected, because a video another source
        /// still publishes is not ours to delete.
        /// </para>
        /// <para>
        /// It lives here, beside the rule it derives from, and is shared by both publication paths.
        /// Two places computing it separately is how one of them would quietly stop agreeing with
        /// the other — the defect this project has already paid for in a backoff rule expressed
        /// twice.
        /// </para>
        /// </remarks>
        public static HashSet<string> ProtectedIds(
            IReadOnlyCollection<string> allWithMetadata,
            IReadOnlyList<VideoRecord> sourceRows,
            int retentionDays,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(allWithMetadata);
            ArgumentNullException.ThrowIfNull(sourceRows);

            var protectedIds = new HashSet<string>(allWithMetadata, StringComparer.OrdinalIgnoreCase);

            foreach (var row in sourceRows)
            {
                if (!IsPublishable(row, retentionDays, now))
                    protectedIds.Remove(row.Id);
            }

            return protectedIds;
        }

        /// <summary>Why this video is not published, or <see cref="PublishRefusal.None"/>.</summary>
        /// <param name="video">The row.</param>
        /// <param name="retentionDays">The source's retention in days; zero or less means unlimited.</param>
        /// <param name="now">The reference time for the retention window.</param>
        /// <returns>The first condition that refuses it, in the order the spec lists them.</returns>
        /// <remarks>
        /// The reason is returned rather than discarded so the configuration page can say why a
        /// video is missing from the library. A video that is simply absent, with no reason given,
        /// is the defect this rework was started to repair.
        /// </remarks>
        public static PublishRefusal Refusal(VideoRecord video, int retentionDays, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(video);

            if (video.MetadataAt is null)
                return PublishRefusal.NeverFetched;

            // Only a refusal BY THE PLATFORM unpublishes. A failure to reach it does not.
            //
            // The difference is not cosmetic: unpublishing deletes the Jellyfin item, and the item
            // carries the watch history. A video does not stop existing because our request timed
            // out, and timeouts are expected — the whole RateLimited/Timeout machinery exists
            // because they happen. With the naive rule "any outcome but Ok unpublishes", a single
            // transient failure during the thirty-day refresh destroyed the watch state of a video
            // that was fine. At roughly 250 refetches a day, even a one-percent transient rate
            // costs two or three videos a day.
            //
            // Tolerating the rest is safe because the tier check below still refuses a row that
            // has nothing to publish: a video whose very first fetch failed has no qualities, so it
            // never reaches the library on the strength of this tolerance. What is tolerated is a
            // row that already holds a good answer and whose refresh could not be made.
            //
            // CallOutcome.None is tolerated for the same reason: it is what an outcome written by a
            // newer build reads back as, and refusing an unreadable value would unpublish on an
            // upgrade.
            if (video.MetadataResult is CallOutcome.Unavailable
                or CallOutcome.NeedsAuth
                or CallOutcome.GeoBlocked
                or CallOutcome.NoFormats)
            {
                return PublishRefusal.PlatformRefused;
            }

            if (QualityLadder.OfferedTiers(video.Extras.Qualities).Count == 0)
                return PublishRefusal.NoOfferedTier;

            // Zero means unlimited, not "nothing passes": a source left at the default must publish
            // its whole history.
            if (retentionDays > 0
                && video.PublishedAt is { } published
                && published < now.AddDays(-retentionDays))
            {
                return PublishRefusal.OutsideRetention;
            }

            return PublishRefusal.None;
        }
    }
}

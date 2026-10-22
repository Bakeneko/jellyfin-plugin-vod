using System;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// The title to use when publishing a video, and the detector for a retitle event.
    /// </summary>
    public static class PublishedTitle
    {
        /// <summary>
        /// Returns the title to publish for a video.
        /// </summary>
        /// <param name="listingTitle">The title the flat listing returned, if any.</param>
        /// <param name="metadata">The video's full metadata, if it has been fetched.</param>
        /// <param name="nativeId">The platform's own id; used as last resort.</param>
        /// <returns>A non-null, trimmed, non-empty title.</returns>
        /// <remarks>
        /// Order of preference:
        /// <list type="number">
        /// <item>
        /// <paramref name="listingTitle"/> when present and not blank — the localized title,
        /// honoring <c>youtube:lang</c>.
        /// </item>
        /// <item>
        /// <see cref="PlatformRules.TitleFor"/> when present and not blank — the original title
        /// from the metadata call. Arte sends no title in its flat listing (measured 2026-08-19:
        /// every entry carries only <c>ie_key</c>, <c>url</c> and two other fields), so without
        /// this fallback every Arte video would be published under its native id. The two Twitch
        /// exceptions live in <see cref="PlatformRules.TitleFor"/> and must not be duplicated here.
        /// </item>
        /// <item>
        /// <paramref name="nativeId"/> — an unavoidable last resort for a dead YouTube playlist
        /// entry that has an id but neither a listing title nor any fetchable metadata.
        /// </item>
        /// </list>
        /// The listing-first order is safe for Twitch: the Twitch flat mode does not carry
        /// <c>live_status</c>, so Twitch videos always arrive here without a listing title and
        /// fallback (2) with its platform rules applies.
        /// </remarks>
        public static string For(string? listingTitle, VideoMetadata? metadata, string nativeId)
        {
            if (!string.IsNullOrWhiteSpace(listingTitle))
                return listingTitle.Trim();

            if (metadata is not null)
            {
                var metaTitle = PlatformRules.TitleFor(metadata);
                if (!string.IsNullOrWhiteSpace(metaTitle))
                    return metaTitle.Trim();
            }

            return nativeId.Trim();
        }

        /// <summary>
        /// Returns <see langword="true"/> when a fresh listing title reveals that the video has
        /// been retitled since the last pass.
        /// </summary>
        /// <param name="storedListingTitle">The listing title stored from the previous pass.</param>
        /// <param name="freshListingTitle">The listing title returned by the current pass.</param>
        /// <returns>Whether a retitle event should be acted upon.</returns>
        /// <remarks>
        /// This is a listing-to-listing comparison, which makes it free: no extra call is needed
        /// because the listing already has the answer.
        ///
        /// Do NOT replace <paramref name="storedListingTitle"/> with
        /// <see cref="Data.Models.VideoRecord.Title"/> (the published title). A listing serves the
        /// localized title and obeys <c>youtube:lang</c>; a metadata call always serves the original
        /// and ignores it. Comparing against the published title would mark every video of a source
        /// with a configured preferred language as changed on every pass.
        ///
        /// Absence is not a change: when the fresh listing carries no title (Arte never does), this
        /// method returns <see langword="false"/>. Without this guard every Arte pass would declare
        /// all ~300 Arte videos retitled and refetch them all, forever.
        ///
        /// The comparison is ordinal (<see cref="StringComparison.Ordinal"/>): a case-only change
        /// IS a retitle, because it changes the title that will be published.
        /// </remarks>
        public static bool HasBeenRetitled(string? storedListingTitle, string? freshListingTitle)
        {
            // Absence is not a change. Arte sends no listing title at all; if null or blank were
            // treated as authoritative, every Arte pass would declare ~300 videos retitled.
            if (string.IsNullOrWhiteSpace(freshListingTitle))
                return false;

            var fresh = freshListingTitle.Trim();

            // Stored is null: the previous pass had no listing title. We do not know the original,
            // so fetching is the safe answer. It costs one call, after which the stored title is
            // filled and the detector stays silent.
            if (storedListingTitle is null)
                return true;

            return !string.Equals(storedListingTitle.Trim(), fresh, StringComparison.Ordinal);
        }
    }
}

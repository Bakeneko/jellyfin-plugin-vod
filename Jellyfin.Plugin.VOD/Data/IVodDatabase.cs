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
        void UpsertSource(SourceRecord source);

        /// <summary>Gets a source by its URL.</summary>
        /// <param name="url">The source URL (primary key).</param>
        /// <returns>The matching <see cref="SourceRecord"/>, or <see langword="null"/> if not found.</returns>
        SourceRecord? GetSource(string url);

        /// <summary>Gets all sources, excluding the internal debug row.</summary>
        /// <returns>A read-only list of all <see cref="SourceRecord"/> entries.</returns>
        IReadOnlyList<SourceRecord> GetAllSources();

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
        /// Bulk-updates <c>synced_at</c> for videos belonging to <paramref name="sourceUrl"/>
        /// whose <c>native_id</c> is in <paramref name="nativeIds"/>, marking them as seen
        /// in the current sync without re-fetching their full metadata.
        /// </summary>
        void TouchVideos(string sourceUrl, IReadOnlyCollection<string> nativeIds, DateTimeOffset syncedAt);

        /// <summary>
        /// Returns UUIDs of videos belonging to <paramref name="sourceUrl"/> that were last synced
        /// before <paramref name="syncedBefore"/> (i.e. disappeared from the source), or whose
        /// <c>published_at</c> is older than <paramref name="retentionCutoff"/> when provided.
        /// </summary>
        /// <remarks>
        /// A video that is broadcasting or scheduled is spared while it is fresh: the probe publishes
        /// ids the sync's listing does not carry, so a sweep on <c>synced_at</c> alone would delete a
        /// live row, and its library item, in the middle of the broadcast. The exemption is bounded
        /// by age, because one such row can never return to <c>NotLive</c> — see the implementation
        /// for which, and for the bound.
        /// </remarks>
        /// <param name="sourceUrl">The parent source URL.</param>
        /// <param name="syncedBefore">Videos synced strictly before this are considered orphaned.</param>
        /// <param name="retentionCutoff">When set, videos published before this date are also returned.</param>
        /// <returns>A read-only list of orphaned video UUIDs.</returns>
        IReadOnlyList<string> GetOrphanedVideoUuids(string sourceUrl, DateTimeOffset syncedBefore, DateTimeOffset? retentionCutoff = null);
    }
}

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
        /// Used by SyncService to detect updates to existing entries.
        /// </summary>
        VideoRecord? GetVideoByNativeId(string sourceUrl, string nativeId);

        /// <summary>Deletes a video by its UUID.</summary>
        /// <param name="uuid">The video UUID to delete.</param>
        void DeleteVideo(string uuid);

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
        /// <param name="sourceUrl">The parent source URL.</param>
        /// <param name="syncedBefore">Videos synced strictly before this are considered orphaned.</param>
        /// <param name="retentionCutoff">When set, videos published before this date are also returned.</param>
        /// <returns>A read-only list of orphaned video UUIDs.</returns>
        IReadOnlyList<string> GetOrphanedVideoUuids(string sourceUrl, DateTimeOffset syncedBefore, DateTimeOffset? retentionCutoff = null);

        /// <summary>Returns all non-null strm_path values across every video record.</summary>
        /// <returns>A read-only list of absolute file paths.</returns>
        IReadOnlyList<string> GetAllStrmPaths();
    }
}

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

        /// <summary>Deletes a video by its UUID.</summary>
        /// <param name="uuid">The video UUID to delete.</param>
        void DeleteVideo(string uuid);

        /// <summary>Returns UUIDs of videos belonging to <paramref name="sourceUrl"/> that were last synced before <paramref name="syncedBefore"/>.</summary>
        /// <param name="sourceUrl">The parent source URL.</param>
        /// <param name="syncedBefore">Cutoff timestamp; videos synced strictly before this are returned.</param>
        /// <returns>A read-only list of orphaned video UUIDs.</returns>
        IReadOnlyList<string> GetOrphanedVideoUuids(string sourceUrl, DateTimeOffset syncedBefore);
    }
}

using System;
using System.IO;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Data
{
    public sealed class VodDatabaseTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly VodDatabase _db;

        public VodDatabaseTests()
        {
            _dbPath = Path.GetTempFileName();
            _db = new VodDatabase(_dbPath, NullLogger<VodDatabase>.Instance);
            _db.EnsureCreated();
        }

        public void Dispose()
        {
            _db.Dispose();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }

        // --- Sources ---

        [Fact]
        public void GetAllSources_OnEmpty_ReturnsEmpty()
        {
            Assert.Empty(_db.GetAllSources());
        }

        [Fact]
        public void UpsertSource_ThenGetSource_RoundTrips()
        {
            var r = new SourceRecord
            {
                Url = "https://www.youtube.com/@arte",
                Name = "Arte",
                VideoCount = 42,
                LastSyncedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero)
            };
            _db.UpsertSource(r);

            var retrieved = _db.GetSource("https://www.youtube.com/@arte");

            Assert.NotNull(retrieved);
            Assert.Equal("Arte", retrieved.Name);
            Assert.Equal(42, retrieved.VideoCount);
            Assert.Equal(r.LastSyncedAt, retrieved.LastSyncedAt);
        }

        [Fact]
        public void UpsertSource_UpdatesExisting()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.com", Name = "Old", VideoCount = 1 });
            _db.UpsertSource(new SourceRecord { Url = "https://example.com", Name = "New", VideoCount = 5 });

            var r = _db.GetSource("https://example.com");
            Assert.Equal("New", r!.Name);
            Assert.Equal(5, r.VideoCount);
        }

        [Fact]
        public void GetSource_Unknown_ReturnsNull()
        {
            Assert.Null(_db.GetSource("https://unknown.example.com"));
        }

        // --- Videos ---

        [Fact]
        public void UpsertVideo_ThenGetVideoByUuid_RoundTrips()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://www.youtube.com/@arte" });
            var v = new VideoRecord
            {
                Id = "550e8400-e29b-41d4-a716-446655440000",
                SourceUrl = "https://www.youtube.com/@arte",
                VideoUrl = "https://www.youtube.com/watch?v=abc123",
                Title = "Test video",
                SeasonNumber = 2026,
                EpisodeNumber = 1,
                SyncedAt = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero)
            };
            _db.UpsertVideo(v);

            var r = _db.GetVideoByUuid("550e8400-e29b-41d4-a716-446655440000");

            Assert.NotNull(r);
            Assert.Equal("https://www.youtube.com/watch?v=abc123", r.VideoUrl);
            Assert.Equal(2026, r.SeasonNumber);
        }

        [Fact]
        public void GetVideoByUuid_Unknown_ReturnsNull()
        {
            Assert.Null(_db.GetVideoByUuid("00000000-0000-0000-0000-000000000000"));
        }

        [Fact]
        public void GetVideosBySource_ReturnsOnlyMatchingSource()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://a.example.com" });
            _db.UpsertSource(new SourceRecord { Url = "https://b.example.com" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = "https://a.example.com",
                VideoUrl = "https://a.example.com/v/1",
                SyncedAt = DateTimeOffset.UtcNow
            });
            _db.UpsertVideo(new VideoRecord
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = "https://b.example.com",
                VideoUrl = "https://b.example.com/v/2",
                SyncedAt = DateTimeOffset.UtcNow
            });

            var result = _db.GetVideosBySource("https://a.example.com");

            Assert.Single(result);
            Assert.Equal("https://a.example.com/v/1", result[0].VideoUrl);
        }

        [Fact]
        public void GetOrphanedVideoUuids_ReturnsUuidsNotSyncedAfterCutoff()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            var cutoff = DateTimeOffset.UtcNow;

            _db.UpsertVideo(new VideoRecord
            {
                Id = "old-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/old",
                SyncedAt = cutoff.AddHours(-2)
            });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "new-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/new",
                SyncedAt = cutoff.AddHours(1)
            });

            var orphans = _db.GetOrphanedVideoUuids("https://example.com", cutoff);

            Assert.Single(orphans);
            Assert.Equal("old-uuid", orphans[0]);
        }

        [Fact]
        public void TouchVideos_UpdatesSyncedAt()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            var oldTime = DateTimeOffset.UtcNow.AddHours(-5);
            var newTime = DateTimeOffset.UtcNow;

            _db.UpsertVideo(new VideoRecord
            {
                Id = "touch-uuid",
                NativeId = "native-abc",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/abc",
                SyncedAt = oldTime
            });

            _db.TouchVideos("https://example.com", ["native-abc"], newTime);

            // After touch, the video should no longer be considered an orphan
            var orphans = _db.GetOrphanedVideoUuids("https://example.com", newTime.AddSeconds(-1));
            Assert.DoesNotContain("touch-uuid", orphans);
        }

        [Fact]
        public void GetOrphanedVideoUuids_WithRetentionCutoff_ReturnsOldVideos()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            var syncStart = DateTimeOffset.UtcNow;
            var retention = syncStart.AddDays(-30);

            // Recent video — within retention, synced now
            _db.UpsertVideo(new VideoRecord
            {
                Id = "recent-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/recent",
                PublishedAt = syncStart.AddDays(-10),
                SyncedAt = syncStart
            });
            // Old video — beyond retention, synced now (still on source but too old)
            _db.UpsertVideo(new VideoRecord
            {
                Id = "old-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/old",
                PublishedAt = syncStart.AddDays(-60),
                SyncedAt = syncStart
            });

            var orphans = _db.GetOrphanedVideoUuids("https://example.com", syncStart, retention);

            Assert.DoesNotContain("recent-uuid", orphans);
            Assert.Contains("old-uuid", orphans);
        }

        [Fact]
        public void DeleteVideo_RemovesEntry()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "to-delete",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/x",
                SyncedAt = DateTimeOffset.UtcNow
            });

            _db.DeleteVideo("to-delete");

            Assert.Null(_db.GetVideoByUuid("to-delete"));
        }

        [Fact]
        public void UpsertVideo_IsLive_RoundTrips()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://www.youtube.com/@live" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "live-uuid",
                SourceUrl = "https://www.youtube.com/@live",
                VideoUrl = "https://www.youtube.com/watch?v=live123",
                IsLive = true,
                SyncedAt = DateTimeOffset.UtcNow
            });

            var r = _db.GetVideoByUuid("live-uuid");

            Assert.NotNull(r);
            Assert.True(r.IsLive);
        }

        [Fact]
        public void EnsureCreated_IsIdempotent()
        {
            // The constructor already ran it once; a second call must be a no-op, not a re-apply.
            _db.EnsureCreated();

            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "idempotent-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/1",
                IsLive = false,
                SyncedAt = DateTimeOffset.UtcNow
            });

            Assert.NotNull(_db.GetVideoByUuid("idempotent-uuid"));
        }

        [Fact]
        public void EnsureCreated_StampsSchemaVersion()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";

            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }
}

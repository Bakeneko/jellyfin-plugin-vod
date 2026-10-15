using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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

        [Theory]
        [InlineData(LiveStatus.Live)]
        [InlineData(LiveStatus.Upcoming)]
        public void GetOrphanedVideoUuids_ExemptsARowThatIsBroadcastingOrScheduled(LiveStatus status)
        {
            // The probe writes rows the sync's listing does not carry: on a subscriber-only channel
            // it stores the STREAM id, which no /videos entry mentions, and for a live-only source
            // the sync and the probe publish different ids for the same broadcast. TouchVideos only
            // reaches enumerated ids, so the sweep deleted the row — and the reconcile its library
            // item — in the middle of the broadcast.
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            var syncStart = DateTimeOffset.UtcNow;

            _db.UpsertVideo(new VideoRecord
            {
                Id = "live-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/live",
                SyncedAt = syncStart.AddMinutes(-3),
                LiveStatus = status
            });

            Assert.DoesNotContain("live-uuid", _db.GetOrphanedVideoUuids("https://example.com", syncStart));
        }

        [Theory]
        [InlineData(LiveStatus.Live)]
        [InlineData(LiveStatus.Upcoming)]
        public void GetOrphanedVideoUuids_SweepsAStaleLiveRowButNotAFreshlyProbedOne(LiveStatus status)
        {
            // The exemption needs an escape, or one Twitch path leaves a permanent phantom: a
            // /videos source on a subscriber-only channel stores the fallback row under the STREAM
            // id, with the channel root as its URL. When the broadcast ends, yt-dlp stops answering
            // for that URL, so the re-fetch never returns and the row never reaches NotLive; and the
            // stream id is absent from the /videos listing, so neither TouchVideos nor the sync's
            // refetch set ever sees it. Nothing would collect it.
            //
            // Freshness is what tells the two apart: a real broadcast is written on every probe
            // pass, while a stuck row stops being written the moment its re-fetch starts failing.
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            var syncStart = DateTimeOffset.UtcNow;

            _db.UpsertVideo(new VideoRecord
            {
                Id = "fresh-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/fresh",
                SyncedAt = syncStart.AddMinutes(-3),
                LiveStatus = status
            });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "stale-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/stale",
                SyncedAt = syncStart.AddDays(-30),
                LiveStatus = status
            });

            var orphans = _db.GetOrphanedVideoUuids("https://example.com", syncStart);

            Assert.DoesNotContain("fresh-uuid", orphans);
            Assert.Contains("stale-uuid", orphans);
        }

        [Fact]
        public void GetOrphanedVideoUuids_StillSweepsAFinishedBroadcast()
        {
            // The exemption lasts exactly as long as the broadcast: once the video is a normal one,
            // disappearing from the source means the same as for any other video.
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            var syncStart = DateTimeOffset.UtcNow;

            _db.UpsertVideo(new VideoRecord
            {
                Id = "was-live-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/was-live",
                SyncedAt = syncStart.AddMinutes(-3),
                LiveStatus = LiveStatus.NotLive
            });

            Assert.Contains("was-live-uuid", _db.GetOrphanedVideoUuids("https://example.com", syncStart));
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
        public void GetOrphanedVideoUuids_DoesNotPrune_ARowStillInTheListing_EvenWhenBeyondRetention()
        {
            // B-C1: a row whose published_at is far outside the retention window must NOT be deleted
            // when it is still in the listing (synced_at == syncStart). Deleting it would put it back
            // as Pending on the next enumeration and drive a yt-dlp call every resolution pass.
            // Retention is a publication filter (ResolutionService.Publishable), not an erase rule.
            _db.UpsertSource(new SourceRecord { Url = "https://example.com" });
            var syncStart = DateTimeOffset.UtcNow;

            // Still in the listing (synced_at == syncStart) but published two years ago.
            _db.UpsertVideo(new VideoRecord
            {
                Id = "old-but-listed-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/old",
                PublishedAt = syncStart.AddDays(-730),
                SyncedAt = syncStart
            });
            // Left the listing (synced_at is before syncStart) — this one IS an orphan.
            _db.UpsertVideo(new VideoRecord
            {
                Id = "gone-uuid",
                SourceUrl = "https://example.com",
                VideoUrl = "https://example.com/v/gone",
                PublishedAt = syncStart.AddDays(-730),
                SyncedAt = syncStart.AddHours(-2)
            });

            var orphans = _db.GetOrphanedVideoUuids("https://example.com", syncStart);

            Assert.DoesNotContain("old-but-listed-uuid", orphans);
            Assert.Contains("gone-uuid", orphans);
        }

        [Fact]
        public void DeleteSource_RemovesTheSourceAndItsVideos()
        {
            // Retiring a source deletes user data, so the cascade is pinned here rather than left to
            // the foreign key: videos.source_url references sources.url, and deleting the parent row
            // first would fail instead of taking the children with it.
            _db.UpsertSource(new SourceRecord { Url = "https://retired.example" });
            _db.UpsertSource(new SourceRecord { Url = "https://kept.example" });

            foreach (var (id, source) in new[]
            {
                ("gone-1", "https://retired.example"),
                ("gone-2", "https://retired.example"),
                ("stays", "https://kept.example")
            })
            {
                _db.UpsertVideo(new VideoRecord
                {
                    Id = id,
                    SourceUrl = source,
                    VideoUrl = "https://example.com/v/" + id,
                    SyncedAt = DateTimeOffset.UtcNow
                });
            }

            _db.DeleteSource("https://retired.example");

            Assert.Null(_db.GetSource("https://retired.example"));
            Assert.Empty(_db.GetVideosBySource("https://retired.example"));

            // The other source must be untouched: retirement is per source, and an over-broad
            // DELETE here would empty the whole library on the next reconciliation.
            Assert.NotNull(_db.GetSource("https://kept.example"));
            Assert.Single(_db.GetVideosBySource("https://kept.example"));
            Assert.Contains("stays", _db.GetAllVideoIds());
        }

        [Fact]
        public void DeleteSource_IsSilentOnAnUnknownUrl()
            => _db.DeleteSource("https://never-existed.example");

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
                LiveStatus = LiveStatus.Live,
                SyncedAt = DateTimeOffset.UtcNow
            });

            var r = _db.GetVideoByUuid("live-uuid");

            Assert.NotNull(r);
            Assert.Equal(LiveStatus.Live, r.LiveStatus);
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
                SyncedAt = DateTimeOffset.UtcNow
            });

            Assert.NotNull(_db.GetVideoByUuid("idempotent-uuid"));
        }

        // --- Published artwork URLs ---

        [Fact]
        public void UpsertSource_LeavesThePublishedArtworkUrlsAlone()
        {
            // UpsertSource deliberately omits published_image_url and published_banner_url from its
            // INSERT and its ON CONFLICT list. SyncService re-upserts a SourceRecord it read before
            // the publisher wrote those URLs, so "completing" the UPSERT would blank them and make
            // every source image download again on every sync.
            const string url = "https://www.youtube.com/@arte";
            _db.UpsertSource(new SourceRecord { Url = url, Name = "Arte" });
            _db.SetSourcePublishedImageUrl(url, "https://cdn.example.com/arte-poster.jpg");
            _db.SetSourcePublishedBannerUrl(url, "https://cdn.example.com/arte-banner.jpg");

            // The stale in-memory record: it never carried the published URLs.
            _db.UpsertSource(new SourceRecord { Url = url, Name = "Arte", VideoCount = 7 });

            var retrieved = _db.GetSource(url);
            Assert.NotNull(retrieved);
            Assert.Equal("https://cdn.example.com/arte-poster.jpg", retrieved.PublishedImageUrl);
            Assert.Equal("https://cdn.example.com/arte-banner.jpg", retrieved.PublishedBannerUrl);
            Assert.Equal(7, retrieved.VideoCount);
        }

        [Fact]
        public void UpsertVideo_LeavesThePublishedImageUrlAlone()
        {
            // Same contract on the video side: published_image_url is the publisher's column.
            const string uuid = "550e8400-e29b-41d4-a716-4466554400ff";
            _db.UpsertSource(new SourceRecord { Url = "https://www.youtube.com/@arte" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = uuid,
                SourceUrl = "https://www.youtube.com/@arte",
                VideoUrl = "https://www.youtube.com/watch?v=abc123",
                Title = "Before",
                SyncedAt = DateTimeOffset.UtcNow
            });
            _db.SetPublishedImageUrl(uuid, "https://cdn.example.com/still.jpg");

            _db.UpsertVideo(new VideoRecord
            {
                Id = uuid,
                SourceUrl = "https://www.youtube.com/@arte",
                VideoUrl = "https://www.youtube.com/watch?v=abc123",
                Title = "After",
                SyncedAt = DateTimeOffset.UtcNow
            });

            var retrieved = _db.GetVideoByUuid(uuid);
            Assert.NotNull(retrieved);
            Assert.Equal("https://cdn.example.com/still.jpg", retrieved.PublishedImageUrl);
            Assert.Equal("After", retrieved.Title);
        }

        [Fact]
        public void UpsertVideo_RoundTripsLiveStatus()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.com/s" });

            _db.UpsertVideo(new VideoRecord
            {
                Id = "11111111-1111-1111-1111-111111111111",
                SourceUrl = "https://example.com/s",
                VideoUrl = "https://example.com/v",
                SyncedAt = DateTimeOffset.UtcNow,
                LiveStatus = LiveStatus.Upcoming
            });

            var read = _db.GetVideoByUuid("11111111-1111-1111-1111-111111111111");
            Assert.NotNull(read);
            Assert.Equal(LiveStatus.Upcoming, read.LiveStatus);
        }

        [Fact]
        public void GetVideoByUuid_OnUnknownLiveStatusValue_ReadsBackAsNotLive()
        {
            // Enum.TryParse accepts numeric strings, so a hand-edited '7' would parse to (LiveStatus)7
            // without the IsDefined guard — an out-of-defined-range value that is neither NotLive,
            // Live nor Upcoming and misleads MembersFor and BuildVideoOverview.
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/src2" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "22222222-2222-2222-2222-222222222222",
                SourceUrl = "https://example.test/src2",
                VideoUrl = "https://example.test/v2",
                SyncedAt = DateTimeOffset.UtcNow
            });

            // Directly corrupt the column with a numeric value outside the enum's range.
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE videos SET live_status = '7' WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", "22222222-2222-2222-2222-222222222222");
                cmd.ExecuteNonQuery();
            }

            var corrupted = _db.GetVideoByUuid("22222222-2222-2222-2222-222222222222");
            Assert.NotNull(corrupted);
            Assert.Equal(LiveStatus.NotLive, corrupted.LiveStatus);
        }

        /// <summary>
        /// Migration v7 on a v6 database that holds what a pre-fix sync wrote: two rows for one
        /// native id. The oldest row survives, the index becomes UNIQUE, and a third row for the
        /// same pair is then refused instead of silently stored.
        /// </summary>
        [Fact]
        public void EnsureCreated_MigrationV7_KeepsOneRowPerNativeIdAndRefusesNewDuplicates()
        {
            var path = Path.GetTempFileName();
            try
            {
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (
                            url         TEXT PRIMARY KEY,
                            name        TEXT,
                            video_count INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE TABLE videos (
                            id               TEXT PRIMARY KEY,
                            source_url       TEXT NOT NULL REFERENCES sources(url),
                            video_url        TEXT NOT NULL,
                            synced_at        TEXT NOT NULL,
                            native_id        TEXT,
                            extras           TEXT,
                            first_seen_at    TEXT,
                            live_status      TEXT NOT NULL DEFAULT 'NotLive',
                            extractor_key TEXT
                        );
                        CREATE INDEX idx_videos_native_id ON videos(source_url, native_id);

                        INSERT INTO sources (url) VALUES ('https://example.test/a');
                        INSERT INTO sources (url) VALUES ('https://example.test/b');

                        -- Two rows for one (source, native id), same first_seen_at: the lower
                        -- rowid wins. Three rows for another, with distinct dates: the oldest wins.
                        INSERT INTO videos (id, source_url, video_url, synced_at, native_id, first_seen_at)
                        VALUES ('keep-tie', 'https://example.test/a', 'https://example.test/v/dup',
                                '2026-10-12T00:00:00+00:00', 'dup', '2026-10-11T15:58:02.8248783+00:00'),
                               ('drop-tie', 'https://example.test/a', 'https://example.test/v/dup',
                                '2026-10-12T00:00:00+00:00', 'dup', '2026-10-11T15:58:02.8248783+00:00'),
                               ('drop-new', 'https://example.test/a', 'https://example.test/v/tri',
                                '2026-10-12T00:00:00+00:00', 'tri', '2026-10-09T00:00:00+00:00'),
                               ('keep-old', 'https://example.test/a', 'https://example.test/v/tri',
                                '2026-10-12T00:00:00+00:00', 'tri', '2026-10-01T00:00:00+00:00'),
                               ('drop-null', 'https://example.test/a', 'https://example.test/v/tri',
                                '2026-10-12T00:00:00+00:00', 'tri', NULL),
                               -- The same video in a second source: a different pair, both survive.
                               ('other-source', 'https://example.test/b', 'https://example.test/v/dup',
                                '2026-10-12T00:00:00+00:00', 'dup', '2026-10-11T00:00:00+00:00'),
                               -- Rows without a native id are not duplicates of each other.
                               ('null-id-1', 'https://example.test/a', 'https://example.test/v/n1',
                                '2026-10-12T00:00:00+00:00', NULL, '2026-10-01T00:00:00+00:00'),
                               ('null-id-2', 'https://example.test/a', 'https://example.test/v/n2',
                                '2026-10-12T00:00:00+00:00', NULL, '2026-10-02T00:00:00+00:00');

                        PRAGMA user_version = 6;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var upgraded = new VodDatabase(path, NullLogger<VodDatabase>.Instance);
                upgraded.EnsureCreated();

                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();

                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(8L, (long)version.ExecuteScalar()!);

                using var survivors = conn.CreateCommand();
                survivors.CommandText = "SELECT id FROM videos ORDER BY id";
                var ids = new List<string>();
                using (var reader = survivors.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        ids.Add(reader.GetString(0));
                    }
                }

                Assert.Equal(
                    ["keep-old", "keep-tie", "null-id-1", "null-id-2", "other-source"],
                    ids);

                // The tie between keep-tie and drop-tie is broken by `rowid ASC`, and the
                // survivor above proves the ORDER BY's first key: drop-tie sorts before
                // keep-tie alphabetically, so the survivor is not merely the first by id.
                // The rowid key itself is NOT pinned here and cannot be: SQLite resolves a
                // ROW_NUMBER() tie in scan order, which agrees with rowid, so the assertion
                // would hold with the clause removed. It stays in the migration as an explicit
                // determinism guarantee, verified by construction rather than by this test.

                using var unique = conn.CreateCommand();
                unique.CommandText =
                    "SELECT \"unique\" FROM pragma_index_list('videos') WHERE name = 'idx_videos_native_id'";
                Assert.Equal(1L, (long)unique.ExecuteScalar()!);

                using var insert = conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO videos (id, source_url, video_url, synced_at, native_id)
                    VALUES ('late-duplicate', 'https://example.test/a', 'https://example.test/v/dup',
                            '2026-10-13T00:00:00+00:00', 'dup');
                    """;
                var ex = Record.Exception(() => insert.ExecuteNonQuery());

                Assert.IsType<SqliteException>(ex);
                Assert.Contains("UNIQUE constraint failed", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// A destructive migration on a published plugin has to say what it destroyed: without a
        /// count in the log, a user upgrading cannot tell afterwards whether rows were dropped,
        /// nor how many.
        /// </summary>
        [Fact]
        public void EnsureCreated_MigrationV7_LogsHowManyDuplicateRowsItDropped()
        {
            var path = Path.GetTempFileName();
            var logger = new RecordingLogger<VodDatabase>();
            try
            {
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (
                            url         TEXT PRIMARY KEY,
                            name        TEXT,
                            video_count INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE TABLE videos (
                            id            TEXT PRIMARY KEY,
                            source_url    TEXT NOT NULL REFERENCES sources(url),
                            video_url     TEXT NOT NULL,
                            synced_at     TEXT NOT NULL,
                            native_id     TEXT,
                            extras        TEXT,
                            first_seen_at TEXT,
                            live_status   TEXT NOT NULL DEFAULT 'NotLive',
                            extractor_key TEXT
                        );
                        INSERT INTO sources (url) VALUES ('https://example.test/a');
                        INSERT INTO videos (id, source_url, video_url, synced_at, native_id, first_seen_at)
                        VALUES ('keep', 'https://example.test/a', 'https://example.test/v/dup',
                                '2026-10-12T00:00:00+00:00', 'dup', '2026-10-01T00:00:00+00:00'),
                               ('drop', 'https://example.test/a', 'https://example.test/v/dup',
                                '2026-10-12T00:00:00+00:00', 'dup', '2026-10-02T00:00:00+00:00');
                        PRAGMA user_version = 6;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var upgraded = new VodDatabase(path, logger);
                upgraded.EnsureCreated();

                Assert.Contains(
                    logger.Messages,
                    m => m.Contains("deleted 1 duplicate", StringComparison.Ordinal));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// A migration step that fails after the deletion: the rows and the version stamp come
        /// back, and the log says so. The deletion line is not retracted — it reports what the
        /// statement did — so the rollback line is what tells a reader it did not stick.
        /// </summary>
        [Fact]
        public void EnsureCreated_LogsTheRollback_WhenAMigrationStepFailsAfterDeletingRows()
        {
            var path = Path.GetTempFileName();
            var logger = new RecordingLogger<VodDatabase>();
            try
            {
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (
                            url         TEXT PRIMARY KEY,
                            name        TEXT,
                            video_count INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE TABLE videos (
                            id            TEXT PRIMARY KEY,
                            source_url    TEXT NOT NULL REFERENCES sources(url),
                            video_url     TEXT NOT NULL,
                            synced_at     TEXT NOT NULL,
                            native_id     TEXT,
                            extras        TEXT,
                            first_seen_at TEXT,
                            live_status   TEXT NOT NULL DEFAULT 'NotLive',
                            extractor_key TEXT
                        );

                        -- A table holding the name migration v7 gives its index. DROP INDEX IF
                        -- EXISTS tolerates it, CREATE UNIQUE INDEX then fails — so the step fails
                        -- after its DELETE has already run.
                        CREATE TABLE idx_videos_native_id (x);

                        INSERT INTO sources (url) VALUES ('https://example.test/a');
                        INSERT INTO videos (id, source_url, video_url, synced_at, native_id, first_seen_at)
                        VALUES ('keep', 'https://example.test/a', 'https://example.test/v/dup',
                                '2026-10-12T00:00:00+00:00', 'dup', '2026-10-01T00:00:00+00:00'),
                               ('drop', 'https://example.test/a', 'https://example.test/v/dup',
                                '2026-10-12T00:00:00+00:00', 'dup', '2026-10-02T00:00:00+00:00');
                        PRAGMA user_version = 6;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var upgraded = new VodDatabase(path, logger);

                Assert.Throws<SqliteException>(upgraded.EnsureCreated);

                Assert.Contains(
                    logger.Messages,
                    m => m.Contains("rolled back", StringComparison.Ordinal));
                Assert.DoesNotContain(
                    logger.Messages,
                    m => m.Contains("migrated from schema", StringComparison.Ordinal));

                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();

                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(6L, (long)version.ExecuteScalar()!);

                using var rows = conn.CreateCommand();
                rows.CommandText = "SELECT COUNT(*) FROM videos";
                Assert.Equal(2L, (long)rows.ExecuteScalar()!);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// A migration that deletes rows is expected to report how many, and the only way to read
        /// that back in a test is to capture what the logger was handed.
        /// </summary>
        private sealed class RecordingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = [];

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                Messages.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new();

                public void Dispose()
                {
                }
            }
        }

        /// <summary>
        /// Migration v8 on a v7 database: a row that already carries metadata has been paid for and
        /// is resolved; a row with none has never been resolved and is pending, for the resolver to
        /// take. Nothing is re-downloaded and nothing is lost.
        /// </summary>
        [Fact]
        public void EnsureCreated_MigrationV8_MarksRowsThatAlreadyHaveMetadataAsResolved()
        {
            var path = Path.GetTempFileName();
            try
            {
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (
                            url         TEXT PRIMARY KEY,
                            name        TEXT,
                            video_count INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE TABLE videos (
                            id            TEXT PRIMARY KEY,
                            source_url    TEXT NOT NULL REFERENCES sources(url),
                            video_url     TEXT NOT NULL,
                            synced_at     TEXT NOT NULL,
                            native_id     TEXT,
                            extractor_key TEXT,
                            extras        TEXT,
                            first_seen_at TEXT,
                            live_status   TEXT NOT NULL DEFAULT 'NotLive'
                        );
                        CREATE UNIQUE INDEX idx_videos_native_id ON videos(source_url, native_id);

                        INSERT INTO sources (url) VALUES ('https://example.test/a');
                        INSERT INTO videos (id, source_url, video_url, synced_at, native_id, extras)
                        VALUES ('has-metadata', 'https://example.test/a', 'https://example.test/v/1',
                                '2026-10-12T00:00:00+00:00', 'one', '{"Version":1}'),
                               ('empty-extras', 'https://example.test/a', 'https://example.test/v/2',
                                '2026-10-12T00:00:00+00:00', 'two', ''),
                               ('no-extras', 'https://example.test/a', 'https://example.test/v/3',
                                '2026-10-12T00:00:00+00:00', 'three', NULL);
                        PRAGMA user_version = 7;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var upgraded = new VodDatabase(path, NullLogger<VodDatabase>.Instance);
                upgraded.EnsureCreated();

                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();
                using var states = conn.CreateCommand();
                states.CommandText = "SELECT id, resolution_state FROM videos ORDER BY id";
                var byId = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var reader = states.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        byId[reader.GetString(0)] = reader.GetString(1);
                    }
                }

                Assert.Equal("Resolved", byId["has-metadata"]);
                Assert.Equal("Pending", byId["empty-extras"]);
                Assert.Equal("Pending", byId["no-extras"]);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void UpsertVideo_RoundTripsTheResolutionColumns()
        {
            var attempted = new DateTimeOffset(2026, 10, 12, 8, 30, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/rt" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "uuid-resolution",
                SourceUrl = "https://example.test/rt",
                VideoUrl = "https://example.test/v/rt",
                NativeId = "rt",
                SyncedAt = attempted,
                ResolutionState = ResolutionState.Failed,
                ResolutionAttemptedAt = attempted,
                ResolutionAttempts = 3,
                ResolutionError = "ERROR: [youtube] rt: Something unrecognised",
                ListingPosition = 7,
            });

            var read = _db.GetVideoByUuid("uuid-resolution");

            Assert.NotNull(read);
            Assert.Equal(ResolutionState.Failed, read!.ResolutionState);
            Assert.Equal(attempted, read.ResolutionAttemptedAt);
            Assert.Equal(3, read.ResolutionAttempts);
            Assert.Equal("ERROR: [youtube] rt: Something unrecognised", read.ResolutionError);
            Assert.Equal(7, read.ListingPosition);
        }

        [Fact]
        public void UpsertSource_RoundTripsLastResolvedAt()
        {
            var resolved = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord
            {
                Url = "https://example.test/lr",
                LastResolvedAt = resolved,
            });

            Assert.Equal(resolved, _db.GetSource("https://example.test/lr")!.LastResolvedAt);
        }

        [Fact]
        public void UpsertSource_DoesNotUndoAClear_WhenTheCallerHoldsAStaleRecord()
        {
            // The sync loads its SourceRecord before cleanup clears the stamp, then upserts that
            // record at the end of the pass; writing the stale stamp back would cancel the clear.
            const string url = "https://example.test/stale";
            _db.UpsertSource(new SourceRecord { Url = url });
            _db.SetLastResolvedAt(url, new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
            var stale = _db.GetSource(url)!;

            _db.ClearLastResolvedAt(url);
            _db.UpsertSource(stale);

            Assert.Null(_db.GetSource(url)!.LastResolvedAt);
        }

        [Fact]
        public void UpsertVideo_UpdatesTheResolutionColumns_OnASecondPass()
        {
            // The branch every later pass takes. A forgotten or swapped `excluded.…` in the
            // ON CONFLICT clause would leave the first pass's values in place, and the whole
            // state machine would look like it worked while never changing anything.
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/up" });
            var row = new VideoRecord
            {
                Id = "uuid-update",
                SourceUrl = "https://example.test/up",
                VideoUrl = "https://example.test/v/up",
                NativeId = "up",
                SyncedAt = DateTimeOffset.UnixEpoch,
                ResolutionState = ResolutionState.Pending,
            };
            _db.UpsertVideo(row);

            var failedAt = new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);
            row.ResolutionState = ResolutionState.Failed;
            row.ResolutionAttemptedAt = failedAt;
            row.ResolutionAttempts = 2;
            row.ResolutionError = "unrecognised";
            row.ListingPosition = 4;
            _db.UpsertVideo(row);

            var read = _db.GetVideoByUuid("uuid-update")!;
            Assert.Equal(ResolutionState.Failed, read.ResolutionState);
            Assert.Equal(failedAt, read.ResolutionAttemptedAt);
            Assert.Equal(2, read.ResolutionAttempts);
            Assert.Equal("unrecognised", read.ResolutionError);
            Assert.Equal(4, read.ListingPosition);
        }

        [Fact]
        public void SetListingPositions_RanksEveryRowItIsGiven_WhateverItsState()
        {
            // The rank belongs to the listing, so an unavailable or pending row gets it too —
            // those are exactly the rows a resolver orders by it, having no date to go on.
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/rank" });
            foreach (var (id, nativeId, state) in new[]
            {
                ("rank-a", "a", ResolutionState.Resolved),
                ("rank-b", "b", ResolutionState.Unavailable),
                ("rank-c", "c", ResolutionState.Pending),
            })
            {
                _db.UpsertVideo(new VideoRecord
                {
                    Id = id,
                    SourceUrl = "https://example.test/rank",
                    VideoUrl = "https://example.test/v/" + nativeId,
                    NativeId = nativeId,
                    SyncedAt = DateTimeOffset.UnixEpoch,
                    ResolutionState = state,
                });
            }

            _db.SetListingPositions(
                "https://example.test/rank",
                new Dictionary<string, int> { ["a"] = 0, ["b"] = 1, ["c"] = 2 });

            Assert.Equal(0, _db.GetVideoByUuid("rank-a")!.ListingPosition);
            Assert.Equal(1, _db.GetVideoByUuid("rank-b")!.ListingPosition);
            Assert.Equal(2, _db.GetVideoByUuid("rank-c")!.ListingPosition);

            // A second listing re-ranks: the one that was newest has slipped to second.
            _db.SetListingPositions(
                "https://example.test/rank",
                new Dictionary<string, int> { ["c"] = 0, ["a"] = 1, ["b"] = 2 });

            Assert.Equal(1, _db.GetVideoByUuid("rank-a")!.ListingPosition);
            Assert.Equal(0, _db.GetVideoByUuid("rank-c")!.ListingPosition);
        }

        [Fact]
        public void PublishableQueries_OfferOnlyResolvedRows()
        {
            // One notion of "publishable", for both the rows handed to the publisher and the ids
            // that protect an item from the orphan sweep. With an unfiltered id list, excluding a
            // row from the expected set leaves its library item in place for ever: the sweep skips
            // any item whose video the database still knows.
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/pub" });

            foreach (var (id, nativeId, state) in new[]
            {
                ("pub-resolved", "r1", ResolutionState.Resolved),
                ("pub-pending", "p1", ResolutionState.Pending),
                ("pub-unavailable", "u1", ResolutionState.Unavailable),
                ("pub-failed", "f1", ResolutionState.Failed),
            })
            {
                _db.UpsertVideo(new VideoRecord
                {
                    Id = id,
                    SourceUrl = "https://example.test/pub",
                    VideoUrl = "https://example.test/v/" + nativeId,
                    NativeId = nativeId,
                    SyncedAt = DateTimeOffset.UnixEpoch,
                    ResolutionState = state,
                });
            }

            Assert.Equal(
                ["pub-resolved"],
                _db.GetPublishableVideosBySource("https://example.test/pub").Select(v => v.Id));
            Assert.Equal(["pub-resolved"], _db.GetPublishableVideoIds());

            // The unfiltered reads keep their meaning: the resolver needs every row.
            Assert.Equal(4, _db.GetVideosBySource("https://example.test/pub").Count);
            Assert.Equal(4, _db.GetAllVideoIds().Count);
        }

        [Fact]
        public void EnsureCreated_LeavesTheSchemaAtVersion8()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";

            Assert.Equal(8L, (long)cmd.ExecuteScalar()!);
        }

        [Fact]
        public void EnsureCreated_GivesAFreshDatabaseTheResolutionColumnsAndIndex()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var columns = conn.CreateCommand();
            columns.CommandText =
                "SELECT GROUP_CONCAT(name) FROM pragma_table_info('videos') "
                + "WHERE name IN ('resolution_state','resolution_attempted_at','resolution_attempts',"
                + "'resolution_error','listing_position')";
            var found = (string?)columns.ExecuteScalar();
            Assert.NotNull(found);
            Assert.Equal(5, found!.Split(',').Length);

            using var sourceColumn = conn.CreateCommand();
            sourceColumn.CommandText =
                "SELECT name FROM pragma_table_info('sources') WHERE name = 'last_resolved_at'";
            Assert.Equal("last_resolved_at", sourceColumn.ExecuteScalar());

            using var index = conn.CreateCommand();
            index.CommandText =
                "SELECT GROUP_CONCAT(name) FROM pragma_index_info('idx_videos_resolution')";
            Assert.Equal("extractor_key,resolution_state", index.ExecuteScalar());
        }

        [Fact]
        public void EnsureCreated_GivesAFreshDatabaseTheUniqueNativeIdIndex()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var unique = conn.CreateCommand();
            unique.CommandText =
                "SELECT \"unique\" FROM pragma_index_list('videos') WHERE name = 'idx_videos_native_id'";
            Assert.Equal(1L, (long)unique.ExecuteScalar()!);

            using var columns = conn.CreateCommand();
            columns.CommandText =
                "SELECT GROUP_CONCAT(name) FROM pragma_index_info('idx_videos_native_id')";
            Assert.Equal("source_url,native_id", columns.ExecuteScalar());
        }

        [Fact]
        public void FirstSeenAt_RoundTripsThroughTheV3Column()
        {
            var seen = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/fs" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "uuid-first-seen",
                SourceUrl = "https://example.test/fs",
                VideoUrl = "https://example.test/v",
                FirstSeenAt = seen,
                SyncedAt = DateTimeOffset.UtcNow
            });

            Assert.Equal(seen, _db.GetVideoByUuid("uuid-first-seen")!.FirstSeenAt);
        }

        /// <summary>
        /// The upgrade path every existing installation takes: a database stamped v2 must gain the
        /// column AND have it backfilled from synced_at, because nothing else knows when a row that
        /// predates v3 was first seen.
        /// </summary>
        [Fact]
        public void EnsureCreated_UpgradesAV2DatabaseAndBackfillsFirstSeenAt()
        {
            var path = Path.GetTempFileName();
            try
            {
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (url TEXT PRIMARY KEY, name TEXT);
                        CREATE TABLE videos (
                            id             TEXT PRIMARY KEY,
                            source_url     TEXT NOT NULL,
                            video_url      TEXT NOT NULL,
                            synced_at      TEXT NOT NULL,
                            audio_langs    TEXT,
                            subtitle_langs TEXT,
                            tags           TEXT,
                            categories     TEXT,
                            channels       TEXT,
                            native_id      TEXT,
                            is_live        INTEGER NOT NULL DEFAULT 0,
                            extractor_key TEXT
                        );
                        INSERT INTO videos (id, source_url, video_url, synced_at)
                        VALUES ('legacy', 'https://example.test/s', 'https://example.test/v',
                                '2026-01-02T03:04:05.0000000+00:00');
                        PRAGMA user_version = 2;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var db = new VodDatabase(path, NullLogger<VodDatabase>.Instance);
                db.EnsureCreated();

                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();

                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(8L, (long)version.ExecuteScalar()!);

                using var backfilled = conn.CreateCommand();
                backfilled.CommandText = "SELECT first_seen_at FROM videos WHERE id = 'legacy'";
                Assert.Equal("2026-01-02T03:04:05.0000000+00:00", backfilled.ExecuteScalar());
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// "Set once, never updated" is enforced in SQL by a COALESCE in the upsert, not by the caller.
        /// If that guard were dropped, the discovery date would move whenever a later pass proposed a
        /// different one, and the published date added would move with it on every sync.
        /// </summary>
        [Fact]
        public void FirstSeenAt_IsNeverOverwrittenByALaterUpsert()
        {
            var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/once" });

            var video = new VideoRecord
            {
                Id = "uuid-set-once",
                SourceUrl = "https://example.test/once",
                VideoUrl = "https://example.test/v",
                FirstSeenAt = first,
                SyncedAt = DateTimeOffset.UtcNow
            };
            _db.UpsertVideo(video);

            // A later pass proposes a different discovery date. The stored one has to win.
            video.FirstSeenAt = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero);
            _db.UpsertVideo(video);

            Assert.Equal(first, _db.GetVideoByUuid("uuid-set-once")!.FirstSeenAt);
        }

        [Fact]
        public void FirstSeenAt_DefaultsToNullWhenNotSet()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/nofs" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "uuid-no-first-seen",
                SourceUrl = "https://example.test/nofs",
                VideoUrl = "https://example.test/v",
                SyncedAt = DateTimeOffset.UtcNow
            });

            Assert.Null(_db.GetVideoByUuid("uuid-no-first-seen")!.FirstSeenAt);
        }

        /// <summary>
        /// The v2 column has to survive a round trip on a database built by replaying every
        /// migration step, which is what a fresh install does: step 1 creates the table without it
        /// and step 2 adds it.
        /// </summary>
        [Fact]
        public void Channels_RoundTripThroughTheExtrasPayload()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/c" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "uuid-channels",
                SourceUrl = "https://example.test/c",
                VideoUrl = "https://example.test/v",
                ExtractorKey = "Youtube",
                Extras = new VideoExtras { Channels = ["Terrapodia"] },
                SyncedAt = DateTimeOffset.UtcNow
            });

            var retrieved = _db.GetVideoByUuid("uuid-channels");

            Assert.NotNull(retrieved);
            Assert.Equal(["Terrapodia"], retrieved.Extras.Channels);
        }

        /// <summary>
        /// The upgrade path, as opposed to a fresh install: a database already stamped v1 has to gain
        /// the v2 column when it is opened, and end up with the same columns as one that replayed
        /// every step. This is the path every existing installation takes.
        /// </summary>
        /// <remarks>
        /// Asserts the CURRENT schema version rather than 2, because opening a v1 database replays
        /// every step after it, not just the next one. The `channels` assertion is what actually pins
        /// the v1 → v2 step.
        /// </remarks>
        [Fact]
        public void EnsureCreated_UpgradesAV1DatabaseAndAddsTheV2Column()
        {
            var upgradedPath = Path.GetTempFileName();
            try
            {
                // Build a v1 database by hand: the v1 videos table, without the channels column.
                using (var seed = new SqliteConnection($"Data Source={upgradedPath}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (url TEXT PRIMARY KEY, name TEXT);
                        CREATE TABLE videos (
                            id             TEXT PRIMARY KEY,
                            source_url     TEXT NOT NULL,
                            video_url      TEXT NOT NULL,
                            synced_at      TEXT NOT NULL,
                            audio_langs    TEXT,
                            subtitle_langs TEXT,
                            tags           TEXT,
                            categories     TEXT,
                            native_id      TEXT,
                            is_live        INTEGER NOT NULL DEFAULT 0,
                            extractor_key TEXT
                        );
                        PRAGMA user_version = 1;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var upgraded = new VodDatabase(upgradedPath, NullLogger<VodDatabase>.Instance);
                upgraded.EnsureCreated();

                using var conn = new SqliteConnection($"Data Source={upgradedPath}");
                conn.Open();

                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(8L, (long)version.ExecuteScalar()!);

                using var columns = conn.CreateCommand();
                columns.CommandText = "SELECT name FROM pragma_table_info('videos') WHERE name = 'extras'";
                Assert.Equal("extras", columns.ExecuteScalar());
            }
            finally
            {
                if (File.Exists(upgradedPath))
                {
                    File.Delete(upgradedPath);
                }
            }
        }

        [Fact]
        public void Channels_DefaultToEmptyWhenNotSet()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/d" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "uuid-no-channels",
                SourceUrl = "https://example.test/d",
                VideoUrl = "https://example.test/v",
                SyncedAt = DateTimeOffset.UtcNow
            });

            Assert.Empty(_db.GetVideoByUuid("uuid-no-channels")!.Extras.Channels);
        }

        [Fact]
        public void UpsertVideo_RoundTripsExtras()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/src" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "aaaaaaaa-0000-0000-0000-000000000001",
                SourceUrl = "https://example.test/src",
                VideoUrl = "https://example.test/v/1",
                SyncedAt = DateTimeOffset.UnixEpoch,
                Extras = new VideoExtras
                {
                    Tags = ["nature", "océan"],
                    Categories = ["Documentaire"],
                    Channels = ["Arte"],
                    SubtitleLanguages = ["fr", "de"],
                    // I5: Qualities and AudioTracks are positional records — System.Text.Json must
                    // match constructor parameters to deserialize them. Null Fps and null Language
                    // exercise the nullable members.
                    Qualities =
                    [
                        new VideoQuality(1080, "h264", null),
                        new VideoQuality(720, "vp9", 60)
                    ],
                    AudioTracks =
                    [
                        new AudioTrack(null, "vf"),
                        new AudioTrack("fr", null)
                    ]
                }
            });

            var read = _db.GetVideoByUuid("aaaaaaaa-0000-0000-0000-000000000001");

            Assert.NotNull(read);
            Assert.Equal(["nature", "océan"], read.Extras.Tags);
            Assert.Equal(["Documentaire"], read.Extras.Categories);
            Assert.Equal(["Arte"], read.Extras.Channels);
            Assert.Equal(["fr", "de"], read.Extras.SubtitleLanguages);

            // Qualities round-trip — including null Fps
            Assert.Equal(2, read.Extras.Qualities.Count);
            Assert.Equal(1080, read.Extras.Qualities[0].Height);
            Assert.Equal("h264", read.Extras.Qualities[0].Codec);
            Assert.Null(read.Extras.Qualities[0].Fps);
            Assert.Equal(720, read.Extras.Qualities[1].Height);
            Assert.Equal("vp9", read.Extras.Qualities[1].Codec);
            Assert.Equal(60, read.Extras.Qualities[1].Fps);

            // AudioTracks round-trip — including null Language and null Label
            Assert.Equal(2, read.Extras.AudioTracks.Count);
            Assert.Null(read.Extras.AudioTracks[0].Language);
            Assert.Equal("vf", read.Extras.AudioTracks[0].Label);
            Assert.Equal("fr", read.Extras.AudioTracks[1].Language);
            Assert.Null(read.Extras.AudioTracks[1].Label);
        }

        [Fact]
        public void EnsureCreated_DropsTheConsolidatedColumns()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info('videos')";
            var columns = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(0));

            Assert.DoesNotContain("audio_langs", columns);
            Assert.DoesNotContain("subtitle_langs", columns);
            Assert.DoesNotContain("tags", columns);
            Assert.DoesNotContain("categories", columns);
            Assert.DoesNotContain("channels", columns);
            Assert.Contains("extras", columns);
        }

        [Fact]
        public void EnsureCreated_RemovesTheDebugSentinelRow()
        {
            // Inserted by migration v1 then filtered out of every listing: it existed only to be excluded.
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sources WHERE url = '__debug__'";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }

        /// <summary>
        /// C2: a pre-v5 database that carries a video row with source_url = '__debug__' must
        /// survive migration. Without the fix (children deleted before parent), the DELETE on
        /// sources raises SQLITE_CONSTRAINT and the plugin is stuck at user_version = 4 forever.
        /// </summary>
        [Fact]
        public void EnsureCreated_MigrationV5_DeletesChildVideosBeforeParentSource()
        {
            var path = Path.GetTempFileName();
            try
            {
                // Seed a v4 schema that looks like a real pre-v5 installation: the debug source
                // row (inserted by v1) and one video referencing it (as a debug session could
                // leave behind). Migration v5 must delete the video before the source.
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var pragma = seed.CreateCommand();
                    pragma.CommandText = "PRAGMA foreign_keys=OFF";
                    pragma.ExecuteNonQuery();

                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (
                            url                  TEXT PRIMARY KEY,
                            name                 TEXT,
                            series_thumbnail_url TEXT,
                            series_banner_url    TEXT,
                            tags                 TEXT,
                            published_image_url  TEXT,
                            published_banner_url TEXT,
                            series_synopsis      TEXT,
                            last_synced_at       TEXT,
                            last_error           TEXT,
                            video_count          INTEGER NOT NULL DEFAULT 0,
                            published_anchor_id  TEXT
                        );
                        CREATE TABLE videos (
                            id               TEXT PRIMARY KEY,
                            source_url       TEXT NOT NULL REFERENCES sources(url),
                            video_url        TEXT NOT NULL,
                            synced_at        TEXT NOT NULL,
                            audio_langs      TEXT,
                            subtitle_langs   TEXT,
                            tags             TEXT,
                            categories       TEXT,
                            channels         TEXT,
                            first_seen_at    TEXT,
                            native_id        TEXT,
                            is_live          INTEGER NOT NULL DEFAULT 0,
                            extractor_key TEXT
                        );
                        INSERT INTO sources (url, name) VALUES ('__debug__', 'Debug (internal)');
                        INSERT INTO videos  (id, source_url, video_url, synced_at)
                            VALUES ('dbg-vid-1', '__debug__', 'https://example.test/v', '2026-01-01T00:00:00Z');
                        PRAGMA user_version = 4;
                        """;
                    cmd.ExecuteNonQuery();
                }

                // Without the fix this throws SQLITE_CONSTRAINT (FOREIGN KEY violation) and the
                // database stays at user_version = 4, breaking every subsequent start.
                using var upgraded = new VodDatabase(path, NullLogger<VodDatabase>.Instance);
                var ex = Record.Exception(upgraded.EnsureCreated);
                Assert.Null(ex);

                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();
                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(8L, (long)version.ExecuteScalar()!);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// C3: a v5 database that has the <c>is_live</c> column must gain <c>live_status</c> and
        /// lose <c>is_live</c> after migration v6. This covers the upgrade path every existing
        /// installation takes; a fresh database created from scratch is covered by
        /// <see cref="EnsureCreated_LeavesTheSchemaAtVersion8"/>.
        /// </summary>
        [Fact]
        public void EnsureCreated_MigrationV6_ReplacesIsLiveColumnWithLiveStatus()
        {
            var path = Path.GetTempFileName();
            try
            {
                // Seed a v5 schema: extras column present, is_live present, no live_status.
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (
                            url         TEXT PRIMARY KEY,
                            name        TEXT,
                            video_count INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE TABLE videos (
                            id               TEXT PRIMARY KEY,
                            source_url       TEXT NOT NULL REFERENCES sources(url),
                            video_url        TEXT NOT NULL,
                            synced_at        TEXT NOT NULL,
                            extras           TEXT,
                            description      TEXT,
                            first_seen_at    TEXT,
                            published_image_url TEXT,
                            thumbnail_url    TEXT,
                            extractor_key    TEXT,
                            native_id        TEXT,
                            published_at     TEXT,
                            duration_seconds INTEGER,
                            season_number    INTEGER,
                            episode_number   INTEGER,
                            is_live          INTEGER NOT NULL DEFAULT 0
                        );
                        INSERT INTO sources (url, name) VALUES ('https://example.test/src', 'Test');
                        INSERT INTO videos (id, source_url, video_url, synced_at, is_live)
                        VALUES ('v5-row', 'https://example.test/src', 'https://example.test/v',
                                '2026-01-01T00:00:00+00:00', 1);
                        PRAGMA user_version = 5;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var upgraded = new VodDatabase(path, NullLogger<VodDatabase>.Instance);
                upgraded.EnsureCreated();

                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();

                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(8L, (long)version.ExecuteScalar()!);

                // live_status must be present (default 'NotLive' applied to the pre-existing row).
                using var liveStatus = conn.CreateCommand();
                liveStatus.CommandText = "SELECT live_status FROM videos WHERE id = 'v5-row'";
                Assert.Equal("NotLive", liveStatus.ExecuteScalar());

                // is_live must be gone.
                using var columns = conn.CreateCommand();
                columns.CommandText = "SELECT name FROM pragma_table_info('videos') WHERE name = 'is_live'";
                Assert.Null(columns.ExecuteScalar());
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// A hand-edited or truncated payload must not take the whole video down: the row still reads,
        /// with an empty payload. Review Focus item 1.
        /// </summary>
        [Fact]
        public void GetVideoByUuid_OnCorruptExtras_ReturnsAnEmptyPayload()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/src" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "aaaaaaaa-0000-0000-0000-000000000002",
                SourceUrl = "https://example.test/src",
                VideoUrl = "https://example.test/v/2",
                SyncedAt = DateTimeOffset.UnixEpoch
            });

            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE videos SET extras = '{not json' WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", "aaaaaaaa-0000-0000-0000-000000000002");
                cmd.ExecuteNonQuery();
            }

            var read = _db.GetVideoByUuid("aaaaaaaa-0000-0000-0000-000000000002");

            Assert.NotNull(read);
            Assert.Empty(read.Extras.Tags);
            Assert.Empty(read.Extras.Qualities);
        }

        [Fact]
        public void PublishedAnchorId_RoundTripsThroughTheV4Column()
        {
            var anchor = Guid.NewGuid();
            _db.UpsertSource(new SourceRecord
            {
                Url = "https://example.test/anchored",
                PublishedAnchorId = anchor
            });

            Assert.Equal(anchor, _db.GetSource("https://example.test/anchored")!.PublishedAnchorId);
        }

        [Fact]
        public void PublishedAnchorId_IsNullUntilSomethingHasBeenPublished()
        {
            // Null is not an error state: it means "unknown", and the sweep then falls back to the
            // configured libraries — exactly the best effort that existed before the column.
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/fresh" });

            Assert.Null(_db.GetSource("https://example.test/fresh")!.PublishedAnchorId);
        }

        [Fact]
        public void PublishedAnchorId_SurvivesAnUpsertMadeFromAReadBackRecord()
        {
            // The trap this guards: SyncTask's error path reads a record, sets LastError and writes
            // it back. If MapSource did not read the column, that round trip would silently erase
            // the anchor and the next retirement would no longer know where to look.
            var anchor = Guid.NewGuid();
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/rt", PublishedAnchorId = anchor });

            var readBack = _db.GetSource("https://example.test/rt")!;
            readBack.LastError = "boom";
            _db.UpsertSource(readBack);

            Assert.Equal(anchor, _db.GetSource("https://example.test/rt")!.PublishedAnchorId);
        }

        [Fact]
        public void PublishedMode_RoundTripsThroughTheV8Column()
        {
            _db.UpsertSource(new SourceRecord
            {
                Url = "https://example.test/moded",
                PublishedMode = SourceMode.Movies
            });

            Assert.Equal(SourceMode.Movies, _db.GetSource("https://example.test/moded")!.PublishedMode);
        }

        [Fact]
        public void PublishedMode_IsNullUntilSomethingHasBeenPublished()
        {
            // Null means "the library's shape is unknown", which a sync reads as a mismatch against
            // any configured mode: the source gets one republication rather than being trusted.
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/unmoded" });

            Assert.Null(_db.GetSource("https://example.test/unmoded")!.PublishedMode);
        }

        [Fact]
        public void PublishedMode_SurvivesAnUpsertMadeFromAReadBackRecord()
        {
            // Same trap as PublishedAnchorId: the sync reads a record and writes it back at step 7.
            // A column MapSource did not read would be erased, and every later sync would see a
            // mismatch and clear last_resolved_at forever.
            _db.UpsertSource(new SourceRecord
            {
                Url = "https://example.test/mode-rt",
                PublishedMode = SourceMode.Series
            });

            var readBack = _db.GetSource("https://example.test/mode-rt")!;
            readBack.LastError = "boom";
            _db.UpsertSource(readBack);

            Assert.Equal(SourceMode.Series, _db.GetSource("https://example.test/mode-rt")!.PublishedMode);
        }

        /// <summary>
        /// A fresh database and a migrated one must agree on the shape of <c>sources</c>. The v3
        /// review left this untested (finding P7) — a column added to the migration but forgotten in
        /// the CREATE TABLE, or the reverse, only shows up on one of the two installation paths.
        /// </summary>
        [Fact]
        public void EnsureCreated_GivesAMigratedDatabaseTheSameSourcesShapeAsAFreshOne()
        {
            var path = Path.GetTempFileName();
            try
            {
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    // The v1 shape of sources, verbatim: anything less would make the comparison
                    // below pass for the wrong reason.
                    cmd.CommandText = """
                        CREATE TABLE sources (
                            url                  TEXT PRIMARY KEY,
                            name                 TEXT,
                            series_thumbnail_url TEXT,
                            series_banner_url    TEXT,
                            tags                 TEXT,
                            published_image_url  TEXT,
                            published_banner_url TEXT,
                            series_synopsis      TEXT,
                            last_synced_at       TEXT,
                            last_error           TEXT,
                            video_count          INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE TABLE videos (
                            id             TEXT PRIMARY KEY,
                            source_url     TEXT NOT NULL,
                            video_url      TEXT NOT NULL,
                            synced_at      TEXT NOT NULL,
                            audio_langs    TEXT,
                            subtitle_langs TEXT,
                            tags           TEXT,
                            categories     TEXT,
                            channels       TEXT,
                            first_seen_at  TEXT,
                            native_id      TEXT,
                            is_live        INTEGER NOT NULL DEFAULT 0,
                            extractor_key TEXT
                        );
                        PRAGMA user_version = 3;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using (var db = new VodDatabase(path, NullLogger<VodDatabase>.Instance))
                {
                    db.EnsureCreated();
                }

                Assert.Equal(SourceColumns(_dbPath), SourceColumns(path));
                Assert.Contains("published_anchor_id", SourceColumns(path));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static List<string> SourceColumns(string path)
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(sources)";
            using var reader = cmd.ExecuteReader();

            var columns = new List<string>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(reader.GetOrdinal("name")));
            }

            columns.Sort(StringComparer.Ordinal);
            return columns;
        }

        // --- Deferred resolution queries ---

        /// <summary>Builds a row in a given state, as a pass would have left it.</summary>
        private void SeedForResolution(
            string sourceUrl,
            string id,
            string nativeId,
            ResolutionState state,
            string? extractorKey = "Youtube",
            DateTimeOffset? attemptedAt = null,
            int attempts = 0,
            int? listingPosition = null)
        {
            _db.UpsertVideo(new VideoRecord
            {
                Id = id,
                SourceUrl = sourceUrl,
                VideoUrl = "https://example.test/v/" + nativeId,
                NativeId = nativeId,
                ExtractorKey = extractorKey,
                SyncedAt = DateTimeOffset.UnixEpoch,
                ResolutionState = state,
                ResolutionAttemptedAt = attemptedAt,
                ResolutionAttempts = attempts,
                ListingPosition = listingPosition,
            });
        }

        [Fact]
        public void SelectSourceToResolve_PrefersTheSourceWithTheFewestVideosDue()
        {
            // Shortest first (§6.2): serving a source lowers its own count, so it stays at the
            // front until exhausted — depth is a consequence, not a rule — and a source with two
            // new videos goes ahead of one with a 300-video backlog.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/big" });
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/small" });
            // Both sources have been resolved before, so the never-served branch does not apply.
            _db.SetLastResolvedAt("https://example.test/big", now.AddDays(-1));
            _db.SetLastResolvedAt("https://example.test/small", now.AddDays(-1));

            for (var i = 0; i < 5; i++)
                SeedForResolution("https://example.test/big", $"big-{i}", $"b{i}", ResolutionState.Pending);

            SeedForResolution("https://example.test/small", "small-0", "s0", ResolutionState.Pending);

            Assert.Equal(
                "https://example.test/small",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));
        }

        [Fact]
        public void SelectSourceToResolve_ServesNeverServedSourcesWithWorkShortestFirst()
        {
            // Seeding after a purge: every source is never-served and has a backlog. Giving each of
            // them the never-served priority served them round-robin — one wave each, none ever
            // finished, so with publication waiting for a settled source nothing appeared. A source
            // with due rows competes on its due count; the shortest is served, and stays first.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/a-big" });
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/b-small" });

            for (var i = 0; i < 5; i++)
                SeedForResolution("https://example.test/a-big", $"big-{i}", $"b{i}", ResolutionState.Pending);
            for (var i = 0; i < 3; i++)
                SeedForResolution("https://example.test/b-small", $"small-{i}", $"s{i}", ResolutionState.Pending);

            Assert.Equal(
                "https://example.test/b-small",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));

            // One wave later the small source is stamped and still has a row due: it is still first.
            _db.SetLastResolvedAt("https://example.test/b-small", now);
            SeedForResolution("https://example.test/b-small", "small-0", "s0", ResolutionState.Resolved);
            SeedForResolution("https://example.test/b-small", "small-1", "s1", ResolutionState.Resolved);

            Assert.Equal(
                "https://example.test/b-small",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));
        }

        [Fact]
        public void SelectSourceToResolve_IgnoresWhatIsNotDueYet()
        {
            // A failed video inside its backoff window is not work; a source with nothing due is
            // not a candidate at all, so the pass can end instead of spinning on it.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/waiting" });
            // Mark the source as previously served so it is not picked up by the never-served
            // branch added for C2 (last_resolved_at IS NULL would otherwise return it).
            _db.SetLastResolvedAt("https://example.test/waiting", now.AddDays(-1));

            // Failed once a minute ago: the base window is 15 minutes, so not due.
            SeedForResolution(
                "https://example.test/waiting", "w-0", "w0", ResolutionState.Failed,
                attemptedAt: now.AddMinutes(-1), attempts: 1);

            // Resolved rows are never due.
            SeedForResolution("https://example.test/waiting", "w-1", "w1", ResolutionState.Resolved);

            // The listing's own verdict has no attempt timestamp: it is re-checked by the next
            // enumeration, for free, and must not cost the resolver a call.
            SeedForResolution("https://example.test/waiting", "w-2", "w2", ResolutionState.Unavailable);

            Assert.Null(_db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));
            Assert.Empty(_db.GetPlatformsWithDueVideos(now, retryMinutes: 15));
        }

        [Fact]
        public void SelectSourceToResolve_ReleasesAFailedVideoWhenItsWindowHasPassed()
        {
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/retry" });
            _db.SetLastResolvedAt("https://example.test/retry", now.AddDays(-1));

            // Two consecutive failures → 15 × 2 = 30 minutes. 29 minutes is too early, 31 is not.
            SeedForResolution(
                "https://example.test/retry", "r-0", "r0", ResolutionState.Failed,
                attemptedAt: now.AddMinutes(-29), attempts: 2);
            Assert.Null(_db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));

            SeedForResolution(
                "https://example.test/retry", "r-0", "r0", ResolutionState.Failed,
                attemptedAt: now.AddMinutes(-31), attempts: 2);
            Assert.Equal(
                "https://example.test/retry",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));
        }

        [Fact]
        public void SelectSourceToResolve_RechecksAnUnavailableVideoAfterSevenDays()
        {
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/gone" });
            _db.SetLastResolvedAt("https://example.test/gone", now.AddDays(-1));

            SeedForResolution(
                "https://example.test/gone", "g-0", "g0", ResolutionState.Unavailable,
                attemptedAt: now.AddDays(-6));
            Assert.Null(_db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));

            SeedForResolution(
                "https://example.test/gone", "g-0", "g0", ResolutionState.Unavailable,
                attemptedAt: now.AddDays(-8));
            Assert.Equal(
                "https://example.test/gone",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));
        }

        [Fact]
        public void GetPlatformsWithDueVideos_PartitionsByExtractorKey()
        {
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/yt" });
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/arte" });
            // Both sources have been resolved before so the never-served arm does not fire;
            // this test pins that the DUE predicate partitions by extractor key, not the UNION arm.
            _db.SetLastResolvedAt("https://example.test/yt", now.AddDays(-1));
            _db.SetLastResolvedAt("https://example.test/arte", now.AddDays(-1));

            SeedForResolution("https://example.test/yt", "y-0", "y0", ResolutionState.Pending);
            SeedForResolution(
                "https://example.test/arte", "a-0", "a0", ResolutionState.Pending,
                extractorKey: "ArteTV");

            Assert.Equal(
                ["ArteTV", "Youtube"],
                _db.GetPlatformsWithDueVideos(now, retryMinutes: 15).OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(
                "https://example.test/arte",
                _db.SelectSourceToResolve("ArteTV", now, retryMinutes: 15));
        }

        [Fact]
        public void GetDueVideos_TakesTheNewestFirst_AndCapsTheWave()
        {
            // listing_position 0 is the newest entry, and a row that has never been enumerated
            // sorts last — there is nothing better to go on, since a pending video has no date.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/order" });

            SeedForResolution("https://example.test/order", "o-2", "o2", ResolutionState.Pending, listingPosition: 2);
            SeedForResolution("https://example.test/order", "o-null", "on", ResolutionState.Pending);
            SeedForResolution("https://example.test/order", "o-0", "o0", ResolutionState.Pending, listingPosition: 0);
            SeedForResolution("https://example.test/order", "o-1", "o1", ResolutionState.Pending, listingPosition: 1);

            Assert.Equal(
                ["o-0", "o-1"],
                _db.GetDueVideos("https://example.test/order", now, retryMinutes: 15, limit: 2)
                    .Select(v => v.Id));
            Assert.Equal(
                ["o-0", "o-1", "o-2", "o-null"],
                _db.GetDueVideos("https://example.test/order", now, retryMinutes: 15, limit: 10)
                    .Select(v => v.Id));
        }

        [Fact]
        public void SetLastResolvedAt_RecordsWhenTheWaveFinished()
        {
            var when = new DateTimeOffset(2026, 10, 13, 12, 30, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/stamp" });

            _db.SetLastResolvedAt("https://example.test/stamp", when);

            Assert.Equal(when, _db.GetSource("https://example.test/stamp")!.LastResolvedAt);
        }

        [Fact]
        public void SelectSourceToResolve_CapWindowAtSevenDaysRegardlessOfBase()
        {
            // With the old predicate (multiplier cap of 672), a 60-minute base would cap at
            // 672 × 60 = 40 320 minutes = 28 days, so a 6-day-old attempt with 20 failures
            // would not be due yet. The spec says seven days unconditionally: this test proves
            // the cap is a time cap, not a factor cap.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/cap" });
            _db.SetLastResolvedAt("https://example.test/cap", now.AddDays(-1));

            // 20 attempts, base 60: window = MIN(60 × 2^19 / 1440, 7.0) = 7.0 days.
            // 6 days old: not yet due.
            SeedForResolution(
                "https://example.test/cap", "cap-0", "c0", ResolutionState.Failed,
                attemptedAt: now.AddDays(-6), attempts: 20);
            Assert.Null(_db.SelectSourceToResolve("Youtube", now, retryMinutes: 60));

            // 8 days old: past the seven-day cap, must be due.
            SeedForResolution(
                "https://example.test/cap", "cap-0", "c0", ResolutionState.Failed,
                attemptedAt: now.AddDays(-8), attempts: 20);
            Assert.Equal(
                "https://example.test/cap",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 60));
        }

        [Fact]
        public void GetDueVideos_ReturnsEmptyForNonPositiveLimit()
        {
            // SQLite treats LIMIT -1 as unbounded; a non-positive limit must be caught before
            // the query runs, so a misconfigured wave size can never trigger an unbounded burst.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/zerowave" });
            SeedForResolution("https://example.test/zerowave", "z-0", "z0", ResolutionState.Pending);

            Assert.Empty(_db.GetDueVideos("https://example.test/zerowave", now, retryMinutes: 15, limit: 0));
            Assert.Empty(_db.GetDueVideos("https://example.test/zerowave", now, retryMinutes: 15, limit: -1));
        }

        [Fact]
        public void GetPlatformsWithDueVideos_GroupsNullExtractorKeyUnderEmptyString()
        {
            // A row written before extractor keys existed has a NULL extractor_key. COALESCE maps
            // it to '', so it appears in the empty-string lane rather than being dropped from
            // every wave. Deleting the COALESCE from either query would make this test fail.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/nokey" });

            SeedForResolution(
                "https://example.test/nokey", "nk-0", "nk0", ResolutionState.Pending,
                extractorKey: null);

            var platforms = _db.GetPlatformsWithDueVideos(now, retryMinutes: 15);
            Assert.Equal([""], platforms.OrderBy(k => k, StringComparer.Ordinal));

            Assert.Equal(
                "https://example.test/nokey",
                _db.SelectSourceToResolve("", now, retryMinutes: 15));
        }

        [Fact]
        public void SelectSourceToResolve_SelectsANeverServedSource_EvenWhenItsOnlyRowIsResolved()
        {
            // A single-video source writes a Resolved row on its first sync, so it has no Pending
            // rows. Without the never-served branch the resolver would never publish it.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/never-served" });

            // last_resolved_at is NULL by default (SourceRecord constructor).
            SeedForResolution("https://example.test/never-served", "ns-0", "ns0", ResolutionState.Resolved);

            Assert.Equal(
                "https://example.test/never-served",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));

            Assert.Contains(
                "Youtube",
                _db.GetPlatformsWithDueVideos(now, retryMinutes: 15));
        }

        [Fact]
        public void SelectSourceToResolve_IgnoresNeverServedSource_AfterSetLastResolvedAt()
        {
            // Once the resolver has run one wave for a source, last_resolved_at is stamped and the
            // never-served branch must no longer return that source — resolved rows are never due.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/now-served" });
            SeedForResolution("https://example.test/now-served", "ts-0", "ts0", ResolutionState.Resolved);

            _db.SetLastResolvedAt("https://example.test/now-served", now.AddMinutes(-5));

            Assert.Null(_db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));
            Assert.DoesNotContain(
                "Youtube",
                _db.GetPlatformsWithDueVideos(now, retryMinutes: 15));
        }

        [Fact]
        public void GetPlatformsWithDueVideos_NeverReturnsListingAbsenceRow_HoweverOldItsResolutionAttempt()
        {
            // B-I3: an Unavailable row written by the listing verdict must never cost a yt-dlp call.
            // The listing-absence verdict does not stamp ResolutionAttemptedAt, so the Unavailable
            // arm of DueNowPredicate (which requires a non-null ResolutionAttemptedAt) never fires.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/absent" });
            _db.SetLastResolvedAt("https://example.test/absent", now.AddDays(-1));

            // Unavailable row with no attempt stamp — exactly what ApplyListingVerdict writes.
            _db.UpsertVideo(new VideoRecord
            {
                Id = "absent-0",
                SourceUrl = "https://example.test/absent",
                VideoUrl = "https://example.test/v/absent",
                NativeId = "absent-0",
                ExtractorKey = "Youtube",
                SyncedAt = DateTimeOffset.UnixEpoch,
                ResolutionState = ResolutionState.Unavailable,
                ResolutionError = SyncService.ListingAbsenceError,
                ResolutionAttemptedAt = null,  // NOT stamped
            });

            // Should never appear as due, regardless of how much time passes.
            Assert.Null(_db.SelectSourceToResolve("Youtube", now.AddDays(30), retryMinutes: 15));
            Assert.DoesNotContain(
                "Youtube",
                _db.GetPlatformsWithDueVideos(now.AddDays(30), retryMinutes: 15));
        }

        [Fact]
        public void SelectSourceToResolve_ReturnsDrainedSource_AfterClearLastResolvedAt()
        {
            // B-I4: a source whose backlog is drained has no due rows, so the resolver would never
            // select it again without a nudge. After a verdict change, ClearLastResolvedAt re-enters
            // it in the never-served arm for exactly one wave that publishes with updated rows.
            var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/drained" });
            _db.SetLastResolvedAt("https://example.test/drained", now.AddHours(-1));

            // Only resolved rows — source is fully drained.
            SeedForResolution("https://example.test/drained", "d-0", "d0", ResolutionState.Resolved);

            // Without ClearLastResolvedAt, the source is not a candidate.
            Assert.Null(_db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));

            // After clearing, the never-served arm picks it up.
            _db.ClearLastResolvedAt("https://example.test/drained");
            Assert.Equal(
                "https://example.test/drained",
                _db.SelectSourceToResolve("Youtube", now, retryMinutes: 15));
            Assert.Contains(
                "Youtube",
                _db.GetPlatformsWithDueVideos(now, retryMinutes: 15));
        }

        [Fact]
        public void EnsureCreated_MigrationV8_AddsAnEmptyStreamDescriptionsColumn()
        {
            var path = Path.GetTempFileName();
            try
            {
                using (var seed = new SqliteConnection($"Data Source={path}"))
                {
                    seed.Open();
                    using var cmd = seed.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (url TEXT PRIMARY KEY);
                        CREATE TABLE videos (
                            id            TEXT PRIMARY KEY,
                            source_url    TEXT NOT NULL,
                            extractor_key TEXT,
                            extras        TEXT
                        );
                        INSERT INTO sources (url) VALUES ('https://example.test/a');
                        INSERT INTO videos (id, source_url) VALUES ('v', 'https://example.test/a');
                        PRAGMA user_version = 7;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using (var upgraded = new VodDatabase(path, NullLogger<VodDatabase>.Instance))
                {
                    upgraded.EnsureCreated();
                }

                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();
                using var read = conn.CreateCommand();
                read.CommandText = "SELECT stream_descriptions FROM videos WHERE id = 'v'";
                Assert.Equal(DBNull.Value, read.ExecuteScalar());
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void UpsertVideo_RoundTripsStreamDescriptions()
        {
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/s" });
            _db.UpsertVideo(new VideoRecord
            {
                Id = "v",
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v/1",
                SyncedAt = DateTimeOffset.UnixEpoch,
                StreamDescriptions = """{"primary":[]}""",
            });

            Assert.Equal("""{"primary":[]}""", _db.GetVideosBySource("https://example.test/s").Single().StreamDescriptions);
        }

        [Fact]
        public void UpsertVideo_KeepsStoredStreamDescriptions_WhenTheNewValueIsNull()
        {
            // A sync re-upserting a known row, or a re-resolution that skips description (outside
            // retention), must not erase a description already paid for (spec E2: kept).
            _db.UpsertSource(new SourceRecord { Url = "https://example.test/s" });
            var row = new VideoRecord
            {
                Id = "v",
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v/1",
                SyncedAt = DateTimeOffset.UnixEpoch,
                StreamDescriptions = """{"primary":[]}""",
            };
            _db.UpsertVideo(row);

            row.StreamDescriptions = null;
            _db.UpsertVideo(row);

            Assert.Equal("""{"primary":[]}""", _db.GetVideosBySource("https://example.test/s").Single().StreamDescriptions);
        }

        [Fact]
        public void GetResolutionSummaries_CountsEachStateAndKeepsTheLatestError()
        {
            const string url = "https://example.test/s";
            _db.UpsertSource(new SourceRecord { Url = url });
            var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
            SeedForResolution(url, "r", "r", ResolutionState.Resolved);
            SeedForResolution(url, "p", "p", ResolutionState.Pending);
            SeedForResolution(url, "u", "u", ResolutionState.Unavailable);
            SeedForResolution(url, "f-old", "f-old", ResolutionState.Failed, attemptedAt: now.AddHours(-2), attempts: 1);
            SeedForResolution(url, "f-new", "f-new", ResolutionState.Failed, attemptedAt: now, attempts: 1);
            SetError("f-old", "old error");
            SetError("f-new", "new error");

            var summary = _db.GetResolutionSummaries()[url];

            Assert.Equal(new ResolutionSummary(1, 1, 2, 1, false, "new error"), summary);
        }

        [Fact]
        public void GetResolutionSummaries_AgreesWithSourceIsSettled()
        {
            // The SQL duplicates ResolutionSettlement.Blocks; this pins the two together.
            const string url = "https://example.test/s";
            _db.UpsertSource(new SourceRecord { Url = url });
            SeedForResolution(url, "r", "r", ResolutionState.Resolved);
            SeedForResolution(url, "f", "f", ResolutionState.Failed, attempts: ResolutionSettlement.BlockingFailedAttempts - 1);
            Assert.Equal(
                ResolutionService.SourceIsSettled(_db.GetVideosBySource(url)),
                _db.GetResolutionSummaries()[url].Settled);

            SeedForResolution(url, "f", "f", ResolutionState.Failed, attempts: ResolutionSettlement.BlockingFailedAttempts);
            Assert.Equal(
                ResolutionService.SourceIsSettled(_db.GetVideosBySource(url)),
                _db.GetResolutionSummaries()[url].Settled);
            Assert.True(_db.GetResolutionSummaries()[url].Settled);
        }

        private void SetError(string id, string error)
        {
            var row = _db.GetVideosBySource("https://example.test/s").Single(v => v.Id == id);
            row.ResolutionError = error;
            _db.UpsertVideo(row);
        }
    }
}

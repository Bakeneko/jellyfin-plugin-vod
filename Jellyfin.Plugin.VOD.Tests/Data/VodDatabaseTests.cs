using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
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

        [Fact]
        public void EnsureCreated_LeavesTheSchemaAtVersion6()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";

            Assert.Equal(6L, (long)cmd.ExecuteScalar()!);
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
                            is_live        INTEGER NOT NULL DEFAULT 0
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
                Assert.Equal(6L, (long)version.ExecuteScalar()!);

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
                            is_live        INTEGER NOT NULL DEFAULT 0
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
                Assert.Equal(6L, (long)version.ExecuteScalar()!);

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
                            is_live          INTEGER NOT NULL DEFAULT 0
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
                Assert.Equal(6L, (long)version.ExecuteScalar()!);
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
        /// <see cref="EnsureCreated_LeavesTheSchemaAtVersion6"/>.
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
                Assert.Equal(6L, (long)version.ExecuteScalar()!);

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
                            is_live        INTEGER NOT NULL DEFAULT 0
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
    }
}

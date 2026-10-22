using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Data
{
    /// <summary>
    /// The data layer of the orchestration rework. Written from scratch against schema 9: the file
    /// it replaces tested the model the rework removed, and most of its subjects — the resolution
    /// state, the settled rule, the per-pass sync stamp, the serialised stream descriptions, the
    /// v1-to-v8 migration chain — no longer exist.
    /// </summary>
    public sealed class VodDatabaseTests : IDisposable
    {
        private const string SourceUrl = "https://www.youtube.com/@chan/videos";
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

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

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        private void SeedSource(string url = SourceUrl)
            => _db.UpsertSource(new SourceRecord { Url = url, Name = "A channel" });

        private VideoRecord SeedVideo(
            string id,
            string? nativeId = null,
            string sourceUrl = SourceUrl,
            DateTimeOffset? publishedAt = null,
            DateTimeOffset? listedAt = null,
            LiveStatus live = LiveStatus.NotLive,
            CallOutcome metadataResult = CallOutcome.None,
            DateTimeOffset? metadataAt = null)
        {
            var video = new VideoRecord
            {
                Id = id,
                SourceUrl = sourceUrl,
                NativeId = nativeId ?? id,
                ExtractorKey = "Youtube",
                VideoUrl = "https://www.youtube.com/watch?v=" + id,
                Title = "Video " + id,
                PublishedAt = publishedAt,
                ListedAt = listedAt,
                LiveStatus = live,
                MetadataAt = metadataAt,
                MetadataResult = metadataResult,
                FirstSeenAt = Now,
            };
            _db.UpsertVideo(video);
            return video;
        }

        /// <summary>The column names of a table, read from the live schema.</summary>
        private List<string> ColumnsOf(string table)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            return columns;
        }

        private long SchemaVersion()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return (long)cmd.ExecuteScalar()!;
        }

        private void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private T Scalar<T>(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), null);
        }

        // ── Schema ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void EnsureCreated_LeavesTheSchemaAtVersion9()
            => Assert.Equal(9, SchemaVersion());

        [Fact]
        public void EnsureCreated_IsIdempotent()
        {
            _db.EnsureCreated();
            _db.EnsureCreated();

            Assert.Equal(9, SchemaVersion());
            Assert.Empty(_db.GetAllSources());
        }

        [Theory]
        [InlineData("short_listing")]
        [InlineData("full_listing")]
        [InlineData("cleanup")]
        [InlineData("wave")]
        [InlineData("checkup")]
        public void AFreshDatabase_CarriesTheTripletOfEveryStage(string stage)
        {
            var columns = ColumnsOf("sources");

            Assert.Contains(stage + "_result", columns);
            Assert.Contains(stage + "_detail", columns);
            Assert.Contains(stage + "_failures", columns);
        }

        [Fact]
        public void AFreshDatabase_CarriesTheTwoVideoTriplets_AndTheListingFields()
        {
            var columns = ColumnsOf("videos");

            Assert.Contains("metadata_at", columns);
            Assert.Contains("metadata_result", columns);
            Assert.Contains("metadata_detail", columns);
            Assert.Contains("metadata_attempts", columns);
            Assert.Contains("described_at", columns);
            Assert.Contains("describe_result", columns);
            Assert.Contains("describe_detail", columns);
            Assert.Contains("describe_attempts", columns);
            Assert.Contains("listing_title", columns);
            Assert.Contains("listed_at", columns);
            Assert.Contains("first_seen_at", columns);
        }

        [Fact]
        public void AFreshDatabase_KeepsNoColumnOfTheOldModel()
        {
            // Asserting absence, not just presence of the new: a leftover column would still be
            // read by nothing, but it would let someone reintroduce the state machine the rework
            // exists to remove without noticing the schema already disagrees with the design.
            var sources = ColumnsOf("sources");
            var videos = ColumnsOf("videos");

            foreach (var gone in new[] { "last_resolved_at", "published_mode", "last_synced_at", "last_error" })
            {
                Assert.DoesNotContain(gone, sources);
            }

            foreach (var gone in new[]
                     {
                         "resolution_state", "resolution_attempts", "resolution_attempted_at",
                         "resolution_error", "synced_at", "stream_descriptions",
                     })
            {
                Assert.DoesNotContain(gone, videos);
            }
        }

        [Fact]
        public void EnsureCreated_OnAVersion8Database_DropsItsDataAndRebuildsTheSchema()
        {
            // The only migration test left. Steps 1 to 8 were reduced to a `return` once step 9
            // began dropping both tables unconditionally, so nothing between a fresh database and
            // this one is observable any more.
            var path = Path.GetTempFileName();
            try
            {
                using (var connection = new SqliteConnection($"Data Source={path}"))
                {
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (url TEXT PRIMARY KEY);
                        CREATE TABLE videos  (id  TEXT PRIMARY KEY);
                        INSERT INTO sources (url) VALUES ('https://old.test/s');
                        INSERT INTO videos  (id)  VALUES ('old-video');
                        PRAGMA user_version = 8;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using var migrated = new VodDatabase(path, NullLogger<VodDatabase>.Instance);
                migrated.EnsureCreated();

                Assert.Empty(migrated.GetAllSources());
                Assert.Empty(migrated.GetAllVideoIds());

                using var check = new SqliteConnection($"Data Source={path}");
                check.Open();
                using var version = check.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(9L, (long)version.ExecuteScalar()!);

                using var columns = check.CreateCommand();
                columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('videos') WHERE name = 'metadata_at'";
                Assert.Equal(1L, (long)columns.ExecuteScalar()!);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void EnsureCreated_OnADatabaseFromTheFuture_SaysSo_AndChangesNothing()
        {
            // A downgrade. Nothing here can migrate backwards and every reader assumes the columns
            // it names exist, so the first MapVideo on a column a later version renamed throws with
            // no explanation. The start is not blocked — downgrading is the administrator's
            // decision — but it must not be silent.
            var path = Path.GetTempFileName();
            var log = new RecordingLogger<VodDatabase>();
            try
            {
                using (var connection = new SqliteConnection($"Data Source={path}"))
                {
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE sources (url TEXT PRIMARY KEY);
                        INSERT INTO sources (url) VALUES ('https://future.test/s');
                        PRAGMA user_version = 99;
                        """;
                    cmd.ExecuteNonQuery();
                }

                using (var db = new VodDatabase(path, log))
                    db.EnsureCreated();

                Assert.Contains(
                    log.Entries,
                    e => e.Level == LogLevel.Warning
                        && e.Message.Contains("newer plugin", StringComparison.Ordinal));

                // Untouched: no migration ran, and the version was not pushed backwards.
                using var check = new SqliteConnection($"Data Source={path}");
                check.Open();
                using var version = check.CreateCommand();
                version.CommandText = "PRAGMA user_version";
                Assert.Equal(99L, (long)version.ExecuteScalar()!);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TheUniqueNativeIdIndex_RefusesASecondRowForOneVideo()
        {
            // A listing that returns the same video twice used to mint a second uuid for it, which
            // took a second rank of its day and published the episode twice.
            SeedSource();
            SeedVideo("first", nativeId: "dupe");

            Assert.Throws<SqliteException>(() => SeedVideo("second", nativeId: "dupe"));
        }

        // ── Round-trips ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GetAllSources_OnEmpty_ReturnsEmpty()
            => Assert.Empty(_db.GetAllSources());

        [Fact]
        public void GetSource_Unknown_ReturnsNull()
            => Assert.Null(_db.GetSource("https://example.test/nope"));

        [Fact]
        public void UpsertSource_RoundTripsWhatAListingOwns()
        {
            _db.UpsertSource(new SourceRecord
            {
                Url = SourceUrl,
                Key = "youtube:chan",
                Name = "A channel",
                SeriesSynopsis = "What it is about",
                SeriesThumbnailUrl = "https://img.test/avatar.jpg",
                SeriesBannerUrl = "https://img.test/banner.jpg",
                Tags = """["science","education"]""",
                VideoCount = 42,
            });

            var source = _db.GetSource(SourceUrl)!;

            Assert.Equal("youtube:chan", source.Key);
            Assert.Equal("A channel", source.Name);
            Assert.Equal("What it is about", source.SeriesSynopsis);
            Assert.Equal("https://img.test/avatar.jpg", source.SeriesThumbnailUrl);
            Assert.Equal("https://img.test/banner.jpg", source.SeriesBannerUrl);
            Assert.Equal("""["science","education"]""", source.Tags);
            Assert.Equal(42, source.VideoCount);
        }

        [Fact]
        public void AFreshSource_HasNoListingClock_NoStageResult_AndNoCount()
        {
            SeedSource();

            var source = _db.GetSource(SourceUrl)!;

            // FullListedAt being null is what the listing selector reads as "no listing has ever
            // run", which makes the first listing a full one.
            Assert.Null(source.ShortListedAt);
            Assert.Null(source.FullListedAt);
            Assert.Null(source.ListingCount);
            Assert.Null(source.ConfigFingerprint);
            Assert.Null(source.PublishedAnchorId);

            Assert.Equal(CallOutcome.None, source.ShortListingResult);
            Assert.Equal(CallOutcome.None, source.FullListingResult);
            Assert.Equal(CallOutcome.None, source.CleanupResult);
            Assert.Equal(CallOutcome.None, source.WaveResult);
            Assert.Equal(CallOutcome.None, source.CheckupResult);
            Assert.Equal(0, source.ShortListingFailures);
            Assert.Equal(0, source.CheckupFailures);
        }

        [Fact]
        public void UpsertVideo_RoundTripsEveryField()
        {
            SeedSource();
            var published = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

            _db.UpsertVideo(new VideoRecord
            {
                Id = "v",
                SourceUrl = SourceUrl,
                NativeId = "abc123",
                ExtractorKey = "Youtube",
                VideoUrl = "https://www.youtube.com/watch?v=abc123",
                Title = "Titre localisé",
                ListingTitle = "Titre localisé",
                Description = "A synopsis",
                DurationSeconds = 1169,
                ThumbnailUrl = "https://img.test/still.jpg",
                PublishedAt = published,
                LiveStatus = LiveStatus.NotLive,
                SeasonNumber = 2026,
                EpisodeNumber = 1014001,
                ListingPosition = 3,
                FirstSeenAt = Now.AddDays(-1),
                ListedAt = Now,
                MetadataAt = Now,
                MetadataResult = CallOutcome.Ok,
                MetadataDetail = null,
                MetadataAttempts = 0,
                DescribedAt = Now,
                DescribeResult = CallOutcome.Ok,
                DescribeAttempts = 0,
                Extras = new VideoExtras
                {
                    Qualities = [new VideoQuality(1080, "h264", 30)],
                    AudioTracks = [new AudioTrack("fr", "français")],
                    SubtitleLanguages = ["fr"],
                    Tags = ["science"],
                    Categories = ["Education"],
                    Channels = ["A channel"],
                },
            });

            var video = _db.GetVideoByUuid("v")!;

            Assert.Equal("abc123", video.NativeId);
            Assert.Equal("Youtube", video.ExtractorKey);
            Assert.Equal("Titre localisé", video.Title);
            Assert.Equal("Titre localisé", video.ListingTitle);
            Assert.Equal("A synopsis", video.Description);
            Assert.Equal(1169, video.DurationSeconds);
            Assert.Equal("https://img.test/still.jpg", video.ThumbnailUrl);
            Assert.Equal(published, video.PublishedAt);
            Assert.Equal(2026, video.SeasonNumber);
            Assert.Equal(1014001, video.EpisodeNumber);
            Assert.Equal(3, video.ListingPosition);
            Assert.Equal(Now, video.ListedAt);
            Assert.Equal(Now, video.MetadataAt);
            Assert.Equal(CallOutcome.Ok, video.MetadataResult);
            Assert.Equal(Now, video.DescribedAt);
            Assert.Equal(CallOutcome.Ok, video.DescribeResult);
            Assert.Equal(1080, Assert.Single(video.Extras.Qualities).Height);
            Assert.Equal("fr", Assert.Single(video.Extras.AudioTracks).Language);
            Assert.Equal("science", Assert.Single(video.Extras.Tags));
        }

        [Fact]
        public void GetVideoByUuid_Unknown_ReturnsNull()
            => Assert.Null(_db.GetVideoByUuid("nope"));

        [Fact]
        public void FirstSeenAt_SurvivesALaterUpsertProposingAnotherValue()
        {
            // It is the date added that gets published, so it must be set once. A later upsert
            // proposing "now" would walk the whole library's date-added forward on every pass.
            SeedSource();
            var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var video = new VideoRecord
            {
                Id = "v",
                SourceUrl = SourceUrl,
                NativeId = "v",
                VideoUrl = "https://www.youtube.com/watch?v=v",
                FirstSeenAt = first,
            };
            _db.UpsertVideo(video);

            video.FirstSeenAt = Now;
            _db.UpsertVideo(video);

            Assert.Equal(first, _db.GetVideoByUuid("v")!.FirstSeenAt);
        }

        [Fact]
        public void GetVideoByUuid_OnCorruptExtras_ReturnsAnEmptyPayload()
        {
            // A hand-edited or truncated payload must not take the row down: the video is still
            // playable without knowing its tiers.
            SeedSource();
            SeedVideo("v");
            Execute("UPDATE videos SET extras = '{ not json' WHERE id = 'v'");

            Assert.Empty(_db.GetVideoByUuid("v")!.Extras.Qualities);
        }

        [Fact]
        public void GetVideoByUuid_OnAnUnknownLiveStatus_ReadsBackAsNotLive()
        {
            SeedSource();
            SeedVideo("v");
            Execute("UPDATE videos SET live_status = 'Teleporting' WHERE id = 'v'");

            Assert.Equal(LiveStatus.NotLive, _db.GetVideoByUuid("v")!.LiveStatus);
        }

        [Theory]
        [InlineData("metadata_result")]
        [InlineData("describe_result")]
        public void AnUnknownOutcome_ReadsBackAsNone_NotAsAFailure(string column)
        {
            // None, because an unknown value means a NEWER build wrote it. Reading it as "no answer
            // yet" costs one more call; reading it as any particular failure would have the
            // configuration page report a cause nothing ever observed — which is the class of
            // defect this whole enumeration exists to remove.
            SeedSource();
            SeedVideo("v");
            Execute($"UPDATE videos SET {column} = 'SomethingNewerKnows' WHERE id = 'v'");

            var video = _db.GetVideoByUuid("v")!;

            Assert.Equal(
                CallOutcome.None,
                column == "metadata_result" ? video.MetadataResult : video.DescribeResult);
        }

        // ── The upsert writes only what a listing owns ────────────────────────────────────────────

        [Fact]
        public void UpsertSource_FromAStaleRecord_DoesNotUndoWhatASetterWroteSince()
        {
            // THE test of this file. The old model carried every field in one upsert, so a caller
            // holding a record loaded earlier in the pass wrote its stale copy back over a value
            // written since — which is how a mid-pass clear was lost. Every clock, every triplet,
            // the fingerprint and the anchor therefore have their own setter, and the upsert must
            // leave all of them alone.
            SeedSource();
            var stale = _db.GetSource(SourceUrl)!;

            var anchor = Guid.NewGuid();
            _db.SetListingClock(SourceUrl, full: true, Now);
            _db.SetListingCount(SourceUrl, 111);
            _db.SetConfigFingerprint(SourceUrl, "fp-1");
            _db.SetPublishedAnchorId(SourceUrl, anchor);
            _db.SetSourcePublishedImageUrl(SourceUrl, "https://img.test/published.jpg");
            _db.SetSourcePublishedBannerUrl(SourceUrl, "https://img.test/banner.jpg");
            _db.SetStageResult(SourceUrl, SourceStage.Wave, CallOutcome.NeedsAuth, "sign in");

            // The listing finishes and writes what it owns, from the record it loaded first.
            stale.Name = "A renamed channel";
            stale.VideoCount = 7;
            _db.UpsertSource(stale);

            var source = _db.GetSource(SourceUrl)!;

            Assert.Equal("A renamed channel", source.Name);
            Assert.Equal(7, source.VideoCount);

            Assert.Equal(Now, source.FullListedAt);
            Assert.Equal(Now, source.ShortListedAt);
            Assert.Equal(111, source.ListingCount);
            Assert.Equal("fp-1", source.ConfigFingerprint);
            Assert.Equal(anchor, source.PublishedAnchorId);
            Assert.Equal("https://img.test/published.jpg", source.PublishedImageUrl);
            Assert.Equal("https://img.test/banner.jpg", source.PublishedBannerUrl);
            Assert.Equal(CallOutcome.NeedsAuth, source.WaveResult);
            Assert.Equal("sign in", source.WaveDetail);
            Assert.Equal(1, source.WaveFailures);
        }

        [Fact]
        public void UpsertVideo_LeavesThePublishedImageUrlAlone()
        {
            // Jellyfin localises an image on first display and forgets its origin, so this column
            // is the only record of what was actually posted. An upsert overwriting it would
            // re-point every item's artwork on the next pass.
            SeedSource();
            SeedVideo("v");
            _db.SetPublishedImageUrl("v", "https://img.test/posted.jpg");

            var reloaded = _db.GetVideoByUuid("v")!;
            reloaded.Title = "A new title";
            _db.UpsertVideo(reloaded);

            Assert.Equal("https://img.test/posted.jpg", _db.GetVideoByUuid("v")!.PublishedImageUrl);
        }

        // ── Consecutive failure counters ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData(SourceStage.ShortListing)]
        [InlineData(SourceStage.FullListing)]
        [InlineData(SourceStage.Cleanup)]
        [InlineData(SourceStage.Wave)]
        [InlineData(SourceStage.Checkup)]
        public void SetStageResult_CountsConsecutiveFailures_AndASuccessZeroesThem(SourceStage stage)
        {
            // Every stage, because the five SQL statements are written out by hand: a typo in one
            // column name would leave exactly one stage silently never counting.
            SeedSource();

            _db.SetStageResult(SourceUrl, stage, CallOutcome.Timeout, "timed out");
            _db.SetStageResult(SourceUrl, stage, CallOutcome.Timeout, "timed out again");

            Assert.Equal(2, FailuresOf(stage));
            Assert.Equal(CallOutcome.Timeout, ResultOf(stage));
            Assert.Equal("timed out again", DetailOf(stage));

            _db.SetStageResult(SourceUrl, stage, CallOutcome.Ok, null);

            Assert.Equal(0, FailuresOf(stage));
            Assert.Equal(CallOutcome.Ok, ResultOf(stage));
            Assert.Null(DetailOf(stage));
        }

        [Fact]
        public void SetStageResult_OnASourceNoListingHasEverCreated_StillRecordsTheFailure()
        {
            // No SeedSource: the row is created by a listing that SUCCEEDS, so a source whose URL
            // is mistyped has none, and the first thing it ever produces is a failure. An UPDATE
            // that matches nothing drops it, and the configuration page then has nothing at all to
            // show for the one source the administrator needs explained.
            _db.SetStageResult(SourceUrl, SourceStage.FullListing, CallOutcome.ExtractorError, "no such channel");

            var record = _db.GetSource(SourceUrl);

            Assert.NotNull(record);
            Assert.Equal(CallOutcome.ExtractorError, record.FullListingResult);
            Assert.Equal("no such channel", record.FullListingDetail);
            Assert.Equal(1, record.FullListingFailures);

            // Still never listed: the row carries the diagnosis, not a claim that a listing ran.
            // A clock here would take the source out of the selection and strand it.
            Assert.Null(record.FullListedAt);
            Assert.Null(record.ShortListedAt);
            Assert.Null(record.Key);
        }

        [Fact]
        public void SetStageResult_TruncatesALongDetail()
        {
            // yt-dlp can produce hundreds of characters. Unbounded, the column would multiply a
            // pathological answer across every source row.
            SeedSource();

            _db.SetStageResult(SourceUrl, SourceStage.Wave, CallOutcome.ExtractorError, new string('x', 900));

            Assert.Equal(500, _db.GetSource(SourceUrl)!.WaveDetail!.Length);
        }

        [Fact]
        public void SetMetadataOutcome_CountsConsecutiveFailures_AndASuccessZeroesThem()
        {
            SeedSource();
            SeedVideo("v");

            _db.SetMetadataOutcome("v", CallOutcome.NeedsAuth, "sign in", Now, countFailure: true);
            _db.SetMetadataOutcome("v", CallOutcome.NeedsAuth, "sign in", Now, countFailure: true);

            var failed = _db.GetVideoByUuid("v")!;
            Assert.Equal(2, failed.MetadataAttempts);
            Assert.Equal(CallOutcome.NeedsAuth, failed.MetadataResult);
            Assert.Equal("sign in", failed.MetadataDetail);

            _db.SetMetadataOutcome("v", CallOutcome.Ok, null, Now, countFailure: true);

            var ok = _db.GetVideoByUuid("v")!;
            Assert.Equal(0, ok.MetadataAttempts);
            Assert.Equal(CallOutcome.Ok, ok.MetadataResult);
            Assert.Null(ok.MetadataDetail);
        }

        [Fact]
        public void SetMetadataOutcome_WithCountFailureFalse_RecordsTheFailureWithoutAdvancingTheBackoff()
        {
            // The rate-limit case: the PLATFORM failed, not the video. Counting it would push every
            // video of a source a step further out for something none of them did, and one window
            // would set the whole source back by hours.
            SeedSource();
            SeedVideo("v");

            _db.SetMetadataOutcome("v", CallOutcome.RateLimited, "429", Now, countFailure: false);

            var video = _db.GetVideoByUuid("v")!;
            Assert.Equal(CallOutcome.RateLimited, video.MetadataResult);
            Assert.Equal("429", video.MetadataDetail);
            Assert.Equal(Now, video.MetadataAt);
            Assert.Equal(0, video.MetadataAttempts);
        }

        [Fact]
        public void SetMetadataOutcome_AdvancesTheClock_EvenOnAFailure()
        {
            // Otherwise the wave would pick the same video again on the very next turn of the loop,
            // for ever, and never reach the rest of the source.
            SeedSource();
            SeedVideo("v");

            _db.SetMetadataOutcome("v", CallOutcome.Unavailable, "gone", Now, countFailure: true);

            Assert.Equal(Now, _db.GetVideoByUuid("v")!.MetadataAt);
        }

        [Fact]
        public void SetDescribeOutcome_CountsItsOwnFailures_AndNeverTouchesTheMetadataTriplet()
        {
            // The two clocks are independent on purpose: a failing ffprobe must not make the pass
            // spend another yt-dlp call.
            SeedSource();
            SeedVideo("v");
            _db.SetMetadataOutcome("v", CallOutcome.Ok, null, Now, countFailure: true);

            _db.SetDescribeOutcome("v", CallOutcome.ProbeFailed, "ffprobe died", Now);
            _db.SetDescribeOutcome("v", CallOutcome.ProbeFailed, "ffprobe died", Now);

            var video = _db.GetVideoByUuid("v")!;
            Assert.Equal(2, video.DescribeAttempts);
            Assert.Equal(CallOutcome.ProbeFailed, video.DescribeResult);
            Assert.Equal(Now, video.DescribedAt);

            Assert.Equal(CallOutcome.Ok, video.MetadataResult);
            Assert.Equal(0, video.MetadataAttempts);
            Assert.Null(video.MetadataDetail);

            _db.SetDescribeOutcome("v", CallOutcome.Ok, null, Now);
            Assert.Equal(0, _db.GetVideoByUuid("v")!.DescribeAttempts);
        }

        [Fact]
        public void SetDescribeOutcome_None_StampsTheClock_AndCountsNoFailure()
        {
            // None means the describing looked and concluded there is nothing to describe: no HLS
            // master, or no quality tier. Nothing failed, so nothing is counted — the clock alone
            // is what takes the video out of the due selection, and the detail says why.
            //
            // The counter matters even though it drives no backoff: it is what the source detail
            // page shows. Counting a conclusion as a failure would put a climbing failure count on
            // several hundred perfectly ordinary videos — six percent of YouTube's, measured — and
            // send an administrator hunting for a breakage that does not exist.
            SeedSource();
            SeedVideo("v");
            _db.SetDescribeOutcome("v", CallOutcome.ProbeFailed, "ffprobe died", Now);

            _db.SetDescribeOutcome("v", CallOutcome.None, "describe: the platform offered no HLS manifest", Now);

            var video = _db.GetVideoByUuid("v")!;
            Assert.Equal(Now, video.DescribedAt);
            Assert.Equal(CallOutcome.None, video.DescribeResult);
            Assert.Equal("describe: the platform offered no HLS manifest", video.DescribeDetail);
            Assert.Equal(0, video.DescribeAttempts);
        }

        [Fact]
        public void ClearDescribedAt_MakesAVideoDueForDescribing_WithoutTouchingItsMetadata()
        {
            // Called by the checkup for an item it just created with no description in hand. A
            // description no longer outlives the pass that made it, so without this the item would
            // stay undescribed until the freshness clock expired — and Jellyfin decides an
            // undescribed tier's codec blind, which means transcoding.
            SeedSource();
            SeedVideo("v");
            _db.SetMetadataOutcome("v", CallOutcome.Ok, null, Now, countFailure: true);
            _db.SetDescribeOutcome("v", CallOutcome.ProbeFailed, "ffprobe died", Now);

            _db.ClearDescribedAt("v");

            var video = _db.GetVideoByUuid("v")!;
            Assert.Null(video.DescribedAt);
            Assert.Equal(CallOutcome.None, video.DescribeResult);
            Assert.Null(video.DescribeDetail);
            Assert.Equal(0, video.DescribeAttempts);

            Assert.Equal(Now, video.MetadataAt);
            Assert.Equal(CallOutcome.Ok, video.MetadataResult);
        }

        [Fact]
        public void MarkMetadataStale_SetsMetadataAtToUnixEpoch_NotNull()
        {
            // The retitle path calls MarkMetadataStale to schedule a metadata refetch without
            // un-publishing the video. Setting metadata_at to NULL would make Publishability
            // return NeverFetched and the checkup would remove the Jellyfin item — destroying
            // watch state. UnixEpoch is non-null, older than any freshness window, and therefore
            // makes the video due for a refetch on the next wave while keeping it published.
            SeedSource();
            SeedVideo("v");
            _db.SetMetadataOutcome("v", CallOutcome.Ok, null, Now, countFailure: false);

            var before = _db.GetVideoByUuid("v")!;
            Assert.Equal(Now, before.MetadataAt);

            _db.MarkMetadataStale("v");

            var after = _db.GetVideoByUuid("v")!;
            Assert.NotNull(after.MetadataAt);
            Assert.Equal(DateTimeOffset.UnixEpoch, after.MetadataAt);
        }

        private int FailuresOf(SourceStage stage)
        {
            var source = _db.GetSource(SourceUrl)!;
            return stage switch
            {
                SourceStage.ShortListing => source.ShortListingFailures,
                SourceStage.FullListing => source.FullListingFailures,
                SourceStage.Cleanup => source.CleanupFailures,
                SourceStage.Wave => source.WaveFailures,
                _ => source.CheckupFailures,
            };
        }

        private CallOutcome ResultOf(SourceStage stage)
        {
            var source = _db.GetSource(SourceUrl)!;
            return stage switch
            {
                SourceStage.ShortListing => source.ShortListingResult,
                SourceStage.FullListing => source.FullListingResult,
                SourceStage.Cleanup => source.CleanupResult,
                SourceStage.Wave => source.WaveResult,
                _ => source.CheckupResult,
            };
        }

        private string? DetailOf(SourceStage stage)
        {
            var source = _db.GetSource(SourceUrl)!;
            return stage switch
            {
                SourceStage.ShortListing => source.ShortListingDetail,
                SourceStage.FullListing => source.FullListingDetail,
                SourceStage.Cleanup => source.CleanupDetail,
                SourceStage.Wave => source.WaveDetail,
                _ => source.CheckupDetail,
            };
        }

        // ── Clocks, count, fingerprint ────────────────────────────────────────────────────────────

        [Fact]
        public void SetListingClock_Short_WritesOnlyTheShortClock()
        {
            SeedSource();

            _db.SetListingClock(SourceUrl, full: false, Now);

            var source = _db.GetSource(SourceUrl)!;
            Assert.Equal(Now, source.ShortListedAt);
            Assert.Null(source.FullListedAt);
        }

        [Fact]
        public void SetListingClock_Full_WritesBothClocks()
        {
            // A full listing has just seen everything a short one would have, so leaving the short
            // clock behind would make the source due for a redundant listing on the next pass.
            SeedSource();

            _db.SetListingClock(SourceUrl, full: true, Now);

            var source = _db.GetSource(SourceUrl)!;
            Assert.Equal(Now, source.FullListedAt);
            Assert.Equal(Now, source.ShortListedAt);
        }

        [Fact]
        public void ClearFullListedAt_MakesAFullListingDue_AndLeavesTheShortClock()
        {
            // The single response to a configuration change. The short clock stays so the source
            // does not also become due for a short listing it does not need.
            SeedSource();
            _db.SetListingClock(SourceUrl, full: true, Now);

            _db.ClearFullListedAt(SourceUrl);

            var source = _db.GetSource(SourceUrl)!;
            Assert.Null(source.FullListedAt);
            Assert.Equal(Now, source.ShortListedAt);
        }

        [Fact]
        public void SetListingCount_Null_StaysNull_AndDoesNotBecomeZero()
        {
            // Only playlists report a count; a channel does not. Zero would mean "the source is
            // empty", which the cleanup would read as every video having been deleted.
            SeedSource();
            _db.SetListingCount(SourceUrl, 111);

            _db.SetListingCount(SourceUrl, null);

            Assert.Null(_db.GetSource(SourceUrl)!.ListingCount);
        }

        [Fact]
        public void SetConfigFingerprint_AndSetPublishedAnchorId_RoundTripIncludingNull()
        {
            SeedSource();
            var anchor = Guid.NewGuid();

            _db.SetConfigFingerprint(SourceUrl, "fp-1");
            _db.SetPublishedAnchorId(SourceUrl, anchor);

            var written = _db.GetSource(SourceUrl)!;
            Assert.Equal("fp-1", written.ConfigFingerprint);
            Assert.Equal(anchor, written.PublishedAnchorId);

            _db.SetConfigFingerprint(SourceUrl, null);
            _db.SetPublishedAnchorId(SourceUrl, null);

            var cleared = _db.GetSource(SourceUrl)!;
            Assert.Null(cleared.ConfigFingerprint);
            Assert.Null(cleared.PublishedAnchorId);
        }

        // ── Queries ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GetVideoIdsWithMetadata_TakesTheSuccessfullyFetchedRowsOfEverySource()
        {
            SeedSource();
            SeedSource("https://www.youtube.com/@other/videos");

            SeedVideo("ok-here", metadataAt: Now, metadataResult: CallOutcome.Ok);
            SeedVideo("ok-there", sourceUrl: "https://www.youtube.com/@other/videos",
                metadataAt: Now, metadataResult: CallOutcome.Ok);
            SeedVideo("never-fetched");
            SeedVideo("failed", metadataAt: Now, metadataResult: CallOutcome.NeedsAuth);

            var ids = _db.GetVideoIdsWithMetadata();

            Assert.Equal(["ok-here", "ok-there"], ids.Order());
        }

        [Fact]
        public void GetVideoIdsWithMetadata_IgnoresRetention_Deliberately()
        {
            // Its job is to PROTECT a library item from the orphan sweep, not to select what to
            // publish. Wider is the safe direction: an item is never wrongly reaped, where a
            // narrower set would reap items another source still wants. Retention and the
            // offered-tier check are per-source publication filters the owning checkup applies.
            SeedSource();
            SeedVideo("ancient", publishedAt: new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
                metadataAt: Now, metadataResult: CallOutcome.Ok);

            Assert.Contains("ancient", _db.GetVideoIdsWithMetadata());
        }

        [Fact]
        public void GetVideosGoneFromLastFullListing_TakesWhatTheListingDidNotCarry()
        {
            SeedSource();
            SeedVideo("seen", listedAt: Now);
            SeedVideo("stale", listedAt: Now.AddDays(-1));
            SeedVideo("never-listed");

            var gone = _db.GetVideosGoneFromLastFullListing(SourceUrl, Now);

            Assert.Equal(["never-listed", "stale"], gone.Order());
        }

        [Theory]
        [InlineData(LiveStatus.Live)]
        [InlineData(LiveStatus.Upcoming)]
        public void GetVideosGoneFromLastFullListing_SparesAFreshBroadcast(LiveStatus status)
        {
            // The probe publishes ids no listing carries — the stream id of a subscriber-only
            // channel, a different id per pass for a live-only source — so a sweep on listed_at
            // alone deleted the row, and with it the library item, mid-broadcast.
            SeedSource();
            SeedVideo("airing", listedAt: Now.AddMinutes(-5), live: status);

            Assert.Empty(_db.GetVideosGoneFromLastFullListing(SourceUrl, Now));
        }

        [Theory]
        [InlineData(LiveStatus.Live)]
        [InlineData(LiveStatus.Upcoming)]
        public void GetVideosGoneFromLastFullListing_SweepsAStaleBroadcast(LiveStatus status)
        {
            // The exemption is bounded, because one such row can never return to NotLive: its URL
            // is the channel root, which yt-dlp answers for only while the channel is live. An
            // unbounded exemption left one phantom per broadcast, accumulating. Freshness separates
            // the two: a live row is rewritten on every probe pass; a stuck one stops being
            // written the moment its re-fetch starts failing.
            SeedSource();
            SeedVideo("stuck", listedAt: Now.AddDays(-8), live: status);

            Assert.Equal("stuck", Assert.Single(_db.GetVideosGoneFromLastFullListing(SourceUrl, Now)));
        }

        [Fact]
        public void GetVideosGoneFromLastFullListing_SweepsAFinishedBroadcast()
        {
            SeedSource();
            SeedVideo("ended", listedAt: Now.AddDays(-1), live: LiveStatus.NotLive);

            Assert.Equal("ended", Assert.Single(_db.GetVideosGoneFromLastFullListing(SourceUrl, Now)));
        }

        [Fact]
        public void GetVideosGoneFromLastFullListing_DoesNotEnforceRetention()
        {
            // Retention is a publication filter, not an erase rule. Deleting a row that is still in
            // the listing would have it rediscovered and refetched on the next pass — the exact
            // call loop this design exists to remove.
            SeedSource();
            SeedVideo("old-but-listed",
                publishedAt: new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
                listedAt: Now);

            Assert.Empty(_db.GetVideosGoneFromLastFullListing(SourceUrl, Now));
        }

        [Fact]
        public void TouchListedAt_MovesOnlyTheIdsItIsGiven()
        {
            SeedSource();
            SeedVideo("a", listedAt: Now.AddDays(-1));
            SeedVideo("b", listedAt: Now.AddDays(-1));

            _db.TouchListedAt(SourceUrl, ["a"], Now);

            Assert.Equal(Now, _db.GetVideoByUuid("a")!.ListedAt);
            Assert.Equal(Now.AddDays(-1), _db.GetVideoByUuid("b")!.ListedAt);
        }

        [Fact]
        public void SetListingPositions_RanksEveryRowItIsGiven()
        {
            // Written for every row the listing carries, including ones the pass will not fetch:
            // the rank is the only order available for a video that has never been fetched and so
            // has no date.
            SeedSource();
            SeedVideo("newest");
            SeedVideo("older");

            _db.SetListingPositions(SourceUrl, new Dictionary<string, int> { ["newest"] = 0, ["older"] = 1 });

            Assert.Equal(0, _db.GetVideoByUuid("newest")!.ListingPosition);
            Assert.Equal(1, _db.GetVideoByUuid("older")!.ListingPosition);
        }

        [Fact]
        public void GetVideosBySource_ReturnsOnlyTheSourceAsked()
        {
            SeedSource();
            SeedSource("https://www.youtube.com/@other/videos");
            SeedVideo("mine");
            SeedVideo("theirs", sourceUrl: "https://www.youtube.com/@other/videos");

            Assert.Equal("mine", Assert.Single(_db.GetVideosBySource(SourceUrl)).Id);
        }


        [Fact]
        public void GetOutcomeCounts_CountsByResult_AndLeavesOutWhatHasNoRow()
        {
            SeedSource();
            SeedVideo("a", metadataAt: Now, metadataResult: CallOutcome.Ok);
            SeedVideo("b", metadataAt: Now, metadataResult: CallOutcome.Ok);
            SeedVideo("c", metadataAt: Now, metadataResult: CallOutcome.NeedsAuth);

            var counts = _db.GetOutcomeCounts();

            Assert.Equal(2, counts[CallOutcome.Ok]);
            Assert.Equal(1, counts[CallOutcome.NeedsAuth]);

            // Absent rather than zero: the authentication warning fires on "at least one", and a
            // zero entry would make an absent cause look observed.
            Assert.DoesNotContain(CallOutcome.GeoBlocked, counts.Keys);
        }

        [Fact]
        public void GetOutcomeCounts_SkipsAnUnreadableValue()
        {
            SeedSource();
            SeedVideo("a", metadataAt: Now, metadataResult: CallOutcome.Ok);
            SeedVideo("b", metadataAt: Now, metadataResult: CallOutcome.Ok);
            Execute("UPDATE videos SET metadata_result = 'SomethingNewerKnows' WHERE id = 'b'");

            var counts = _db.GetOutcomeCounts();

            Assert.Equal(1, counts[CallOutcome.Ok]);
            Assert.Single(counts);
        }

        // ── Deletion and reset ───────────────────────────────────────────────────────────────────

        [Fact]
        public void DeleteSource_RemovesTheSourceAndItsVideos()
        {
            SeedSource();
            SeedSource("https://www.youtube.com/@other/videos");
            SeedVideo("mine");
            SeedVideo("theirs", sourceUrl: "https://www.youtube.com/@other/videos");

            _db.DeleteSource(SourceUrl);

            Assert.Null(_db.GetSource(SourceUrl));
            Assert.Null(_db.GetVideoByUuid("mine"));
            Assert.NotNull(_db.GetVideoByUuid("theirs"));
        }

        [Fact]
        public void DeleteSource_IsSilentOnAnUnknownUrl()
            => _db.DeleteSource("https://example.test/never-configured");

        [Fact]
        public void DeleteVideo_RemovesTheRow()
        {
            SeedSource();
            SeedVideo("v");

            _db.DeleteVideo("v");

            Assert.Null(_db.GetVideoByUuid("v"));
        }

        [Fact]
        public void ResetVideo_ClearsBothTripletsOfThatVideoOnly()
        {
            // The manual way out when the external cause is known fixed — re-exported cookies,
            // typically. Clocks AND counters, so the video is eligible on the very next wave
            // instead of serving out a backoff earned under a cause that is gone.
            SeedSource();
            SeedVideo("target");
            SeedVideo("bystander");
            foreach (var id in new[] { "target", "bystander" })
            {
                _db.SetMetadataOutcome(id, CallOutcome.NeedsAuth, "sign in", Now, countFailure: true);
                _db.SetDescribeOutcome(id, CallOutcome.ProbeFailed, "ffprobe died", Now);
            }

            _db.ResetVideo("target");

            var target = _db.GetVideoByUuid("target")!;
            Assert.Null(target.MetadataAt);
            Assert.Equal(CallOutcome.None, target.MetadataResult);
            Assert.Null(target.MetadataDetail);
            Assert.Equal(0, target.MetadataAttempts);
            Assert.Null(target.DescribedAt);
            Assert.Equal(CallOutcome.None, target.DescribeResult);
            Assert.Equal(0, target.DescribeAttempts);

            var bystander = _db.GetVideoByUuid("bystander")!;
            Assert.Equal(CallOutcome.NeedsAuth, bystander.MetadataResult);
            Assert.Equal(1, bystander.MetadataAttempts);
        }

        // ── GetDueVideos ─────────────────────────────────────────────────────────────────────────

        // Common parameters used across all GetDueVideos tests.
        private const int RetryMinutes = 15;
        private const int MetadataMaxAgeDays = 30;
        private const int RetentionDays = 365;

        private VideoRecord SeedDueVideo(
            string id,
            int? listingPosition = null,
            LiveStatus live = LiveStatus.NotLive,
            DateTimeOffset? publishedAt = null,
            DateTimeOffset? metadataAt = null,
            int metadataAttempts = 0,
            DateTimeOffset? describedAt = null,
            int describeAttempts = 0,
            string sourceUrl = SourceUrl)
        {
            var video = new VideoRecord
            {
                Id = id,
                SourceUrl = sourceUrl,
                NativeId = id,
                VideoUrl = "https://example.test/v/" + id,
                LiveStatus = live,
                PublishedAt = publishedAt,
                ListingPosition = listingPosition,
                MetadataAt = metadataAt,
                MetadataAttempts = metadataAttempts,
                DescribedAt = describedAt,
                DescribeAttempts = describeAttempts,
                FirstSeenAt = Now,
            };
            _db.UpsertVideo(video);
            return video;
        }

        [Fact]
        public void GetDueVideos_Condition1_NullMetadataAt_Selects()
        {
            // A video that has never had a metadata fetch is always due regardless of
            // any other field, including described_at and live_status.
            SeedSource();
            SeedDueVideo("v", metadataAt: null);

            var result = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100);

            Assert.Equal("v", Assert.Single(result).Id);
        }

        [Fact]
        public void GetDueVideos_Condition2_StaleMetadataAt_Selects()
        {
            // A video whose metadata_at is older than the configured max age is due for a
            // refresh even when described_at is already set.
            SeedSource();
            SeedDueVideo("v",
                metadataAt: Now.AddDays(-(MetadataMaxAgeDays + 1)),
                describedAt: Now);  // described_at set, but that doesn't help: metadata is stale

            var result = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100);

            Assert.Equal("v", Assert.Single(result).Id);
        }

        [Fact]
        public void GetDueVideos_Condition3_NullDescribedAt_NotLive_InRetention_Selects()
        {
            // A video that has fresh metadata but has never been stream-described is due when
            // it is NotLive and inside the retention window.
            SeedSource();
            SeedDueVideo("v",
                metadataAt: Now.AddDays(-1),
                publishedAt: Now.AddDays(-7));

            var result = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100);

            Assert.Equal("v", Assert.Single(result).Id);
        }

        [Theory]
        [InlineData(LiveStatus.Live)]
        [InlineData(LiveStatus.Upcoming)]
        public void GetDueVideos_Condition3_LiveVideo_IsNotSelected(LiveStatus status)
        {
            // A live or upcoming video is excluded from condition 3 at selection time, not at
            // execution time. Selecting it only to skip it would burn a yt-dlp call on every
            // wave — the exact tight loop this rework was built to prevent.
            SeedSource();
            SeedDueVideo("v",
                live: status,
                metadataAt: Now.AddDays(-1),  // metadata is fresh → condition 2 does not apply
                publishedAt: Now.AddDays(-7));

            Assert.Empty(_db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100));
        }

        [Fact]
        public void GetDueVideos_Condition3_VideoOutsideRetention_IsNotSelected()
        {
            // A video outside the retention window is excluded from condition 3. It has fresh
            // metadata, so condition 2 does not fire either.
            SeedSource();
            SeedDueVideo("v",
                metadataAt: Now.AddDays(-1),
                publishedAt: Now.AddDays(-(RetentionDays + 1)));

            Assert.Empty(_db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100));
        }

        [Fact]
        public void GetDueVideos_Condition1_OutsideRetention_IsStillSelected()
        {
            // Retention only gates condition 3 (the describe condition). It does not prevent
            // a video that has never had a metadata fetch from being selected. Without this, a
            // video discovered outside the window could never be refetched should it re-enter it.
            SeedSource();
            SeedDueVideo("v",
                metadataAt: null,
                publishedAt: Now.AddDays(-(RetentionDays + 1)));

            var result = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100);

            Assert.Equal("v", Assert.Single(result).Id);
        }

        [Fact]
        public void GetDueVideos_ZeroRetentionDays_SelectsAVideoFromAnyYear()
        {
            // retentionDays = 0 means unlimited: a video published in 2019 must still pass
            // condition 3.
            SeedSource();
            SeedDueVideo("v",
                metadataAt: Now.AddDays(-1),
                publishedAt: new DateTimeOffset(2019, 6, 1, 0, 0, 0, TimeSpan.Zero));

            var result = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, retentionDays: 0, 100);

            Assert.Equal("v", Assert.Single(result).Id);
        }

        [Fact]
        public void GetDueVideos_VideoUpToDateOnBothClocks_IsNotSelected()
        {
            // A video whose metadata is fresh and whose stream description is present is
            // not due for any of the three conditions and must not appear in the wave.
            SeedSource();
            SeedDueVideo("v",
                metadataAt: Now.AddHours(-1),     // fresh
                describedAt: Now.AddHours(-1),    // described
                publishedAt: Now.AddDays(-7));

            Assert.Empty(_db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100));
        }

        [Fact]
        public void GetDueVideos_Order_PositionAscending_NullsLast()
        {
            // Seeded in an order that is opposite to the expected result so the test would
            // fail without an ORDER BY: null first (DESC), then 5, 2, 0.
            SeedSource();
            SeedDueVideo("null-pos");                        // listing_position = null
            SeedDueVideo("pos-5", listingPosition: 5);
            SeedDueVideo("pos-2", listingPosition: 2);
            SeedDueVideo("pos-0", listingPosition: 0);

            var ids = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100)
                         .Select(v => v.Id)
                         .ToList();

            Assert.Equal(["pos-0", "pos-2", "pos-5", "null-pos"], ids);
        }

        [Fact]
        public void GetDueVideos_Limit_KeepsFirstByOrder_NotFirstInserted()
        {
            // Seeded with position 5 first so that a query without ORDER BY would return the
            // wrong two rows. The limit must respect the sort, keeping position 0 and 2.
            SeedSource();
            SeedDueVideo("pos-5", listingPosition: 5);
            SeedDueVideo("pos-2", listingPosition: 2);
            SeedDueVideo("pos-0", listingPosition: 0);
            SeedDueVideo("null-pos");

            var ids = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, limit: 2)
                         .Select(v => v.Id)
                         .ToList();

            Assert.Equal(["pos-0", "pos-2"], ids);
        }

        [Fact]
        public void GetDueVideos_StopTest_Limit1_ReturnsSingleRow_WhenWorkExists()
        {
            // The loop's stop test: limit=1 on a source with work yields one row. If it
            // returned empty the pass would stop prematurely; if it diverged from the full
            // selection by seeing work this query would never return, the pass would loop forever.
            SeedSource();
            SeedDueVideo("v");

            var result = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, limit: 1);

            Assert.Single(result);
        }

        [Fact]
        public void GetDueVideos_StopTest_Limit1_ReturnsEmpty_WhenNoWork()
        {
            // The loop's stop test negative case: limit=1 on a fully up-to-date source yields
            // nothing, which is the signal for the pass to stop.
            SeedSource();
            SeedDueVideo("v",
                metadataAt: Now.AddHours(-1),
                describedAt: Now.AddHours(-1),
                publishedAt: Now.AddDays(-7));

            Assert.Empty(_db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, limit: 1));
        }

        [Fact]
        public void GetDueVideos_Backoff_VideoNotReturnedWhileInBackoff_ThenReturnedAfterExpiry()
        {
            // A video with metadata failures must not re-appear in the wave until its
            // exponential backoff has elapsed. Without this the orchestration would retry a
            // failing video on every single wave, spending a yt-dlp call per turn forever.
            //
            // metadataMaxAgeDays = 0 makes every non-null metadata_at immediately stale, which
            // ensures condition 2 fires even for a MetadataAt set seconds ago. This lets us
            // place MetadataAt inside the backoff window without making the test depend on
            // clock arithmetic spanning 30 days.
            SeedSource();
            _db.UpsertVideo(new VideoRecord
            {
                Id = "v",
                SourceUrl = SourceUrl,
                NativeId = "v",
                VideoUrl = "https://example.test/v/v",
                MetadataAt = Now.AddMinutes(-5),   // 5 minutes ago — stale (maxAge = 0)
                MetadataAttempts = 1,              // 1 failure → 15-minute backoff
                FirstSeenAt = Now,
            });

            // 5 minutes into a 15-minute backoff: not yet due.
            var duringBackoff = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, metadataMaxAgeDays: 0, RetentionDays, 100);
            Assert.DoesNotContain(duringBackoff, v => v.Id == "v");

            // 16 minutes after the attempt: backoff of 15 minutes has elapsed.
            var afterExpiry = _db.GetDueVideos(
                SourceUrl,
                Now.AddMinutes(11),   // Now + 11 min > MetadataAt + 15 min (= Now + 10 min)
                RetryMinutes,
                metadataMaxAgeDays: 0,
                RetentionDays,
                100);
            Assert.Contains(afterExpiry, v => v.Id == "v");
        }

        [Fact]
        public void GetDueVideos_FailedDescription_NotSelectedWhileMetadataFresh_ThenSelectedOnceStale()
        {
            // A failed description sets described_at unconditionally (SetDescribeOutcome), which
            // removes the video from condition 3 entirely. It does not return to the wave on its
            // own — it waits for the metadata clock to expire. This is deliberate and bounded by
            // MetadataMaxAgeDays (30 days here). Playback's own failsafe describes the video if
            // someone plays it in the meantime, so the cost of waiting is at worst one transcoded
            // first play, not a broken item.
            SeedSource();
            var video = new VideoRecord
            {
                Id = "probe-failed",
                SourceUrl = SourceUrl,
                NativeId = "probe-failed",
                VideoUrl = "https://example.test/v/probe-failed",
                MetadataAt = Now.AddHours(-1),               // fresh: condition 2 does not apply
                MetadataAttempts = 0,
                DescribedAt = Now.AddHours(-1),              // set by a failed describe attempt
                DescribeAttempts = 1,
                DescribeResult = CallOutcome.ProbeFailed,
                LiveStatus = LiveStatus.NotLive,
                PublishedAt = Now.AddDays(-7),
                FirstSeenAt = Now,
            };
            _db.UpsertVideo(video);

            // With fresh metadata and described_at set: none of the three conditions fires.
            Assert.Empty(_db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100));

            // Once the metadata clock expires, condition 2 fires and the video is selected.
            var nowStale = Now.AddDays(MetadataMaxAgeDays + 1);
            var result = _db.GetDueVideos(SourceUrl, nowStale, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100);
            Assert.Equal("probe-failed", Assert.Single(result).Id);
        }

        [Fact]
        public void GetDueVideos_AnotherSource_IsNeverReturned()
        {
            // Videos belonging to a different source must never appear in the selection,
            // regardless of how due they are.
            const string otherUrl = "https://example.test/other";
            SeedSource();
            SeedSource(otherUrl);
            SeedDueVideo("mine");
            SeedDueVideo("theirs", sourceUrl: otherUrl);

            var result = _db.GetDueVideos(SourceUrl, Now, RetryMinutes, MetadataMaxAgeDays, RetentionDays, 100);

            Assert.All(result, v => Assert.Equal(SourceUrl, v.SourceUrl));
            Assert.DoesNotContain(result, v => v.Id == "theirs");
        }

        [Fact]
        public void ResetSource_ClearsItsClocks_ItsFiveStageCounters_AndAllItsVideos()
        {
            // The stage counters go too: without them the source would still carry the failure
            // count that made it look broken while its videos were already eligible again — the
            // manual way out would only work by half.
            const string other = "https://www.youtube.com/@other/videos";
            SeedSource();
            SeedSource(other);
            SeedVideo("mine");
            SeedVideo("theirs", sourceUrl: other);

            foreach (var url in new[] { SourceUrl, other })
            {
                _db.SetListingClock(url, full: true, Now);
                foreach (var stage in Enum.GetValues<SourceStage>())
                {
                    _db.SetStageResult(url, stage, CallOutcome.Timeout, "timed out");
                }
            }

            _db.SetMetadataOutcome("mine", CallOutcome.NeedsAuth, "sign in", Now, countFailure: true);
            _db.SetMetadataOutcome("theirs", CallOutcome.NeedsAuth, "sign in", Now, countFailure: true);

            _db.ResetSource(SourceUrl);

            var source = _db.GetSource(SourceUrl)!;
            Assert.Null(source.ShortListedAt);
            Assert.Null(source.FullListedAt);
            foreach (var stage in Enum.GetValues<SourceStage>())
            {
                Assert.Equal(0, FailuresOf(stage));
                Assert.Equal(CallOutcome.None, ResultOf(stage));
                Assert.Null(DetailOf(stage));
            }

            var mine = _db.GetVideoByUuid("mine")!;
            Assert.Null(mine.MetadataAt);
            Assert.Equal(CallOutcome.None, mine.MetadataResult);
            Assert.Equal(0, mine.MetadataAttempts);

            var untouched = _db.GetSource(other)!;
            Assert.Equal(Now, untouched.FullListedAt);
            Assert.Equal(1, untouched.WaveFailures);
            Assert.Equal(CallOutcome.NeedsAuth, _db.GetVideoByUuid("theirs")!.MetadataResult);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// The all-or-nothing preparation, exercised for real. Reachable only because the configuration
    /// is now a parameter instead of a read of Plugin.Instance, which is null under xUnit.
    /// </summary>
    public sealed class SyncServiceTests : IDisposable
    {
        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly IVodLocalizer _localizer = Substitute.For<IVodLocalizer>();
        private readonly ITaskManager _tasks = Substitute.For<ITaskManager>();
        private readonly List<VirtualFolderInfo> _folders = [];
        private readonly string _root = Directory.CreateTempSubdirectory("vod-sync-tests").FullName;
        private readonly SyncService _service;

        public SyncServiceTests()
        {
            // BaseItem.LibraryManager is deliberately NOT installed here, unlike in
            // LibraryPublisherTests: it is a shared static, xUnit runs test classes in parallel, and
            // a second class writing it would race with that one. Nothing on the preparation path
            // needs it — resolving, writing the .keep marker and FindByPath all go through the
            // substitute directly.
            _library.GetVirtualFolders().Returns(_ => _folders);

            var publisher = new LibraryPublisher(
                _library, _db,
                new ArtworkStore(Path.Combine(Path.GetTempPath(), "vod-tests-artwork")),
                _localizer, NullLogger<LibraryPublisher>.Instance);

            _service = new SyncService(
                _db,
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                publisher, _localizer, NullLogger<SyncService>.Instance, _tasks);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        /// <summary>Makes <paramref name="name"/> a library backed by a real temp folder.</summary>
        private string GivenLibrary(string name)
        {
            var location = Path.Combine(_root, name);
            Directory.CreateDirectory(location);
            _folders.Add(new VirtualFolderInfo { Name = name, Locations = [location] });
            _library.FindByPath(location, true)
                .Returns(new Folder { Id = Guid.NewGuid(), Path = location });
            return location;
        }

        private static PluginConfiguration ConfigWith(string movies, string series, SourceMode mode)
        {
            var config = new PluginConfiguration
            {
                MoviesLibraryName = movies,
                SeriesLibraryName = series
            };
            config.Sources.Add(new SourceDefinition
            {
                Url = "https://example.test/only",
                Mode = mode,
                Enabled = true
            });
            return config;
        }

        [Fact]
        public async Task PrepareLibraryAsync_PreparesOnlyWhatAnEnabledSourceNeeds()
        {
            // Only a Series source exists, so the movies setting naming nothing must not stop the
            // pass. This is not failure isolation — the pass is still all-or-nothing — it is simply
            // not touching a library nothing publishes into.
            var location = GivenLibrary("Séries");
            var config = ConfigWith("Nowhere", "Séries", SourceMode.Series);

            await _service.PrepareLibraryAsync(config, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(location, ".keep")));
        }

        [Fact]
        public async Task PrepareLibraryAsync_FailsNamingTheRole_WhenANeededLibraryIsBlank()
        {
            // Review focus 4. "No library named ''." would tell the administrator nothing about
            // WHICH of the two settings is blank, and a blank setting now stops the pass instead of
            // silently skipping every source.
            GivenLibrary("Séries");
            var config = ConfigWith(string.Empty, "Séries", SourceMode.Movies);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.PrepareLibraryAsync(config, CancellationToken.None));

            Assert.Contains("movies", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PrepareLibraryAsync_FailsNamingTheLibrary_WhenItDoesNotResolve()
        {
            var config = ConfigWith("Films", "Séries", SourceMode.Movies);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.PrepareLibraryAsync(config, CancellationToken.None));

            Assert.Contains("Films", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PrepareLibraryAsync_PreparesOneLibraryWhenBothRolesShareIt()
        {
            // The mixed setup. Preparing twice would be harmless but wasteful; what matters is that
            // the two names resolving to one library stay one piece of work.
            var location = GivenLibrary("VOD");
            var config = ConfigWith("VOD", "vod", SourceMode.Movies);
            config.Sources.Add(new SourceDefinition
            {
                Url = "https://example.test/series",
                Mode = SourceMode.Series,
                Enabled = true
            });

            await _service.PrepareLibraryAsync(config, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(location, ".keep")));
            await _library.DidNotReceive().ValidateTopLibraryFolders(Arg.Any<CancellationToken>());
        }

        [Fact]
        public void RetireRemovedSources_PurgesSourceItems_WhenSourceRemovedFromConfig()
        {
            GivenLibrary("VOD");

            _db.GetAllSources().Returns(new List<SourceRecord>
            {
                new SourceRecord { Url = "https://example.test/gone", PublishedAnchorId = null }
            });
            _db.GetAllVideoIds().Returns(Array.Empty<string>());

            var item = new Movie { Id = Guid.NewGuid() };
            item.SetProviderId("VOD", "3f9a2b1c-0000-0000-0000-000000000400");
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { item });

            var config = new PluginConfiguration
            {
                MoviesLibraryName = "VOD",
                SeriesLibraryName = "VOD"
            };
            // "https://example.test/gone" is absent from config.Sources

            var count = _service.RetireRemovedSources(config, CancellationToken.None);

            Assert.Equal(1, count);
            _library.Received(1).DeleteItem(item, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            _db.Received(1).DeleteSource("https://example.test/gone");
        }

        [Fact]
        public void RetireRemovedSources_SkipsSource_WhenNoLibraryResolves()
        {
            // No call to GivenLibrary: _folders is empty, all ResolveTarget calls throw.
            _db.GetAllSources().Returns(new List<SourceRecord>
            {
                new SourceRecord { Url = "https://example.test/unreachable", PublishedAnchorId = null }
            });

            var config = new PluginConfiguration
            {
                MoviesLibraryName = "Nowhere",
                SeriesLibraryName = "Nowhere"
            };

            var count = _service.RetireRemovedSources(config, CancellationToken.None);

            Assert.Equal(0, count);
            _db.DidNotReceive().DeleteSource(Arg.Any<string>());
        }

        [Fact]
        public void RetireRemovedSources_IncludesStoredAnchor_WhenPublishedAnchorIdIsSet()
        {
            // A source retired from a config where its old library is no longer configured
            // must still be purged from the stored anchor.
            var storedFolder = new Folder { Id = Guid.NewGuid(), Path = "/media/Stored" };
            _library.GetItemById(storedFolder.Id).Returns(storedFolder);

            _db.GetAllSources().Returns(new List<SourceRecord>
            {
                new SourceRecord { Url = "https://example.test/stored", PublishedAnchorId = storedFolder.Id }
            });
            _db.GetAllVideoIds().Returns(Array.Empty<string>());

            var item = new Movie { Id = Guid.NewGuid() };
            item.SetProviderId("VOD", "3f9a2b1c-0000-0000-0000-000000000401");
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { item });

            // No configured library resolves, but the stored anchor does.
            var config = new PluginConfiguration
            {
                MoviesLibraryName = "Nowhere",
                SeriesLibraryName = "Nowhere"
            };

            var count = _service.RetireRemovedSources(config, CancellationToken.None);

            Assert.Equal(1, count);
            _library.Received(1).DeleteItem(item, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }
    }

    /// <summary>
    /// I4: HasPayload is internal so the skip predicate can be unit-tested without running yt-dlp.
    /// </summary>
    public sealed class SyncServiceHasPayloadTests
    {
        [Fact]
        public void HasPayload_ReturnsFalse_ForARowThatHasNeverBeenGivenOne()
        {
            // What a NULL extras column reads back as: a row written before the column existed, or
            // one whose value no longer parses. It must NOT be in the skip set, so it is probed once.
            Assert.False(SyncService.HasPayload(VideoExtras.NoPayload));
        }

        [Fact]
        public void HasPayload_ReturnsTrue_ForAnEmptyPayload()
        {
            // Empty is an answer, not a gap: the platform offered no formats, tags, categories,
            // channels or subtitles. Probing it again would cost one yt-dlp process per sync for ever.
            Assert.True(SyncService.HasPayload(new VideoExtras()));
        }

        [Fact]
        public void HasPayload_ReturnsTrue_WhenQualitiesIsPopulated()
        {
            var extras = new VideoExtras
            {
                Qualities = [new VideoQuality(1080, "h264", null)]
            };
            Assert.True(SyncService.HasPayload(extras));
        }
    }

    /// <summary>
    /// The skip decision against a real database, because it turns on what the row hands back, not
    /// on what was put in it. A payload-less row must be probed once and never again: the full
    /// metadata fetch is one yt-dlp process per row per sync.
    /// </summary>
    public sealed class SyncServiceSkipDecisionTests : IDisposable
    {
        private const string SourceUrl = "https://example.test/src";
        private readonly string _dbPath = Path.GetTempFileName();
        private readonly VodDatabase _db;

        public SyncServiceSkipDecisionTests()
        {
            _db = new VodDatabase(_dbPath, NullLogger<VodDatabase>.Instance);
            _db.EnsureCreated();
            _db.UpsertSource(new SourceRecord { Url = SourceUrl });
        }

        public void Dispose()
        {
            _db.Dispose();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }

        private VideoRecord Stored(string uuid, VideoExtras extras)
        {
            _db.UpsertVideo(new VideoRecord
            {
                Id = uuid,
                SourceUrl = SourceUrl,
                NativeId = uuid,
                VideoUrl = "https://example.test/v/" + uuid,
                SyncedAt = DateTimeOffset.UnixEpoch,
                Extras = extras
            });
            return _db.GetVideoByUuid(uuid)!;
        }

        /// <summary>Makes the row look like one written before the extras column existed.</summary>
        private void ClearExtrasColumn(string uuid)
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE videos SET extras = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", uuid);
            cmd.ExecuteNonQuery();
        }

        [Fact]
        public void HasPayload_IsTrue_ForAnEmptyPayloadTheRowHasBeenGiven()
        {
            // A video yt-dlp resolves but reports nothing for: no HLS formats, no tags, no
            // categories, no channels, no subtitles. The row has been given a payload — this is
            // what the platform offers — so the next sync must not spend a process on it again.
            var read = Stored("aaaaaaaa-0000-0000-0000-000000000001", new VideoExtras());

            Assert.True(SyncService.HasPayload(read.Extras));
        }

        [Fact]
        public void HasPayload_IsFalse_ForARowWrittenBeforeTheExtrasColumnExisted()
        {
            // The behaviour being protected: such a row has never been given a payload, so it must
            // still be probed, and a video that still resolves still gets its real payload.
            const string uuid = "aaaaaaaa-0000-0000-0000-000000000002";
            Stored(uuid, new VideoExtras());
            ClearExtrasColumn(uuid);

            Assert.False(SyncService.HasPayload(_db.GetVideoByUuid(uuid)!.Extras));
        }

        [Fact]
        public void HasPayload_IsTrue_OnceAPayloadlessRowHasBeenRefetchedAndStored()
        {
            // The production path for a row that predates the extras column and still resolves:
            // ProcessEntries mutates the payload it read with a `with` expression, which carries the
            // "never given a payload" version 0 along with it, and stores the result. The write
            // boundary has to stamp the current shape version there, or a fetch that reports nothing
            // would be indistinguishable from never having fetched and the row would be probed for
            // ever even though it has just been answered.
            const string uuid = "aaaaaaaa-0000-0000-0000-000000000003";
            Stored(uuid, new VideoExtras());
            ClearExtrasColumn(uuid);

            var refetched = _db.GetVideoByUuid(uuid)!;
            Assert.False(SyncService.HasPayload(refetched.Extras));

            refetched.Extras = refetched.Extras with { Tags = [], Qualities = [] };
            _db.UpsertVideo(refetched);

            Assert.True(SyncService.HasPayload(_db.GetVideoByUuid(uuid)!.Extras));
        }

        [Fact]
        public void HasPayload_IsNotTheOnlySkipCondition_AnUpcomingRowIsAlwaysRefetched()
        {
            // A scheduled stream's metadata changes under it: it starts, or it is rescheduled. Both
            // Live and Upcoming rows must leave the skip-set, where only IsLive used to.
            Assert.True(SyncService.MustRefetch(LiveStatus.Live));
            Assert.True(SyncService.MustRefetch(LiveStatus.Upcoming));
            Assert.False(SyncService.MustRefetch(LiveStatus.NotLive));
        }

        [Fact]
        public void PublishedAtForUpcoming_IsTheScheduledStart_AndFollowsAReschedule()
        {
            // ParseDate prefers upload_date, which for a scheduled stream is the day it was created,
            // not when it will air (spec F18). And PublishedAt is normally frozen once set, which
            // would pin a stale schedule.
            var first = SyncService.PublishedAtFor(
                LiveStatus.Upcoming,
                releaseTimestamp: 1791128700,
                parsed: new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
                stored: null,
                syncStart: new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791128700), first);

            var rescheduled = SyncService.PublishedAtFor(
                LiveStatus.Upcoming,
                releaseTimestamp: 1791215100,
                parsed: new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
                stored: DateTimeOffset.FromUnixTimeSeconds(1791128700),
                syncStart: new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791215100), rescheduled);
        }

        [Fact]
        public void PublishedAtFor_KeepsTheStoredValue_ForAnOrdinaryVideo()
        {
            var stored = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);

            var result = SyncService.PublishedAtFor(
                LiveStatus.NotLive,
                releaseTimestamp: null,
                parsed: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                stored: stored,
                syncStart: new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero));

            Assert.Equal(stored, result);
        }
    }

    /// <summary>
    /// ProcessEntries returns the count of records created (not updated) in a pass.
    /// </summary>
    public sealed class SyncServiceProcessEntriesTests
    {
        private const string SourceUrl = "https://example.test/pe";
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly IVodLocalizer _localizer = Substitute.For<IVodLocalizer>();
        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly ITaskManager _tasks = Substitute.For<ITaskManager>();
        private readonly SyncService _service;

        public SyncServiceProcessEntriesTests()
        {
            var publisher = new LibraryPublisher(
                _library, _db,
                new ArtworkStore(Path.Combine(Path.GetTempPath(), "vod-tests-artwork")),
                _localizer, NullLogger<LibraryPublisher>.Instance);

            _service = new SyncService(
                _db,
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                publisher, _localizer, NullLogger<SyncService>.Instance, _tasks);
        }

        /// <summary>Builds a playlist entry and its corresponding full metadata with the given id.</summary>
        private static (PlaylistEntry Entry, VideoMetadata Meta) EntryWithMeta(string id) => (
            new PlaylistEntry { Id = id, VideoUrl = "https://example.test/v/" + id },
            new VideoMetadata { Id = id, Title = "Video " + id });

        /// <summary>Builds a stored video record with the given nativeId, as if already synced.</summary>
        private static VideoRecord StoredVideo(string nativeId) => new VideoRecord
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = SourceUrl,
            NativeId = nativeId,
            VideoUrl = "https://example.test/v/" + nativeId,
            SyncedAt = DateTimeOffset.UnixEpoch,
            Extras = new VideoExtras()
        };

        [Fact]
        public void ProcessEntries_ReturnsTwoWhenTwoOfThreeEntriesAreNew()
        {
            // Three entries: "a" is already in the database, "b" and "c" are new.
            var (entryA, metaA) = EntryWithMeta("a");
            var (entryB, metaB) = EntryWithMeta("b");
            var (entryC, metaC) = EntryWithMeta("c");

            var entries = new List<PlaylistEntry> { entryA, entryB, entryC };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = metaA,
                ["b"] = metaB,
                ["c"] = metaC,
            };
            var existingVideos = new List<VideoRecord> { StoredVideo("a") };
            var source = new SourceDefinition { Url = SourceUrl };

            var created = _service.ProcessEntries(
                source, entries, metadataMap, existingVideos,
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Equal(2, created);
        }

        [Fact]
        public void ProcessEntries_CountsARepeatedEntryOnce_AndUpsertsItOnce()
        {
            // A flat listing that returns the same video twice. Before the fix this created two
            // rows, each with its own Guid, and every later sync of the source threw on the
            // native-id map build.
            var (entry, meta) = EntryWithMeta("dup");

            var entries = new List<PlaylistEntry> { entry, entry };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["dup"] = meta,
            };
            var source = new SourceDefinition { Url = SourceUrl };

            var created = _service.ProcessEntries(
                source, entries, metadataMap, existingVideos: [],
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Equal(1, created);
            _db.Received(1).UpsertVideo(Arg.Any<VideoRecord>());
        }

        [Fact]
        public void ProcessEntries_DoesNotLetARepeatedEntryConsumeTwoRanksOfItsDay()
        {
            // Two distinct videos published the same day, the first one repeated by the listing.
            // The day's ranks must be 1 and 2 — not 1 and 3, which is what a repeated entry
            // produced while it was ranked twice.
            var first = new PlaylistEntry { Id = "aaa", VideoUrl = "https://example.test/v/aaa" };
            var second = new PlaylistEntry { Id = "bbb", VideoUrl = "https://example.test/v/bbb" };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaa"] = new VideoMetadata { Id = "aaa", Title = "First",  UploadDate = "20260705" },
                ["bbb"] = new VideoMetadata { Id = "bbb", Title = "Second", UploadDate = "20260705" },
            };

            var saved = new List<VideoRecord>();
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved.Add(call.Arg<VideoRecord>()));

            _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [first, first, second],
                metadataMap,
                existingVideos: [],
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                CancellationToken.None);

            // (month * 100 + day) * 1000 + rankOfDay, with month = 7 and day = 5 → 705_001 and 705_002.
            Assert.Equal(new int?[] { 705_001, 705_002 }, saved.Select(v => v.EpisodeNumber));
        }

        [Fact]
        public void ProcessEntries_DoesNotThrow_WhenTheDatabaseAlreadyHoldsTwoRowsForOneNativeId()
        {
            // The state of a database written by a pre-fix build. Migration v7 cleans it up at
            // startup, but ProcessEntries must not be the thing that crashes if it ever sees it:
            // throwing here takes down the whole source, which is the failure being removed.
            var (entry, meta) = EntryWithMeta("dup");
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["dup"] = meta,
            };
            var existingVideos = new List<VideoRecord> { StoredVideo("dup"), StoredVideo("dup") };

            var created = _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [entry],
                metadataMap,
                existingVideos,
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Equal(0, created);
        }

        [Fact]
        public void ProcessEntries_MatchesTheStoredRowOnTheIdItWrote_NotTheListingId()
        {
            // The row's native_id is written from meta.Id, but the lookup used entry.Id. Those are
            // equal on every platform today — the Arte id derived from the listing URL is the id a
            // full fetch reports, deliberately so — and if that ever stopped holding, the entry
            // would be taken for new: a second row for the same video, which the UNIQUE index of
            // schema v7 now refuses, failing that source's every sync from then on.
            var stored = StoredVideo("real");
            stored.PublishedAt = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
            stored.SeasonNumber = 2026;
            stored.EpisodeNumber = 1012001;

            var diverging = new PlaylistEntry { Id = "derived", VideoUrl = "https://example.test/v/real" };
            var fresh = new PlaylistEntry { Id = "new", VideoUrl = "https://example.test/v/new" };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                // The listing calls it "derived"; the full fetch calls it "real".
                ["derived"] = new VideoMetadata { Id = "real", Title = "Known", UploadDate = "20261012" },
                ["new"] = new VideoMetadata { Id = "new", Title = "New", UploadDate = "20261012" },
            };

            var saved = new List<VideoRecord>();
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved.Add(call.Arg<VideoRecord>()));

            var created = _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [diverging, fresh],
                metadataMap,
                new List<VideoRecord> { stored },
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                CancellationToken.None);

            // Only the genuinely new video counts as created, the known row keeps its number, and
            // the day's rank is not spent twice on the same video.
            Assert.Equal(1, created);
            Assert.Equal(1012001, saved[0].EpisodeNumber);
            Assert.Equal(1012002, saved[1].EpisodeNumber);
        }

        [Fact]
        public void ProcessEntries_WritesOneRow_WhenTwoListingEntriesResolveToTheSameStoredId()
        {
            // The pass deduplicates on the listing id, but the row is keyed on the metadata's id.
            // Admit that those can differ — which is the whole premise of matching on meta.Id —
            // and two distinct entries can resolve to one stored id: both would look new, both
            // would mint a Guid for the same native_id, and the second insert would hit the UNIQUE
            // index of schema v7 and fail that source's every sync from then on. Exactly the
            // bricked source this work exists to abolish.
            var firstEntry = new PlaylistEntry { Id = "listing-a", VideoUrl = "https://example.test/v/a" };
            var secondEntry = new PlaylistEntry { Id = "listing-b", VideoUrl = "https://example.test/v/b" };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["listing-a"] = new VideoMetadata { Id = "same", Title = "One", UploadDate = "20261012" },
                ["listing-b"] = new VideoMetadata { Id = "same", Title = "One again", UploadDate = "20261012" },
            };

            var saved = new List<VideoRecord>();
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved.Add(call.Arg<VideoRecord>()));

            var created = _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [firstEntry, secondEntry],
                metadataMap,
                existingVideos: [],
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Equal(1, created);
            Assert.Single(saved);
            Assert.Equal("same", saved[0].NativeId);
        }

        [Fact]
        public void ApplyListingVerdict_MarksAnUntitledYoutubeEntryUnavailable_WithoutACall()
        {
            // The production case: 33 of 43 entries of a playlist are dead, and YouTube says so by
            // returning a template of nulls. Before this, each of them cost one yt-dlp call on
            // every single sync, all of them failed, and no row was ever written.
            var entries = new List<PlaylistEntry>
            {
                new() { Id = "alive", VideoUrl = "https://example.test/v/alive", Title = "A video", IeKey = "Youtube" },
                new() { Id = "dead", VideoUrl = "https://example.test/v/dead", Title = null, IeKey = "Youtube" },
            };

            var verdict = SyncService.ApplyListingVerdict(entries, [], DateTimeOffset.UtcNow);

            Assert.Equal(["dead"], verdict.Unavailable);
            var written = Assert.Single(verdict.Updated);
            Assert.Equal("dead", written.NativeId);
            Assert.Equal(ResolutionState.Unavailable, written.ResolutionState);
            Assert.Equal("absent from the listing", written.ResolutionError);
        }

        [Fact]
        public void ApplyListingVerdict_LeavesAnUntitledArteEntryAlone()
        {
            // Arte titles nothing: 0 of 79 measured. Reading silence as death here empties a
            // whole collection from the library.
            var entries = new List<PlaylistEntry>
            {
                new() { Id = "a1", VideoUrl = "https://example.test/v/a1", Title = null, IeKey = "ArteTV" },
                new() { Id = "a2", VideoUrl = "https://example.test/v/a2", Title = null, IeKey = "ArteTV" },
            };

            var verdict = SyncService.ApplyListingVerdict(entries, [], DateTimeOffset.UtcNow);

            Assert.Empty(verdict.Unavailable);
            Assert.Empty(verdict.Updated);
        }

        [Fact]
        public void ApplyListingVerdict_BringsBackAVideoWhoseTitleReappeared()
        {
            // A video made private and then public again: the re-check is free and immediate, and
            // does not wait for the seven-day window that only concerns verdicts a call produced.
            var stored = StoredVideo("back");
            stored.ResolutionState = ResolutionState.Unavailable;
            stored.ResolutionError = "absent from the listing";

            var entries = new List<PlaylistEntry>
            {
                new() { Id = "back", VideoUrl = "https://example.test/v/back", Title = "Public again", IeKey = "Youtube" },
            };

            var verdict = SyncService.ApplyListingVerdict(entries, [stored], DateTimeOffset.UtcNow);

            Assert.Empty(verdict.Unavailable);
            var written = Assert.Single(verdict.Updated);
            Assert.Equal(ResolutionState.Pending, written.ResolutionState);
            Assert.Null(written.ResolutionError);

            // Reported so the caller can lift it out of its skip set: the row still carries the
            // payload of its earlier life, so a skip would leave it pending for ever.
            Assert.Equal(["back"], verdict.Reopened);
        }

        [Fact]
        public void ApplyListingVerdict_RetiresAVideoThatLostItsTitle()
        {
            // The original goal of this line of work: a published video that goes private leaves
            // the library, because publication only offers what is resolved.
            var stored = StoredVideo("gone");
            stored.ResolutionState = ResolutionState.Resolved;

            var entries = new List<PlaylistEntry>
            {
                new() { Id = "gone", VideoUrl = "https://example.test/v/gone", Title = null, IeKey = "Youtube" },
            };

            var verdict = SyncService.ApplyListingVerdict(entries, [stored], DateTimeOffset.UtcNow);

            var written = Assert.Single(verdict.Updated);
            Assert.Equal(ResolutionState.Unavailable, written.ResolutionState);
        }

        [Fact]
        public void ApplyListingVerdict_NeverTouchesALiveEntry()
        {
            // A broadcast's listing entry has no stable title either, and MustRefetch already
            // forces it to be fetched again every pass.
            var stored = StoredVideo("airing");
            stored.LiveStatus = LiveStatus.Live;
            stored.ResolutionState = ResolutionState.Resolved;

            var entries = new List<PlaylistEntry>
            {
                new() { Id = "airing", VideoUrl = "https://example.test/v/airing", Title = null, IeKey = "Youtube", LiveStatus = LiveStatus.Live },
            };

            var verdict = SyncService.ApplyListingVerdict(entries, [stored], DateTimeOffset.UtcNow);

            Assert.Empty(verdict.Unavailable);
            Assert.Empty(verdict.Updated);
        }
    }

    /// <summary>
    /// QueueDescriptionIfCreated queues the stream description task exactly when the pass created
    /// at least one video, and stays silent when it created nothing.
    /// </summary>
    public sealed class SyncServiceQueueDescriptionIfCreatedTests
    {
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly IVodLocalizer _localizer = Substitute.For<IVodLocalizer>();
        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly ITaskManager _tasks = Substitute.For<ITaskManager>();
        private readonly SyncService _service;

        public SyncServiceQueueDescriptionIfCreatedTests()
        {
            var publisher = new LibraryPublisher(
                _library, _db,
                new ArtworkStore(Path.Combine(Path.GetTempPath(), "vod-tests-artwork")),
                _localizer, NullLogger<LibraryPublisher>.Instance);

            _service = new SyncService(
                _db,
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                publisher, _localizer, NullLogger<SyncService>.Instance, _tasks);
        }

        [Fact]
        public void QueueDescriptionIfCreated_QueuesTask_WhenCreatedIsPositive()
        {
            _service.QueueDescriptionIfCreated(1);

            _tasks.Received(1).QueueIfNotRunning<StreamDescriptionTask>();
        }

        [Fact]
        public void QueueDescriptionIfCreated_DoesNotQueueTask_WhenCreatedIsZero()
        {
            _service.QueueDescriptionIfCreated(0);

            _tasks.DidNotReceive().QueueIfNotRunning<StreamDescriptionTask>();
        }
    }
}

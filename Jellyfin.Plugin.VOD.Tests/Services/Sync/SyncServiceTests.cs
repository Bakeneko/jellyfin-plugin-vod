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

    public sealed class SyncServiceSkipDecisionTests
    {
        [Fact]
        public void MustRefetch_IsTrueForLiveAndUpcoming_FalseForNotLive()
        {
            // A scheduled stream's metadata changes under it: it starts, or it is rescheduled. Both
            // Live and Upcoming rows must leave the skip-set, where only IsLive used to.
            Assert.True(SyncService.MustRefetch(LiveStatus.Live));
            Assert.True(SyncService.MustRefetch(LiveStatus.Upcoming));
            Assert.False(SyncService.MustRefetch(LiveStatus.NotLive));
        }

        [Fact]
        public void ModeChanged_IsTrue_WhenTheConfiguredModeIsNotThePublishedOne()
        {
            // The instance bug: flipping a settled source from Movies to Series changed nothing,
            // because no due row and a stamped last_resolved_at keep it out of the selector. The
            // mismatch is what earns the source its free wave.
            var record = new SourceRecord
            {
                Url = "https://example.test/s",
                PublishedAnchorId = Guid.NewGuid(),
                PublishedMode = SourceMode.Movies
            };

            Assert.True(SyncService.ModeChanged(record, SourceMode.Series));
        }

        [Fact]
        public void ModeChanged_IsFalse_WhenTheModeStillAgrees()
        {
            var record = new SourceRecord
            {
                Url = "https://example.test/s",
                PublishedAnchorId = Guid.NewGuid(),
                PublishedMode = SourceMode.Series
            };

            Assert.False(SyncService.ModeChanged(record, SourceMode.Series));
        }

        [Fact]
        public void ModeChanged_IsTrue_WhenAPublishedSourceHasNoRecordedMode()
        {
            // A row written before the column existed: the library holds items in a shape nobody
            // recorded, so it is republished once rather than trusted.
            var record = new SourceRecord
            {
                Url = "https://example.test/s",
                PublishedAnchorId = Guid.NewGuid(),
                PublishedMode = null
            };

            Assert.True(SyncService.ModeChanged(record, SourceMode.Series));
        }

        [Fact]
        public void ModeChanged_IsFalse_ForASourceThatHasNeverPublished()
        {
            // Nothing is in the library to re-shape, and the first wave will publish in the right
            // mode anyway. Clearing here would hand an unpublished source the never-served arm on
            // every single sync.
            var record = new SourceRecord { Url = "https://example.test/s" };

            Assert.False(SyncService.ModeChanged(record, SourceMode.Series));
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
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
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
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            Assert.Equal(1, created);
            _db.Received(1).UpsertVideo(Arg.Any<VideoRecord>());
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
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
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
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            // Only the genuinely new video counts as created; the known row keeps its stored number.
            // The new video on the same day gets the next slot: 1012001 + 1 = 1012002.
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
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            Assert.Equal(1, created);
            Assert.Single(saved);
            Assert.Equal("same", saved[0].NativeId);
        }

        [Fact]
        public void ProcessEntries_WritesANewSingleVideoPending_WithItsPreloadedMetadata()
        {
            // I3 (b): a row written Resolved from the preloaded metadata never reached the resolver,
            // so it was never described before its first play. Pending, the resolver resolves it
            // and describes it like any other video; the metadata the sync already holds is kept.
            var entry = new PlaylistEntry { Id = "solo", VideoUrl = "https://example.test/v/solo" };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["solo"] = new VideoMetadata
                {
                    Id = "solo", Title = "A single video", UploadDate = "20261012", Duration = 600,
                },
            };

            VideoRecord? saved = null;
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved = call.Arg<VideoRecord>());

            var created = _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [entry],
                metadataMap,
                existingVideos: [],
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            Assert.Equal(1, created);
            Assert.NotNull(saved);
            Assert.Equal(ResolutionState.Pending, saved!.ResolutionState);
            Assert.Equal(0, saved.ResolutionAttempts);
            Assert.Null(saved.ResolutionAttemptedAt);
            Assert.Equal("A single video", saved.Title);
            Assert.Equal(600, saved.DurationSeconds);
            Assert.NotNull(saved.PublishedAt);
        }

        [Fact]
        public void ProcessEntries_KeepsAKnownSingleVideoResolved()
        {
            // Only a new entry goes through the resolver: a known one was resolved — and described —
            // already, and spending a yt-dlp call on it every pass is what the design removes.
            var stored = StoredVideo("solo");
            stored.ResolutionState = ResolutionState.Resolved;
            var entry = new PlaylistEntry { Id = "solo", VideoUrl = "https://example.test/v/solo" };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["solo"] = new VideoMetadata { Id = "solo", Title = "A single video", UploadDate = "20261012" },
            };

            VideoRecord? saved = null;
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved = call.Arg<VideoRecord>());

            _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [entry],
                metadataMap,
                new List<VideoRecord> { stored },
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            Assert.Equal(ResolutionState.Resolved, saved!.ResolutionState);
        }

        [Fact]
        public void ProcessEntries_KeepsAKnownPendingSingleVideoPending()
        {
            // N1: a new single video is written Pending so the resolver describes it. If the next
            // sync comes before the resolver did, forcing Resolved would skip that description.
            var stored = StoredVideo("solo");
            stored.ResolutionState = ResolutionState.Pending;
            stored.ResolutionAttempts = 0;
            stored.ResolutionAttemptedAt = null;
            stored.ResolutionError = null;

            var saved = ProcessKnownSingleVideo(stored);

            Assert.Equal(ResolutionState.Pending, saved.ResolutionState);
            Assert.Equal(0, saved.ResolutionAttempts);
            Assert.Null(saved.ResolutionAttemptedAt);
        }

        [Fact]
        public void ProcessEntries_KeepsAKnownFailedSingleVideoFailed_WithItsAttempts()
        {
            // N1: a Failed row waits for its retry; resetting it to Resolved would drop the retry
            // and the description it is meant to produce.
            var attemptedAt = DateTimeOffset.UtcNow.AddHours(-1);
            var stored = StoredVideo("solo");
            stored.ResolutionState = ResolutionState.Failed;
            stored.ResolutionAttempts = 2;
            stored.ResolutionAttemptedAt = attemptedAt;
            stored.ResolutionError = "describe failed";

            var saved = ProcessKnownSingleVideo(stored);

            Assert.Equal(ResolutionState.Failed, saved.ResolutionState);
            Assert.Equal(2, saved.ResolutionAttempts);
            Assert.Equal(attemptedAt, saved.ResolutionAttemptedAt);
            Assert.Equal("describe failed", saved.ResolutionError);
        }

        [Fact]
        public void ProcessEntries_CountsAReopenedUnavailableSingleVideo_AsCreated()
        {
            // P2: a reopened Unavailable row is treated as a new creation for scheduling purposes.
            // Without this, QueueResolutionIfDue is never called and the resolver never wakes.
            var stored = StoredVideo("solo");
            stored.ResolutionState = ResolutionState.Unavailable;

            var entry = new PlaylistEntry { Id = "solo", VideoUrl = "https://example.test/v/solo" };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["solo"] = new VideoMetadata { Id = "solo", Title = "A single video", UploadDate = "20261012" },
            };

            var created = _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [entry],
                metadataMap,
                new List<VideoRecord> { stored },
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            Assert.Equal(1, created);
        }

        [Fact]
        public void ProcessEntries_ReopensAKnownUnavailableSingleVideo_AsPending()
        {
            // The listing returned its metadata, so the video is available again: left Unavailable
            // it would stay hidden until the weekly re-check, and written Resolved it would never
            // be described. Pending, with a clean slate, the resolver resolves and describes it.
            var stored = StoredVideo("solo");
            stored.ResolutionState = ResolutionState.Unavailable;
            stored.ResolutionAttempts = 3;
            stored.ResolutionAttemptedAt = DateTimeOffset.UtcNow.AddDays(-2);
            stored.ResolutionError = "absent from the listing";

            var saved = ProcessKnownSingleVideo(stored);

            Assert.Equal(ResolutionState.Pending, saved.ResolutionState);
            Assert.Equal(0, saved.ResolutionAttempts);
            Assert.Null(saved.ResolutionAttemptedAt);
            Assert.Null(saved.ResolutionError);
        }

        private VideoRecord ProcessKnownSingleVideo(VideoRecord stored)
        {
            var entry = new PlaylistEntry { Id = "solo", VideoUrl = "https://example.test/v/solo" };
            var metadataMap = new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["solo"] = new VideoMetadata { Id = "solo", Title = "A single video", UploadDate = "20261012" },
            };

            VideoRecord? saved = null;
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved = call.Arg<VideoRecord>());

            _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [entry],
                metadataMap,
                new List<VideoRecord> { stored },
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            Assert.NotNull(saved);
            return saved!;
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
        public void ApplyListingVerdict_DoesNotStampResolutionAttemptedAt_ForListingAbsenceVerdict()
        {
            // B-I3: DueNowPredicate makes an Unavailable row with a non-null ResolutionAttemptedAt
            // due after seven days — one yt-dlp call per dead entry per week, violating §5.1's
            // promise of "sans aucun appel yt-dlp" for the listing-absence case.
            // The fix: do not stamp ResolutionAttemptedAt when writing the free listing verdict.
            var entries = new List<PlaylistEntry>
            {
                new() { Id = "dead", VideoUrl = "https://example.test/v/dead", Title = null, IeKey = "Youtube" },
            };

            var verdict = SyncService.ApplyListingVerdict(entries, [], DateTimeOffset.UtcNow);

            var written = Assert.Single(verdict.Updated);
            Assert.Equal(ResolutionState.Unavailable, written.ResolutionState);
            // Must be null — a non-null stamp would make the row due after seven days.
            Assert.Null(written.ResolutionAttemptedAt);
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

        [Fact]
        public void ProcessEntries_SkipsUnavailableIds_LeavingExactlyOneRow()
        {
            // ApplyListingVerdict already wrote an Unavailable row for dead YouTube entries; a
            // second upsert from ProcessEntries would mint a fresh Guid for the same native_id
            // and hit the UNIQUE index, bricking the source for all future syncs.
            var alive = new PlaylistEntry
            {
                Id = "alive",
                VideoUrl = "https://example.test/v/alive",
                Title = "A video",
                IeKey = "Youtube",
            };
            var dead = new PlaylistEntry
            {
                Id = "dead",
                VideoUrl = "https://example.test/v/dead",
                Title = null,
                IeKey = "Youtube",
            };

            var saved = new List<VideoRecord>();
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved.Add(call.Arg<VideoRecord>()));

            var created = _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [alive, dead],
                new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase),
                existingVideos: [],
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                unavailableIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dead" },
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            // Only the alive entry was written; the dead one must be silently skipped.
            Assert.Equal(1, created);
            var row = Assert.Single(saved);
            Assert.Equal("alive", row.NativeId);
        }

        [Fact]
        public void ProcessEntries_WritesAPendingRow_FromTheListingAlone()
        {
            // The sync enumerates and nothing more: a new entry becomes a pending row carrying
            // what the listing gave, and the resolver fetches the rest.
            var entry = new PlaylistEntry
            {
                Id = "fresh",
                VideoUrl = "https://example.test/v/fresh",
                Title = "A video",
                IeKey = "Youtube",
                Duration = 212.5,
            };

            var saved = new List<VideoRecord>();
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved.Add(call.Arg<VideoRecord>()));

            var created = _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [entry],
                new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase),
                existingVideos: [],
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fresh"] = 3,
                },
                CancellationToken.None);

            Assert.Equal(1, created);
            var row = Assert.Single(saved);
            Assert.Equal(ResolutionState.Pending, row.ResolutionState);
            Assert.Equal("fresh", row.NativeId);
            Assert.Equal("A video", row.Title);
            Assert.Equal(212, row.DurationSeconds);
            Assert.Equal("Youtube", row.ExtractorKey);
            Assert.Null(row.PublishedAt);
            // The rank from the positions map must be carried into the row.
            Assert.Equal(3, row.ListingPosition);
        }

        [Fact]
        public void ProcessEntries_LeavesAKnownRowsStateAlone_ExceptALiveOne()
        {
            var resolved = StoredVideo("known");
            resolved.ResolutionState = ResolutionState.Resolved;
            var airing = StoredVideo("airing");
            airing.ResolutionState = ResolutionState.Resolved;
            airing.LiveStatus = LiveStatus.Live;
            // A broadcast that ended: stored as Live, but the next listing entry is NotLive.
            // The stored status is the trigger — the row must still go back to Pending so the
            // resolver can fetch the final metadata (duration, description, actual title).
            var ended = StoredVideo("ended");
            ended.ResolutionState = ResolutionState.Resolved;
            ended.LiveStatus = LiveStatus.Live;

            var saved = new List<VideoRecord>();
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
               .Do(call => saved.Add(call.Arg<VideoRecord>()));

            _service.ProcessEntries(
                new SourceDefinition { Url = SourceUrl },
                [
                    new PlaylistEntry { Id = "known", VideoUrl = "https://example.test/v/known", Title = "Known", IeKey = "Youtube" },
                    new PlaylistEntry { Id = "airing", VideoUrl = "https://example.test/v/airing", Title = "Airing", IeKey = "Youtube", LiveStatus = LiveStatus.Live },
                    new PlaylistEntry { Id = "ended", VideoUrl = "https://example.test/v/ended", Title = "Ended", IeKey = "Youtube", LiveStatus = LiveStatus.NotLive },
                ],
                new Dictionary<string, VideoMetadata>(StringComparer.OrdinalIgnoreCase),
                [resolved, airing, ended],
                seriesName: string.Empty,
                syncStart: DateTimeOffset.UtcNow,
                unavailableIds: [],
                positions: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["known"] = 0,
                    ["airing"] = 1,
                    ["ended"] = 2,
                },
                CancellationToken.None);

            var known = saved.Single(v => v.NativeId == "known");
            var live = saved.Single(v => v.NativeId == "airing");
            var broadcastEnded = saved.Single(v => v.NativeId == "ended");

            Assert.Equal(ResolutionState.Resolved, known.ResolutionState);
            Assert.Equal(0, known.ListingPosition);

            // A broadcast's metadata changes under us, so its verdict cannot be trusted from a
            // previous pass.
            Assert.Equal(ResolutionState.Pending, live.ResolutionState);
            Assert.Equal(1, live.ListingPosition);

            // Stored as Live but listed as NotLive: the broadcast has ended; refetch so the
            // resolver can record the final duration, description and corrected title.
            Assert.Equal(ResolutionState.Pending, broadcastEnded.ResolutionState);
            Assert.Equal(2, broadcastEnded.ListingPosition);
        }

        [Fact]
        public void QueueResolutionIfDue_QueuesOnlyWhenSomethingIsPending()
        {
            _service.QueueResolutionIfDue(0);
            _tasks.DidNotReceive().QueueIfNotRunning<ResolutionTask>();

            _service.QueueResolutionIfDue(3);
            _tasks.Received(1).QueueIfNotRunning<ResolutionTask>();
        }

        [Fact]
        public void CleanupOrphans_WakesTheResolver_WhenItDeletesAnOrphan()
        {
            // Deleting the last blocking row (pending, or failed with attempts left) can be what
            // settles a source; without a nudge the resolver would never number or publish it.
            var syncStart = DateTimeOffset.UtcNow;
            _db.GetOrphanedVideoUuids(SourceUrl, syncStart).Returns(["gone"]);

            _service.CleanupOrphans(new SourceDefinition { Url = SourceUrl }, syncStart, enumeratedCount: 2);

            _db.Received(1).DeleteVideo("gone");
            _db.Received(1).ClearLastResolvedAt(SourceUrl);
        }

        [Fact]
        public void CleanupOrphans_LeavesTheResolverAlone_WhenNothingIsDeleted()
        {
            var syncStart = DateTimeOffset.UtcNow;
            _db.GetOrphanedVideoUuids(SourceUrl, syncStart).Returns([]);

            _service.CleanupOrphans(new SourceDefinition { Url = SourceUrl }, syncStart, enumeratedCount: 2);

            _db.DidNotReceive().DeleteVideo(Arg.Any<string>());
            _db.DidNotReceive().ClearLastResolvedAt(Arg.Any<string>());
        }

        [Fact]
        public void CleanupOrphans_LeavesTheResolverAlone_WhenTheListingIsEmpty()
        {
            // An empty enumeration skips cleanup entirely, so nothing changes and nothing wakes.
            var syncStart = DateTimeOffset.UtcNow;
            _db.GetOrphanedVideoUuids(SourceUrl, syncStart).Returns(["gone"]);

            _service.CleanupOrphans(new SourceDefinition { Url = SourceUrl }, syncStart, enumeratedCount: 0);

            _db.DidNotReceive().DeleteVideo(Arg.Any<string>());
            _db.DidNotReceive().ClearLastResolvedAt(Arg.Any<string>());
        }
    }

}

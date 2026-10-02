using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
                    NullLogger<YtDlpService>.Instance),
                publisher, _localizer, NullLogger<SyncService>.Instance);
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
    }
}

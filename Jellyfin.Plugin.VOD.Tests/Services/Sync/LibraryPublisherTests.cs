using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class LibraryPublisherTests
    {
        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly IVodLocalizer _localizer = Substitute.For<IVodLocalizer>();
        private readonly LibraryPublisher _publisher;

        public LibraryPublisherTests()
        {
            // BaseItem routes AddChild and UpdateToRepositoryAsync through this static, so the
            // substitute has to be installed before either path is exercised.
            BaseItem.LibraryManager = _library;
            // Publishing a Series reaches CreatePresentationUniqueKey, which walks back into the
            // library manager twice. An unconfigured substitute returns null from both, where the real
            // one never does, so the Series path would throw before reaching anything under test.
            // PreferredMetadataLanguage is set so GetPreferredMetadataLanguage resolves here rather
            // than walking on to BaseItem.ConfigurationManager, a second static this harness does not
            // install. A real library carries one, so this is a production path, not a shortcut.
            _library.GetLibraryOptions(Arg.Any<BaseItem>())
                .Returns(new LibraryOptions { PreferredMetadataLanguage = "en" });
            _library.GetCollectionFolders(Arg.Any<BaseItem>()).Returns([]);
            _publisher = new LibraryPublisher(
                _library, _db, new ArtworkStore(Path.Combine(Path.GetTempPath(), "vod-tests-artwork")),
                _localizer, NullLogger<LibraryPublisher>.Instance);
        }

        [Fact]
        public void ItemKeyFor_HasTheExactPublishedFormat()
        {
            // Pinned, not recomputed: the key feeds GetNewItemId, so changing its shape gives every
            // published item a new id and orphans the whole library along with its user data.
            Assert.Equal(
                "vod:3f9a2b1c-0000-0000-0000-000000000001",
                _publisher.ItemKeyFor("3f9a2b1c-0000-0000-0000-000000000001"));
        }

        [Fact]
        public void ItemKeyFor_DiffersBetweenVideos()
        {
            Assert.NotEqual(
                _publisher.ItemKeyFor("3f9a2b1c-0000-0000-0000-000000000001"),
                _publisher.ItemKeyFor("3f9a2b1c-0000-0000-0000-000000000002"));
        }

        [Fact]
        public void ItemKeyFor_ContainsNoPath()
        {
            // The whole point: the id must survive renames, moves and title changes.
            var key = _publisher.ItemKeyFor("3f9a2b1c-0000-0000-0000-000000000001");
            Assert.DoesNotContain('/', key);
            Assert.DoesNotContain('\\', key);
        }

        [Fact]
        public void ResolveAnchor_Throws_WhenLibraryUnknown()
        {
            _library.GetVirtualFolders().Returns([]);
            Assert.Throws<InvalidOperationException>(() => _publisher.ResolveAnchor("Vods"));
        }

        [Fact]
        public void ResolveAnchor_Throws_WhenPhysicalFolderMissing()
        {
            // Falling back to the CollectionFolder would publish items nothing can find —
            // the most expensive failure mode observed. It must be loud.
            _library.GetVirtualFolders().Returns(
            [
                new VirtualFolderInfo
                {
                    Name = "Vods",
                    ItemId = Guid.NewGuid().ToString("N"),
                    Locations = ["/media/Vods"]
                }
            ]);
            _library.FindByPath("/media/Vods", true).Returns((BaseItem?)null);

            var ex = Assert.Throws<InvalidOperationException>(() => _publisher.ResolveAnchor("Vods"));
            Assert.Contains("/media/Vods", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ResolveAnchor_ReturnsPhysicalFolder()
        {
            var folder = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            _library.GetVirtualFolders().Returns(
            [
                new VirtualFolderInfo
                {
                    Name = "Vods",
                    ItemId = Guid.NewGuid().ToString("N"),
                    Locations = ["/media/Vods"]
                }
            ]);
            _library.FindByPath("/media/Vods", true).Returns(folder);

            Assert.Same(folder, _publisher.ResolveAnchor("Vods"));
        }

        [Fact]
        public async Task ReconcileAsync_DoesNotDelete_WhenVideoIsKnownByAnotherSource()
        {
            // An item published for a different source is present in the library-wide map.
            // As long as allKnownVideoIds claims it, ReconcileAsync must not remove it.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000042";
            var orphan = new Movie();
            orphan.SetProviderId("VOD", vodId);

            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { orphan });

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Movies, "Test",
                Array.Empty<VideoRecord>(),
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.DidNotReceive().DeleteItem(
                Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_Deletes_WhenVideoIsNotKnownByAnySource()
        {
            // An item present in the library but absent from every source's video set is orphaned
            // and must be removed.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000043";
            var orphan = new Movie();
            orphan.SetProviderId("VOD", vodId);

            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { orphan });

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Movies, "Test",
                Array.Empty<VideoRecord>(),
                new HashSet<string>(),
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.Received(1).DeleteItem(orphan, Arg.Any<DeleteOptions>(), true);
        }

        [Fact]
        public async Task ReconcileAsync_KeepsSeries_WhenAnotherSourceStillFillsIt()
        {
            // Two source URLs that resolve to the same channel share one Series item. Emptying one
            // of them must not delete the series: DeleteItem would take the other source's whole
            // subtree — episodes and watch history alike.
            var series = new Series { Id = Guid.NewGuid() };
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { series });
            _library.GetCount(Arg.Any<InternalItemsQuery>()).Returns(4);

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://www.youtube.com/@arte/streams" };

            await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Series, "Arte",
                Array.Empty<VideoRecord>(),
                new HashSet<string>(),
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.DidNotReceive().DeleteItem(
                Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_DeletesSeries_WhenNothingIsLeftUnderIt()
        {
            // The mirror case: no episode survives under the series, so nothing else claims it.
            var series = new Series { Id = Guid.NewGuid() };
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { series });
            _library.GetCount(Arg.Any<InternalItemsQuery>()).Returns(0);

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://www.youtube.com/@arte/streams" };

            await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Series, "Arte",
                Array.Empty<VideoRecord>(),
                new HashSet<string>(),
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.Received(1).DeleteItem(series, Arg.Any<DeleteOptions>(), true);
        }

        [Fact]
        public async Task ReconcileAsync_ReplacesTheStaleItem_WhenTheSourceModeWasFlipped()
        {
            // Switching a source from Series to Movies republishes the video as a Movie under a new
            // id. Nothing else accounts for the Episode afterwards — the orphan loop has already
            // taken it out of the map — so the upsert has to delete it itself.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000044";
            var stale = new Episode { Id = Guid.NewGuid() };
            stale.SetProviderId("VOD", vodId);

            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { stale });

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = new VideoRecord
            {
                Id = vodId,
                SourceUrl = source.Url,
                VideoUrl = "https://example.com/watch/44",
                Title = "Flipped"
            };

            var report = await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Movies, "Test",
                new[] { video },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.Received(1).DeleteItem(stale, Arg.Any<DeleteOptions>(), true);
            Assert.Equal(1, report.Created);
            Assert.Equal(0, report.Updated);
            await _library.DidNotReceive().UpdateItemAsync(
                Arg.Any<BaseItem>(), Arg.Any<BaseItem>(), Arg.Any<ItemUpdateType>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ReconcileAsync_WritesNothing_WhenTheSecondPassFindsNothingChanged()
        {
            // Rule 3: an item that is expected, present and unchanged is left alone. Committing it
            // anyway raises ItemUpdated per item on every sync and runs the metadata savers.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000045";
            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = new VideoRecord
            {
                Id = vodId,
                SourceUrl = source.Url,
                VideoUrl = "https://example.com/watch/45",
                Title = "Steady",
                DurationSeconds = 120
            };

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var first = await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Movies, "Test",
                new[] { video },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(1, first.Created);
            Assert.NotNull(created);

            // Second pass: the library now holds exactly what the first pass published.
            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Movies, "Test",
                new[] { video },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(0, second.Created);
            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
            await _library.DidNotReceive().UpdateItemAsync(
                Arg.Any<BaseItem>(), Arg.Any<BaseItem>(), Arg.Any<ItemUpdateType>(),
                Arg.Any<CancellationToken>());
            _library.DidNotReceive().CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>());
        }

        [Fact]
        public async Task ReconcileAsync_Updates_WhenAFieldActuallyChanged()
        {
            // The mirror of the no-op pass: a retitled video still has to reach the repository.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000046";
            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = new VideoRecord
            {
                Id = vodId,
                SourceUrl = source.Url,
                VideoUrl = "https://example.com/watch/46",
                Title = "Before"
            };

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Movies, "Test",
                new[] { video },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.NotNull(created);
            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });
            video.Title = "After";

            var second = await _publisher.ReconcileAsync(
                anchor, source, SourceMode.Movies, "Test",
                new[] { video },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(1, second.Updated);
            Assert.Equal(0, second.Unchanged);
            await _library.Received(1).UpdateItemAsync(
                created, Arg.Any<BaseItem>(), ItemUpdateType.MetadataEdit,
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ReconcileAsync_PublishesChannelsAsStudios_QualifiedWithThePlatform()
        {
            // The platform suffix is what keeps two same-named channels on different platforms from
            // collapsing into one studio.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000050";
            var created = await PublishOneMovieAsync(vodId, "Youtube", """["Terrapodia"]""");

            Assert.Equal(["Terrapodia (Youtube)"], created.Studios);
        }

        [Fact]
        public async Task ReconcileAsync_PublishesEveryReportedChannel()
        {
            // yt-dlp reports channel and uploader separately; they diverge on auto-generated
            // "Topic" channels, where the uploader is the artist behind the channel.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000051";
            var created = await PublishOneMovieAsync(
                vodId, "Youtube", """["Some Artist - Topic","Some Artist"]""");

            Assert.Equal(
                ["Some Artist - Topic (Youtube)", "Some Artist (Youtube)"],
                created.Studios);
        }

        /// <summary>
        /// Arte reports neither channel nor uploader. Publishing no studio is deliberate: the
        /// alternative is inventing one.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_PublishesNoStudio_WhenThePlatformReportsNoChannel()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000052";
            var created = await PublishOneMovieAsync(vodId, "ArteTV", channelsJson: null);

            Assert.Empty(created.Studios);
        }

        [Fact]
        public async Task ReconcileAsync_FallsBackToBareChannelNames_WhenTheExtractorIsUnknown()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000053";
            var created = await PublishOneMovieAsync(vodId, null, """["Somewhere"]""");

            Assert.Equal(["Somewhere"], created.Studios);
        }

        /// <summary>
        /// Guards the reconciliation rule that a field written without being compared is recomputed
        /// and thrown away on every pass: if the studios were not part of the change detection, the
        /// second pass would report an update instead of leaving the item alone.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_LeavesStudiosAlone_OnTheSecondPass()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000054";
            var created = await PublishOneMovieAsync(vodId, "Youtube", """["Terrapodia"]""");

            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                _studioAnchor,
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test",
                new[] { VideoWith(vodId, "Youtube", """["Terrapodia"]""") },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
        }

        [Theory]
        // Option off: the discovery date wins.
        [InlineData(false, "2026-05-01T00:00:00Z", "2026-01-01T00:00:00Z", "2026-05-01T00:00:00Z")]
        // Option on: the release date wins.
        [InlineData(true, "2026-05-01T00:00:00Z", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z")]
        public void DateAddedFor_PicksTheConfiguredSource(
            bool useReleaseDate, string firstSeen, string published, string expected)
        {
            var video = new VideoRecord
            {
                Id = "v", SourceUrl = "s", VideoUrl = "u",
                FirstSeenAt = DateTimeOffset.Parse(firstSeen, null, DateTimeStyles.RoundtripKind),
                PublishedAt = DateTimeOffset.Parse(published, null, DateTimeStyles.RoundtripKind),
                SyncedAt = DateTimeOffset.UtcNow
            };

            Assert.Equal(
                DateTime.Parse(expected, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                LibraryPublisher.DateAddedFor(video, useReleaseDate));
        }

        /// <summary>
        /// With the option on, a platform reporting no release date falls back to the discovery date.
        /// </summary>
        [Fact]
        public void DateAddedFor_FallsBackToDiscovery_WhenThereIsNoReleaseDate()
        {
            var seen = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero);
            var noPublished = new VideoRecord
            {
                Id = "v", SourceUrl = "s", VideoUrl = "u", FirstSeenAt = seen,
                SyncedAt = new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero)
            };

            Assert.Equal(seen.UtcDateTime, LibraryPublisher.DateAddedFor(noPublished, true));
        }

        /// <summary>
        /// SyncedAt is rewritten on every pass — SyncService assigns syncStart to it and TouchVideos
        /// updates the column for rows it did not re-fetch — so deriving the published date from it
        /// would move that date every pass and rewrite the item for ever. Only FirstSeenAt and
        /// PublishedAt are set once, so only they may be read. A row with neither yields null, and
        /// the item then keeps whatever date it already carries.
        /// </summary>
        [Fact]
        public void DateAddedFor_IgnoresSyncedAt_SoTheValueCannotMoveBetweenPasses()
        {
            var video = new VideoRecord
            {
                Id = "v", SourceUrl = "s", VideoUrl = "u",
                SyncedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            };

            Assert.Null(LibraryPublisher.DateAddedFor(video, useReleaseDate: false));
            Assert.Null(LibraryPublisher.DateAddedFor(video, useReleaseDate: true));

            // A later pass moved SyncedAt. The derived date must not have moved with it.
            video.SyncedAt = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

            Assert.Null(LibraryPublisher.DateAddedFor(video, useReleaseDate: false));
            Assert.Null(LibraryPublisher.DateAddedFor(video, useReleaseDate: true));
        }

        /// <summary>
        /// The reversibility the whole design rests on, exercised through real publishing rather than
        /// through the pure helper: on, off, and back again, with the original values restored and the
        /// following pass finding nothing left to change.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_RestoresTheDateAdded_WhenTheOptionIsTurnedBackOff()
        {
            var seen = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var released = new DateTimeOffset(2019, 3, 3, 0, 0, 0, TimeSpan.Zero);

            var video = VideoWith("3f9a2b1c-0000-0000-0000-000000000080", "Youtube", null);
            video.FirstSeenAt = seen;
            video.PublishedAt = released;

            var created = await PublishAsync(video);
            Assert.Equal(seen.UtcDateTime, created.DateCreated);

            // Jellyfin now holds what the first pass published.
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            await Republish(video, useReleaseDate: true);
            Assert.Equal(released.UtcDateTime, created.DateCreated);

            await Republish(video, useReleaseDate: false);
            Assert.Equal(seen.UtcDateTime, created.DateCreated);

            var stable = await Republish(video, useReleaseDate: false);
            Assert.Equal(0, stable.Updated);
            Assert.Equal(1, stable.Unchanged);
        }

        /// <summary>
        /// The Series path on a second pass. It carries DateLastMediaAdded, whose column Jellyfin
        /// stores as null and reads back as MinValue, so it is the field most exposed to the
        /// permanent-rewrite failure this project has hit repeatedly.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_LeavesTheSeriesAlone_OnTheSecondPass()
        {
            // The real GetNewItemId returns a distinct deterministic id per key. An unconfigured
            // substitute returns Guid.Empty for every call, which collapses the series, its season and
            // its episode onto one id and makes re-reading what was published meaningless.
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            var video = VideoWith("3f9a2b1c-0000-0000-0000-000000000081", "Youtube", null);
            video.FirstSeenAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            video.SeasonNumber = 2026;
            video.EpisodeNumber = 1;

            var captured = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => captured.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await ReconcileSeriesAsync(video, useReleaseDate: false);

            var series = Assert.Single(captured.OfType<Series>());
            var episode = Assert.Single(captured.OfType<Episode>());
            Assert.NotEqual(default, series.DateLastMediaAdded);

            foreach (var item in captured)
                _library.GetItemById(item.Id).Returns(item);
            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { episode });

            var second = await ReconcileSeriesAsync(video, useReleaseDate: false);

            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
            await _library.DidNotReceive().UpdateItemAsync(
                series, Arg.Any<BaseItem>(), Arg.Any<ItemUpdateType>(), Arg.Any<CancellationToken>());
        }

        private static Guid DeterministicId(string key)
            => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

        private Task<ReconcileReport> Republish(VideoRecord video, bool useReleaseDate)
            => _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { video.Id }, "http://localhost:8096",
                useReleaseDate, CancellationToken.None);

        private Task<ReconcileReport> ReconcileSeriesAsync(VideoRecord video, bool useReleaseDate)
            => _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Series, "Test", new[] { video },
                new HashSet<string> { video.Id }, "http://localhost:8096",
                useReleaseDate, CancellationToken.None);

        /// <summary>
        /// A DateTimeOffset carrying a non-zero offset must resolve to the same instant whichever
        /// derivation reads it, or the comparison flip-flops and the item is rewritten for ever.
        /// </summary>
        [Fact]
        public void DateAddedFor_NormalisesToUtc()
        {
            var video = new VideoRecord
            {
                Id = "v", SourceUrl = "s", VideoUrl = "u",
                FirstSeenAt = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.FromHours(2)),
                SyncedAt = DateTimeOffset.UtcNow
            };

            var result = LibraryPublisher.DateAddedFor(video, false);

            Assert.NotNull(result);
            Assert.Equal(DateTimeKind.Utc, result.Value.Kind);
            Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), result.Value);
        }

        [Fact]
        public async Task ReconcileAsync_PublishesTheDiscoveryDateAsDateAdded()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000061";
            var seen = new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero);

            var video = VideoWith(vodId, "Youtube", null);
            video.FirstSeenAt = seen;
            video.PublishedAt = new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var created = await PublishAsync(video);

            Assert.Equal(seen.UtcDateTime, created.DateCreated);
        }

        /// <summary>
        /// The option's path, reachable only because the flag is a parameter: reading it from
        /// Plugin.Instance would make this state untestable, since that static is null under xUnit.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_PublishesTheReleaseDateAsDateAdded_WhenTheOptionIsOn()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000063";
            var released = new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var video = VideoWith(vodId, "Youtube", null);
            video.FirstSeenAt = new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero);
            video.PublishedAt = released;

            var created = await PublishAsync(video, useReleaseDate: true);

            Assert.Equal(released.UtcDateTime, created.DateCreated);
        }

        /// <summary>
        /// The regression guard: the second pass must find nothing changed. Feeding the same in-memory
        /// item back would not prove it, so the item is re-read the way Jellyfin returns it.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_LeavesTheDateAddedAlone_OnTheSecondPass()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000062";
            var video = VideoWith(vodId, "Youtube", null);
            video.FirstSeenAt = new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero);

            var created = await PublishAsync(video);

            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
        }

        private async Task<BaseItem> PublishAsync(VideoRecord video, bool useReleaseDate = false)
        {
            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var report = await _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { video.Id }, "http://localhost:8096",
                useReleaseDate, CancellationToken.None);

            Assert.Equal(1, report.Created);
            Assert.NotNull(created);
            return created;
        }

        /// <summary>
        /// A series is "added" when its first video appeared and "last gained content" when its most
        /// recent one did. DateLastMediaAdded deliberately ignores the option: its meaning is the
        /// truth about acquisition, whereas DateCreated is the sort key the user steers.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_RollsUpSeriesDatesFromItsEpisodes()
        {
            var oldest = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var newest = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

            var first = VideoWith("3f9a2b1c-0000-0000-0000-000000000070", "Youtube", null);
            first.FirstSeenAt = oldest;
            first.SeasonNumber = 2026;
            first.EpisodeNumber = 1;

            var second = VideoWith("3f9a2b1c-0000-0000-0000-000000000071", "Youtube", null);
            second.FirstSeenAt = newest;
            second.SeasonNumber = 2026;
            second.EpisodeNumber = 2;

            var series = await PublishSeriesAsync(first, second);

            Assert.Equal(oldest.UtcDateTime, series.DateCreated);
            Assert.Equal(newest.UtcDateTime, series.DateLastMediaAdded);
        }

        /// <summary>
        /// Option on, the series' added date becomes the NEWEST release rather than the oldest
        /// discovery — that asymmetry is the whole point of the setting, which exists to surface
        /// series carrying recent videos.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_UsesTheNewestReleaseAsTheSeriesDateAdded_WhenTheOptionIsOn()
        {
            var older = new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var newer = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);

            var first = VideoWith("3f9a2b1c-0000-0000-0000-000000000074", "Youtube", null);
            first.PublishedAt = older;
            first.FirstSeenAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            first.SeasonNumber = 2019;
            first.EpisodeNumber = 1;

            var second = VideoWith("3f9a2b1c-0000-0000-0000-000000000075", "Youtube", null);
            second.PublishedAt = newer;
            second.FirstSeenAt = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
            second.SeasonNumber = 2026;
            second.EpisodeNumber = 1;

            var series = await PublishSeriesAsync(useReleaseDate: true, first, second);

            Assert.Equal(newer.UtcDateTime, series.DateCreated);
            // Unchanged by the option: acquisition truth, the newest discovery.
            Assert.Equal(
                new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), series.DateLastMediaAdded);
        }

        /// <summary>
        /// Never null: Jellyfin stores a null here and reads it back as MinValue, so assigning null
        /// would report a change on every pass and rewrite the series for ever.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_UsesMinValueRatherThanNull_ForAnEmptyRollup()
        {
            // No discovery date on the only video, so the rollup has nothing to aggregate. The point
            // is that it lands on MinValue and not on null: Jellyfin stores a null here and reads it
            // back as MinValue, so publishing null would report a change on every pass for ever.
            var video = VideoWith("3f9a2b1c-0000-0000-0000-000000000072", "Youtube", null);
            video.FirstSeenAt = null;
            video.SeasonNumber = 2026;
            video.EpisodeNumber = 1;

            var series = await PublishSeriesAsync(video);

            Assert.NotNull(series.DateLastMediaAdded);
            Assert.Equal(DateTime.MinValue, series.DateLastMediaAdded!.Value);
            // And the added date is left alone rather than forced to a poison value.
            Assert.Null(LibraryPublisher.DateAddedFor(video, useReleaseDate: false));
        }

        private async Task<Series> PublishSeriesAsync(params VideoRecord[] videos)
            => await PublishSeriesAsync(useReleaseDate: false, videos);

        private async Task<Series> PublishSeriesAsync(bool useReleaseDate, params VideoRecord[] videos)
        {
            var captured = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => captured.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Series, "Test", videos,
                videos.Select(v => v.Id).ToHashSet(), "http://localhost:8096",
                useReleaseDate, CancellationToken.None);

            return Assert.Single(captured.OfType<Series>());
        }

        /// <summary>
        /// Turning the option on and back off must restore the original dates. This is the guard on the
        /// trap rejected in the spec: a field written in only one direction is not reversible.
        /// </summary>
        [Fact]
        public void DateAddedFor_RestoresTheOriginalDate_WhenTheOptionIsTurnedBackOff()
        {
            var seen = new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero);
            var released = new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var video = VideoWith("3f9a2b1c-0000-0000-0000-000000000073", "Youtube", null);
            video.FirstSeenAt = seen;
            video.PublishedAt = released;

            Assert.Equal(seen.UtcDateTime, LibraryPublisher.DateAddedFor(video, useReleaseDate: false));
            Assert.Equal(released.UtcDateTime, LibraryPublisher.DateAddedFor(video, useReleaseDate: true));
            Assert.Equal(seen.UtcDateTime, LibraryPublisher.DateAddedFor(video, useReleaseDate: false));
        }

        private static VideoRecord VideoWith(string vodId, string? extractorKey, string? channelsJson)
            => new()
            {
                Id = vodId,
                SourceUrl = "https://example.com/source",
                VideoUrl = "https://example.com/watch/" + vodId,
                Title = "Studio subject",
                DurationSeconds = 120,
                ExtractorKey = extractorKey,
                Channels = channelsJson,
                // Always set in production (SyncService assigns syncStart), and it must be here too:
                // a default DateTimeOffset derives a MinValue date added, which AddChild then treats
                // as unset and replaces — the item would differ on every pass for ever.
                SyncedAt = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)
            };

        /// <summary>
        /// Jellyfin cannot store an empty studio list faithfully: it writes one as <c>""</c> and reads
        /// it back as <c>[""]</c>, because BaseItemMapper splits that column without the
        /// IsNullOrWhiteSpace guard it applies to Genres and Tags. An item with no studio — every Arte
        /// item — therefore came back differing from what was written and was rewritten on every pass,
        /// for ever, defeating the rule that an unchanged item is left alone.
        /// <para>
        /// Feeding the same in-memory item back, as the pass-two test above does, cannot catch this:
        /// nothing round-trips through the mapper. The stored shape is simulated explicitly instead.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_LeavesAStudiolessItemAlone_WhenJellyfinReadsItBackAsOneEmptyEntry()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000057";
            var created = await PublishOneMovieAsync(vodId, "ArteTV", channelsJson: null);
            Assert.Empty(created.Studios);

            // What Jellyfin hands back for the row it just wrote.
            created.Studios = [string.Empty];

            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                _studioAnchor,
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test",
                new[] { VideoWith(vodId, "ArteTV", null) },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
            await _library.DidNotReceive().UpdateItemAsync(
                Arg.Any<BaseItem>(), Arg.Any<BaseItem>(), Arg.Any<ItemUpdateType>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Observed live: one video of a real channel carried both "Le monde de Jamy" and
        /// "Le Monde de Jamy" in its yt-dlp tags. Jellyfin deduplicates the column case-insensitively
        /// on write, so it stored fewer tags than were written and that single item reported
        /// <c>1 updated</c> on every pass, indefinitely, on a source that had not changed.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_LeavesAnItemAlone_WhenItsTagsDifferOnlyByCase()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000060";
            var video = VideoWith(vodId, "Youtube", null);
            video.Tags = """["Le monde de Jamy","Le Monde de Jamy","Sciences"]""";

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var first = await _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            Assert.Equal(1, first.Created);
            Assert.NotNull(created);
            Assert.Equal(["Le monde de Jamy", "Sciences"], created.Tags);

            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
        }

        /// <summary>
        /// '|' is how Jellyfin joins the stored column, so a channel carrying one would be read back
        /// as two studios and never match what was written.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_KeepsTheSeparatorOutOfStudioNames()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000058";
            var created = await PublishOneMovieAsync(
                vodId, "Youtube", """["Hardware | Software"]""");

            Assert.Equal(["Hardware / Software (Youtube)"], created.Studios);
            Assert.DoesNotContain('|', Assert.Single(created.Studios));
        }

        /// <summary>
        /// Jellyfin applies Distinct(OrdinalIgnoreCase) when writing the column, so keeping two case
        /// variants would mean reading back fewer studios than were written — on every pass.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_DeduplicatesStudiosCaseInsensitively()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000059";
            var created = await PublishOneMovieAsync(
                vodId, "Youtube", """["Terrapodia","TERRAPODIA"]""");

            Assert.Equal(["Terrapodia (Youtube)"], created.Studios);
        }

        /// <summary>
        /// Jellyfin materialises Studio items in a post-scan validator, which never runs for items
        /// published outside a scan. Without this call the studio is a name no query resolves, and
        /// its page in the client sits on a loading spinner until the next library scan.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_CreatesTheStudioEntity()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000055";
            await PublishOneMovieAsync(vodId, "Youtube", """["Terrapodia"]""");

            _library.Received(1).GetStudio("Terrapodia (Youtube)");
        }

        [Fact]
        public async Task ReconcileAsync_CreatesNoStudioEntity_WhenThePlatformReportsNoChannel()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000056";
            await PublishOneMovieAsync(vodId, "ArteTV", channelsJson: null);

            _library.DidNotReceive().GetStudio(Arg.Any<string>());
        }

        /// <summary>
        /// Shared across both passes of the idempotence test: a fresh anchor would give the item a
        /// different ParentId and report an update for that reason rather than for its studios.
        /// </summary>
        private readonly Folder _studioAnchor = new() { Id = Guid.NewGuid(), Path = "/media/Vods" };

        private async Task<BaseItem> PublishOneMovieAsync(
            string vodId, string? extractorKey, string? channelsJson)
        {
            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var report = await _publisher.ReconcileAsync(
                _studioAnchor,
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test",
                new[] { VideoWith(vodId, extractorKey, channelsJson) },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(1, report.Created);
            Assert.NotNull(created);
            return created;
        }
    }
}

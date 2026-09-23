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
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
                "http://localhost:8096",
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
                "http://localhost:8096",
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
                "http://localhost:8096",
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
                "http://localhost:8096",
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
                "http://localhost:8096",
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
                "http://localhost:8096",
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
                "http://localhost:8096",
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
                "http://localhost:8096",
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
                "http://localhost:8096",
                CancellationToken.None);

            Assert.Equal(1, second.Updated);
            Assert.Equal(0, second.Unchanged);
            await _library.Received(1).UpdateItemAsync(
                created, Arg.Any<BaseItem>(), ItemUpdateType.MetadataEdit,
                Arg.Any<CancellationToken>());
        }
    }
}

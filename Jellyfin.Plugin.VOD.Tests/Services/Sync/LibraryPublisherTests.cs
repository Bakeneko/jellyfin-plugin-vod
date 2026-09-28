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
                "http://localhost:8096",
                CancellationToken.None);

            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
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
                Channels = channelsJson
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
                "http://localhost:8096",
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
                new HashSet<string> { vodId }, "http://localhost:8096", CancellationToken.None);

            Assert.Equal(1, first.Created);
            Assert.NotNull(created);
            Assert.Equal(["Le monde de Jamy", "Sciences"], created.Tags);

            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                _studioAnchor, new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096", CancellationToken.None);

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
                "http://localhost:8096",
                CancellationToken.None);

            Assert.Equal(1, report.Created);
            Assert.NotNull(created);
            return created;
        }
    }
}

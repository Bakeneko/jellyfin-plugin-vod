using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
        public void ResolveTarget_Throws_WhenLibraryUnknown()
        {
            _library.GetVirtualFolders().Returns([]);
            Assert.Throws<InvalidOperationException>(() => _publisher.ResolveTarget("Vods"));
        }

        [Fact]
        public void ResolveTarget_Throws_WhenPhysicalFolderMissing()
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

            var ex = Assert.Throws<InvalidOperationException>(() => _publisher.ResolveTarget("Vods"));
            Assert.Contains("/media/Vods", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ResolveTarget_ReturnsPhysicalFolder()
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

            Assert.Same(folder, _publisher.ResolveTarget("Vods").Anchor);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData(CollectionTypeOptions.mixed, true)]
        [InlineData(CollectionTypeOptions.movies, false)]
        [InlineData(CollectionTypeOptions.tvshows, false)]
        public void ResolveTarget_CarriesTheCollectionType(
            CollectionTypeOptions? type, bool expectedMixed)
        {
            // Read from the same VirtualFolderInfo the location comes from, so learning the type
            // costs no extra query.
            var folder = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            _library.GetVirtualFolders().Returns(
            [
                new VirtualFolderInfo
                {
                    Name = "Vods",
                    ItemId = Guid.NewGuid().ToString("N"),
                    Locations = ["/media/Vods"],
                    CollectionType = type
                }
            ]);
            _library.FindByPath("/media/Vods", true).Returns(folder);

            var target = _publisher.ResolveTarget("Vods");
            Assert.Equal(type, target.CollectionType);
            Assert.Equal(expectedMixed, target.IsMixed);
            Assert.Equal("Vods", target.LibraryName);
        }

        [Fact]
        public void ResolveTarget_MatchesTheLibraryNameCaseInsensitively()
        {
            // The name is stored from a dropdown but can be edited, renamed, or carried over from an
            // older configuration. Jellyfin's own library lookup is case-insensitive.
            var folder = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            _library.GetVirtualFolders().Returns(
            [
                new VirtualFolderInfo
                {
                    Name = "VODs",
                    ItemId = Guid.NewGuid().ToString("N"),
                    Locations = ["/media/Vods"]
                }
            ]);
            _library.FindByPath("/media/Vods", true).Returns(folder);

            Assert.Same(folder, _publisher.ResolveTarget("vods").Anchor);
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Series, "Arte",
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Series, "Arte",
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
            // id. The stale Episode's id does not match any expected member's id, so TakeById never
            // claims it. After the per-video loop, processedVodIds contains the video's id, so the
            // orphan loop's guard does not skip it — the Episode is deleted there.
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
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
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
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
            var created = await PublishOneMovieAsync(vodId, "Youtube", ["Terrapodia"]);

            Assert.Equal(["Terrapodia (Youtube)"], created.Studios);
        }

        [Fact]
        public async Task ReconcileAsync_PublishesEveryReportedChannel()
        {
            // yt-dlp reports channel and uploader separately; they diverge on auto-generated
            // "Topic" channels, where the uploader is the artist behind the channel.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000051";
            var created = await PublishOneMovieAsync(
                vodId, "Youtube", ["Some Artist - Topic", "Some Artist"]);

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
            var created = await PublishOneMovieAsync(vodId, "ArteTV", channels: null);

            Assert.Empty(created.Studios);
        }

        [Fact]
        public async Task ReconcileAsync_FallsBackToBareChannelNames_WhenTheExtractorIsUnknown()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000053";
            var created = await PublishOneMovieAsync(vodId, null, ["Somewhere"]);

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
            var created = await PublishOneMovieAsync(vodId, "Youtube", ["Terrapodia"]);

            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                StudioTarget, [],
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test",
                new[] { VideoWith(vodId, "Youtube", ["Terrapodia"]) },
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
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { video.Id }, "http://localhost:8096",
                useReleaseDate, CancellationToken.None);

        private Task<ReconcileReport> ReconcileSeriesAsync(VideoRecord video, bool useReleaseDate)
            => _publisher.ReconcileAsync(
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
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

        [Theory]
        [InlineData(null, true)]
        [InlineData(CollectionTypeOptions.mixed, true)]
        [InlineData(CollectionTypeOptions.movies, false)]
        [InlineData(CollectionTypeOptions.tvshows, false)]
        public async Task ReconcileAsync_AppliesTheReleaseDateOnlyToAMixedTarget(
            CollectionTypeOptions? type, bool expectRelease)
        {
            // The option exists because a mixed library defaults to the "Folders" view. A typed
            // library does not have that problem, so overwriting the date added there would cost the
            // known price — a channel active for years never surfacing in "Recently added" — and buy
            // nothing.
            var seen = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var released = new DateTimeOffset(2019, 3, 3, 0, 0, 0, TimeSpan.Zero);

            var video = VideoWith("3f9a2b1c-0000-0000-0000-000000000090", "Youtube", null);
            video.FirstSeenAt = seen;
            video.PublishedAt = released;

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", _studioAnchor, type), [],
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { video.Id }, "http://localhost:8096",
                useReleaseDateAsDateAdded: true, CancellationToken.None);

            Assert.NotNull(created);
            Assert.Equal(
                expectRelease ? released.UtcDateTime : seen.UtcDateTime,
                created.DateCreated);
        }

        [Fact]
        public async Task ReconcileAsync_RestoresTheDateAdded_WhenAMixedLibraryIsLaterTyped()
        {
            // Re-typing a library is as reversible as turning the option off, because the option-off
            // branch assigns FirstSeenAt rather than leaving the field alone. A field written in one
            // direction only is never reversible, whatever is kept beside it.
            var seen = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var released = new DateTimeOffset(2019, 3, 3, 0, 0, 0, TimeSpan.Zero);

            var video = VideoWith("3f9a2b1c-0000-0000-0000-000000000091", "Youtube", null);
            video.FirstSeenAt = seen;
            video.PublishedAt = released;

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await ReconcileInto(new LibraryTarget("Vods", _studioAnchor, null), video);
            Assert.Equal(released.UtcDateTime, created!.DateCreated);

            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            await ReconcileInto(
                new LibraryTarget("Vods", _studioAnchor, CollectionTypeOptions.movies), video);
            Assert.Equal(seen.UtcDateTime, created.DateCreated);
        }

        private Task<ReconcileReport> ReconcileInto(LibraryTarget target, VideoRecord video)
            => _publisher.ReconcileAsync(
                target, [], new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { video.Id }, "http://localhost:8096",
                useReleaseDateAsDateAdded: true, CancellationToken.None);

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
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
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
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
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

        /// <summary>
        /// Publishes a series from a caller-supplied source, so a test can carry artwork on it.
        /// <see cref="PublishSeriesAsync(VideoRecord[])"/> builds a bare record and cannot.
        /// </summary>
        private async Task<Series> PublishSeriesWithSourceAsync(
            SourceRecord source, params VideoRecord[] videos)
        {
            var captured = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => captured.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                StudioTarget, [], source,
                SourceMode.Series, "Test", videos,
                videos.Select(v => v.Id).ToHashSet(), "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            return Assert.Single(captured.OfType<Series>());
        }

        private async Task<Series> PublishSeriesAsync(bool useReleaseDate, params VideoRecord[] videos)
        {
            var captured = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => captured.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
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

        /// <summary>
        /// Jellyfin builds a library's own tile from its children's Backdrop, not their Primary:
        /// BaseDynamicImageProvider sets <c>useBackdrop</c> for a CollectionFolder and only falls back
        /// to Primary then Thumb. A published item carrying no Backdrop therefore leaves the library
        /// without a generated image, which is what these three tests pin.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_PublishesTheStillAsBackdrop_ForAMovie()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000060";
            const string still = "https://example.test/still.jpg";

            var video = VideoWith(vodId, "Youtube", null);
            video.ThumbnailUrl = still;

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            Assert.NotNull(created);
            Assert.Equal(still, created.GetImageInfo(ImageType.Backdrop, 0)?.Path);
        }

        /// <summary>
        /// The Backdrop follows the series image, not the Thumb's banner: a YouTube channel banner is
        /// about 6.2:1, so the 16:9 collage tile would crop it to an unrecognisable strip.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_PublishesTheSeriesImageAsBackdrop_NotTheBanner()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000061";
            const string avatar = "https://example.test/avatar.jpg";

            var video = VideoWith(vodId, "Youtube", null);
            video.ThumbnailUrl = "https://example.test/still.jpg";

            var series = await PublishSeriesWithSourceAsync(
                new SourceRecord
                {
                    Url = "https://example.com/source",
                    SeriesThumbnailUrl = avatar,
                    SeriesBannerUrl = "https://example.test/banner.jpg"
                },
                video);

            Assert.Equal(avatar, series.GetImageInfo(ImageType.Backdrop, 0)?.Path);
        }

        /// <summary>
        /// Arte exposes no image at collection level, so the series image is the first episode's still
        /// and the Backdrop must follow it there too.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_FallsBackToTheFirstStillAsBackdrop_WhenTheSourceHasNoImage()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000062";
            const string still = "https://example.test/still.jpg";

            var video = VideoWith(vodId, "ArteTV", null);
            video.ThumbnailUrl = still;

            var series = await PublishSeriesWithSourceAsync(
                new SourceRecord { Url = "https://example.com/source" }, video);

            Assert.Equal(still, series.GetImageInfo(ImageType.Backdrop, 0)?.Path);
        }

        private static VideoRecord VideoWith(string vodId, string? extractorKey, IReadOnlyList<string>? channels)
            => new()
            {
                Id = vodId,
                SourceUrl = "https://example.com/source",
                VideoUrl = "https://example.com/watch/" + vodId,
                Title = "Studio subject",
                DurationSeconds = 120,
                ExtractorKey = extractorKey,
                Extras = new VideoExtras { Channels = channels ?? [] },
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
            var created = await PublishOneMovieAsync(vodId, "ArteTV", channels: null);
            Assert.Empty(created.Studios);

            // What Jellyfin hands back for the row it just wrote.
            created.Studios = [string.Empty];

            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                StudioTarget, [],
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
            video.Extras = video.Extras with { Tags = ["Le monde de Jamy", "Le Monde de Jamy", "Sciences"] };

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var first = await _publisher.ReconcileAsync(
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
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
                StudioTarget, [], new SourceRecord { Url = "https://example.com/source" },
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
                vodId, "Youtube", ["Hardware | Software"]);

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
                vodId, "Youtube", ["Terrapodia", "TERRAPODIA"]);

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
            await PublishOneMovieAsync(vodId, "Youtube", ["Terrapodia"]);

            _library.Received(1).GetStudio("Terrapodia (Youtube)");
        }

        [Fact]
        public async Task ReconcileAsync_CreatesNoStudioEntity_WhenThePlatformReportsNoChannel()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000056";
            await PublishOneMovieAsync(vodId, "ArteTV", channels: null);

            _library.DidNotReceive().GetStudio(Arg.Any<string>());
        }

        /// <summary>
        /// The duplicate the split creates. Flipping a source's mode while the two libraries differ
        /// publishes the Episode in one and leaves the Movie in the other; the flip-detection delete
        /// only ever saw one anchor, so nothing removed the stale kind and the video existed twice.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_DeletesTheOtherKind_WhenItLivesInTheOtherLibrary()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000100";
            var otherAnchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Films" };

            var stale = new Movie { Id = DeterministicId("movie"), ParentId = otherAnchor.Id };
            stale.SetProviderId("VOD", vodId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { stale });

            var video = VideoWith(vodId, "Youtube", null);
            await _publisher.ReconcileAsync(
                StudioTarget, new[] { otherAnchor },
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Series, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            _library.Received(1).DeleteItem(
                stale, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_DeletesEveryStaleCandidate_NotJustTheFirst()
        {
            // Review focus 3. Two libraries can each hold a leftover of the wrong kind once a source
            // has moved twice. Keeping only the first would leave the second orphaned for ever,
            // since the next pass finds the right kind and never looks again.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000101";
            var first = new Folder { Id = Guid.NewGuid(), Path = "/media/A" };
            var second = new Folder { Id = Guid.NewGuid(), Path = "/media/B" };

            var staleA = new Movie { Id = DeterministicId("stale-a"), ParentId = first.Id };
            staleA.SetProviderId("VOD", vodId);
            var staleB = new Movie { Id = DeterministicId("stale-b"), ParentId = second.Id };
            staleB.SetProviderId("VOD", vodId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { staleA, staleB });

            var video = VideoWith(vodId, "Youtube", null);
            await _publisher.ReconcileAsync(
                StudioTarget, new[] { first, second },
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Series, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            _library.Received(1).DeleteItem(staleA, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            _library.Received(1).DeleteItem(staleB, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_MovesSeriesAndEpisodeThroughAddChild_WhenLibraryChanges()
        {
            // Flipping SeriesLibraryName from "Old" to "Vods" (StudioTarget) must commit the Series,
            // its Season and its Episode through AddChild so Jellyfin recomputes TopParentId for all.
            // The movie-mode equivalent is ReconcileAsync_ReparentsThroughAddChild_KeepingTheItemId.
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            const string vodId = "3f9a2b1c-0000-0000-0000-000000000300";
            var oldAnchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Old" };

            // Series in the old anchor — simulates what was stored before the library change.
            var seriesKey = $"vod-source:https://example.com/s|Series";
            var seriesId = DeterministicId(seriesKey);
            var existingSeries = new Series { Id = seriesId, ParentId = oldAnchor.Id };
            _library.GetItemById(seriesId).Returns(existingSeries);

            // Episode in the old anchor (appears in ExistingByVodId query).
            var stale = new Episode { Id = DeterministicId($"vod:{vodId}|Episode"), ParentId = oldAnchor.Id };
            stale.SetProviderId("VOD", vodId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { stale });

            var created = new List<(BaseItem item, BaseItem parent)>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add((call.ArgAt<BaseItem>(0), call.ArgAt<BaseItem>(1))));

            var video = new VideoRecord
            {
                Id = vodId, SourceUrl = "https://example.com/s", VideoUrl = "http://localhost:8096/stream.m3u8",
                Title = "Ep 1", SeasonNumber = 1, EpisodeNumber = 1,
                FirstSeenAt = DateTimeOffset.UtcNow, SyncedAt = DateTimeOffset.UtcNow
            };

            await _publisher.ReconcileAsync(
                StudioTarget, new[] { oldAnchor },
                new SourceRecord { Url = "https://example.com/s" },
                SourceMode.Series, "Test Series", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            // Series must have been committed through AddChild (CreateItem), not UpdateToRepositoryAsync.
            Assert.Contains(created, t => t.item is Series && t.item.Id == seriesId);
            // Episode must also go through AddChild with seriesMoved = true.
            Assert.Contains(created, t => t.item is Episode);
        }

        [Fact]
        public async Task ReconcileAsync_ReparentsThroughAddChild_KeepingTheItemId()
        {
            // Changing the target library must MOVE the item, not replace it: the id is derived from
            // the content key, and deleting to recreate would take the user's watch history with it.
            // AddChild is the path a live probe exercised on a library change; it upserts and keeps
            // the id, and Jellyfin recomputes TopParentId from the new parent on persist.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000102";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var oldAnchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Old" };

            var moved = new Movie { Id = DeterministicId($"vod:{vodId}|Movie"), ParentId = oldAnchor.Id };
            moved.SetProviderId("VOD", vodId);
            var originalId = moved.Id;
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { moved });

            BaseItem? added = null;
            BaseItem? addedTo = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call =>
                {
                    added = call.ArgAt<BaseItem>(0);
                    addedTo = call.ArgAt<BaseItem>(1);
                });

            var video = VideoWith(vodId, "Youtube", null);
            await _publisher.ReconcileAsync(
                StudioTarget, new[] { oldAnchor },
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            Assert.Same(moved, added);
            Assert.Same(_studioAnchor, addedTo);
            Assert.Equal(originalId, moved.Id);
            Assert.Equal(_studioAnchor.Id, moved.ParentId);
            _library.DidNotReceive().DeleteItem(moved, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_SweepsEachAnchorOnlyOnce_WhenTwoNamesResolveToTheSameFolder()
        {
            // Review focus 1. Two configured names differing only in case resolve to ONE folder. A
            // sweep deduplicating by name rather than by id would list the target twice and sweep it
            // against itself.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000103";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var same = _studioAnchor;

            var published = new Movie { Id = DeterministicId($"vod:{vodId}|Movie"), ParentId = same.Id };
            published.SetProviderId("VOD", vodId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { published });

            var video = VideoWith(vodId, "Youtube", null);
            var report = await _publisher.ReconcileAsync(
                StudioTarget, new[] { same },
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            Assert.Equal(0, report.Removed);
            _library.DidNotReceive().DeleteItem(published, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_BehavesIdentically_WhenThereIsOnlyOneAnchor()
        {
            // The non-regression that matters most: someone who keeps a single mixed library must
            // see exactly what 7f4670b did.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000104";
            var video = VideoWith(vodId, "Youtube", null);

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var report = await _publisher.ReconcileAsync(
                StudioTarget, [],
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            Assert.Equal(1, report.Created);
            Assert.Equal(0, report.Removed);
            Assert.NotNull(created);
            Assert.Equal(_studioAnchor.Id, created.ParentId);
        }

        [Fact]
        public void PurgeSource_DeletesTheSourcesItemsInEveryAnchor()
        {
            var other = new Folder { Id = Guid.NewGuid(), Path = "/media/Films" };

            var here = new Movie { Id = DeterministicId("here"), ParentId = _studioAnchor.Id };
            here.SetProviderId("VOD", "3f9a2b1c-0000-0000-0000-000000000110");
            var there = new Movie { Id = DeterministicId("there"), ParentId = other.Id };
            there.SetProviderId("VOD", "3f9a2b1c-0000-0000-0000-000000000111");
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { here, there });

            var removed = _publisher.PurgeSource(
                new[] { _studioAnchor, other },
                new SourceRecord { Url = "https://example.com/source" },
                new HashSet<string>());

            Assert.Equal(2, removed);
            _library.Received(1).DeleteItem(here, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            _library.Received(1).DeleteItem(there, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public void PurgeSource_SparesAVideoAnotherSourceStillClaims()
        {
            // The same rule as the orphan sweep: an item survives while ANY source still accounts for
            // its video. Retiring one source must not delete a video a second one shares.
            const string shared = "3f9a2b1c-0000-0000-0000-000000000112";
            var kept = new Movie { Id = DeterministicId("kept"), ParentId = _studioAnchor.Id };
            kept.SetProviderId("VOD", shared);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { kept });

            var removed = _publisher.PurgeSource(
                new[] { _studioAnchor },
                new SourceRecord { Url = "https://example.com/source" },
                new HashSet<string> { shared });

            Assert.Equal(0, removed);
            _library.DidNotReceive().DeleteItem(kept, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public void SweepAnchorsFor_ListsTheOtherModesLibraryAndTheStoredAnchor()
        {
            // Both the sync and the live probe reconcile with these anchors. The probe used to pass
            // none, so after a mode or library change it created a duplicate in the new anchor and
            // left the old item standing until the next sync healed it.
            var movies = new Folder { Id = Guid.NewGuid(), Path = "/media/Movies" };
            var stored = new Folder { Id = Guid.NewGuid(), Path = "/media/Old" };
            _library.GetVirtualFolders().Returns(
            [
                new VirtualFolderInfo
                {
                    Name = "Movies",
                    ItemId = Guid.NewGuid().ToString("N"),
                    Locations = ["/media/Movies"]
                }
            ]);
            _library.FindByPath("/media/Movies", true).Returns(movies);
            _library.GetItemById(stored.Id).Returns(stored);

            var config = new PluginConfiguration { MoviesLibraryName = "Movies", SeriesLibraryName = "Series" };
            var anchors = _publisher.SweepAnchorsFor(
                config,
                new SourceDefinition { Url = "https://example.com", Mode = SourceMode.Series },
                new SourceRecord { Url = "https://example.com", PublishedAnchorId = stored.Id });

            Assert.Equal(2, anchors.Count);
            Assert.Contains(movies, anchors);
            Assert.Contains(stored, anchors);
        }

        [Fact]
        public void SweepAnchorsFor_SkipsAnAnchorThatNoLongerResolves()
        {
            // Best effort by design: these anchors only ever serve cleanup, so one that has gone
            // must not fail a source whose own target is fine.
            _library.GetVirtualFolders().Returns([]);

            var anchors = _publisher.SweepAnchorsFor(
                new PluginConfiguration { MoviesLibraryName = "Gone", SeriesLibraryName = "Series" },
                new SourceDefinition { Url = "https://example.com", Mode = SourceMode.Series },
                new SourceRecord { Url = "https://example.com" });

            Assert.Empty(anchors);
        }

        [Fact]
        public void FindAnchorById_ReturnsNull_WhenTheAnchorIsGoneOrIsNotAFolder()
        {
            // Review focus 2. A stored anchor outlives the library it named. It only ever serves
            // cleanup, so its disappearance must not fail a source whose own target is fine.
            Assert.Null(_publisher.FindAnchorById(Guid.Empty));

            var missing = Guid.NewGuid();
            _library.GetItemById(missing).Returns((BaseItem?)null);
            Assert.Null(_publisher.FindAnchorById(missing));

            var notAFolder = Guid.NewGuid();
            _library.GetItemById(notAFolder).Returns(new Movie { Id = notAFolder });
            Assert.Null(_publisher.FindAnchorById(notAFolder));

            var real = Guid.NewGuid();
            var folder = new Folder { Id = real, Path = "/media/Real" };
            _library.GetItemById(real).Returns(folder);
            Assert.Same(folder, _publisher.FindAnchorById(real));
        }

        [Fact]
        public async Task ReconcileAsync_PurgesEmptySeriesShell_InNonTargetAnchor()
        {
            // spec §14. After a source moves to a new library, the old one may still hold an empty
            // Series container. The swept[1..] loop reaps it if no episode lives under it.
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            const string vodId = "3f9a2b1c-0000-0000-0000-000000000301";
            var oldAnchor = new Folder { Id = Guid.NewGuid(), Path = "/media/OldShell" };

            // An empty Series shell that FindSeries will find in the old anchor.
            var shell = new Series { Id = Guid.NewGuid() };
            shell.SetProviderId("VOD", "https://example.com/shell");

            // ExistingByVodId (queries Episode/Movie): returns nothing.
            _library.GetItemList(Arg.Is<InternalItemsQuery>(q =>
                !q.IncludeItemTypes.Contains(BaseItemKind.Series)))
                .Returns(new List<BaseItem>());
            // FindSeries for old anchor: returns the shell.
            _library.GetItemList(Arg.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.Series)
                && q.TopParentIds.Contains(oldAnchor.Id)))
                .Returns(new List<BaseItem> { shell });
            // ChildCount for the shell: 0 episodes.
            _library.GetCount(Arg.Is<InternalItemsQuery>(q => q.ParentId == shell.Id)).Returns(0);

            var video = new VideoRecord
            {
                Id = vodId, SourceUrl = "https://example.com/shell", VideoUrl = "u",
                Title = "Ep", SeasonNumber = 1, EpisodeNumber = 1,
                FirstSeenAt = DateTimeOffset.UtcNow, SyncedAt = DateTimeOffset.UtcNow
            };

            await _publisher.ReconcileAsync(
                StudioTarget, new[] { oldAnchor },
                new SourceRecord { Url = "https://example.com/shell" },
                SourceMode.Series, "Shell Series", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            _library.Received(1).DeleteItem(shell, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_SparesSeriesShell_InNonTargetAnchor_WhenItHasEpisodes()
        {
            // spec §14. A Series shell that still holds episodes must not be reaped.
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            const string vodId = "3f9a2b1c-0000-0000-0000-000000000302";
            var oldAnchor = new Folder { Id = Guid.NewGuid(), Path = "/media/OldLive" };

            var activeShell = new Series { Id = Guid.NewGuid() };
            activeShell.SetProviderId("VOD", "https://example.com/active");

            _library.GetItemList(Arg.Is<InternalItemsQuery>(q =>
                !q.IncludeItemTypes.Contains(BaseItemKind.Series)))
                .Returns(new List<BaseItem>());
            _library.GetItemList(Arg.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.Series)
                && q.TopParentIds.Contains(oldAnchor.Id)))
                .Returns(new List<BaseItem> { activeShell });
            _library.GetCount(Arg.Is<InternalItemsQuery>(q => q.ParentId == activeShell.Id)).Returns(2);

            var video = new VideoRecord
            {
                Id = vodId, SourceUrl = "https://example.com/active", VideoUrl = "u",
                Title = "Ep", SeasonNumber = 1, EpisodeNumber = 1,
                FirstSeenAt = DateTimeOffset.UtcNow, SyncedAt = DateTimeOffset.UtcNow
            };

            await _publisher.ReconcileAsync(
                StudioTarget, new[] { oldAnchor },
                new SourceRecord { Url = "https://example.com/active" },
                SourceMode.Series, "Active Series", new[] { video },
                new HashSet<string> { vodId }, "http://localhost:8096",
                useReleaseDateAsDateAdded: false, CancellationToken.None);

            _library.DidNotReceive().DeleteItem(activeShell, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        /// <summary>
        /// Shared across both passes of the idempotence test: a fresh anchor would give the item a
        /// different ParentId and report an update for that reason rather than for its studios.
        /// </summary>
        private readonly Folder _studioAnchor = new() { Id = Guid.NewGuid(), Path = "/media/Vods" };

        /// <summary>
        /// The anchor as a target. Mixed (CollectionType null) on purpose: that is what every test
        /// written before the split assumed, so the option keeps biting exactly where it used to.
        /// </summary>
        private LibraryTarget StudioTarget => new("Vods", _studioAnchor, null);

        private async Task<BaseItem> PublishOneMovieAsync(
            string vodId, string? extractorKey, IReadOnlyList<string>? channels)
        {
            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var report = await _publisher.ReconcileAsync(
                StudioTarget, [],
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test",
                new[] { VideoWith(vodId, extractorKey, channels) },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(1, report.Created);
            Assert.NotNull(created);
            return created;
        }

        // VideoRecord is a sealed class with settable properties, not a record, so there is no
        // `with` expression available: every variant is built here.
        private static VideoRecord TieredVideo(
            string vodId,
            LiveStatus liveStatus = LiveStatus.NotLive,
            int? seasonNumber = null,
            int? episodeNumber = null,
            VideoQuality[]? qualities = null) => new()
        {
            Id = vodId,
            Title = "A Video Title",
            VideoUrl = "https://example.com/watch?v=abc",
            SourceUrl = "https://example.com/channel",
            LiveStatus = liveStatus,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            Extras = new VideoExtras
            {
                Qualities = qualities ??
                [
                    new VideoQuality(1080, "h264", 30),
                    new VideoQuality(2160, "vp9", 30)
                ]
            }
        };

        [Fact]
        public void MembersFor_ReturnsThePrimaryAlone_WhenTheVideoIsLive()
        {
            // A live ladder is volatile and its URLs rotate, so tiers captured at sync may already
            // be wrong. Offering none is the honest answer.
            var members = _publisher.MembersFor(
                TieredVideo("3f9a2b1c-0000-0000-0000-000000000050", liveStatus: LiveStatus.Live),
                "http://localhost:8096");

            var primary = Assert.Single(members);
            Assert.Null(primary.Tier);
        }

        [Fact]
        public void MembersFor_ReturnsThePrimaryAndItsTiers_WhenTheVideoIsNotLive()
        {
            var members = _publisher.MembersFor(
                TieredVideo("3f9a2b1c-0000-0000-0000-000000000051"),
                "http://localhost:8096");

            Assert.Equal(3, members.Count);
            Assert.Null(members[0].Tier);
            Assert.Equal("[1080p] A Video Title", members[1].Name);
            Assert.Equal("[2160p (VP9)] A Video Title", members[2].Name);
        }

        [Fact]
        public async Task ReconcileAsync_CreatesOneItemPerMember()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000052";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [TieredVideo(vodId)],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            // One primary plus two alternates are created under the anchor. Asserted on what the
            // publisher asked the library to create: Folder.AddChild ends in
            // LibraryManager.CreateItem, and Folder.Children is not an in-memory list, so a
            // substituted library manager never populates it.
            Assert.Equal(3, created.Count);
            // The load-bearing property of the whole one-to-many change: two items sharing a Guid
            // collide in Jellyfin's item table. Nothing asserted this before.
            Assert.Equal(3, created.Select(c => c.Id).Distinct().Count());
        }

        [Fact]
        public async Task ReconcileAsync_NamesThePrimaryAfterTheVideoAndTheAlternatesAfterTheirTier()
        {
            // The publisher rewrites Name on every sync. Without a deliberate per-member rule the
            // next sync would rename every alternate to the bare video title and the menu would turn
            // into three identical entries.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000053";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [TieredVideo(vodId)],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var names = created.Select(c => c.Name).ToList();
            Assert.Contains("A Video Title", names);
            Assert.Contains("[1080p] A Video Title", names);
            Assert.Contains("[2160p (VP9)] A Video Title", names);
        }

        [Fact]
        public async Task ReconcileAsync_GivesEachAlternateItsParameterisedPath()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000054";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [TieredVideo(vodId)],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var paths = created.Select(c => c.Path).ToList();
            Assert.Contains($"http://localhost:8096/VOD/stream/{vodId}.m3u8", paths);
            Assert.Contains($"http://localhost:8096/VOD/stream/{vodId}.m3u8?height=1080&codec=h264", paths);
            Assert.Contains($"http://localhost:8096/VOD/stream/{vodId}.m3u8?height=2160&codec=vp9", paths);
        }

        [Fact]
        public async Task ReconcileAsync_GivesEveryMemberTheSameVodProviderId()
        {
            // PurgeSource tests allKnownVideoIds.Contains(vodId) and ExistingByVodId groups on this
            // value. Encoding the tier here would make every alternate look like an unknown video
            // and get it deleted on every purge.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000055";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [TieredVideo(vodId)],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(3, created.Count);
            Assert.All(created, c => Assert.Equal(vodId, c.GetProviderId("VOD")));
        }

        [Fact]
        public async Task ReconcileAsync_GivesEveryMemberTheKeyJellyfinComputesForIt()
        {
            // The publisher must never invent a key of its own: Jellyfin's query-level
            // de-duplication is driven by this column, and a hand-made value would hide the wrong
            // item. Each member therefore carries exactly what the entity computes for itself —
            // its own Guid for the primary, and the primary's Guid for a linked alternate, which is
            // how Jellyfin hides an alternate from listings (Video.cs:434-438).
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000056";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [TieredVideo(vodId)],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(3, created.Count);
            Assert.All(created, c => Assert.Equal(c.CreatePresentationUniqueKey(), c.PresentationUniqueKey));

            var primaryId = _library.GetNewItemId(_publisher.ItemKeyFor(vodId), typeof(Movie));
            var primaryKey = primaryId.ToString("N", CultureInfo.InvariantCulture);
            Assert.Equal(
                primaryKey, created.Single(c => c.Id.Equals(primaryId)).PresentationUniqueKey);
            Assert.All(
                created.Where(c => !c.Id.Equals(primaryId)),
                c => Assert.Equal(primaryKey, c.PresentationUniqueKey));
        }

        [Fact]
        public async Task ReconcileAsync_DoesNotDeleteAnAlternate_AsIfItWereAStaleDuplicate()
        {
            // TakeOfType used to delete every item beyond the first claiming a video. An alternate
            // is an expected member, not a leftover from a flipped source mode.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000057";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var video = TieredVideo(vodId);
            var members = _publisher.MembersFor(video, "http://localhost:8096");

            var published = new List<BaseItem>();
            foreach (var member in members)
            {
                var movie = new Movie
                {
                    Id = _library.GetNewItemId(member.ItemKey, typeof(Movie)),
                    Name = member.Name,
                    Path = member.Path
                };
                movie.SetProviderId("VOD", vodId);
                published.Add(movie);
            }

            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(published);

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.DidNotReceive().DeleteItem(
                Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_CreatesAlternatesAsEpisodes_InSeriesMode()
        {
            // Spec: in Series mode the alternates are Episodes under the same season, where being
            // hidden from listings keeps them out of the season view.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000058";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            var video = TieredVideo(vodId, seasonNumber: 1, episodeNumber: 3);

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Series, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var episodes = created.OfType<Episode>().Select(e => e.Name).ToList();

            Assert.Contains("[1080p] A Video Title", episodes);
            Assert.Contains("[2160p (VP9)] A Video Title", episodes);
        }

        [Fact]
        public async Task ReconcileAsync_DeletesTheStaleAlternate_WhenATierDisappearsUpstream()
        {
            // The reason the orphan guard needed processedVodIds: the vodId is still known, so the
            // old guard would have skipped the leftover forever. The primary and the surviving
            // alternate must come through untouched.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000070";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var before = TieredVideo(vodId);
            var members = _publisher.MembersFor(before, "http://localhost:8096");
            var staleId = _library.GetNewItemId(members[2].ItemKey, typeof(Movie));

            var stale = new Movie { Id = staleId, Name = members[2].Name, Path = members[2].Path };
            stale.SetProviderId("VOD", vodId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { stale });

            var after = TieredVideo(vodId, qualities: [new VideoQuality(1080, "h264", 30)]);

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [after],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.Received(1).DeleteItem(stale, Arg.Any<DeleteOptions>(), true);
        }

        [Fact]
        public async Task ReconcileAsync_SparesItemsOwnedByAnotherSource_WhenAnotherVideoIsProcessed()
        {
            // The preservation half of the processedVodIds guard. The pre-existing preservation
            // test covers expected = []; this one exercises a pass that processes one video while
            // another known video is in the map but not in expected.
            const string ownedId = "3f9a2b1c-0000-0000-0000-000000000076";
            const string processedId = "3f9a2b1c-0000-0000-0000-000000000077";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            // An item that belongs to a different source — in allKnownVideoIds but not in expected.
            var spare = new Movie { Id = Guid.NewGuid() };
            spare.SetProviderId("VOD", ownedId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { spare });

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [VideoWith(processedId, "Youtube", null)],
                new HashSet<string> { ownedId, processedId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            _library.DidNotReceive().DeleteItem(spare, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ReconcileAsync_GivesAnAlternateTheSameDescriptiveMetadataAsThePrimary()
        {
            // An alternate used to carry only enough to be identified and played, on the reasoning
            // that being linked hides it from every listing so its artwork would never be displayed.
            // That premise is too narrow: the playback history and resume rows show the item actually
            // played, which is the alternate. Everything descriptive therefore matches the primary,
            // and only the name and the id differ.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000066";
            const string still = "https://example.test/still.jpg";

            var video = TieredVideo(vodId);
            video.Description = "What the video is about.";
            video.ThumbnailUrl = still;
            video.Extras = video.Extras with
            {
                Tags = ["space", "cosmos"],
                Categories = ["Education"],
                Channels = ["Kosmo"]
            };

            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [],
                new SourceRecord { Url = "https://example.com/source" },
                SourceMode.Movies, "Test", [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var primary = Assert.Single(created, c => c.Name == "A Video Title");
            var alternate = Assert.Single(created, c => c.Name == "[1080p] A Video Title");

            Assert.Equal(primary.Overview, alternate.Overview);
            Assert.NotNull(alternate.Overview);
            Assert.Equal(primary.Tags, alternate.Tags);
            Assert.Equal(primary.Genres, alternate.Genres);
            Assert.Equal(primary.Studios, alternate.Studios);
            Assert.Equal(primary.ProductionYear, alternate.ProductionYear);
            Assert.Equal(still, alternate.GetImageInfo(ImageType.Primary, 0)?.Path);
            Assert.Equal(still, alternate.GetImageInfo(ImageType.Thumb, 0)?.Path);
            Assert.Equal(still, alternate.GetImageInfo(ImageType.Backdrop, 0)?.Path);
        }

        [Theory]
        [InlineData(SourceMode.Movies)]
        [InlineData(SourceMode.Series)]
        public async Task ReconcileAsync_MarksEveryVideoItemAsHls(SourceMode mode)
        {
            // Jellyfin only adds aac_adtstoasc to a copied AAC track when the source container is
            // ts, aac or hls (EncodingHelper.GetAudioBitStreamArguments), and reads it from
            // item.Container. Without it the mp4 muxer rejects the audio and the client falls back to
            // a full transcode. Jellyfin's own probe used to set it; the stream describer does not.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000067";
            var video = TieredVideo(vodId);

            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [],
                new SourceRecord { Url = "https://example.com/source" },
                mode, "Test", [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            // The primary and one alternate per tier.
            var videos = created.OfType<Video>().ToList();
            Assert.Equal(3, videos.Count);
            Assert.All(videos, v => Assert.Equal("hls", v.Container));
        }

        [Fact]
        public async Task ReconcileAsync_Updates_AnItemPublishedWithoutAContainer()
        {
            // Items published before the container was set carry none; the next pass has to write it
            // rather than count them unchanged.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000068";
            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = new VideoRecord
            {
                Id = vodId,
                SourceUrl = source.Url,
                VideoUrl = "https://example.com/watch/68",
                Title = "Steady",
                DurationSeconds = 120
            };

            BaseItem? created = null;
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created = call.ArgAt<BaseItem>(0));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                new[] { video },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.NotNull(created);
            created.Container = null;
            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { created });

            var second = await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                new[] { video },
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(1, second.Updated);
            Assert.Equal("hls", created.Container);
        }

        [Fact]
        public async Task ReconcileAsync_AsksForAlternateVersions_WhenReadingWhatIsAlreadyPublished()
        {
            // Jellyfin hides every item carrying a PrimaryVersionId from any query that does not ask
            // for alternates (BaseItemRepository.TranslateQuery -> ApplyAlternateVersionFiltering);
            // its own comment there says the library scan keeps them "so a merged version is not
            // mistaken for a new item", which is exactly the failure. Without the flag this sweep
            // never sees the alternates it published: it recreates them on every pass, and the orphan
            // loop below can never reap one whose tier the upstream dropped. A substituted
            // ILibraryManager applies no such filter, so only the flag itself can be pinned here.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000065";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [TieredVideo(vodId)],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            // TopParentIds distinguishes the published-items sweep from any other query the pass makes.
            _library.Received().GetItemList(Arg.Is<InternalItemsQuery>(
                q => q.TopParentIds.Length > 0 && q.IncludeAlternateVersions));
        }

        [Fact]
        public async Task ReconcileAsync_WritesNothingForAlternates_WhenTheSecondPassFindsNothingChanged()
        {
            // Mirrors ReconcileAsync_WritesNothing_WhenTheSecondPassFindsNothingChanged but for a
            // tiered video, and in the state production reaches: pass 1 links the tiers, so pass 2
            // compares a primary that already carries its links against alternates whose
            // PresentationUniqueKey SetPrimaryVersionId has already rewritten. Jellyfin hydrates
            // both from the repository (BaseItemMapper.cs:170 for PrimaryVersionId, and
            // LinkedChildEntities for the list queries), which is what makes a second pass able to
            // see its own work and leave it alone.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000059";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = TieredVideo(vodId);

            var allCreated = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => allCreated.Add(call.ArgAt<BaseItem>(0)));
            ResolveCreatedItems(allCreated);
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.Equal(3, allCreated.Count);

            // The linking step really ran, so the comparisons pass 2 relies on are the live ones.
            var primaryId = _library.GetNewItemId(_publisher.ItemKeyFor(vodId), typeof(Movie));
            var primary = Assert.IsType<Movie>(allCreated.Single(c => c.Id.Equals(primaryId)));
            var alternates = allCreated.OfType<Movie>().Where(c => !c.Id.Equals(primaryId)).ToList();
            Assert.Equal(2, primary.LinkedAlternateVersions.Length);
            Assert.Equal(2, alternates.Count);
            Assert.All(alternates, a => Assert.Equal(primaryId, a.PrimaryVersionId));

            // Second pass: the library now holds exactly what the first pass published.
            _library.ClearReceivedCalls();
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem>(allCreated));

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            await _library.DidNotReceive().UpdateItemAsync(
                Arg.Any<BaseItem>(), Arg.Any<BaseItem>(), Arg.Any<ItemUpdateType>(),
                Arg.Any<CancellationToken>());
            _library.DidNotReceive().CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>());
        }

        [Fact]
        public async Task ReconcileAsync_LinksTheAlternatesToThePrimary()
        {
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000060";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            ResolveCreatedItems(created);
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = TieredVideo(vodId);

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var members = _publisher.MembersFor(video, "http://localhost:8096");
            var primaryId = _library.GetNewItemId(members[0].ItemKey, typeof(Movie));

            var primary = Assert.IsType<Movie>(created.Single(c => c.Id.Equals(primaryId)));
            var alternates = created
                .OfType<Movie>()
                .Where(c => !c.Id.Equals(primaryId))
                .ToList();

            Assert.Equal(2, alternates.Count);
            Assert.All(alternates, a => Assert.Equal(primaryId, a.PrimaryVersionId));
            Assert.Equal(2, primary.LinkedAlternateVersions.Length);
            Assert.Null(primary.PrimaryVersionId);

            // Jellyfin's own hiding mechanism: a linked alternate's presentation key becomes the
            // primary's Guid, which is why the publisher must store the computed key rather than a
            // string of its own. Nothing else in the suite pins that.
            var primaryKey = primaryId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            Assert.All(alternates, a => Assert.Equal(primaryKey, a.PresentationUniqueKey));
        }

        [Fact]
        public async Task ReconcileAsync_LinksThroughTheCommittedPrimary_WhenTheCachedCopyIsStale()
        {
            // Inside one Jellyfin process the two lookups disagree. GetItemList returns repository
            // instances (LibraryManager.cs:1757) and is what the publisher upserts, while
            // GetItemById answers from the LRU cache first (LibraryManager.cs:1697) and
            // UpdateItemsAsync never re-registers what it saved (LibraryManager.cs:2713-2719), so
            // the cache can still hold the copy from the pass that created the item. Writing the
            // links through that copy saves the whole stale entity and reverts the metadata an
            // earlier pass committed.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000065";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = TieredVideo(vodId);
            var members = _publisher.MembersFor(video, "http://localhost:8096");
            var primaryId = _library.GetNewItemId(members[0].ItemKey, typeof(Movie));

            // What the repository holds, and therefore what the upsert mutates and commits.
            var fresh = new Movie
            {
                Id = primaryId,
                Name = video.Title,
                ParentId = anchor.Id,
                Path = members[0].Path
            };
            fresh.SetProviderId("VOD", vodId);

            // What the cache would hand back: same id, the title and parent of an earlier pass.
            var stale = new Movie
            {
                Id = primaryId,
                Name = "A title two passes ago",
                ParentId = Guid.NewGuid(),
                Path = members[0].Path
            };
            stale.SetProviderId("VOD", vodId);

            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));

            // Captured by reference: BaseItem.Equals compares ids only (BaseItem.cs:3085), so an
            // NSubstitute argument matcher cannot tell these two instances apart.
            var saved = new List<BaseItem>();
            _library.When(l => l.UpdateItemAsync(
                    Arg.Any<BaseItem>(), Arg.Any<BaseItem>(), Arg.Any<ItemUpdateType>(),
                    Arg.Any<CancellationToken>()))
                .Do(call => saved.Add(call.ArgAt<BaseItem>(0)));

            _library.GetItemById(Arg.Any<Guid>()).Returns(call =>
                call.Arg<Guid>().Equals(primaryId)
                    ? stale
                    : created.FirstOrDefault(c => c.Id.Equals(call.Arg<Guid>())));

            // Only the primary exists; its two tiers are published by this pass, so the expected
            // links differ from the stored ones and the linking step has to write.
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { fresh });

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            Assert.NotSame(fresh, stale);
            Assert.Equal(2, fresh.LinkedAlternateVersions.Length);
            Assert.Empty(stale.LinkedAlternateVersions);

            // The stale copy must never reach the repository: UpdateToRepositoryAsync saves the
            // whole entity, so one write of it undoes every field a later pass had corrected.
            Assert.DoesNotContain(saved, s => ReferenceEquals(s, stale));
            Assert.Contains(saved, s => ReferenceEquals(s, fresh));
            Assert.Equal("A title two passes ago", stale.Name);
        }

        [Fact]
        public async Task ReconcileAsync_OrdersTheLinkedAlternatesByAscendingHeight()
        {
            // Measured on the test instance: the menu follows the primary, then this list in order.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000061";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            ResolveCreatedItems(created);
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };
            var video = TieredVideo(vodId);

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var members = _publisher.MembersFor(video, "http://localhost:8096");
            var primaryId = _library.GetNewItemId(members[0].ItemKey, typeof(Movie));
            var primary = Assert.IsType<Movie>(created.Single(c => c.Id.Equals(primaryId)));

            // LinkedChild.ItemId is a Guid?, hence the unwrap: comparing it to a Guid directly
            // leaves the generic argument ambiguous.
            Assert.Equal(
                _library.GetNewItemId(members[1].ItemKey, typeof(Movie)),
                primary.LinkedAlternateVersions[0].ItemId!.Value);
            Assert.Equal(
                _library.GetNewItemId(members[2].ItemKey, typeof(Movie)),
                primary.LinkedAlternateVersions[1].ItemId!.Value);
        }

        [Fact]
        public async Task ReconcileAsync_LinksNothing_WhenTheVideoHasNoTiers()
        {
            // An empty ladder must leave the primary exactly as it was before version groups
            // existed: no links, no alternates, playback unchanged.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000062";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            ResolveCreatedItems(created);
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            var video = new VideoRecord
            {
                Id = vodId,
                Title = "A Video Title",
                VideoUrl = "https://example.com/watch?v=abc",
                SourceUrl = "https://example.com/channel",
                Extras = new VideoExtras()
            };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [video],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var primary = Assert.IsType<Movie>(Assert.Single(created));
            Assert.Empty(primary.LinkedAlternateVersions);
        }

        [Fact]
        public async Task ReconcileAsync_UnlinksAVanishedTier_BeforeDeletingIt()
        {
            // The upstream dropped 2160p. The alternate must leave the primary's list first: a link
            // pointing at a deleted item would leave a dead entry in the version menu. The primary
            // keeps its id, and therefore its watch state.
            const string vodId = "3f9a2b1c-0000-0000-0000-000000000063";
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));

            var before = TieredVideo(vodId);
            var stale = _publisher.MembersFor(before, "http://localhost:8096")[2];
            var staleId = _library.GetNewItemId(stale.ItemKey, typeof(Movie));

            var staleItem = new Movie { Id = staleId, Name = stale.Name, Path = stale.Path };
            staleItem.SetProviderId("VOD", vodId);
            var created = new List<BaseItem>();
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => created.Add(call.ArgAt<BaseItem>(0)));
            ResolveCreatedItems(created);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { staleItem });

            var after = TieredVideo(vodId, qualities: [new VideoQuality(1080, "h264", 30)]);

            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Vods" };
            var source = new SourceRecord { Url = "https://example.com/source" };

            await _publisher.ReconcileAsync(
                new LibraryTarget("Vods", anchor, null), [], source, SourceMode.Movies, "Test",
                [after],
                new HashSet<string> { vodId },
                "http://localhost:8096", useReleaseDateAsDateAdded: false,
                CancellationToken.None);

            var members = _publisher.MembersFor(after, "http://localhost:8096");
            var primaryId = _library.GetNewItemId(members[0].ItemKey, typeof(Movie));
            var primary = Assert.IsType<Movie>(created.Single(c => c.Id.Equals(primaryId)));

            // One tier left, so one link, and it is not the vanished one.
            Assert.Single(primary.LinkedAlternateVersions);
            Assert.DoesNotContain(
                staleId, primary.LinkedAlternateVersions.Select(l => l.ItemId!.Value));
            _library.Received().DeleteItem(staleItem, Arg.Any<DeleteOptions>(), true);
        }

        [Fact]
        public void RunTimeTicks_IsNullWhileBroadcastingOrScheduled()
        {
            // An item with a duration gets a frozen playlist computed from it, so playback would stop
            // at the length known when it started. Without one, the client asks for live.m3u8 and
            // follows the growing source. Measured 2026-10-03 (spec F11).
            var live = TieredVideo("3f9a2b1c-0000-0000-0000-000000000070");
            live.DurationSeconds = 2685;
            live.LiveStatus = LiveStatus.Live;
            Assert.Null(LibraryPublisher.RunTimeTicks(live));

            var upcoming = TieredVideo("3f9a2b1c-0000-0000-0000-000000000071");
            upcoming.DurationSeconds = 0;
            upcoming.LiveStatus = LiveStatus.Upcoming;
            Assert.Null(LibraryPublisher.RunTimeTicks(upcoming));

            var finished = TieredVideo("3f9a2b1c-0000-0000-0000-000000000072");
            finished.DurationSeconds = 48698;
            finished.LiveStatus = LiveStatus.NotLive;
            Assert.Equal(48698L * TimeSpan.TicksPerSecond, LibraryPublisher.RunTimeTicks(finished));
        }

        [Fact]
        public void DateAddedAndPremiereDate_KeepALiveFirstInBothSortOrders()
        {
            // A live keeps living, so it must stay at the top of "recently added" and of the release
            // date sort even as other videos arrive after it started. Both therefore track the last
            // pass that saw it live, not when it was first seen.
            var lastPass = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);
            var live = TieredVideo("3f9a2b1c-0000-0000-0000-000000000073");
            live.LiveStatus = LiveStatus.Live;
            live.FirstSeenAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            live.PublishedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            live.SyncedAt = lastPass;

            Assert.Equal(lastPass.UtcDateTime, LibraryPublisher.DateAddedFor(live, useReleaseDate: false));
            Assert.Equal(lastPass.UtcDateTime, LibraryPublisher.DateAddedFor(live, useReleaseDate: true));
            Assert.Equal(lastPass.UtcDateTime, LibraryPublisher.PremiereDateFor(live));
        }

        [Fact]
        public void PremiereDate_OfAScheduledStreamIsItsScheduledStart()
        {
            // In the future, so it sorts first; and it follows a reschedule because PublishedAt is
            // rewritten on every fetch while the stream is Upcoming.
            var scheduled = new DateTimeOffset(2026, 10, 4, 15, 45, 0, TimeSpan.Zero);
            var upcoming = TieredVideo("3f9a2b1c-0000-0000-0000-000000000074");
            upcoming.LiveStatus = LiveStatus.Upcoming;
            upcoming.PublishedAt = scheduled;
            upcoming.SyncedAt = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);

            Assert.Equal(scheduled.UtcDateTime, LibraryPublisher.PremiereDateFor(upcoming));
        }

        [Fact]
        public void BuildVideoOverview_LeadsWithTheBroadcastState()
        {
            // The state is why the viewer is looking at this item; the source line is reference
            // material. User request 2026-10-03.
            _localizer.Get("LiveNow").Returns("Diffusion en cours");
            _localizer.Get("SourceLabel").Returns("Source : {0}");

            var live = TieredVideo("3f9a2b1c-0000-0000-0000-000000000075");
            live.LiveStatus = LiveStatus.Live;
            live.Description = "A description.";

            var overview = _publisher.BuildVideoOverview(live);

            Assert.StartsWith("Diffusion en cours", overview, StringComparison.Ordinal);
            Assert.Contains("Source : ", overview, StringComparison.Ordinal);
            Assert.True(
                overview.IndexOf("Diffusion en cours", StringComparison.Ordinal)
                < overview.IndexOf("Source : ", StringComparison.Ordinal));
        }

        [Fact]
        public void BuildVideoOverview_LeadsWithTheScheduledTime_ForAnUpcomingStream()
        {
            _localizer.Get("UpcomingBroadcast").Returns("Diffusion prévue : {0} UTC");

            var upcoming = TieredVideo("3f9a2b1c-0000-0000-0000-000000000076");
            upcoming.LiveStatus = LiveStatus.Upcoming;
            upcoming.PublishedAt = new DateTimeOffset(2026, 10, 4, 15, 45, 0, TimeSpan.Zero);

            var overview = _publisher.BuildVideoOverview(upcoming);

            Assert.StartsWith("Diffusion prévue : 2026-10-04 15:45 UTC", overview, StringComparison.Ordinal);
        }

        /// <summary>
        /// On a real library AddChild makes the item retrievable by id; a substitute returns null
        /// for every id, so a pass would run against a library that has forgotten everything it was
        /// just given — including the parent <c>BaseItem.GetParent()</c> resolves when an item is
        /// saved. Route that lookup through the items the publisher asked the library to create.
        /// </summary>
        private void ResolveCreatedItems(List<BaseItem> created)
            => _library.GetItemById(Arg.Any<Guid>())
                .Returns(call => created.FirstOrDefault(c => c.Id.Equals(call.Arg<Guid>())));
    }
}

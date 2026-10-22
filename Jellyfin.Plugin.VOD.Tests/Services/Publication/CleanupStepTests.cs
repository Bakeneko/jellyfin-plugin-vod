using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The cleanup step: the only step that deletes Jellyfin items, and the one where a logic error
    /// would silently wipe a library. Each test below pins one invariant that, if broken, would
    /// either leave orphans in the library, delete content the user still wants, or destroy watch
    /// state permanently.
    /// </summary>
    [Collection(BaseItemStaticsCollection.Name)]
    public sealed class CleanupStepTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        private const string SourceUrl = "https://www.youtube.com/@testchan/videos";
        private const string MoviesLibrary = "Films";
        private const string SeriesLibrary = "VODs";

        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly LibraryPublisher _publisher;
        private readonly CleanupStep _step;

        public CleanupStepTests()
        {
            // BaseItem routes AddChild and UpdateToRepositoryAsync through this static; setting it
            // here is also what PurgeSource needs for SetProviderId to round-trip via GetProviderId.
            BaseItem.LibraryManager = _library;
            _library.GetLibraryOptions(Arg.Any<BaseItem>())
                .Returns(new LibraryOptions { PreferredMetadataLanguage = "en" });
            _library.GetCollectionFolders(Arg.Any<BaseItem>()).Returns([]);

            var artwork = new ArtworkStore(
                Path.Combine(Path.GetTempPath(), "vod-tests-cleanup-" + Guid.NewGuid().ToString("N")));
            _publisher = new LibraryPublisher(
                _library, _db, artwork,
                Substitute.For<IVodLocalizer>(),
                NullLogger<LibraryPublisher>.Instance);
            _step = new CleanupStep(_db, _publisher, NullLogger<CleanupStep>.Instance);
        }

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

        private static SourceDefinition Source(string url = SourceUrl) => new()
        {
            Url = url,
            Enabled = true,
        };

        private static PluginConfiguration Config(
            IReadOnlyList<SourceDefinition>? sources = null,
            string movies = MoviesLibrary,
            string series = SeriesLibrary)
        {
            var config = new PluginConfiguration
            {
                MoviesLibraryName = movies,
                SeriesLibraryName = series,
            };
            if (sources != null)
                config.Sources.AddRange(sources);
            return config;
        }

        private static SourceRecord RetiredRecord(string url = SourceUrl, Guid? anchorId = null)
            => new() { Url = url, PublishedAnchorId = anchorId };

        private static VideoRecord VideoFor(string sourceUrl, string videoId) => new()
        {
            Id = videoId,
            SourceUrl = sourceUrl,
            NativeId = "native-" + videoId,
            VideoUrl = "https://example.com/" + videoId,
            Title = "Title " + videoId,
            MetadataAt = Now.AddDays(-1),
            MetadataResult = CallOutcome.Ok,
        };

        // ── RemoveVideosGoneFromTheSource: one deletion rule, three edge cases ────────────────────

        [Fact]
        public void RemoveVideosGoneFromTheSource_DeletesGoneVideo_KeepsVideoSeenAtListing()
        {
            // The listing timestamp is the only deletion rule: absent from the last full listing
            // means retire, regardless of how old the video is. A video the listing saw is kept, no
            // matter how far outside the retention window, because deleting it would destroy its
            // watch state without any reason visible in the listing data.
            const string goneId = "gone-video-id";
            const string seenId = "seen-video-id";
            _db.GetVideosGoneFromLastFullListing(SourceUrl, Now).Returns([goneId]);

            _step.RemoveVideosGoneFromTheSource(Source(), Now, CancellationToken.None);

            _db.Received(1).DeleteVideo(goneId);
            _db.DidNotReceive().DeleteVideo(seenId);
        }

        [Fact]
        public void RemoveVideosGoneFromTheSource_DeletesNothingBeyondWhatTheQueryReturned()
        {
            // The step applies no rule of its own. The retention window and the seven-day live
            // exemption live in the SQL, and they are tested against real SQLite in
            // VodDatabaseTests.GetVideosGoneFromLastFullListing_*. Two tests here used to be named
            // for those rules while asserting DidNotReceive().DeleteVideo(<an id the stub never
            // returned>) — true for any implementation that forwards the list, including a wrong
            // one. What is worth pinning here is the count: nothing extra is deleted.
            _db.GetVideosGoneFromLastFullListing(SourceUrl, Now).Returns(["gone-a", "gone-b"]);

            _step.RemoveVideosGoneFromTheSource(Source(), Now, CancellationToken.None);

            _db.Received(1).DeleteVideo("gone-a");
            _db.Received(1).DeleteVideo("gone-b");
            _db.Received(2).DeleteVideo(Arg.Any<string>());
        }

        [Fact]
        public void RemoveVideosGoneFromTheSource_RecordsSuccessResult()
        {
            // Every stage records its result so the configuration page can show consecutive failure
            // counts and the last error without a history table. A missing record would make a
            // silently-stuck source look healthy.
            _db.GetVideosGoneFromLastFullListing(SourceUrl, Now).Returns([]);

            _step.RemoveVideosGoneFromTheSource(Source(), Now, CancellationToken.None);

            _db.Received(1).SetStageResult(SourceUrl, SourceStage.Cleanup, CallOutcome.Ok, null);
        }

        [Fact]
        public void RemoveVideosGoneFromTheSource_RecordsFailureResult()
        {
            // A failure records the stage result so the failure count advances, giving the operator
            // a visible signal without needing a history table.
            const string videoId = "some-video";
            _db.GetVideosGoneFromLastFullListing(SourceUrl, Now).Returns([videoId]);
            _db.When(d => d.DeleteVideo(videoId))
                .Do(_ => throw new InvalidOperationException("database locked"));

            _step.RemoveVideosGoneFromTheSource(Source(), Now, CancellationToken.None);

            _db.Received(1).SetStageResult(
                SourceUrl, SourceStage.Cleanup,
                Arg.Is<CallOutcome>(o => o != CallOutcome.Ok),
                Arg.Any<string?>());
        }

        // ── RetireSourcesGoneFromTheConfiguration: order, protected set, failure handling ─────────

        [Fact]
        public void RetireSourcesGoneFromTheConfiguration_PurgesLibraryBeforeDeletingRow()
        {
            // Order is load-bearing: the source row carries published_anchor_id, the only pointer
            // to a source's items once its library has left the configuration. Deleting the row
            // before purging would lose that anchor permanently if the purge then fails, leaving
            // orphaned items no later turn can find or remove.
            var videoId = Guid.NewGuid().ToString();
            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/VOD" };
            var retiredRecord = RetiredRecord(anchorId: anchor.Id);

            _library.GetItemById(anchor.Id).Returns((BaseItem)anchor);
            // No configured library resolves; the stored anchor is used.
            _library.GetVirtualFolders().Returns([]);

            var retiredItem = new Movie { Id = Guid.NewGuid(), ParentId = anchor.Id };
            retiredItem.SetProviderId("VOD", videoId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { retiredItem });

            _db.GetAllSources().Returns([retiredRecord]);
            _db.GetVideosBySource(SourceUrl).Returns([VideoFor(SourceUrl, videoId)]);
            _db.GetVideoIdsWithMetadata().Returns([videoId]);

            // Record the call order across both substitutes.
            var callOrder = new List<string>();
            _library.When(l => l.DeleteItem(
                    Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>()))
                .Do(_ => callOrder.Add("purge"));
            _db.When(d => d.DeleteSource(SourceUrl))
                .Do(_ => callOrder.Add("delete-source"));

            _step.RetireSourcesGoneFromTheConfiguration(Config(), CancellationToken.None);

            // Purge must happen before the row is deleted; the reverse order breaks retry.
            Assert.Contains("purge", callOrder);
            Assert.Contains("delete-source", callOrder);
            Assert.True(
                callOrder.IndexOf("purge") < callOrder.IndexOf("delete-source"),
                "PurgeSource must be called before DeleteSource; reversing the order would lose the " +
                "anchor on a failed purge and leave orphaned items that no later turn can find.");
        }

        [Fact]
        public void RetireSourcesGoneFromTheConfiguration_ExcludesRetiredSourceVideosFromProtectedSet()
        {
            // PurgeSource skips any item whose VOD id appears in allKnownVideoIds. Passing the
            // global metadata set unchanged would protect the retired source's own videos — the
            // purge would silently delete nothing, return a plausible count, and leave every item
            // orphaned in the library. The retired source's ids must be removed from that set.
            var retiredVideoId = Guid.NewGuid().ToString();
            var activeVideoId = Guid.NewGuid().ToString();
            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/VOD" };
            var retiredRecord = RetiredRecord(anchorId: anchor.Id);

            _library.GetItemById(anchor.Id).Returns((BaseItem)anchor);
            _library.GetVirtualFolders().Returns([]);

            var retiredItem = new Movie { Id = Guid.NewGuid(), ParentId = anchor.Id };
            retiredItem.SetProviderId("VOD", retiredVideoId);
            var activeItem = new Movie { Id = Guid.NewGuid(), ParentId = anchor.Id };
            activeItem.SetProviderId("VOD", activeVideoId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { retiredItem, activeItem });

            _db.GetAllSources().Returns([retiredRecord]);
            _db.GetVideosBySource(SourceUrl).Returns([VideoFor(SourceUrl, retiredVideoId)]);
            // Both videos appear in the global metadata set; only the active one must be protected.
            _db.GetVideoIdsWithMetadata().Returns([retiredVideoId, activeVideoId]);

            _step.RetireSourcesGoneFromTheConfiguration(Config(), CancellationToken.None);

            // Retired source's item was purged (its id was excluded from the protected set).
            _library.Received(1).DeleteItem(
                retiredItem, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            // Active source's item was spared (its id remained in the protected set).
            _library.DidNotReceive().DeleteItem(
                activeItem, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public void RetireSourcesGoneFromTheConfiguration_KeepsRowWhenPurgeFails()
        {
            // A failed purge must not delete the row: without the anchor stored on the row there is
            // no way to find this source's items on the next turn, so the partial orphans would
            // survive permanently with no means of recovery.
            var videoId = Guid.NewGuid().ToString();
            var anchor = new Folder { Id = Guid.NewGuid(), Path = "/media/VOD" };
            var retiredRecord = RetiredRecord(anchorId: anchor.Id);

            _library.GetItemById(anchor.Id).Returns((BaseItem)anchor);
            _library.GetVirtualFolders().Returns([]);

            var retiredItem = new Movie { Id = Guid.NewGuid(), ParentId = anchor.Id };
            retiredItem.SetProviderId("VOD", videoId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { retiredItem });
            _library.When(l => l.DeleteItem(
                    Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>()))
                .Do(_ => throw new InvalidOperationException("library unavailable"));

            _db.GetAllSources().Returns([retiredRecord]);
            _db.GetVideosBySource(SourceUrl).Returns([VideoFor(SourceUrl, videoId)]);
            _db.GetVideoIdsWithMetadata().Returns([videoId]);

            _step.RetireSourcesGoneFromTheConfiguration(Config(), CancellationToken.None);

            // The row must survive the failed purge; the anchor it carries is the only retry path.
            _db.DidNotReceive().DeleteSource(SourceUrl);
        }

        [Fact]
        public void RetireSourcesGoneFromTheConfiguration_UsesStoredAnchorWhenLibrariesDontResolve()
        {
            // The stored anchor covers the case where the library itself has since left the
            // configuration — the one case the two currently configured libraries cannot cover.
            // Retirement must succeed using only that anchor when both library resolutions fail.
            var videoId = Guid.NewGuid().ToString();
            var storedAnchor = new Folder { Id = Guid.NewGuid(), Path = "/media/Old" };
            var retiredRecord = RetiredRecord(anchorId: storedAnchor.Id);

            // Stored anchor resolves; both configured libraries are gone.
            _library.GetItemById(storedAnchor.Id).Returns((BaseItem)storedAnchor);
            _library.GetVirtualFolders().Returns([]);

            var retiredItem = new Movie { Id = Guid.NewGuid(), ParentId = storedAnchor.Id };
            retiredItem.SetProviderId("VOD", videoId);
            // The stub HONOURS TopParentIds, and that is the whole point of this test. A stub that
            // returned the item for any query would hand it back even when no anchor was passed at
            // all, so the assertion below would hold whether or not the stored anchor was used.
            // Verified by mutation: with an unconditional stub, dropping the stored anchor
            // entirely left the whole suite green — and the real consequence would be a purge that
            // finds nothing, followed by DeleteSource erasing the only pointer to those items.
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(call => call.Arg<InternalItemsQuery>().TopParentIds.Contains(storedAnchor.Id)
                    ? new List<BaseItem> { retiredItem }
                    : []);

            _db.GetAllSources().Returns([retiredRecord]);
            _db.GetVideosBySource(SourceUrl).Returns([VideoFor(SourceUrl, videoId)]);
            _db.GetVideoIdsWithMetadata().Returns([videoId]);

            _step.RetireSourcesGoneFromTheConfiguration(Config(), CancellationToken.None);

            // Retirement succeeded via the stored anchor; the item was deleted and the row removed.
            _library.Received(1).DeleteItem(
                retiredItem, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            _db.Received(1).DeleteSource(SourceUrl);
        }

        [Fact]
        public void RetireSourcesGoneFromTheConfiguration_ContinuesWhenOneLibraryDoesntResolve()
        {
            // A configured library that no longer resolves must not abort the retirement. The step
            // catches the exception per library so one missing library does not prevent a source
            // whose items live in another library from being retired.
            var videoId = Guid.NewGuid().ToString();
            var seriesAnchor = new Folder { Id = Guid.NewGuid(), Path = "/media/VODs" };
            var retiredRecord = RetiredRecord();

            // Movies library is gone; series library exists and has the source's item.
            _library.GetVirtualFolders()
                .Returns([new VirtualFolderInfo { Name = SeriesLibrary, Locations = ["/media/VODs"] }]);
            _library.FindByPath("/media/VODs", true).Returns(seriesAnchor);

            var retiredItem = new Movie { Id = Guid.NewGuid(), ParentId = seriesAnchor.Id };
            retiredItem.SetProviderId("VOD", videoId);
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { retiredItem });

            _db.GetAllSources().Returns([retiredRecord]);
            _db.GetVideosBySource(SourceUrl).Returns([VideoFor(SourceUrl, videoId)]);
            _db.GetVideoIdsWithMetadata().Returns([videoId]);

            _step.RetireSourcesGoneFromTheConfiguration(Config(), CancellationToken.None);

            // Retirement succeeded via the series anchor despite the movies library failure.
            _library.Received(1).DeleteItem(
                retiredItem, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            _db.Received(1).DeleteSource(SourceUrl);
        }

        [Fact]
        public void RetireSourcesGoneFromTheConfiguration_DoesNotRetireConfiguredSource()
        {
            // A source present in the configuration must never be retired, even when its row exists
            // in the database. Only URLs removed from the configuration trigger retirement.
            _db.GetAllSources().Returns([new SourceRecord { Url = SourceUrl }]);

            _step.RetireSourcesGoneFromTheConfiguration(
                Config(sources: [Source()]), CancellationToken.None);

            _db.DidNotReceive().DeleteSource(SourceUrl);
            _library.DidNotReceive().DeleteItem(
                Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public void RetireSourcesGoneFromTheConfiguration_DoesNotRetireDisabledConfiguredSource()
        {
            // A disabled source is paused, not removed — disabling a source must not delete its
            // library content or destroy the watch state attached to it. Only removing the URL from
            // the configuration triggers retirement.
            var disabledSource = Source();
            disabledSource.Enabled = false;
            _db.GetAllSources().Returns([new SourceRecord { Url = SourceUrl }]);

            _step.RetireSourcesGoneFromTheConfiguration(
                Config(sources: [disabledSource]), CancellationToken.None);

            _db.DidNotReceive().DeleteSource(SourceUrl);
        }

        // ── End-to-end: the invariant that protects the whole library ─────────────────────────────

        // Removed: a test built a ListingOutcome literal with CleanupAllowed false and then
        // branched on that literal, so the step under test was never called. It survived deleting
        // the pass's own `if (cleanupAllowed)` guard — the behaviour it claimed to protect. The
        // contract it was about belongs to the pass and is covered where the pass is tested, by
        // PublicationServiceTests.AShortListing_DoesNotRunTheVideoCleanup.
    }
}

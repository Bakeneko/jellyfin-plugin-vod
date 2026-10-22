using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using VodSourceType = Jellyfin.Plugin.VOD.Services.Sync.SourceType;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// Tests the publication path of <see cref="LiveProbeService.ProbeSourceCoreAsync"/> — tests
    /// that require a working library target (non-empty series library name) and therefore live in
    /// a separate file to avoid the SourceType naming conflict that
    /// <c>MediaBrowser.Controller.Entities.SourceType</c> causes in the main test class.
    /// </summary>
    public sealed class LiveProbeServicePublicationTests
    {
        private const string LibraryName = "VODs";

        /// <summary>
        /// Creates a service backed by a real temp-directory library so the publication path
        /// (ResolveTarget → ReconcileAsync) does not throw.
        /// </summary>
        private static (LiveProbeService Service, string TempRoot, List<BaseItem> Published)
            MakeWithLibrary(IVodDatabase db)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "vod-tests-probe-pub-" + Guid.NewGuid().ToString("N"));
            var location = Path.Combine(tempRoot, LibraryName);
            Directory.CreateDirectory(location);

            var library = Substitute.For<ILibraryManager>();
            library.GetVirtualFolders()
                .Returns([new VirtualFolderInfo { Name = LibraryName, Locations = [location] }]);
            library.FindByPath(location, true)
                .Returns(new Folder { Id = Guid.NewGuid(), Path = location });
            library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns([]);

            // CreateItem is what a publication actually does, so capturing it is how a test can
            // tell "published" from "considered and refused".
            var published = new List<BaseItem>();
            library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => published.Add(call.ArgAt<BaseItem>(0)));

            var publisher = new LibraryPublisher(
                library,
                db,
                new ArtworkStore(Path.Combine(tempRoot, "artwork")),
                Substitute.For<IVodLocalizer>(),
                NullLogger<LibraryPublisher>.Instance);

            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                Substitute.For<ILibraryManager>(),
                Substitute.For<IMediaStreamRepository>(),
                Substitute.For<IMediaEncoder>(),
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);

            var service = new LiveProbeService(
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                db,
                publisher,
                describer,
                new StreamDescriptionStep(Substitute.For<IHttpClientFactory>(), describer),
                NullLogger<LiveProbeService>.Instance);

            return (service, tempRoot, published);
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_PublishesNothing_ForARowOutsideRetention()
        {
            // Publishability is computed, and retention is one of its three conditions. The old
            // probe asked the database for "every resolved row", which had no retention check in
            // it, so a video the administrator had retired by shortening the window came back.
            //
            // Asserting on what reaches the library rather than on which query was made: the query
            // this replaced no longer exists, so an assertion about it could not fail.
            const string sourceUrl = "https://www.twitch.tv/testchan2";
            var now = DateTimeOffset.UtcNow;

            var oldRow = new VideoRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceUrl = sourceUrl,
                VideoUrl = sourceUrl + "/v/1",
                NativeId = "1",
                PublishedAt = now.AddDays(-60),
                MetadataAt = now.AddDays(-60),
                MetadataResult = CallOutcome.Ok,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                Extras = new VideoExtras { Qualities = [new VideoQuality(1080, "h264", 30)] },
            };

            var db = Substitute.For<IVodDatabase>();
            db.GetVideosBySource(sourceUrl).Returns([oldRow]);
            db.GetVideoIdsWithMetadata().Returns([]);

            var (service, tempRoot, published) = MakeWithLibrary(db);
            try
            {
                await service.ProbeSourceCoreAsync(
                    new PluginConfiguration { SeriesLibraryName = LibraryName },
                    new SourceDefinition
                    {
                        Url = sourceUrl,
                        Enabled = true,
                        Mode = SourceMode.Series,
                        RetentionDays = 30,
                    },
                    VodSourceType.TwitchChannelLive,
                    new SourceRecord { Url = sourceUrl, Name = "Test Chan 2" },
                    ProbeOutcome.Nothing,
                    CancellationToken.None);
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }

            // Not vacuous: the call below proves the probe reached the reconcile — it is the
            // argument the reconcile takes — so "published nothing" is a refusal, not an early
            // return. The positive counterpart needs BaseItem.LibraryManager installed and lives
            // in LiveProbeServiceDescriptionTests, which joins that collection.
            db.Received().GetVideoIdsWithMetadata();
            Assert.DoesNotContain(published, i => i.GetProviderId("VOD") == oldRow.Id);
        }

    }
}

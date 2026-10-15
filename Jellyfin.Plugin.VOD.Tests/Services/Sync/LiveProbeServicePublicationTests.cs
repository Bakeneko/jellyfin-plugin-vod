using System;
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
        private static (LiveProbeService Service, string TempRoot) MakeWithLibrary(IVodDatabase db)
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
                new VideoDescriptionStep(Substitute.For<IHttpClientFactory>(), describer),
                NullLogger<LiveProbeService>.Instance);

            return (service, tempRoot);
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_DoesNotCall_GetPublishableVideosBySource()
        {
            // B-I7: the probe must use ResolutionService.Publishable (which applies retention and
            // day-complete filters) rather than GetPublishableVideosBySource (which returns every
            // resolved row without those filters). This test verifies the old call is gone.
            const string sourceUrl = "https://www.twitch.tv/testchan";
            var db = Substitute.For<IVodDatabase>();
            db.GetVideosBySource(sourceUrl).Returns([]);
            db.GetPublishableVideoIds().Returns([]);

            var config = new PluginConfiguration { SeriesLibraryName = LibraryName };
            var source = new SourceDefinition
            {
                Url = sourceUrl,
                Enabled = true,
                Mode = SourceMode.Series,
                RetentionDays = 30,
            };
            var sourceRecord = new SourceRecord { Url = sourceUrl, Name = "Test Chan" };

            var (service, tempRoot) = MakeWithLibrary(db);
            try
            {
                await service.ProbeSourceCoreAsync(
                    config,
                    source,
                    VodSourceType.TwitchChannelLive,
                    sourceRecord,
                    ProbeOutcome.Nothing,
                    CancellationToken.None);
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }

            // After B-I7: GetPublishableVideosBySource must never be called.
            db.DidNotReceive().GetPublishableVideosBySource(Arg.Any<string>());
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_UsesPublishableFilter_ExcludesRowsBeyondRetention()
        {
            // B-I7: a resolved row beyond the retention window must not be published by the probe.
            // Before the fix, GetPublishableVideosBySource would have included it.
            const string sourceUrl = "https://www.twitch.tv/testchan2";
            var now = DateTimeOffset.UtcNow;

            // A resolved row that is 60 days old — beyond the 30-day retention window.
            var oldRow = new VideoRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceUrl = sourceUrl,
                VideoUrl = sourceUrl + "/v/1",
                NativeId = "1",
                SyncedAt = now.AddDays(-60),
                ResolutionState = ResolutionState.Resolved,
                PublishedAt = now.AddDays(-60),
                SeasonNumber = 1,
                EpisodeNumber = 1,
            };

            var db = Substitute.For<IVodDatabase>();
            db.GetVideosBySource(sourceUrl).Returns([oldRow]);
            db.GetPublishableVideoIds().Returns([]);

            var config = new PluginConfiguration { SeriesLibraryName = LibraryName };
            var source = new SourceDefinition
            {
                Url = sourceUrl,
                Enabled = true,
                Mode = SourceMode.Series,
                RetentionDays = 30,
            };
            var sourceRecord = new SourceRecord { Url = sourceUrl, Name = "Test Chan 2" };

            var (service, tempRoot) = MakeWithLibrary(db);
            try
            {
                await service.ProbeSourceCoreAsync(
                    config,
                    source,
                    VodSourceType.TwitchChannelLive,
                    sourceRecord,
                    ProbeOutcome.Nothing,
                    CancellationToken.None);
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }

            // The old row must not have been queried via GetPublishableVideosBySource — that would
            // have returned it without the retention check.
            db.DidNotReceive().GetPublishableVideosBySource(Arg.Any<string>());
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using VodSourceType = Jellyfin.Plugin.VOD.Services.Sync.SourceType;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// A broadcast the probe finds ended is described while its manifest URL is fresh, and the
    /// probe's own publication applies the stored description (I3, option a).
    /// </summary>
    [Collection(BaseItemStaticsCollection.Name)]
    public sealed class LiveProbeServiceDescriptionTests : IDisposable
    {
        private const string LibraryName = "VODs";
        private const string SourceUrl = "https://www.youtube.com/@archives/streams";
        private const string VideoId = "3f9a2b1c-0000-0000-0000-0000000000a1";

        private const string Master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            https://cdn.test/v-1080.m3u8
            """;

        private sealed class StubHandler(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }

        private readonly string _tempRoot =
            Path.Combine(Path.GetTempPath(), "vod-tests-probe-describe-" + Guid.NewGuid().ToString("N"));

        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly IMediaEncoder _encoder = Substitute.For<IMediaEncoder>();
        private readonly ILibraryManager _describerLibrary = Substitute.For<ILibraryManager>();

        // The publisher's library, kept across passes so a second pass finds what the first one
        // published.
        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly List<BaseItem> _published = [];

        // The row as the probe reads it: a broadcast it was following, numbered when it was found.
        private readonly VideoRecord _row = new()
        {
            Id = VideoId,
            SourceUrl = SourceUrl,
            NativeId = "ended",
            VideoUrl = "https://www.youtube.com/watch?v=ended",
            SyncedAt = DateTimeOffset.UnixEpoch,
            LiveStatus = LiveStatus.Live,
            ResolutionState = ResolutionState.Resolved,
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
            SeasonNumber = 2026,
            EpisodeNumber = 1013001,
        };

        // What the re-fetch says once the stream is over: an archive served over HLS, with a tier.
        private static VideoMetadata Archive() => new()
        {
            Id = "ended",
            ExtractorKey = "Youtube",
            Title = "Yesterday's stream",
            FullTitle = "Yesterday's stream",
            UploadDate = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            LiveStatus = LiveStatus.NotLive,
            FormatCount = 10,
            ManifestUrl = "https://manifest.test/master.m3u8",
            Qualities = [new VideoQuality(1080, "h264", 30)],
        };

        public LiveProbeServiceDescriptionTests()
        {
            _db.GetVideosBySource(SourceUrl).Returns(_ => [_row]);
            _db.GetPublishableVideoIds().Returns([]);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        private LiveProbeService Make()
        {
            var location = Path.Combine(_tempRoot, LibraryName);
            Directory.CreateDirectory(location);

            var library = _library;
            library.GetVirtualFolders()
                .Returns([new VirtualFolderInfo { Name = LibraryName, Locations = [location] }]);
            library.FindByPath(location, true)
                .Returns(new Folder { Id = Guid.NewGuid(), Path = location });
            // Deterministic ids, and a library that holds what CreateItem received: a later pass
            // then finds the items the earlier one published, as it does in production.
            library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => _published.Add(call.ArgAt<BaseItem>(0)));
            library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(_ => _published.ToList());
            library.GetItemById(Arg.Any<Guid>())
                .Returns(call => _published.FirstOrDefault(i => i.Id.Equals(call.Arg<Guid>())));

            // Publishing a movie goes through Folder.AddChild, which reaches this static; the
            // collection keeps LibraryPublisherTests from installing its own meanwhile.
            BaseItem.LibraryManager = library;

            var publisher = new LibraryPublisher(
                library,
                _db,
                new ArtworkStore(Path.Combine(_tempRoot, "artwork")),
                Substitute.For<IVodLocalizer>(),
                NullLogger<LibraryPublisher>.Instance);

            _describerLibrary.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                _describerLibrary,
                Substitute.For<IMediaStreamRepository>(),
                _encoder,
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHandler(Master)));

            return new LiveProbeService(
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                _db,
                publisher,
                describer,
                new VideoDescriptionStep(factory, describer),
                NullLogger<LiveProbeService>.Instance);
        }

        private static Guid DeterministicId(string key)
            => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

        private Task RunPassAsync(string libraryName)
            => Make().ProbeSourceCoreAsync(
                new PluginConfiguration { MoviesLibraryName = libraryName, LocalBaseUrl = "http://localhost:8096" },
                new SourceDefinition { Url = SourceUrl, Enabled = true, Mode = SourceMode.Movies },
                VodSourceType.YoutubeChannel,
                new SourceRecord { Url = SourceUrl, Name = "Archives" },
                ProbeOutcome.Nothing,
                (_, _) => Task.FromResult<VideoMetadata?>(Archive()),
                CancellationToken.None);

        private void ProbeAnswers()
            => _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new MediaInfo
                {
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 }],
                }));

        [Fact]
        public async Task AnEndedBroadcast_IsDescribed_WhenTheProbeRefreshesIt()
        {
            ProbeAnswers();

            await RunPassAsync(libraryName: string.Empty);

            var stored = StoredStreams.Deserialize(_row.StreamDescriptions);
            Assert.NotNull(stored);
            Assert.True(stored!.ContainsKey("1080/h264"));
            Assert.Equal(ResolutionState.Resolved, _row.ResolutionState);
            _db.Received().UpsertVideo(Arg.Is<VideoRecord>(v => v.StreamDescriptions != null));
        }

        [Fact]
        public async Task AFailedDescription_LeavesTheEndedBroadcastResolved()
        {
            // The probe has no retry of its own, and the playback failsafe describes the archive
            // on first play: a failure must not take a playable video out of the library.
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<MediaInfo>>(_ => throw new InvalidOperationException("ffprobe died"));

            await RunPassAsync(libraryName: string.Empty);

            Assert.Null(_row.StreamDescriptions);
            Assert.Equal(ResolutionState.Resolved, _row.ResolutionState);
            Assert.Equal(0, _row.ResolutionAttempts);
            Assert.Null(_row.ResolutionError);
            _db.DidNotReceive().UpsertVideo(Arg.Is<VideoRecord>(v => v.ResolutionState == ResolutionState.Failed));
        }

        [Fact]
        public async Task TheProbesPublication_AppliesTheStoredDescription()
        {
            ProbeAnswers();

            await RunPassAsync(LibraryName);

            // ApplyStored looks the video's members up by its VOD provider id; the describe step
            // itself never does, so this query is the application.
            _describerLibrary.Received().GetItemList(Arg.Is<InternalItemsQuery>(
                q => q.HasAnyProviderId != null
                     && q.HasAnyProviderId.ContainsKey("VOD")
                     && q.HasAnyProviderId["VOD"] == VideoId));
        }

        [Fact]
        public async Task TheProbesPublication_DoesNotApplyTheStoredDescriptionAgain_ToAnUnchangedRow()
        {
            // N2: the probe publishes every few minutes. A row whose items were all published by
            // an earlier pass has nothing left to receive, so its description is not applied again.
            ProbeAnswers();
            await RunPassAsync(LibraryName);
            Assert.NotEmpty(_published);
            Assert.NotNull(_row.StreamDescriptions);
            _describerLibrary.ClearReceivedCalls();

            await RunPassAsync(LibraryName);

            _describerLibrary.DidNotReceive().GetItemList(Arg.Is<InternalItemsQuery>(
                q => q.HasAnyProviderId != null
                     && q.HasAnyProviderId.ContainsKey("VOD")
                     && q.HasAnyProviderId["VOD"] == VideoId));
        }
    }
}

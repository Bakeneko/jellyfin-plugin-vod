using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class VideoDescriptionStepTests
    {
        private const string Master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            https://cdn.test/v-1080.m3u8
            """;

        private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
        }

        private readonly IMediaEncoder _encoder = Substitute.For<IMediaEncoder>();

        private VideoDescriptionStep Make(StubHandler handler)
        {
            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                Substitute.For<ILibraryManager>(),
                Substitute.For<IMediaStreamRepository>(),
                _encoder,
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

            return new VideoDescriptionStep(factory, describer);
        }

        private static VideoRecord Video(
            LiveStatus live = LiveStatus.NotLive, bool withTiers = true, DateTimeOffset? publishedAt = null)
            => new()
            {
                Id = "3f9a2b1c-0000-0000-0000-000000000002",
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v",
                SyncedAt = DateTimeOffset.UnixEpoch,
                LiveStatus = live,
                PublishedAt = publishedAt ?? Now.AddDays(-1),
                Extras = new VideoExtras
                {
                    Qualities = withTiers ? [new VideoQuality(1080, "h264", 30)] : [],
                },
            };

        private static readonly PluginConfiguration Config = new() { LocalBaseUrl = "http://localhost:8096" };

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task NothingToDescribe_WithoutAManifest(string? manifest)
        {
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var error = await Make(handler).DescribeAsync(
                Config, Video(), manifest, 0, Now, CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task NothingToDescribe_ForALive()
        {
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var error = await Make(handler).DescribeAsync(
                Config, Video(live: LiveStatus.Live), "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task NothingToDescribe_WithoutAnOfferedTier()
        {
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var error = await Make(handler).DescribeAsync(
                Config, Video(withTiers: false), "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task NothingToDescribe_OutsideRetention()
        {
            // E3: a video that will never be published is not worth 25 MB of probe per tier.
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var error = await Make(handler).DescribeAsync(
                Config, Video(publishedAt: Now.AddDays(-40)), "https://cdn.test/m.m3u8", 30, Now,
                CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task AMalformedManifestUrl_IsAFailure_NotAnException()
        {
            // P4: new Uri(url) throws UriFormatException outside the try; use Uri.TryCreate so a
            // bad manifest URL from yt-dlp returns a reason instead of crashing the resolver.
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var error = await Make(handler).DescribeAsync(
                Config, Video(), "not a valid url %%", 0, Now, CancellationToken.None);

            Assert.NotNull(error);
            Assert.StartsWith("describe:", error, StringComparison.Ordinal);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task AMasterThatCannotBeFetched_IsAFailure()
        {
            var video = Video();

            var error = await Make(new StubHandler(HttpStatusCode.ServiceUnavailable, ""))
                .DescribeAsync(Config, video, "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.NotNull(error);
            Assert.Contains("503", error, StringComparison.Ordinal);
            Assert.Null(video.StreamDescriptions);
        }

        [Fact]
        public async Task AProbeThatFails_IsAFailure()
        {
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<MediaInfo>>(_ => throw new InvalidOperationException("ffprobe died"));
            var video = Video();

            var error = await Make(new StubHandler(HttpStatusCode.OK, Master))
                .DescribeAsync(Config, video, "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.NotNull(error);
            Assert.Null(video.StreamDescriptions);
        }

        [Fact]
        public async Task ASuccessfulDescription_IsStoredOnTheRow()
        {
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new MediaInfo
                {
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 }],
                }));
            var video = Video();

            var error = await Make(new StubHandler(HttpStatusCode.OK, Master))
                .DescribeAsync(Config, video, "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.Null(error);
            var stored = StoredStreams.Deserialize(video.StreamDescriptions)!;
            Assert.True(stored.ContainsKey(StoredStreams.PrimaryKey));
            Assert.True(stored.ContainsKey("1080/h264"));
        }
    }
}

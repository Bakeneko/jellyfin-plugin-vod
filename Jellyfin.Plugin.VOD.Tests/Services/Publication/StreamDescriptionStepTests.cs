using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The description step now RETURNS its result instead of writing it onto the row. The row has
    /// no column to put it in: describing and publishing happen in the same pass, so the dictionary
    /// travels in memory.
    /// </summary>
    public sealed class StreamDescriptionStepTests
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

        private StreamDescriptionStep Make(StubHandler handler)
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

            return new StreamDescriptionStep(factory, describer);
        }

        private static VideoRecord Video(
            LiveStatus live = LiveStatus.NotLive, bool withTiers = true, DateTimeOffset? publishedAt = null)
            => new()
            {
                Id = "3f9a2b1c-0000-0000-0000-000000000002",
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v",
                LiveStatus = live,
                PublishedAt = publishedAt ?? Now.AddDays(-1),
                Extras = new VideoExtras
                {
                    Qualities = withTiers ? [new VideoQuality(1080, "h264", 30)] : [],
                },
            };

        private static readonly PluginConfiguration Config = new() { LocalBaseUrl = "http://localhost:8096" };

        /// <summary>
        /// Nothing to describe, and nothing wrong with that. <c>Attempted</c> is what the caller
        /// reads to decide NOT to record an outcome — a live stream or a video outside retention
        /// must not advance a failure counter it was never a candidate for.
        /// </summary>
        /// <summary>
        /// Nothing to describe, for a reason only a new metadata answer could change.
        /// </summary>
        /// <remarks>
        /// <c>Conclusive</c> is the assertion that matters, and it is there to stop a loop. A video
        /// is selected for a wave partly because it has never been described; an answer that writes
        /// nothing would leave it due for ever, since its metadata fetch succeeds every turn and so
        /// no backoff ever starts. The caller stamps the clock on these, and the detail says why.
        /// </remarks>
        private static void AssertNothingToDescribe(StreamDescription result)
        {
            Assert.False(result.Attempted);
            Assert.True(result.Conclusive);
            Assert.Equal(CallOutcome.None, result.Outcome);
            Assert.NotNull(result.Detail);
            Assert.Null(result.Streams);
        }

        /// <summary>
        /// Not a candidate yet, for a reason that changes on its own.
        /// </summary>
        /// <remarks>
        /// The selection query already excludes a live stream and a video outside retention, so
        /// there is nothing to record — and recording would be wrong, since a broadcast ends and a
        /// retention window can be widened.
        /// </remarks>
        private static void AssertNotYetApplicable(StreamDescription result)
        {
            Assert.False(result.Attempted);
            Assert.False(result.Conclusive);
            Assert.Equal(CallOutcome.None, result.Outcome);
            Assert.Null(result.Detail);
            Assert.Null(result.Streams);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task NothingToDescribe_WithoutAManifest(string? manifest)
        {
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var result = await Make(handler).DescribeAsync(
                Config, Video(), manifest, 0, Now, CancellationToken.None);

            AssertNothingToDescribe(result);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task NothingToDescribe_ForALive()
        {
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var result = await Make(handler).DescribeAsync(
                Config, Video(live: LiveStatus.Live), "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            AssertNotYetApplicable(result);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task NothingToDescribe_WithoutAnOfferedTier()
        {
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var result = await Make(handler).DescribeAsync(
                Config, Video(withTiers: false), "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            AssertNothingToDescribe(result);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task NothingToDescribe_OutsideRetention()
        {
            // E3: a video that will never be published is not worth 25 MB of probe per tier.
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var result = await Make(handler).DescribeAsync(
                Config, Video(publishedAt: Now.AddDays(-40)), "https://cdn.test/m.m3u8", 30, Now,
                CancellationToken.None);

            AssertNotYetApplicable(result);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task AMalformedManifestUrl_IsAFailure_NotAnException()
        {
            // P4: new Uri(url) throws UriFormatException outside the try; use Uri.TryCreate so a
            // bad manifest URL from yt-dlp returns a reason instead of crashing the resolver.
            var handler = new StubHandler(HttpStatusCode.OK, Master);

            var result = await Make(handler).DescribeAsync(
                Config, Video(), "not a valid url %%", 0, Now, CancellationToken.None);

            Assert.True(result.Attempted);
            Assert.Equal(CallOutcome.ProbeFailed, result.Outcome);
            Assert.StartsWith("describe:", result.Detail, StringComparison.Ordinal);
            Assert.Null(result.Streams);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task AMasterThatCannotBeFetched_IsAFailure()
        {
            var result = await Make(new StubHandler(HttpStatusCode.ServiceUnavailable, ""))
                .DescribeAsync(Config, Video(), "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.True(result.Attempted);
            Assert.Equal(CallOutcome.ProbeFailed, result.Outcome);
            Assert.Contains("503", result.Detail, StringComparison.Ordinal);
            Assert.Null(result.Streams);
        }

        [Fact]
        public async Task AProbeThatFails_IsAFailure()
        {
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<MediaInfo>>(_ => throw new InvalidOperationException("ffprobe died"));
            var result = await Make(new StubHandler(HttpStatusCode.OK, Master))
                .DescribeAsync(Config, Video(), "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.True(result.Attempted);
            Assert.Equal(CallOutcome.ProbeFailed, result.Outcome);
            Assert.Null(result.Streams);
        }

        [Fact]
        public async Task ASuccessfulDescription_IsReturnedPerMember()
        {
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new MediaInfo
                {
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 }],
                }));
            var result = await Make(new StubHandler(HttpStatusCode.OK, Master))
                .DescribeAsync(Config, Video(), "https://cdn.test/m.m3u8", 0, Now, CancellationToken.None);

            Assert.Equal(CallOutcome.Ok, result.Outcome);
            Assert.Null(result.Detail);
            Assert.NotNull(result.Streams);
            Assert.True(result.Streams.ContainsKey("primary"));
            Assert.True(result.Streams.ContainsKey("1080/h264"));
        }
    }
}

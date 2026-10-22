using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The wave step: for each video, one metadata fetch then one description in the same pass.
    /// "Same pass" is not an optimisation: the manifest URL expires within hours and is not stored,
    /// so describing later would require a second yt-dlp call. Each test pins one invariant that,
    /// if broken, would either waste calls or corrupt the pass result.
    /// </summary>
    public sealed class WaveStepTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        private const string SourceUrl = "https://www.youtube.com/@testchan/videos";

        // A minimal valid HLS master for tests that need the HTTP fetch to succeed.
        private const string ValidHlsMaster = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            https://cdn.test/v-1080.m3u8
            """;

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

        private static SourceDefinition Source(int retentionDays = 0)
            => new() { Url = SourceUrl, Enabled = true, RetentionDays = retentionDays };

        private static PluginConfiguration Config()
            => new() { LocalBaseUrl = "http://localhost:8096" };

        /// <summary>
        /// A video row that has been ingested by the listing step but not yet fetched.
        /// Qualities is empty (no metadata yet), which keeps the description step from attempting
        /// unless the test fills them via MetadataFill (i.e. when metadata succeeds).
        /// </summary>
        private static VideoRecord Video(
            string? listingTitle = "Test Video",
            string? nativeId = "abc123",
            LiveStatus live = LiveStatus.NotLive)
            => new()
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = SourceUrl,
                NativeId = nativeId,
                VideoUrl = $"https://www.youtube.com/watch?v={nativeId ?? "abc123"}",
                ListingTitle = listingTitle,
                LiveStatus = live,
                Extras = new VideoExtras(),
            };

        /// <summary>
        /// Successful metadata, with qualities so the description step will attempt, and with a
        /// manifest URL so it has something to fetch.
        /// </summary>
        private static VideoMetadata OkMeta(
            string? manifestUrl = "https://cdn.test/master.m3u8",
            LiveStatus live = LiveStatus.NotLive,
            string? title = "Metadata Title",
            IReadOnlyList<VideoQuality>? qualities = null)
            => new()
            {
                Id = "abc123",
                Title = title,
                FormatCount = 1,
                ManifestUrl = manifestUrl,
                LiveStatus = live,
                Qualities = qualities ?? [new VideoQuality(1080, "h264", 30)],
                UploadDate = "20261001",
            };

        /// <summary>
        /// A failed metadata result with the availability field set (as yt-dlp reports on a
        /// sign-in-required video). Using availability rather than stderr to make the fixture
        /// independent of yt-dlp prose that can change between versions.
        /// </summary>
        private static MetadataResult NeedsAuthResult()
            => new(MetadataOutcome.Failed, new VideoMetadata { Availability = "needs_auth" });

        private static MetadataResult RateLimitResult()
            => new(MetadataOutcome.Failed, null, StdErr: "HTTP Error 429: Too Many Requests");

        private static MetadataResult TimeoutResult()
            => new(MetadataOutcome.Failed, null, TimedOut: true);

        private static MetadataResult UnavailableResult()
            => new(MetadataOutcome.Failed, null, StdErr: "Video unavailable");

        // A StubHandler whose response is configurable — used only when the wave actually attempts
        // to fetch the HLS master.
        private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }

        /// <summary>
        /// Builds a <see cref="StreamDescriptionStep"/> whose HTTP stub returns a valid HLS master
        /// and whose <see cref="IStreamDescriber"/> is controlled by the caller.
        /// </summary>
        private static StreamDescriptionStep DescriptionStepWith(
            IStreamDescriber describer, HttpStatusCode status = HttpStatusCode.OK, string body = ValidHlsMaster)
        {
            var handler = new StubHandler(status, body);
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>())
                .Returns(_ => new HttpClient(handler, disposeHandler: false));
            return new StreamDescriptionStep(factory, describer);
        }

        /// <summary>
        /// A <see cref="StreamDescriptionStep"/> whose HTTP stub will never be reached for the
        /// tests that do not attempt description (live, scheduled, no manifest, etc.).
        /// </summary>
        private static StreamDescriptionStep NullDescriptionStep()
            => DescriptionStepWith(Substitute.For<IStreamDescriber>(), HttpStatusCode.InternalServerError, string.Empty);

        private static WaveStep Make(IVodDatabase db, IYtDlpService ytDlp, StreamDescriptionStep? step = null)
            => new(ytDlp, step ?? NullDescriptionStep(), db, NullLogger<WaveStep>.Instance);

        // ── Happy path ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task MetadataOk_DescriptionOk_WritesBothOutcomes_AndPutsStreamsInDescriptions()
        {
            // Both clocks must be written: the metadata clock gates future yt-dlp calls, and the
            // description clock gates future describe calls. A missing outcome would make the pass
            // re-spend a call on the next turn.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var video = Video();
            var meta = OkMeta();
            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(meta));

            var streams = new Dictionary<string, IReadOnlyList<MediaStream>>
            {
                ["1080p"] = [new MediaStream { Type = MediaStreamType.Video, Width = 1920, Height = 1080 }],
            };
            var describer = Substitute.For<IStreamDescriber>();
            describer.DescribeVideoAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<VideoRecord>(),
                    Arg.Any<string>(), Arg.Any<Uri>(), Arg.Any<CancellationToken>())
                .Returns(streams);

            var sut = Make(db, ytDlp, DescriptionStepWith(describer));

            var result = await sut.RunAsync(Source(), [video], Config(), Now, default);

            // Metadata outcome written.
            db.Received(1).SetMetadataOutcome(
                video.Id, CallOutcome.Ok, null, Now, countFailure: Arg.Any<bool>());

            // Description outcome written (Ok because the describer returned streams).
            db.Received(1).SetDescribeOutcome(
                video.Id, CallOutcome.Ok, null, Now);

            // The streams travel to the checkup via Descriptions.
            Assert.True(result.Descriptions.ContainsKey(video.Id),
                "Descriptions must contain the video id after a successful describe.");
            Assert.Equal(CallOutcome.Ok, result.Outcome);
        }

        [Fact]
        public async Task MetadataOk_DescriptionFailed_MetadataOutcomeStaysOk_OnlyDescribeAttemptsAdvances()
        {
            // A failing ffprobe must NOT re-trigger a yt-dlp call on the next pass. The two
            // counters are independent: describe_attempts advances, metadata_attempts stays at zero.
            // This test is the central assertion for that design decision.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var video = Video();
            var meta = OkMeta();
            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(meta));

            // Returning null from the describer makes the step return ProbeFailed.
            var describer = Substitute.For<IStreamDescriber>();
            describer.DescribeVideoAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<VideoRecord>(),
                    Arg.Any<string>(), Arg.Any<Uri>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>?)null);

            var sut = Make(db, ytDlp, DescriptionStepWith(describer));

            var result = await sut.RunAsync(Source(), [video], Config(), Now, default);

            // The metadata outcome is Ok — a failed describe does not retroactively fail metadata.
            db.Received(1).SetMetadataOutcome(
                video.Id, CallOutcome.Ok, null, Now, countFailure: Arg.Any<bool>());

            // The describe outcome is recorded as a failure. SetDescribeOutcome (not SetMetadataOutcome
            // with a non-Ok value) is what advances describe_attempts.
            db.Received(1).SetDescribeOutcome(
                video.Id, CallOutcome.ProbeFailed, Arg.Any<string?>(), Now);

            // The wave itself succeeded (all metadata Ok), even though one description failed.
            Assert.Equal(CallOutcome.Ok, result.Outcome);
        }

        [Fact]
        public async Task UpsertVideo_IsCalledBeforeDescribeAsync()
        {
            // The describer probes through our own stream route, which reads this row from the
            // database. A row not yet written would make the route refuse the video, losing the
            // description silently. The order must be verified by tracking the call sequence —
            // asserting final state would pass even with the wrong order.
            var callOrder = new List<string>();

            var db = Substitute.For<IVodDatabase>();
            db.When(x => x.UpsertVideo(Arg.Any<VideoRecord>()))
                .Do(_ => callOrder.Add("UpsertVideo"));

            var ytDlp = Substitute.For<IYtDlpService>();
            var video = Video();
            var meta = OkMeta();
            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(meta));

            var describer = Substitute.For<IStreamDescriber>();
            describer.DescribeVideoAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<VideoRecord>(),
                    Arg.Any<string>(), Arg.Any<Uri>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    callOrder.Add("DescribeVideoAsync");
                    return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>?>(
                        new Dictionary<string, IReadOnlyList<MediaStream>>());
                });

            var sut = Make(db, ytDlp, DescriptionStepWith(describer));
            await sut.RunAsync(Source(), [video], Config(), Now, default);

            var upsertIdx = callOrder.IndexOf("UpsertVideo");
            var describeIdx = callOrder.IndexOf("DescribeVideoAsync");

            Assert.True(upsertIdx >= 0, "UpsertVideo was never called.");
            Assert.True(describeIdx >= 0, "DescribeVideoAsync was never called.");
            Assert.True(upsertIdx < describeIdx,
                $"UpsertVideo (pos {upsertIdx}) must come before DescribeVideoAsync (pos {describeIdx}).");
        }

        // ── Description not attempted ─────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(LiveStatus.Live)]
        [InlineData(LiveStatus.Upcoming)]
        public async Task LiveOrScheduled_SetDescribeOutcomeNotCalled(LiveStatus live)
        {
            // A live or scheduled broadcast has no stable manifest to describe.
            // Not attempted is not a failure — writing an outcome would advance describe_attempts
            // for something that was never a candidate.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var video = Video(live: live);
            var meta = OkMeta(live: live, manifestUrl: "https://cdn.test/live.m3u8");
            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(meta));

            var sut = Make(db, ytDlp);
            await sut.RunAsync(Source(), [video], Config(), Now, default);

            // Metadata was applied (the row was upserted).
            db.Received(1).UpsertVideo(Arg.Any<VideoRecord>());

            // But SetDescribeOutcome must NOT have been called — not attempted ≠ failed.
            db.DidNotReceive().SetDescribeOutcome(
                Arg.Any<string>(), Arg.Any<CallOutcome>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>());
        }

        [Fact]
        public async Task NoManifestUrl_StampsTheDescribeClock_SoTheVideoStopsBeingSelected()
        {
            // Without a manifest URL there is nothing to probe — but the clock must still be
            // stamped, and this is the test that stops an infinite pass.
            //
            // A video is selected for a wave partly BECAUSE described_at is null. Writing nothing
            // here left it due on every turn: the metadata fetch succeeds each time, which resets
            // its failure counter, so no backoff ever started and the pass could not finish — one
            // yt-dlp call per turn until the turn cap, after which the platform's own rate limit
            // became the stopping mechanism. Measured on the test instance, six percent of YouTube
            // videos are in this state, several hundred of them.
            //
            // The outcome is None, not a failure: nothing failed, so nothing is counted. The
            // thirty-day metadata refresh is the retry, and the detail says why there was nothing
            // to describe.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var video = Video();
            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(OkMeta(manifestUrl: null)));

            await Make(db, ytDlp).RunAsync(Source(), [video], Config(), Now, default);

            db.Received(1).UpsertVideo(Arg.Any<VideoRecord>());
            db.Received(1).SetDescribeOutcome(
                video.Id, CallOutcome.None, Arg.Is<string?>(d => d != null), Arg.Any<DateTimeOffset>());
        }

        // ── Failure handling ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task NeedsAuth_WritesOutcomeAndDetail_MetadataNotApplied()
        {
            // An age-restricted or auth-gated video: the outcome and its detail (the availability
            // value) are recorded, but the row must not be updated — a bad metadata answer must
            // not overwrite a previously good one.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var video = Video();
            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(NeedsAuthResult());

            var sut = Make(db, ytDlp);
            await sut.RunAsync(Source(), [video], Config(), Now, default);

            // The outcome is written with the availability value as detail.
            db.Received(1).SetMetadataOutcome(
                video.Id, CallOutcome.NeedsAuth, "needs_auth", Now, countFailure: true);

            // The row must not be upserted when metadata was not applied.
            db.DidNotReceive().UpsertVideo(Arg.Any<VideoRecord>());
        }

        [Fact]
        public async Task Timeout_AbandonedForPlatform_RemainingVideoNotTouched()
        {
            // A timeout is a platform-level fault (the session stopped answering). Every remaining
            // video would time out the same way. Abandoning early avoids spending WaveSize calls
            // for nothing.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var first = Video(nativeId: "v1");
            var second = Video(nativeId: "v2");

            ytDlp.GetMetadataAsync(first.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(TimeoutResult());
            // The second video's mock is deliberately not set up — if it were called, the test
            // would catch that by seeing unexpected calls or an unhandled exception.

            var sut = Make(db, ytDlp);
            var result = await sut.RunAsync(Source(), [first, second], Config(), Now, default);

            Assert.True(result.AbandonedForPlatform);
            Assert.Equal(CallOutcome.Timeout, result.Outcome);

            // The second video must not have been touched.
            db.DidNotReceive().SetMetadataOutcome(
                second.Id, Arg.Any<CallOutcome>(), Arg.Any<string?>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task RateLimit_CountFailureFalseInSetMetadataOutcome_WaveAbandoned()
        {
            // A rate limit must not push the video further out in its retry backoff: it is the
            // platform's fault, not the video's. Verify the countFailure argument directly —
            // checking the retry counter would not catch the bug if the DB happened to not advance
            // it even when countFailure is true.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var video = Video();
            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(RateLimitResult());

            var sut = Make(db, ytDlp);
            var result = await sut.RunAsync(Source(), [video], Config(), Now, default);

            // The critical assertion: countFailure must be false.
            db.Received(1).SetMetadataOutcome(
                video.Id, CallOutcome.RateLimited, Arg.Any<string?>(), Now, countFailure: false);

            Assert.True(result.AbandonedForPlatform);
        }

        [Fact]
        public async Task WaveOfThree_SecondRateLimited_FirstHasResult_ThirdNotTouched()
        {
            // Without the third video, an abandon test proves nothing: it would pass even if the
            // code simply stopped after two videos for any reason. The third video is the falsifier.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var first = Video(nativeId: "v1");
            var second = Video(nativeId: "v2");
            var third = Video(nativeId: "v3");

            ytDlp.GetMetadataAsync(first.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(OkMeta()));
            ytDlp.GetMetadataAsync(second.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(RateLimitResult());

            var sut = Make(db, ytDlp);
            var result = await sut.RunAsync(Source(), [first, second, third], Config(), Now, default);

            // The first video's metadata was applied and its outcome written.
            db.Received(1).SetMetadataOutcome(
                first.Id, CallOutcome.Ok, null, Now, countFailure: Arg.Any<bool>());

            // The second video triggered the abandon — its rate-limit was recorded.
            db.Received(1).SetMetadataOutcome(
                second.Id, CallOutcome.RateLimited, Arg.Any<string?>(), Now, countFailure: false);

            // The third video must not have been touched at all.
            db.DidNotReceive().SetMetadataOutcome(
                third.Id, Arg.Any<CallOutcome>(), Arg.Any<string?>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<bool>());

            Assert.True(result.AbandonedForPlatform);
        }

        [Fact]
        public async Task TwoFailures_StageResultCarriesFirstFailure()
        {
            // The first failure is the most likely common cause of a bad batch: showing the last
            // error across a heterogeneous wave would mislead whoever reads the configuration page.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var first = Video(nativeId: "v1");
            var second = Video(nativeId: "v2");

            // Two distinct outcomes so the test can tell which one was recorded.
            ytDlp.GetMetadataAsync(first.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(UnavailableResult());
            ytDlp.GetMetadataAsync(second.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(NeedsAuthResult());

            var sut = Make(db, ytDlp);
            var result = await sut.RunAsync(Source(), [first, second], Config(), Now, default);

            // The stage result carries the first failure (Unavailable), not the second (NeedsAuth).
            Assert.Equal(CallOutcome.Unavailable, result.Outcome);

            db.Received(1).SetStageResult(
                SourceUrl, SourceStage.Wave, CallOutcome.Unavailable, Arg.Any<string?>());
        }

        [Fact]
        public async Task EmptyWave_NoStageResultWritten_ReturnsOk()
        {
            // An empty wave means the selector found nothing due — not a failure. Writing a stage
            // result would advance the consecutive-failure counter for no work done, making a
            // source appear more broken than it is.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var sut = Make(db, ytDlp);
            var result = await sut.RunAsync(Source(), [], Config(), Now, default);

            db.DidNotReceive().SetStageResult(
                Arg.Any<string>(), Arg.Any<SourceStage>(),
                Arg.Any<CallOutcome>(), Arg.Any<string?>());

            Assert.Equal(CallOutcome.Ok, result.Outcome);
        }

        // ── Title selection ───────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ListingTitle_TakesPriorityOverMetadataTitle()
        {
            // The listing honours youtube:lang and returns the localized title; the metadata call
            // always returns the original. The listing title must win when present.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            const string listingTitle = "Localized Listing Title";
            var video = Video(listingTitle: listingTitle);
            var meta = OkMeta(title: "Original Metadata Title");

            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(meta));

            VideoRecord? upserted = null;
            db.When(x => x.UpsertVideo(Arg.Any<VideoRecord>())).Do(ci => upserted = ci.Arg<VideoRecord>());

            var sut = Make(db, ytDlp);
            await sut.RunAsync(Source(), [video], Config(), Now, default);

            Assert.NotNull(upserted);
            Assert.Equal(listingTitle, upserted.Title);
        }

        [Fact]
        public async Task NoListingTitle_FallsBackToMetadataTitle()
        {
            // Arte sends no title in its flat listing (measured 2026-08-19: entries carry only
            // ie_key, url, and two other fields). Without this fallback every Arte video would be
            // published under its native id.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            const string metadataTitle = "Arte Episode Title";
            var video = Video(listingTitle: null, nativeId: "arte-id");
            // PlatformRules.TitleFor returns metadata.Title for a non-Twitch video.
            var meta = new VideoMetadata
            {
                Id = "arte-id",
                Title = metadataTitle,
                FormatCount = 1,
                ManifestUrl = null,
                LiveStatus = LiveStatus.NotLive,
                Qualities = [new VideoQuality(1080, "h264", 30)],
            };

            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(meta));

            VideoRecord? upserted = null;
            db.When(x => x.UpsertVideo(Arg.Any<VideoRecord>())).Do(ci => upserted = ci.Arg<VideoRecord>());

            var sut = Make(db, ytDlp);
            await sut.RunAsync(Source(), [video], Config(), Now, default);

            Assert.NotNull(upserted);
            Assert.Equal(metadataTitle, upserted.Title);
        }

        [Fact]
        public async Task ListingTitle_IsNeverOverwrittenByMetadata()
        {
            // listing_title is the retitle detector's comparison point. MetadataFill must never
            // write it; if it did, the detector would never fire because the stored and fresh
            // values would always match.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            const string originalListingTitle = "Original Listing Title";
            var video = Video(listingTitle: originalListingTitle);
            var meta = OkMeta(title: "Different Metadata Title");

            ytDlp.GetMetadataAsync(video.VideoUrl, Arg.Any<CancellationToken>())
                .Returns(MetadataResult.From(meta));

            VideoRecord? upserted = null;
            db.When(x => x.UpsertVideo(Arg.Any<VideoRecord>())).Do(ci => upserted = ci.Arg<VideoRecord>());

            var sut = Make(db, ytDlp);
            await sut.RunAsync(Source(), [video], Config(), Now, default);

            Assert.NotNull(upserted);
            Assert.Equal(originalListingTitle, upserted.ListingTitle);
        }
    }
}

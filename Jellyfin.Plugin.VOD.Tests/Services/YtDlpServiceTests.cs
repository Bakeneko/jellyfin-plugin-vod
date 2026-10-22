using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YtDlpServiceTests
    {
        [Fact]
        public void IndicatesChannelOffAir_RecognisesYtDlpsNotLiveError()
        {
            // The exact line measured on 2026-10-04 for an off-air channel. It exits 1 and prints no
            // JSON, which is why the probe needs to tell it apart from a failed call.
            Assert.True(YtDlpService.IndicatesChannelOffAir(
                "ERROR: [twitch:stream] riotgames: The channel is not currently live"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("ERROR: [twitch:vod] 2890554613: This video is subscriber-only content")]
        [InlineData("ERROR: Unable to download JSON metadata: HTTP Error 429: Too Many Requests")]
        [InlineData("ERROR: [generic] foo: Unable to download webpage")]
        public void IndicatesChannelOffAir_TreatsEverythingElseAsAFailure(string stderr)
        {
            // Anything that is not yt-dlp's own not-live wording stays a failure: a rate limit read
            // as "nothing live" would retract a broadcast that is still running, and the probe
            // deletes library items on that conclusion.
            Assert.False(YtDlpService.IndicatesChannelOffAir(stderr));
        }

        [Fact]
        public void IndicatesChannelOffAir_TreatsAbsentStderrAsAFailure()
        {
            Assert.False(YtDlpService.IndicatesChannelOffAir(null));
        }

        // ── Listing arguments, built by a pure function ───────────────────────────
        // Tested here rather than through a fake process: building arguments is a function of its
        // inputs, and a fake process would be test code living in production.

        /// <summary>
        /// Measured on 2026-10-19: the flat listing serves the LOCALISED title and obeys
        /// <c>youtube:lang</c>, while the metadata call always serves the original and ignores it.
        /// Pinning the language is what makes the retitle detector's fingerprint deterministic.
        /// </summary>
        [Fact]
        public void PlaylistArguments_PinTheMetadataLanguage_OnTheYoutubeLane()
        {
            var args = YtDlpService.BuildPlaylistArguments(
                "https://www.youtube.com/@x/videos", maxEntries: 30, dateAfter: null,
                lane: PlatformLane.Youtube, metadataLanguage: "fr");

            Assert.Contains("--extractor-args", args);
            Assert.Contains("youtube:lang=fr", args);
        }

        [Fact]
        public void PlaylistArguments_PinNothing_WhenNoLanguageIsConfigured()
        {
            var args = YtDlpService.BuildPlaylistArguments(
                "https://www.youtube.com/@x/videos", 30, null, PlatformLane.Youtube, null);

            Assert.DoesNotContain("--extractor-args", args);
        }

        /// <summary>
        /// <c>youtube:lang</c> is an argument of the YouTube extractor: passing it to Arte or
        /// Twitch means nothing, and this project never applies one platform's rule to another.
        /// </summary>
        [Fact]
        public void PlaylistArguments_PinNothing_OnAnotherLane()
        {
            var args = YtDlpService.BuildPlaylistArguments(
                "https://www.arte.tv/fr/videos/RC-022076/x/", 30, null, PlatformLane.Arte, "fr");

            Assert.DoesNotContain("--extractor-args", args);
        }

        [Fact]
        public void PlaylistArguments_OmitPlaylistEnd_WhenTheCapMeansUnlimited()
        {
            // MaxVideosPerSource = 0 means "unlimited" and is the field's default: passing it
            // through would produce --playlist-end 0, which is no videos at all.
            var args = YtDlpService.BuildPlaylistArguments(
                "https://www.youtube.com/@x/videos", maxEntries: null, dateAfter: null,
                lane: PlatformLane.Youtube, metadataLanguage: null);

            Assert.DoesNotContain("--playlist-end", args);
        }

        [Fact]
        public void PlaylistArguments_CarryThePlaylistEnd_WhenCapped()
        {
            var args = YtDlpService.BuildPlaylistArguments(
                "https://www.youtube.com/@x/videos", 30, null, PlatformLane.Youtube, null);

            Assert.Contains("--playlist-end", args);
            Assert.Contains("30", args);
        }

        // ── Interpreting a run, as a pure function ────────────────────────────────
        // This is where the reason for a failure lives. It used to be lost: the database kept only
        // the word "Failed" for 14 of the instance's 16 failing videos, and the symptom was taken
        // for a rate limit that did not exist.

        [Fact]
        public void Interpret_CarriesStdErr_OnANonZeroExit()
        {
            var result = YtDlpService.Interpret(
                exitCode: 1, stdout: string.Empty,
                stderr: "ERROR: Video unavailable", timedOut: false);

            Assert.Equal(MetadataOutcome.Failed, result.Outcome);
            Assert.Equal("ERROR: Video unavailable", result.StdErr);
            Assert.False(result.TimedOut);
        }

        [Fact]
        public void Interpret_FlagsATimeout_RatherThanThrowing()
        {
            // Measured: a rate-limited session stops answering rather than saying so, so the
            // timeout IS the signal. Thrown as an exception, it only reached the classifier as a
            // text message.
            var result = YtDlpService.Interpret(-1, string.Empty, string.Empty, timedOut: true);

            Assert.Equal(MetadataOutcome.Failed, result.Outcome);
            Assert.True(result.TimedOut);
        }

        [Fact]
        public void Interpret_RecognisesAnOffAirChannel()
        {
            var result = YtDlpService.Interpret(
                1, string.Empty,
                "ERROR: [twitch:stream] riotgames: The channel is not currently live", false);

            Assert.Equal(MetadataOutcome.ChannelOffAir, result.Outcome);
        }

        /// <summary>
        /// The heart of the defect: the "unavailable" branch discarded the parsed metadata, so
        /// <c>availability</c> never reached the classifier and <c>needs_auth</c> could not be
        /// produced — while it is the cause of 14 of the instance's 16 failures.
        /// </summary>
        [Fact]
        public void Interpret_KeepsTheParsedMetadata_WhenTheVideoIsUnavailable()
        {
            const string Json = @"{""id"":""x"",""title"":""t"",""availability"":""needs_auth"",""formats"":[]}";

            var result = YtDlpService.Interpret(0, Json, string.Empty, false);

            Assert.Equal(MetadataOutcome.Failed, result.Outcome);
            Assert.NotNull(result.Metadata);
            Assert.Equal("needs_auth", result.Metadata!.Availability);
        }

        [Fact]
        public void Interpret_SucceedsOnAUsableAnswer()
        {
            const string Json = @"{""id"":""x"",""title"":""t"",""formats"":[{""format_id"":""18"",""vcodec"":""avc1""}]}";

            var result = YtDlpService.Interpret(0, Json, string.Empty, false);

            Assert.Equal(MetadataOutcome.Succeeded, result.Outcome);
            Assert.Equal("x", result.Metadata!.Id);
        }

        [Fact]
        public void Interpret_ReportsAParseFailure_WhenTheOutputIsNotJson()
        {
            var result = YtDlpService.Interpret(0, "not json at all", string.Empty, false);

            Assert.Equal(MetadataOutcome.Failed, result.Outcome);
            Assert.Null(result.Metadata);
        }
    }
}

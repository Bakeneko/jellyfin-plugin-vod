using Jellyfin.Plugin.VOD.Services;
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
    }
}

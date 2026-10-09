using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class LiveStatusTests
    {
        [Theory]
        [InlineData("is_live", LiveStatus.Live)]
        [InlineData("IS_LIVE", LiveStatus.Live)]
        [InlineData("is_upcoming", LiveStatus.Upcoming)]
        [InlineData("was_live", LiveStatus.NotLive)]
        [InlineData("post_live", LiveStatus.NotLive)]
        [InlineData("not_live", LiveStatus.NotLive)]
        public void FromYtDlp_MapsTheStatusesYtDlpEmits(string raw, LiveStatus expected)
        {
            Assert.Equal(expected, LiveStatusParser.FromYtDlp(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("some_future_value")]
        public void FromYtDlp_TreatsAnythingElseAsNotLive(string? raw)
        {
            // A platform that reports nothing, and a value a later yt-dlp invents, must both leave a
            // video behaving exactly as a normal one rather than throwing.
            Assert.Equal(LiveStatus.NotLive, LiveStatusParser.FromYtDlp(raw));
        }
    }
}

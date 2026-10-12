using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class PlatformLimitsTests
    {
        private const string YoutubeRateLimit =
            "ERROR: [youtube] abc: The current session has been rate-limited by YouTube for up to "
            + "an hour. It is recommended to use `-t sleep` to add a delay between requests.";

        [Fact]
        public void IsPlatformRefusal_RecognisesYoutubesOwnRateLimitAnswer()
            => Assert.True(PlatformLimits.IsPlatformRefusal("youtube", YoutubeRateLimit, timedOut: false));

        [Fact]
        public void IsPlatformRefusal_DoesNotApplyYoutubesStringToAnotherPlatform()
        {
            // Spec S6: a string that identifies one platform's answer is that platform's rule.
            // Arte never says this, so seeing it on the Arte lane means something else is going on
            // and must not be read as "Arte is rate-limiting us".
            Assert.False(PlatformLimits.IsPlatformRefusal("arte", YoutubeRateLimit, timedOut: false));
        }

        [Fact]
        public void IsPlatformRefusal_TreatsATimeoutAsARefusal_OnEveryPlatform()
        {
            // F9: a rate-limited session answers by hanging, with no message at all.
            Assert.True(PlatformLimits.IsPlatformRefusal("youtube", stderr: null, timedOut: true));
            Assert.True(PlatformLimits.IsPlatformRefusal("arte", stderr: "", timedOut: true));
            Assert.True(PlatformLimits.IsPlatformRefusal("vimeo.com", stderr: null, timedOut: true));
        }

        [Fact]
        public void IsPlatformRefusal_IsFalse_ForAVideoSpecificError()
        {
            // The video is gone; the platform is fine. Backing the lane off would punish every
            // other video of that source for one dead one.
            Assert.False(PlatformLimits.IsPlatformRefusal(
                "youtube", "ERROR: [youtube] uUpG2EmiJ9w: Private video", timedOut: false));
            Assert.False(PlatformLimits.IsPlatformRefusal("youtube", null, timedOut: false));
        }
    }
}

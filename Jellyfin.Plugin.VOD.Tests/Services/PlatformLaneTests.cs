using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class PlatformLaneTests
    {
        [Theory]
        [InlineData("https://www.youtube.com/@arte/videos", "youtube")]
        [InlineData("https://youtube.com/playlist?list=PL123", "youtube")]
        [InlineData("https://music.youtube.com/watch?v=abc", "youtube")]
        [InlineData("https://youtu.be/abc", "youtube")]
        [InlineData("https://www.arte.tv/fr/videos/RC-022076/les-idees-larges/", "arte")]
        [InlineData("https://www.twitch.tv/otplol_/videos", "twitch")]
        [InlineData("https://clips.twitch.tv/abc", "twitch")]
        public void For_GroupsTheKnownPlatforms(string url, string expected)
            => Assert.Equal(expected, PlatformLane.For(url));

        [Theory]
        [InlineData("https://evilyoutube.com/@x/videos")]
        [InlineData("https://notyoutu.be/abc")]
        [InlineData("https://myarte.tv/video/1")]
        [InlineData("https://nottwitch.tv/channel")]
        public void For_DoesNotHandAPlatformsLaneToALookalikeHost(string url)
        {
            // The lane is matched on the registrable domain, not on a suffix of the host, so a
            // host that merely ends with a platform's name does not get to spend its budget.
            Assert.DoesNotContain(PlatformLane.For(url), new[] { "youtube", "arte", "twitch" });
        }

        [Fact]
        public void For_KeepsTheWholeHost_ForAnUnknownPlatform()
        {
            // Only the known platforms are reduced to one lane per platform. An unknown host keeps
            // its full name, so two unrelated sites under one public suffix — anything .co.uk —
            // cannot end up sharing a budget.
            Assert.Equal("some.site.co.uk", PlatformLane.For("https://some.site.co.uk/v/1"));
            Assert.NotEqual(
                PlatformLane.For("https://a.co.uk/v/1"),
                PlatformLane.For("https://b.co.uk/v/1"));
        }

        [Fact]
        public void For_GivesAnUnknownHostItsOwnLane()
        {
            // Not YouTube's lane, and not a shared "other" lane either: a rate limit is imposed by
            // one platform and says nothing about any other.
            Assert.Equal("vimeo.com", PlatformLane.For("https://vimeo.com/123456"));
            Assert.NotEqual(
                PlatformLane.For("https://vimeo.com/123456"),
                PlatformLane.For("https://dailymotion.com/video/x1"));
        }

        [Fact]
        public void For_IsCaseInsensitiveOnTheHost()
            => Assert.Equal("youtube", PlatformLane.For("https://WWW.YouTube.COM/@x/videos"));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a url")]
        public void For_FallsBackToOneSharedLane_WhenThereIsNoHostToRead(string? url)
            => Assert.Equal("generic", PlatformLane.For(url));

        [Fact]
        public void Playback_IsItsOwnLane_SoAStartedVideoNeverWaitsBehindBackgroundWork()
        {
            Assert.Equal("playback", PlatformLane.Playback);
            Assert.NotEqual(PlatformLane.Playback, PlatformLane.For("https://www.youtube.com/watch?v=a"));
        }
    }
}

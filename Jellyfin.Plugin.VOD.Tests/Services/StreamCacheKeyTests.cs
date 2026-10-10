using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class StreamCacheKeyTests
    {
        [Fact]
        public void For_ATier_ProducesTheKeyTheRouteAlwaysUsed()
        {
            var key = StreamCacheKey.For(
                "abc", StreamCacheKey.DefaultFormat, 2160, "vp9", "fr,en", "fr");

            Assert.Equal("abc|bv*[protocol^=m3u8]|2160|vp9|fr,en|fr", key);
        }

        [Fact]
        public void For_ThePrimary_LeavesTheTierFieldsEmpty()
        {
            var key = StreamCacheKey.For("abc", StreamCacheKey.DefaultFormat, null, null, "", "");

            Assert.Equal("abc|bv*[protocol^=m3u8]||||", key);
        }

        [Theory]
        [InlineData(new string[0], "")]
        [InlineData(new[] { "fr" }, "fr")]
        [InlineData(new[] { "fr", "en" }, "fr,en")]
        [InlineData(new[] { "fr", "", "en" }, "fr,en")]
        [InlineData(new[] { "fr", "   ", "en" }, "fr,en")]
        [InlineData(new[] { "", "  " }, "")]
        public void JoinLanguages_DropsBlankEntries(string[] input, string expected)
        {
            // A naive Join yields "fr,,en", and HlsPlaylistFilter then matches an empty language
            // prefix against every rendition.
            Assert.Equal(expected, StreamCacheKey.JoinLanguages(input));
        }

        [Fact]
        public void JoinLanguages_TreatsNullAsEmpty()
            => Assert.Equal(string.Empty, StreamCacheKey.JoinLanguages(null));
    }
}

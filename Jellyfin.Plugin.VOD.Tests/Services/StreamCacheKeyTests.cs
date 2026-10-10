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
    }
}

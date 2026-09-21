using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class SourceTypeDetectorTests
    {
        [Theory]
        [InlineData("https://www.youtube.com/@arte",               SourceType.YoutubeChannel)]
        [InlineData("https://www.youtube.com/channel/UC_x5XG1OV",  SourceType.YoutubeChannel)]
        [InlineData("https://www.youtube.com/c/artede",            SourceType.YoutubeChannel)]
        [InlineData("https://www.youtube.com/user/Thinkerview",    SourceType.YoutubeChannel)]
        [InlineData("https://www.youtube.com/playlist?list=PL1",   SourceType.YoutubePlaylist)]
        [InlineData("https://www.youtube.com/watch?v=abc123",      SourceType.YoutubeVideo)]
        [InlineData("https://youtu.be/abc123",                     SourceType.YoutubeVideo)]
        [InlineData("https://www.arte.tv/fr/videos/RC-024539/karambolage/", SourceType.ArtePlaylist)]
        [InlineData("https://www.arte.tv/fr/videos/123456-057-A/ep/",       SourceType.ArteVideo)]
        [InlineData("https://www.france.tv/france-5/emission/123.html",     SourceType.FranceTvVideo)]
        [InlineData("https://www.example.com/video",               SourceType.Generic)]
        public void Detect_ReturnsExpectedType(string url, SourceType expected)
        {
            Assert.Equal(expected, SourceTypeDetector.Detect(url));
        }

        [Fact]
        public void Detect_YoutubeWatchWithListParam_ReturnsPlaylist()
        {
            // A watch URL with a list= parameter is a playlist context
            var url = "https://www.youtube.com/watch?v=abc&list=PLabc123";
            Assert.Equal(SourceType.YoutubePlaylist, SourceTypeDetector.Detect(url));
        }

        [Fact]
        public void Detect_EmptyOrInvalid_ReturnsGeneric()
        {
            Assert.Equal(SourceType.Generic, SourceTypeDetector.Detect("not-a-url"));
            Assert.Equal(SourceType.Generic, SourceTypeDetector.Detect(string.Empty));
        }
    }
}

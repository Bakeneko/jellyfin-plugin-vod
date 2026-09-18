using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class HlsPlaylistFilterTests
    {
        private static readonly Uri Base = new("https://example.com/master.m3u8");

        [Fact]
        public void FilterMaster_NonMaster_ReturnsNull()
        {
            var result = HlsPlaylistFilter.FilterMaster(
                "#EXTM3U\n#EXT-X-VERSION:3\n", Base, 1080, [], [], null);
            Assert.Null(result);
        }

        [Fact]
        public void FilterMaster_SelectsBestVariantUnderCap()
        {
            var master = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
                https://cdn.example.com/hd.m3u8
                #EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=1280x720,CODECS="avc1.640028,mp4a.40.2"
                https://cdn.example.com/sd.m3u8
                """;

            var result = HlsPlaylistFilter.FilterMaster(master, Base, 720, [], [], null);
            Assert.NotNull(result);
            Assert.Contains("sd.m3u8", result, StringComparison.Ordinal);
            Assert.DoesNotContain("hd.m3u8", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FilterMaster_WhenNothingFitsUnderCap_UsesFirstAvailable()
        {
            var master = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
                https://cdn.example.com/hd.m3u8
                """;

            var result = HlsPlaylistFilter.FilterMaster(master, Base, 480, [], [], null);
            Assert.NotNull(result);
            Assert.Contains("hd.m3u8", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FilterMaster_DropsDubbedAutoAudio()
        {
            var master = """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="en",NAME="English",DEFAULT=YES,URI="https://cdn.example.com/audio-en?acont=original"
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="fr",NAME="French (auto)",DEFAULT=NO,URI="https://cdn.example.com/audio-fr-dubbed-auto"
                #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080,AUDIO="audio",CODECS="avc1.640028,mp4a.40.2"
                https://cdn.example.com/video.m3u8
                """;

            var result = HlsPlaylistFilter.FilterMaster(master, Base, 1080, ["fr"], [], null);
            Assert.NotNull(result);
            Assert.DoesNotContain("dubbed-auto", result, StringComparison.Ordinal);
        }

        [Fact]
        public void Attribute_ParsesQuotedValue()
        {
            var line = "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio-0\",LANGUAGE=\"fr\"";
            Assert.Equal("audio-0", HlsPlaylistFilter.Attribute(line, "GROUP-ID"));
            Assert.Equal("fr", HlsPlaylistFilter.Attribute(line, "LANGUAGE"));
        }

        [Fact]
        public void Attribute_ParsesBareValue()
        {
            var line = "#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080";
            Assert.Equal("5000000", HlsPlaylistFilter.Attribute(line, "BANDWIDTH"));
        }

        [Fact]
        public void Attribute_MissingAttribute_ReturnsNull()
        {
            Assert.Null(HlsPlaylistFilter.Attribute("#EXT-X-MEDIA:TYPE=AUDIO", "URI"));
        }
    }
}

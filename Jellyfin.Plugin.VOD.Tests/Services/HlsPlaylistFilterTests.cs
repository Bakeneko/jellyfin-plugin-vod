using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class HlsPlaylistFilterTests
    {
        private static readonly Uri Base = new("https://example.com/master.m3u8");

        private const string LadderMaster = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028"
            https://cdn.test/1080-h264.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1920x1080,CODECS="vp09.00.40.08"
            https://cdn.test/1080-vp9.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=9000000,RESOLUTION=3840x2160,CODECS="vp09.00.50.08"
            https://cdn.test/2160-vp9.m3u8
            """;

        [Fact]
        public void FilterMaster_NonMaster_ReturnsNull()
        {
            var result = HlsPlaylistFilter.FilterMaster(
                "#EXTM3U\n#EXT-X-VERSION:3\n", Base, null, null, [], [], null);
            Assert.Null(result);
        }

        [Fact]
        public void FilterMaster_WithASingleVariant_SelectsIt()
        {
            var master = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
                https://cdn.example.com/hd.m3u8
                """;

            var result = HlsPlaylistFilter.FilterMaster(master, Base, null, null, [], [], null);
            Assert.NotNull(result);
            Assert.Contains("hd.m3u8", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FilterMaster_KeepsDubbedAutoAudio()
        {
            // The viewer chooses from the labels the client shows at playback, so an auto-dubbed
            // rendition is a legitimate choice rather than noise to hide. Dropping it also made
            // behaviour differ between platforms, since only YouTube marks them in the URI.
            const string master = """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="fr",NAME="French (auto)",DEFAULT=NO,URI="https://cdn.example.com/audio-fr-dubbed-auto"
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="en",NAME="English",DEFAULT=YES,URI="https://cdn.example.com/audio-en"
                #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028",AUDIO="audio"
                https://cdn.example.com/v1080.m3u8
                """;

            var result = HlsPlaylistFilter.FilterMaster(
                master, new Uri("https://cdn.example.com/master.m3u8"),
                height: null, codec: null, audioLanguages: [], subtitleLanguages: []);

            Assert.NotNull(result);
            Assert.Contains("dubbed-auto", result, StringComparison.Ordinal);
            Assert.Contains("audio-en", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FilterMaster_WithAnExactSelection_ServesThatVariant()
        {
            var result = HlsPlaylistFilter.FilterMaster(
                LadderMaster, new Uri("https://cdn.test/master.m3u8"), 2160, "vp9", [], []);

            Assert.NotNull(result);
            Assert.Contains("2160-vp9.m3u8", result, StringComparison.Ordinal);
            Assert.DoesNotContain("1080-h264.m3u8", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FilterMaster_WhenTheSelectedTierIsGone_ReturnsNull()
        {
            // The upstream may have dropped a tier since the sync captured it. Serving something
            // else would contradict what the menu advertised.
            var result = HlsPlaylistFilter.FilterMaster(
                LadderMaster, new Uri("https://cdn.test/master.m3u8"), 1440, "vp9", [], []);

            Assert.Null(result);
        }

        [Fact]
        public void FilterMaster_WithNoSelection_PrefersTheBestAvc()
        {
            var result = HlsPlaylistFilter.FilterMaster(
                LadderMaster, new Uri("https://cdn.test/master.m3u8"), null, null, [], []);

            Assert.NotNull(result);
            Assert.Contains("1080-h264.m3u8", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FilterMaster_WithNoSelectionAndNoAvc_StaysAtOrBelow1080()
        {
            // Guard: a VP9-only platform must not make 2160p the default and surprise the viewer
            // with a transcode.
            const string vp9Only = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1920x1080,CODECS="vp09.00.40.08"
                https://cdn.test/1080-vp9.m3u8
                #EXT-X-STREAM-INF:BANDWIDTH=9000000,RESOLUTION=3840x2160,CODECS="vp09.00.50.08"
                https://cdn.test/2160-vp9.m3u8
                """;

            var result = HlsPlaylistFilter.FilterMaster(
                vp9Only, new Uri("https://cdn.test/master.m3u8"), null, null, [], []);

            Assert.NotNull(result);
            Assert.Contains("1080-vp9.m3u8", result, StringComparison.Ordinal);
            Assert.DoesNotContain("2160-vp9.m3u8", result, StringComparison.Ordinal);
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

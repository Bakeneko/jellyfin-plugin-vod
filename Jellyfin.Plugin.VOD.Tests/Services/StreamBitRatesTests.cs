using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class StreamBitRatesTests
    {
        // Twitch's shape: one muxed variant, no rendition declared, for which MediaStreamBuilder
        // deliberately derives nothing.
        private const string MuxedMaster = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=8444342,CODECS="avc1.64002A,mp4a.40.2",RESOLUTION=1920x1080,VIDEO="chunked",FRAME-RATE=60.000
            https://cdn.test/chunked.m3u8
            """;

        [Fact]
        public void VariantBandwidth_ReadsTheVariantDeclaration_EvenWithoutRenditions()
        {
            Assert.Equal(8444342, StreamBitRates.VariantBandwidth(MuxedMaster));
        }

        [Fact]
        public void VariantBandwidth_IsNull_WhenTheMasterDeclaresNoVariant()
        {
            Assert.Null(StreamBitRates.VariantBandwidth("#EXTM3U\n"));
        }

        [Fact]
        public void FillMissingVideo_GivesAnUnmeasuredVideoTheBandwidth_EvenWhenTheVideoComesLast()
        {
            // ffprobe reports no bit_rate for H.264 in MPEG-TS segments, and Jellyfin refuses to copy
            // a video whose bitrate is unknown whenever the client caps it (EncodingHelper
            // .CanStreamCopyVideo) — which the web client always does.
            var described = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, BitRate = 192000 },
                new() { Type = MediaStreamType.Video, Codec = "h264" },
                new() { Type = MediaStreamType.Data, Codec = "timed_id3" }
            };

            var filled = StreamBitRates.FillMissingVideo(described, 8444342);

            Assert.Equal(1, filled);
            Assert.Equal(8444342, described[1].BitRate);
            Assert.Equal(192000, described[0].BitRate);
            Assert.Null(described[2].BitRate);
        }

        [Fact]
        public void FillMissingVideo_KeepsABitrateTheProbeMeasured()
        {
            var described = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "vp9", BitRate = 16731044 }
            };

            Assert.Equal(0, StreamBitRates.FillMissingVideo(described, 6000000));
            Assert.Equal(16731044, described[0].BitRate);
        }

        [Fact]
        public void FillMissingVideo_WithoutABandwidth_FillsNothing()
        {
            var described = new List<MediaStream> { new() { Type = MediaStreamType.Video } };

            Assert.Equal(0, StreamBitRates.FillMissingVideo(described, null));
            Assert.Null(described[0].BitRate);
        }
    }
}

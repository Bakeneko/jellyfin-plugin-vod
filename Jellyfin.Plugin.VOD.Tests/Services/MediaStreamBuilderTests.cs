using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class MediaStreamBuilderTests
    {
        // Minimal master with one video variant, one audio and one subtitle rendition.
        private const string FullMaster = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="fr",NAME="français (confort audio)",DEFAULT=YES,URI="https://cdn.example.com/audio-fr.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="en",NAME="English - original",DEFAULT=NO,URI="https://cdn.example.com/audio-en.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",LANGUAGE="fr",NAME="Français",DEFAULT=NO,FORCED=NO,URI="https://cdn.example.com/sub-fr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080,AUDIO="audio",SUBTITLES="subs",CODECS="avc1.640028,mp4a.40.2"
            https://cdn.example.com/video.m3u8
            """;

        [Fact]
        public void Build_VideoFirst_ThenAudio_ThenSubtitles()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.Equal(4, streams.Count);
            Assert.Equal(MediaStreamType.Video, streams[0].Type);
            Assert.Equal(MediaStreamType.Audio, streams[1].Type);
            Assert.Equal(MediaStreamType.Audio, streams[2].Type);
            Assert.Equal(MediaStreamType.Subtitle, streams[3].Type);
        }

        [Fact]
        public void Build_SequentialIndexing()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            for (var i = 0; i < streams.Count; i++)
            {
                Assert.Equal(i, streams[i].Index);
            }
        }

        [Fact]
        public void Build_AudioStream_PreservesNameAndLanguage()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            // streams[1] is the first audio track (order follows #EXT-X-MEDIA declaration order)
            Assert.Equal("français (confort audio)", streams[1].Title);
            Assert.Equal("fr", streams[1].Language);

            Assert.Equal("English - original", streams[2].Title);
            Assert.Equal("en", streams[2].Language);
        }

        [Fact]
        public void Build_SubtitleStream_PreservesNameAndLanguage()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.Equal("Français", streams[3].Title);
            Assert.Equal("fr", streams[3].Language);
        }

        [Fact]
        public void Build_VideoStream_ResolutionParsed()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.Equal(1920, streams[0].Width);
            Assert.Equal(1080, streams[0].Height);
        }

        [Fact]
        public void Build_VideoStream_BandwidthParsed()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.Equal(2000000, streams[0].BitRate);
        }

        [Fact]
        public void Build_VideoCodecNormalised_Avc1ToH264()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.Equal("h264", streams[0].Codec);
        }

        [Fact]
        public void Build_AudioCodecNormalised_Mp4aToAac()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            // All audio streams share the codec from the first CODECS attribute
            Assert.Equal("aac", streams[1].Codec);
            Assert.Equal("aac", streams[2].Codec);
        }

        [Fact]
        public void Build_SubtitleCodecIsWebvtt()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.Equal("webvtt", streams[3].Codec);
        }

        [Fact]
        public void Build_DefaultAndForcedFlags()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.True(streams[1].IsDefault);   // fr audio: DEFAULT=YES
            Assert.False(streams[2].IsDefault);  // en audio: DEFAULT=NO
            Assert.False(streams[3].IsForced);   // subtitle: FORCED=NO
        }

        [Fact]
        public void Build_ForcedSubtitle_ForcedIsTrue()
        {
            var master = """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",LANGUAGE="en",NAME="English SDH",DEFAULT=NO,FORCED=YES,URI="https://cdn.example.com/sub-en.m3u8"
                #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720,SUBTITLES="subs",CODECS="avc1.640028"
                https://cdn.example.com/video.m3u8
                """;

            var streams = MediaStreamBuilder.Build(master);

            var sub = Assert.Single(streams, s => s.Type == MediaStreamType.Subtitle);
            Assert.True(sub.IsForced);
        }

        [Fact]
        public void Build_NoStreamInf_ReturnsEmpty()
        {
            var master = """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="en",NAME="English",DEFAULT=YES,URI="https://cdn.example.com/audio-en.m3u8"
                """;

            var streams = MediaStreamBuilder.Build(master);

            Assert.Empty(streams);
        }

        [Fact]
        public void Build_EmptyPlaylist_ReturnsEmpty()
        {
            var streams = MediaStreamBuilder.Build(string.Empty);

            Assert.Empty(streams);
        }

        [Fact]
        public void Build_VideoOnly_NoAudioNoSubtitle()
        {
            var master = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720,CODECS="avc1.640028"
                https://cdn.example.com/video.m3u8
                """;

            var streams = MediaStreamBuilder.Build(master);

            Assert.Single(streams);
            Assert.Equal(MediaStreamType.Video, streams[0].Type);
        }

        [Fact]
        public void Build_VideoCodecNormalised_Hevc()
        {
            var master = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=3840x2160,CODECS="hev1.1.6.L150.90"
                https://cdn.example.com/4k.m3u8
                """;

            var streams = MediaStreamBuilder.Build(master);

            Assert.Equal("hevc", streams[0].Codec);
        }

        [Fact]
        public void Build_MissingResolution_WidthAndHeightAreNull()
        {
            var master = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=1000000,CODECS="avc1.640028"
                https://cdn.example.com/video.m3u8
                """;

            var streams = MediaStreamBuilder.Build(master);

            Assert.Null(streams[0].Width);
            Assert.Null(streams[0].Height);
        }
    }
}

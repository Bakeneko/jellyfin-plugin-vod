using System.Collections.Generic;
using System.Linq;
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
        public void Build_IndexesInPlaylistOrder_BecauseThatIsWhatFfmpegNumbers()
        {
            // These indices become ffmpeg's -map arguments, and ffmpeg numbers an HLS master's
            // streams in the order the playlist declares them: renditions first, the variant last.
            // Measured on the test instance, where declaring the video at 0 cost the picture outright:
            //   input    #0:0 Audio(fr-FR), #0:1 Audio(en-US), #0:2 Video(h264)
            //   Jellyfin -map 0:0 -map 0:1
            //   output   #0:0 Audio, #0:1 Audio      <- two audio streams, no video
            // So the order is read from the playlist rather than imposed: a bucket-and-concatenate
            // pass would discard the one fact that makes the indices mean anything.
            var streams = MediaStreamBuilder.Build(FullMaster);

            Assert.Equal(4, streams.Count);
            Assert.Equal(MediaStreamType.Audio, streams[0].Type);
            Assert.Equal(MediaStreamType.Audio, streams[1].Type);
            Assert.Equal(MediaStreamType.Subtitle, streams[2].Type);
            Assert.Equal(MediaStreamType.Video, streams[3].Type);
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

        // Selected by type rather than by position: the position is pinned once, by
        // Build_IndexesInPlaylistOrder_BecauseThatIsWhatFfmpegNumbers, and these tests are about what
        // each stream carries. Hard-coded offsets here would turn one ordering change into a dozen
        // unrelated failures.
        private static MediaStream OnlyVideo(IReadOnlyList<MediaStream> streams)
            => Assert.Single(streams, s => s.Type == MediaStreamType.Video);

        private static List<MediaStream> AudioOf(IReadOnlyList<MediaStream> streams)
            => [.. streams.Where(s => s.Type == MediaStreamType.Audio)];

        private static MediaStream OnlySubtitle(IReadOnlyList<MediaStream> streams)
            => Assert.Single(streams, s => s.Type == MediaStreamType.Subtitle);

        [Fact]
        public void Build_ReadsTheCodecsByKind_WhenCodecsListsAudioFirst()
        {
            // Position 0 of CODECS used to be taken as the video codec and position 1 as the audio
            // one, so an audio-first list labelled the picture "aac" and the sound "h264".
            const string audioFirst = """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",LANGUAGE="fr",NAME="français",URI="fr.m3u8"
                #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="mp4a.40.2,avc1.640028",AUDIO="a"
                https://cdn.test/1080.m3u8
                """;

            var streams = MediaStreamBuilder.Build(audioFirst);

            Assert.Equal("h264", OnlyVideo(streams).Codec);
            Assert.Equal("aac", Assert.Single(AudioOf(streams)).Codec);
        }

        [Fact]
        public void Build_AudioStream_PreservesNameAndLanguage()
        {
            var audio = AudioOf(MediaStreamBuilder.Build(FullMaster));

            // In #EXT-X-MEDIA declaration order.
            Assert.Equal("français (confort audio)", audio[0].Title);
            Assert.Equal("fr", audio[0].Language);

            Assert.Equal("English - original", audio[1].Title);
            Assert.Equal("en", audio[1].Language);
        }

        [Fact]
        public void Build_SubtitleStream_PreservesNameAndLanguage()
        {
            var subtitle = OnlySubtitle(MediaStreamBuilder.Build(FullMaster));

            Assert.Equal("Français", subtitle.Title);
            Assert.Equal("fr", subtitle.Language);
        }

        [Fact]
        public void Build_VideoStream_ResolutionParsed()
        {
            var video = OnlyVideo(MediaStreamBuilder.Build(FullMaster));

            Assert.Equal(1920, video.Width);
            Assert.Equal(1080, video.Height);
        }

        [Fact]
        public void Build_VideoStream_BandwidthParsed()
        {
            Assert.Equal(2000000, OnlyVideo(MediaStreamBuilder.Build(FullMaster)).BitRate);
        }

        [Fact]
        public void Build_VideoCodecNormalised_Avc1ToH264()
        {
            Assert.Equal("h264", OnlyVideo(MediaStreamBuilder.Build(FullMaster)).Codec);
        }

        [Fact]
        public void Build_AudioCodecNormalised_Mp4aToAac()
        {
            // All audio streams share the codec from the second CODECS entry.
            Assert.All(AudioOf(MediaStreamBuilder.Build(FullMaster)), s => Assert.Equal("aac", s.Codec));
        }

        [Fact]
        public void Build_SubtitleCodecIsWebvtt()
        {
            Assert.Equal("webvtt", OnlySubtitle(MediaStreamBuilder.Build(FullMaster)).Codec);
        }

        [Fact]
        public void Build_DefaultAndForcedFlags()
        {
            var streams = MediaStreamBuilder.Build(FullMaster);
            var audio = AudioOf(streams);

            Assert.True(audio[0].IsDefault);   // fr audio: DEFAULT=YES
            Assert.False(audio[1].IsDefault);  // en audio: DEFAULT=NO
            Assert.False(OnlySubtitle(streams).IsForced);  // subtitle: FORCED=NO
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

        [Fact]
        public void Build_OnAMuxedVariant_SynthesisesTheAudioStream()
        {
            // Twitch ships muxed variants: the audio codec is declared in CODECS with no separate
            // #EXT-X-MEDIA rendition. Declaring video only would hide a track the stream carries,
            // and mislead Jellyfin's track selection and transcoding decisions.
            const string muxed = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=9168232,RESOLUTION=1920x1080,CODECS="avc1.64002A,mp4a.40.2"
                https://cdn.test/1080p60.m3u8
                """;

            var streams = MediaStreamBuilder.Build(muxed);

            Assert.Equal(2, streams.Count);
            Assert.Equal(MediaStreamType.Video, streams[0].Type);
            Assert.Equal("h264", streams[0].Codec);
            Assert.Equal(MediaStreamType.Audio, streams[1].Type);
            Assert.Equal("aac", streams[1].Codec);
            Assert.True(streams[1].IsDefault);
            Assert.Equal(1, streams[1].Index);
        }

        [Fact]
        public void Build_OnSeparateRenditions_DoesNotSynthesiseAnExtraTrack()
        {
            const string separate = """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="français (confort audio)",LANGUAGE="fr",DEFAULT=YES,URI="fr.m3u8"
                #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="a"
                https://cdn.test/1080.m3u8
                """;

            var streams = MediaStreamBuilder.Build(separate);

            // One audio stream, and it is the declared rendition rather than a synthesised stand-in:
            // only the rendition carries NAME and LANGUAGE. It precedes the video, as the playlist
            // declares it — the contrast with Build_OnAMuxedVariant_SynthesisesTheAudioStream, where
            // the synthesised track follows the variant because that is where ffmpeg finds it.
            Assert.Equal(2, streams.Count);
            var audio = Assert.Single(streams, s => s.Type == MediaStreamType.Audio);
            Assert.Equal("français (confort audio)", audio.Title);
            Assert.Equal("fr", audio.Language);
            Assert.Equal(0, audio.Index);
            Assert.Equal(MediaStreamType.Video, streams[1].Type);
        }
    }
}

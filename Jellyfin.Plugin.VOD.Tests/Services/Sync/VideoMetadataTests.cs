using System.IO;
using System.Linq;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// Parsed from real yt-dlp output, not hand-written JSON. A hand-written fixture would have put
    /// "acodec": "mp4a.40.2" on the audio renditions, because that is what one assumes; the real -J
    /// puts null. The predicate bug these tests pin survived precisely because nobody looked.
    /// </summary>
    public sealed class VideoMetadataTests
    {
        private static VideoMetadata Parse(string fixture)
        {
            var json = File.ReadAllText(Path.Combine("Fixtures", fixture));
            var meta = VideoMetadata.ParseJson(json);
            Assert.NotNull(meta);
            return meta;
        }

        [Fact]
        public void Arte_KeepsBothFrenchTracks_DistinguishedByLabel()
        {
            var meta = Parse("arte-video.json");

            var french = meta.AudioTracks.Where(t => t.Language == "fr").ToList();

            // Two fr tracks: "français" and "français (confort audio)". The language code alone
            // cannot tell them apart, which is the whole point of carrying Label.
            Assert.Equal(2, french.Count);
            Assert.Equal(2, french.Select(t => t.Label).Distinct().Count());
            Assert.Contains(french, t => t.Label!.Contains("confort_audio"));
        }

        [Fact]
        public void Arte_CollectsAudioTracksDespiteNullAcodec()
        {
            // The HLS audio renditions report "acodec": null. The previous predicate required a
            // non-empty acodec and silently dropped every one of them.
            var meta = Parse("arte-video.json");

            Assert.Equal(4, meta.AudioTracks.Count);
        }

        [Fact]
        public void Arte_TopsOutAt1080_WithBothCodecs()
        {
            var meta = Parse("arte-video.json");

            Assert.Equal(1080, meta.Qualities.Max(q => q.Height));
            Assert.Contains(meta.Qualities, q => q.Height == 1080 && q.Codec == "h264");
            Assert.Contains(meta.Qualities, q => q.Height == 1080 && q.Codec == "hevc");
        }

        [Fact]
        public void Youtube_Offers1440And2160InVp9Only()
        {
            var meta = Parse("youtube-video.json");

            Assert.Equal(["vp9"], meta.Qualities.Where(q => q.Height == 1440)
                .Select(q => q.Codec).Distinct());
            Assert.Equal(["vp9"], meta.Qualities.Where(q => q.Height == 2160)
                .Select(q => q.Codec).Distinct());
            Assert.Contains(meta.Qualities, q => q.Height == 1080 && q.Codec == "h264");
        }

        [Fact]
        public void Youtube_ExcludesStoryboardsAndDashOnlyFormats()
        {
            var meta = Parse("youtube-video.json");

            // Storyboards are mhtml; AV1 and the opus/m4a audio are https-only. None is reachable
            // through the HLS path this plugin serves, so advertising them would be dishonest.
            Assert.DoesNotContain(meta.Qualities, q => q.Codec == "av1");
            Assert.All(meta.Qualities, q => Assert.True(q.Height > 0));
        }

        [Fact]
        public void Twitch_KeepsFpsSoThat720And720p60Differ()
        {
            var meta = Parse("twitch-live.json");

            var q720 = Assert.Single(meta.Qualities, q => q.Height == 720);
            Assert.Equal(60, q720.Fps);
            Assert.Equal("h264", q720.Codec);
        }

        [Fact]
        public void Twitch_MuxedVariantsAreNotCountedAsAudioTracks()
        {
            // Every Twitch variant carries mp4a, but only audio_only is a separate track.
            var meta = Parse("twitch-live.json");

            Assert.Single(meta.AudioTracks);
        }

        /// <summary>
        /// NTSC content reports 59.94 fps. Truncating it would label the tier "1080p59".
        /// </summary>
        [Fact]
        public void ParseJson_RoundsFractionalFps()
        {
            const string json = """
                {
                  "id": "ntsc",
                  "formats": [
                    {
                      "format_id": "1080p60",
                      "protocol": "m3u8_native",
                      "vcodec": "avc1.640028",
                      "acodec": "none",
                      "height": 1080,
                      "fps": 59.94
                    }
                  ]
                }
                """;

            var meta = VideoMetadata.ParseJson(json);

            Assert.NotNull(meta);
            Assert.Equal(60, Assert.Single(meta.Qualities).Fps);
        }
    }
}

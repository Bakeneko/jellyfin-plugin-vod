using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class CodecNamesTests
    {
        [Fact]
        public void Split_FindsTheVideoAndAudioCodecs_WhenVideoIsListedFirst()
        {
            var (video, audio) = CodecNames.Split("avc1.640028,mp4a.40.2");

            Assert.Equal("avc1.640028", video);
            Assert.Equal("mp4a.40.2", audio);
        }

        [Fact]
        public void Split_FindsTheVideoAndAudioCodecs_WhenAudioIsListedFirst()
        {
            // RFC 8216 does not order the CODECS list. Reading the video codec off position 0
            // made every exact tier unreachable for a platform listing audio first.
            var (video, audio) = CodecNames.Split("mp4a.40.2,avc1.640028");

            Assert.Equal("avc1.640028", video);
            Assert.Equal("mp4a.40.2", audio);
        }

        [Fact]
        public void Split_ReportsNoAudio_ForAVideoOnlyVariant()
        {
            var (video, audio) = CodecNames.Split("vp09.00.50.08");

            Assert.Equal("vp09.00.50.08", video);
            Assert.Null(audio);
        }

        [Fact]
        public void Split_KeepsAVideoCodecItDoesNotKnow()
        {
            // An unrecognised entry is taken as video rather than dropped, which is what reading
            // position 0 used to do for it: only codecs known to be audio or text are set aside.
            var (video, audio) = CodecNames.Split("mp4a.40.2,dvh1.05.06");

            Assert.Equal("dvh1.05.06", video);
            Assert.Equal("mp4a.40.2", audio);
        }

        [Theory]
        [InlineData("ac-4.02.01.01")]
        [InlineData("mhm1.0x0D")]
        [InlineData("mha1.0x0D")]
        [InlineData("dtsc")]
        [InlineData("dtse")]
        [InlineData("dtsx")]
        public void Split_RecognisesLessCommonAudioCodecs_WhicheverComesFirst(string audioCodec)
        {
            // An audio codec missing from the known set is taken for video, so listed first it hid the
            // HEVC tier from exact selection and that menu entry answered 503.
            var (video, audio) = CodecNames.Split($"{audioCodec},hvc1.2.4.L153.B0");

            Assert.Equal("hvc1.2.4.L153.B0", video);
            Assert.Equal(audioCodec, audio);
        }

        [Fact]
        public void Split_IgnoresATextCodec()
        {
            var (video, audio) = CodecNames.Split("wvtt,avc1.640028,mp4a.40.2");

            Assert.Equal("avc1.640028", video);
            Assert.Equal("mp4a.40.2", audio);
        }

        [Fact]
        public void Split_ReportsNothing_WhenTheAttributeIsMissing()
        {
            Assert.Equal((null, null), CodecNames.Split(null));
        }
    }
}

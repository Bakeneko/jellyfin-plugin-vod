using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class QualityLadderTests
    {
        [Fact]
        public void OfferedTiers_IsEmpty_WhenTheLadderIsEmpty()
        {
            // A row synced before schema v5, or a platform reporting no formats at all. The video
            // must still publish: one primary, no alternates.
            Assert.Empty(QualityLadder.OfferedTiers([]));
        }

        [Fact]
        public void OfferedTiers_ReturnsOneTier_WhenArteCapsAt1080pInBothCodecs()
        {
            // Arte ships every height in avc1 AND hev1. The reference tier is the best AVC, and the
            // best available at the top height is the same item once the codec preference applies,
            // so a single tier is offered and there is no pointless duplicate.
            var tiers = QualityLadder.OfferedTiers(
            [
                new VideoQuality(432, "h264", 25), new VideoQuality(432, "hevc", 25),
                new VideoQuality(720, "h264", 25), new VideoQuality(720, "hevc", 25),
                new VideoQuality(1080, "h264", 25), new VideoQuality(1080, "hevc", 25)
            ]);

            var tier = Assert.Single(tiers);
            Assert.Equal(1080, tier.Height);
            Assert.Equal("h264", tier.Codec);
            Assert.Equal("1080p", QualityLadder.LabelFor(tier));
        }

        [Fact]
        public void OfferedTiers_ReturnsTwoTiers_WhenTheTopHeightIsNotAvc()
        {
            // The YouTube case: 1080p in AVC, 2160p only in VP9. The viewer gets the direct-playing
            // tier and the best one, and the label warns about the codec.
            var tiers = QualityLadder.OfferedTiers(
            [
                new VideoQuality(720, "h264", 30),
                new VideoQuality(1080, "h264", 30),
                new VideoQuality(2160, "vp9", 30)
            ]);

            Assert.Equal(2, tiers.Count);
            Assert.Equal("1080p", QualityLadder.LabelFor(tiers[0]));
            Assert.Equal("2160p (VP9)", QualityLadder.LabelFor(tiers[1]));
        }

        [Fact]
        public void OfferedTiers_LabelsTheHeightActuallyElected_WhenTheVideoCapsBelow1080p()
        {
            // "1080p + best" is shorthand. On a 720p-max video the reference tier is 720p, and
            // claiming 1080p would advertise a tier SelectExact could not serve.
            var tiers = QualityLadder.OfferedTiers([new VideoQuality(720, "h264", 25)]);

            var tier = Assert.Single(tiers);
            Assert.Equal("720p", QualityLadder.LabelFor(tier));
        }

        [Fact]
        public void ReferenceTier_FallsBackToTheWholeLadder_WhenEveryHeightExceeds1080p()
        {
            // No AVC and nothing at or below 1080p, so the ceiling has nothing to cap: the rule falls
            // back to the whole ladder and elects its best, exactly as HlsPlaylistFilter.SelectDefault
            // does. Electing 1440 here would make the reference tier announce a height the primary's
            // bare path does not serve.
            var tier = QualityLadder.ReferenceTier(
            [
                new VideoQuality(1440, "vp9", 30),
                new VideoQuality(2160, "vp9", 30)
            ]);

            Assert.NotNull(tier);
            Assert.Equal(2160, tier.Height);
            Assert.Equal("vp9", tier.Codec);
        }

        [Fact]
        public void ReferenceTier_PrefersAvc_OverATallerNonAvcVariant()
        {
            // The whole point of mirroring SelectDefault: a VP9-only 2160p must not become the
            // default and surprise the viewer with a transcode.
            var tier = QualityLadder.ReferenceTier(
            [
                new VideoQuality(1080, "h264", 30),
                new VideoQuality(2160, "vp9", 30)
            ]);

            Assert.NotNull(tier);
            Assert.Equal(1080, tier.Height);
            Assert.Equal("h264", tier.Codec);
        }

        [Fact]
        public void ReferenceTier_CapsAt1080p_WhenNoAvcExists()
        {
            var tier = QualityLadder.ReferenceTier(
            [
                new VideoQuality(720, "vp9", 30),
                new VideoQuality(1080, "vp9", 30),
                new VideoQuality(2160, "vp9", 30)
            ]);

            Assert.NotNull(tier);
            Assert.Equal(1080, tier.Height);
        }

        [Fact]
        public void OfferedTiers_KeepsTheFpsSuffix_OnTheTwitchLadder()
        {
            // Twitch ships 1080p60 in AVC. One tier, and the label carries the fps.
            var tiers = QualityLadder.OfferedTiers([new VideoQuality(1080, "h264", 60)]);

            var tier = Assert.Single(tiers);
            Assert.Equal("1080p60", QualityLadder.LabelFor(tier));
        }

        [Fact]
        public void BestTier_BreaksACodecTieAtTheTopHeight_ByPreference()
        {
            var tier = QualityLadder.BestTier(
            [
                new VideoQuality(2160, "vp9", 30),
                new VideoQuality(2160, "h264", 30)
            ]);

            Assert.NotNull(tier);
            Assert.Equal("h264", tier.Codec);
        }

        [Fact]
        public void ReferenceTier_BreaksAnFpsTie_ByPreferringTheHigherFps()
        {
            // Same height, same codec: fps is the last discriminator in Elect, and it had no test
            // after the whitelist tests were replaced.
            var tier = QualityLadder.ReferenceTier(
            [
                new VideoQuality(1080, "h264", 30),
                new VideoQuality(1080, "h264", 60)
            ]);

            Assert.NotNull(tier);
            Assert.Equal(60, tier.Fps);
        }

        [Fact]
        public void ReferenceTier_ElectsTheTallestH264_WhenSeveralH264HeightsExist()
        {
            // The ordering this pins is the height-for-bandwidth substitution itself: inverted, the
            // reference tier would be the smallest AVC variant rather than the largest.
            var tier = QualityLadder.ReferenceTier(
            [
                new VideoQuality(480, "h264", 30),
                new VideoQuality(720, "h264", 30),
                new VideoQuality(1080, "h264", 30)
            ]);

            Assert.NotNull(tier);
            Assert.Equal(1080, tier.Height);
        }
    }
}

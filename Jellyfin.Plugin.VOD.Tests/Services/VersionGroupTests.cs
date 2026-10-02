using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class VersionGroupTests
    {
        private const string Uuid = "3f9a2b1c-0000-0000-0000-000000000001";
        private const string BaseUrl = "http://localhost:8096";

        private static VideoRecord Video(params VideoQuality[] qualities) => new()
        {
            Id = Uuid,
            Title = "A Video Title",
            VideoUrl = "https://example.com/watch?v=abc",
            SourceUrl = "https://example.com/channel",
            Extras = new VideoExtras { Qualities = qualities }
        };

        [Fact]
        public void KeyFor_PrimaryKeepsTheHistoricalFormat()
        {
            // Pinned: this key feeds GetNewItemId. Changing its shape gives every published item a
            // new id and orphans the whole library along with its user data.
            Assert.Equal($"vod:{Uuid}", VersionGroup.KeyFor(Uuid, null));
        }

        [Fact]
        public void KeyFor_AlternateAppendsHeightAndCodec()
        {
            Assert.Equal(
                $"vod:{Uuid}:2160:vp9",
                VersionGroup.KeyFor(Uuid, new Tier(2160, "vp9", 30)));
        }

        [Fact]
        public void KeyFor_DiffersBetweenTiers()
        {
            // Two items may never share a Guid, so the tier has to reach the key.
            Assert.NotEqual(
                VersionGroup.KeyFor(Uuid, new Tier(1080, "h264", 30)),
                VersionGroup.KeyFor(Uuid, new Tier(2160, "vp9", 30)));
        }

        [Fact]
        public void PathFor_PrimaryCarriesNoQueryString()
        {
            // A bare path is what makes the primary the "auto" entry: the route applies its own
            // default rule when height and codec are absent.
            Assert.Equal(
                $"http://localhost:8096/VOD/stream/{Uuid}.m3u8",
                VersionGroup.PathFor(BaseUrl, Uuid, null));
        }

        [Fact]
        public void PathFor_AlternateCarriesHeightAndCodec()
        {
            Assert.Equal(
                $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264",
                VersionGroup.PathFor(BaseUrl, Uuid, new Tier(1080, "h264", 30)));
        }

        [Fact]
        public void PathFor_TrimsATrailingSlashOnTheBaseUrl()
        {
            Assert.Equal(
                $"http://localhost:8096/VOD/stream/{Uuid}.m3u8",
                VersionGroup.PathFor("http://localhost:8096/", Uuid, null));
        }

        [Fact]
        public void TierOf_ReturnsNulls_ForTheBarePrimaryPath()
        {
            var (height, codec) = VersionGroup.TierOf(
                $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");

            Assert.Null(height);
            Assert.Null(codec);
        }

        [Fact]
        public void TierOf_ReadsTheTierBack()
        {
            var (height, codec) = VersionGroup.TierOf(
                $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");

            Assert.Equal(2160, height);
            Assert.Equal("vp9", codec);
        }

        [Fact]
        public void TierOf_IgnoresAStaleBaseUrlPrefix()
        {
            // LocalBaseUrl can change between two syncs, leaving every stored path with a stale
            // prefix. Only the query parameters identify the tier.
            var (height, codec) = VersionGroup.TierOf(
                $"https://jellyfin.example.com:9000/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");

            Assert.Equal(1080, height);
            Assert.Equal("h264", codec);
        }

        [Fact]
        public void TierOf_ReturnsNulls_ForNullOrGarbage()
        {
            Assert.Equal((null, null), VersionGroup.TierOf(null));
            Assert.Equal((null, null), VersionGroup.TierOf(string.Empty));
            Assert.Equal((null, null), VersionGroup.TierOf("/not/a/url?height=abc&codec=h264"));
        }

        [Fact]
        public void TierOf_ToleratesTheParametersInEitherOrder()
        {
            var (height, codec) = VersionGroup.TierOf(
                $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?codec=h264&height=720");

            Assert.Equal(720, height);
            Assert.Equal("h264", codec);
        }

        [Fact]
        public void For_ReturnsThePrimaryAlone_WhenTheLadderIsEmpty()
        {
            var members = VersionGroup.For(Video(), BaseUrl);

            var primary = Assert.Single(members);
            Assert.Null(primary.Tier);
            Assert.Equal("A Video Title", primary.Name);
            Assert.Equal($"vod:{Uuid}", primary.ItemKey);
        }

        [Fact]
        public void For_PrefixesEachAlternatesTierOntoTheVideoTitle()
        {
            // An alternate's name is the only string Jellyfin has for it: the version menu falls back
            // to item.Name for a non-file item (BaseItem.GetMediaSourceName, whose file-name-based
            // labelling needs IsFileProtocol), and the playback history shows that same name. A bare
            // "1080p" therefore reads as a title wherever the item surfaces outside the menu. The tier
            // leads so the discriminating token survives truncation.
            var members = VersionGroup.For(
                Video(
                    new VideoQuality(1080, "h264", 30),
                    new VideoQuality(2160, "vp9", 30)),
                BaseUrl);

            Assert.Equal(3, members.Count);
            Assert.Equal("A Video Title", members[0].Name);
            Assert.Equal("[1080p] A Video Title", members[1].Name);
            Assert.Equal("[2160p (VP9)] A Video Title", members[2].Name);
        }

        [Fact]
        public void For_OrdersTheAlternatesByAscendingHeight()
        {
            // The primary's LinkedAlternateVersions list is what orders the menu, so the plan of
            // record has to be in display order already.
            var members = VersionGroup.For(
                Video(
                    new VideoQuality(1080, "h264", 30),
                    new VideoQuality(2160, "vp9", 30)),
                BaseUrl);

            Assert.Equal(1080, members[1].Tier!.Height);
            Assert.Equal(2160, members[2].Tier!.Height);
        }

        [Fact]
        public void For_GivesEveryMemberADistinctKeyAndPath()
        {
            var members = VersionGroup.For(
                Video(
                    new VideoQuality(1080, "h264", 30),
                    new VideoQuality(2160, "vp9", 30)),
                BaseUrl);

            Assert.Equal(3, members.Count);
            Assert.Equal(3, new HashSet<string>(members.Select(m => m.ItemKey), StringComparer.Ordinal).Count);
            Assert.Equal(3, new HashSet<string>(members.Select(m => m.Path), StringComparer.Ordinal).Count);
        }

        [Fact]
        public void For_RoundTripsEveryAlternateThroughTierOf()
        {
            // The forward and inverse directions must agree, because the publisher writes paths and
            // the stream writer reads them back.
            var members = VersionGroup.For(
                Video(
                    new VideoQuality(1080, "h264", 30),
                    new VideoQuality(2160, "vp9", 30)),
                BaseUrl);

            foreach (var member in members)
            {
                var (height, codec) = VersionGroup.TierOf(member.Path);
                Assert.Equal(member.Tier?.Height, height);
                Assert.Equal(member.Tier?.Codec, codec);
            }
        }
    }
}

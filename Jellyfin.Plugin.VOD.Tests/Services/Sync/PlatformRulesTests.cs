using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class PlatformRulesTests
    {
        [Fact]
        public void TitleFor_PrefersFullTitle_OnATwitchVod()
        {
            // yt-dlp appends the broadcast date to a live VOD's title; fulltitle is the stream's own
            // name, so the item is not renamed when the broadcast ends. Measured 2026-10-03 (F15).
            var meta = new VideoMetadata
            {
                Id = "2890554613",
                ExtractorKey = "TwitchVod",
                Title = "DEMACIA CUP - NAVI vs RED 2026-10-03 09:58",
                FullTitle = "DEMACIA CUP - NAVI vs RED",
                LiveStatus = LiveStatus.Live
            };

            Assert.Equal("DEMACIA CUP - NAVI vs RED", PlatformRules.TitleFor(meta));
        }

        [Fact]
        public void TitleFor_UsesTheDescription_OnATwitchChannelStream()
        {
            // The channel root's title is generic ("otplol_ (live) 2026-10-03 09:54") and the stream's
            // real name is in description. Measured 2026-10-03 (F15).
            var meta = new VideoMetadata
            {
                Id = "317890898530",
                ExtractorKey = "TwitchStream",
                Title = "otplol_ (live) 2026-10-03 09:54",
                FullTitle = "otplol_ (live)",
                Description = "DEMACIA CUP - NAVI vs RED - SWISS STAGE - BO1",
                LiveStatus = LiveStatus.Live
            };

            Assert.Equal("DEMACIA CUP - NAVI vs RED - SWISS STAGE - BO1", PlatformRules.TitleFor(meta));
        }

        [Fact]
        public void TitleFor_LeavesOtherPlatformsAlone()
        {
            var meta = new VideoMetadata
            {
                Id = "abc",
                ExtractorKey = "Youtube",
                Title = "[LIVE] 🔴 DEMACIA CUP",
                FullTitle = "[LIVE] 🔴 DEMACIA CUP",
                Description = "Not the title.",
                LiveStatus = LiveStatus.Live
            };

            Assert.Equal("[LIVE] 🔴 DEMACIA CUP", PlatformRules.TitleFor(meta));
        }

        [Fact]
        public void LiveThumbnailFor_UsesTwitchsLivePreview_WhenTheVodHasNone()
        {
            // An in-progress Twitch VOD sometimes carries no thumbnail, which is why a generated
            // cover appeared: measured empty on 2026-10-03 (F16) and present on 2026-10-04 for the
            // same channel, so both cases are real and only the empty one needs a stand-in. The
            // channel's live preview is refreshed by Twitch during the broadcast.
            var meta = new VideoMetadata
            {
                Id = "2890554613",
                ExtractorKey = "TwitchVod",
                Thumbnail = null,
                LiveStatus = LiveStatus.Live
            };

            Assert.Equal(
                "https://static-cdn.jtvnw.net/previews-ttv/live_user_otplol_-1280x720.jpg",
                PlatformRules.LiveThumbnailFor(meta, "https://www.twitch.tv/otplol_/videos"));
        }

        [Fact]
        public void LiveThumbnailFor_ReturnsNull_WhenAFinishedArchiveHasNoThumbnail()
        {
            // Twitch serves its offline banner on the live preview once the broadcast has ended, so
            // the stand-in only holds while the video is live. A finished archive with no thumbnail
            // is better off with a generated cover than with a picture of an offline channel.
            var meta = new VideoMetadata
            {
                Id = "2889978018",
                ExtractorKey = "TwitchVod",
                Thumbnail = null,
                LiveStatus = LiveStatus.NotLive
            };

            Assert.Null(PlatformRules.LiveThumbnailFor(meta, "https://www.twitch.tv/otplol_/videos"));
        }

        [Fact]
        public void LiveThumbnailFor_KeepsAThumbnailTheMetadataAlreadyHas()
        {
            var meta = new VideoMetadata
            {
                Id = "2889978018",
                ExtractorKey = "TwitchVod",
                Thumbnail = "https://static-cdn.jtvnw.net/cf_vods/abc/thumb.jpg",
                LiveStatus = LiveStatus.NotLive
            };

            Assert.Equal(
                "https://static-cdn.jtvnw.net/cf_vods/abc/thumb.jpg",
                PlatformRules.LiveThumbnailFor(meta, "https://www.twitch.tv/otplol_/videos"));
        }

        // ── P1: IsTwitchExtractor gates Arte's title resolution correctly ──────────────────────────

        [Fact]
        public void IsTwitchExtractor_ReturnsTrueForTwitchVod()
        {
            // A TwitchVod with no fulltitle must never enter Arte's title logic: the gate in
            // SyncService branches on IsTwitchExtractor so that value equality cannot mislead it.
            var meta = new VideoMetadata
            {
                Id = "2890554613",
                ExtractorKey = "TwitchVod",
                Title = "some series",
                FullTitle = null
            };

            Assert.True(PlatformRules.IsTwitchExtractor(meta));
        }

        [Fact]
        public void IsTwitchExtractor_ReturnsTrueForTwitchStream()
        {
            var meta = new VideoMetadata { Id = "x", ExtractorKey = "TwitchStream" };

            Assert.True(PlatformRules.IsTwitchExtractor(meta));
        }

        [Fact]
        public void IsTwitchExtractor_ReturnsFalseForOtherPlatforms()
        {
            var meta = new VideoMetadata { Id = "x", ExtractorKey = "Youtube" };

            Assert.False(PlatformRules.IsTwitchExtractor(meta));
        }

        // ── P3: fallback paths and non-Twitch thumbnail ───────────────────────────────────────────

        [Fact]
        public void TitleFor_FallsBackToTitle_WhenATwitchStreamHasNoDescription()
        {
            // A TwitchStream whose description is empty or absent must not produce a blank name.
            var meta = new VideoMetadata
            {
                Id = "317890898530",
                ExtractorKey = "TwitchStream",
                Title = "otplol_ (live) 2026-10-03 09:54",
                Description = null
            };

            Assert.Equal("otplol_ (live) 2026-10-03 09:54", PlatformRules.TitleFor(meta));
        }

        [Fact]
        public void TitleFor_FallsBackToTitle_WhenATwitchVodHasNoFullTitle()
        {
            // A TwitchVod that carries no fulltitle (e.g. a very old VOD) must not produce null.
            var meta = new VideoMetadata
            {
                Id = "2890554613",
                ExtractorKey = "TwitchVod",
                Title = "some stream 2026-10-03",
                FullTitle = null
            };

            Assert.Equal("some stream 2026-10-03", PlatformRules.TitleFor(meta));
        }

        [Fact]
        public void LiveThumbnailFor_ReturnsNull_ForNonTwitchSourceWithNoThumbnail()
        {
            // A YouTube video with no thumbnail must not receive a Twitch CDN URL by accident.
            var meta = new VideoMetadata
            {
                Id = "dQw4w9WgXcQ",
                ExtractorKey = "Youtube",
                Thumbnail = null
            };

            Assert.Null(PlatformRules.LiveThumbnailFor(meta, "https://www.youtube.com/@RickAstleyYT"));
        }

        [Fact]
        public void LiveThumbnailFor_TreatsTwitchsProcessingPlaceholderAsNoThumbnail()
        {
            // An in-progress Twitch VOD does not lack a thumbnail: it carries a placeholder,
            // measured 2026-10-08. Taking it at face value showed a grey "processing" picture where
            // the live preview belongs.
            var meta = new VideoMetadata
            {
                Id = "v2895006332",
                ExtractorKey = "TwitchVod",
                Thumbnail = "https://vod-secure.twitch.tv/_404/404_processing_320x180.png",
                LiveStatus = LiveStatus.Live
            };

            Assert.Equal(
                "https://static-cdn.jtvnw.net/previews-ttv/live_user_otplol_-1280x720.jpg",
                PlatformRules.LiveThumbnailFor(meta, "https://www.twitch.tv/otplol_/videos"));
        }

        [Fact]
        public void LiveThumbnailFor_DropsTheProcessingPlaceholder_OnceTheBroadcastHasEnded()
        {
            // The placeholder is no picture of the video either way; an archive still carrying it
            // gets none rather than a grey square.
            var meta = new VideoMetadata
            {
                Id = "v2895006332",
                ExtractorKey = "TwitchVod",
                Thumbnail = "https://vod-secure.twitch.tv/_404/404_processing_320x180.png",
                LiveStatus = LiveStatus.NotLive
            };

            Assert.Null(PlatformRules.LiveThumbnailFor(meta, "https://www.twitch.tv/otplol_/videos"));
        }

        // ── SupportsLiveProbe ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("https://www.twitch.tv/otplol_", true)]
        [InlineData("https://www.twitch.tv/otplol_/videos", true)]
        [InlineData("https://www.youtube.com/@OTP_VODs/streams", true)]
        [InlineData("https://www.youtube.com/@OTP_VODs/streams/", true)]
        [InlineData("https://www.youtube.com/@LeMondeDeJamy/videos", false)]
        [InlineData("https://www.youtube.com/@LeMondeDeJamy", false)]
        [InlineData("https://www.youtube.com/playlist?list=PLxxx", false)]
        [InlineData("https://www.youtube.com/watch?v=sKuVMM02LVM", false)]
        [InlineData("https://www.twitch.tv/videos/2890554613", false)]
        [InlineData("https://www.arte.tv/fr/videos/RC-022076/les-idees-larges/", false)]
        [InlineData("https://www.france.tv/documentaires/", false)]
        public void SupportsLiveProbe_OnlyWherePollingCanFindALive(string url, bool expected)
        {
            // A YouTube channel broadcasts on its /streams tab; polling its /videos tab or a playlist
            // spent one yt-dlp call per source every five minutes — about 1 400 a day for the user's
            // five such channels, measured 2026-10-08 — to learn nothing, and spending calls is what
            // brings YouTube's bot check. Arte and france.tv list no live at all.
            Assert.Equal(expected, PlatformRules.SupportsLiveProbe(url));
        }
    }

    public sealed class PlatformRulesListingVerdictTests
    {
        [Theory]
        [InlineData("Youtube")]
        [InlineData("youtube")]
        [InlineData("YoutubeTab")]
        public void ListingTellsUnavailable_IsTrueForYoutube(string ieKey)
        {
            // Measured: a dead entry is a template of nulls, and the 10 titled entries of a
            // 43-entry playlist were exactly the 10 YouTube said were watchable.
            Assert.True(PlatformRules.ListingTellsUnavailable(ieKey));
        }

        [Theory]
        [InlineData("ArteTV")]
        [InlineData("ArteTVPlaylist")]
        public void ListingTellsUnavailable_IsFalseForArte(string ieKey)
        {
            // Measured: 0 titles out of 79. Reading silence as death here would empty an entire
            // Arte collection from the library on the first sync.
            Assert.False(PlatformRules.ListingTellsUnavailable(ieKey));
        }

        [Theory]
        [InlineData("Twitch")]
        [InlineData("TwitchVideos")]
        public void ListingTellsUnavailable_IsFalseForTwitch(string ieKey)
        {
            // Measured: 15 titled out of 15. A deleted VOD leaves the listing entirely, which is
            // the orphan sweep's business.
            Assert.False(PlatformRules.ListingTellsUnavailable(ieKey));
        }

        [Theory]
        [InlineData("Vimeo")]
        [InlineData("generic")]
        [InlineData("")]
        [InlineData(null)]
        public void ListingTellsUnavailable_IsFalseForAnUnmeasuredPlatform(string? ieKey)
        {
            // A platform that has not been measured gets the prudent answer, never a neighbour's.
            Assert.False(PlatformRules.ListingTellsUnavailable(ieKey));
        }
    }

}

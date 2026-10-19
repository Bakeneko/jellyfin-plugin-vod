using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class VodNativeIdTests
    {
        // ── ForVideo ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Youtube",    "PM6lMoClzy8",   "youtube_v_PM6lMoClzy8")]
        [InlineData("YoutubeTab", "PM6lMoClzy8",   "youtube_v_PM6lMoClzy8")]
        [InlineData("ArteTV",     "126957-001-A",  "arte_v_126957-001-A")]
        [InlineData("ArteTV",     "128788-000-A",  "arte_v_128788-000-A")]
        [InlineData("FranceTV",   "2566c050-d28b-4d43-a387-31956cfb9b93", "ftv_v_2566c050-d28b-4d43-a387-31956cfb9b93")]
        public void ForVideo_KnownExtractor_ReturnsCorrectId(string extractor, string nativeId, string expected)
            => Assert.Equal(expected, VodNativeId.ForVideo(extractor, nativeId));

        [Theory]
        [InlineData(null,    "PM6lMoClzy8")]
        [InlineData("",     "PM6lMoClzy8")]
        [InlineData("DRTV", "abc123")]        // unknown extractor
        public void ForVideo_UnknownOrNullExtractor_ReturnsNull(string? extractor, string nativeId)
            => Assert.Null(VodNativeId.ForVideo(extractor, nativeId));

        [Fact]
        public void ForVideo_NullNativeId_ReturnsNull()
            => Assert.Null(VodNativeId.ForVideo("Youtube", null));

        [Fact]
        public void ForVideo_EmptyNativeId_ReturnsNull()
            => Assert.Null(VodNativeId.ForVideo("Youtube", string.Empty));

        // ── ForSource ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData("https://www.youtube.com/@deadwattsofficiel/videos",           "youtube_ch_deadwattsofficiel")]
        [InlineData("https://www.youtube.com/@MrBeast/videos",                     "youtube_ch_MrBeast")]
        [InlineData("https://youtube.com/@arte",                                   "youtube_ch_arte")]
        [InlineData("https://www.youtube.com/channel/UCwBCIWxUAT4J2MnjKGPeFMQ",   "youtube_ch_UCwBCIWxUAT4J2MnjKGPeFMQ")]
        [InlineData("https://www.youtube.com/c/artede",                            "youtube_ch_artede")]
        [InlineData("https://www.youtube.com/c/artede/videos",                     "youtube_ch_artede")]
        [InlineData("https://www.youtube.com/user/Thinkerview",                    "youtube_ch_Thinkerview")]
        [InlineData("https://www.youtube.com/playlist?list=PLAvUNBCqj5HGqACcp4maJDye8hznbIwhe", "youtube_pl_PLAvUNBCqj5HGqACcp4maJDye8hznbIwhe")]
        [InlineData("https://www.arte.tv/fr/videos/RC-027708/happy-valley/",       "arte_pl_RC-027708")]
        [InlineData("https://www.arte.tv/fr/videos/RC-022076/les-idees-larges/",   "arte_pl_RC-022076")]
        [InlineData("https://www.arte.tv/de/videos/RC-012345/some-show/",          "arte_pl_RC-012345")]
        [InlineData("https://www.arte.tv/fr/videos/128788-000-A/la-femme-qui-murmure-a-l-oreille-des-baleines-bleues/", "arte_v_128788-000-A")]
        [InlineData("https://www.france.tv/documentaires/doc/8808888-le-titre.html", "ftv_v_8808888")]
        [InlineData("https://www.france.tv/france-5/c-dans-l-air/7654321-emission.html", "ftv_v_7654321")]
        [InlineData("https://www.france.tv/documentaires/doc/8808888-le-titre.html?autoplay=1", "ftv_v_8808888")]
        [InlineData("https://www.france.tv/documentaires/doc/8808888-le-titre/", "ftv_v_8808888")]
        public void ForSource_KnownUrl_ReturnsCorrectId(string url, string expected)
            => Assert.Equal(expected, VodNativeId.ForSource(url));

        /// <summary>
        /// A percent-encoded handle must not be truncated at the escape. The production incident:
        /// "@arte%C3%A9vasion" derived "youtube_ch_arte" — the same key as "@arte" — so both
        /// sources computed the same series GUID and 597 episodes were filed under the wrong
        /// series, each pass renaming it and un-claiming the other's episodes.
        /// </summary>
        [Theory]
        [InlineData("https://www.youtube.com/@arte%C3%A9vasion/videos", "youtube_ch_arteévasion")]
        [InlineData("https://www.youtube.com/@arte/videos",             "youtube_ch_arte")]
        [InlineData("https://www.youtube.com/@caf%C3%A9/videos",        "youtube_ch_café")]
        public void ForSource_PercentEncodedHandle_IsDecodedWhole_NotTruncated(
            string url, string expected)
            => Assert.Equal(expected, VodNativeId.ForSource(url));

        /// <summary>
        /// Whatever the quality of the derivation, two sources resolving to one key must be
        /// reported rather than silently overwrite each other's series. The silence is what made
        /// the Arte incident expensive, not the regex.
        /// </summary>
        [Fact]
        public void FindCollisions_ReportsTwoSourcesSharingOneKey()
        {
            var collisions = VodNativeId.FindCollisions(
            [
                "https://www.youtube.com/@arte/videos",
                "https://youtube.com/@arte",
                "https://www.youtube.com/@terrapodia/videos",
            ]);

            var only = Assert.Single(collisions);
            Assert.Equal("youtube_ch_arte", only.Key);
            Assert.Equal(2, only.Urls.Count);
        }

        [Fact]
        public void FindCollisions_IsEmpty_WhenEveryKeyIsDistinct()
        {
            var collisions = VodNativeId.FindCollisions(
            [
                "https://www.youtube.com/@arte/videos",
                "https://www.youtube.com/@arte%C3%A9vasion/videos",
                "https://www.youtube.com/playlist?list=PLabc",
            ]);

            Assert.Empty(collisions);
        }

        [Fact]
        public void FindCollisions_IgnoresUrlsThatDeriveNoKey()
        {
            // Two unrecognised URLs both derive null; that is not a collision, it is two sources
            // the publisher will key on their URL instead.
            var collisions = VodNativeId.FindCollisions(
                ["https://example.com/a", "https://example.com/b"]);

            Assert.Empty(collisions);
        }

        [Fact]
        public void ForSource_TwoHandlesSharingAnAsciiPrefix_DeriveDistinctKeys()
        {
            var a = VodNativeId.ForSource("https://www.youtube.com/@arte/videos");
            var b = VodNativeId.ForSource("https://www.youtube.com/@arte%C3%A9vasion/videos");

            Assert.NotEqual(a, b);
        }

        // ── IsChannel ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData("youtube_ch_arte",   true)]
        [InlineData("youtube_ch_UCabc",  true)]
        [InlineData("youtube_pl_PLabc",  false)]
        [InlineData("arte_pl_RC-027708", false)]
        [InlineData("arte_v_128788-000-A", false)]
        [InlineData("ftv_v_8808888",     false)]
        [InlineData(null,                false)]
        [InlineData("",                  false)]
        public void IsChannel_DistinguishesChannelsFromEverythingElse(string? key, bool expected)
            => Assert.Equal(expected, VodNativeId.IsChannel(key));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("https://www.france.tv/direct")]
        [InlineData("https://www.france.tv/sport/123-categorie/latest-news.html")]
        [InlineData("https://example.com/some/path")]
        public void ForSource_UnknownOrNull_ReturnsNull(string? url)
            => Assert.Null(VodNativeId.ForSource(url));

        // ── Twitch ────────────────────────────────────────────────────────────

        [Fact]
        public void ForVideo_PrefixesATwitchVideo()
        {
            Assert.Equal("twitch_v_2890554613", VodNativeId.ForVideo("Twitch", "2890554613"));
        }

        [Theory]
        [InlineData("https://www.twitch.tv/otplol_", "twitch_ch_otplol_")]
        [InlineData("https://www.twitch.tv/otplol_/videos", "twitch_ch_otplol_")]
        public void ForSource_DerivesTheSameChannelKeyFromBothTwitchForms(string url, string expected)
        {
            // Both forms describe one channel: the key must not depend on which one was declared.
            Assert.Equal(expected, VodNativeId.ForSource(url));
        }

        [Fact]
        public void ForSource_NamesNoChannel_ForTwitchsVideosPathItself()
        {
            // Deliberate, not a side effect of sharing the host rule: "twitch.tv/videos" names no
            // channel, and Detect calls it Generic. The old pattern answered "twitch_ch_videos",
            // which made IsChannel true and sent SeriesNaming down the channel-name path for a URL
            // that has no channel to be named after.
            Assert.Null(VodNativeId.ForSource("https://twitch.tv/videos"));
            Assert.Equal(SourceType.Generic, SourceTypeDetector.Detect("https://twitch.tv/videos"));
        }

        [Fact]
        public void ForSource_AndDetect_AgreeThatALookalikeHostIsNotTwitch()
        {
            // The host rule is shared, so the two cannot disagree about "mytwitch.tv" — which they
            // did, and which was enough to route a stranger's page into the probe.
            const string Lookalike = "https://mytwitch.tv/otplol_";
            Assert.Equal(SourceType.Generic, SourceTypeDetector.Detect(Lookalike));
            Assert.Null(VodNativeId.ForSource(Lookalike));
        }

        [Fact]
        public void ForSource_AndDetect_AgreeOnMobileTwitchUrl()
        {
            // Detect accepts any subdomain of twitch.tv; ForSource must match the same host set so
            // a URL copied from the Twitch mobile app yields the same channel key as its www form.
            const string MobileUrl = "https://m.twitch.tv/otplol_";
            Assert.Equal(SourceType.TwitchChannelLive, SourceTypeDetector.Detect(MobileUrl));
            Assert.Equal("twitch_ch_otplol_", VodNativeId.ForSource(MobileUrl));
        }
    }
}

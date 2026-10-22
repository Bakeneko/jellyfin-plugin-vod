using System;
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
        [InlineData("https://www.youtube.com/@MrBeast/videos",                     "youtube_ch_mrbeast")]
        [InlineData("https://youtube.com/@arte",                                   "youtube_ch_arte")]
        [InlineData("https://www.youtube.com/channel/UCwBCIWxUAT4J2MnjKGPeFMQ",   "youtube_ch_UCwBCIWxUAT4J2MnjKGPeFMQ")]
        [InlineData("https://www.youtube.com/c/artede",                            "youtube_ch_artede")]
        [InlineData("https://www.youtube.com/c/artede/videos",                     "youtube_ch_artede")]
        [InlineData("https://www.youtube.com/user/Thinkerview",                    "youtube_ch_thinkerview")]
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

        // ── Case folding ──────────────────────────────────────────────────────

        /// <summary>
        /// YouTube resolves handles, /c/ paths and /user/ paths case-insensitively. Without
        /// folding, "@Arte" and "@arte" derive different series GUIDs, so each publication
        /// renames the series and un-claims the other source's episodes indefinitely.
        /// </summary>
        [Theory]
        [InlineData("https://www.youtube.com/@Arte/videos",   "https://www.youtube.com/@arte/videos")]
        [InlineData("https://www.youtube.com/c/Foo/videos",   "https://www.youtube.com/c/foo/videos")]
        [InlineData("https://www.youtube.com/user/Bar",       "https://www.youtube.com/user/bar")]
        public void ForSource_FoldsCase_ForHandlesAndLegacyChannelForms(string mixedCase, string lowerCase)
            => Assert.Equal(VodNativeId.ForSource(mixedCase), VodNativeId.ForSource(lowerCase));

        /// <summary>
        /// Channel IDs are assigned by YouTube and are case-sensitive: UCxxx and ucxxx name
        /// distinct channels. If the key were folded, two different series would silently share
        /// one GUID and each publication would rename and un-claim the other's episodes.
        /// This is the most expensive possible mistake, so a dedicated test is the guard.
        /// </summary>
        [Fact]
        public void ForSource_PreservesCase_ForYouTubeChannelId()
        {
            var upper = VodNativeId.ForSource("https://www.youtube.com/channel/UCAbCdEfGhIjKl");
            var lower = VodNativeId.ForSource("https://www.youtube.com/channel/UCabcdefghijkl");
            Assert.NotEqual(upper, lower);
        }

        /// <summary>
        /// Playlist IDs are case-sensitive: PLxxx and plxxx identify distinct playlists.
        /// Same guard as the channel ID test — silent series merges are the most expensive
        /// possible outcome of an erroneous fold.
        /// </summary>
        [Fact]
        public void ForSource_PreservesCase_ForYouTubePlaylistId()
        {
            var upper = VodNativeId.ForSource("https://www.youtube.com/playlist?list=PLAbCdEfGh");
            var lower = VodNativeId.ForSource("https://www.youtube.com/playlist?list=PLAbcdefgh");
            Assert.NotEqual(upper, lower);
        }

        // ── Unicode normalisation ──────────────────────────────────────────────

        /// <summary>
        /// NFC and NFD are two byte-level encodings of the same character. A handle stored in
        /// one encoding or the other names the same channel, so both must produce the same key.
        /// </summary>
        [Fact]
        public void ForSource_NfcAndNfdHandles_DeriveTheSameKey()
        {
            // "\u00E9" is U+00E9, ‘é’ in NFC (precomposed Latin small letter e with acute).
            // "e\u0301" is its NFD decomposition: Latin small letter e (U+0065) followed by
            // combining acute accent (U+0301). Using escape sequences rather than literal characters
            // makes the encoding explicit and verifiable by a reader without opening a hex editor.
            const string NfcUrl = "https://www.youtube.com/@\u00E9vasion/videos";
            const string NfdUrl = "https://www.youtube.com/@e\u0301vasion/videos";
            Assert.Equal(VodNativeId.ForSource(NfcUrl), VodNativeId.ForSource(NfdUrl));
        }

        // ── F2: legacy /c/ and /user/ percent-encoding ────────────────────────

        /// <summary>
        /// Before the fix, the /c/ and /user/ regexes used a character class that excluded "%",
        /// so "/c/Art%C3%A9" was truncated to "Art" — the same defect that "@arte%C3%A9vasion"
        /// had before v3. The corrected key must match the literal-character form of the URL
        /// and must not be truncated at the percent-escape.
        /// </summary>
        [Fact]
        public void ForSource_LegacyChannelWithPercentEncodedName_IsDecodedWhole()
        {
            var encoded = VodNativeId.ForSource("https://www.youtube.com/c/Art%C3%A9");
            // "é" is 'é' NFC — the decoded form of %C3%A9.
            var literal  = VodNativeId.ForSource("https://www.youtube.com/c/Arté");
            Assert.NotNull(encoded);
            Assert.NotEqual("youtube_ch_art", encoded);   // must not truncate at the escape
            Assert.Equal(literal, encoded);               // must match the literal-character form
        }

        // ── FindCollisions after case folding ─────────────────────────────────

        /// <summary>
        /// After case folding, "@Arte" and "@arte" derive the same key. FindCollisions must
        /// surface them as a collision so the operator learns they point at one channel, rather
        /// than silently alternating which source owns the series on every publication pass.
        /// </summary>
        [Fact]
        public void FindCollisions_ReportsCaseFoldingCollision()
        {
            var collisions = VodNativeId.FindCollisions(
            [
                "https://www.youtube.com/@Arte/videos",
                "https://www.youtube.com/@arte/videos",
            ]);

            var only = Assert.Single(collisions);
            Assert.Equal("youtube_ch_arte", only.Key);
            Assert.Equal(2, only.Urls.Count);
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

        /// <summary>
        /// Twitch logins are case-insensitive at the platform level: "Riotgames" and "riotgames"
        /// are the same channel. Without case folding, a user who copies the URL from the browser
        /// (mixed case) and a user who types it in lowercase would end up with two separate series.
        /// Case is folded in ForSource, not in TwitchUrls.ChannelFrom, because ChannelFrom also
        /// drives SourceTypeDetector and PlatformRules — widening a key fix there would introduce
        /// risk this task does not cover.
        /// </summary>
        [Fact]
        public void ForSource_Twitch_FoldsChannelCase()
        {
            var upper = VodNativeId.ForSource("https://www.twitch.tv/Riotgames");
            var lower = VodNativeId.ForSource("https://www.twitch.tv/riotgames");
            Assert.Equal(upper, lower);
            Assert.Equal("twitch_ch_riotgames", lower);
        }

        // ── UuidForVideo ──────────────────────────────────────────────────────
        //
        // The video uuid is what Jellyfin's watch history hangs on. GetNewItemId derives an item's
        // GUID from "vod:{uuid}", and for a Movie that GUID is the ONLY user-data key Jellyfin
        // writes under — Video.GetUserDataKeys inserts a provider key only for Imdb or Tmdb, and
        // Jellyfin 12 accepts neither from this plugin. A random uuid therefore means that any
        // video which leaves the database and comes back loses everything the viewer had done
        // with it, silently.

        [Fact]
        public void UuidForVideo_IsReproducible()
        {
            // The whole point: the same source and the same platform id must produce the same uuid
            // in a different process, on a different day, after a purge.
            Assert.Equal(
                VodNativeId.UuidForVideo("youtube_ch_arte", "PM6lMoClzy8"),
                VodNativeId.UuidForVideo("youtube_ch_arte", "PM6lMoClzy8"));
        }

        [Fact]
        public void UuidForVideo_SeparatesTheSourceFromTheNativeId()
        {
            // Concatenating without a separator would make ("ab", "c") and ("a", "bc") the same
            // video, so two sources could claim one Jellyfin item and overwrite each other's.
            Assert.NotEqual(
                VodNativeId.UuidForVideo("ab", "c"),
                VodNativeId.UuidForVideo("a", "bc"));
        }

        [Fact]
        public void UuidForVideo_DistinguishesTheSource()
        {
            // The same platform id under two sources is a real case: a video cross-posted to two
            // channels. They are two rows, so they must be two items.
            Assert.NotEqual(
                VodNativeId.UuidForVideo("youtube_ch_arte", "PM6lMoClzy8"),
                VodNativeId.UuidForVideo("youtube_ch_other", "PM6lMoClzy8"));
        }

        [Fact]
        public void UuidForVideo_DistinguishesTheVideo()
        {
            Assert.NotEqual(
                VodNativeId.UuidForVideo("youtube_ch_arte", "PM6lMoClzy8"),
                VodNativeId.UuidForVideo("youtube_ch_arte", "aaaaaaaaaaa"));
        }

        [Fact]
        public void UuidForVideo_IsAWellFormedUuid()
        {
            // Stored as text in a TEXT primary key and published inside a provider id, so the
            // shape has to be the one every reader expects. Version 8 is the one RFC 9562 reserves
            // for a value derived by the application rather than drawn at random.
            var uuid = VodNativeId.UuidForVideo("youtube_ch_arte", "PM6lMoClzy8");

            Assert.Equal(uuid, Guid.Parse(uuid).ToString());
            Assert.Equal('8', uuid[14]);
        }
    }
}

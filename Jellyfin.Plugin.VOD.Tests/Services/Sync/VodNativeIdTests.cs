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
    }
}

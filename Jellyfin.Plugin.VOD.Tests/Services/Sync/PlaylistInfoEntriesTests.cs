using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// Entry parsing, checked against the shapes yt-dlp 2026.08.19 actually returns for
    /// <c>--flat-playlist -J</c>. Arte lists bare url references with no <c>id</c> at all, which
    /// silently dropped every entry and made those sources sync zero videos.
    /// </summary>
    public sealed class PlaylistInfoEntriesTests
    {
        /// <summary>
        /// Trimmed from real output for
        /// <c>https://www.arte.tv/fr/videos/RC-022076/les-idees-larges/</c>. The entries carry
        /// exactly these four keys — no <c>id</c>, no <c>title</c>, no <c>duration</c>.
        /// </summary>
        private const string ArtePlaylistJson = """
        {
          "_type": "playlist",
          "title": "Les idées larges",
          "entries": [
            {
              "ie_key": "ArteTV",
              "_type": "url",
              "url": "https://www.arte.tv/fr/videos/130515-005-A/pourquoi-eclate-t-on-en-sanglots/",
              "__x_forwarded_for_ip": null
            },
            {
              "ie_key": "ArteTV",
              "_type": "url",
              "url": "https://www.arte.tv/fr/videos/130515-004-A/un-autre-titre/",
              "__x_forwarded_for_ip": null
            }
          ]
        }
        """;

        private const string YoutubePlaylistJson = """
        {
          "_type": "playlist",
          "title": "Terrapodia - Videos",
          "entries": [
            {
              "ie_key": "Youtube",
              "_type": "url",
              "id": "PM6lMoClzy8",
              "url": "https://www.youtube.com/watch?v=PM6lMoClzy8",
              "title": "Some video",
              "duration": 1169
            }
          ]
        }
        """;

        private const string UnknownExtractorJson = """
        {
          "_type": "playlist",
          "title": "Somewhere else",
          "entries": [
            {
              "ie_key": "SomeOtherSite",
              "_type": "url",
              "url": "https://example.test/videos/abc123/a-slug/"
            }
          ]
        }
        """;

        [Fact]
        public void ArteEntries_RecoverIdsFromTheirUrls()
        {
            var info = PlaylistInfo.ParseJson(ArtePlaylistJson);

            Assert.NotNull(info);
            Assert.Equal(2, info.Entries.Count);
            Assert.Equal("130515-005-A", info.Entries[0].Id);
            Assert.Equal("130515-004-A", info.Entries[1].Id);
            Assert.Equal(
                "https://www.arte.tv/fr/videos/130515-005-A/pourquoi-eclate-t-on-en-sanglots/",
                info.Entries[0].VideoUrl);
            Assert.Equal("ArteTV", info.Entries[0].IeKey);
        }

        [Fact]
        public void ExplicitId_IsPreferredOverTheUrl()
        {
            var info = PlaylistInfo.ParseJson(YoutubePlaylistJson);

            Assert.NotNull(info);
            var entry = Assert.Single(info.Entries);
            Assert.Equal("PM6lMoClzy8", entry.Id);
            Assert.Equal(1169, entry.Duration);
        }

        /// <summary>
        /// The recovery is keyed on the extractor on purpose: an id guessed for a site whose
        /// canonical id is not the URL segment would never match the stored row, so every pass
        /// would republish the source instead of failing where it can be seen.
        /// </summary>
        [Fact]
        public void UnknownExtractorWithoutId_IsDropped()
        {
            var info = PlaylistInfo.ParseJson(UnknownExtractorJson);

            Assert.NotNull(info);
            Assert.Empty(info.Entries);
        }

        [Theory]
        [InlineData("https://www.arte.tv/fr/videos/130515-005-A/pourquoi-eclate-t-on-en-sanglots/", "130515-005-A")]
        [InlineData("https://www.arte.tv/de/videos/RC-022076/die-grossen-ideen/", "RC-022076")]
        [InlineData("https://www.arte.tv/fr/videos/128788-000-A/la-femme/", "128788-000-A")]
        public void DeriveIdFromUrl_TakesTheSegmentAfterVideos(string url, string expected)
            => Assert.Equal(expected, PlaylistInfo.DeriveIdFromUrl(url, "ArteTV"));

        [Theory]
        [InlineData("https://www.arte.tv/fr/videos/", "ArteTV")]
        [InlineData("https://www.arte.tv/fr/guide/something/", "ArteTV")]
        [InlineData("not-an-absolute-url", "ArteTV")]
        [InlineData("https://www.youtube.com/watch?v=abc", "Youtube")]
        [InlineData("https://www.arte.tv/fr/videos/130515-005-A/slug/", "Youtube")]
        public void DeriveIdFromUrl_ReturnsNullWhenItCannotRecover(string url, string ieKey)
            => Assert.Null(PlaylistInfo.DeriveIdFromUrl(url, ieKey));
    }
}

using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// Image selection, checked against the shapes yt-dlp 2026.08.19 actually returns. The fixtures
    /// below are trimmed from real output: a YouTube channel lists its banner first and its avatar
    /// last, which is what previously made a 6:1 banner the series poster.
    /// </summary>
    public sealed class PlaylistInfoImagesTests
    {
        private const string YoutubeChannelJson = """
        {
          "_type": "playlist",
          "title": "Le Fossoyeur de Films - Videos",
          "entries": [],
          "thumbnails": [
            { "id": "0", "url": "https://yt3.googleusercontent.com/banner", "width": 1060, "height": 175 },
            { "id": "5", "url": "https://yt3.googleusercontent.com/banner", "width": 2560, "height": 424 },
            { "id": "banner_uncropped", "url": "https://yt3.googleusercontent.com/banner" },
            { "id": "7", "url": "https://yt3.googleusercontent.com/avatar", "width": 900, "height": 900 },
            { "id": "avatar_uncropped", "url": "https://yt3.googleusercontent.com/avatar" }
          ]
        }
        """;

        private const string YoutubePlaylistJson = """
        {
          "_type": "playlist",
          "title": "WORLDS 2025",
          "entries": [],
          "thumbnails": [
            { "id": "0", "url": "https://i.ytimg.com/vi/abc/hqdefault.jpg", "width": 168, "height": 94 },
            { "id": "3", "url": "https://i.ytimg.com/vi/abc/hqdefault.jpg", "width": 336, "height": 188 }
          ]
        }
        """;

        private const string ArtePlaylistJson = """
        {
          "_type": "playlist",
          "title": "Happy Valley",
          "entries": []
        }
        """;

        [Fact]
        public void Channel_ExposesTheChannelNameSeparatelyFromTheTabTitle()
        {
            // yt-dlp names a channel's playlist after the tab, not the channel.
            const string json = """
            {
              "_type": "playlist",
              "title": "Le Fossoyeur de Films - Videos",
              "channel": "Le Fossoyeur de Films",
              "uploader": "Le Fossoyeur de Films",
              "entries": []
            }
            """;

            var info = PlaylistInfo.ParseJson(json);

            Assert.NotNull(info);
            Assert.Equal("Le Fossoyeur de Films - Videos", info!.Title);
            Assert.Equal("Le Fossoyeur de Films", info.ChannelName);
        }

        [Fact]
        public void Playlist_ChannelNameIsTheOwnerNotThePlaylist()
        {
            // The trap this exists to pin: a playlist reports "channel" too, but it is the OWNER.
            // Naming the series from it would turn "WORLDS 2025" into "OTP - VODs", which is why
            // only a channel source may use the field.
            const string json = """
            {
              "_type": "playlist",
              "title": "WORLDS 2025",
              "channel": "OTP - VODs",
              "entries": []
            }
            """;

            var info = PlaylistInfo.ParseJson(json);

            Assert.NotNull(info);
            Assert.Equal("WORLDS 2025", info!.Title);
            Assert.Equal("OTP - VODs", info.ChannelName);
        }

        [Fact]
        public void ChannelName_FallsBackToUploader()
        {
            const string json = """
            {
              "_type": "playlist",
              "title": "x - Videos",
              "uploader": "x",
              "entries": []
            }
            """;

            Assert.Equal("x", PlaylistInfo.ParseJson(json)!.ChannelName);
        }

        [Fact]
        public void ChannelName_IsNullWhenThePlatformReportsNone()
        {
            // arte reports neither field.
            Assert.Null(PlaylistInfo.ParseJson(ArtePlaylistJson)!.ChannelName);
        }

        [Fact]
        public void Channel_PrefersTheAvatarOverTheBanner()
        {
            var info = PlaylistInfo.ParseJson(YoutubeChannelJson);

            Assert.NotNull(info);
            Assert.Equal("https://yt3.googleusercontent.com/avatar", info!.ThumbnailUrl);
        }

        [Fact]
        public void Channel_PutsTheBannerInTheWideSlot()
        {
            var info = PlaylistInfo.ParseJson(YoutubeChannelJson);

            Assert.NotNull(info);
            Assert.Equal("https://yt3.googleusercontent.com/banner", info!.BannerUrl);
        }

        [Fact]
        public void Playlist_TakesTheLargestVariantAndHasNoBanner()
        {
            // All four YouTube playlist entries are the same file at different declared crops, so the
            // only thing that matters is that we no longer take the 168x94 one.
            var info = PlaylistInfo.ParseJson(YoutubePlaylistJson);

            Assert.NotNull(info);
            Assert.Equal("https://i.ytimg.com/vi/abc/hqdefault.jpg", info!.ThumbnailUrl);
            Assert.Null(info.BannerUrl);
        }

        [Fact]
        public void ArtePlaylist_HasNoImageAtAll()
        {
            // Verified against yt-dlp: an arte collection exposes neither "thumbnail" nor "thumbnails",
            // in flat and full extraction alike. The series image has to come from a fallback.
            var info = PlaylistInfo.ParseJson(ArtePlaylistJson);

            Assert.NotNull(info);
            Assert.Null(info!.ThumbnailUrl);
            Assert.Null(info.BannerUrl);
        }

        [Fact]
        public void ScalarThumbnailWinsWhenPresent()
        {
            const string json = """
            {
              "_type": "playlist",
              "title": "x",
              "entries": [],
              "thumbnail": "https://example.test/scalar.jpg",
              "thumbnails": [ { "id": "0", "url": "https://example.test/array.jpg", "width": 900, "height": 900 } ]
            }
            """;

            var info = PlaylistInfo.ParseJson(json);

            Assert.NotNull(info);
            Assert.Equal("https://example.test/scalar.jpg", info!.ThumbnailUrl);
        }

        [Fact]
        public void BannerIsNotAlsoUsedAsPrimary()
        {
            // A source whose only image is a wide banner must not report the same URL twice: the
            // publisher decides whether to copy an image across slots, the parser does not.
            const string json = """
            {
              "_type": "playlist",
              "title": "x",
              "entries": [],
              "thumbnails": [ { "id": "0", "url": "https://example.test/banner.jpg", "width": 2560, "height": 424 } ]
            }
            """;

            var info = PlaylistInfo.ParseJson(json);

            Assert.NotNull(info);
            Assert.Equal("https://example.test/banner.jpg", info!.ThumbnailUrl);
            Assert.Null(info.BannerUrl);
        }
    }
}

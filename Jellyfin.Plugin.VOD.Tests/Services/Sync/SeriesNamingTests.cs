using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// The naming rule the sync and the configuration page must agree on. Two copies of it would
    /// drift, and the first sync after a save would silently rename the series.
    /// </summary>
    public sealed class SeriesNamingTests
    {
        [Fact]
        public void Channel_TakesTheChannelNameRatherThanTheTabTitle()
        {
            // yt-dlp names a channel's playlist after the tab it enumerated.
            var playlist = new PlaylistInfo
            {
                Title = "Le Fossoyeur de Films - Videos",
                ChannelName = "Le Fossoyeur de Films"
            };

            Assert.Equal(
                "Le Fossoyeur de Films",
                SeriesNaming.Resolve("https://www.youtube.com/@LeFossoyeurDeFilms/videos", playlist));
        }

        [Fact]
        public void Playlist_KeepsItsOwnTitleAlthoughItReportsAChannel()
        {
            // The trap: a playlist reports "channel" too, but it holds the OWNER. Using it would
            // rename "WORLDS 2025" to "OTP - VODs".
            var playlist = new PlaylistInfo
            {
                Title = "WORLDS 2025",
                ChannelName = "OTP - VODs"
            };

            Assert.Equal(
                "WORLDS 2025",
                SeriesNaming.Resolve("https://www.youtube.com/playlist?list=PLabc", playlist));
        }

        [Fact]
        public void Channel_FallsBackToTheTitleWhenNoChannelNameIsReported()
        {
            var playlist = new PlaylistInfo { Title = "Something - Videos" };

            Assert.Equal(
                "Something - Videos",
                SeriesNaming.Resolve("https://www.youtube.com/@something/videos", playlist));
        }

        [Fact]
        public void FallsBackToTheUrlWhenThePlatformReportsNoTitle()
        {
            const string Url = "https://www.arte.tv/fr/videos/RC-027708/happy-valley/";

            Assert.Equal(Url, SeriesNaming.Resolve(Url, new PlaylistInfo()));
        }

        [Fact]
        public void EmptyTitle_FallsBackToUrl()
        {
            // Platforms occasionally report an empty string rather than null for the title.
            var playlist = new PlaylistInfo { Title = "", ChannelName = "Channel" };
            Assert.Equal("https://example.com/list", SeriesNaming.Resolve("https://example.com/list", playlist));
        }

        [Fact]
        public void WhitespaceTitle_FallsBackToUrl()
        {
            var playlist = new PlaylistInfo { Title = "   ", ChannelName = null };
            Assert.Equal("https://example.com/list", SeriesNaming.Resolve("https://example.com/list", playlist));
        }

        [Fact]
        public void WatchPlusListUrl_KeepsPlaylistTitle()
        {
            // A watch?v=…&list=… URL is a playlist (not a channel) and must not take ChannelName.
            var playlist = new PlaylistInfo { Title = "My playlist", ChannelName = "Owner" };
            Assert.Equal("My playlist",
                SeriesNaming.Resolve("https://www.youtube.com/watch?v=abc&list=PLxxx", playlist));
        }

        [Theory]
        [InlineData("https://www.twitch.tv/otplol_/videos", "otplol_ - All Videos sorted by Date")]
        [InlineData("https://www.twitch.tv/otplol_", "otplol_ (live) 2026-10-08 09:25")]
        public void TwitchChannel_TakesTheHandleFromTheUrl(string url, string playlistTitle)
        {
            // Twitch reports no "channel" field on either form, measured 2026-10-08, so the playlist
            // title used to win: the tab's generic label for /videos, and for the root a title with
            // the sync's own time in it, which renamed the series on every pass. The handle in the
            // URL is the one name both forms agree on, and the one the probe already derives.
            var playlist = new PlaylistInfo { Title = playlistTitle, ChannelName = null };

            Assert.Equal("otplol_", SeriesNaming.Resolve(url, playlist));
        }
    }
}

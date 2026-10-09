namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>The kind of source URL, used to select the yt-dlp enumeration strategy.</summary>
    public enum SourceType
    {
        /// <summary>A YouTube channel URL (@handle, /channel/, /c/, /user/).</summary>
        YoutubeChannel,

        /// <summary>A YouTube playlist URL (/playlist?list=…) or a watch URL with a list= parameter.</summary>
        YoutubePlaylist,

        /// <summary>A single YouTube video (/watch?v=… without list, or youtu.be/…).</summary>
        YoutubeVideo,

        /// <summary>An Arte collection or series URL (/videos/RC-…/).</summary>
        ArtePlaylist,

        /// <summary>A single Arte video URL (/videos/{numeric-id}/).</summary>
        ArteVideo,

        /// <summary>A single france.tv video URL.</summary>
        FranceTvVideo,

        /// <summary>A Twitch channel root URL: live only, nothing published off air.</summary>
        TwitchChannelLive,

        /// <summary>A Twitch channel's /videos tab: the archive, plus the in-progress VOD while live.</summary>
        TwitchVideos,

        /// <summary>A single Twitch VOD URL (/videos/{numeric-id}).</summary>
        TwitchVideo,

        /// <summary>An unrecognised URL — treated as a single video fetch.</summary>
        Generic
    }
}

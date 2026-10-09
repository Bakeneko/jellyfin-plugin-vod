using System;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Decides what a source's series is called, from what the platform reported about it.
    /// </summary>
    /// <remarks>
    /// Shared by the sync path and by the configuration page's auto-fill, so the name a user is
    /// shown before saving is the name the sync will publish. One copy is the point: two would
    /// drift, and the first sync after a save would rename the series under the user.
    /// </remarks>
    internal static class SeriesNaming
    {
        /// <summary>
        /// Returns the series name for a source URL and the playlist yt-dlp returned for it.
        /// </summary>
        /// <param name="url">The configured source URL.</param>
        /// <param name="playlist">The playlist yt-dlp enumerated for that URL.</param>
        /// <returns>The name the series is published under.</returns>
        internal static string Resolve(string url, PlaylistInfo playlist)
        {
            ArgumentNullException.ThrowIfNull(playlist);

            // Twitch reports no "channel" field on either source form (measured 2026-10-08), so the
            // playlist title used to win: the /videos tab's generic "… - All Videos sorted by Date",
            // and for the channel root a title carrying the sync's own time, which renamed the series
            // on every pass. The handle in the URL is stable, and it is the name the live probe
            // already derives for a source that has never synced.
            if (TwitchUrls.ChannelFrom(url) is { } handle)
                return handle;

            // yt-dlp names a channel's playlist after the TAB rather than the channel — "Le
            // Fossoyeur de Films - Videos", "… - Live", "… - Shorts" — so a channel takes its
            // "channel" field instead. A playlist reports that field too, but there it holds the
            // playlist's OWNER, which would rename "WORLDS 2025" to "OTP - VODs"; hence the
            // channel-only test.
            if (VodNativeId.IsChannel(VodNativeId.ForSource(url))
                && !string.IsNullOrWhiteSpace(playlist.ChannelName))
            {
                return playlist.ChannelName;
            }

            return string.IsNullOrWhiteSpace(playlist.Title) ? url : playlist.Title;
        }
    }
}

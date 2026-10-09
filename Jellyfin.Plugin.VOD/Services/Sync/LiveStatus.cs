using System;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>Whether a video is broadcasting, scheduled, or neither.</summary>
    public enum LiveStatus
    {
        /// <summary>A normal video: finished, or never a broadcast.</summary>
        NotLive,

        /// <summary>Broadcasting now: playable, and published without a duration.</summary>
        Live,

        /// <summary>Scheduled: not playable yet.</summary>
        Upcoming
    }

    /// <summary>Reads <see cref="LiveStatus"/> out of yt-dlp's <c>live_status</c> field.</summary>
    /// <remarks>
    /// yt-dlp emits no <c>is_upcoming</c> boolean, whatever its documentation suggests: a scheduled
    /// stream is only identifiable through <c>live_status</c>. Measured 2026-10-03 (spec F14).
    /// </remarks>
    public static class LiveStatusParser
    {
        /// <summary>Maps a raw <c>live_status</c> value.</summary>
        /// <param name="liveStatus">The raw value, possibly null.</param>
        /// <returns>
        /// The matching status. Anything unrecognised is <see cref="LiveStatus.NotLive"/>, so a
        /// platform that reports nothing and a value a later yt-dlp invents both leave the video
        /// behaving as a normal one.
        /// </returns>
        public static LiveStatus FromYtDlp(string? liveStatus)
            => liveStatus?.Trim().ToLowerInvariant() switch
            {
                "is_live" => LiveStatus.Live,
                "is_upcoming" => LiveStatus.Upcoming,
                _ => LiveStatus.NotLive
            };
    }
}

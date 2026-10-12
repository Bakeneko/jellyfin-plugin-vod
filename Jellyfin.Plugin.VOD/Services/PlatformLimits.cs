using System;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Whether a failed yt-dlp call was the platform refusing, as opposed to something wrong with
    /// the video.
    /// </summary>
    /// <remarks>
    /// The distinction decides who waits: a platform's refusal backs off that platform's whole
    /// lane, while a video's own problem must cost only that video. Each platform signals a
    /// refusal in its own words, so the strings are matched per lane — a rule taken from one
    /// platform and applied to another is the mistake this plugin keeps paying for.
    /// </remarks>
    public static class PlatformLimits
    {
        /// <summary>
        /// YouTube's own answer, measured in production on 2026-10-12.
        /// </summary>
        private const string YoutubeRateLimited =
            "has been rate-limited by YouTube";

        /// <summary>Whether this failure should back the platform's lane off.</summary>
        /// <param name="lane">The lane the call was made on, from <see cref="PlatformLane.For"/>.</param>
        /// <param name="stderr">What yt-dlp wrote to stderr, if anything.</param>
        /// <param name="timedOut">Whether the process had to be killed for taking too long.</param>
        /// <returns>True when the platform refused the call.</returns>
        public static bool IsPlatformRefusal(string lane, string? stderr, bool timedOut)
        {
            // Platform-agnostic, and measured: a rate-limited session stops answering rather than
            // saying so, so every call simply hangs (F9).
            if (timedOut)
                return true;

            if (string.IsNullOrEmpty(stderr))
                return false;

            // Per platform from here down. Arte and Twitch have no measured refusal string, so
            // they get the prudent answer — nothing but a timeout backs them off — and measuring
            // them is a task of its own.
            return string.Equals(lane, "youtube", StringComparison.OrdinalIgnoreCase)
                && stderr.Contains(YoutubeRateLimited, StringComparison.OrdinalIgnoreCase);
        }
    }
}

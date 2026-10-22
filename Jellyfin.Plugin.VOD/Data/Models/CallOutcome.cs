namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>
    /// How a call to a platform ended. Recorded per video for the metadata fetch and the stream
    /// description, and per source for each stage of a pass.
    /// </summary>
    /// <remarks>
    /// This enumeration exists because the old model recorded the word <c>"Failed"</c> and nothing
    /// else. On 2026-10-19, fourteen of sixteen stuck videos on the test instance carried exactly
    /// that word, and finding out that the cause was one expired cookie session took a manual
    /// investigation through a database and 230 MB of logs. Every value below was reachable at that
    /// moment and would have named the cause on the configuration page.
    /// <para>
    /// A result always travels with a plain-text detail: the first line of stderr, or the
    /// <c>availability</c> value yt-dlp returned.
    /// </para>
    /// </remarks>
    public enum CallOutcome
    {
        /// <summary>Never attempted. The zero value, so an unwritten row reads as "no answer yet".</summary>
        None = 0,

        /// <summary>The call returned what was asked of it.</summary>
        Ok,

        /// <summary>
        /// The platform demands a signed-in session. Counted, because one such video on an instance
        /// that has a cookie file configured means the session has expired.
        /// </summary>
        NeedsAuth,

        /// <summary>The platform served the page but offered no playable format.</summary>
        NoFormats,

        /// <summary>
        /// The platform says the video is gone — private, deleted, or withdrawn. A result, not a
        /// state: nothing branches on it, and the 30-day clock retries it like anything else.
        /// </summary>
        Unavailable,

        /// <summary>Refused for this region.</summary>
        GeoBlocked,

        /// <summary>The platform asked us to slow down. Never counted against the video itself.</summary>
        RateLimited,

        /// <summary>The process had to be killed for taking too long.</summary>
        Timeout,

        /// <summary>yt-dlp's extractor failed in a way none of the above names.</summary>
        ExtractorError,

        /// <summary>The answer arrived but could not be read as the JSON we expect.</summary>
        ParseError,

        /// <summary>The call never reached the platform.</summary>
        NetworkError,

        /// <summary>The stream probe failed, or found no stream to describe.</summary>
        ProbeFailed,
    }
}

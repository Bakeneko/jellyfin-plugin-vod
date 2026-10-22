using System;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// The backoff calculation for the wave selection — the C# mirror of the SQL backoff
    /// filter in <see cref="IVodDatabase.GetDueVideos"/>.
    /// </summary>
    /// <remarks>
    /// Both this class and <see cref="IVodDatabase.GetDueVideos"/> express the same doubling
    /// rule. The calculation lives here (in C#, testable without a database) and in SQL
    /// (applied per-row inside the query). The concordance test in
    /// <c>WaveSelectorTests.IsInBackoff_SqlConcordance_AgreesWithDatabase</c> checks that the
    /// two expressions cannot drift: if one is updated without the other, the test fails.
    /// </remarks>
    public static class WaveSelector
    {
        private static readonly TimeSpan BackoffCap = TimeSpan.FromDays(7);

        /// <summary>
        /// Returns how long to wait before retrying after <paramref name="consecutiveFailures"/>
        /// consecutive failures.
        /// </summary>
        /// <param name="consecutiveFailures">
        /// How many consecutive failures the video has accumulated. Zero or less means no backoff.
        /// </param>
        /// <param name="retryMinutes">
        /// The base retry interval in minutes.  Zero or less is treated as zero (no backoff),
        /// which prevents a negative value from returning a negative duration that the wave would
        /// silently interpret as "eligible immediately".
        /// </param>
        /// <returns>
        /// <see cref="TimeSpan.Zero"/> for zero or fewer failures or a non-positive base;
        /// otherwise <c>retryMinutes × 2^(failures − 1)</c>, capped at seven days.
        /// </returns>
        /// <remarks>
        /// The cap is enforced via <see cref="Math.Min(double,double)"/> on a
        /// <see langword="double"/>, not on an integer. An integer left-shift or multiply by
        /// 2^2000 overflows silently into a negative or zero result. The double saturates to
        /// +Infinity instead, and <c>Math.Min(+Infinity, cap)</c> returns cap.
        /// The SQL mirror uses <c>MIN(POWER(2.0, n), …)</c> for the same reason — see
        /// <see cref="IVodDatabase.GetDueVideos"/>.
        /// </remarks>
        public static TimeSpan BackoffFor(int consecutiveFailures, int retryMinutes)
        {
            if (consecutiveFailures <= 0 || retryMinutes <= 0)
                return TimeSpan.Zero;

            var capMinutes = BackoffCap.TotalMinutes;
            var minutes = Math.Min(
                (double)retryMinutes * Math.Pow(2.0, consecutiveFailures - 1),
                capMinutes);

            return TimeSpan.FromMinutes(minutes);
        }

        /// <summary>
        /// Returns whether <paramref name="video"/> is currently serving a backoff and therefore
        /// must not be included in the next wave.
        /// </summary>
        /// <param name="video">The video to evaluate.</param>
        /// <param name="retryMinutes">
        /// The base retry interval in minutes, matching the source's configuration.
        /// </param>
        /// <param name="now">The reference time for the comparison.</param>
        /// <returns>
        /// <see langword="true"/> when the video's last attempt is recent enough that the
        /// computed backoff has not yet elapsed.
        /// </returns>
        /// <remarks>
        /// Only the metadata pair (<see cref="VideoRecord.MetadataAttempts"/> /
        /// <see cref="VideoRecord.MetadataAt"/>) drives the backoff, for an architectural reason:
        /// describing a video requires the stream-manifest URLs that the metadata step fetches;
        /// those URLs are not stored on the row.  A describe retry is therefore always a metadata
        /// retry — there is no standalone describe-only retry path, and
        /// <see cref="VideoRecord.DescribeAttempts"/> cannot drive a backoff.  When a description
        /// fails, <see cref="IVodDatabase.SetDescribeOutcome"/> stamps <c>described_at</c>,
        /// removing the video from condition 3; it waits for the metadata clock to go stale, then
        /// the next wave re-fetches the metadata and re-describes in the same breath.
        /// <para>
        /// Using the metadata pair unconditionally also avoids requiring
        /// <c>metadataMaxAgeDays</c> as a parameter, which would complicate the concordance test
        /// without adding coverage — see
        /// <c>WaveSelectorTests.IsInBackoff_SqlConcordance_AgreesWithDatabase</c>.
        /// </para>
        /// </remarks>
        public static bool IsInBackoff(VideoRecord video, int retryMinutes, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(video);

            // A null clock means the video has never been attempted: first attempt, no backoff.
            // A zero counter means the last attempt succeeded and the counter was reset.
            if (video.MetadataAttempts <= 0 || video.MetadataAt is null)
                return false;

            return video.MetadataAt.Value + BackoffFor(video.MetadataAttempts, retryMinutes) > now;
        }
    }
}

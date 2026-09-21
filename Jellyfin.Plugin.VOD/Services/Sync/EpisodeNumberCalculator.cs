using System;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Assigns deterministic season/episode numbers to synced videos.
    /// </summary>
    /// <remarks>
    /// Season  = calendar year of the publication date.
    /// Episode = (Month * 100 + Day) * 1000 + rankOfDay (1-based).
    /// The formula produces a sortable 6–7 digit integer that encodes the date and
    /// the intra-day ordering. Maximum rank is 999; videos beyond that are not published.
    /// Once assigned and stored in the database, numbers are never recomputed.
    /// </remarks>
    public static class EpisodeNumberCalculator
    {
        /// <summary>
        /// Computes the (season, episode) pair for a video published on <paramref name="publishedAt"/>
        /// at rank <paramref name="rankOfDay"/> (1-based, within its source on that day).
        /// Returns <see langword="null"/> when <paramref name="rankOfDay"/> is outside [1, 999].
        /// </summary>
        public static (int season, int episode)? TryCalculate(DateTimeOffset publishedAt, int rankOfDay)
        {
            if (rankOfDay < 1 || rankOfDay > 999)
                return null;

            var season = publishedAt.Year;
            var episode = (publishedAt.Month * 100 + publishedAt.Day) * 1000 + rankOfDay;
            return (season, episode);
        }
    }
}

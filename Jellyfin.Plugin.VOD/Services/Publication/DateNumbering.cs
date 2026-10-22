using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// Gives every publishable video its season and episode, recomputed from scratch at each
    /// checkup.
    /// </summary>
    /// <remarks>
    /// Season is the year of publication, episode is <c>(month * 100 + day) * 1000 + rank</c>, and
    /// the rank is the video's place among those published the same day, by time ascending, from 1.
    /// <para>
    /// A NUMBER IS NO LONGER FROZEN. Recomputing it is what dissolved the "settled source" rule and
    /// everything built to hold it: a pass no longer has to wait for a source to be complete before
    /// it may number anything. The accepted cost is that an older video of the same day entering
    /// the window redistributes that day's ranks, and Jellyfin keys a virtual item's watch state on
    /// season and episode — so such a change TRANSFERS watch state between two videos of one day.
    /// Measured on the user's sources, several videos in one day is the exception: a peak of 9, a
    /// median of 1 to 4.
    /// </para>
    /// <para>
    /// A number the platform supplies — announced in the metadata, or parseable from the title — is
    /// kept as it stands and takes no part in the ranking, otherwise it would shift its
    /// same-day neighbours.
    /// </para>
    /// </remarks>
    public static class DateNumbering
    {
        /// <summary>The numbers to write for a source's publishable videos.</summary>
        /// <param name="publishable">
        /// The source's publishable rows. Only these are ranked: a row the retention filters out
        /// must not occupy a rank, or removing it would renumber the day.
        /// </param>
        /// <returns>
        /// The number for each row, keyed by <see cref="VideoRecord.Id"/>. A row with no date and
        /// no supplied number is absent from the result, which means "leave it unnumbered" — such a
        /// row is filed under Specials, which is correct and visible, rather than given a wrong
        /// number.
        /// </returns>
        public static IReadOnlyDictionary<string, (int Season, int Episode)> Assign(
            IReadOnlyList<VideoRecord> publishable)
        {
            ArgumentNullException.ThrowIfNull(publishable);

            var numbers = new Dictionary<string, (int Season, int Episode)>(StringComparer.Ordinal);
            var dateBased = new List<VideoRecord>();

            foreach (var video in publishable)
            {
                if (Supplied(video) is { } supplied)
                {
                    numbers[video.Id] = supplied;
                }
                else
                {
                    dateBased.Add(video);
                }
            }

            // Ranks are recomputed from the day's whole membership, so the order has to be total:
            // the id breaks a tie between two videos sharing a publication instant, and without it
            // two identical checkups could hand out different numbers.
            var byDay = dateBased
                .Where(v => v.PublishedAt.HasValue)
                .OrderBy(v => v.PublishedAt!.Value)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
                .GroupBy(v => DateOnly.FromDateTime(v.PublishedAt!.Value.UtcDateTime.Date));

            foreach (var day in byDay)
            {
                var rank = 0;
                foreach (var video in day)
                {
                    rank++;

                    // The calculator rejects a rank outside [1,999]; that needs 1000 videos
                    // published on one day. Such a row stays unnumbered rather than taking a number
                    // that encodes another day.
                    if (EpisodeNumberCalculator.TryCalculate(video.PublishedAt!.Value, rank) is { } assignment)
                        numbers[video.Id] = assignment;
                }
            }

            return numbers;
        }

        /// <summary>
        /// The number the platform supplied for this video, or null when it must be computed.
        /// </summary>
        /// <param name="video">The row.</param>
        /// <returns>The supplied number, or null.</returns>
        /// <remarks>
        /// Two sources, in order. The title is parsed first, because a title that only became
        /// parseable now is a better answer than a number computed earlier. Otherwise a number
        /// already stored counts as supplied when it does NOT decode as a date-based number: the
        /// wave writes a metadata-announced number straight onto the row, and by checkup time the
        /// metadata answer is gone, so the shape of the number is the only evidence left of where
        /// it came from. This is why <see cref="EncodedDay"/> exists.
        /// </remarks>
        internal static (int Season, int Episode)? Supplied(VideoRecord video)
        {
            if (TitleSeasonEpisodeParser.TryParse(video.Title ?? string.Empty, out var season, out var episode))
                return (season, episode);

            if (video.SeasonNumber is { } storedSeason
                && video.EpisodeNumber is { } storedEpisode
                && EncodedDay(storedSeason, storedEpisode) is null)
            {
                return (storedSeason, storedEpisode);
            }

            return null;
        }

        /// <summary>
        /// The calendar day a date-based number encodes, or null when the number has another shape.
        /// </summary>
        /// <param name="season">The stored season: the year, for a date-based number.</param>
        /// <param name="episode">The stored episode: <c>(month * 100 + day) * 1000 + rank</c>.</param>
        /// <returns>The encoded day, or null.</returns>
        /// <remarks>
        /// The bounds are the calculator's own: a year from 1 to 9999, a real calendar day in that
        /// year, and a rank from 1 to 999. A small platform season (S01E05) decodes to no day,
        /// which is exactly how it is recognised as supplied rather than computed.
        /// </remarks>
        internal static DateOnly? EncodedDay(int season, int episode)
        {
            if (season is < 1 or > 9999 || episode < 0)
                return null;

            var rank = episode % 1000;
            var monthDay = episode / 1000;
            var month = monthDay / 100;
            var day = monthDay % 100;

            if (rank is < 1 or > 999 || month is < 1 or > 12)
                return null;

            if (day < 1 || day > DateTime.DaysInMonth(season, month))
                return null;

            return new DateOnly(season, month, day);
        }
    }
}

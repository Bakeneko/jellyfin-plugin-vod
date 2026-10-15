using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// The per-day tally <see cref="SyncService.NextDateBasedNumber"/> continues from: the highest
    /// date-based rank a day already uses, not how many of its rows are numbered.
    /// </summary>
    public sealed class SyncServiceDayTallyTests
    {
        private static readonly DateTimeOffset Day = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        private static VideoRecord Row(string id, DateTimeOffset publishedAt, int? season, int? episode)
            => new()
            {
                Id = id,
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v/" + id,
                NativeId = id,
                SyncedAt = DateTimeOffset.UnixEpoch,
                ResolutionState = ResolutionState.Resolved,
                PublishedAt = publishedAt,
                SeasonNumber = season,
                EpisodeNumber = episode,
            };

        [Fact]
        public void NextNumber_FollowsTheHighestRank_WhenAnEarlierRankOfTheDayIsGone()
        {
            // Rank 2 was deleted (an orphan cleanup, say): counting the two rows left would hand
            // out rank 3 again, a duplicate (season, episode) and a shared watch state.
            var tally = SyncService.NumberedPerDay(
            [
                Row("a", Day.AddHours(8), 2026, 1013001),
                Row("c", Day.AddHours(18), 2026, 1013003),
            ]);

            var next = SyncService.NextDateBasedNumber(tally, Day.AddHours(21));

            Assert.Equal((2026, 1013004), next);
        }

        [Fact]
        public void NextNumber_IgnoresARowNumberedByItsTitle()
        {
            // S01E05 does not encode the day: it holds no rank of it, so it must not push the
            // date-based numbering past rank 2.
            var tally = SyncService.NumberedPerDay(
            [
                Row("a", Day.AddHours(8), 2026, 1013001),
                Row("t", Day.AddHours(10), 1, 5),
            ]);

            var next = SyncService.NextDateBasedNumber(tally, Day.AddHours(21));

            Assert.Equal((2026, 1013002), next);
        }

        [Fact]
        public void NextNumber_IgnoresADateNumberOfAnotherDay()
        {
            // A row whose number encodes another day (renumbered by hand, or a platform number
            // that happens to look like one) holds no rank of this day.
            var tally = SyncService.NumberedPerDay(
            [
                Row("x", Day.AddHours(8), 2026, 1012007),
            ]);

            var next = SyncService.NextDateBasedNumber(tally, Day.AddHours(21));

            Assert.Equal((2026, 1013001), next);
        }

        [Fact]
        public void NextNumber_StartsAtRankOne_OnAnEmptyDay()
        {
            var tally = SyncService.NumberedPerDay(new List<VideoRecord>());

            Assert.Equal((2026, 1013001), SyncService.NextDateBasedNumber(tally, Day.AddHours(9)));
        }
        [Fact]
        public void NextNumber_CountsARankInTheDayItsNumberEncodes_AfterItsDateMoved()
        {
            // O1: 1013002 was given when the row was dated 2026-10-13; its date later moved to
            // 2026-10-14 (an archive's upload date, a rescheduled premiere) but the number never
            // moves. It still holds rank 2 of the 13th, and holds none of the 14th.
            var tally = SyncService.NumberedPerDay(
            [
                Row("moved", Day.AddDays(1).AddHours(2), 2026, 1013002),
            ]);

            Assert.Equal((2026, 1013003), SyncService.NextDateBasedNumber(tally, Day.AddHours(21)));
            Assert.Equal((2026, 1014001), SyncService.NextDateBasedNumber(tally, Day.AddDays(1).AddHours(9)));
        }

        [Fact]
        public void NextNumber_IgnoresANumberWhoseEncodedDateIsNotACalendarDay()
        {
            // O1: month 13 is no day at all, so S2026E1310001 holds no rank anywhere.
            var tally = SyncService.NumberedPerDay(
            [
                Row("bogus", Day.AddHours(8), 2026, 1310001),
            ]);

            Assert.Empty(tally);
            Assert.Equal((2026, 1013001), SyncService.NextDateBasedNumber(tally, Day.AddHours(21)));
        }

        [Fact]
        public void NextNumber_CountsADateNumber_OnARowWithoutAPublicationDate()
        {
            // O1: the number alone says which day it occupies; a row that lost its date still
            // holds its rank.
            var row = Row("undated", Day, 2026, 1013004);
            row.PublishedAt = null;

            var tally = SyncService.NumberedPerDay([row]);

            Assert.Equal((2026, 1013005), SyncService.NextDateBasedNumber(tally, Day.AddHours(21)));
        }
    }
}

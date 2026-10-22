using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Publication;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// Numbering, recomputed from scratch at every checkup. This is the piece whose change
    /// dissolved the "settled source" rule, so the test that matters most is the one showing a rank
    /// MOVING — the behaviour the old design existed to prevent and this one accepts.
    /// </summary>
    public sealed class DateNumberingTests
    {
        private static VideoRecord Video(
            string id, DateTimeOffset? publishedAt, string? title = null,
            int? season = null, int? episode = null)
            => new()
            {
                Id = id,
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/" + id,
                Title = title ?? "Episode " + id,
                PublishedAt = publishedAt,
                SeasonNumber = season,
                EpisodeNumber = episode,
            };

        private static DateTimeOffset Day(int year, int month, int day, int hour = 0)
            => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

        [Fact]
        public void SeasonIsTheYear_AndEpisodeEncodesTheDayAndTheRank()
        {
            var numbers = DateNumbering.Assign([Video("a", Day(2026, 10, 14, 9))]);

            Assert.Equal((2026, 1014001), numbers["a"]);
        }

        [Fact]
        public void TwoVideosOfOneDay_AreRankedByPublicationTime()
        {
            var numbers = DateNumbering.Assign(
            [
                Video("late", Day(2026, 10, 14, 18)),
                Video("early", Day(2026, 10, 14, 6)),
            ]);

            Assert.Equal((2026, 1014001), numbers["early"]);
            Assert.Equal((2026, 1014002), numbers["late"]);
        }

        [Fact]
        public void AnOlderVideoEnteringTheWindow_RedistributesThatDaysRanks()
        {
            // THE accepted cost of the rework. Jellyfin keys a virtual item's watch state on its
            // season and episode, so this transfers watch state between two videos of one day.
            // Measured on the user's sources, several videos in a day is the exception: a peak of
            // 9, a median of 1 to 4 — judged a fair price for dissolving the settled rule, which
            // let one pathological video hold 300 others out of the library for up to seven days.
            var first = Video("first", Day(2026, 10, 14, 18), season: 2026, episode: 1014001);

            var before = DateNumbering.Assign([first]);
            Assert.Equal((2026, 1014001), before["first"]);

            var after = DateNumbering.Assign([first, Video("older", Day(2026, 10, 14, 6))]);

            Assert.Equal((2026, 1014001), after["older"]);
            Assert.Equal((2026, 1014002), after["first"]);
        }

        [Fact]
        public void DifferentDays_EachStartAtRankOne()
        {
            var numbers = DateNumbering.Assign(
            [
                Video("a", Day(2026, 10, 14, 9)),
                Video("b", Day(2026, 10, 15, 9)),
            ]);

            Assert.Equal((2026, 1014001), numbers["a"]);
            Assert.Equal((2026, 1015001), numbers["b"]);
        }

        [Fact]
        public void DaysAreUtc_NotLocal()
        {
            // Two videos an hour apart across a UTC midnight belong to different days. Ranking on
            // local time would make the numbering depend on the server's timezone, so the same
            // library would number differently after a move.
            var numbers = DateNumbering.Assign(
            [
                Video("before", new DateTimeOffset(2026, 10, 14, 23, 30, 0, TimeSpan.Zero)),
                Video("after", new DateTimeOffset(2026, 10, 15, 0, 30, 0, TimeSpan.Zero)),
            ]);

            Assert.Equal((2026, 1014001), numbers["before"]);
            Assert.Equal((2026, 1015001), numbers["after"]);
        }

        [Fact]
        public void AVideoWithNoDate_GetsNoNumber()
        {
            // Absent from the result rather than given a guess: it is filed under Specials, which
            // is visible and correctable, where a wrong number silently steals another video's
            // watch state.
            var numbers = DateNumbering.Assign([Video("undated", null)]);

            Assert.DoesNotContain("undated", numbers.Keys);
        }

        [Theory]
        [InlineData("Les idées larges - Saison 3 (7/12)")]
        [InlineData("Die grossen Ideen - Staffel 3 (7/12)")]
        public void ATitleAnnouncedNumber_IsKept(string title)
        {
            // The parser knows Arte's shape — "Saison N (E/Total)" and its language variants — and
            // nothing else. It does not read "S03E07": checked against the production code, which
            // is where this test started as a wrong assumption.
            var numbers = DateNumbering.Assign([Video("a", Day(2026, 10, 14), title: title)]);

            Assert.Equal((3, 7), numbers["a"]);
        }

        [Fact]
        public void ASuppliedNumber_DoesNotShiftItsSameDayNeighbours()
        {
            // It takes no part in the ranking. Counting it would push the computed videos of that
            // day one rank further for no reason, and the shift would survive every later checkup.
            var numbers = DateNumbering.Assign(
            [
                Video("announced", Day(2026, 10, 14, 6), title: "Les idées larges - Saison 3 (7/12)"),
                Video("computed", Day(2026, 10, 14, 12)),
            ]);

            Assert.Equal((3, 7), numbers["announced"]);
            Assert.Equal((2026, 1014001), numbers["computed"]);
        }

        [Fact]
        public void APlatformNumberAlreadyOnTheRow_IsRecognisedByItsShape()
        {
            // The wave writes a metadata-announced number straight onto the row, and by checkup time
            // the metadata answer is gone — so the shape of the number is the only evidence left of
            // where it came from. S01E05 decodes to no calendar day, which is how it is told apart
            // from a number this code computed.
            var numbers = DateNumbering.Assign([Video("a", Day(2026, 10, 14), season: 1, episode: 5)]);

            Assert.Equal((1, 5), numbers["a"]);
        }

        [Fact]
        public void ADateBasedNumberAlreadyOnTheRow_IsRecomputed_NotKept()
        {
            // The counterpart of the test above, and what makes renumbering possible at all: a
            // number this code produced decodes back to its day, so it is not mistaken for a
            // platform's and is free to move.
            var stale = Video("a", Day(2026, 10, 14, 6), season: 2026, episode: 1014009);

            var numbers = DateNumbering.Assign([stale]);

            Assert.Equal((2026, 1014001), numbers["a"]);
        }

        [Fact]
        public void TheOrderIsTotal_SoTwoIdenticalRunsAgree()
        {
            // Two videos sharing a publication instant. Without the id as a tiebreaker the ranks
            // would depend on enumeration order, and two checkups could hand out different numbers
            // for the same library.
            var instant = Day(2026, 10, 14, 9);
            var videos = new List<VideoRecord> { Video("bbb", instant), Video("aaa", instant) };

            var first = DateNumbering.Assign(videos);
            var second = DateNumbering.Assign(videos.AsEnumerable().Reverse().ToList());

            Assert.Equal(first["aaa"], second["aaa"]);
            Assert.Equal(first["bbb"], second["bbb"]);
            Assert.Equal((2026, 1014001), first["aaa"]);
        }

        [Theory]
        [InlineData(2026, 1014001, 2026, 10, 14)]
        [InlineData(2026, 1231999, 2026, 12, 31)]
        public void EncodedDay_ReadsBackADateBasedNumber(
            int season, int episode, int year, int month, int day)
            => Assert.Equal(new DateOnly(year, month, day), DateNumbering.EncodedDay(season, episode));

        [Theory]
        [InlineData(1, 5)]            // S01E05: a platform number
        [InlineData(2026, 12)]        // too small to carry a month
        [InlineData(2026, 1014000)]   // rank 0 is outside the calculator's [1,999]
        [InlineData(2026, 1332001)]   // month 13
        [InlineData(2026, 229001)]    // 29 February 2026, which is not a real day
        [InlineData(0, 1014001)]      // year 0
        [InlineData(2026, -1)]
        public void EncodedDay_RefusesAnythingThatIsNotOne(int season, int episode)
            => Assert.Null(DateNumbering.EncodedDay(season, episode));

        [Fact]
        public void EncodedDay_AcceptsALeapDay()
            => Assert.Equal(new DateOnly(2024, 2, 29), DateNumbering.EncodedDay(2024, 229001));
    }
}

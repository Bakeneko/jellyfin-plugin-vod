using System;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The one place a yt-dlp answer becomes a row. Shared by the wave and the live probe, so a
    /// field it forgets is a field missing from both.
    /// </summary>
    public sealed class MetadataFillTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        private const string SourceUrl = "https://www.youtube.com/@chan/videos";

        private static VideoRecord Row() => new()
        {
            Id = "v",
            SourceUrl = SourceUrl,
            VideoUrl = "https://www.youtube.com/watch?v=v",
        };

        [Fact]
        public void Apply_CarriesEveryPublishedField()
        {
            var row = Row();
            MetadataFill.Apply(
                row,
                new VideoMetadata
                {
                    Id = "v",
                    Title = "Original title",
                    Description = "A synopsis",
                    Duration = 1169.4,
                    UploadDate = "20261014",
                    Qualities = [new VideoQuality(1080, "h264", 30)],
                    AudioTracks = [new AudioTrack("fr", "français")],
                    SubtitleLanguages = ["fr"],
                    Tags = ["science"],
                    Categories = ["Education"],
                    Channels = ["A channel"],
                },
                SourceUrl,
                publishedTitle: "Titre localisé",
                Now);

            Assert.Equal("Titre localisé", row.Title);
            Assert.Equal("A synopsis", row.Description);
            Assert.Equal(1169, row.DurationSeconds);
            Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), row.PublishedAt);
            Assert.Equal(1080, Assert.Single(row.Extras.Qualities).Height);
            Assert.Equal("fr", Assert.Single(row.Extras.AudioTracks).Language);
            Assert.Equal("fr", Assert.Single(row.Extras.SubtitleLanguages));
            Assert.Equal("science", Assert.Single(row.Extras.Tags));
            Assert.Equal("Education", Assert.Single(row.Extras.Categories));
            Assert.Equal("A channel", Assert.Single(row.Extras.Channels));
        }

        [Fact]
        public void Apply_PublishesTheTitleTheCallerChose_NotTheMetadataTitle()
        {
            // The caller resolves the published title, because only it knows what the LISTING said.
            // A listing serves the localized title and obeys youtube:lang; a metadata call always
            // serves the original and ignores it.
            var row = Row();

            MetadataFill.Apply(
                row, new VideoMetadata { Id = "v", Title = "Original" }, SourceUrl, "Localisé", Now);

            Assert.Equal("Localisé", row.Title);
        }

        [Fact]
        public void Apply_NeverTouchesTheListingTitle()
        {
            // ListingTitle exists only to be compared against the next listing. Writing a metadata
            // title into it would make the retitle detector fire on every single pass, because the
            // two paths do not serve the same language.
            var row = Row();
            row.ListingTitle = "What the listing said";

            MetadataFill.Apply(
                row, new VideoMetadata { Id = "v", Title = "Original" }, SourceUrl, "Localisé", Now);

            Assert.Equal("What the listing said", row.ListingTitle);
        }

        [Fact]
        public void Apply_NeverComputesANumber()
        {
            // Numbering belongs to the checkup, which sees a whole day at once. A number computed
            // here would be computed from one row in isolation.
            var row = Row();

            MetadataFill.Apply(
                row,
                new VideoMetadata { Id = "v", Title = "Original", UploadDate = "20261014" },
                SourceUrl, "Original", Now);

            Assert.Null(row.SeasonNumber);
            Assert.Null(row.EpisodeNumber);
        }

        [Fact]
        public void Apply_WritesAPlatformAnnouncedNumber()
        {
            // The one number written here, because the metadata answer is the only place it exists
            // and it is gone by the time the checkup runs.
            var row = Row();

            MetadataFill.Apply(
                row,
                new VideoMetadata { Id = "v", Title = "Original", SeasonNumber = 3, EpisodeNumber = 7 },
                SourceUrl, "Original", Now);

            Assert.Equal(3, row.SeasonNumber);
            Assert.Equal(7, row.EpisodeNumber);
        }

        [Fact]
        public void Apply_IgnoresHalfAnAnnouncedNumber()
        {
            // A season without an episode is not a number. Storing it would make the checkup read
            // the row as platform-numbered — its season decodes to no day — and leave it there for
            // ever with no episode at all.
            var row = Row();

            MetadataFill.Apply(
                row,
                new VideoMetadata { Id = "v", Title = "Original", SeasonNumber = 3 },
                SourceUrl, "Original", Now);

            Assert.Null(row.SeasonNumber);
            Assert.Null(row.EpisodeNumber);
        }

        [Fact]
        public void Apply_RewritesAScheduledStreamsDate_BecauseASchedulesMoves()
        {
            var scheduled = new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.Zero);
            var row = Row();
            row.PublishedAt = new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.Zero);

            MetadataFill.Apply(
                row,
                new VideoMetadata
                {
                    Id = "v",
                    Title = "Premiere",
                    LiveStatus = LiveStatus.Upcoming,
                    ReleaseTimestamp = scheduled.ToUnixTimeSeconds(),
                },
                SourceUrl, "Premiere", Now);

            Assert.Equal(scheduled, row.PublishedAt);
        }

        [Fact]
        public void Apply_KeepsAStoredDate_ForAnOrdinaryVideo()
        {
            // A published date that moved would rewrite the item and move it in every date sort.
            var stored = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var row = Row();
            row.PublishedAt = stored;

            MetadataFill.Apply(
                row,
                new VideoMetadata { Id = "v", Title = "Original", UploadDate = "20261014" },
                SourceUrl, "Original", Now);

            Assert.Equal(stored, row.PublishedAt);
        }

        [Fact]
        public void Apply_FallsBackToNow_WhenNothingCarriesADate()
        {
            var row = Row();

            MetadataFill.Apply(row, new VideoMetadata { Id = "v", Title = "Original" }, SourceUrl, "Original", Now);

            Assert.Equal(Now, row.PublishedAt);
        }

        [Fact]
        public void ParseDate_PrefersTheUploadDate()
            => Assert.Equal(
                new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero),
                MetadataFill.ParseDate(new VideoMetadata
                {
                    Id = "v",
                    UploadDate = "20261014",
                    Timestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                }));

        [Fact]
        public void ParseDate_FallsBackToTheReleaseTimestamp_ThenTheTimestamp()
        {
            var release = new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.Zero);
            var stamp = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

            Assert.Equal(release, MetadataFill.ParseDate(new VideoMetadata
            {
                Id = "v",
                ReleaseTimestamp = release.ToUnixTimeSeconds(),
                Timestamp = stamp.ToUnixTimeSeconds(),
            }));

            Assert.Equal(stamp, MetadataFill.ParseDate(new VideoMetadata
            {
                Id = "v",
                Timestamp = stamp.ToUnixTimeSeconds(),
            }));
        }

        [Theory]
        [InlineData("2026101")]     // too short
        [InlineData("20261344")]    // month 13, day 44
        [InlineData("notadate")]
        public void ParseDate_SurvivesAnUnusableUploadDate(string uploadDate)
            => Assert.Null(MetadataFill.ParseDate(new VideoMetadata { Id = "v", UploadDate = uploadDate }));
    }
}

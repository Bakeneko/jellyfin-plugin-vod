using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Publication;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The predicate that replaced the state column. Its three conditions are the whole definition
    /// of "may be published", so each one is pinned alone: a condition that silently stopped
    /// refusing would make videos appear that the administrator had retired, and one that started
    /// refusing wrongly would empty a library with no error anywhere.
    /// </summary>
    public sealed class PublishabilityTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        private static VideoRecord Fetched() => new()
        {
            Id = "v",
            SourceUrl = "https://example.test/s",
            VideoUrl = "https://example.test/v",
            MetadataAt = Now.AddHours(-1),
            MetadataResult = CallOutcome.Ok,
            PublishedAt = Now.AddDays(-2),
            Extras = new VideoExtras { Qualities = [new VideoQuality(1080, "h264", 30)] },
        };

        [Fact]
        public void AFetchedVideoWithATier_InsideRetention_IsPublishable()
        {
            Assert.True(Publishability.IsPublishable(Fetched(), retentionDays: 30, Now));
            Assert.Equal(PublishRefusal.None, Publishability.Refusal(Fetched(), 30, Now));
        }

        [Fact]
        public void AVideoNeverFetched_IsRefused()
        {
            var video = Fetched();
            video.MetadataAt = null;

            Assert.Equal(PublishRefusal.NeverFetched, Publishability.Refusal(video, 30, Now));
        }

        [Theory]
        [InlineData(CallOutcome.Unavailable)]
        [InlineData(CallOutcome.NeedsAuth)]
        [InlineData(CallOutcome.GeoBlocked)]
        [InlineData(CallOutcome.NoFormats)]
        public void AVideoThePlatformRefused_IsRefused(CallOutcome outcome)
        {
            // These four are the platform saying no: gone, private, age-restricted, geo-blocked,
            // or offering no format. Unavailable among them is the point of making it a RESULT
            // rather than a state — it refuses publication like any other platform refusal, and
            // the freshness clock retries it like any other row, with no branch of its own.
            var video = Fetched();
            video.MetadataResult = outcome;

            Assert.Equal(PublishRefusal.PlatformRefused, Publishability.Refusal(video, 30, Now));
        }

        [Theory]
        [InlineData(CallOutcome.Timeout)]
        [InlineData(CallOutcome.RateLimited)]
        [InlineData(CallOutcome.NetworkError)]
        [InlineData(CallOutcome.ExtractorError)]
        [InlineData(CallOutcome.ParseError)]
        [InlineData(CallOutcome.None)]
        public void AVideoWeMerelyFailedToReach_StaysPublished(CallOutcome outcome)
        {
            // A video does not stop existing because our request timed out, and timeouts are
            // expected — the whole RateLimited/Timeout machinery exists because they happen.
            //
            // This is the test that protects watch history. Unpublishing deletes the Jellyfin item,
            // and the item carries the watch state. Under the naive rule "any outcome but Ok
            // unpublishes", one transient failure during the thirty-day refresh destroyed the watch
            // state of a video that was perfectly fine: at roughly 250 refetches a day, a
            // one-percent transient rate costs two or three videos a day.
            //
            // None is here too: it is what an outcome written by a newer build reads back as, and
            // refusing an unreadable value would unpublish a whole library on an upgrade.
            var video = Fetched();
            video.MetadataResult = outcome;

            Assert.True(Publishability.IsPublishable(video, 30, Now));
        }

        [Theory]
        [InlineData(CallOutcome.Timeout)]
        [InlineData(CallOutcome.NetworkError)]
        public void AVideoWeFailedToReach_AndNeverSucceededFor_IsStillRefused(CallOutcome outcome)
        {
            // What makes tolerating a transport failure safe: a row whose very FIRST fetch failed
            // holds no qualities, so the tier check refuses it anyway. The tolerance only ever
            // keeps a row that already carries a good answer.
            var video = Fetched();
            video.MetadataResult = outcome;
            video.Extras = new VideoExtras { Qualities = [] };

            Assert.Equal(PublishRefusal.NoOfferedTier, Publishability.Refusal(video, 30, Now));
        }

        [Fact]
        public void AVideoWithStaleQualitiesThePlatformRefused_IsStillRefused()
        {
            // A row that was published once and later became unavailable keeps the tiers of its
            // last good answer. Judging it on the tiers alone would keep an unplayable video in
            // the library for ever.
            var video = Fetched();
            video.MetadataResult = CallOutcome.Unavailable;
            video.MetadataDetail = "ERROR: Video unavailable";

            Assert.False(Publishability.IsPublishable(video, 30, Now));
        }

        [Fact]
        public void AVideoWithNoOfferedTier_IsRefused()
        {
            // A real case, not a theoretical one: a scheduled stream has no format at all yet.
            var video = Fetched();
            video.Extras = new VideoExtras { Qualities = [] };

            Assert.Equal(PublishRefusal.NoOfferedTier, Publishability.Refusal(video, 30, Now));
        }

        [Fact]
        public void AVideoOlderThanRetention_IsRefused()
        {
            var video = Fetched();
            video.PublishedAt = Now.AddDays(-40);

            Assert.Equal(PublishRefusal.OutsideRetention, Publishability.Refusal(video, 30, Now));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void RetentionOfZeroOrLess_MeansUnlimited(int retentionDays)
        {
            // Zero is the default. Reading it as "nothing passes" would publish an empty library on
            // a fresh install, which is the kind of failure nobody thinks to look for.
            var video = Fetched();
            video.PublishedAt = new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);

            Assert.True(Publishability.IsPublishable(video, retentionDays, Now));
        }

        [Fact]
        public void AVideoWithNoDate_IsNotRefusedByRetention()
        {
            // Retention can only exclude what it can date. A dateless row passes this gate and is
            // filed under Specials, which is visible and correctable, rather than vanishing.
            var video = Fetched();
            video.PublishedAt = null;

            Assert.True(Publishability.IsPublishable(video, 30, Now));
        }

        [Fact]
        public void AVideoExactlyOnTheRetentionBoundary_IsPublishable()
        {
            // The comparison is strict, so the boundary day is inside the window. Stated because
            // an off-by-one here silently drops a day's worth of videos.
            var video = Fetched();
            video.PublishedAt = Now.AddDays(-30);

            Assert.True(Publishability.IsPublishable(video, 30, Now));
        }

        // ── ProtectedIds: the set a reconciliation must not reap ─────────────────────────────────

        private static VideoRecord Row(string id, string sourceUrl, bool publishable)
        {
            var video = Fetched();
            video.Id = id;
            video.SourceUrl = sourceUrl;
            if (!publishable)
                video.Extras = new VideoExtras { Qualities = [] };
            return video;
        }

        [Fact]
        public void ProtectedIds_DropsThisSourcesUnpublishableRows()
        {
            // The global set is wider than publishability on purpose — it ignores retention and the
            // tier check — so the owning source's publication is what applies them. Leaving one of
            // its own unpublishable rows in the set would protect the very item it has just decided
            // to retract, and retention would never take anything out of the library.
            var rows = new List<VideoRecord>
            {
                Row("keep", "https://example.test/mine", publishable: true),
                Row("drop", "https://example.test/mine", publishable: false),
            };

            var ids = Publishability.ProtectedIds(["keep", "drop"], rows, retentionDays: 30, Now);

            Assert.Contains("keep", ids);
            Assert.DoesNotContain("drop", ids);
        }

        [Fact]
        public void ProtectedIds_KeepsRowsOfOtherSources_EvenUnpublishableOnes()
        {
            // This is the half that makes the subtraction safe. A video another source still
            // publishes is not ours to delete, and we cannot judge its publishability anyway: its
            // retention window is its own source's, which we were not given.
            var rows = new List<VideoRecord>
            {
                Row("mine", "https://example.test/mine", publishable: true),
            };

            var ids = Publishability.ProtectedIds(["mine", "theirs"], rows, retentionDays: 30, Now);

            Assert.Contains("theirs", ids);
        }

        [Fact]
        public void ProtectedIds_IsCaseInsensitive_LikeTheDatabase()
        {
            var rows = new List<VideoRecord> { Row("ABC", "https://example.test/mine", publishable: true) };

            Assert.Contains("abc", Publishability.ProtectedIds(["ABC"], rows, 30, Now));
        }

        [Fact]
        public void ProtectedIds_DropsARowOutsideRetention()
        {
            // The case the raw set made impossible: a video that has crossed the retention horizon
            // must lose its protection, or the reconciliation spares the item it should retract.
            var video = Fetched();
            video.Id = "aged";
            video.PublishedAt = Now.AddDays(-40);

            var ids = Publishability.ProtectedIds(["aged"], [video], retentionDays: 30, Now);

            Assert.DoesNotContain("aged", ids);
        }

    }
}

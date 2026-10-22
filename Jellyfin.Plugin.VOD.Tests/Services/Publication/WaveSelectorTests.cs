using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Publication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The backoff calculation in <see cref="WaveSelector"/>. Each test pins one invariant;
    /// a regression breaks either the C# side, the SQL side, or the concordance between them.
    /// </summary>
    public sealed class WaveSelectorTests : IDisposable
    {
        private const string SourceUrl = "https://www.youtube.com/@chan/videos";
        private const int RetryMinutes = 15;
        private const int RetentionDays = 365;

        // A fixed reference time used across all tests.
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        // ── Concordance database ──────────────────────────────────────────────────────────────────

        private readonly string _dbPath;
        private readonly VodDatabase _db;

        public WaveSelectorTests()
        {
            _dbPath = Path.GetTempFileName();
            _db = new VodDatabase(_dbPath, NullLogger<VodDatabase>.Instance);
            _db.EnsureCreated();
            _db.UpsertSource(new SourceRecord { Url = SourceUrl, Name = "Test channel" });
        }

        public void Dispose()
        {
            _db.Dispose();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        // ── BackoffFor ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void BackoffFor_ZeroFailures_IsZero()
        {
            // The first attempt must never be delayed; a zero counter must produce no wait.
            Assert.Equal(TimeSpan.Zero, WaveSelector.BackoffFor(0, RetryMinutes));
        }

        [Theory]
        [InlineData(1, 15)]
        [InlineData(2, 30)]
        [InlineData(3, 60)]
        public void BackoffFor_DoublesWithEachFailure(int failures, int expectedMinutes)
        {
            // 1 failure → 15 min, 2 failures → 30 min, 3 failures → 60 min: verifies that the
            // formula is retryMinutes × 2^(n − 1), not any other scaling factor.
            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), WaveSelector.BackoffFor(failures, RetryMinutes));
        }

        [Fact]
        public void BackoffFor_Cap_IsSevenDays()
        {
            // The cap prevents a handful of failures from pushing a video out for months.
            // A counter of 15 with a 15-minute base would give 2^14 × 15 ≈ 245760 minutes
            // without the cap.
            var result = WaveSelector.BackoffFor(15, RetryMinutes);
            Assert.Equal(TimeSpan.FromDays(7), result);
        }

        [Fact]
        public void BackoffFor_WellBeyondCap_StillReturnsSevenDays()
        {
            // The cap must hold for any counter, not only the first one to hit it.
            Assert.Equal(TimeSpan.FromDays(7), WaveSelector.BackoffFor(100, RetryMinutes));
        }

        [Fact]
        public void BackoffFor_2000Failures_Returns7Days_NotNegativeOrZero()
        {
            // 2^2000 is not representable as an integer; an integer left-shift or multiply
            // overflows silently into a negative or zero result. The implementation must
            // use double precision so that 2^2000 saturates to +Infinity and the cap wins.
            var result = WaveSelector.BackoffFor(2000, RetryMinutes);
            Assert.Equal(TimeSpan.FromDays(7), result);
            Assert.True(result > TimeSpan.Zero, "A counter of 2000 must not produce zero or negative backoff");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void BackoffFor_NonPositiveBase_ReturnsZero(int retryMinutes)
        {
            // A zero or negative retryMinutes is treated as "no backoff" rather than producing
            // a negative or nonsensical duration. This prevents a misconfigured value from
            // silently collapsing every video's backoff to zero or, worse, a negative value
            // that looks like "eligible immediately" while hiding the problem.
            Assert.Equal(TimeSpan.Zero, WaveSelector.BackoffFor(1, retryMinutes));
        }

        // ── IsInBackoff ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void IsInBackoff_SqlConcordance_AgreesWithDatabase()
        {
            // Anti-drift check: for several failure counts, the SQL POWER expression in
            // GetDueVideos and the C# BackoffFor / IsInBackoff must return the same answer.
            // A test built to match the code proves nothing; inserting rows in an order that
            // would succeed without ORDER BY is one way to catch silent agreement by accident —
            // using metadataMaxAgeDays = 0 below is another: it ensures condition 2 fires for a
            // very recent MetadataAt, which is the only value that can also be in the backoff
            // window without a 30-day wait between attempt and test.
            //
            // metadataMaxAgeDays = 0: any non-null metadata_at is immediately stale, so
            // condition 2 fires and metadata_attempts drives the backoff. MetadataAt can then
            // be set to Now - backoff/2, placing the video squarely inside the backoff window.
            const int concordanceMaxAgeDays = 0;

            // 15 and 2000 are here to reach the seven-day cap, and they are the reason this test
            // is worth anything. Verified by mutation: with only [1, 2, 3, 7] — at most sixteen
            // hours of backoff — raising the cap in the SQL alone left the whole suite green,
            // because nothing exercised a wait long enough to be capped. A chronically failing
            // video would then have been withheld for months instead of a week, and the two
            // expressions of the rule would have disagreed in exactly the place this test exists
            // to watch.
            //
            // The assertion that catches it is the post-expiry one, not the mid-window one: an
            // uncapped SQL deadline is FURTHER away, so "still in backoff at mid-window" stays
            // true and only "released once the cap has elapsed" fails.
            int[] failureCounts = [1, 2, 3, 7, 15, 2000];

            foreach (var n in failureCounts)
            {
                var backoff = WaveSelector.BackoffFor(n, RetryMinutes);
                var videoId = $"concordance-{n}";

                // MetadataAt is halfway through the backoff window: still in backoff at Now,
                // and stale (< Now = metadataCutoff when maxAge = 0).
                var metadataAt = Now - backoff / 2;

                _db.UpsertVideo(new VideoRecord
                {
                    Id = videoId,
                    SourceUrl = SourceUrl,
                    NativeId = videoId,
                    VideoUrl = "https://example.test/v/" + videoId,
                    MetadataAt = metadataAt,
                    MetadataAttempts = n,
                    FirstSeenAt = Now,
                });

                var video = _db.GetVideoByUuid(videoId)!;

                // ── During backoff ──────────────────────────────────────────────────────────
                var sqlDuringBackoff = _db.GetDueVideos(
                        SourceUrl, Now, RetryMinutes, concordanceMaxAgeDays, RetentionDays, 1000)
                    .Any(v => v.Id == videoId);
                var csharpDuringBackoff = WaveSelector.IsInBackoff(video, RetryMinutes, Now);

                Assert.True(csharpDuringBackoff,
                    $"failures={n}: C# must report the video as in backoff at mid-window");
                Assert.False(sqlDuringBackoff,
                    $"failures={n}: SQL must NOT return the video while in backoff");

                // ── After backoff expiry ────────────────────────────────────────────────────
                // nowAfterExpiry = metadataAt + backoff + 1 minute: one minute past the deadline.
                var nowAfterExpiry = metadataAt + backoff + TimeSpan.FromMinutes(1);

                var sqlAfterExpiry = _db.GetDueVideos(
                        SourceUrl, nowAfterExpiry, RetryMinutes, concordanceMaxAgeDays, RetentionDays, 1000)
                    .Any(v => v.Id == videoId);
                var csharpAfterExpiry = WaveSelector.IsInBackoff(video, RetryMinutes, nowAfterExpiry);

                Assert.False(csharpAfterExpiry,
                    $"failures={n}: C# must NOT report the video as in backoff after expiry");
                Assert.True(sqlAfterExpiry,
                    $"failures={n}: SQL must return the video once the backoff has elapsed");
            }
        }
    }
}

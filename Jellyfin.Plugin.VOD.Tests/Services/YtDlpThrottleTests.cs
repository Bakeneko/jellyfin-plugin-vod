using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YtDlpThrottleTests
    {
        private static YtDlpThrottle Make(ThrottleClock clock, int callsPerMinute = 20)
            => new(
                clock.Provider,
                () => callsPerMinute,
                clock.DelayAsync,
                NullLogger<YtDlpThrottle>.Instance);

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(30, 0)]
        [InlineData(31, 1)]
        [InlineData(50, 1)]
        [InlineData(100, 3)]
        [InlineData(300, 9)]
        public void ExtraListingCalls_ChargesOneUnitPerPageBeyondTheFirst(int entries, int expected)
        {
            // Measured on 2026-10-12: 50 entries cost 2 requests, 100 cost 4, 300 cost 10 — one
            // request for the page itself plus a continuation every thirty entries.
            Assert.Equal(expected, YtDlpThrottle.ExtraListingCalls(entries));
        }

        [Fact]
        public async Task EnterAsync_SpacesTwoCallsOfOneLane_ByTheNominalInterval()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            using (var first = await throttle.EnterAsync("youtube", YtDlpCallKind.Listing, CancellationToken.None))
            {
                first.Succeeded();
            }

            using (var second = await throttle.EnterAsync("youtube", YtDlpCallKind.Listing, CancellationToken.None))
            {
                second.Succeeded();
            }

            // 20 a minute is one every three seconds. The first call waits for nothing.
            Assert.Equal([TimeSpan.FromSeconds(3)], clock.Waits);
        }

        [Fact]
        public async Task EnterAsync_DoesNotMakeOnePlatformWaitForAnother()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            using (var youtube = await throttle.EnterAsync("youtube", YtDlpCallKind.Listing, CancellationToken.None))
            {
                youtube.Succeeded();
            }

            using (var arte = await throttle.EnterAsync("arte", YtDlpCallKind.Listing, CancellationToken.None))
            {
                arte.Succeeded();
            }

            Assert.Empty(clock.Waits);
        }

        [Fact]
        public async Task Charge_MakesAnEnumerationPayForEveryRequestItMade()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            using (var listing = await throttle.EnterAsync("youtube", YtDlpCallKind.Listing, CancellationToken.None))
            {
                // 300 entries is ten requests to YouTube, not one (measured).
                listing.Charge(9);
                listing.Succeeded();
            }

            using (var next = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                next.Succeeded();
            }

            Assert.Equal([TimeSpan.FromSeconds(30)], clock.Waits);
        }

        [Fact]
        public async Task RateLimited_BacksTheLaneOff_DoublingAndCappedAtAnHour()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            for (var i = 0; i < 8; i++)
            {
                using var lease = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None);
                lease.RateLimited();
            }

            // The first call waits for nothing, then 1, 2, 4, 8, 16, 32, and 60 — the cap YouTube
            // itself announces.
            Assert.Equal(
                [
                    TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4),
                    TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(32),
                    TimeSpan.FromMinutes(60)
                ],
                clock.Waits);
        }

        [Fact]
        public async Task Succeeded_ClearsTheLanesBackoff()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            using (var limited = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                limited.RateLimited();
            }

            using (var recovered = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                recovered.Succeeded();
            }

            using (var after = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                after.Succeeded();
            }

            // One minute of backoff, then the nominal interval — not two minutes.
            Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3)], clock.Waits);
        }

        [Fact]
        public async Task Dispose_WithoutAVerdict_KeepsTheLanesBackoffLadder()
        {
            // A call can end without telling the lease anything: the caller's own token is
            // cancelled, the process fails to start, or reading its output throws. Counting that
            // as a success would reset the escalation, so a user stopping the sync task — or one
            // crashing process — would restart the ladder at one minute every time, and it would
            // never reach the hour the platform itself announces.
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            using (var first = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                first.RateLimited();
            }

            using (await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                // No verdict at all.
            }

            using (var third = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                third.RateLimited();
            }

            using (var fourth = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                fourth.Succeeded();
            }

            // One minute of backoff; then the verdictless call pays the nominal interval but does
            // not touch the ladder, so the next refusal is the SECOND step — two minutes, not one.
            Assert.Equal(
                [TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(2)],
                clock.Waits);
        }

        [Fact]
        public async Task EnterAsync_ObservesCancellationDuringTheBackoffWait_WithoutLeavingTheLaneLocked()
        {
            // The cancellation that matters is the one that strikes while the lane's gate is
            // already held — waiting out a backoff of up to an hour. Cancelling before the call
            // never reaches that code: SemaphoreSlim.WaitAsync hands back a cancelled task
            // without taking the lock.
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            using (var limited = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                limited.RateLimited();
            }

            using var cts = new CancellationTokenSource();
            clock.CancelOnFirstWait = cts;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, cts.Token));

            // The gate was held when the wait was abandoned; if it was not released, this hangs.
            using var after = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None);
            after.Succeeded();
        }

        [Fact]
        public async Task EnterAsync_LetsAListingOvertakeAWaitingResolution()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);
            var order = new List<string>();

            // Hold the lane, so both callers below have to queue.
            var held = await throttle.EnterAsync("youtube", YtDlpCallKind.Listing, CancellationToken.None);

            var resolution = Task.Run(async () =>
            {
                using var lease = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None);
                lock (order)
                {
                    order.Add("resolution");
                }

                lease.Succeeded();
            });

            // Give the resolution time to be queued before the listing asks.
            while (throttle.QueuedCount("youtube") == 0)
            {
                await Task.Yield();
            }

            var listing = Task.Run(async () =>
            {
                using var lease = await throttle.EnterAsync("youtube", YtDlpCallKind.Listing, CancellationToken.None);
                lock (order)
                {
                    order.Add("listing");
                }

                lease.Succeeded();
            });

            while (throttle.QueuedCount("youtube") < 2)
            {
                await Task.Yield();
            }

            held.Succeeded();
            held.Dispose();

            await Task.WhenAll(resolution, listing);

            // An enumeration round is bounded and conditions everything else, so it goes first
            // even though the resolution asked earlier.
            Assert.Equal(["listing", "resolution"], order);
        }

        [Fact]
        public async Task EnterAsync_ObservesCancellation_WithoutLeavingTheLaneLocked()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            using (var first = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None))
            {
                // An hour of backoff is now pending for this lane.
                first.RateLimited();
            }

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, cancelled.Token));

            // The lane is still usable by the next caller.
            using var after = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None);
            after.Succeeded();
        }

        [Fact]
        public async Task Lease_ReleasesTheLane_WhenTheCallThrows()
        {
            var clock = new ThrottleClock();
            var throttle = Make(clock);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using var lease = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None);
                throw new InvalidOperationException("yt-dlp died");
            });

            // Without the release, this would hang instead of returning.
            using var after = await throttle.EnterAsync("youtube", YtDlpCallKind.Resolution, CancellationToken.None);
            after.Succeeded();
        }
    }
}

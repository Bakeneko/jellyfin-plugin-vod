using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    /// <summary>
    /// A clock the test moves by hand. Every wait the throttle asks for is recorded and applied
    /// instantly, so a test that exercises an hour of backoff runs in microseconds.
    /// </summary>
    internal sealed class ThrottleClock
    {
        private readonly object _sync = new();
        private readonly List<TimeSpan> _waits = [];
        private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Cancelled the first time the throttle asks to wait, for the one path where cancellation
        /// strikes while the lane's gate is held.
        /// </summary>
        internal CancellationTokenSource? CancelOnFirstWait { get; set; }

        internal IReadOnlyList<TimeSpan> Waits
        {
            get
            {
                lock (_sync)
                {
                    return [.. _waits];
                }
            }
        }

        internal TimeProvider Provider => new Stub(this);

        /// <summary>A throttle that never sleeps, for tests that only need one to exist.</summary>
        internal static YtDlpThrottle Throttle(int callsPerMinute = 20)
        {
            var clock = new ThrottleClock();
            return new YtDlpThrottle(
                clock.Provider,
                () => callsPerMinute,
                clock.DelayAsync,
                NullLogger<YtDlpThrottle>.Instance);
        }

        internal Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                _waits.Add(delay);
                if (delay > TimeSpan.Zero)
                {
                    _now += delay;
                }
            }

            // The wait is where a caller's cancellation lands when the lane is already held, so a
            // test that needs that path asks for it here.
            if (CancelOnFirstWait is { } cts)
            {
                CancelOnFirstWait = null;
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        }

        private sealed class Stub(ThrottleClock clock) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow()
            {
                lock (clock._sync)
                {
                    return clock._now;
                }
            }
        }
    }
}

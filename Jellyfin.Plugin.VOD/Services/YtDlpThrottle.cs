using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>What a background yt-dlp call is for, which decides who waits for whom.</summary>
    public enum YtDlpCallKind
    {
        /// <summary>Enumerating a source. Bounded, short, and a precondition of everything else.</summary>
        Listing,

        /// <summary>Resolving one video's metadata or streams. A long tail.</summary>
        Resolution,
    }

    /// <summary>
    /// Paces background yt-dlp calls, one lane per platform.
    /// </summary>
    /// <remarks>
    /// A platform limits the session, not the caller, so sync, stream descriptions and the live
    /// probe all share one allowance per platform — and only per platform: slowing Arte down
    /// because YouTube protested protects no one and delays everyone. Each lane serialises its
    /// calls, keeps a minimum interval between them, and backs off on its own when its platform
    /// says it has had enough. Resolving a video a user just started does not come through here
    /// at all (see <c>YtDlpService.ResolveAsync</c>): making a started video wait behind a backlog
    /// would break playback.
    /// </remarks>
    public sealed partial class YtDlpThrottle : IDisposable
    {
        /// <summary>First step of a lane's backoff, doubling from here.</summary>
        internal static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(1);

        /// <summary>The ceiling YouTube itself announces for a rate-limited session.</summary>
        internal static readonly TimeSpan BackoffCap = TimeSpan.FromMinutes(60);

        private readonly ConcurrentDictionary<string, Lane> _lanes = new(StringComparer.OrdinalIgnoreCase);
        internal TimeProvider Clock => _time;

        private readonly TimeProvider _time;
        private readonly Func<int> _callsPerMinute;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly ILogger<YtDlpThrottle> _logger;
        private volatile bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="YtDlpThrottle"/> class.</summary>
        /// <param name="time">The clock, injected so tests do not depend on the real one.</param>
        /// <param name="callsPerMinute">
        /// Reads the configured per-platform rate on every call, so a settings change applies
        /// without a restart.
        /// </param>
        /// <param name="delay">
        /// How to wait. Injected rather than taken from <paramref name="time"/> so tests can
        /// answer instantly: a test that exercises an hour of backoff must not take an hour.
        /// </param>
        /// <param name="logger">Logger instance.</param>
        public YtDlpThrottle(
            TimeProvider time,
            Func<int> callsPerMinute,
            Func<TimeSpan, CancellationToken, Task> delay,
            ILogger<YtDlpThrottle> logger)
        {
            _time = time;
            _callsPerMinute = callsPerMinute;
            _delay = delay;
            _logger = logger;
        }

        /// <summary>
        /// Takes a lane for the duration of one yt-dlp call, waiting out its interval and any
        /// backoff it is under.
        /// </summary>
        /// <param name="lane">The lane key, from <see cref="PlatformLane.For"/>.</param>
        /// <param name="kind">What the call is for; a listing overtakes a queued resolution.</param>
        /// <param name="cancellationToken">A token to abandon the wait.</param>
        /// <returns>A lease to dispose once the call has returned.</returns>
        /// <remarks>
        /// <b>The caller must dispose the lease</b>, and the only safe way is <c>using</c>: the
        /// lane stays taken until it is disposed, and a leaked <see cref="YtDlpCallKind.Listing"/>
        /// lease leaves the lane's pending-listing count above zero — which makes every
        /// <see cref="YtDlpCallKind.Resolution"/> on that platform yield for ever, since a
        /// resolution waits for the listings to clear. One forgotten <c>using</c> wedges a
        /// platform until the server restarts.
        /// </remarks>
        public async Task<YtDlpLease> EnterAsync(
            string lane, YtDlpCallKind kind, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lane);

            var entry = _lanes.GetOrAdd(lane, _ => new Lane());
            var interval = Interval();

            if (kind == YtDlpCallKind.Listing)
            {
                Interlocked.Increment(ref entry.PendingListings);
            }

            Interlocked.Increment(ref entry.Queued);
            try
            {
                while (true)
                {
                    await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                    // A resolution yields to any listing that is queued or being served: an
                    // enumeration round is bounded and conditions everything else, while the
                    // resolution tail does not care about a few minutes. The yielding caller
                    // waits one nominal interval rather than spinning.
                    if (kind == YtDlpCallKind.Resolution
                        && Volatile.Read(ref entry.PendingListings) > 0)
                    {
                        entry.Gate.Release();
                        await _delay(interval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }
            }
            catch
            {
                if (kind == YtDlpCallKind.Listing)
                {
                    Interlocked.Decrement(ref entry.PendingListings);
                }

                Interlocked.Decrement(ref entry.Queued);
                throw;
            }

            Interlocked.Decrement(ref entry.Queued);

            try
            {
                var wait = entry.NextAllowed - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                Release(entry, kind);
                throw;
            }

            return new YtDlpLease(this, entry, kind, interval);
        }

        /// <summary>
        /// How many platform requests an enumeration made beyond the first, for
        /// <see cref="YtDlpLease.Charge"/>.
        /// </summary>
        /// <param name="entries">How many entries the listing returned.</param>
        /// <returns>The extra units to charge; zero for a listing that fitted in one request.</returns>
        /// <remarks>
        /// One yt-dlp process is not one request: a channel tab is paged about thirty entries at a
        /// time and followed by continuation tokens. Measured on 2026-10-12 against
        /// <c>@veritasium/videos</c> — 50 entries cost 2 requests, 100 cost 4, 300 cost 10 — which
        /// is <c>ceil(entries / 30)</c>. Without this, a round of 47 sources at 300 videos would
        /// spend 47 units for 449 requests and the configured cap would lie by a factor of ten.
        /// </remarks>
        public static int ExtraListingCalls(int entries)
            => entries <= 30 ? 0 : ((entries + 29) / 30) - 1;

        /// <summary>How many callers are currently queueing for a lane. For tests.</summary>
        /// <param name="lane">The lane key.</param>
        /// <returns>The number of callers waiting to be served.</returns>
        public int QueuedCount(string lane)
            => _lanes.TryGetValue(lane, out var entry) ? Volatile.Read(ref entry.Queued) : 0;

        /// <inheritdoc />
        /// <remarks>
        /// The lanes are kept rather than cleared: a lease still outstanding at shutdown releases
        /// its lane as it unwinds, and a cleared dictionary would hand the next caller a brand new
        /// lane — a fresh semaphore that nothing disposes, and a backoff forgotten.
        /// </remarks>
        public void Dispose()
        {
            _disposed = true;
            foreach (var lane in _lanes.Values)
            {
                lane.Dispose();
            }
        }

        private TimeSpan Interval()
        {
            // Clamped at both ends: 0 or a negative would divide by zero, and an absurdly high
            // value would make the interval round to zero ticks, turning the pacing into a hot
            // loop rather than a limit.
            var perMinute = Math.Clamp(_callsPerMinute(), 1, 600);
            return TimeSpan.FromSeconds(60d / perMinute);
        }

        internal void Release(Lane entry, YtDlpCallKind kind)
        {
            if (_disposed)
            {
                // Shut down under us. Releasing a disposed semaphore throws, and this runs from a
                // lease's Dispose — often a compiler-generated finally — where an exception would
                // mask whatever was already unwinding.
                return;
            }

            if (kind == YtDlpCallKind.Listing)
            {
                Interlocked.Decrement(ref entry.PendingListings);
            }

            entry.Gate.Release();
        }

        /// <summary>One platform's queue, its pacing and its backoff.</summary>
        internal sealed class Lane : IDisposable
        {
            internal readonly SemaphoreSlim Gate = new(1, 1);

            /// <summary>Read without the gate, so interlocked.</summary>
            internal int PendingListings;

            /// <summary>Read without the gate, so interlocked.</summary>
            internal int Queued;

            /// <summary>Only touched while the gate is held.</summary>
            internal DateTimeOffset NextAllowed;

            /// <summary>Only touched while the gate is held.</summary>
            internal int ConsecutiveLimits;

            /// <inheritdoc />
            public void Dispose() => Gate.Dispose();
        }

        internal void ReportBackingOff(double minutes, int limits)
            => LogBackingOff(minutes, limits);

        [LoggerMessage(EventId = 400, Level = LogLevel.Warning,
            Message = "yt-dlp backing off {Minutes} minute(s) after {Limits} consecutive refusal(s) "
                + "from this platform")]
        private partial void LogBackingOff(double minutes, int limits);
    }
}

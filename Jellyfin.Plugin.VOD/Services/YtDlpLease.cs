using System;
using System.Threading;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// One call's hold on a lane. Disposing it releases the lane and charges it for what the
    /// call cost, so a call that throws cannot wedge a platform.
    /// </summary>
    public sealed class YtDlpLease : IDisposable
    {
        private readonly YtDlpThrottle _throttle;
        private readonly YtDlpThrottle.Lane _lane;
        private readonly YtDlpCallKind _kind;
        private readonly TimeSpan _interval;
        private int _units = 1;
        private Verdict _verdict = Verdict.Unknown;
        private int _disposed;

    internal YtDlpLease(YtDlpThrottle throttle, YtDlpThrottle.Lane lane, YtDlpCallKind kind, TimeSpan interval)
        {
            _throttle = throttle;
            _lane = lane;
            _kind = kind;
            _interval = interval;
        }

        /// <summary>
        /// Charges the lane for requests this call made beyond the first.
        /// </summary>
        /// <param name="extraCalls">
        /// How many additional platform requests the call turned out to make — for an
        /// enumeration, <c>ceil(entries / 30) - 1</c>, which is only known once it returns.
        /// Negative values are ignored.
        /// </param>
        public void Charge(int extraCalls)
        {
            if (extraCalls > 0)
            {
                _units += extraCalls;
            }
        }

        /// <summary>Reports that the platform refused the call: rate limit, or a timeout.</summary>
        public void RateLimited() => _verdict = Verdict.Limited;

        /// <summary>Reports that the platform answered, which clears its backoff.</summary>
        public void Succeeded() => _verdict = Verdict.Succeeded;

        /// <inheritdoc />
        public void Dispose()
        {
            // Interlocked rather than a plain flag: releasing a SemaphoreSlim(1, 1) twice throws,
            // and this is a public type whose callers we do not all control.
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            var now = _throttle.Clock.GetUtcNow();
            switch (_verdict)
            {
                case Verdict.Limited:
                    _lane.ConsecutiveLimits++;
                    var backoff = BackoffFor(_lane.ConsecutiveLimits);
                    _lane.NextAllowed = now + backoff;
                    _throttle.ReportBackingOff(backoff.TotalMinutes, _lane.ConsecutiveLimits);
                    break;

                case Verdict.Succeeded:
                    _lane.ConsecutiveLimits = 0;
                    _lane.NextAllowed = now + (_interval * _units);
                    break;

                default:
                    // Nothing was reported: the caller's own token was cancelled, the process
                    // failed to start, or reading its output threw. That says nothing about the
                    // platform, so the backoff ladder is left exactly where it was — counting it
                    // as a success would restart the escalation at one minute every time a user
                    // stops the sync, and it would never reach the hour the platform announces.
                    // The nominal interval is still charged, so a crash loop cannot spin.
                    _lane.NextAllowed = now + (_interval * _units);
                    break;
            }

            _throttle.Release(_lane, _kind);
        }

        /// <summary>What a call turned out to be, as far as the platform is concerned.</summary>
        private enum Verdict
        {
            /// <summary>Nothing was reported, so the lane's backoff is left untouched.</summary>
            Unknown,

            /// <summary>The platform answered.</summary>
            Succeeded,

            /// <summary>The platform refused.</summary>
            Limited,
        }

        private static TimeSpan BackoffFor(int consecutiveLimits)
        {
            var doublings = Math.Min(consecutiveLimits - 1, 30);
            var backoff = YtDlpThrottle.BackoffBase * Math.Pow(2, doublings);
            return backoff > YtDlpThrottle.BackoffCap ? YtDlpThrottle.BackoffCap : backoff;
        }
    }
}

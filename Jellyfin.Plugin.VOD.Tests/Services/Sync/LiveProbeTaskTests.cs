using System;
using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class LiveProbeTaskTests
    {
        [Fact]
        public void ShouldSkip_ReturnsTrueWhenProbedWithinInterval()
        {
            // A source probed 2 minutes ago must be skipped when the interval is 5 minutes.
            var now = DateTimeOffset.UtcNow;
            Assert.True(LiveProbeTask.ShouldSkip(now.AddMinutes(-2), intervalMinutes: 5, now));
        }

        [Fact]
        public void ShouldSkip_ReturnsFalseWhenProbedBeyondInterval()
        {
            // A source probed 6 minutes ago must not be skipped when the interval is 5 minutes.
            var now = DateTimeOffset.UtcNow;
            Assert.False(LiveProbeTask.ShouldSkip(now.AddMinutes(-6), intervalMinutes: 5, now));
        }

        [Fact]
        public void ShouldSkip_ReturnsFalseWhenNeverProbed()
        {
            // A source that has never been probed in this session must always be visited. The value
            // is what the caller's dictionary lookup yields when the key is absent, so caller and
            // test take the same path.
            Assert.False(LiveProbeTask.ShouldSkip(default, intervalMinutes: 5, DateTimeOffset.UtcNow));
        }

        [Fact]
        public void ShouldSkip_ReturnsFalseWhenIntervalIsZero()
        {
            // Interval <= 0 means disabled; ExecuteAsync exits early in that case, but ShouldSkip
            // itself must not skip sources when the value is zero (belt-and-suspenders).
            var now = DateTimeOffset.UtcNow;
            Assert.False(LiveProbeTask.ShouldSkip(now.AddMinutes(-1), intervalMinutes: 0, now));
        }
    }
}

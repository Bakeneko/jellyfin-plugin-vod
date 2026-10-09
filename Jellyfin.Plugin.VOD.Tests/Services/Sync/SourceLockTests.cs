using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class SourceLockTests
    {
        [Fact]
        public async Task AcquireAsync_SerialisesTwoHoldersOfTheSameSource()
        {
            // The sync and the probe both publish a source by reconciling it, and reconciling deletes
            // items: two passes over one source at the same time would fight.
            var inside = 0;
            var peak = 0;

            async Task Hold()
            {
                using (await SourceLock.AcquireAsync("https://example.com/s", CancellationToken.None))
                {
                    peak = Math.Max(peak, Interlocked.Increment(ref inside));
                    await Task.Delay(30, CancellationToken.None);
                    Interlocked.Decrement(ref inside);
                }
            }

            await Task.WhenAll(Hold(), Hold(), Hold());

            Assert.Equal(1, peak);
        }

        [Fact]
        public async Task AcquireAsync_DoesNotBlockDifferentSources()
        {
            using var first = await SourceLock.AcquireAsync("https://example.com/a", CancellationToken.None);

            var second = SourceLock.AcquireAsync("https://example.com/b", CancellationToken.None);
            var completed = await Task.WhenAny(second, Task.Delay(500, CancellationToken.None));

            Assert.Same(second, completed);
            (await second).Dispose();
        }
    }
}

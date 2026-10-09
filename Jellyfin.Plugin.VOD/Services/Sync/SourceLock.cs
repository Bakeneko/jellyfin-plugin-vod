using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Per-source mutual exclusion for the sync and live-probe paths.
    /// </summary>
    /// <remarks>
    /// Both paths publish a source by calling <c>LibraryPublisher.ReconcileAsync</c>, which deletes
    /// library items that the source no longer accounts for. Two concurrent passes over one source
    /// would fight: the loser's items disappear. A semaphore per URL prevents that.
    ///
    /// Entries are never removed from the dictionary once added: the number of sources is small,
    /// and removing an entry on release would open a window between the release and the next
    /// acquisition where the second caller creates a NEW semaphore and both hold it simultaneously.
    /// </remarks>
    public static class SourceLock
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Acquires the per-source lock for <paramref name="sourceUrl"/>, waiting until it is
        /// available, and returns a handle whose disposal releases it.
        /// </summary>
        /// <param name="sourceUrl">The source URL that identifies the lock.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>
        /// A task that completes with an <see cref="IDisposable"/> that releases the lock.
        /// </returns>
        public static async Task<IDisposable> AcquireAsync(
            string sourceUrl, CancellationToken cancellationToken)
        {
            var semaphore = _semaphores.GetOrAdd(sourceUrl, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ReleaseHandle(semaphore);
        }

        private sealed class ReleaseHandle : IDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            private bool _disposed;

            internal ReleaseHandle(SemaphoreSlim semaphore) => _semaphore = semaphore;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _semaphore.Release();
            }
        }
    }
}

using System;
using System.Threading;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>Exclusive hold on the cookie file for the duration of one yt-dlp invocation.</summary>
    public sealed class CookieLease : IDisposable
    {
        private SemaphoreSlim? _lock;

        internal CookieLease(string? path, SemaphoreSlim? heldLock)
        {
            Path = path;
            _lock = heldLock;
        }

        /// <summary>Gets the cookie file to pass to <c>--cookies</c>, or <see langword="null"/> when there is none.</summary>
        public string? Path { get; }

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _lock, null)?.Release();
    }
}

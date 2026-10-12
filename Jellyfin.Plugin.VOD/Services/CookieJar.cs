using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Hands yt-dlp a cookie file it may safely rewrite, without ever touching the user's seed.
    /// </summary>
    /// <remarks>
    /// yt-dlp rewrites <c>--cookies</c> in place. The seed is therefore copied into the plugin data
    /// folder; re-seeding triggers when the seed's mtime changes. Concurrent yt-dlp processes would race
    /// on one working copy, so there is a copy — and a semaphore — <b>per lane</b>: a single shared copy
    /// behind a single lock is what would otherwise make the throttle's per-platform lanes fictional,
    /// since an Arte call would queue behind a YouTube one for as long as its process ran.
    /// </remarks>
    public sealed partial class CookieJar : IDisposable
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<CookieJar> _logger;
        /// <summary>
        /// Per lane, not global: with one flag, a lane whose copy succeeds clears the flag a lane
        /// whose copy keeps failing had set, so the same error logs again on every single call —
        /// up to the full call rate, with a stack trace each time.
        /// </summary>
        private readonly ConcurrentDictionary<string, bool> _seedFailureLogged =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initializes a new instance of the <see cref="CookieJar"/> class.</summary>
        public CookieJar(ILogger<CookieJar> logger) => _logger = logger;

        private static string? ConfiguredSeedPath
        {
            get
            {
                var path = Plugin.Instance?.Configuration.CookiesPath;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }

        /// <summary>The file name the working copy of a lane's cookies takes.</summary>
        /// <param name="lane">The lane key, from <see cref="PlatformLane.For"/>.</param>
        /// <returns>A file name safe on every platform the server runs on.</returns>
        /// <remarks>
        /// One copy per lane rather than one for everything: yt-dlp rewrites the file in place, so
        /// a shared copy has to be serialised, and that serialisation is what would make the
        /// throttle's per-platform lanes fictional. A lane only talks to its own platform, so the
        /// cookies it keeps refreshed are the only ones it needs.
        /// </remarks>
        internal static string WorkingFileNameFor(string lane)
        {
            var safe = lane;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(invalid, '_');
            }

            return $"cookies-{safe}.txt";
        }

        private static string WorkingPathFor(string lane)
            => Path.Combine(Plugin.Instance!.DataFolderPath, WorkingFileNameFor(lane));

        private static string SeedStampPathFor(string lane)
            => Path.Combine(Plugin.Instance!.DataFolderPath, WorkingFileNameFor(lane) + ".seed");

        /// <summary>
        /// Takes the cookie file of one lane for the duration of one yt-dlp invocation.
        /// Returns a lease whose <see cref="CookieLease.Path"/> is null when no cookies are configured.
        /// </summary>
        /// <param name="lane">The lane key, from <see cref="PlatformLane.For"/>.</param>
        /// <param name="cancellationToken">A token to abandon the wait.</param>
        /// <returns>The lease; disposing it releases that lane's file.</returns>
        public async Task<CookieLease> AcquireAsync(string lane, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lane);

            var seed = ConfiguredSeedPath;
            if (seed is null || Plugin.Instance is null)
            {
                return new CookieLease(null, null);
            }

            var gate = _locks.GetOrAdd(lane, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return new CookieLease(EnsureWorkingCopy(seed, lane), gate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSeedFailedOnce(seed, ex, lane);
                gate.Release();
                return new CookieLease(null, null);
            }
        }

        private string? EnsureWorkingCopy(string seed, string lane)
        {
            if (!File.Exists(seed))
            {
                LogSeedMissingOnce(seed, lane);
                return null;
            }

            var stamp = File.GetLastWriteTimeUtc(seed).Ticks
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var workingPath = WorkingPathFor(lane);
            var stampPath = SeedStampPathFor(lane);
            var seeded = File.Exists(stampPath)
                ? File.ReadAllText(stampPath).Trim()
                : null;

            if (!File.Exists(workingPath) || !string.Equals(stamp, seeded, StringComparison.Ordinal))
            {
                Plugin.Instance!.EnsureDataFolder();
                File.Copy(seed, workingPath, overwrite: true);
                File.WriteAllText(stampPath, stamp);
                RestrictPermissions(workingPath);
                LogSeeded(seed);
            }

            _seedFailureLogged[lane] = false;
            return workingPath;
        }

        private static void RestrictPermissions(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        private void LogSeedFailedOnce(string seed, Exception ex, string lane)
        {
            if (!_seedFailureLogged.TryGetValue(lane, out var logged) && logged)
            {
                _seedFailureLogged[lane] = true;
                LogSeedFailed(seed, ex);
            }
        }

        private void LogSeedMissingOnce(string seed, string lane)
        {
            if (!_seedFailureLogged.TryGetValue(lane, out var logged) && logged)
            {
                _seedFailureLogged[lane] = true;
                LogSeedMissing(seed);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var gate in _locks.Values)
            {
                gate.Dispose();
            }

            _locks.Clear();
        }

        [LoggerMessage(EventId = 20, Level = LogLevel.Information,
            Message = "Seeded working cookie file from {SeedPath}")]
        private partial void LogSeeded(string seedPath);

        [LoggerMessage(EventId = 21, Level = LogLevel.Warning,
            Message = "Cookie seed {SeedPath} does not exist; continuing without cookies")]
        private partial void LogSeedMissing(string seedPath);

        [LoggerMessage(EventId = 22, Level = LogLevel.Error,
            Message = "Could not read cookie seed {SeedPath}; continuing without cookies")]
        private partial void LogSeedFailed(string seedPath, Exception exception);
    }
}

using System;
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
    /// yt-dlp rewrites <c>--cookies</c> in place. The seed is therefore copied once into the plugin data
    /// folder; re-seeding triggers when the seed's mtime changes. Concurrent yt-dlp processes would race
    /// on the working copy, so access is serialised via a semaphore.
    /// </remarks>
    public sealed partial class CookieJar : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ILogger<CookieJar> _logger;
        private bool _seedFailureLogged;

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

        private static string WorkingPath
            => Path.Combine(Plugin.Instance!.DataFolderPath, "cookies.txt");

        private static string SeedStampPath
            => Path.Combine(Plugin.Instance!.DataFolderPath, "cookies.seed");

        /// <summary>
        /// Takes the cookie file for the duration of one yt-dlp invocation.
        /// Returns a lease whose <see cref="CookieLease.Path"/> is null when no cookies are configured.
        /// </summary>
        public async Task<CookieLease> AcquireAsync(CancellationToken cancellationToken)
        {
            var seed = ConfiguredSeedPath;
            if (seed is null || Plugin.Instance is null)
            {
                return new CookieLease(null, null);
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return new CookieLease(EnsureWorkingCopy(seed), _lock);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSeedFailedOnce(seed, ex);
                _lock.Release();
                return new CookieLease(null, null);
            }
        }

        private string? EnsureWorkingCopy(string seed)
        {
            if (!File.Exists(seed))
            {
                LogSeedMissingOnce(seed);
                return null;
            }

            var stamp = File.GetLastWriteTimeUtc(seed).Ticks
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var seeded = File.Exists(SeedStampPath)
                ? File.ReadAllText(SeedStampPath).Trim()
                : null;

            if (!File.Exists(WorkingPath) || !string.Equals(stamp, seeded, StringComparison.Ordinal))
            {
                Plugin.Instance!.EnsureDataFolder();
                File.Copy(seed, WorkingPath, overwrite: true);
                File.WriteAllText(SeedStampPath, stamp);
                RestrictPermissions(WorkingPath);
                LogSeeded(seed);
            }

            _seedFailureLogged = false;
            return WorkingPath;
        }

        private static void RestrictPermissions(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        private void LogSeedFailedOnce(string seed, Exception ex)
        {
            if (!_seedFailureLogged)
            {
                _seedFailureLogged = true;
                LogSeedFailed(seed, ex);
            }
        }

        private void LogSeedMissingOnce(string seed)
        {
            if (!_seedFailureLogged)
            {
                _seedFailureLogged = true;
                LogSeedMissing(seed);
            }
        }

        /// <inheritdoc />
        public void Dispose() => _lock.Dispose();

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

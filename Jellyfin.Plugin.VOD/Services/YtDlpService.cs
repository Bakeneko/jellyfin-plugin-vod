using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Resolves a page URL into a directly playable stream URL by shelling out to yt-dlp.
    /// </summary>
    /// <remarks>
    /// Asks yt-dlp for <c>%(manifest_url)s</c> then <c>%(url)s</c>. The manifest URL is an HLS master
    /// playlist that carries video, audio and subtitles together; neither YouTube nor Arte still publishes
    /// a muxed progressive format, so the bare URL means video without audio.
    /// </remarks>
    public sealed partial class YtDlpService
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PlaylistTimeout = TimeSpan.FromMinutes(10);

        private readonly CookieJar _cookies;
        private readonly ILogger<YtDlpService> _logger;

        /// <summary>Initializes a new instance of the <see cref="YtDlpService"/> class.</summary>
        public YtDlpService(CookieJar cookies, ILogger<YtDlpService> logger)
        {
            _cookies = cookies;
            _logger = logger;
        }

        /// <summary>
        /// Resolves a page URL into something playable, or returns <see langword="null"/> on failure.
        /// </summary>
        /// <param name="pageUrl">The page URL as accepted by yt-dlp.</param>
        /// <param name="format">The yt-dlp format selector (e.g. <c>bv*[protocol^=m3u8]</c>).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<StreamResolution?> ResolveAsync(
            string pageUrl,
            string format,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
            ArgumentException.ThrowIfNullOrWhiteSpace(format);

            var startInfo = BuildBaseStartInfo();
            using var cookies = await _cookies.AcquireAsync(cancellationToken).ConfigureAwait(false);
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo);

            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(format);
            startInfo.ArgumentList.Add("--print");
            startInfo.ArgumentList.Add("%(manifest_url)s");
            startInfo.ArgumentList.Add("--print");
            startInfo.ArgumentList.Add("%(url)s");
            startInfo.ArgumentList.Add(pageUrl);

            var sw = Stopwatch.StartNew();
            var stdout = await RunProcessAsync(startInfo, Timeout, cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();

            if (stdout is null)
                return null;

            var lines = stdout.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length == 0)
            {
                LogResolveFailed(pageUrl, 0, "yt-dlp printed nothing");
                return null;
            }

            var manifestUrl = lines[0];
            var hasManifest = !string.Equals(manifestUrl, "NA", StringComparison.Ordinal);
            var resolved = hasManifest ? manifestUrl : lines[^1];

            LogResolved(pageUrl, hasManifest, sw.ElapsedMilliseconds);
            return new StreamResolution(resolved, hasManifest);
        }

        /// <summary>
        /// Runs <c>yt-dlp --flat-playlist -J [--playlist-end N] [--dateafter YYYYMMDD]</c> and returns the parsed result.
        /// Returns <see langword="null"/> when yt-dlp exits non-zero or output cannot be parsed.
        /// </summary>
        /// <param name="url">The playlist or channel URL.</param>
        /// <param name="maxEntries">When set, passes <c>--playlist-end N</c>.</param>
        /// <param name="dateAfter">
        /// When set, passes <c>--dateafter YYYYMMDD</c> so yt-dlp stops enumerating entries older than this date.
        /// Use when <c>RetentionDays</c> is configured to avoid fetching videos that will be immediately discarded.
        /// Note: behaviour varies by extractor — yt-dlp may still enumerate all entries and filter client-side.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<PlaylistInfo?> GetPlaylistEntriesAsync(
            string url,
            int? maxEntries,
            DateTimeOffset? dateAfter,
            CancellationToken cancellationToken)
        {
            var startInfo = BuildBaseStartInfo();
            using var cookies = await _cookies.AcquireAsync(cancellationToken).ConfigureAwait(false);
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo);

            startInfo.ArgumentList.Add("--flat-playlist");
            startInfo.ArgumentList.Add("-J");
            if (maxEntries.HasValue)
            {
                startInfo.ArgumentList.Add("--playlist-end");
                startInfo.ArgumentList.Add(maxEntries.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (dateAfter.HasValue)
            {
                startInfo.ArgumentList.Add("--dateafter");
                startInfo.ArgumentList.Add(dateAfter.Value.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }

            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add(url);

            var stdout = await RunProcessAsync(startInfo, PlaylistTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (stdout is null)
                return null;

            var info = PlaylistInfo.ParseJson(stdout);
            if (info is null)
                LogParseFailed(url, "flat-playlist");
            return info;
        }

        /// <summary>
        /// Runs <c>yt-dlp -J --no-playlist</c> for a single video and returns the parsed metadata.
        /// Returns <see langword="null"/> when yt-dlp exits non-zero or output cannot be parsed.
        /// </summary>
        /// <param name="url">The video page URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<VideoMetadata?> GetVideoMetadataAsync(
            string url,
            CancellationToken cancellationToken)
        {
            var startInfo = BuildBaseStartInfo();
            using var cookies = await _cookies.AcquireAsync(cancellationToken).ConfigureAwait(false);
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo);

            startInfo.ArgumentList.Add("-J");
            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add(url);

            var stdout = await RunProcessAsync(startInfo, Timeout, cancellationToken)
                .ConfigureAwait(false);
            if (stdout is null)
                return null;

            var meta = VideoMetadata.ParseJson(stdout);
            if (meta is null)
                LogParseFailed(url, "video metadata");
            return meta;
        }

        private static ProcessStartInfo BuildBaseStartInfo() => new()
        {
            FileName = Plugin.Instance?.Configuration.YtDlpPath ?? "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        private static void ApplyCookies(ProcessStartInfo startInfo, CookieLease cookies)
        {
            if (cookies.Path is not null)
            {
                startInfo.ArgumentList.Add("--cookies");
                startInfo.ArgumentList.Add(cookies.Path);
            }
        }

        private static void ApplyJsRuntime(ProcessStartInfo startInfo)
        {
            var js = Plugin.Instance?.Configuration.JsRuntime;
            if (!string.IsNullOrWhiteSpace(js))
            {
                startInfo.ArgumentList.Add("--js-runtimes");
                startInfo.ArgumentList.Add(js);
            }
        }

        /// <summary>Runs a yt-dlp process and returns stdout, or null on non-zero exit.</summary>
        private async Task<string?> RunProcessAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = startInfo };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException($"yt-dlp timed out after {timeout.TotalSeconds:0}s");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                LogResolveFailed(startInfo.ArgumentList[^1], process.ExitCode, stderr.Trim());
                return null;
            }

            return stdout;
        }

        private void TryKill(Process process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (SystemException ex) { LogKillFailed(ex); }
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "Resolved {PageUrl} in {ElapsedMs} ms (manifest: {HasManifest})")]
        private partial void LogResolved(string pageUrl, bool hasManifest, long elapsedMs);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error,
            Message = "yt-dlp failed on {PageUrl} exit={ExitCode}: {StdErr}")]
        private partial void LogResolveFailed(string pageUrl, int exitCode, string stdErr);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Could not kill yt-dlp process after timeout")]
        private partial void LogKillFailed(Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
            Message = "yt-dlp output for {Url} ({Mode}) could not be parsed as JSON")]
        private partial void LogParseFailed(string url, string mode);
    }
}

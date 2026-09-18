using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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

            var startInfo = new ProcessStartInfo
            {
                FileName = Plugin.Instance?.Configuration.YtDlpPath ?? "yt-dlp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var cookies = await _cookies.AcquireAsync(cancellationToken).ConfigureAwait(false);
            if (cookies.Path is not null)
            {
                startInfo.ArgumentList.Add("--cookies");
                startInfo.ArgumentList.Add(cookies.Path);
            }

            var jsRuntime = Plugin.Instance?.Configuration.JsRuntime;
            if (!string.IsNullOrWhiteSpace(jsRuntime))
            {
                startInfo.ArgumentList.Add("--js-runtimes");
                startInfo.ArgumentList.Add(jsRuntime);
            }

            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(format);
            startInfo.ArgumentList.Add("--print");
            startInfo.ArgumentList.Add("%(manifest_url)s");
            startInfo.ArgumentList.Add("--print");
            startInfo.ArgumentList.Add("%(url)s");
            startInfo.ArgumentList.Add(pageUrl);

            using var process = new Process { StartInfo = startInfo };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            var sw = Stopwatch.StartNew();
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            if (process.ExitCode != 0)
            {
                LogResolveFailed(pageUrl, process.ExitCode, stderr.Trim());
                return null;
            }

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
    }
}

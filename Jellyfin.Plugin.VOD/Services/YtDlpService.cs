using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services.Sync;
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
            => (await GetMetadataAsync(url, cancellationToken).ConfigureAwait(false)).Metadata;

        /// <summary>
        /// Runs the same call as <see cref="GetVideoMetadataAsync"/> and additionally says WHY no
        /// metadata came back.
        /// </summary>
        /// <param name="url">The video or channel page URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The metadata, or the reason there is none.</returns>
        /// <remarks>
        /// A channel that is off air has no metadata to return — yt-dlp exits 1 with "The channel is
        /// not currently live" and prints no JSON, and <c>--ignore-no-formats-error</c> does not
        /// suppress it. Callers that must tell that apart from a rate limit or a network failure
        /// need this overload: the live probe concludes "nothing is broadcasting" from the former
        /// and refuses to conclude anything from the latter.
        /// </remarks>
        public async Task<MetadataResult> GetMetadataAsync(
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
            // A scheduled live has no formats, and yt-dlp exits non-zero on it without this flag, so
            // its metadata would never arrive. Measured 2026-10-03 (spec F14).
            startInfo.ArgumentList.Add("--ignore-no-formats-error");
            startInfo.ArgumentList.Add(url);

            var run = await RunProcessRawAsync(startInfo, Timeout, cancellationToken)
                .ConfigureAwait(false);

            if (run.ExitCode != 0)
            {
                if (IndicatesChannelOffAir(run.StdErr))
                {
                    LogChannelOffAir(url);
                    return MetadataResult.ChannelOffAir;
                }

                LogResolveFailed(url, run.ExitCode, run.StdErr.Trim());
                return MetadataResult.Failed;
            }

            var meta = VideoMetadata.ParseJson(run.Stdout);
            if (meta is null)
            {
                LogParseFailed(url, "video metadata");
                return MetadataResult.Failed;
            }

            if (meta.IsUnavailable)
            {
                LogUnavailable(url);
                return MetadataResult.Failed;
            }

            return MetadataResult.From(meta);
        }

        /// <summary>
        /// Whether yt-dlp's standard error says the channel is simply not broadcasting.
        /// </summary>
        /// <param name="stderr">Everything yt-dlp wrote to standard error.</param>
        /// <returns><see langword="true"/> only for yt-dlp's own not-live wording.</returns>
        /// <remarks>
        /// Deliberately one phrase: the message yt-dlp's <c>UserNotLive</c> carries, measured on
        /// 2026-10-04 as "ERROR: [twitch:stream] riotgames: The channel is not currently live".
        /// Anything else stays a failure. Widening this to "a non-zero exit means absent" would let
        /// a rate-limited call retract a broadcast that is still running, and the probe deletes
        /// library items on that conclusion.
        /// </remarks>
        internal static bool IndicatesChannelOffAir(string? stderr)
            => stderr is not null
               && stderr.Contains(NotLiveSignal, StringComparison.OrdinalIgnoreCase);

        // yt-dlp's UserNotLive message, verbatim.
        private const string NotLiveSignal = "The channel is not currently live";

        /// <summary>
        /// Runs <c>yt-dlp -J --playlist-items 1</c> and returns the first entry's full metadata.
        /// Returns <see langword="null"/> when yt-dlp exits non-zero or the output cannot be parsed.
        /// </summary>
        /// <param name="url">A playlist or channel-tab URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// Deliberately not <c>--flat-playlist</c>: a flat Twitch listing carries no live status, so
        /// one full extraction of the head answers "is this channel broadcasting, and what is the
        /// video" in a single call. Measured at 2.1 s (spec F4).
        /// </remarks>
        public async Task<VideoMetadata?> GetFirstEntryMetadataAsync(
            string url,
            CancellationToken cancellationToken)
        {
            var startInfo = BuildBaseStartInfo();
            using var cookies = await _cookies.AcquireAsync(cancellationToken).ConfigureAwait(false);
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo);

            startInfo.ArgumentList.Add("-J");
            startInfo.ArgumentList.Add("--playlist-items");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--ignore-no-formats-error");
            startInfo.ArgumentList.Add(url);

            var stdout = await RunProcessAsync(startInfo, PlaylistTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (stdout is null)
                return null;

            var meta = VideoMetadata.ParseFirstEntryJson(stdout);
            if (meta is null)
            {
                LogParseFailed(url, "first playlist entry");
                return null;
            }

            if (meta.IsUnavailable)
            {
                LogUnavailable(url);
                return null;
            }

            return meta;
        }

        /// <summary>
        /// Probes a candidate source URL and reports what the configuration page needs to show.
        /// </summary>
        /// <remarks>
        /// Enumerates a single entry: enough to prove yt-dlp can read the source and to pick up its
        /// title, description and artwork, without paying for a full channel listing every time an
        /// administrator pastes a URL.
        ///
        /// Note for the caller: this inherits GetPlaylistEntriesAsync's ten-minute PlaylistTimeout.
        /// A cold channel behind a JS runtime can genuinely take minutes, so a UI must not block on
        /// it behind a modal with no way out.
        /// </remarks>
        /// <param name="url">The channel, playlist or video URL to inspect.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The source description, or <see langword="null"/> when yt-dlp could not read the URL.
        /// </returns>
        public async Task<SourceInfo?> GetSourceInfoAsync(
            string url,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);

            var playlist = await GetPlaylistEntriesAsync(url, 1, null, cancellationToken)
                .ConfigureAwait(false);

            if (playlist is null)
                return null;

            return new SourceInfo(
                SeriesNaming.Resolve(url, playlist),
                playlist.Description,
                playlist.ThumbnailUrl,
                SourceTypeDetector.Detect(url).ToString(),
                playlist.Entries.Count > 0);
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

        /// <summary>What one yt-dlp process left behind.</summary>
        private readonly record struct ProcessRun(int ExitCode, string Stdout, string StdErr);

        /// <summary>Runs a yt-dlp process and returns stdout, or null on non-zero exit.</summary>
        private async Task<string?> RunProcessAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var run = await RunProcessRawAsync(startInfo, timeout, cancellationToken)
                .ConfigureAwait(false);

            if (run.ExitCode != 0)
            {
                LogResolveFailed(startInfo.ArgumentList[^1], run.ExitCode, run.StdErr.Trim());
                return null;
            }

            return run.Stdout;
        }

        /// <summary>
        /// Runs a yt-dlp process and hands back its exit code and both streams, so a caller that
        /// can read meaning into a specific failure gets the chance to.
        /// </summary>
        private async Task<ProcessRun> RunProcessRawAsync(
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

            return new ProcessRun(process.ExitCode, stdout, stderr);
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

        [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
            Message = "yt-dlp reports {Url} is not broadcasting")]
        private partial void LogChannelOffAir(string url);

        [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
            Message = "yt-dlp lists no format for {Url}: the video is private, removed or otherwise unavailable")]
        private partial void LogUnavailable(string url);
    }
}

using System;
using System.Collections.Generic;
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
    public sealed partial class YtDlpService : IYtDlpService
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PlaylistTimeout = TimeSpan.FromMinutes(10);

        private readonly CookieJar _cookies;
        private readonly YtDlpThrottle _throttle;
        private readonly ILogger<YtDlpService> _logger;

        /// <summary>Initializes a new instance of the <see cref="YtDlpService"/> class.</summary>
        /// <param name="cookies">The cookie jar, one working copy per lane.</param>
        /// <param name="throttle">Paces background calls, per platform.</param>
        /// <param name="logger">Logger instance.</param>
        public YtDlpService(CookieJar cookies, YtDlpThrottle throttle, ILogger<YtDlpService> logger)
        {
            _cookies = cookies;
            _throttle = throttle;
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
            using var cookies = await _cookies.AcquireAsync(PlatformLane.Playback, cancellationToken).ConfigureAwait(false);
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
        /// <param name="lane">
        /// The throttle lane to spend, when it is not the URL's own platform — playback uses
        /// <see cref="PlatformLane.Playback"/> so a started video cannot queue behind a
        /// platform's backoff.
        /// </param>
        /// <param name="metadataLanguage">
        /// A BCP-47 prefix pinned as <c>--extractor-args "youtube:lang=XX"</c>, or null to pass
        /// nothing.
        /// </param>
        public async Task<PlaylistInfo?> GetPlaylistEntriesAsync(
            string url,
            int? maxEntries,
            DateTimeOffset? dateAfter,
            CancellationToken cancellationToken,
            string? lane = null,
            string? metadataLanguage = null)
        {
            lane ??= PlatformLane.For(url);

            // The lease is taken before the cookie file, so the pacing wait happens outside the
            // lane's cookie lock rather than holding it for three seconds.
            using var lease = await _throttle
                .EnterAsync(lane, YtDlpCallKind.Listing, cancellationToken)
                .ConfigureAwait(false);

            var startInfo = BuildBaseStartInfo();
            using var cookies = await _cookies.AcquireAsync(lane, cancellationToken).ConfigureAwait(false);
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo);

            foreach (var argument in BuildPlaylistArguments(
                url, maxEntries, dateAfter, lane, metadataLanguage))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var stdout = await RunProcessAsync(startInfo, PlaylistTimeout, cancellationToken, lane, lease)
                .ConfigureAwait(false);
            if (stdout is null)
                return null;

            var info = PlaylistInfo.ParseJson(stdout);
            if (info is null)
                LogParseFailed(url, "flat-playlist");

            // One process, several requests: a listing is charged for the pages it really
            // fetched, not for one call.
            if (info is not null)
            {
                lease.Charge(YtDlpThrottle.ExtraListingCalls(info.Entries.Count));
            }

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
            var lane = PlatformLane.For(url);
            using var lease = await _throttle
                .EnterAsync(lane, YtDlpCallKind.Resolution, cancellationToken)
                .ConfigureAwait(false);

            var startInfo = BuildBaseStartInfo();
            using var cookies = await _cookies.AcquireAsync(lane, cancellationToken).ConfigureAwait(false);
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo);

            startInfo.ArgumentList.Add("-J");
            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("--no-warnings");
            // A scheduled live has no formats, and yt-dlp exits non-zero on it without this flag, so
            // its metadata would never arrive. Measured 2026-10-03 (spec F14).
            startInfo.ArgumentList.Add("--ignore-no-formats-error");
            startInfo.ArgumentList.Add(url);

            var run = await RunProcessRawAsync(startInfo, Timeout, cancellationToken, lane, lease)
                .ConfigureAwait(false);

            var result = Interpret(run.ExitCode, run.Stdout, run.StdErr, run.TimedOut);

            switch (result.Outcome)
            {
                case MetadataOutcome.ChannelOffAir:
                    LogChannelOffAir(url);
                    break;
                case MetadataOutcome.Failed when run.ExitCode != 0:
                    LogResolveFailed(url, run.ExitCode, run.StdErr.Trim());
                    break;
                case MetadataOutcome.Failed when result.Metadata is null:
                    LogParseFailed(url, "video metadata");
                    break;
                case MetadataOutcome.Failed:
                    LogUnavailable(url);
                    break;
                default:
                    break;
            }

            return result;
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
            var lane = PlatformLane.For(url);
            using var lease = await _throttle
                .EnterAsync(lane, YtDlpCallKind.Resolution, cancellationToken)
                .ConfigureAwait(false);

            var startInfo = BuildBaseStartInfo();
            using var cookies = await _cookies.AcquireAsync(lane, cancellationToken).ConfigureAwait(false);
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo);

            startInfo.ArgumentList.Add("-J");
            startInfo.ArgumentList.Add("--playlist-items");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--ignore-no-formats-error");
            startInfo.ArgumentList.Add(url);

            var stdout = await RunProcessAsync(startInfo, PlaylistTimeout, cancellationToken, lane, lease)
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
        /// Turns a finished yt-dlp run into a verdict, as a pure function of its outputs.
        /// </summary>
        /// <remarks>
        /// Extracted so the verdict is testable without running a process. It is also where the
        /// reason for a failure survives: the previous code returned the shared
        /// <c>MetadataResult.Failed</c> singleton, which carried neither stderr nor the parsed
        /// metadata — so <c>availability</c> never reached a caller, and 14 of 16 failing videos
        /// on the test instance recorded nothing but the word "Failed".
        /// </remarks>
        /// <param name="exitCode">yt-dlp's exit code; -1 when it had to be killed.</param>
        /// <param name="stdout">Everything it wrote to standard output.</param>
        /// <param name="stderr">Everything it wrote to standard error.</param>
        /// <param name="timedOut">Whether it had to be killed for taking too long.</param>
        /// <returns>The verdict, carrying stderr and whatever metadata parsed.</returns>
        internal static MetadataResult Interpret(
            int exitCode, string stdout, string stderr, bool timedOut)
        {
            if (timedOut)
                return new MetadataResult(MetadataOutcome.Failed, null, stderr, TimedOut: true);

            if (exitCode != 0)
            {
                return IndicatesChannelOffAir(stderr)
                    ? new MetadataResult(MetadataOutcome.ChannelOffAir, null, stderr)
                    : new MetadataResult(MetadataOutcome.Failed, null, stderr);
            }

            var meta = VideoMetadata.ParseJson(stdout);
            if (meta is null)
                return new MetadataResult(MetadataOutcome.Failed, null, stderr);

            // The metadata is KEPT on this failure path. It holds `availability`, which is the
            // only thing that distinguishes an age-restricted video from a deleted one.
            return meta.IsUnavailable
                ? new MetadataResult(MetadataOutcome.Failed, meta, stderr)
                : new MetadataResult(MetadataOutcome.Succeeded, meta, stderr);
        }

        /// <summary>
        /// The arguments a flat-playlist listing needs, as a pure function of its inputs.
        /// </summary>
        /// <remarks>
        /// Extracted so it can be tested directly. The alternative — faking the process — would put
        /// test scaffolding in production code to verify something that is a function of its
        /// arguments and nothing else.
        /// </remarks>
        /// <param name="url">The playlist or channel URL, passed last as yt-dlp expects.</param>
        /// <param name="maxEntries">
        /// The cap, or null for no cap. Null is NOT the same as zero: a source configured with
        /// <c>MaxVideosPerSource = 0</c> means unlimited, and passing <c>--playlist-end 0</c> would
        /// fetch nothing at all. Callers map zero to null.
        /// </param>
        /// <param name="dateAfter">When set, passes <c>--dateafter YYYYMMDD</c>.</param>
        /// <param name="lane">The platform lane, which decides whether a language may be pinned.</param>
        /// <param name="metadataLanguage">
        /// A BCP-47 prefix pinned as <c>--extractor-args "youtube:lang=XX"</c>, or null.
        /// <para>
        /// Measured on 2026-10-19: a flat listing serves the LOCALIZED title and honours this
        /// argument, while a single-video metadata call serves the ORIGINAL and ignores it. Pinning
        /// it makes a change detector's fingerprint deterministic — left unpinned, the language
        /// served depends on the session and drifts, which would mark every video as changed on
        /// every pass.
        /// </para>
        /// <para>
        /// Applied on the YouTube lane only: it is a YouTube extractor argument, and this project
        /// never carries one platform's rule to another.
        /// </para>
        /// </param>
        /// <returns>The arguments, in the order yt-dlp receives them.</returns>
        internal static IReadOnlyList<string> BuildPlaylistArguments(
            string url,
            int? maxEntries,
            DateTimeOffset? dateAfter,
            string lane,
            string? metadataLanguage)
        {
            var args = new List<string> { "--flat-playlist", "-J" };

            if (maxEntries.HasValue)
            {
                args.Add("--playlist-end");
                args.Add(maxEntries.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (dateAfter.HasValue)
            {
                args.Add("--dateafter");
                args.Add(dateAfter.Value.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(metadataLanguage)
                && string.Equals(lane, PlatformLane.Youtube, StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--extractor-args");
                args.Add($"youtube:lang={metadataLanguage}");
            }

            args.Add("--no-warnings");
            args.Add(url);
            return args;
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
        private readonly record struct ProcessRun(
            int ExitCode, string Stdout, string StdErr, bool TimedOut = false);

        /// <summary>Runs a yt-dlp process and returns stdout, or null on non-zero exit.</summary>
        private async Task<string?> RunProcessAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string? lane = null,
            YtDlpLease? lease = null)
        {
            var run = await RunProcessRawAsync(startInfo, timeout, cancellationToken, lane, lease)
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
        /// <remarks>
        /// The throttle's verdict is settled here rather than at each call site: a timeout is
        /// <b>returned</b> from this method, not thrown — a rate-limited session stops answering
        /// rather than saying so, which makes the timeout itself the signal — and
        /// <see cref="RunProcessAsync"/> does not hand stderr back to its callers, so two of the
        /// four background entry points could not judge their own outcome. One place knows
        /// "exit code, stderr, timeout" and reports it.
        /// </remarks>
        private async Task<ProcessRun> RunProcessRawAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string? lane = null,
            YtDlpLease? lease = null)
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

                // F9: a rate-limited session stops answering rather than saying so, so a timeout
                // is a refusal on every platform and backs its lane off.
                lease?.RateLimited();

                // Returned, not thrown. A timeout is the single most informative failure this
                // service can report — it is how a rate limit actually manifests — and an
                // exception reduced it to a message string that callers folded into a generic
                // failure. The caller now sees the flag and can classify it.
                return new ProcessRun(-1, string.Empty, string.Empty, TimedOut: true);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (lease is not null)
            {
                if (lane is not null && PlatformLimits.IsPlatformRefusal(lane, stderr, timedOut: false))
                {
                    lease.RateLimited();
                }
                else
                {
                    lease.Succeeded();
                }
            }

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

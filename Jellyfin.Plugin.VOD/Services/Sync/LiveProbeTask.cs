using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Scheduled task that polls VOD sources for a broadcast in progress.
    /// </summary>
    /// <remarks>
    /// The sync cannot find a live in time: it runs per source at
    /// <see cref="Configuration.SourceDefinition.ListingIntervalMinutes"/>, 720 minutes by default,
    /// and the sync task itself only wakes once an hour. This task polls every five minutes by
    /// default and skips sources whose platform cannot answer the question, so no yt-dlp call
    /// is spent learning nothing.
    /// </remarks>
    public sealed partial class LiveProbeTask : IScheduledTask
    {
        private readonly LiveProbeService _probe;
        private readonly ILogger<LiveProbeTask> _logger;

        // In-memory timestamp of the last probe per source URL.
        // Losing it on a restart is intentional: the cost is one extra probe pass, which is
        // exactly what the task does every few minutes anyway, and writing to the database would
        // overwrite LastSyncedAt — a field the sync task owns.
        private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastProbedAt =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initializes a new instance of the <see cref="LiveProbeTask"/> class.</summary>
        public LiveProbeTask(LiveProbeService probe, ILogger<LiveProbeTask> logger)
        {
            _probe = probe;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "VOD Live Probe";

        /// <inheritdoc />
        public string Key => "VodLiveProbe";

        /// <inheritdoc />
        public string Description =>
            "Polls VOD sources for a broadcast in progress, so a live appears without waiting for the next sync.";

        /// <inheritdoc />
        public string Category => "VOD";

        /// <summary>
        /// The fixed wake interval. Jellyfin persists a task's triggers to a file on first load and
        /// never reads <see cref="GetDefaultTriggers"/> again, so a config-derived trigger would
        /// freeze at whatever the setting was on first load. The per-source throttle inside
        /// <see cref="ExecuteAsync"/> enforces the user-configured interval instead.
        /// </summary>
        private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(5);

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = WakeInterval.Ticks
            }
        ];

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
                return;

            if (config.LiveProbeIntervalMinutes <= 0)
                return;

            var sources = config.Sources.FindAll(s =>
                s.Enabled && PlatformRules.SupportsLiveProbe(s.Url));

            if (sources.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var now = DateTimeOffset.UtcNow;

            for (var i = 0; i < sources.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report((double)i / sources.Count * 100);

                _lastProbedAt.TryGetValue(sources[i].Url, out var lastProbedAt);
                if (ShouldSkip(lastProbedAt, config.LiveProbeIntervalMinutes, now))
                {
                    LogSkippingSource(sources[i].Url, lastProbedAt, config.LiveProbeIntervalMinutes);
                    continue;
                }

                LogProbingSource(i + 1, sources.Count, sources[i].Url);

                try
                {
                    await _probe.ProbeSourceAsync(config, sources[i], cancellationToken)
                        .ConfigureAwait(false);
                    _lastProbedAt[sources[i].Url] = now;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One failing source must not prevent the others from being probed.
                    LogProbeFailed(sources[i].Url, ex);
                }
            }

            progress.Report(100);
        }

        /// <summary>
        /// Whether a source should be skipped because it was probed more recently than
        /// <paramref name="intervalMinutes"/> ago.
        /// </summary>
        /// <param name="lastProbedAt">
        /// The UTC time of the last probe. <c>default</c> for a source never probed in this session,
        /// which is what the dictionary lookup yields and which no interval can be within.
        /// </param>
        /// <param name="intervalMinutes">The configured probe interval. Zero or less means disabled.</param>
        /// <param name="now">The reference time (caller supplies it so the test can control it).</param>
        /// <returns>
        /// True when the source was probed within the interval and should be skipped this pass.
        /// </returns>
        internal static bool ShouldSkip(
            DateTimeOffset lastProbedAt, int intervalMinutes, DateTimeOffset now)
            => intervalMinutes > 0
               && (now - lastProbedAt) < TimeSpan.FromMinutes(intervalMinutes);

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "LiveProbeTask: probing source {Index}/{Total}: {Url}")]
        private partial void LogProbingSource(int index, int total, string url);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error,
            Message = "LiveProbeTask: probe failed for {Url}")]
        private partial void LogProbeFailed(string url, Exception exception);

        [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
            Message = "LiveProbeTask: skipping source {Url} — last probed {LastProbedAt}, interval {IntervalMinutes} min")]
        private partial void LogSkippingSource(string url, DateTimeOffset lastProbedAt, int intervalMinutes);
    }
}

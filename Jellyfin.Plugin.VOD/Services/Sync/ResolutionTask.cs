using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>What the resolution task needs, and nothing more.</summary>
    /// <remarks>
    /// A seam, so the task's own logic — how many waves to launch, whether to requeue, never
    /// failing the pass — is testable without standing up a database, a publisher and a yt-dlp
    /// service. <see cref="ResolutionRunner"/> is the one production implementation.
    /// </remarks>
    public interface IVodResolutionRunner
    {
        /// <summary>The platforms that have at least one video due right now.</summary>
        /// <param name="config">The current configuration.</param>
        /// <returns>Their extractor keys; the empty string is the unknown-platform lane.</returns>
        IReadOnlyList<string> PlatformsWithWork(PluginConfiguration config);

        /// <summary>Resolves one wave for one platform.</summary>
        /// <param name="config">The current configuration.</param>
        /// <param name="ieKey">The platform's extractor key.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>How many videos the wave resolved.</returns>
        Task<int> ResolveWaveAsync(PluginConfiguration config, string ieKey, CancellationToken cancellationToken);
    }

    /// <inheritdoc />
    public sealed class ResolutionRunner : IVodResolutionRunner
    {
        private readonly IVodDatabase _db;
        private readonly ResolutionService _service;

        /// <summary>Initializes a new instance of the <see cref="ResolutionRunner"/> class.</summary>
        /// <param name="db">The VOD database.</param>
        /// <param name="service">The service that resolves one wave.</param>
        public ResolutionRunner(IVodDatabase db, ResolutionService service)
        {
            _db = db;
            _service = service;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> PlatformsWithWork(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            return _db.GetPlatformsWithDueVideos(
                DateTimeOffset.UtcNow, Math.Max(1, config.ResolutionRetryMinutes));
        }

        /// <inheritdoc />
        public Task<int> ResolveWaveAsync(
            PluginConfiguration config, string ieKey, CancellationToken cancellationToken)
            => _service.ResolveWaveAsync(config, ieKey, cancellationToken);
    }

    /// <summary>
    /// Scheduled task that resolves the videos an enumeration left pending — one wave per
    /// platform, launched together.
    /// </summary>
    /// <remarks>
    /// Replaces the stream-description task: describing a video's streams is now one step of
    /// resolving it, since the same yt-dlp answer carries the metadata and the HLS master.
    /// </remarks>
    public sealed partial class ResolutionTask : IScheduledTask
    {
        /// <summary>
        /// The fixed wake interval. Jellyfin persists a task's triggers on first load and never
        /// reads <see cref="GetDefaultTriggers"/> again, so a config-derived interval would freeze
        /// at whatever the setting was that first time. The wave size bounds each pass instead, and
        /// the administrator may change the interval in Jellyfin's own task screen.
        /// </summary>
        private static readonly TimeSpan _wakeInterval = TimeSpan.FromMinutes(15);

        private readonly IVodResolutionRunner _runner;
        private readonly ITaskManager _taskManager;
        private readonly ILogger<ResolutionTask> _logger;

        /// <summary>Initializes a new instance of the <see cref="ResolutionTask"/> class.</summary>
        /// <param name="runner">What resolves a wave, and says which platforms have work.</param>
        /// <param name="taskManager">Used to requeue the task while work remains.</param>
        /// <param name="logger">The logger.</param>
        public ResolutionTask(
            IVodResolutionRunner runner, ITaskManager taskManager, ILogger<ResolutionTask> logger)
        {
            _runner = runner;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "VOD Resolution";

        /// <inheritdoc />
        public string Key => "VodResolution";

        /// <inheritdoc />
        public string Description =>
            "Fetches the metadata and streams of videos an enumeration left pending, a wave at a time.";

        /// <inheritdoc />
        public string Category => "VOD";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = _wakeInterval.Ticks
            }
        ];

        /// <inheritdoc />
        /// <remarks>
        /// One wave per platform, launched together and awaited together: the throttle gives each
        /// platform its own lane and its own backoff, so a platform that is refusing calls must not
        /// hold up one that is answering.
        /// <para>
        /// A pass never ends in failure. It is one task execution per wave — about 700 of them for
        /// a large first fill — and Jellyfin writes an activity-log entry for every failed
        /// execution, so one unlucky platform would fill the user's log with alarms about work that
        /// is simply retried.
        /// </para>
        /// </remarks>
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                progress.Report(100);
                return;
            }

            await RunPassAsync(config, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The testable core of <see cref="ExecuteAsync"/>: one wave per platform, in parallel.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="progress">Progress sink; reports progress as lanes finish, 100 on completion.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// Only <see cref="OperationCanceledException"/> escapes this method. Every other exception
        /// — including one thrown by <see cref="IVodResolutionRunner.PlatformsWithWork"/> (a real
        /// database read in production) — is caught, logged as a warning, and the pass ends
        /// reporting 100. A pass that ends in failure writes an activity-log entry; at one execution
        /// per wave, a SQLite error would flood the log with alarms about work that is simply retried.
        /// </remarks>
        internal async Task RunPassAsync(
            PluginConfiguration config,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            try
            {
                var platforms = _runner.PlatformsWithWork(config);
                if (platforms.Count == 0)
                {
                    progress.Report(100);
                    return;
                }

                var resolved = 0;
                var lanesCompleted = 0;
                var laneCount = platforms.Count;

                var waves = platforms.Select(async ieKey =>
                {
                    try
                    {
                        var count = await _runner
                            .ResolveWaveAsync(config, ieKey, cancellationToken)
                            .ConfigureAwait(false);
                        Interlocked.Add(ref resolved, count);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
#pragma warning disable CA1031 // one platform's failure must not take the others down
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogWaveFailed(ex, ieKey);
                    }

                    // Report aggregate progress as each lane finishes — honest at this granularity
                    // since a lane does not report its own sub-progress. Only reached when the lane
                    // completed (successfully or with a handled error); not reached on cancellation.
                    progress.Report((double)Interlocked.Increment(ref lanesCompleted) / laneCount * 100);
                });

                await Task.WhenAll(waves).ConfigureAwait(false);
                progress.Report(100);
                LogPassFinished(resolved, platforms.Count);

                // Requeued only on forward progress (at least one video resolved this pass).
                // Progress is the right gate: a pass that only stamped unconfigured or disabled
                // sources resolved nothing and should not trigger another immediate execution —
                // their rows remain due, but no real work was done, and the 15-minute interval is
                // the correct pacing for that case. A pass that resolved something requeues because
                // the wave size bounds each execution and there may be more to do.
                // QueueScheduledTask enqueues — QueueIfNotRunning, called from inside the task,
                // would see this very execution's Running state and enqueue nothing, leaving a
                // large backlog to drain one wave per quarter of an hour.
                if (resolved > 0)
                {
                    _taskManager.QueueScheduledTask<ResolutionTask>();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // a database failure must not mark the pass as failed
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPassFailed(ex);
                progress.Report(100);
            }
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "ResolutionTask: {Resolved} video(s) resolved across {Platforms} platform(s)")]
        private partial void LogPassFinished(int resolved, int platforms);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
            Message = "Resolution wave for platform {Platform} failed; the others are unaffected")]
        private partial void LogWaveFailed(Exception exception, string platform);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "ResolutionTask: pass failed unexpectedly; work will be retried on the next wake")]
        private partial void LogPassFailed(Exception exception);
    }
}

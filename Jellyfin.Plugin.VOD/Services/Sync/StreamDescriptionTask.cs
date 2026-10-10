using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Scheduled task that describes the media streams of videos whose tiers have none yet.
    /// </summary>
    /// <remarks>
    /// Without it, a tier is only ever described once someone plays it — by which time Jellyfin has
    /// already decided, blind, whether to copy the stream or re-encode it.
    /// </remarks>
    public sealed partial class StreamDescriptionTask : IScheduledTask
    {
        private readonly StreamDescriptionService _service;
        private readonly ILogger<StreamDescriptionTask> _logger;

        /// <summary>
        /// The fixed wake interval. Jellyfin persists a task's triggers to a file on first load and
        /// never reads <see cref="GetDefaultTriggers"/> again, so a config-derived trigger would
        /// freeze at whatever the setting was on first load. The batch size bounds each pass
        /// instead, and the administrator may change the interval in Jellyfin's own task screen.
        /// </summary>
        private static readonly TimeSpan _wakeInterval = TimeSpan.FromMinutes(15);

        /// <summary>Initializes a new instance of the <see cref="StreamDescriptionTask"/> class.</summary>
        /// <param name="service">The service that describes one batch.</param>
        /// <param name="logger">The logger.</param>
        public StreamDescriptionTask(
            StreamDescriptionService service, ILogger<StreamDescriptionTask> logger)
        {
            _service = service;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "VOD Stream Description";

        /// <inheritdoc />
        public string Key => "VodStreamDescription";

        /// <inheritdoc />
        public string Description =>
            "Describes the media streams of quality tiers that have none, so playback is never decided blind.";

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
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            var described = await _service
                .DescribeNextBatchAsync(config, progress, cancellationToken)
                .ConfigureAwait(false);

            LogPassFinished(described);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "StreamDescriptionTask: described {DescribedCount} video(s) this pass")]
        private partial void LogPassFinished(int describedCount);
    }
}

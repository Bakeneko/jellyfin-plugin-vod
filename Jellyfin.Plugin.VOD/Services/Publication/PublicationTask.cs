using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>The scheduled task that runs one publication pass.</summary>
    /// <remarks>
    /// Thin on purpose. Everything that decides what to do — what to list, what is due, when to
    /// stop — belongs to the pass and its steps; the task only supplies the configuration, the
    /// clock and the progress reporter, and lets what the pass throws reach Jellyfin.
    /// </remarks>
    public sealed partial class PublicationTask : IScheduledTask
    {
        private readonly IPublicationPass _pass;
        private readonly Func<PluginConfiguration?> _configuration;
        private readonly ILogger<PublicationTask> _logger;

        /// <summary>Initializes a new instance of the <see cref="PublicationTask"/> class.</summary>
        /// <param name="pass">The publication pass.</param>
        /// <param name="configuration">
        /// Where to read the current configuration. A delegate rather than the configuration
        /// itself: a scheduled task is a singleton that outlives any number of administrator
        /// edits, so a value captured at construction would freeze on the settings the server
        /// started with.
        /// </param>
        /// <param name="logger">Logger.</param>
        public PublicationTask(
            IPublicationPass pass,
            Func<PluginConfiguration?> configuration,
            ILogger<PublicationTask> logger)
        {
            _pass = pass;
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "VOD Listing & publication";

        /// <inheritdoc />
        public string Key => "VodListingAndPublication";

        /// <inheritdoc />
        public string Description =>
            "Lists the configured VOD sources, fetches what is due, and publishes it to the library.";

        /// <inheritdoc />
        public string Category => "VOD";

        /// <summary>How often the task wakes.</summary>
        /// <remarks>
        /// Short, because a wake-up that finds nothing costs one database query per source and no
        /// platform call: the work list is also the stop test, so an idle pass is a single turn.
        /// The background work is done by the loop INSIDE one pass, not by waking often — a first
        /// fill is hours long and runs to completion within a single execution. What this interval
        /// buys is latency: how long a newly configured source, or a video the listing has just
        /// discovered, waits before anything happens.
        /// <para>
        /// Jellyfin persists a task's triggers on first load and never reads
        /// <see cref="GetDefaultTriggers"/> again, so this cannot be made configurable here; it
        /// would freeze at whatever the setting was the first time the plugin loaded.
        /// </para>
        /// </remarks>
        private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(15);

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = WakeInterval.Ticks,
            }
        ];

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = _configuration();
            if (config is null)
            {
                LogNoConfiguration();
                return;
            }

            var report = await _pass
                .RunPassAsync(config, progress, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            // A pass that ends with a lane refused did not finish its work, and the only place that
            // is visible is here: the report carries it, the database does not.
            if (report.RefusedLanes.Count > 0)
                LogEndedWithRefusedLanes(string.Join(", ", report.RefusedLanes));
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
            Message = "VOD publication task: the plugin has no configuration yet, so nothing ran")]
        private partial void LogNoConfiguration();

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
            Message = "VOD publication task: the pass ended with work still left on lane(s) "
                + "{Lanes}; the next run of this task is the retry")]
        private partial void LogEndedWithRefusedLanes(string lanes);
    }
}

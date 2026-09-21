using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>Scheduled task that syncs all enabled VOD sources.</summary>
    public sealed partial class SyncTask : IScheduledTask
    {
        private readonly SyncService _syncService;
        private readonly IVodDatabase _db;
        private readonly LibraryWriter _library;
        private readonly ILogger<SyncTask> _logger;

        /// <summary>Initializes a new instance of the <see cref="SyncTask"/> class.</summary>
        public SyncTask(SyncService syncService, IVodDatabase db, LibraryWriter library, ILogger<SyncTask> logger)
        {
            _syncService = syncService;
            _db = db;
            _library = library;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "VOD Sync";

        /// <inheritdoc />
        public string Key => "VodSync";

        /// <inheritdoc />
        public string Description => "Syncs all enabled VOD sources and writes .strm files to the library.";

        /// <inheritdoc />
        public string Category => "VOD";

        /// <summary>
        /// How often the task wakes up. Each source then syncs at most once per its own
        /// <see cref="Configuration.SourceDefinition.SyncIntervalMinutes"/>, so this only bounds
        /// how late a source can be past its due time.
        /// </summary>
        private static readonly TimeSpan WakeInterval = TimeSpan.FromHours(1);

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

            var sources = config.Sources.FindAll(s => s.Enabled);
            if (sources.Count == 0)
            {
                progress.Report(100);
                return;
            }

            for (var i = 0; i < sources.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report((double)i / sources.Count * 100);

                var sourceRecord = _db.GetSource(sources[i].Url);
                if (sources[i].SyncIntervalMinutes > 0
                    && sourceRecord?.LastSyncedAt is not null
                    && (DateTimeOffset.UtcNow - sourceRecord.LastSyncedAt.Value) < TimeSpan.FromMinutes(sources[i].SyncIntervalMinutes))
                {
                    LogSkippingSource(sources[i].Url, sourceRecord.LastSyncedAt.Value, sources[i].SyncIntervalMinutes);
                    continue;
                }

                LogSyncingSource(i + 1, sources.Count, sources[i].Url);

                await _syncService.SyncSourceAsync(sources[i], cancellationToken)
                    .ConfigureAwait(false);
            }

            // Reconcile: delete .strm files on disk that have no DB record
            if (!string.IsNullOrWhiteSpace(config.LibraryBasePath))
            {
                var knownPaths = new HashSet<string>(
                    _db.GetAllStrmPaths().Select(Path.GetFullPath),
                    StringComparer.OrdinalIgnoreCase);
                _library.ReconcileLibrary(config.LibraryBasePath, knownPaths);
            }

            progress.Report(100);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "SyncTask: syncing source {Index}/{Total}: {Url}")]
        private partial void LogSyncingSource(int index, int total, string url);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "SyncTask: skipping source {Url} — last synced {LastSyncedAt}, interval {IntervalMinutes} min")]
        private partial void LogSkippingSource(string url, DateTimeOffset lastSyncedAt, int intervalMinutes);
    }
}

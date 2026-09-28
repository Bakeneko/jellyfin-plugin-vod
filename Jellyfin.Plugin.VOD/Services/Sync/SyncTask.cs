using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>Scheduled task that syncs all enabled VOD sources.</summary>
    public sealed partial class SyncTask : IScheduledTask
    {
        private readonly SyncService _syncService;
        private readonly IVodDatabase _db;
        private readonly ILogger<SyncTask> _logger;

        /// <summary>Initializes a new instance of the <see cref="SyncTask"/> class.</summary>
        public SyncTask(SyncService syncService, IVodDatabase db, ILogger<SyncTask> logger)
        {
            _syncService = syncService;
            _db = db;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "VOD Sync";

        /// <inheritdoc />
        public string Key => "VodSync";

        /// <inheritdoc />
        public string Description => "Syncs all enabled VOD sources into the Jellyfin library.";

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

            // Before retirement and before the loop, because both need the anchor and neither can do
            // anything without it. Failing here rather than inside the loop is what keeps a broken
            // library from costing one yt-dlp enumeration per source on every single wake-up.
            try
            {
                await _syncService.PrepareLibraryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                LogLibraryUnavailable(config.LibraryName, ex.Message);

                // Recorded against each source so the configuration page can show why nothing synced.
                foreach (var source in sources)
                {
                    var record = _db.GetSource(source.Url) ?? new SourceRecord { Url = source.Url };
                    record.LastError = ex.Message;
                    _db.UpsertSource(record);
                }

                progress.Report(100);
                return;
            }

            // Before the early return below: a source removed from the configuration is never visited
            // by the loop that follows, since that loop only ever reads the configuration. Retiring
            // has to be driven from the database instead — and emptying the source list entirely is
            // exactly the case where it matters most.
            await _syncService.RetireRemovedSourcesAsync(cancellationToken).ConfigureAwait(false);

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

            progress.Report(100);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "SyncTask: syncing source {Index}/{Total}: {Url}")]
        private partial void LogSyncingSource(int index, int total, string url);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "SyncTask: skipping source {Url} — last synced {LastSyncedAt}, interval {IntervalMinutes} min")]
        private partial void LogSkippingSource(string url, DateTimeOffset lastSyncedAt, int intervalMinutes);

        [LoggerMessage(EventId = 3, Level = LogLevel.Error,
            Message = "SyncTask: nothing synced — library {LibraryName} cannot hold published items: {Reason}")]
        private partial void LogLibraryUnavailable(string libraryName, string reason);
    }
}

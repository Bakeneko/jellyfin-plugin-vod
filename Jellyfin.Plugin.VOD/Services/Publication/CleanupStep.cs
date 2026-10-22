using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>What one cleanup turn produced.</summary>
    /// <param name="Outcome">How the step ended overall.</param>
    /// <param name="Detail">Why, in plain text, or null on a clean success.</param>
    /// <param name="VideosRemoved">Videos deleted from the database (absent from the last full listing).</param>
    /// <param name="SourcesRetired">Sources fully purged from the library and removed from the database.</param>
    public sealed record CleanupOutcome(
        CallOutcome Outcome,
        string? Detail,
        int VideosRemoved,
        int SourcesRetired);

    /// <summary>
    /// Removes what left the configuration or the source, before the wave runs on what remains.
    /// Runs early so the wave does not spend yt-dlp calls on content about to be discarded.
    /// </summary>
    /// <remarks>
    /// This is the most dangerous step in a pass: it deletes Jellyfin items, and a deleted item
    /// takes its watch history with it. Every safeguard in this class is load-bearing.
    /// </remarks>
    public sealed partial class CleanupStep
    {
        private readonly IVodDatabase _db;
        private readonly LibraryPublisher _publisher;
        private readonly ILogger<CleanupStep> _logger;

        /// <summary>Initializes a new instance of the <see cref="CleanupStep"/> class.</summary>
        /// <param name="db">The VOD database.</param>
        /// <param name="publisher">The library publisher used to purge Jellyfin items.</param>
        /// <param name="logger">Logger.</param>
        public CleanupStep(IVodDatabase db, LibraryPublisher publisher, ILogger<CleanupStep> logger)
        {
            _db = db;
            _publisher = publisher;
            _logger = logger;
        }

        /// <summary>
        /// Purges every source that has left the plugin configuration and removes its database row.
        /// </summary>
        /// <param name="config">The current plugin configuration. Defines which URLs are still wanted.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>How many sources were retired.</returns>
        /// <remarks>
        /// Global: runs once per turn, not per source, so the wave does not spend yt-dlp calls on
        /// sources about to be discarded.
        /// <para>
        /// There is no <c>SetStageResult</c> call here: there is no source row to attribute the
        /// result to — the source row is what this method deletes. Failures are logged only.
        /// </para>
        /// </remarks>
        public CleanupOutcome RetireSourcesGoneFromTheConfiguration(
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            var configuredUrls = new HashSet<string>(
                config.Sources.Select(s => s.Url),
                StringComparer.OrdinalIgnoreCase);

            var allSources = _db.GetAllSources();
            var retired = 0;
            var anyFailed = false;
            string? lastFailureDetail = null;

            foreach (var record in allSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Disabled sources are still configured — disabling a source must not empty the
                // library.
                if (configuredUrls.Contains(record.Url))
                    continue;

                // Three-step order: compute protected set, purge, then delete the row.
                // The row carries published_anchor_id, which is the only way to find this source's
                // items once its library has left the configuration. Deleting the row before purging
                // would lose that anchor permanently if the purge fails, leaving orphaned items that
                // no later turn can clean up.
                var protectedSet = BuildProtectedSetExcluding(record.Url);
                var anchors = BuildAnchorsFor(record, config);

                try
                {
                    _publisher.PurgeSource(anchors, record, protectedSet);

                    // Delete the row only after a successful purge: if the purge throws, the row
                    // stays so the next turn can retry with the anchor still readable.
                    _db.DeleteSource(record.Url);
                    retired++;
                    LogSourceRetired(record.Url);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed purge must not delete the row: without the anchor there is no way to
                    // find this source's items on the next turn, so partial orphans would survive
                    // indefinitely.
                    LogSourceRetirementFailed(record.Url, ex.Message);
                    anyFailed = true;
                    lastFailureDetail = ex.Message;
                }
            }

            if (anyFailed)
                return new CleanupOutcome(
                    CallOutcome.ExtractorError, lastFailureDetail,
                    VideosRemoved: 0, SourcesRetired: retired);

            return new CleanupOutcome(CallOutcome.Ok, null, VideosRemoved: 0, SourcesRetired: retired);
        }

        /// <summary>
        /// Removes videos that the last full listing did not carry, then records the step result.
        /// </summary>
        /// <param name="source">The source to clean up.</param>
        /// <param name="fullListedAt">
        /// When the full listing that just succeeded ran — the same <c>now</c> the listing used
        /// for <c>TouchListedAt</c>. Passing any other timestamp would make videos the listing just
        /// touched look absent, which would retire the whole source.
        /// Must be called only when <see cref="ListingOutcome.CleanupAllowed"/> is true: a short
        /// listing touches only its 30 entries, so running cleanup on one would retire everything
        /// beyond position 30, deleting most of the source and the watch state attached to it.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>What the step produced.</returns>
        /// <remarks>
        /// Latency: up to <c>FullListingIntervalDays</c> before a deleted-from-platform video is
        /// retired. For Arte (~3 videos/day, cap 100), the database holds at most ~21 videos beyond
        /// the cap between two full listings; they are not published, and the next full listing
        /// retires them.
        /// </remarks>
        public CleanupOutcome RemoveVideosGoneFromTheSource(
            SourceDefinition source, DateTimeOffset fullListedAt, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);

            try
            {
                var gone = _db.GetVideosGoneFromLastFullListing(source.Url, fullListedAt);

                foreach (var videoId in gone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _db.DeleteVideo(videoId);
                }

                _db.SetStageResult(source.Url, SourceStage.Cleanup, CallOutcome.Ok, null);
                LogVideosRetired(source.Url, gone.Count);

                return new CleanupOutcome(CallOutcome.Ok, null, VideosRemoved: gone.Count, SourcesRetired: 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogVideoRetirementFailed(source.Url, ex.Message);
                _db.SetStageResult(source.Url, SourceStage.Cleanup, CallOutcome.ExtractorError, ex.Message);
                return new CleanupOutcome(
                    CallOutcome.ExtractorError, ex.Message, VideosRemoved: 0, SourcesRetired: 0);
            }
        }

        // ── Private helpers ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every video id the database knows, minus the ids belonging to
        /// <paramref name="excludeSourceUrl"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="LibraryPublisher.PurgeSource"/> skips any item whose VOD id appears in this
        /// set. Passing the global set unchanged would protect the retired source's own videos — the
        /// purge would silently delete nothing, leave every item orphaned, and return a plausible
        /// count. Excluding this source's ids is what makes the sweep actually fire.
        /// </remarks>
        private HashSet<string> BuildProtectedSetExcluding(string excludeSourceUrl)
        {
            var allWithMetadata = _db.GetVideoIdsWithMetadata()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var video in _db.GetVideosBySource(excludeSourceUrl))
                allWithMetadata.Remove(video.Id);

            return allWithMetadata;
        }

        /// <summary>
        /// Assembles the Jellyfin folders to sweep when retiring a source that has left the
        /// configuration.
        /// </summary>
        /// <remarks>
        /// <see cref="LibraryPublisher.SweepAnchorsFor"/> cannot be used here: it requires a
        /// <see cref="SourceDefinition"/>, which a retired source no longer has.
        /// <para>
        /// The stored anchor (<see cref="SourceRecord.PublishedAnchorId"/>) covers the one case the
        /// two currently configured libraries cannot: a source whose library has itself since left
        /// the configuration.
        /// </para>
        /// </remarks>
        private List<Folder> BuildAnchorsFor(SourceRecord record, PluginConfiguration config)
        {
            var anchors = new List<Folder>();

            // Stored anchor first: this is the only pointer to items a source published into a
            // library that has since left the configuration.
            if (record.PublishedAnchorId is { } anchorId && anchorId != Guid.Empty)
            {
                if (_publisher.FindAnchorById(anchorId) is Folder stored)
                    anchors.Add(stored);
            }

            // Best effort: the currently configured libraries may still hold items from this source.
            // ResolveTarget throws when a library does not exist — catch per library so one missing
            // library does not block retirement of a source whose items are elsewhere.
            foreach (var libraryName in LibraryRouting.ConfiguredNames(config))
            {
                try
                {
                    anchors.Add(_publisher.ResolveTarget(libraryName).Anchor);
                }
                catch (InvalidOperationException ex)
                {
                    LogLibraryUnavailableForRetirement(libraryName, record.Url, ex.Message);
                }
            }

            // Deduplicate by folder id. PurgeSource already deduplicates, but passing duplicates
            // would obscure that two configured names resolved to the same folder.
            return anchors.GroupBy(a => a.Id).Select(g => g.First()).ToList();
        }

        // ── Log messages ─────────────────────────────────────────────────────────────────────────

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "Retired source {SourceUrl}: purged from library and row deleted")]
        private partial void LogSourceRetired(string sourceUrl);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error,
            Message = "Failed to retire source {SourceUrl}: {Reason}; row kept so the next turn can retry")]
        private partial void LogSourceRetirementFailed(string sourceUrl, string reason);

        [LoggerMessage(EventId = 3, Level = LogLevel.Information,
            Message = "Cleanup for {SourceUrl}: {Count} video(s) retired (absent from last full listing)")]
        private partial void LogVideosRetired(string sourceUrl, int count);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
            Message = "Cleanup for {SourceUrl} failed: {Reason}")]
        private partial void LogVideoRetirementFailed(string sourceUrl, string reason);

        [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
            Message = "Library '{LibraryName}' not available while retiring '{SourceUrl}': {Reason}")]
        private partial void LogLibraryUnavailableForRetirement(
            string libraryName, string sourceUrl, string reason);
    }
}

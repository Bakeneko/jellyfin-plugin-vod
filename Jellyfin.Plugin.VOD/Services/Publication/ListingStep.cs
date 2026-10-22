using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>What one listing turn produced.</summary>
    /// <param name="Outcome">How the step ended overall.</param>
    /// <param name="Detail">Why, in plain text, or null on a clean success.</param>
    /// <param name="Full">True when a full listing was what actually ran (planned or escalated).</param>
    /// <param name="Created">Videos that did not exist in the database before this step.</param>
    /// <param name="Retitled">Videos whose listing title changed, triggering a metadata refetch.</param>
    /// <param name="EntriesSeen">Entries the listing carried, after deduplication.</param>
    /// <param name="CleanupAllowed">
    /// True only after a full listing that succeeded. The cleanup reads this flag.
    /// </param>
    public sealed record ListingOutcome(
        CallOutcome Outcome,
        string? Detail,
        bool Full,
        int Created,
        int Retitled,
        int EntriesSeen,
        bool CleanupAllowed);

    /// <summary>
    /// Lists a source and ingests what it returns: creates new-video rows, refreshes listing
    /// timestamps, writes source metadata, marks retitled videos, and records the step result.
    /// </summary>
    /// <remarks>
    /// This is the first step of every pass turn and the only one that enumerates the platform
    /// (subsequent steps only fetch individual videos). It writes directly to the database and is
    /// not a pure function.
    /// <para>
    /// Every log line emitted by this step carries the source URL, so a multi-source pass log can
    /// be filtered by source — correlation was the gap that 230 MB of logs could not fill.
    /// </para>
    /// </remarks>
    public sealed partial class ListingStep
    {
        private readonly IYtDlpService _ytDlp;
        private readonly IVodDatabase _db;
        private readonly ILogger<ListingStep> _logger;

        /// <summary>Initializes a new instance of <see cref="ListingStep"/>.</summary>
        /// <param name="ytDlp">The yt-dlp seam. Must be substitutable for tests.</param>
        /// <param name="db">The VOD database.</param>
        /// <param name="logger">Logger.</param>
        public ListingStep(IYtDlpService ytDlp, IVodDatabase db, ILogger<ListingStep> logger)
        {
            _ytDlp = ytDlp;
            _db = db;
            _logger = logger;
        }

        /// <summary>Short-listing entry cap: one YouTube page, one HTTP request.</summary>
        private const int ShortListingEntryCount = 30;

        /// <summary>
        /// Below this many stored videos, a full listing's entry count is not second-guessed.
        /// </summary>
        /// <remarks>
        /// Proportions carry no information on a handful of rows: a three-video source dropping to
        /// one is ordinary, and withholding its cleanup would leave deleted videos published for
        /// ever. Ten is where "half of them vanished" starts being a claim worth doubting.
        /// </remarks>
        private const int ShortfallFloor = 10;

        /// <summary>
        /// Enumerates <paramref name="planned"/>'s source, ingests the result, and returns what
        /// happened.
        /// </summary>
        /// <param name="planned">Which source to list, and whether to do a full listing.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="now">The reference time for all clock writes.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What the step produced.</returns>
        public async Task<ListingOutcome> RunAsync(
            PlannedListing planned,
            PluginConfiguration config,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(planned);
            ArgumentNullException.ThrowIfNull(config);

            var sourceUrl = planned.Source.Url;

            // Pass the preferred language only when it is actually configured. The listing path is
            // the only one that serves a localized title; passing it to metadata calls would imply
            // both paths align, which they do not.
            var lang = string.IsNullOrWhiteSpace(config.PreferredMetadataLanguage)
                ? null
                : config.PreferredMetadataLanguage;

            // No date cutoff is EVER passed to a listing, and that is load-bearing rather than an
            // omission. yt-dlp's --dateafter would make the listing stop at the retention horizon,
            // so every video older than it would be absent from a full listing — and absence from
            // a full listing is precisely what the cleanup deletes. Retention would become an
            // erase rule, which it must not be: a deleted row is rediscovered by the next listing
            // and refetched, which is the call loop this rework exists to remove. Retention is a
            // publication filter and nothing else.
            DateTimeOffset? noDateCutoff = null;

            // Load existing videos once: used to detect new vs. known entries, to detect the
            // "zero entries on a non-empty source" outage case, and to find the record to modify on
            // a retitle — all without a query per entry.
            var existing = _db.GetVideosBySource(sourceUrl);
            var existingByNativeId = BuildNativeIdIndex(existing);

            bool full = planned.Full;
            PlaylistInfo? playlist;

            if (!full)
            {
                // Short listing: exactly 30 entries, one YouTube page, one request.
                playlist = await _ytDlp.GetPlaylistEntriesAsync(
                    sourceUrl, ShortListingEntryCount, noDateCutoff, cancellationToken,
                    metadataLanguage: lang).ConfigureAwait(false);

                if (playlist is null)
                    return RecordFailure(sourceUrl, SourceStage.ShortListing, full, "listing call returned no answer");

                if (playlist.Entries.Count == 0)
                    return HandleZeroEntries(sourceUrl, planned.Source, playlist, existing.Count, full, now);

                var deduped = Deduplicate(playlist.Entries);

                // Escalate to a full listing when the short window contains no known video.
                // This guards against a configuration gap so wide that more than 30 videos
                // appeared since the last listing — otherwise every entry would be created,
                // and the ones below position 30 would never be discovered.
                var hasKnown = HasAnyKnown(deduped, existingByNativeId);
                if (!hasKnown)
                {
                    LogEscalating(sourceUrl);
                    full = true;
                    playlist = await _ytDlp.GetPlaylistEntriesAsync(
                        sourceUrl, FullListingMax(planned.Source), noDateCutoff, cancellationToken,
                        metadataLanguage: lang).ConfigureAwait(false);

                    if (playlist is null)
                        return RecordFailure(sourceUrl, SourceStage.FullListing, full: true, "listing call returned no answer after escalation");

                    if (playlist.Entries.Count == 0)
                        return HandleZeroEntries(sourceUrl, planned.Source, playlist, existing.Count, true, now);
                }
            }
            else
            {
                // Full listing: up to MaxVideosPerSource, or unlimited when that value is 0.
                playlist = await _ytDlp.GetPlaylistEntriesAsync(
                    sourceUrl, FullListingMax(planned.Source), noDateCutoff, cancellationToken,
                    metadataLanguage: lang).ConfigureAwait(false);

                if (playlist is null)
                    return RecordFailure(sourceUrl, SourceStage.FullListing, full, "listing call returned no answer");

                if (playlist.Entries.Count == 0)
                    return HandleZeroEntries(sourceUrl, planned.Source, playlist, existing.Count, full, now);
            }

            return Ingest(sourceUrl, planned.Source, playlist, existingByNativeId,
                full, existing.Count, now);
        }

        // ── Private helpers ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the max-entries argument for a full listing: null when <c>MaxVideosPerSource</c>
        /// is 0 (unlimited), otherwise the configured cap. Null is not zero: passing zero to
        /// yt-dlp's <c>--playlist-end</c> would return nothing.
        /// </summary>
        private static int? FullListingMax(SourceDefinition source)
            => source.MaxVideosPerSource == 0 ? null : source.MaxVideosPerSource;

        /// <summary>Builds a case-insensitive native-id → record index for a source's videos.</summary>
        private static Dictionary<string, VideoRecord> BuildNativeIdIndex(IReadOnlyList<VideoRecord> videos)
        {
            var index = new Dictionary<string, VideoRecord>(
                videos.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var v in videos)
            {
                if (!string.IsNullOrEmpty(v.NativeId))
                    index.TryAdd(v.NativeId, v);
            }

            return index;
        }

        /// <summary>
        /// Removes duplicate entries by native id, keeping the first occurrence. The first entry
        /// is the most recent, because the listing is ordered newest-first. Without this, a
        /// platform that returns the same video twice would fail the whole turn on the unique index.
        /// </summary>
        private static List<PlaylistEntry> Deduplicate(IReadOnlyList<PlaylistEntry> entries)
        {
            var seen = new HashSet<string>(entries.Count, StringComparer.OrdinalIgnoreCase);
            var unique = new List<PlaylistEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (seen.Add(entry.Id))
                    unique.Add(entry);
            }

            return unique;
        }

        private static bool HasAnyKnown(
            List<PlaylistEntry> entries,
            Dictionary<string, VideoRecord> existingByNativeId)
        {
            foreach (var entry in entries)
            {
                if (existingByNativeId.ContainsKey(entry.Id))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Handles the case where a listing returned zero entries.
        /// <list type="bullet">
        /// <item><description>
        /// If the source has no known videos: a legitimately empty source. Success, clocks written.
        /// </description></item>
        /// <item><description>
        /// If the source has known videos: a platform outage. Failure, no clocks written. Treating
        /// a platform outage as an emptied source would trigger cleanup and delete the whole
        /// source from the library, which is irreversible for watch state.
        /// </description></item>
        /// </list>
        /// </summary>
        private ListingOutcome HandleZeroEntries(
            string sourceUrl,
            SourceDefinition sourceDef,
            PlaylistInfo playlist,
            int existingCount,
            bool full,
            DateTimeOffset now)
        {
            if (existingCount == 0)
            {
                // Legitimately empty source (e.g., a channel with no videos). Write the source
                // metadata and the clocks so this source is not immediately due again next pass.
                LogEmptySource(sourceUrl);
                var stage = full ? SourceStage.FullListing : SourceStage.ShortListing;

                var name = !string.IsNullOrWhiteSpace(sourceDef.Name)
                    ? sourceDef.Name
                    : SeriesNaming.Resolve(sourceUrl, playlist);

                _db.UpsertSource(new SourceRecord
                {
                    Url = sourceUrl,
                    Key = VodNativeId.ForSource(sourceUrl),
                    Name = name,
                    SeriesSynopsis = playlist.Description,
                    SeriesThumbnailUrl = playlist.ThumbnailUrl,
                    SeriesBannerUrl = playlist.BannerUrl,
                    VideoCount = 0,
                });

                _db.SetListingCount(sourceUrl, playlist.PlaylistCount);
                _db.SetListingPositions(sourceUrl, new Dictionary<string, int>());
                _db.TouchListedAt(sourceUrl, [], now);
                _db.SetListingClock(sourceUrl, full, now);
                _db.SetStageResult(sourceUrl, stage, CallOutcome.Ok, null);
                return new ListingOutcome(
                    CallOutcome.Ok, null, Full: full,
                    Created: 0, Retitled: 0, EntriesSeen: 0,
                    CleanupAllowed: full);
            }

            // Zero entries on a source that has known videos: treat as a platform outage.
            // Writing clocks here would make the source look up to date — and the cleanup step
            // reads CleanupAllowed to decide whether to retire missing videos. If we set the
            // clock and allowed cleanup, every video in the library would be deleted.
            const string detail = "listing returned zero entries for a source with known videos; treating as platform outage";
            LogZeroEntriesOutage(sourceUrl);
            var failStage = full ? SourceStage.FullListing : SourceStage.ShortListing;
            _db.SetStageResult(sourceUrl, failStage, CallOutcome.ExtractorError, detail);
            return new ListingOutcome(
                CallOutcome.ExtractorError, detail, Full: full,
                Created: 0, Retitled: 0, EntriesSeen: 0,
                CleanupAllowed: false);
        }

        /// <summary>Records a failure, logs it, and returns a failure outcome.</summary>
        private ListingOutcome RecordFailure(
            string sourceUrl, SourceStage stage, bool full, string detail)
        {
            // A failure records the stage result so the failure count advances, but writes no
            // clocks — that is what leaves this source due on the next pass instead of looking
            // up to date. Writing the clock on failure would hide the outage.
            LogListingFailed(sourceUrl, detail);
            _db.SetStageResult(sourceUrl, stage, CallOutcome.ExtractorError, detail);
            return new ListingOutcome(
                CallOutcome.ExtractorError, detail, Full: full,
                Created: 0, Retitled: 0, EntriesSeen: 0,
                CleanupAllowed: false);
        }

        /// <summary>
        /// Ingests the entries, writes source metadata and clocks, records the stage result, and
        /// returns the outcome.
        /// </summary>
        private ListingOutcome Ingest(
            string sourceUrl,
            SourceDefinition sourceDef,
            PlaylistInfo playlist,
            Dictionary<string, VideoRecord> existingByNativeId,
            bool full,
            int existingCount,
            DateTimeOffset now)
        {
            var deduped = Deduplicate(playlist.Entries);

            // Before the first video, and not with the source metadata below: a video's source_url
            // is a foreign key onto the sources table and foreign keys are enforced, so writing a
            // video for a source that has no row fails outright. The metadata that fills the row —
            // the name, the images, the video count — is only known once the entries have been
            // ingested, so the row has to be brought into being first and filled afterwards.
            _db.EnsureSource(sourceUrl);

            var positions = new Dictionary<string, int>(deduped.Count, StringComparer.OrdinalIgnoreCase);
            int created = 0;
            int retitled = 0;

            for (int i = 0; i < deduped.Count; i++)
            {
                var entry = deduped[i];
                positions[entry.Id] = i; // 0 = newest

                if (existingByNativeId.TryGetValue(entry.Id, out var existing))
                {
                    // Existing video: update listing_title and, when the listing provides a title,
                    // the published title. Arte carries no title in its flat listing, so we must
                    // not overwrite the metadata-fetched title with the native id fallback.
                    var oldListingTitle = existing.ListingTitle;
                    existing.ListingTitle = entry.Title;
                    if (!string.IsNullOrWhiteSpace(entry.Title))
                        existing.Title = entry.Title.Trim();

                    _db.UpsertVideo(existing);

                    // A retitle means the wave must refetch metadata for the new title. Use
                    // MarkMetadataStale (UnixEpoch) rather than nulling metadata_at: null would
                    // make Publishability refuse the video and unpublish it at this turn's checkup,
                    // destroying the Jellyfin item and its watch state for a title change.
                    if (PublishedTitle.HasBeenRetitled(oldListingTitle, entry.Title))
                    {
                        _db.MarkMetadataStale(existing.Id);
                        retitled++;
                        LogRetitled(sourceUrl, entry.Id);
                    }
                }
                else
                {
                    // New video: create the row. listing_title and title are set from what the
                    // listing returned; the wave will overwrite title with the full metadata answer.
                    var duration = entry.Duration.HasValue ? (int?)((int)entry.Duration.Value) : null;
                    var record = new VideoRecord
                    {
                        // Derived, never drawn: this uuid is what Jellyfin's watch history hangs
                        // on, so a video retired and re-listed has to come back as the same item.
                        Id = VodNativeId.UuidForVideo(
                            VodNativeId.ForSource(sourceUrl) ?? sourceUrl, entry.Id),
                        SourceUrl = sourceUrl,
                        NativeId = entry.Id,
                        ExtractorKey = entry.IeKey,
                        VideoUrl = entry.VideoUrl,
                        Title = PublishedTitle.For(entry.Title, null, entry.Id),
                        ListingTitle = entry.Title,
                        LiveStatus = entry.LiveStatus,
                        DurationSeconds = duration,
                        FirstSeenAt = now,
                    };
                    _db.UpsertVideo(record);
                    created++;
                }
            }

            // ── Source metadata ─────────────────────────────────────────────────────────────────

            // SourceDefinition.Name, when set by the administrator, wins over the name the platform
            // reported. SeriesNaming resolves the name from the playlist otherwise.
            var name = !string.IsNullOrWhiteSpace(sourceDef.Name)
                ? sourceDef.Name
                : SeriesNaming.Resolve(sourceUrl, playlist);

            // Tags are stored as a JSON array. Null when the platform reported none, to distinguish
            // "no tags" from "tags not fetched yet".
            var tagsJson = playlist.Tags.Count > 0
                ? JsonSerializer.Serialize<IReadOnlyList<string>>(playlist.Tags)
                : null;

            _db.UpsertSource(new SourceRecord
            {
                Url = sourceUrl,
                Key = VodNativeId.ForSource(sourceUrl),
                Name = name,
                SeriesSynopsis = playlist.Description,
                SeriesThumbnailUrl = playlist.ThumbnailUrl,
                SeriesBannerUrl = playlist.BannerUrl,
                Tags = tagsJson,
                VideoCount = existingCount + created,
            });

            // listing_count: the platform's own entry count. Null when the platform did not say
            // (channels don't report it). Null must stay null and never become zero: zero means an
            // emptied source, while null means "the platform did not say".
            _db.SetListingCount(sourceUrl, playlist.PlaylistCount);

            // Positions and listing timestamps. TouchListedAt is called only for entries THIS
            // listing saw — a short listing touches its 30 only, which is why CleanupAllowed is
            // false for a short listing: everything past position 30 would look gone.
            var seenIds = new List<string>(deduped.Count);
            foreach (var entry in deduped)
                seenIds.Add(entry.Id);

            _db.SetListingPositions(sourceUrl, positions);
            _db.TouchListedAt(sourceUrl, seenIds, now);

            // Clock: stamp both clocks for a full listing (it just saw everything a short would
            // have seen, so leaving the short clock behind would trigger a redundant listing next
            // turn). SetListingClock knows this and stamps both when full=true.
            _db.SetListingClock(sourceUrl, full, now);

            // Proportionality. A full listing is authoritative, and the cleanup deletes every row
            // it did not touch — irreversibly, watch state included. Zero entries is already
            // treated as an outage; a listing that carries a handful where dozens are stored is
            // the same outage with one page delivered, and yt-dlp is not guaranteed to fail the
            // call on a playlist it only partly enumerated. Withholding the cleanup costs one
            // listing interval of stale rows and self-heals on the next complete answer;
            // accepting a truncated one costs the library.
            //
            // Not applied to a listing that reached the operator's cap — lowering
            // MaxVideosPerSource is the deliberate way to shrink a source, so the cap is the
            // expected count — nor below the floor, where proportions are meaningless and a
            // three-video source dropping to one is ordinary churn.
            var cleanupAllowed = full;
            string? detail = null;
            var capped = sourceDef.MaxVideosPerSource > 0
                && deduped.Count >= sourceDef.MaxVideosPerSource;

            if (full && !capped
                && existingCount >= ShortfallFloor
                && deduped.Count * 2 < existingCount)
            {
                cleanupAllowed = false;
                detail = $"full listing carried {deduped.Count} entries where {existingCount} are "
                    + "stored; cleanup withheld in case the listing was truncated";
                LogShortfallWithheldCleanup(sourceUrl, deduped.Count, existingCount);
            }

            var stage = full ? SourceStage.FullListing : SourceStage.ShortListing;
            _db.SetStageResult(sourceUrl, stage, CallOutcome.Ok, detail);

            LogListingSucceeded(sourceUrl, deduped.Count, created, retitled, full);

            return new ListingOutcome(
                CallOutcome.Ok, detail, Full: full,
                Created: created, Retitled: retitled, EntriesSeen: deduped.Count,
                CleanupAllowed: cleanupAllowed);
        }

        // ── Log messages ─────────────────────────────────────────────────────────────────────────

        [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
            Message = "Listing for {SourceUrl} failed: {Detail}")]
        private partial void LogListingFailed(string sourceUrl, string detail);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "Listing for {SourceUrl}: source has no videos and the platform returned none — treating as legitimately empty")]
        private partial void LogEmptySource(string sourceUrl);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Listing for {SourceUrl}: platform returned zero entries but source has known videos — treating as outage, not writing clocks")]
        private partial void LogZeroEntriesOutage(string sourceUrl);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information,
            Message = "Listing for {SourceUrl}: short window had no known video, escalating to full listing")]
        private partial void LogEscalating(string sourceUrl);

        [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
            Message = "Listing for {SourceUrl}: video {NativeId} has been retitled, marking metadata stale")]
        private partial void LogRetitled(string sourceUrl, string nativeId);

        [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
            Message = "Listing for {SourceUrl} succeeded: {Seen} entries, {Created} created, {Retitled} retitled, full={Full}")]
        private partial void LogListingSucceeded(string sourceUrl, int seen, int created, int retitled, bool full);

        [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
            Message = "Listing for {SourceUrl} carried {Seen} entries where {Stored} are stored — "
                + "withholding the cleanup in case the listing was truncated")]
        private partial void LogShortfallWithheldCleanup(string sourceUrl, int seen, int stored);
    }
}

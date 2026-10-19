using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// What a reconciliation pass changed. <c>Unchanged</c> counts the expected items that were
    /// already correct and therefore never written back, so a no-op sync is legible in the log.
    /// </summary>
    /// <param name="Created">How many primary items the pass added.</param>
    /// <param name="Updated">How many primary items the pass rewrote.</param>
    /// <param name="Removed">How many items (orphans and empty containers) the pass deleted.</param>
    /// <param name="Unchanged">How many primary items were already correct.</param>
    /// <param name="CreatedVideoIds">
    /// The VOD ids (video uuids) for which the pass created at least one item, primary or tier:
    /// the only items that may still lack the stream description stored on their row.
    /// </param>
    public sealed record ReconcileReport(
        int Created, int Updated, int Removed, int Unchanged, IReadOnlySet<string> CreatedVideoIds);

    /// <summary>
    /// Aligns the Jellyfin library with what the plugin holds in SQLite, publishing items
    /// directly with no file on disk.
    /// </summary>
    /// <remarks>
    /// Reconciles by sets rather than publishing as it goes: one query lists what exists, and
    /// AddChild is then called only for genuine creations. That matters because AddChild reaches
    /// CreateItems, which raises ItemAdded on every call whether or not the item already existed —
    /// re-publishing each pass would flood the handlers, one of which refreshes metadata and
    /// clears SeriesPresentationUniqueKey.
    /// <para>
    /// The same argument applies to the update path, which is why every upsert compares before it
    /// commits: UpdateToRepositoryAsync runs the metadata savers and raises ItemUpdated, so an item
    /// that is expected, present and unchanged is left alone rather than written back.
    /// </para>
    /// </remarks>
    public sealed partial class LibraryPublisher
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IVodDatabase _db;
        private readonly ArtworkStore _artwork;
        private readonly IVodLocalizer _localizer;
        private readonly ILogger<LibraryPublisher> _logger;

        /// <summary>Initializes a new instance of the <see cref="LibraryPublisher"/> class.</summary>
        public LibraryPublisher(
            ILibraryManager libraryManager,
            IVodDatabase db,
            ArtworkStore artwork,
            IVodLocalizer localizer,
            ILogger<LibraryPublisher> logger)
        {
            _libraryManager = libraryManager;
            _db = db;
            _artwork = artwork;
            _localizer = localizer;
            _logger = logger;
        }

        /// <summary>The id key of a video's primary item.</summary>
        /// <param name="videoUuid">The video's opaque VOD uuid.</param>
        /// <returns>The id key.</returns>
        public string ItemKeyFor(string videoUuid) => VersionGroup.KeyFor(videoUuid, null);

        /// <summary>
        /// Every item this video should have: the primary, plus one alternate per offered quality
        /// tier.
        /// </summary>
        /// <param name="video">The stored video record.</param>
        /// <param name="localBaseUrl">The base URL Jellyfin reaches this plugin on.</param>
        /// <returns>One to three members, the primary at index 0.</returns>
        /// <remarks>
        /// A live video gets no alternates: its ladder is volatile and its segment URLs rotate, so a
        /// tier captured at sync may already be gone by the time anyone selects it.
        /// </remarks>
        internal IReadOnlyList<VersionMember> MembersFor(VideoRecord video, string localBaseUrl)
        {
            ArgumentNullException.ThrowIfNull(video);

            return video.LiveStatus != LiveStatus.NotLive
                ? [new VersionMember(
                    null, VersionGroup.KeyFor(video.Id, null), video.Title ?? string.Empty,
                    VersionGroup.PathFor(localBaseUrl, video.Id, null))]
                : VersionGroup.For(video, localBaseUrl);
        }

        /// <summary>The marker that keeps a library location from reading as empty.</summary>
        private const string KeepMarkerName = ".keep";

        private const string KeepMarkerContent =
            "Prevent Jellyfin from skipping empty library folders needed for virtual libraries";

        /// <summary>The container Jellyfin's probe reports for a stream route, and what every item declares.</summary>
        private const string HlsContainer = "hls";

        /// <summary>
        /// Returns the library's virtual folder. Resolves against the configuration only, so it
        /// answers whether the library exists without asking whether Jellyfin has indexed it.
        /// </summary>
        /// <remarks>
        /// Returns the whole <see cref="VirtualFolderInfo"/> rather than just its first location:
        /// the collection type is on the same object, so learning it costs no extra query.
        /// </remarks>
        private VirtualFolderInfo ResolveVirtualFolder(string libraryName)
        {
            var virtualFolder = _libraryManager.GetVirtualFolders()
                .Find(v => string.Equals(v.Name, libraryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No library named '{libraryName}'.");

            return virtualFolder.Locations.Length > 0
                ? virtualFolder
                : throw new InvalidOperationException($"Library '{libraryName}' has no location.");
        }

        /// <summary>
        /// Returns the target new items must hang off: the library's physical folder, with the type
        /// that library is declared as.
        /// </summary>
        /// <param name="libraryName">The configured library name.</param>
        /// <returns>The resolved target.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the library or its indexed folder is missing. Falling back to the
        /// CollectionFolder would publish items no query can reach, so this fails loudly.
        /// </exception>
        public LibraryTarget ResolveTarget(string libraryName)
        {
            var virtualFolder = ResolveVirtualFolder(libraryName);
            var location = virtualFolder.Locations[0];

            if (_libraryManager.FindByPath(location, true) is not Folder anchor)
            {
                throw new InvalidOperationException(
                    $"Library '{libraryName}' has no indexed folder at {location}. "
                    + "Scan the library once so Jellyfin indexes it before publishing.");
            }

            return new LibraryTarget(libraryName, anchor, virtualFolder.CollectionType);
        }

        /// <summary>
        /// Resolves the target, letting Jellyfin index the library location first when it never has.
        /// </summary>
        /// <remarks>
        /// A scan skips a library folder that holds no filesystem entries: the check it gates on,
        /// <c>DirectoryService.IsAccessible</c>, is an emptiness test rather than an access test, and
        /// the early return it triggers means the folder item <see cref="ResolveTarget"/> needs is
        /// never created. A library holding only virtual items therefore stays unusable however many
        /// times it is scanned — the failure looks like a scan that did not take. Writing a marker
        /// makes the directory non-empty, after which re-validating the top-level folders indexes it.
        /// <para>
        /// The marker is written whenever it is absent, before the lookup rather than only after the
        /// lookup fails, because a library can return to the empty case after it has worked: an
        /// indexed folder survives a full re-validation on the strength of its published children,
        /// so retiring every source strands it again. Re-validating the top-level folders is the
        /// expensive half and stays on the miss path.
        /// </para>
        /// </remarks>
        /// <param name="libraryName">The configured library name.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The resolved target.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the library is missing, has no location, or could not be indexed.
        /// </exception>
        public async Task<LibraryTarget> PrepareTargetAsync(
            string libraryName, CancellationToken cancellationToken)
        {
            var virtualFolder = ResolveVirtualFolder(libraryName);
            var location = virtualFolder.Locations[0];

            EnsureKeepMarker(location);

            if (_libraryManager.FindByPath(location, true) is Folder indexed)
                return new LibraryTarget(libraryName, indexed, virtualFolder.CollectionType);

            LogIndexingLibrary(libraryName, location);
            await _libraryManager.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);

            // Reuses the "has no indexed folder" message when the validation still produced nothing.
            return ResolveTarget(libraryName);
        }

        private static void EnsureKeepMarker(string location)
        {
            var marker = Path.Combine(location, KeepMarkerName);
            try
            {
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, KeepMarkerContent + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not write {marker}, so Jellyfin cannot index the library folder: {ex.Message}",
                    ex);
            }
        }

        /// <summary>
        /// Aligns the library with <paramref name="expected"/> for one source.
        /// </summary>
        /// <remarks>
        /// <paramref name="useReleaseDateAsDateAdded"/> is the administrator's setting, not what is
        /// applied: it only takes effect on a MIXED target, because the "Folders" default view it
        /// works around exists nowhere else. Deriving that from the target itself makes an
        /// inconsistent (anchor, flag) pair impossible to build, and both stay parameters — reading
        /// the configuration here would make the option's ON state untestable, since Plugin.Instance
        /// is null under xUnit.
        /// <para>
        /// <paramref name="otherAnchors"/> lists the anchors OTHER than the target that may still
        /// hold this source's items: the library the other mode publishes into, and the one this
        /// source last published into. The target is always swept and need not appear here. An empty
        /// list reproduces the single-library behaviour exactly.
        /// </para>
        /// </remarks>
        public async Task<ReconcileReport> ReconcileAsync(
            LibraryTarget target,
            IReadOnlyList<Folder> otherAnchors,
            SourceRecord source,
            SourceMode mode,
            string seriesName,
            IReadOnlyList<VideoRecord> expected,
            IReadOnlySet<string> allKnownVideoIds,
            string localBaseUrl,
            bool useReleaseDateAsDateAdded,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(otherAnchors);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(allKnownVideoIds);

            var anchor = target.Anchor;
            var useReleaseDate = useReleaseDateAsDateAdded && target.IsMixed;
            var swept = DistinctById([anchor, .. otherAnchors]);

            var published = ExistingByVodId(swept);
            var studios = EnsureStudioEntities(expected);

            Series? series = null;
            Season? currentSeason = null;
            int created = 0, updated = 0, unchanged = 0;

            var processedVodIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The videos this pass created any item for, primary or tier: the callers apply the
            // stream descriptions stored at resolution to these only.
            var createdVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var video in expected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var members = MembersFor(video, localBaseUrl);
                var primary = members[0];
                processedVodIds.Add(video.Id);

                UpsertOutcome outcome;
                if (mode == SourceMode.Movies)
                {
                    Video committed;
                    (outcome, committed) = await UpsertMovieAsync(
                        anchor, video, primary, published, useReleaseDate,
                        cancellationToken).ConfigureAwait(false);

                    var alternates = new List<BaseItem>();
                    for (var i = 1; i < members.Count; i++)
                    {
                        var (alternateCreated, alternate) = await UpsertAlternateAsync<Movie>(
                                anchor, video, members[i], published, false, cancellationToken)
                            .ConfigureAwait(false);
                        alternates.Add(alternate);
                        if (alternateCreated)
                            createdVideoIds.Add(video.Id);
                    }

                    await LinkVersionsAsync(committed, alternates, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    bool seriesMoved = false;
                    if (series is null)
                        (series, seriesMoved) = await EnsureSeriesAsync(
                            anchor, source, seriesName, NewestThumbnail(expected),
                            OldestPublishedAt(expected), studios,
                            SeriesDates(expected, useReleaseDate), cancellationToken)
                            .ConfigureAwait(false);
                    currentSeason = await EnsureSeasonAsync(series, video.SeasonNumber, currentSeason, seriesMoved, cancellationToken).ConfigureAwait(false);

                    Video committed;
                    (outcome, committed) = await UpsertEpisodeAsync(
                        series, currentSeason, video, primary, published,
                        useReleaseDate, seriesMoved, cancellationToken)
                        .ConfigureAwait(false);

                    var alternates = new List<BaseItem>();
                    for (var i = 1; i < members.Count; i++)
                    {
                        var (alternateCreated, alternate) = await UpsertAlternateAsync<Episode>(
                                currentSeason, video, members[i], published, seriesMoved, cancellationToken)
                            .ConfigureAwait(false);
                        alternates.Add(alternate);
                        if (alternateCreated)
                            createdVideoIds.Add(video.Id);
                    }

                    await LinkVersionsAsync(committed, alternates, cancellationToken)
                        .ConfigureAwait(false);
                }

                switch (outcome)
                {
                    case UpsertOutcome.Created:
                        created++;
                        createdVideoIds.Add(video.Id);
                        break;
                    case UpsertOutcome.Updated:
                        updated++;
                        break;
                    default:
                        unchanged++;
                        break;
                }
            }

            // An item is orphaned when no source claims its video any more, or when it is a member
            // this video no longer expects — a stale item from a flipped source mode, or an
            // alternate for a tier the upstream has dropped. Every expected member was removed from
            // the map by TakeById above, so whatever is left is genuinely unaccounted for. Items
            // belonging to other sources are in this map too, because the query spans whole
            // libraries while `expected` is per-source.
            var removed = 0;
            foreach (var (vodId, orphans) in published)
            {
                // Skip items that belong to a video claimed by another source and were not
                // processed in this reconcile: they are not ours to delete.
                if (allKnownVideoIds.Contains(vodId) && !processedVodIds.Contains(vodId))
                    continue;

                foreach (var orphan in orphans)
                {
                    _libraryManager.DeleteItem(
                        orphan, new DeleteOptions { DeleteFileLocation = false }, true);
                    removed++;
                }
            }

            // A Movies source must own no containers at all, so the same reconciliation runs with an
            // empty expectation: it reaps the series and seasons a previous Series-mode run left
            // behind when the source's Mode was flipped, under the same episode-count guard.
            removed += mode == SourceMode.Movies
                ? ReconcileContainers(anchor, source, [], null)
                : ReconcileContainers(anchor, source, expected, series);

            // The empty Series and Season shells a change of target library leaves behind: FindSeries
            // is scoped to one anchor, so moving the episodes creates a new Series and never finds
            // the old one. swept[0] is the target, already reconciled above.
            for (var i = 1; i < swept.Count; i++)
                removed += ReconcileContainers(swept[i], source, [], null);

            LogReconciled(source.Url, created, updated, removed, unchanged);
            return new ReconcileReport(created, updated, removed, unchanged, createdVideoIds);
        }

        /// <summary>What publishing one video did to the library.</summary>
        private enum UpsertOutcome
        {
            /// <summary>The item was already correct, so nothing was written.</summary>
            Unchanged,

            /// <summary>The item did not exist and was added through AddChild.</summary>
            Created,

            /// <summary>The item existed and at least one field differed.</summary>
            Updated
        }

        /// <summary>
        /// Deletes the seasons this source no longer accounts for, and the series itself once the
        /// source accounts for nothing at all.
        /// </summary>
        /// <remarks>
        /// Decided from <paramref name="expected"/>, the same database view the orphan loop above
        /// uses, rather than by sweeping the library for empty containers afterwards: a season is
        /// removed because no video claims it any more, not as a consequence of its episodes having
        /// been deleted. Scoped to this source's series, so no other source can be touched.
        /// The emptiness check is a guard, not the mechanism: an episode can outlive its source when
        /// another source still claims the same video, and that episode must not be deleted with a
        /// season this source considers gone.
        /// </remarks>
        private int ReconcileContainers(
            Folder anchor, SourceRecord source, IReadOnlyList<VideoRecord> expected, Series? series)
        {
            series ??= FindSeries(anchor, VodNativeId.ForSource(source.Url) ?? source.Url);
            if (series is null)
                return 0;

            if (expected.Count == 0)
            {
                // Another source may still be filling this series: the key is shared by every URL
                // that resolves to the same channel. DeleteItem takes the whole subtree with it.
                if (ChildCount(series.Id, BaseItemKind.Episode) > 0)
                    return 0;

                _libraryManager.DeleteItem(
                    series, new DeleteOptions { DeleteFileLocation = false }, true);
                return 1;
            }

            var expectedSeasons = new HashSet<int>();
            foreach (var video in expected)
                expectedSeasons.Add(video.SeasonNumber ?? 0);

            var removed = 0;
            var seasons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = [BaseItemKind.Season]
            });

            foreach (var item in seasons)
            {
                if (item is not Season season || expectedSeasons.Contains(season.IndexNumber ?? 0))
                    continue;

                if (ChildCount(season.Id, BaseItemKind.Episode) > 0)
                    continue;

                _libraryManager.DeleteItem(
                    season, new DeleteOptions { DeleteFileLocation = false }, true);
                removed++;
            }

            return removed;
        }

        private Series? FindSeries(Folder anchor, string sourceKey)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                TopParentIds = [anchor.Id],
                IncludeItemTypes = [BaseItemKind.Series],
                HasAnyProviderId = new Dictionary<string, string> { ["VOD"] = sourceKey },
                Recursive = true
            });

            return items.Count > 0 ? items[0] as Series : null;
        }

        private int ChildCount(Guid parentId, BaseItemKind kind)
            => _libraryManager.GetCount(new InternalItemsQuery
            {
                ParentId = parentId,
                IncludeItemTypes = [kind],
                Recursive = true
            });

        /// <summary>
        /// One query across every swept anchor, indexed by the VOD provider id.
        /// </summary>
        /// <remarks>
        /// A list per id rather than a single item: the same video can legitimately have a Movie in
        /// one library and an Episode in another once a source's mode has been flipped while the two
        /// libraries differ. The two carry different ids — GetNewItemId derives them from the type —
        /// so they coexist, and the stale one has to be found to be removed.
        /// <para>
        /// TopParentIds takes an array, so widening the sweep costs no extra query.
        /// </para>
        /// </remarks>
        private Dictionary<string, List<BaseItem>> ExistingByVodId(List<Folder> anchors)
        {
            var topParentIds = new Guid[anchors.Count];
            for (var i = 0; i < anchors.Count; i++)
                topParentIds[i] = anchors[i].Id;

            // IncludeAlternateVersions is load-bearing: Jellyfin hides every item carrying a
            // PrimaryVersionId behind the version it belongs to, and its own comment on that filter
            // says the library scan keeps them "so a merged version is not mistaken for a new item".
            // Without it this sweep never sees the alternates it published — it would recreate them on
            // every pass, and the orphan loop could never reap one whose tier the upstream dropped.
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                TopParentIds = topParentIds,
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                IncludeAlternateVersions = true,
                Recursive = true
            });

            var map = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var id = item.GetProviderId("VOD");
                if (string.IsNullOrEmpty(id))
                    continue;

                if (!map.TryGetValue(id, out var candidates))
                    map[id] = candidates = [];

                candidates.Add(item);
            }

            return map;
        }

        /// <summary>
        /// Returns the anchors with duplicates dropped, first occurrence kept.
        /// </summary>
        /// <remarks>
        /// By id, never by name: two configured names differing only in case resolve to the same
        /// folder, and a target listed twice would be swept against itself.
        /// </remarks>
        private static List<Folder> DistinctById(IReadOnlyList<Folder> anchors)
        {
            var seen = new HashSet<Guid>();
            var distinct = new List<Folder>(anchors.Count);
            foreach (var folder in anchors)
            {
                if (seen.Add(folder.Id))
                    distinct.Add(folder);
            }

            return distinct;
        }

        /// <summary>
        /// Returns the item carrying <paramref name="itemId"/> from this video's published group, and
        /// removes it from the group so later passes do not see it again.
        /// </summary>
        /// <remarks>
        /// Targeted by id rather than by type: a video now has up to three items of the same type,
        /// so "the first one of type T" is no longer a usable identity. Whatever is left in the group
        /// after every expected member has been taken is handled by the caller — a stale item from a
        /// flipped source mode, or an alternate for a tier the upstream dropped.
        /// </remarks>
        private static T? TakeById<T>(string vodId, Guid itemId, Dictionary<string, List<BaseItem>> published)
            where T : BaseItem
        {
            if (!published.TryGetValue(vodId, out var candidates))
            {
                return null;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Id.Equals(itemId) && candidates[i] is T wanted)
                {
                    candidates.RemoveAt(i);
                    return wanted;
                }
            }

            return null;
        }

        /// <summary>
        /// Publishes one video as a Movie and reports what that did to the library, along with the
        /// instance it committed.
        /// </summary>
        /// <remarks>
        /// Hands the instance back for the same reason <see cref="UpsertAlternateAsync{T}"/> does: the
        /// linking step must write through the entity this method mutated and saved. Reading the
        /// primary back by id instead would return whatever the LRU cache holds, which
        /// <c>LibraryManager.UpdateItemsAsync</c> deliberately leaves unrefreshed — a copy from the
        /// pass that created the item, whose stale fields a link write would commit over the fresh
        /// ones.
        /// </remarks>
        private async Task<(UpsertOutcome Outcome, Video Item)> UpsertMovieAsync(
            Folder anchor,
            VideoRecord video,
            VersionMember member,
            Dictionary<string, List<BaseItem>> published,
            bool useReleaseDateAsDateAdded,
            CancellationToken cancellationToken)
        {
            var itemId = _libraryManager.GetNewItemId(member.ItemKey, typeof(Movie));
            var existing = TakeById<Movie>(video.Id, itemId, published);
            var isNew = existing is null;

            // The item moved between libraries: commit through AddChild so Jellyfin re-parents it
            // and recomputes TopParentId, keeping the id and therefore the user's watch history.
            var reparent = existing is not null && existing.ParentId != anchor.Id;

            var movie = existing ?? new Movie { Id = itemId };

            var overview = BuildVideoOverview(video);
            var premiereDate = PremiereDateFor(video);
            var productionYear = video.PublishedAt?.UtcDateTime.Year;
            var runTimeTicks = RunTimeTicks(video);
            var dateAdded = DateAddedFor(video, useReleaseDateAsDateAdded);

            var changed = isNew
                || !string.Equals(movie.Name, video.Title, StringComparison.Ordinal)
                || !string.Equals(movie.Overview, overview, StringComparison.Ordinal)
                || reparent
                || !string.Equals(movie.Path, member.Path, StringComparison.Ordinal)
                || movie.PremiereDate != premiereDate
                || movie.ProductionYear != productionYear
                || movie.RunTimeTicks != runTimeTicks;

            movie.Name = video.Title;
            movie.Overview = overview;
            movie.ParentId = anchor.Id;
            movie.Path = member.Path;
            movie.PremiereDate = premiereDate;
            movie.ProductionYear = productionYear;
            movie.RunTimeTicks = runTimeTicks;
            changed |= ApplyDateAdded(movie, dateAdded);
            changed |= ApplyContainer(movie);
            changed |= ApplyVideoIds(movie, video);
            changed |= ApplyTags(movie, video.Extras.Tags, video.Extras.Categories);
            changed |= ApplyStudios(movie, StudiosFor(video));

            var presentationKey = movie.CreatePresentationUniqueKey();
            changed |= !string.Equals(movie.PresentationUniqueKey, presentationKey, StringComparison.Ordinal);
            movie.PresentationUniqueKey = presentationKey;

            changed |= ApplyImage(movie, ImageType.Primary, video.ThumbnailUrl, video.PublishedImageUrl,
                url => _db.SetPublishedImageUrl(video.Id, url), out var imageWrite);
            // The still is landscape, so it serves as the Thumb too; Jellyfin crops it for the poster
            // grid. A generated 2:3 poster only steps in when there is no still at all.
            changed |= ApplyImage(movie, ImageType.Thumb, video.ThumbnailUrl, video.PublishedImageUrl, null, out _);
            // Jellyfin builds a library's own tile from its children's Backdrop, falling back to
            // Primary then Thumb (BaseDynamicImageProvider.GetStripCollageImagePaths). Without one
            // the library is left with no generated image.
            changed |= ApplyImage(movie, ImageType.Backdrop, video.ThumbnailUrl, video.PublishedImageUrl, null, out _);
            changed |= EnsurePoster(movie, ItemKeyFor(video.Id));

            if (!changed)
                return (UpsertOutcome.Unchanged, movie);

            await CommitAsync(movie, anchor, isNew || reparent, cancellationToken).ConfigureAwait(false);
            imageWrite?.Invoke();
            return (isNew ? UpsertOutcome.Created : UpsertOutcome.Updated, movie);
        }

        /// <summary>
        /// Publishes one video as an Episode and reports what that did to the library, along with the
        /// instance it committed.
        /// </summary>
        /// <remarks>See <see cref="UpsertMovieAsync"/> for why the instance is handed back.</remarks>
        private async Task<(UpsertOutcome Outcome, Video Item)> UpsertEpisodeAsync(
            Series series,
            Season season,
            VideoRecord video,
            VersionMember member,
            Dictionary<string, List<BaseItem>> published,
            bool useReleaseDateAsDateAdded,
            bool seriesMoved,
            CancellationToken cancellationToken)
        {
            var itemId = _libraryManager.GetNewItemId(member.ItemKey, typeof(Episode));
            var existing = TakeById<Episode>(video.Id, itemId, published);
            var isNew = existing is null;
            var reparent = existing is not null && (existing.ParentId != season.Id || seriesMoved);

            var episode = existing ?? new Episode { Id = itemId };

            var overview = BuildVideoOverview(video);
            var premiereDate = PremiereDateFor(video);
            var productionYear = video.PublishedAt?.UtcDateTime.Year;
            var runTimeTicks = RunTimeTicks(video);
            var dateAdded = DateAddedFor(video, useReleaseDateAsDateAdded);
            // Queries tie an episode to its series through this field, not through ParentId.
            var seriesKey = series.GetPresentationUniqueKey();

            var changed = isNew
                || !string.Equals(episode.Name, video.Title, StringComparison.Ordinal)
                || !string.Equals(episode.Overview, overview, StringComparison.Ordinal)
                || reparent
                || !string.Equals(episode.Path, member.Path, StringComparison.Ordinal)
                || episode.IndexNumber != video.EpisodeNumber
                || episode.ParentIndexNumber != season.IndexNumber
                || episode.PremiereDate != premiereDate
                || episode.ProductionYear != productionYear
                || episode.RunTimeTicks != runTimeTicks
                || episode.SeasonId != season.Id
                || !string.Equals(episode.SeasonName, season.Name, StringComparison.Ordinal)
                || episode.SeriesId != series.Id
                || !string.Equals(episode.SeriesName, series.Name, StringComparison.Ordinal)
                || !string.Equals(episode.SeriesPresentationUniqueKey, seriesKey, StringComparison.Ordinal);

            episode.Name = video.Title;
            episode.Overview = overview;
            episode.ParentId = season.Id;
            episode.Path = member.Path;
            episode.IndexNumber = video.EpisodeNumber;
            episode.ParentIndexNumber = season.IndexNumber;
            episode.PremiereDate = premiereDate;
            episode.ProductionYear = productionYear;
            episode.RunTimeTicks = runTimeTicks;
            episode.SeasonId = season.Id;
            episode.SeasonName = season.Name;
            episode.SeriesId = series.Id;
            episode.SeriesName = series.Name;
            episode.SeriesPresentationUniqueKey = seriesKey;
            changed |= ApplyDateAdded(episode, dateAdded);
            changed |= ApplyContainer(episode);
            changed |= ApplyVideoIds(episode, video);
            changed |= ApplyTags(episode, video.Extras.Tags, video.Extras.Categories);
            changed |= ApplyStudios(episode, StudiosFor(video));

            var presentationKey = episode.CreatePresentationUniqueKey();
            changed |= !string.Equals(episode.PresentationUniqueKey, presentationKey, StringComparison.Ordinal);
            episode.PresentationUniqueKey = presentationKey;

            // An episode's Primary is its still, by Jellyfin convention — landscape, not a poster.
            changed |= ApplyImage(episode, ImageType.Primary, video.ThumbnailUrl, video.PublishedImageUrl,
                url => _db.SetPublishedImageUrl(video.Id, url), out var imageWrite);
            changed |= ApplyImage(episode, ImageType.Thumb, video.ThumbnailUrl, video.PublishedImageUrl, null, out _);

            if (!changed)
                return (UpsertOutcome.Unchanged, episode);

            await CommitAsync(episode, season, isNew || reparent, cancellationToken).ConfigureAwait(false);
            imageWrite?.Invoke();
            return (isNew ? UpsertOutcome.Created : UpsertOutcome.Updated, episode);
        }

        /// <summary>
        /// Publishes one quality tier as its own item, carrying the same descriptive metadata as the
        /// primary: only the name and the id differ.
        /// </summary>
        /// <remarks>
        /// An alternate used to carry no description, tags, studios or images, on the reasoning that
        /// being linked into a version group hides it from every listing, so artwork would be stored
        /// for something nothing ever displays. That premise is too narrow: the playback history and
        /// the resume rows show the item actually played, which is the alternate, so it needs to look
        /// like the video. Only the published-image bookkeeping stays the primary's, because the
        /// database holds one published_image_url per video.
        /// <para>
        /// Its PresentationUniqueKey is the computed value, like every other item. Before the
        /// primary and alternate are linked, <c>Video.CreatePresentationUniqueKey</c> yields each
        /// item's own <c>Id.ToString("N")</c>, so the keys are distinct. Once Task 6 calls
        /// <c>SetPrimaryVersionId</c>, that method itself sets <c>PresentationUniqueKey</c> to the
        /// primary's Guid, which is Jellyfin's own mechanism for hiding linked alternates from
        /// listings — so there is nothing to fight here.
        /// </para>
        /// </remarks>
        private async Task<(bool Created, BaseItem Item)> UpsertAlternateAsync<T>(
            Folder parent,
            VideoRecord video,
            VersionMember member,
            Dictionary<string, List<BaseItem>> published,
            bool seriesMoved,
            CancellationToken cancellationToken)
            where T : BaseItem, new()
        {
            var itemId = _libraryManager.GetNewItemId(member.ItemKey, typeof(T));
            var existing = TakeById<T>(video.Id, itemId, published);
            var isNew = existing is null;
            var reparent = existing is not null && (existing.ParentId != parent.Id || seriesMoved);

            var item = existing ?? new T { Id = itemId };

            var runTimeTicks = RunTimeTicks(video);
            var premiereDate = PremiereDateFor(video);

            var overview = BuildVideoOverview(video);
            var productionYear = video.PublishedAt?.UtcDateTime.Year;

            var changed = isNew
                || reparent
                || !string.Equals(item.Name, member.Name, StringComparison.Ordinal)
                || !string.Equals(item.Overview, overview, StringComparison.Ordinal)
                || !string.Equals(item.Path, member.Path, StringComparison.Ordinal)
                || item.PremiereDate != premiereDate
                || item.ProductionYear != productionYear
                || item.RunTimeTicks != runTimeTicks;

            item.Name = member.Name;
            item.Overview = overview;
            item.Path = member.Path;
            item.ParentId = parent.Id;
            item.PremiereDate = premiereDate;
            item.ProductionYear = productionYear;
            item.RunTimeTicks = runTimeTicks;
            changed |= ApplyContainer(item);
            changed |= ApplyVideoIds(item, video);
            changed |= ApplyTags(item, video.Extras.Tags, video.Extras.Categories);
            changed |= ApplyStudios(item, StudiosFor(video));

            // The published-image bookkeeping stays the primary's: the database holds one
            // published_image_url per video, so an alternate passes no write-back callback.
            changed |= ApplyImage(item, ImageType.Primary, video.ThumbnailUrl, video.PublishedImageUrl,
                null, out _);
            changed |= ApplyImage(item, ImageType.Thumb, video.ThumbnailUrl, video.PublishedImageUrl,
                null, out _);
            changed |= ApplyImage(item, ImageType.Backdrop, video.ThumbnailUrl, video.PublishedImageUrl,
                null, out _);
            // Keyed on the primary's item key, so the whole group shares one generated file instead of
            // three identical ones. The primary is upserted before its alternates, so the text drawn
            // on it is the video title rather than a tier label.
            changed |= EnsurePoster(item, ItemKeyFor(video.Id));

            var presentationKey = item.CreatePresentationUniqueKey();
            changed |= !string.Equals(item.PresentationUniqueKey, presentationKey, StringComparison.Ordinal);
            item.PresentationUniqueKey = presentationKey;

            if (item is Episode episode && parent is Season season)
            {
                var seriesKey = season.SeriesPresentationUniqueKey;
                changed |= episode.SeasonId != season.Id
                    || episode.SeriesId != season.SeriesId
                    || episode.IndexNumber != video.EpisodeNumber
                    || episode.ParentIndexNumber != season.IndexNumber
                    || !string.Equals(episode.SeriesPresentationUniqueKey, seriesKey, StringComparison.Ordinal);

                episode.SeasonId = season.Id;
                episode.SeasonName = season.Name;
                episode.SeriesId = season.SeriesId;
                episode.SeriesName = season.SeriesName;
                episode.SeriesPresentationUniqueKey = seriesKey;
                episode.IndexNumber = video.EpisodeNumber;
                episode.ParentIndexNumber = season.IndexNumber;
            }

            if (changed)
            {
                await CommitAsync(item, parent, isNew || reparent, cancellationToken)
                    .ConfigureAwait(false);
            }

            // The item itself, not only an outcome: Task 6's linking step needs the committed
            // instance, and an unchanged alternate still has to be linked. Whether it was created
            // tells the caller the tier still lacks its stored stream description.
            return (isNew, item);
        }

        /// <summary>
        /// Ties a video's alternates to its primary: PrimaryVersionId on each alternate,
        /// LinkedAlternateVersions on the primary in display order.
        /// </summary>
        /// <remarks>
        /// Deliberately not MergeVersions: that endpoint elects the primary by Width, which this
        /// plugin never sets, so the choice would be arbitrary. Measured on the test instance, it
        /// picked by id order.
        /// <para>
        /// The primary's list is rewritten from the expected tiers every time, which is what makes a
        /// vanished tier disappear from the menu before the orphan sweep deletes its item: a link
        /// pointing at a deleted item would leave a dead entry behind.
        /// </para>
        /// <para>
        /// Takes the instance the upsert committed rather than looking the primary up by id: the two
        /// are not the same object inside one Jellyfin process, and only the committed one carries
        /// this pass's metadata. See <see cref="UpsertMovieAsync"/>.
        /// </para>
        /// </remarks>
        private static async Task LinkVersionsAsync(
            Video primary,
            IReadOnlyList<BaseItem> alternates,
            CancellationToken cancellationToken)
        {
            var primaryId = primary.Id;

            foreach (var alternate in alternates)
            {
                if (alternate is not Video alternateVideo
                    || alternateVideo.PrimaryVersionId == primaryId)
                {
                    continue;
                }

                alternateVideo.SetPrimaryVersionId(primaryId);
                await alternateVideo
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
            }

            var links = alternates
                .Select(a => new LinkedChild
                {
                    ItemId = a.Id,
                    Type = LinkedChildType.LinkedAlternateVersion
                })
                .ToArray();

            if (primary.LinkedAlternateVersions.Length == links.Length
                && primary.LinkedAlternateVersions
                    .Select(l => l.ItemId)
                    .SequenceEqual(links.Select(l => l.ItemId)))
            {
                return;
            }

            primary.LinkedAlternateVersions = links;
            await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates or re-parents through AddChild, updates in place through the repository.
        /// </summary>
        /// <remarks>
        /// AddChild raises ItemAdded every time it is called, so it is reserved for an item genuinely
        /// arriving under this parent: a creation, or a move from another library. Updating an
        /// existing item in place through it would re-fire the event on every sync.
        /// </remarks>
        private static async Task CommitAsync(
            BaseItem item, Folder parent, bool arriving, CancellationToken cancellationToken)
        {
            if (arriving)
                parent.AddChild(item);
            else
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
        }

        private async Task<(Series Series, bool Moved)> EnsureSeriesAsync(
            Folder anchor,
            SourceRecord source,
            string seriesName,
            string? fallbackImageUrl,
            DateTimeOffset? oldestPublishedAt,
            string[] studios,
            (DateTime? Added, DateTime LastContent) dates,
            CancellationToken cancellationToken)
        {
            var key = VodNativeId.ForSource(source.Url) ?? source.Url;
            var id = _libraryManager.GetNewItemId($"vod-source:{key}", typeof(Series));
            var existing = _libraryManager.GetItemById(id) as Series;
            var series = existing ?? new Series { Id = id };

            var overview = BuildSeriesOverview(source);
            var premiereDate = oldestPublishedAt?.UtcDateTime;
            var productionYear = oldestPublishedAt?.UtcDateTime.Year;

            // Must be computed before the assignments mutate the existing item.
            var seriesMoved = existing is not null && existing.ParentId != anchor.Id;

            var changed = existing is null
                || seriesMoved
                || !string.Equals(series.Name, seriesName, StringComparison.Ordinal)
                || !string.Equals(series.Overview, overview, StringComparison.Ordinal)
                || series.PremiereDate != premiereDate
                || series.ProductionYear != productionYear
                || series.DateLastMediaAdded != dates.LastContent;

            series.Name = seriesName;
            series.Overview = overview;
            series.ParentId = anchor.Id;
            series.PremiereDate = premiereDate;
            series.ProductionYear = productionYear;
            series.DateLastMediaAdded = dates.LastContent;
            changed |= ApplyDateAdded(series, dates.Added);
            changed |= ApplySourceIds(series, key);
            changed |= ApplyTags(series, DeserialiseStrings(source.Tags), []);
            changed |= ApplyStudios(series, studios);

            // Jellyfin only fills PresentationUniqueKey in MetadataService.BeforeSaveInternal, which
            // never runs on an item we create through the API. Leaving it null is not harmless:
            // Episode.FindSeriesPresentationUniqueKey reads the *stored* property, not
            // GetPresentationUniqueKey(), so on the next episode refresh Jellyfin would copy the null
            // back and unlink every episode from its series.
            var presentationKey = series.CreatePresentationUniqueKey();
            changed |= !string.Equals(series.PresentationUniqueKey, presentationKey, StringComparison.Ordinal);
            series.PresentationUniqueKey = presentationKey;
            // arte exposes no image at collection level, so the first episode's still is the only
            // candidate. Verified against yt-dlp: an arte collection has neither "thumbnail" nor
            // "thumbnails", in flat and full extraction alike.
            // The newest video's still, in preference to the channel avatar and banner, on every
            // slot. Decided on the user's production observation: a 16:9 landscape still reads
            // better in Jellyfin's interface than a near-square avatar stretched into a 2:3
            // poster — compared side by side between channel sources, which posted the avatar,
            // and playlist sources, which already fell back to a still.
            var seriesImage = fallbackImageUrl;

            changed |= ApplyImage(series, ImageType.Primary, seriesImage, source.PublishedImageUrl,
                url => _db.SetSourcePublishedImageUrl(source.Url, url), out var primaryWrite);
            changed |= ApplyImage(series, ImageType.Thumb, seriesImage,
                source.PublishedBannerUrl,
                url => _db.SetSourcePublishedBannerUrl(source.Url, url), out var bannerWrite);
            // Gives a series library a generated tile, for the reason given on the movie Backdrop
            // above. Follows the series image rather than the Thumb's banner: a YouTube channel banner
            // is about 6.2:1, which the 16:9 collage tile would crop to an unrecognisable strip. It
            // therefore tracks the URL the Primary call persists, which that call already writes.
            changed |= ApplyImage(series, ImageType.Backdrop, seriesImage,
                source.PublishedImageUrl, null, out _);
            changed |= EnsurePoster(series, key);

            if (changed)
                await CommitAsync(series, anchor, existing is null || seriesMoved, cancellationToken)
                    .ConfigureAwait(false);
            primaryWrite?.Invoke();
            bannerWrite?.Invoke();

            return (series, seriesMoved);
        }

        private async Task<Season> EnsureSeasonAsync(
            Series series, int? seasonNumber, Season? current, bool seriesMoved, CancellationToken cancellationToken)
        {
            var number = seasonNumber ?? 0;
            if (current is not null && current.IndexNumber == number)
                return current;

            var id = _libraryManager.GetNewItemId(
                series.Id.ToString("N", CultureInfo.InvariantCulture)
                    + "Season" + number.ToString(CultureInfo.InvariantCulture),
                typeof(Season));

            var existing = _libraryManager.GetItemById(id) as Season;
            var season = existing ?? new Season { Id = id };

            var name = string.Format(
                CultureInfo.InvariantCulture, _localizer.Get("SeasonLabel"), number);
            var seriesKey = series.GetPresentationUniqueKey();

            var changed = existing is null
                || seriesMoved
                || !string.Equals(season.Name, name, StringComparison.Ordinal)
                || season.IndexNumber != number
                || season.ParentId != series.Id
                || season.SeriesId != series.Id
                || !string.Equals(season.SeriesName, series.Name, StringComparison.Ordinal)
                || !string.Equals(season.SeriesPresentationUniqueKey, seriesKey, StringComparison.Ordinal);

            season.Name = name;
            season.IndexNumber = number;
            season.ParentId = series.Id;
            season.SeriesId = series.Id;
            season.SeriesName = series.Name;
            season.SeriesPresentationUniqueKey = seriesKey;

            var presentationKey = season.CreatePresentationUniqueKey();
            changed |= !string.Equals(season.PresentationUniqueKey, presentationKey, StringComparison.Ordinal);
            season.PresentationUniqueKey = presentationKey;

            // A season has no artwork anywhere upstream, so it always gets a generated poster. The key
            // includes the season number so two seasons of one source look different.
            changed |= EnsurePoster(
                season,
                $"{series.GetProviderId("VOD") ?? series.Id.ToString()}|S{number.ToString(CultureInfo.InvariantCulture)}");

            if (changed)
                await CommitAsync(season, series, existing is null || seriesMoved, cancellationToken)
                    .ConfigureAwait(false);

            return season;
        }

        /// <summary>
        /// Points the item's primary image at a remote URL, which Jellyfin downloads and caches
        /// locally the first time the image is requested (ImageController → ConvertImageToLocal).
        /// </summary>
        /// <remarks>
        /// Comparing against the URL we last published, rather than against the item's current image
        /// path, is what makes an upstream artwork change visible: once Jellyfin has localised the
        /// image its path is a file under /config/metadata and no longer says where it came from.
        /// Re-pointing an unchanged image would also cost a fresh download every sync, since
        /// SetImage overwrites Path unconditionally.
        /// </remarks>
        /// <returns>True when the image was written, so the caller knows the item has to be saved.</returns>
        private static bool ApplyImage(
            BaseItem item,
            ImageType type,
            string? url,
            string? publishedUrl,
            Action<string?>? recordPublished,
            out Action? pendingWrite)
        {
            pendingWrite = null;
            if (string.IsNullOrEmpty(url))
                return false;

            var existing = item.GetImageInfo(type, 0);
            var alreadyPublished = existing is not null
                && string.Equals(publishedUrl, url, StringComparison.Ordinal);

            if (alreadyPublished)
                return false;

            item.SetImage(new ItemImageInfo { Path = url, Type = type }, 0);
            if (recordPublished is not null)
                pendingWrite = () => recordPublished(url);
            return true;
        }

        /// <summary>
        /// Gives the item a generated 2:3 poster when nothing upstream supplied a Primary image.
        /// </summary>
        /// <returns>True when a poster was written, so the caller knows the item has to be saved.</returns>
        private bool EnsurePoster(BaseItem item, string key)
        {
            if (item.GetImageInfo(ImageType.Primary, 0) is not null)
                return false;

            item.SetImage(
                new ItemImageInfo { Path = _artwork.GetOrCreate(key, item.Name), Type = ImageType.Primary },
                0);
            return true;
        }

        /// <summary>The first still available among a source's videos, used when the source itself
        /// exposes no image.</summary>
        /// <summary>The still of the most recently published video that has one.</summary>
        /// <remarks>
        /// Computed on <see cref="VideoRecord.PublishedAt"/> rather than taken from the list order:
        /// the order is whatever the publication pass happened to produce, so keying on it made the
        /// series image depend on an implementation detail. A row with no date loses to any dated
        /// row, and only wins if nothing else has a still.
        /// </remarks>
        private static string? NewestThumbnail(IReadOnlyList<VideoRecord> videos)
        {
            string? best = null;
            DateTimeOffset? bestDate = null;

            foreach (var v in videos)
            {
                if (string.IsNullOrEmpty(v.ThumbnailUrl))
                    continue;

                if (best is null || (v.PublishedAt is { } d && (bestDate is null || d > bestDate)))
                {
                    best = v.ThumbnailUrl;
                    bestDate = v.PublishedAt;
                }
            }

            return best;
        }

        /// <summary>
        /// Builds a video's Overview: the broadcast state first (if any), then the source URL, then
        /// the description.
        /// </summary>
        /// <remarks>
        /// The broadcast state leads because it is why the viewer is looking at this item; the source
        /// line is reference material. Composed here rather than stored, so a stream that has ended
        /// stops advertising itself as live as soon as the next sync clears the flag.
        /// </remarks>
        internal string BuildVideoOverview(VideoRecord video)
        {
            var sb = new StringBuilder();

            // The broadcast state leads: it is why the viewer is looking at this item, where the
            // source line is reference material. User request 2026-10-03.
            if (video.LiveStatus == LiveStatus.Live)
                sb.Append(_localizer.Get("LiveNow")).Append("\n\n");
            else if (video.LiveStatus == LiveStatus.Upcoming && video.PublishedAt.HasValue)
                sb.Append(string.Format(
                        CultureInfo.InvariantCulture,
                        _localizer.Get("UpcomingBroadcast"),
                        video.PublishedAt.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                  .Append("\n\n");

            sb.Append(SourceLine(video.VideoUrl));

            if (!string.IsNullOrEmpty(video.Description))
                sb.Append("\n\n").Append(video.Description);

            return sb.ToString();
        }

        /// <summary>The duration to publish, or null while the source is still growing.</summary>
        /// <remarks>
        /// Jellyfin builds a frozen segment list from RunTimeTicks, so an item with a duration stops
        /// playing at the length known when playback started. Without one, the client asks for
        /// live.m3u8 and follows the growing source. Measured 2026-10-03 (spec F11).
        /// </remarks>
        internal static long? RunTimeTicks(VideoRecord video)
            => video.LiveStatus == LiveStatus.NotLive && video.DurationSeconds.HasValue
                ? video.DurationSeconds.Value * TimeSpan.TicksPerSecond
                : null;

        /// <summary>The release date to publish.</summary>
        /// <remarks>
        /// A broadcast keeps living, so it must stay first in the release-date sort too: while it is
        /// live its date is the last pass that saw it, and while it is scheduled it is the airing
        /// time, which is in the future. Both are computed here rather than stored, so the row keeps
        /// the dates episode numbering and the eventual replay depend on.
        /// </remarks>
        internal static DateTime? PremiereDateFor(VideoRecord video)
        {
            ArgumentNullException.ThrowIfNull(video);
            return video.LiveStatus switch
            {
                LiveStatus.Live => video.SyncedAt.UtcDateTime,
                LiveStatus.Upcoming => video.PublishedAt?.UtcDateTime ?? video.SyncedAt.UtcDateTime,
                _ => video.PublishedAt?.UtcDateTime
            };
        }

        private static bool ApplyVideoIds(BaseItem item, VideoRecord video)
        {
            var changed = SetProviderId(item, "VOD", video.Id);
            if (!string.IsNullOrEmpty(video.ExtractorKey) && !string.IsNullOrEmpty(video.NativeId))
                changed |= SetProviderId(item, video.ExtractorKey, video.NativeId);

            return changed;
        }

        /// <summary>Sets a provider id and reports whether that was a change.</summary>
        private static bool SetProviderId(BaseItem item, string name, string value)
        {
            if (string.Equals(item.GetProviderId(name), value, StringComparison.Ordinal))
                return false;

            item.SetProviderId(name, value);
            return true;
        }

        /// <remarks>
        /// A series keeps the type prefix in its value — "ch_arte", "pl_PL…" — because it tells a
        /// channel from a playlist, which the bare id does not.
        /// <para>
        /// The <c>Custom</c> id is what makes the series' presentation key stable. Series overrides
        /// CreatePresentationUniqueKey and, when automatic grouping is on, derives the key from the
        /// series *name* unless GetUserDataKeys returns more than one entry — and that method only
        /// counts Imdb, Tvdb and Custom (MediaBrowser.Controller/Entities/TV/Series.cs:183).
        /// Setting Custom therefore keeps the key name-independent whatever the library option.
        /// </para>
        /// </remarks>
        private static bool ApplySourceIds(BaseItem item, string sourceKey)
        {
            var changed = SetProviderId(item, "VOD", sourceKey);
            changed |= SetProviderId(item, MetadataProvider.Custom.ToString(), sourceKey);

            var separator = sourceKey.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 || separator == sourceKey.Length - 1)
                return changed;

            var platform = sourceKey[..separator] switch
            {
                "youtube" => "Youtube",
                "arte" => "ArteTV",
                "ftv" => "FranceTV",
                "twitch" => "Twitch",
                _ => null
            };

            if (platform is not null)
                changed |= SetProviderId(item, platform, sourceKey[(separator + 1)..]);

            return changed;
        }

        private string? BuildSeriesOverview(SourceRecord source)
        {
            var definition = Plugin.Instance?.Configuration.Sources
                .Find(s => string.Equals(s.Url, source.Url, StringComparison.OrdinalIgnoreCase));

            var sb = new StringBuilder();
            sb.Append(SourceLine(source.Url));

            if (!string.IsNullOrWhiteSpace(definition?.Description))
                sb.Append("\n\n").Append(definition.Description);

            if (!string.IsNullOrWhiteSpace(source.SeriesSynopsis))
                sb.Append("\n\n").Append(source.SeriesSynopsis);

            return sb.ToString();
        }

        /// <summary>Formats a source or video URL as the localised "Source: …" line.</summary>
        private string SourceLine(string url)
            => string.Format(CultureInfo.InvariantCulture, _localizer.Get("SourceLabel"), url);

        /// <summary>
        /// Applies tags and genres, reporting whether either changed.
        /// </summary>
        /// <remarks>
        /// Returns a flag rather than assigning blindly because the reconciliation only commits an
        /// item when something actually differs: a field written without being compared would be
        /// computed on every pass and silently thrown away.
        /// </remarks>
        private static bool ApplyTags(
            BaseItem item, IReadOnlyList<string> tagValues, IReadOnlyList<string> categoryValues)
        {
            var tags = AsJellyfinStores(tagValues);
            var genres = AsJellyfinStores(categoryValues);

            var changed = !item.Tags.SequenceEqual(tags, StringComparer.Ordinal)
                || !item.Genres.SequenceEqual(genres, StringComparer.Ordinal);

            item.Tags = tags;
            item.Genres = genres;

            return changed;
        }

        /// <summary>
        /// Puts a list into the shape Jellyfin will store it in, so that what comes back on the next
        /// pass can compare equal to what was written.
        /// </summary>
        /// <remarks>
        /// Tags, genres and studios are each persisted as one <c>'|'</c>-joined column, with
        /// <c>Distinct(OrdinalIgnoreCase)</c> applied on write (<c>BaseItemMapper.cs:306</c>,
        /// <c>:333</c>, <c>:334</c>). So a value carrying a <c>'|'</c> comes back split in two, and two
        /// values differing only in case come back as one. Either way the item never compares equal to
        /// what was written, and is rewritten on every pass for ever — the counter reads
        /// <c>1 updated</c> on a source that has not changed in days. Seen live on a channel whose
        /// yt-dlp tags held both "Le monde de Jamy" and "Le Monde de Jamy".
        /// <para>
        /// Normalising here rather than where the metadata is parsed fixes rows already stored, which
        /// are never re-fetched: full metadata is only pulled for new playlist entries.
        /// </para>
        /// </remarks>
        private static string[] AsJellyfinStores(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return [];

            var kept = new List<string>(values.Count);
            foreach (var value in values)
            {
                var cleaned = value.Replace('|', '/');
                if (!string.IsNullOrEmpty(cleaned)
                    && !kept.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    kept.Add(cleaned);
                }
            }

            return [.. kept];
        }

        /// <summary>
        /// Publishes channels as Jellyfin studios, each qualified with the platform it came from.
        /// </summary>
        /// <remarks>
        /// The platform suffix is what stops two same-named channels on different platforms from
        /// collapsing into one studio. <see cref="VideoExtras.Channels"/> holds the bare names so
        /// the label can be rebuilt without migrating stored rows — but changing its format is not
        /// free: every label already published has a Studio item behind it, <c>Studio.CanDelete</c>
        /// is false, and nothing reaps the old ones. A relabel, like an upstream channel rename,
        /// therefore strands them permanently (under Jellyfin's metadata path, not the media tree).
        /// <para>
        /// Returns a flag rather than assigning blindly, for the reason given on
        /// <see cref="ApplyTags"/>.
        /// </para>
        /// </remarks>
        private static bool ApplyStudios(BaseItem item, string[] studios)
        {
            // Empty entries are dropped before comparing because Jellyfin cannot store an empty
            // studio list faithfully: it writes one as "" and reads it back as [""], splitting the
            // column without the IsNullOrWhiteSpace guard it applies to Genres and Tags
            // (BaseItemMapper.cs:147 against :115 and :148). Comparing raw would report a change on
            // every pass for every item with no studio — Arte publishes none — and rewrite it hourly.
            var current = item.Studios.Where(s => !string.IsNullOrEmpty(s));
            var changed = !current.SequenceEqual(studios, StringComparer.Ordinal);
            item.Studios = studios;

            return changed;
        }

        /// <summary>
        /// The date added to publish for one video: its release date when the option is on, otherwise
        /// the date the plugin first saw it.
        /// </summary>
        /// <remarks>
        /// Derived at publish time rather than stored, which is what makes the option reversible: both
        /// sources survive in the database, so flipping the setting either way just republishes with
        /// the other value.
        /// <para>
        /// For a live or scheduled broadcast, <see cref="VideoRecord.SyncedAt"/> is returned
        /// unconditionally: a broadcast keeps living, so it must stay first in "recently added" as
        /// new videos arrive after it started. This is a deliberate exception to the rule below.
        /// </para>
        /// <para>
        /// For an ordinary video (<c>NotLive</c>), only fields that are set once are read.
        /// <see cref="VideoRecord.SyncedAt"/> is deliberately NOT consulted as a fallback, however
        /// tempting: it is rewritten on every pass, so a date derived from it would move every pass
        /// and rewrite the item for ever. A row with neither a discovery nor a release date therefore
        /// yields null, and the item keeps whatever date it already carries — stable, and honest about
        /// having nothing to say.
        /// </para>
        /// </remarks>
        internal static DateTime? DateAddedFor(VideoRecord video, bool useReleaseDate)
        {
            // A live or scheduled stream must stay at the top of "recently added" while it lives, so
            // its date added is the last pass that saw it in that state. Deliberate exception to the
            // rule that the date added derives only from fields set once, and it outranks the
            // release-date option, which would otherwise put a scheduled item in the future.
            if (video.LiveStatus != LiveStatus.NotLive)
                return video.SyncedAt.UtcDateTime;

            var released = video.PublishedAt?.UtcDateTime;
            var discovered = video.FirstSeenAt?.UtcDateTime;
            var chosen = useReleaseDate ? released ?? discovered : discovered;

            // Null rather than MinValue, because MinValue is a poison value for this field: AddChild
            // reads it as "unset" and replaces it with UtcNow, so the next pass would compare that
            // timestamp against MinValue, differ, and rewrite the item on every pass for ever.
            return chosen == DateTime.MinValue ? null : chosen;
        }

        /// <summary>
        /// The dates to publish on a series: when it was added, and when it last gained content.
        /// </summary>
        /// <remarks>
        /// Both are assigned in both states of the option, and that is deliberate. A field written in
        /// only one direction is not reversible: turning the option on would write the release date and
        /// turning it off would leave it there for ever. The added date therefore follows the option,
        /// while the content date always reports the truth about acquisition, whatever the setting.
        /// <para>
        /// The two states do not aggregate the same way, and that asymmetry is the point. Off, the
        /// added date is when the series first appeared, so the OLDEST discovery wins. On, the whole
        /// purpose is to surface series with recent videos, so the NEWEST release wins.
        /// </para>
        /// <para>
        /// Aggregated over the videos being published for the source — the same set
        /// <c>OldestPublishedAt</c> uses — so a MaxVideosPerSource cap narrows it to what the library
        /// actually holds. The content date is never null: Jellyfin stores null there and reads it back
        /// as MinValue, which would rewrite the series on every pass.
        /// </para>
        /// </remarks>
        private static (DateTime? Added, DateTime LastContent) SeriesDates(
            IReadOnlyList<VideoRecord> videos, bool useReleaseDate)
        {
            DateTime? oldestSeen = null;
            var newestSeen = DateTime.MinValue;
            DateTime? newestReleased = null;

            foreach (var video in videos)
            {
                var seen = DateAddedFor(video, useReleaseDate: false);
                if (seen.HasValue)
                {
                    if (oldestSeen is null || seen.Value < oldestSeen.Value)
                        oldestSeen = seen.Value;
                    if (seen.Value > newestSeen)
                        newestSeen = seen.Value;
                }

                var released = DateAddedFor(video, useReleaseDate: true);
                if (released.HasValue && (newestReleased is null || released.Value > newestReleased.Value))
                    newestReleased = released.Value;
            }

            return (useReleaseDate ? newestReleased : oldestSeen, newestSeen);
        }

        /// <summary>
        /// Publishes the date added, leaving whatever the item already carries when there is none to
        /// publish.
        /// </summary>
        /// <remarks>
        /// Returns a flag rather than assigning blindly, for the reason given on
        /// <see cref="ApplyTags"/>.
        /// </remarks>
        private static bool ApplyDateAdded(BaseItem item, DateTime? dateAdded)
        {
            if (dateAdded is null)
                return false;

            var changed = item.DateCreated != dateAdded.Value;
            item.DateCreated = dateAdded.Value;

            return changed;
        }

        /// <summary>
        /// Declares the item's container as HLS, which every item's path — a stream route of this
        /// plugin — serves.
        /// </summary>
        /// <remarks>
        /// Jellyfin reads it as the media source container, and only adds <c>aac_adtstoasc</c> to a
        /// copied AAC track when that container is ts, aac or hls
        /// (<c>EncodingHelper.GetAudioBitStreamArguments</c>). Without it the mp4 muxer rejects the
        /// audio and the client falls back to a full transcode. Jellyfin's probe would set it, but a
        /// described item is never probed. Returns a flag for the reason given on
        /// <see cref="ApplyTags"/>.
        /// </remarks>
        private static bool ApplyContainer(BaseItem item)
        {
            var changed = !string.Equals(item.Container, HlsContainer, StringComparison.Ordinal);
            item.Container = HlsContainer;

            return changed;
        }

        /// <summary>
        /// Qualifies one video's channels with its platform. Falls back to the bare names when the
        /// extractor is unknown: there is then no platform to disambiguate against anyway.
        /// </summary>
        private static string[] StudiosFor(VideoRecord video)
        {
            var channels = video.Extras.Channels;
            if (channels.Count == 0)
                return [];

            var qualified = new string[channels.Count];
            for (var i = 0; i < channels.Count; i++)
            {
                qualified[i] = string.IsNullOrWhiteSpace(video.ExtractorKey)
                    ? channels[i]
                    : $"{channels[i]} ({video.ExtractorKey})";
            }

            return AsJellyfinStores(qualified);
        }

        /// <summary>
        /// Returns the studios for a source's videos, having made sure each one exists as a Studio
        /// item rather than a name no query can resolve.
        /// </summary>
        /// <remarks>
        /// Jellyfin materialises Studio items in a post-scan validator, which never runs for items
        /// published outside a scan. Until a library scan happens, a studio on a freshly published
        /// item is a dead link: the client navigates to its id, finds nothing, and sits on a loading
        /// spinner. <c>GetStudio</c> is the same call that validator makes — it returns the existing
        /// item when there is one, and otherwise creates it, writing a single directory under
        /// Jellyfin's own metadata path rather than into the media tree.
        /// </remarks>
        private string[] EnsureStudioEntities(IReadOnlyList<VideoRecord> videos)
        {
            var studios = DistinctStudios(videos);
            foreach (var studio in studios)
                _libraryManager.GetStudio(studio);

            return studios;
        }

        /// <summary>
        /// The studios across a source's videos, in first-seen order. A channel source yields a
        /// single one, but a playlist can gather videos from several uploaders.
        /// </summary>
        private static string[] DistinctStudios(IReadOnlyList<VideoRecord> videos)
        {
            var studios = new List<string>();
            foreach (var video in videos)
            {
                foreach (var studio in StudiosFor(video))
                {
                    // Each list is already deduplicated; this folds across videos, on the same terms.
                    if (!studios.Contains(studio, StringComparer.OrdinalIgnoreCase))
                        studios.Add(studio);
                }
            }

            return [.. studios];
        }

        private static string[] DeserialiseStrings(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return [];

            try
            {
                return JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch (JsonException)
            {
                // The column is ours to write, so a malformed value means a past bug rather than
                // hostile input: drop it instead of failing the whole sync.
                return [];
            }
        }

        /// <summary>
        /// The oldest publication date among a source's videos.
        /// </summary>
        /// <remarks>
        /// Used as the series' year. yt-dlp reports no creation date for a channel or a playlist —
        /// measured: `release_year` is null on every source shape we handle — so the earliest video
        /// this plugin has synced is the closest stand-in available.
        /// </remarks>
        private static DateTimeOffset? OldestPublishedAt(IReadOnlyList<VideoRecord> videos)
        {
            DateTimeOffset? oldest = null;
            foreach (var video in videos)
            {
                if (video.PublishedAt is { } published && (oldest is null || published < oldest))
                    oldest = published;
            }

            return oldest;
        }

        /// <summary>
        /// Returns a previously published anchor by id, or null when it no longer exists or is no
        /// longer a folder.
        /// </summary>
        /// <remarks>
        /// A stored anchor outlives the library it named. It only ever serves cleanup, so a caller
        /// must be able to carry on without it rather than fail a source whose own target is fine.
        /// </remarks>
        public Folder? FindAnchorById(Guid anchorId)
            => anchorId == Guid.Empty ? null : _libraryManager.GetItemById(anchorId) as Folder;

        /// <summary>
        /// The anchors other than a source's target that may still hold its items.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="source">The source being published.</param>
        /// <param name="record">The source's stored row, which remembers where it last published.</param>
        /// <returns>The anchors to sweep alongside the target.</returns>
        /// <remarks>
        /// Shared by the sync and the live probe: both reconcile the same source, and a pass that
        /// swept fewer anchors than the other left a duplicate behind until the other healed it.
        /// <para>
        /// Best effort by design: an anchor that no longer resolves is simply absent, because it
        /// only ever serves cleanup. The stored anchor covers the one case the other configured
        /// library cannot — changing a source's mode AND its library in the same edit leaves the old
        /// target as neither of the two currently configured.
        /// </para>
        /// </remarks>
        public IReadOnlyList<Folder> SweepAnchorsFor(
            PluginConfiguration config, SourceDefinition source, SourceRecord record)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(record);

            var anchors = new List<Folder>();

            var otherMode = source.Mode == SourceMode.Movies ? SourceMode.Series : SourceMode.Movies;
            var otherName = LibraryRouting.NameFor(config, otherMode);
            if (!string.IsNullOrWhiteSpace(otherName))
            {
                try
                {
                    anchors.Add(ResolveTarget(otherName).Anchor);
                }
                catch (InvalidOperationException ex)
                {
                    LogSweepAnchorUnavailable(otherName, ex.Message);
                }
            }

            if (record.PublishedAnchorId is { } anchorId && FindAnchorById(anchorId) is { } stored)
                anchors.Add(stored);

            return anchors;
        }

        /// <summary>
        /// Removes every trace of one source from the given anchors: its items, and the containers
        /// that held them.
        /// </summary>
        /// <param name="anchors">Everywhere this source may have published. Deduplicated here.</param>
        /// <param name="source">The source being retired.</param>
        /// <param name="allKnownVideoIds">
        /// Every video the database still knows. An item whose video appears here is claimed by
        /// another source and is spared.
        /// </param>
        /// <returns>How many items were removed.</returns>
        /// <remarks>
        /// Separate from <see cref="ReconcileAsync"/> rather than an empty-expectation call to it:
        /// there is no target to publish into and no mode to honour, and passing an arbitrary one
        /// only invited a comment explaining that it did not matter.
        /// </remarks>
        public int PurgeSource(
            IReadOnlyList<Folder> anchors,
            SourceRecord source,
            IReadOnlySet<string> allKnownVideoIds)
        {
            ArgumentNullException.ThrowIfNull(anchors);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(allKnownVideoIds);

            var swept = DistinctById(anchors);

            var removed = 0;
            foreach (var (vodId, items) in ExistingByVodId(swept))
            {
                if (allKnownVideoIds.Contains(vodId))
                    continue;

                foreach (var item in items)
                {
                    _libraryManager.DeleteItem(
                        item, new DeleteOptions { DeleteFileLocation = false }, true);
                    removed++;
                }
            }

            foreach (var anchor in swept)
                removed += ReconcileContainers(anchor, source, [], null);

            LogPurgedSource(source.Url, removed);
            return removed;
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "Reconciled {SourceUrl}: {Created} created, {Updated} updated, {Removed} removed, {Unchanged} unchanged")]
        private partial void LogReconciled(
            string sourceUrl, int created, int updated, int removed, int unchanged);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Library {LibraryName} has no indexed folder at {Location}; re-validating the top-level folders so Jellyfin indexes it")]
        private partial void LogIndexingLibrary(string libraryName, string location);

        [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
            Message = "Not sweeping library '{LibraryName}', it does not resolve: {Reason}")]
        private partial void LogSweepAnchorUnavailable(string libraryName, string reason);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information,
            Message = "Purged source {SourceUrl}: {Removed} item(s) removed")]
        private partial void LogPurgedSource(string sourceUrl, int removed);
    }
}

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
    public sealed record ReconcileReport(int Created, int Updated, int Removed, int Unchanged);

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

        /// <summary>The key a video's item id is derived from. Never contains a path.</summary>
        public string ItemKeyFor(string videoUuid) => $"vod:{videoUuid}";

        /// <summary>The marker that keeps a library location from reading as empty.</summary>
        private const string KeepMarkerName = ".keep";

        private const string KeepMarkerContent =
            "Prevent Jellyfin from skipping empty library folders needed for virtual libraries";

        /// <summary>
        /// Returns the library's first location. Resolves against the configuration only, so it
        /// answers whether the library exists without asking whether Jellyfin has indexed it.
        /// </summary>
        private string ResolveLocation(string libraryName)
        {
            var virtualFolder = _libraryManager.GetVirtualFolders()
                .Find(v => string.Equals(v.Name, libraryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No library named '{libraryName}'.");

            return virtualFolder.Locations.Length > 0
                ? virtualFolder.Locations[0]
                : throw new InvalidOperationException($"Library '{libraryName}' has no location.");
        }

        /// <summary>
        /// Returns the folder new items must hang off: the library's physical folder.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the library or its indexed folder is missing. Falling back to the
        /// CollectionFolder would publish items no query can reach, so this fails loudly.
        /// </exception>
        public Folder ResolveAnchor(string libraryName)
        {
            var location = ResolveLocation(libraryName);

            if (_libraryManager.FindByPath(location, true) is not Folder anchor)
            {
                throw new InvalidOperationException(
                    $"Library '{libraryName}' has no indexed folder at {location}. "
                    + "Scan the library once so Jellyfin indexes it before publishing.");
            }

            return anchor;
        }

        /// <summary>
        /// Resolves the anchor, letting Jellyfin index the library location first when it never has.
        /// </summary>
        /// <remarks>
        /// A scan skips a library folder that holds no filesystem entries: the check it gates on,
        /// <c>DirectoryService.IsAccessible</c>, is an emptiness test rather than an access test, and
        /// the early return it triggers means the folder item <see cref="ResolveAnchor"/> needs is
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
        /// <returns>The folder new items must hang off.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the library is missing, has no location, or could not be indexed.
        /// </exception>
        public async Task<Folder> PrepareAnchorAsync(string libraryName, CancellationToken cancellationToken)
        {
            var location = ResolveLocation(libraryName);

            EnsureKeepMarker(location);

            if (_libraryManager.FindByPath(location, true) is Folder indexed)
                return indexed;

            LogIndexingLibrary(libraryName, location);
            await _libraryManager.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);

            // Reuses the "has no indexed folder" message when the validation still produced nothing.
            return ResolveAnchor(libraryName);
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
        public async Task<ReconcileReport> ReconcileAsync(
            Folder anchor,
            SourceRecord source,
            SourceMode mode,
            string seriesName,
            IReadOnlyList<VideoRecord> expected,
            IReadOnlySet<string> allKnownVideoIds,
            string localBaseUrl,
            bool useReleaseDateAsDateAdded,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(allKnownVideoIds);

            var published = ExistingByVodId(anchor);
            var studios = EnsureStudioEntities(expected);

            Series? series = null;
            Season? currentSeason = null;
            int created = 0, updated = 0, unchanged = 0;

            foreach (var video in expected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var streamUrl = $"{localBaseUrl.TrimEnd('/')}/VOD/stream/{video.Id}.m3u8";

                UpsertOutcome outcome;
                if (mode == SourceMode.Movies)
                {
                    outcome = await UpsertMovieAsync(
                        anchor, video, streamUrl, published, useReleaseDateAsDateAdded,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    series ??= await EnsureSeriesAsync(
                        anchor, source, seriesName, FirstAvailableThumbnail(expected),
                        OldestPublishedAt(expected), studios,
                        SeriesDates(expected, useReleaseDateAsDateAdded), cancellationToken)
                        .ConfigureAwait(false);
                    currentSeason = await EnsureSeasonAsync(series, video.SeasonNumber, currentSeason, cancellationToken).ConfigureAwait(false);

                    outcome = await UpsertEpisodeAsync(
                        series, currentSeason, video, streamUrl, published,
                        useReleaseDateAsDateAdded, cancellationToken)
                        .ConfigureAwait(false);
                }

                switch (outcome)
                {
                    case UpsertOutcome.Created:
                        created++;
                        break;
                    case UpsertOutcome.Updated:
                        updated++;
                        break;
                    default:
                        unchanged++;
                        break;
                }

                published.Remove(video.Id);
            }

            // An item is orphaned only when no source claims its video any more. Items
            // belonging to other sources are in this map too, because the query is
            // library-wide while `expected` is per-source.
            var removed = 0;
            foreach (var (vodId, orphan) in published)
            {
                if (allKnownVideoIds.Contains(vodId))
                    continue;

                _libraryManager.DeleteItem(
                    orphan, new DeleteOptions { DeleteFileLocation = false }, true);
                removed++;
            }

            // A Movies source must own no containers at all, so the same reconciliation runs with an
            // empty expectation: it reaps the series and seasons a previous Series-mode run left
            // behind when the source's Mode was flipped, under the same episode-count guard.
            removed += mode == SourceMode.Movies
                ? ReconcileContainers(anchor, source, [], null)
                : ReconcileContainers(anchor, source, expected, series);

            LogReconciled(source.Url, created, updated, removed, unchanged);
            return new ReconcileReport(created, updated, removed, unchanged);
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
        /// One query for the whole library, indexed by the VOD provider id.
        /// </summary>
        /// <remarks>
        /// Scoped by TopParentIds, which assumes the library is dedicated to the plugin.
        /// Series carry a source-shaped VOD id rather than a video uuid, so filtering on the
        /// item kinds keeps them out of the map.
        /// </remarks>
        private Dictionary<string, BaseItem> ExistingByVodId(Folder anchor)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                TopParentIds = [anchor.Id],
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                Recursive = true
            });

            var map = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var id = item.GetProviderId("VOD");
                if (!string.IsNullOrEmpty(id))
                    map[id] = item;
            }

            return map;
        }

        /// <summary>Publishes one video as a Movie and reports what that did to the library.</summary>
        private async Task<UpsertOutcome> UpsertMovieAsync(
            Folder anchor,
            VideoRecord video,
            string streamUrl,
            Dictionary<string, BaseItem> published,
            bool useReleaseDateAsDateAdded,
            CancellationToken cancellationToken)
        {
            var isNew = !published.TryGetValue(video.Id, out var existing);

            // A source whose Mode was flipped has the video published as the other kind. The id is
            // derived from the type, so the new item cannot reuse the old one's row: the stale item
            // has to go, or it stays in the library with nothing accounting for it.
            if (existing is not null and not Movie)
            {
                _libraryManager.DeleteItem(
                    existing, new DeleteOptions { DeleteFileLocation = false }, true);
                isNew = true;
                existing = null;
            }

            var movie = existing as Movie ?? new Movie
            {
                Id = _libraryManager.GetNewItemId(ItemKeyFor(video.Id), typeof(Movie))
            };

            var overview = BuildVideoOverview(video);
            var premiereDate = video.PublishedAt?.UtcDateTime;
            var productionYear = video.PublishedAt?.UtcDateTime.Year;
            var runTimeTicks = RunTimeTicks(video);
            var dateAdded = DateAddedFor(video, useReleaseDateAsDateAdded);

            var changed = isNew
                || !string.Equals(movie.Name, video.Title, StringComparison.Ordinal)
                || !string.Equals(movie.Overview, overview, StringComparison.Ordinal)
                || movie.ParentId != anchor.Id
                || !string.Equals(movie.Path, streamUrl, StringComparison.Ordinal)
                || movie.PremiereDate != premiereDate
                || movie.ProductionYear != productionYear
                || movie.RunTimeTicks != runTimeTicks;

            movie.Name = video.Title;
            movie.Overview = overview;
            movie.ParentId = anchor.Id;
            movie.Path = streamUrl;
            movie.PremiereDate = premiereDate;
            movie.ProductionYear = productionYear;
            movie.RunTimeTicks = runTimeTicks;
            changed |= ApplyDateAdded(movie, dateAdded);
            changed |= ApplyVideoIds(movie, video);
            changed |= ApplyTags(movie, video.Tags, video.Categories);
            changed |= ApplyStudios(movie, StudiosFor(video));

            var presentationKey = movie.CreatePresentationUniqueKey();
            changed |= !string.Equals(movie.PresentationUniqueKey, presentationKey, StringComparison.Ordinal);
            movie.PresentationUniqueKey = presentationKey;

            changed |= ApplyImage(movie, ImageType.Primary, video.ThumbnailUrl, video.PublishedImageUrl,
                url => _db.SetPublishedImageUrl(video.Id, url), out var imageWrite);
            // The still is landscape, so it serves as the Thumb too; Jellyfin crops it for the poster
            // grid. A generated 2:3 poster only steps in when there is no still at all.
            changed |= ApplyImage(movie, ImageType.Thumb, video.ThumbnailUrl, video.PublishedImageUrl, null, out _);
            changed |= EnsurePoster(movie, ItemKeyFor(video.Id));

            if (!changed)
                return UpsertOutcome.Unchanged;

            await CommitAsync(movie, anchor, isNew, cancellationToken).ConfigureAwait(false);
            imageWrite?.Invoke();
            return isNew ? UpsertOutcome.Created : UpsertOutcome.Updated;
        }

        /// <summary>Publishes one video as an Episode and reports what that did to the library.</summary>
        private async Task<UpsertOutcome> UpsertEpisodeAsync(
            Series series,
            Season season,
            VideoRecord video,
            string streamUrl,
            Dictionary<string, BaseItem> published,
            bool useReleaseDateAsDateAdded,
            CancellationToken cancellationToken)
        {
            var isNew = !published.TryGetValue(video.Id, out var existing);

            // A source whose Mode was flipped has the video published as the other kind. The id is
            // derived from the type, so the new item cannot reuse the old one's row: the stale item
            // has to go, or it stays in the library with nothing accounting for it.
            if (existing is not null and not Episode)
            {
                _libraryManager.DeleteItem(
                    existing, new DeleteOptions { DeleteFileLocation = false }, true);
                isNew = true;
                existing = null;
            }

            var episode = existing as Episode ?? new Episode
            {
                Id = _libraryManager.GetNewItemId(ItemKeyFor(video.Id), typeof(Episode))
            };

            var overview = BuildVideoOverview(video);
            var premiereDate = video.PublishedAt?.UtcDateTime;
            var productionYear = video.PublishedAt?.UtcDateTime.Year;
            var runTimeTicks = RunTimeTicks(video);
            var dateAdded = DateAddedFor(video, useReleaseDateAsDateAdded);
            // Queries tie an episode to its series through this field, not through ParentId.
            var seriesKey = series.GetPresentationUniqueKey();

            var changed = isNew
                || !string.Equals(episode.Name, video.Title, StringComparison.Ordinal)
                || !string.Equals(episode.Overview, overview, StringComparison.Ordinal)
                || episode.ParentId != season.Id
                || !string.Equals(episode.Path, streamUrl, StringComparison.Ordinal)
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
            episode.Path = streamUrl;
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
            changed |= ApplyVideoIds(episode, video);
            changed |= ApplyTags(episode, video.Tags, video.Categories);
            changed |= ApplyStudios(episode, StudiosFor(video));

            var presentationKey = episode.CreatePresentationUniqueKey();
            changed |= !string.Equals(episode.PresentationUniqueKey, presentationKey, StringComparison.Ordinal);
            episode.PresentationUniqueKey = presentationKey;

            // An episode's Primary is its still, by Jellyfin convention — landscape, not a poster.
            changed |= ApplyImage(episode, ImageType.Primary, video.ThumbnailUrl, video.PublishedImageUrl,
                url => _db.SetPublishedImageUrl(video.Id, url), out var imageWrite);
            changed |= ApplyImage(episode, ImageType.Thumb, video.ThumbnailUrl, video.PublishedImageUrl, null, out _);

            if (!changed)
                return UpsertOutcome.Unchanged;

            await CommitAsync(episode, season, isNew, cancellationToken).ConfigureAwait(false);
            imageWrite?.Invoke();
            return isNew ? UpsertOutcome.Created : UpsertOutcome.Updated;
        }

        /// <summary>
        /// Creates through AddChild, updates through the repository.
        /// </summary>
        /// <remarks>
        /// AddChild raises ItemAdded every time it is called, so it is reserved for genuine
        /// creations; updating an existing item through it would re-fire the event each sync.
        /// </remarks>
        private static async Task CommitAsync(
            BaseItem item, Folder parent, bool isNew, CancellationToken cancellationToken)
        {
            if (isNew)
                parent.AddChild(item);
            else
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
        }

        private async Task<Series> EnsureSeriesAsync(
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

            var changed = existing is null
                || !string.Equals(series.Name, seriesName, StringComparison.Ordinal)
                || !string.Equals(series.Overview, overview, StringComparison.Ordinal)
                || series.ParentId != anchor.Id
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
            changed |= ApplyTags(series, source.Tags, null);
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
            var seriesImage = string.IsNullOrEmpty(source.SeriesThumbnailUrl)
                ? fallbackImageUrl
                : source.SeriesThumbnailUrl;

            changed |= ApplyImage(series, ImageType.Primary, seriesImage, source.PublishedImageUrl,
                url => _db.SetSourcePublishedImageUrl(source.Url, url), out var primaryWrite);
            changed |= ApplyImage(series, ImageType.Thumb, source.SeriesBannerUrl ?? seriesImage,
                source.PublishedBannerUrl,
                url => _db.SetSourcePublishedBannerUrl(source.Url, url), out var bannerWrite);
            changed |= EnsurePoster(series, key);

            if (changed)
                await CommitAsync(series, anchor, existing is null, cancellationToken)
                    .ConfigureAwait(false);
            primaryWrite?.Invoke();
            bannerWrite?.Invoke();

            return series;
        }

        private async Task<Season> EnsureSeasonAsync(
            Series series, int? seasonNumber, Season? current, CancellationToken cancellationToken)
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
                await CommitAsync(season, series, existing is null, cancellationToken)
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
        private static string? FirstAvailableThumbnail(IReadOnlyList<VideoRecord> videos)
        {
            foreach (var v in videos)
            {
                if (!string.IsNullOrEmpty(v.ThumbnailUrl))
                    return v.ThumbnailUrl;
            }

            return null;
        }

        /// <summary>
        /// Builds a video's Overview: the source URL, then the live notice when it applies, then the
        /// description.
        /// </summary>
        /// <remarks>
        /// The live mention is composed here rather than stored, so a stream that has ended stops
        /// advertising itself as live as soon as the next sync clears the flag.
        /// </remarks>
        private string BuildVideoOverview(VideoRecord video)
        {
            var sb = new StringBuilder();
            sb.Append(SourceLine(video.VideoUrl));

            if (video.IsLive)
                sb.Append("\n\n").Append(_localizer.Get("LiveNow"));

            if (!string.IsNullOrEmpty(video.Description))
                sb.Append("\n\n").Append(video.Description);

            return sb.ToString();
        }

        private static long? RunTimeTicks(VideoRecord video)
            => video.DurationSeconds.HasValue
                ? video.DurationSeconds.Value * TimeSpan.TicksPerSecond
                : null;

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
        private static bool ApplyTags(BaseItem item, string? tagsJson, string? categoriesJson)
        {
            var tags = AsJellyfinStores(DeserialiseStrings(tagsJson));
            var genres = AsJellyfinStores(DeserialiseStrings(categoriesJson));

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
        private static string[] AsJellyfinStores(string[] values)
        {
            if (values.Length == 0)
                return values;

            var kept = new List<string>(values.Length);
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
        /// collapsing into one studio. <see cref="VideoRecord.Channels"/> holds the bare names so
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
        /// Reads only fields that are set once. <see cref="VideoRecord.SyncedAt"/> is deliberately NOT
        /// consulted as a fallback, however tempting: it is rewritten on every pass, so a date derived
        /// from it would move every pass and rewrite the item for ever. A row with neither a discovery
        /// nor a release date therefore yields null, and the item keeps whatever date it already
        /// carries — stable, and honest about having nothing to say.
        /// </para>
        /// </remarks>
        internal static DateTime? DateAddedFor(VideoRecord video, bool useReleaseDate)
        {
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
        /// Qualifies one video's channels with its platform. Falls back to the bare names when the
        /// extractor is unknown: there is then no platform to disambiguate against anyway.
        /// </summary>
        private static string[] StudiosFor(VideoRecord video)
        {
            var channels = DeserialiseStrings(video.Channels);
            if (channels.Length == 0)
                return channels;

            var qualified = new string[channels.Length];
            for (var i = 0; i < channels.Length; i++)
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

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "Reconciled {SourceUrl}: {Created} created, {Updated} updated, {Removed} removed, {Unchanged} unchanged")]
        private partial void LogReconciled(
            string sourceUrl, int created, int updated, int removed, int unchanged);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Library {LibraryName} has no indexed folder at {Location}; re-validating the top-level folders so Jellyfin indexes it")]
        private partial void LogIndexingLibrary(string libraryName, string location);
    }
}

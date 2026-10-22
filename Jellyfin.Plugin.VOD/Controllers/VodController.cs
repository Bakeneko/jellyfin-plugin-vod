using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Controllers
{
    /// <summary>
    /// VOD streaming endpoints.
    /// </summary>
    /// <remarks>
    /// No global <c>[Authorize]</c>: ffmpeg and media players call the stream endpoint without a
    /// Jellyfin token. Admin endpoints carry their own <c>[Authorize]</c>.
    /// </remarks>
    [ApiController]
    [Route("VOD")]
    public sealed partial class VodController : ControllerBase
    {
        private readonly IVodDatabase _db;
        private readonly YtDlpService _ytDlp;
        private readonly PlaylistCache _cache;
        private readonly SubtitleTokens _subtitles;
        private readonly MediaStreamWriter _streamWriter;
        private readonly StreamDescriber _describer;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly LibraryPublisher _publisher;
        private readonly ILogger<VodController> _logger;

        /// <summary>How many videos one source's detail panel is given at most.</summary>
        /// <remarks>
        /// The biggest source measured on the author's instance holds 7449 videos, and the panel
        /// renders every row it receives as a div, a two-row table and a button. Uncapped, one
        /// click ships every video's verbatim stderr in a single response and freezes the browser.
        /// Two hundred is far more than anyone reads and still a page the browser builds instantly;
        /// the videos needing attention are at the top, so the cap never hides a failure.
        /// </remarks>
        private const int SourceDetailCap = 200;

        /// <summary>Initializes a new instance of the <see cref="VodController"/> class.</summary>
        /// <param name="db">The VOD database used to look up videos by UUID.</param>
        /// <param name="ytDlp">The yt-dlp service used to resolve stream URLs.</param>
        /// <param name="cache">The playlist cache.</param>
        /// <param name="subtitles">Mints and resolves the subtitle proxy tokens.</param>
        /// <param name="streamWriter">Stores media streams so Jellyfin does not have to re-probe.</param>
        /// <param name="describer">Describes the version group's streams before they are played.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="libraryManager">Used to list the libraries the plugin may publish into.</param>
        /// <param name="publisher">Used to check that the configured library still resolves.</param>
        /// <param name="logger">The logger.</param>
        public VodController(
            IVodDatabase db,
            YtDlpService ytDlp,
            PlaylistCache cache,
            SubtitleTokens subtitles,
            MediaStreamWriter streamWriter,
            StreamDescriber describer,
            IHttpClientFactory httpClientFactory,
            ILibraryManager libraryManager,
            LibraryPublisher publisher,
            ILogger<VodController> logger)
        {
            _db = db;
            _ytDlp = ytDlp;
            _cache = cache;
            _subtitles = subtitles;
            _streamWriter = streamWriter;
            _describer = describer;
            _httpClientFactory = httpClientFactory;
            _libraryManager = libraryManager;
            _publisher = publisher;
            _logger = logger;
        }

        /// <summary>
        /// Resolves a video by its opaque UUID and returns a filtered HLS master playlist.
        /// </summary>
        /// <param name="id">Opaque UUID from the <c>videos</c> table.</param>
        /// <param name="format">yt-dlp format selector. Default picks any HLS video stream.</param>
        /// <param name="height">Exact variant height to serve, or null for the default tier.</param>
        /// <param name="codec">Exact variant codec to serve, or null for the default tier.</param>
        /// <param name="audioLangs">Comma-separated language prefixes to keep beside the original audio. Falls back to the configured value.</param>
        /// <param name="subLangs">Comma-separated subtitle language prefixes to include. Falls back to the configured value.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// The route ends in <c>.m3u8</c> so that <c>MediaSourceManager.SupportsDirectStream</c> returns
        /// false — Jellyfin must not hand this URL to a remote client (CDN segments are signed for the
        /// server's IP; loopback resolves only inside the container).
        ///
        /// Audio and subtitle parameters are nullable so an absent one can fall back to the configured
        /// value: Jellyfin builds these URLs itself and passes none of them, so without the fallback
        /// the administrator's settings would never reach playback. Absent and "?audioLangs=" are
        /// NOT distinguishable here — MVC binds an empty query value to null through
        /// ModelMetadata.ConvertEmptyStringToNull — so both take the configured value.
        /// </remarks>
        [HttpGet("stream/{id}.m3u8")]
        public async Task<ActionResult> GetStream(
            [FromRoute] string id,
            [FromQuery] string format = StreamCacheKey.DefaultFormat,
            [FromQuery] int? height = null,
            [FromQuery] string? codec = null,
            [FromQuery] string? audioLangs = null,
            [FromQuery] string? subLangs = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsWellFormedSelection(height, codec))
            {
                return BadRequest(new
                {
                    error = "height and codec must be supplied together, or neither",
                    height,
                    codec
                });
            }

            var config = Plugin.Instance?.Configuration;
            var effectiveAudioLangs = audioLangs ?? StreamCacheKey.JoinLanguages(config?.PreferredAudioLanguages);
            var effectiveSubLangs = subLangs ?? StreamCacheKey.JoinLanguages(config?.PreferredSubtitleLanguages);

            var video = _db.GetVideoByUuid(id);
            if (video is null)
            {
                return NotFound(new { error = "Unknown video id", id });
            }

            if (!IsStreamable(video))
            {
                // A scheduled stream has no formats at all, so resolving it would fail deep in
                // yt-dlp. Refusing here keeps the failure readable.
                return NotFound(new { error = "This broadcast has not started yet.", id });
            }

            var cacheKey = StreamCacheKey.For(
                id, format, height, codec, effectiveAudioLangs, effectiveSubLangs);

            if (_cache.TryGet(cacheKey, out var cached))
            {
                return Content(cached, "application/vnd.apple.mpegurl");
            }

            var resolution = await _ytDlp
                .ResolveAsync(video.VideoUrl, format, cancellationToken)
                .ConfigureAwait(false);

            if (resolution is null)
            {
                return StatusCode(503, new { error = "Video unavailable", videoUrl = video.VideoUrl });
            }

            if (!resolution.IsManifest)
            {
                LogNoManifest(video.VideoUrl, format);
                return Redirect(resolution.Url);
            }

            var master = await FilterUpstreamAsync(
                    resolution.Url, height, codec, effectiveAudioLangs, effectiveSubLangs,
                    cancellationToken)
                .ConfigureAwait(false);

            if (master is null)
            {
                return height is null
                    ? Redirect(resolution.Url)
                    : StatusCode(503, new
                    {
                        error = "Could not serve the requested quality: the tier is no longer offered upstream, or the upstream playlist could not be fetched",
                        height,
                        codec
                    });
            }

            // Everything downstream is keyed or filtered for the default master, so a request that
            // chose another one may be served but must leave no trace. A custom-format caller
            // re-resolves on every request, which is that caller's own cost and not the default
            // playback path.
            if (DescribesTheServedMaster(format))
            {
                _cache.Set(cacheKey, master.Filtered);

                // Cache miss only: this bounds the work to roughly one per playlist TTL. Labels are
                // applied inline because they are immediate and cheap; the description itself is a
                // probe per member, so it runs in the background and never delays this response.
                try
                {
                    var streams = MediaStreamBuilder.Build(master.Filtered);
                    _streamWriter.TryWrite(
                        id, height, codec, streams,
                        StreamBitRates.VariantBandwidth(master.Filtered), cancellationToken);
                }
#pragma warning disable CA1031 // nothing about persisting track labels is worth failing playback for
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogStreamPersistFailed(ex, id);
                }
            }

            DescribeInBackground(config, id, master, format);

            return Content(master.Filtered, "application/vnd.apple.mpegurl");
        }

        /// <summary>Returns a subtitle playlist whose segment ffmpeg will accept.</summary>
        [HttpGet("subtitles/{token}.m3u8")]
        public async Task<ActionResult> GetSubtitlePlaylist(
            [FromRoute] string token,
            CancellationToken cancellationToken = default)
        {
            if (!_subtitles.TryResolve(token, out var upstreamUrl))
            {
                return NotFound(new { error = "Unknown or expired subtitle token", token });
            }

            var playlist = await FetchStringAsync(upstreamUrl, cancellationToken)
                .ConfigureAwait(false);
            if (playlist is null)
            {
                return StatusCode(503, new { error = "Could not fetch subtitle playlist" });
            }

            var sb = new StringBuilder();
            foreach (var line in playlist.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                sb.Append(line.StartsWith('#') ? line : $"{token}.vtt").Append('\n');
            }

            return Content(sb.ToString(), "application/vnd.apple.mpegurl");
        }

        /// <summary>Proxies the subtitle WebVTT text, under a route ffmpeg recognises by its extension.</summary>
        [HttpGet("subtitles/{token}.vtt")]
        public async Task<ActionResult> GetSubtitleText(
            [FromRoute] string token,
            CancellationToken cancellationToken = default)
        {
            if (!_subtitles.TryResolve(token, out var upstreamUrl))
            {
                return NotFound(new { error = "Unknown or expired subtitle token", token });
            }

            var playlist = await FetchStringAsync(upstreamUrl, cancellationToken).ConfigureAwait(false);
            var segment = playlist?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !l.StartsWith('#'));

            if (segment is null)
            {
                return StatusCode(503, new { error = "Could not resolve subtitle segment" });
            }

            var segmentUri = new Uri(new Uri(upstreamUrl), segment);
            var vtt = await FetchStringAsync(segmentUri.ToString(), cancellationToken).ConfigureAwait(false);

            return vtt is null
                ? StatusCode(503, new { error = "Could not fetch subtitle text" })
                : Content(vtt, "text/vtt");
        }

        /// <summary>
        /// Lists the Jellyfin libraries an administrator may publish into.
        /// </summary>
        /// <returns>Library names, sorted for display.</returns>
        /// <remarks>
        /// The configuration page offers these instead of a free-text field: a name that does not
        /// match a library exactly makes every sync fail in ResolveTarget, and the error only
        /// surfaces when the scheduled task next runs.
        /// </remarks>
        [HttpGet("libraries")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult<IReadOnlyList<LibraryOption>> GetLibraries()
            => Ok(ProjectLibraryOptions(_libraryManager.GetVirtualFolders()));

        /// <summary>
        /// Reduces Jellyfin's virtual folders to the options the configuration page offers.
        /// </summary>
        /// <param name="folders">The libraries Jellyfin knows about.</param>
        /// <returns>Usable libraries, sorted for display; never null.</returns>
        /// <remarks>
        /// The acceptance flags are computed here rather than in the page, so the rule deciding which
        /// library may hold what lives in one place and can be tested.
        /// </remarks>
        internal static IReadOnlyList<LibraryOption> ProjectLibraryOptions(
            IEnumerable<VirtualFolderInfo> folders)
            => folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.Name))
                .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(folder => new LibraryOption(
                    folder.Name,
                    folder.CollectionType?.ToString(),
                    LibraryTypeRules.AcceptsMovies(folder.CollectionType),
                    LibraryTypeRules.AcceptsSeries(folder.CollectionType)))
                .ToList();

        /// <summary>
        /// Reports what each configured source last did, so the configuration page can show it.
        /// </summary>
        /// <returns>The library in use, why it is unusable if it is, and one row per source.</returns>
        /// <remarks>
        /// The library is resolved live rather than read from a stored flag: the failure this
        /// surfaces is a library that was renamed or deleted after the fact, so a value recorded at
        /// the last sync would describe a world that no longer exists.
        /// </remarks>
        [HttpGet("sources/status")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public async Task<ActionResult<SourcesStatus>> GetSourcesStatus(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            IReadOnlyList<LibraryStatus> libraries;
            if (config is null)
            {
                libraries = [];
            }
            else
            {
                libraries =
                [
                    await ResolveLibraryStatusAsync(config, SourceMode.Movies, cancellationToken).ConfigureAwait(false),
                    await ResolveLibraryStatusAsync(config, SourceMode.Series, cancellationToken).ConfigureAwait(false),
                ];
            }

            // Read from the configuration, not the database: a collision is a configuration
            // mistake, and both colliding sources may be perfectly healthy rows on their own.
            var collisions = config is null
                ? []
                : VodNativeId.FindCollisions(config.Sources.Select(s => s.Url));

            var warnings = BuildWarnings(config, _db.GetOutcomeCounts());

            return Ok(new SourcesStatus(
                libraries,
                ProjectSourceStatus(_db.GetAllSources()),
                collisions,
                warnings));
        }

        /// <summary>Resolves one library role live and reduces it to what the page shows.</summary>
        private async Task<LibraryStatus> ResolveLibraryStatusAsync(
            PluginConfiguration config, SourceMode mode, CancellationToken cancellationToken)
        {
            var role = LibraryRouting.RoleFor(mode);
            var name = LibraryRouting.NameFor(config, mode);
            var needed = LibraryRouting.IsNeeded(config, mode);

            if (string.IsNullOrWhiteSpace(name))
                return ProjectLibraryStatus(role, name, needed, null, $"No {role} library is configured.");

            try
            {
                var target = await _publisher.PrepareTargetAsync(name, cancellationToken).ConfigureAwait(false);
                return ProjectLibraryStatus(role, name, needed, target.CollectionType, null);
            }
            catch (InvalidOperationException ex)
            {
                return ProjectLibraryStatus(role, name, needed, null, ex.Message);
            }
        }

        /// <summary>
        /// Reduces one resolved library role to the row the configuration page shows.
        /// </summary>
        /// <param name="role">The setting this row describes.</param>
        /// <param name="name">The configured name.</param>
        /// <param name="needed">Whether an enabled source publishes into it.</param>
        /// <param name="collectionType">Its collection type, or null when it did not resolve.</param>
        /// <param name="error">Why it cannot be published into, or null.</param>
        /// <returns>The row, with a type warning added when the type does not fit the role.</returns>
        internal static LibraryStatus ProjectLibraryStatus(
            string role, string name, bool needed, CollectionTypeOptions? collectionType, string? error)
        {
            // A library that did not resolve has no type worth judging, and a second message would
            // only bury the one that matters.
            if (error is not null)
                return new LibraryStatus(role, name, needed, error, null);

            var accepts = string.Equals(role, LibraryRouting.MoviesRole, StringComparison.Ordinal)
                ? LibraryTypeRules.AcceptsMovies(collectionType)
                : LibraryTypeRules.AcceptsSeries(collectionType);

            var warning = accepts
                ? null
                : $"Library '{name}' is typed {collectionType} and will display {role} badly.";

            return new LibraryStatus(role, name, needed, null, warning);
        }

        /// <summary>
        /// Reduces stored source rows to what the configuration table displays.
        /// </summary>
        /// <param name="records">The sources the database knows about.</param>
        /// <returns>One row per source, in the order the database returned them; never null.</returns>
        internal static IReadOnlyList<SourceStatus> ProjectSourceStatus(
            IEnumerable<SourceRecord> records)
            => records
                .Select(r => new SourceStatus(
                    r.Url,
                    r.Name,
                    r.ShortListedAt,
                    r.FullListedAt,
                    r.VideoCount,
                    r.ListingCount,
                    r.ShortListingResult,
                    r.ShortListingDetail,
                    r.ShortListingFailures,
                    r.FullListingResult,
                    r.FullListingDetail,
                    r.FullListingFailures,
                    r.CleanupResult,
                    r.CleanupDetail,
                    r.CleanupFailures,
                    r.WaveResult,
                    r.WaveDetail,
                    r.WaveFailures,
                    r.CheckupResult,
                    r.CheckupDetail,
                    r.CheckupFailures))
                .ToList();

        /// <summary>
        /// Reduces stored video rows to what the source detail page displays.
        /// </summary>
        /// <param name="records">The videos belonging to the source.</param>
        /// <returns>One row per video, in the order the database returned them; never null.</returns>
        internal static IReadOnlyList<VideoStatus> ProjectVideoStatus(
            IEnumerable<VideoRecord> records)
            => records
                .Select(r => new VideoStatus(
                    r.Id,
                    r.Title,
                    r.VideoUrl,
                    r.LiveStatus,
                    r.PublishedAt,
                    r.SeasonNumber,
                    r.EpisodeNumber,
                    r.MetadataResult,
                    r.MetadataDetail,
                    r.MetadataAttempts,
                    r.MetadataAt,
                    r.DescribeResult,
                    r.DescribeDetail,
                    r.DescribeAttempts,
                    r.DescribedAt))
                .ToList();

        /// <summary>
        /// Reports the videos of one source, with everything that happened to each one.
        /// </summary>
        /// <param name="url">The source URL (query parameter).</param>
        /// <returns>
        /// The videos needing attention first, then the most recent, capped — with the total so the
        /// page can say what it is leaving out. <c>404</c> when the source URL is not known.
        /// </returns>
        /// <remarks>
        /// The order is the panel's whole purpose. The database returns its rows oldest publication
        /// date first, undated ones ahead of those, so on a real channel the one video that failed
        /// today sits below hundreds of healthy ones. A non-zero failure counter is this project's
        /// own definition of a failure — a conclusion such as "no HLS manifest" keeps its counter at
        /// zero and is not an error — so that counter is what sorts.
        /// </remarks>
        [HttpGet("sources/detail")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult<SourceDetail> GetSourceDetail([FromQuery] string url)
        {
            if (_db.GetSource(url) is null)
                return NotFound();

            var all = _db.GetVideosBySource(url);

            var ordered = all
                .OrderByDescending(r => r.MetadataAttempts + r.DescribeAttempts)
                .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(SourceDetailCap);

            return Ok(new SourceDetail(all.Count, ProjectVideoStatus(ordered)));
        }

        /// <summary>
        /// Resets a video's clocks and counters, making it immediately eligible for the next wave.
        /// </summary>
        /// <param name="id">The video UUID (primary key).</param>
        /// <returns>
        /// <c>204</c> on success, or <c>404</c> when the video UUID is not known.
        /// </returns>
        /// <remarks>
        /// The manual exit when the external cause is known to be fixed — re-exported cookies,
        /// typically. It never lifts the platform backoff the throttle holds: working around that
        /// would restart the rate limiting that already hit this project. The throttle's state is
        /// not exposed, so there is deliberately no test for this invariant — the constraint is
        /// structural, not conditional.
        /// </remarks>
        [HttpPost("videos/{id}/reset")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult ResetVideo([FromRoute] string id)
        {
            // An unknown id must return 404, not a silent 200: an administrator who clicks Reset
            // and sees nothing cannot tell "done" from "the id matched nothing".
            if (_db.GetVideoByUuid(id) is null)
                return NotFound(new { error = "Unknown video id", id });

            _db.ResetVideo(id);
            return NoContent();
        }

        /// <summary>
        /// Resets a source's clocks, stage counters, and all of its videos.
        /// </summary>
        /// <param name="url">The source URL (primary key), as a query parameter.</param>
        /// <returns>
        /// <c>204</c> on success, or <c>404</c> when the source URL is not known.
        /// </returns>
        /// <remarks>
        /// The stage counters go too: without them the source would still carry the failure count
        /// that made it look broken while its videos were already eligible again. It never lifts
        /// the platform backoff the throttle holds: working around that would restart the rate
        /// limiting. The throttle's state is not exposed, so there is deliberately no test for
        /// this invariant — the constraint is structural, not conditional.
        /// </remarks>
        [HttpPost("sources/reset")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult ResetSource([FromQuery] string url)
        {
            // An unknown URL must return 404, not a silent 200.
            if (_db.GetSource(url) is null)
                return NotFound(new { error = "Unknown source URL", url });

            _db.ResetSource(url);
            return NoContent();
        }

        /// <summary>
        /// Builds the list of configuration warnings to surface on the status page.
        /// </summary>
        /// <param name="config">The current plugin configuration, or null when not initialised.</param>
        /// <param name="outcomeCounts">Video counts per metadata outcome, as returned by the database.</param>
        /// <returns>Zero or more warning messages, never null.</returns>
        /// <remarks>
        /// Two warnings are emitted:
        /// <list type="bullet">
        /// <item><description>
        /// A NeedsAuth warning when at least one video carries <see cref="CallOutcome.NeedsAuth"/>
        /// AND a cookies file is configured. Both conditions must hold: a single age-restricted video
        /// on an instance with no cookies is normal, not an anomaly. On an instance that has cookies,
        /// it means the session has expired.
        /// </description></item>
        /// <item><description>
        /// A ListingsPerPass warning when the value is zero or negative. At zero nothing is ever
        /// listed, so the library stays empty, silently. The default is 10.
        /// </description></item>
        /// </list>
        /// </remarks>
        internal static IReadOnlyList<string> BuildWarnings(
            PluginConfiguration? config,
            IReadOnlyDictionary<CallOutcome, int> outcomeCounts)
        {
            var warnings = new List<string>();

            if (config is not null)
            {
                // Both conditions must hold: a NeedsAuth video on an instance without cookies is
                // normal (age-restricted content). On an instance with cookies it means the session
                // has expired and must be renewed.
                var hasNeedsAuth = outcomeCounts.TryGetValue(CallOutcome.NeedsAuth, out var count) && count > 0;
                var hasCookies = !string.IsNullOrEmpty(config.CookiesPath);
                if (hasNeedsAuth && hasCookies)
                {
                    warnings.Add(
                        "One or more videos require authentication. " +
                        "The configured cookies file may have expired — re-export it from your browser.");
                }

                if (config.ListingsPerPass <= 0)
                {
                    warnings.Add(
                        "ListingsPerPass is set to 0 or a negative value. " +
                        "No source will ever be listed or refreshed until this is corrected. " +
                        "The default value is 10.");
                }
            }

            return warnings;
        }

        /// <summary>
        /// Whether the quality selection is complete. height and codec identify one variant
        /// together; one without the other is a malformed request, not half a selection to guess at.
        /// </summary>
        /// <param name="height">The requested height, or null for the default tier.</param>
        /// <param name="codec">The requested codec, or null for the default tier.</param>
        internal static bool IsWellFormedSelection(int? height, string? codec)
            => height.HasValue == !string.IsNullOrWhiteSpace(codec);

        /// <summary>Whether a stream request for this video can be served.</summary>
        /// <param name="video">The stored video record.</param>
        /// <remarks>
        /// A scheduled stream has no formats at all: refusing here keeps the failure readable
        /// instead of letting it propagate deep into yt-dlp.
        /// </remarks>
        internal static bool IsStreamable(VideoRecord video)
            => video.LiveStatus != LiveStatus.Upcoming;

        /// <summary>
        /// Whether the master fetched by this request is the one <see cref="StreamDescriber"/>
        /// would probe for the same video.
        /// </summary>
        /// <param name="format">The yt-dlp format selector used by this request.</param>
        /// <returns><see langword="true"/> only when <paramref name="format"/> is the default.</returns>
        /// <remarks>
        /// <see cref="StreamDescriber"/> primes its cache entries under
        /// <see cref="StreamCacheKey.DefaultFormat"/> and the plugin-configured languages — exactly
        /// the keys normal playback reads. A request that passes a custom format resolves a
        /// different master; handing that master to the describer would prime those default-format
        /// entries with the wrong content and then probe it into a permanent, incorrect description
        /// (Jellyfin never re-probes a source that already has a video stream). The background
        /// description is therefore only safe when the request used the default format.
        /// </remarks>
        internal static bool DescribesTheServedMaster(string? format)
            => string.Equals(format, StreamCacheKey.DefaultFormat, StringComparison.Ordinal);

        /// <summary>A fetched master and the playlist filtered out of it.</summary>
        /// <param name="Filtered">What this request serves.</param>
        /// <param name="Upstream">The complete upstream master, which the describer reuses for the
        /// sibling tiers — downloading it once is what keeps the group's description free of
        /// further network calls.</param>
        /// <param name="BaseUri">The URI the master was fetched from.</param>
        private sealed record FetchedMaster(string Filtered, string Upstream, Uri BaseUri);

        private async Task<FetchedMaster?> FilterUpstreamAsync(
            string masterUrl, int? height, string? codec, string audioLangs, string subLangs, CancellationToken cancellationToken)
        {
            var baseUri = new Uri(masterUrl);
            var upstream = await FetchStringAsync(masterUrl, cancellationToken).ConfigureAwait(false);
            if (upstream is null)
            {
                return null;
            }

            var filtered = HlsPlaylistFilter.FilterMaster(
                upstream,
                baseUri,
                height,
                codec,
                audioLangs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                subLangs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _subtitles.Rewrite);

            return filtered is null ? null : new FetchedMaster(filtered, upstream, baseUri);
        }

        /// <summary>
        /// Describes this video's group without delaying the response.
        /// </summary>
        /// <param name="config">The plugin configuration, or null when the plugin is unloaded.</param>
        /// <param name="id">The video UUID.</param>
        /// <param name="master">The fetched master whose upstream content the describer reuses.</param>
        /// <param name="format">The yt-dlp format selector used by this request.</param>
        /// <remarks>
        /// Deliberately not awaited, and deliberately not given the request's cancellation token:
        /// the request ends as soon as the playlist is written, which would cancel the work. The
        /// describer never throws, and its own guards keep two callers from probing the same member
        /// twice.
        /// </remarks>
        private void DescribeInBackground(PluginConfiguration? config, string id, FetchedMaster master, string format)
        {
            if (config is null)
            {
                return;
            }

            // Only launch when the request used the default format: StreamDescriber primes its
            // cache entries under DefaultFormat and the plugin-configured languages, which are
            // exactly the keys normal playback reads. A custom-format master would prime those same
            // keys with different content and then probe it into a permanent wrong description.
            if (!DescribesTheServedMaster(format))
            {
                return;
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        // The outcome is intentionally discarded: the background path is a best-effort
                        // failsafe, and the task loop and its back-off registry own the retry logic.
                        _ = await _describer
                            .DescribeGroupAsync(
                                config, id, master.Upstream, master.BaseUri, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // a background description must never take the server down
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogBackgroundDescribeFailed(ex, id);
                    }
                },
                CancellationToken.None);
        }

        private async Task<string?> FetchStringAsync(string url, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(new Uri(url), cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }

        [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
            Message = "{VideoUrl} resolved with {Format} to a bare media URL (no HLS manifest): expect video-only")]
        private partial void LogNoManifest(string videoUrl, string format);

        [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
            Message = "Could not persist the media streams of {VideoId}; playback is unaffected")]
        private partial void LogStreamPersistFailed(Exception exception, string videoId);

        [LoggerMessage(EventId = 12, Level = LogLevel.Warning,
            Message = "Describing the version group of {VideoId} in the background failed; playback is unaffected")]
        private partial void LogBackgroundDescribeFailed(Exception exception, string videoId);
    }
}

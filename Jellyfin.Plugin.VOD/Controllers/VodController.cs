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
        private readonly MediaStreamWriter _streamWriter;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly LibraryPublisher _publisher;
        private readonly ILogger<VodController> _logger;

        /// <summary>Initializes a new instance of the <see cref="VodController"/> class.</summary>
        /// <param name="db">The VOD database used to look up videos by UUID.</param>
        /// <param name="ytDlp">The yt-dlp service used to resolve stream URLs.</param>
        /// <param name="cache">The playlist cache.</param>
        /// <param name="streamWriter">Stores media streams so Jellyfin does not have to re-probe.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="libraryManager">Used to list the libraries the plugin may publish into.</param>
        /// <param name="publisher">Used to check that the configured library still resolves.</param>
        /// <param name="logger">The logger.</param>
        public VodController(
            IVodDatabase db,
            YtDlpService ytDlp,
            PlaylistCache cache,
            MediaStreamWriter streamWriter,
            IHttpClientFactory httpClientFactory,
            ILibraryManager libraryManager,
            LibraryPublisher publisher,
            ILogger<VodController> logger)
        {
            _db = db;
            _ytDlp = ytDlp;
            _cache = cache;
            _streamWriter = streamWriter;
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
            [FromQuery] string format = "bv*[protocol^=m3u8]",
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
            var effectiveAudioLangs = audioLangs ?? JoinLanguages(config?.PreferredAudioLanguages);
            var effectiveSubLangs = subLangs ?? JoinLanguages(config?.PreferredSubtitleLanguages);

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

            var cacheKey = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{id}|{format}|{height}|{codec}|{effectiveAudioLangs}|{effectiveSubLangs}");

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

            var filtered = await FilterUpstreamAsync(
                    resolution.Url, height, codec, effectiveAudioLangs, effectiveSubLangs,
                    cancellationToken)
                .ConfigureAwait(false);

            if (filtered is null)
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

            // Cache miss only: this bounds stream writes to roughly one per playlist TTL.
            // Storing the tracks is a fidelity gain, not a prerequisite for playback, so a failure
            // here must not cost the caller its playlist nor keep it out of the cache.
            try
            {
                var streams = MediaStreamBuilder.Build(filtered);
                _streamWriter.TryWrite(id, height, codec, streams, cancellationToken);
            }
#pragma warning disable CA1031 // nothing about persisting track labels is worth failing playback for
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStreamPersistFailed(ex, id);
            }

            _cache.Set(cacheKey, filtered);
            return Content(filtered, "application/vnd.apple.mpegurl");
        }

        /// <summary>Returns a subtitle playlist whose segment ffmpeg will accept.</summary>
        [HttpGet("subtitles/{token}.m3u8")]
        public async Task<ActionResult> GetSubtitlePlaylist(
            [FromRoute] string token,
            CancellationToken cancellationToken = default)
        {
            if (!_cache.TryGet(SubtitleKey(token), out var upstreamUrl))
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
            if (!_cache.TryGet(SubtitleKey(token), out var upstreamUrl))
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
        /// Reports what yt-dlp can tell us about a candidate source URL.
        /// </summary>
        /// <param name="url">The channel, playlist or video URL to inspect.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The source description, or an error status.</returns>
        /// <remarks>
        /// Requires elevation: the route issues an outbound request to a URL the caller supplies,
        /// which is an SSRF primitive if left anonymous.
        /// </remarks>
        [HttpGet("source-info")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public async Task<ActionResult> GetSourceInfo(
            [FromQuery] string? url,
            CancellationToken cancellationToken)
        {
            if (!IsProbeableUrl(url))
            {
                return BadRequest(new { error = "url must be an absolute HTTP(S) URL" });
            }

            var info = await _ytDlp.GetSourceInfoAsync(url!, cancellationToken).ConfigureAwait(false);

            return info is null
                ? StatusCode(503, new { error = "yt-dlp could not read this URL", url })
                : Ok(info);
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
        /// Returns whether a caller-supplied URL is safe to hand to yt-dlp.
        /// </summary>
        /// <param name="url">The URL the caller asked to probe.</param>
        /// <returns>True when the URL is absolute and uses HTTP or HTTPS.</returns>
        /// <remarks>
        /// Anything else — a relative path, a <c>file://</c> URL, a <c>javascript:</c> URL — would
        /// either fail noisily inside yt-dlp or reach something local that the caller should not be
        /// able to name.
        /// </remarks>
        internal static bool IsProbeableUrl(string? url)
            => !string.IsNullOrWhiteSpace(url)
               && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
               && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

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

            return Ok(new SourcesStatus(libraries, ProjectSourceStatus(_db.GetAllSources())));
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
                .Select(r => new SourceStatus(r.Url, r.Name, r.LastSyncedAt, r.LastError, r.VideoCount))
                .ToList();

        /// <summary>
        /// Joins configured language prefixes into the comma-separated form the filter expects.
        /// </summary>
        /// <param name="languages">The configured prefixes, possibly null or containing blanks.</param>
        /// <returns>A comma-separated list with no empty entries.</returns>
        /// <remarks>
        /// Blank entries are dropped rather than joined: the administrator's field is free text, and
        /// a trailing comma would otherwise reach HlsPlaylistFilter as an empty prefix, which
        /// matches every rendition.
        /// </remarks>
        internal static string JoinLanguages(string[]? languages)
            => languages is null
                ? string.Empty
                : string.Join(',', languages.Where(l => !string.IsNullOrWhiteSpace(l)));

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

        private async Task<string?> FilterUpstreamAsync(
            string masterUrl, int? height, string? codec, string audioLangs, string subLangs, CancellationToken cancellationToken)
        {
            var baseUri = new Uri(masterUrl);
            var upstream = await FetchStringAsync(masterUrl, cancellationToken).ConfigureAwait(false);
            if (upstream is null)
            {
                return null;
            }

            return HlsPlaylistFilter.FilterMaster(
                upstream,
                baseUri,
                height,
                codec,
                audioLangs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                subLangs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ServeSubtitle);
        }

        private string? ServeSubtitle(string upstreamUri)
        {
            if (!upstreamUri.Contains("timedtext", StringComparison.OrdinalIgnoreCase))
            {
                return upstreamUri;
            }

            var token = Guid.NewGuid().ToString("N");
            _cache.Set(SubtitleKey(token), upstreamUri, TimeSpan.FromHours(1));

            // Relative to /VOD/stream/{id}: never bake in a host — the cached master may be served
            // to a different caller (container ffmpeg vs. Jellyfin's own probe).
            return $"../subtitles/{token}.m3u8";
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

        private static string SubtitleKey(string token) => $"sub:{token}";

        [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
            Message = "{VideoUrl} resolved with {Format} to a bare media URL (no HLS manifest): expect video-only")]
        private partial void LogNoManifest(string videoUrl, string format);

        [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
            Message = "Could not persist the media streams of {VideoId}; playback is unaffected")]
        private partial void LogStreamPersistFailed(Exception exception, string videoId);
    }
}

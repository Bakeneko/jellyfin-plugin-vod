using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Common.Api;
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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<VodController> _logger;

        /// <summary>Initializes a new instance of the <see cref="VodController"/> class.</summary>
        public VodController(
            IVodDatabase db,
            YtDlpService ytDlp,
            PlaylistCache cache,
            IHttpClientFactory httpClientFactory,
            ILogger<VodController> logger)
        {
            _db = db;
            _ytDlp = ytDlp;
            _cache = cache;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Resolves a video by its opaque UUID and returns a filtered HLS master playlist.
        /// </summary>
        /// <param name="id">Opaque UUID from the <c>videos</c> table.</param>
        /// <param name="format">yt-dlp format selector. Default picks any HLS video stream.</param>
        /// <param name="maxHeight">Tallest variant to serve. Default 1080 avoids 2160p VP9.</param>
        /// <param name="audioLangs">Comma-separated language prefixes to keep beside the original audio (empty = original only).</param>
        /// <param name="subLangs">Comma-separated subtitle language prefixes to include (empty = no subtitles).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// The route ends in <c>.m3u8</c> so that <c>MediaSourceManager.SupportsDirectStream</c> returns
        /// false — Jellyfin must not hand this URL to a remote client (CDN segments are signed for the
        /// server's IP; loopback resolves only inside the container).
        /// </remarks>
        [HttpGet("stream/{id}.m3u8")]
        public async Task<ActionResult> GetStream(
            [FromRoute] string id,
            [FromQuery] string format = "bv*[protocol^=m3u8]",
            [FromQuery] int maxHeight = 1080,
            [FromQuery] string audioLangs = "",
            [FromQuery] string subLangs = "",
            CancellationToken cancellationToken = default)
        {
            var video = _db.GetVideoByUuid(id);
            if (video is null)
            {
                return NotFound(new { error = "Unknown video id", id });
            }

            var cacheKey = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{id}|{format}|{maxHeight}|{audioLangs}|{subLangs}");

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
                    resolution.Url, maxHeight, audioLangs, subLangs, cancellationToken)
                .ConfigureAwait(false);

            if (filtered is null)
            {
                return Redirect(resolution.Url);
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
        /// Registers a video URL in the database and returns its opaque UUID.
        /// Debug endpoint — lets you test the stream before SyncService exists. Remove at Plan 4.
        /// </summary>
        [HttpPost("admin/register")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult<string> RegisterVideo([FromBody] string videoUrl)
        {
            if (string.IsNullOrWhiteSpace(videoUrl)
                || !Uri.TryCreate(videoUrl, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return BadRequest(new { error = "videoUrl must be an absolute HTTP(S) URL" });
            }

            var id = Guid.NewGuid().ToString();
            _db.UpsertVideo(new VideoRecord
            {
                Id = id,
                SourceUrl = "__debug__",
                VideoUrl = videoUrl,
                SyncedAt = DateTimeOffset.UtcNow
            });

            return Ok(id);
        }

        private async Task<string?> FilterUpstreamAsync(
            string masterUrl, int maxHeight, string audioLangs, string subLangs, CancellationToken cancellationToken)
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
                maxHeight,
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
    }
}

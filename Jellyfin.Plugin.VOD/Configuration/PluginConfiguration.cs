using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.VOD.Configuration
{
    /// <summary>Global plugin settings, persisted as XML by Jellyfin.</summary>
    public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration
    {
        // ── External tools ──────────────────────────────────────────────────────

        /// <summary>Gets or sets the absolute path or PATH name of the yt-dlp executable.</summary>
        public string YtDlpPath { get; set; } = "yt-dlp";

        /// <summary>
        /// Gets or sets the JS runtime hint passed to yt-dlp's <c>--js-runtimes</c>.
        /// Format: <c>NAME</c> or <c>NAME:PATH</c> (e.g. <c>deno:/config/deno</c>).
        /// Empty means use yt-dlp's default detection.
        /// </summary>
        public string JsRuntime { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path to the Netscape-format cookie seed file.
        /// This file is copied into the plugin data folder; yt-dlp never touches the original.
        /// </summary>
        public string CookiesPath { get; set; } = string.Empty;

        /// <summary>Gets or sets the absolute path or PATH name of the ffmpeg executable.</summary>
        public string FfmpegPath { get; set; } = "ffmpeg";

        // ── Library ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets the root directory where .strm files and artwork are written.
        /// Must be a path that Jellyfin's library scanner watches.
        /// </summary>
        public string LibraryBasePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base URL used inside .strm files.
        /// Should be reachable by the Jellyfin server itself (loopback is fine).
        /// Example: <c>http://localhost:8096</c>.
        /// </summary>
        public string LocalBaseUrl { get; set; } = "http://localhost:8096";

        // ── Playback ─────────────────────────────────────────────────────────────

        /// <summary>Gets or sets the HLS playlist cache TTL in minutes.</summary>
        public int CacheMinutes { get; set; } = 5;

        /// <summary>Gets or sets the maximum video height (pixels) passed to HlsPlaylistFilter.</summary>
        public int MaxHeight { get; set; } = 1080;

        /// <summary>
        /// Gets or sets the preferred audio language codes (BCP-47 prefixes, e.g. "en", "fr").
        /// Used by HlsPlaylistFilter to keep matching audio renditions.
        /// Empty array means keep the original track only.
        /// </summary>
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "string[] is required for Jellyfin XML serialization of BasePluginConfiguration.")]
        public string[] PreferredAudioLanguages { get; set; } = [];

        /// <summary>
        /// Gets or sets the preferred subtitle language codes (BCP-47 prefixes, e.g. "en", "fr").
        /// Used by HlsPlaylistFilter to keep matching subtitle renditions.
        /// Empty array means no subtitles.
        /// </summary>
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "string[] is required for Jellyfin XML serialization of BasePluginConfiguration.")]
        public string[] PreferredSubtitleLanguages { get; set; } = [];

        // ── Sources ───────────────────────────────────────────────────────────────

        /// <summary>Gets or sets the list of configured sync sources.</summary>
        [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Setter required for XML deserialization by Jellyfin.")]
        [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "List<T> required for Jellyfin XML serialization of plugin configuration collections.")]
        public List<SourceDefinition> Sources { get; set; } = [];
    }
}

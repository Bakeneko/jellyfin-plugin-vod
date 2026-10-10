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
        /// Gets or sets the name of the Jellyfin library sources in <see cref="SourceMode.Movies"/>
        /// publish into. Empty means no library is configured for that role.
        /// </summary>
        /// <remarks>
        /// May name the same library as <see cref="SeriesLibraryName"/>. That only means one target
        /// for both kinds; whether that library is MIXED is decided by its collection type, never by
        /// the two names being equal.
        /// </remarks>
        public string MoviesLibraryName { get; set; } = "VOD";

        /// <summary>
        /// Gets or sets the name of the Jellyfin library sources in <see cref="SourceMode.Series"/>
        /// publish into. Empty means no library is configured for that role.
        /// </summary>
        public string SeriesLibraryName { get; set; } = "VOD";

        /// <summary>
        /// Gets or sets a value indicating whether the published date added is the video's release
        /// date instead of the date the plugin first saw it.
        /// </summary>
        /// <remarks>
        /// Only takes effect on a MIXED target — one whose collection type is null or "mixed" —
        /// because the problem it works around, Jellyfin defaulting such a library to the "Folders"
        /// view, exists nowhere else. Turning it on makes "date added" sort chronologically by
        /// publication; the cost is that a channel added today but active for years will never
        /// surface in "recently added", because its videos carry their old release dates. Reversible
        /// either way: nothing is overwritten, the published date is recomputed. Re-typing a mixed
        /// library therefore recomputes the dates back on the next pass.
        /// </remarks>
        public bool UseReleaseDateAsDateAdded { get; set; }

        /// <summary>
        /// Gets or sets the base URL used to build stream URLs served to Jellyfin.
        /// Should be reachable by the Jellyfin server itself (loopback is fine).
        /// Example: <c>http://localhost:8096</c>.
        /// </summary>
        public string LocalBaseUrl { get; set; } = "http://localhost:8096";

        // ── Playback ─────────────────────────────────────────────────────────────

        /// <summary>Gets or sets the HLS playlist cache TTL in minutes.</summary>
        public int CacheMinutes { get; set; } = 5;

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

        /// <summary>
        /// Gets or sets how many videos the stream description task describes per pass.
        /// Zero or less disables the task; the description at playback stays active either way.
        /// </summary>
        /// <remarks>
        /// Each video costs one yt-dlp resolution and at most one heavy probe (up to ~25 MB read).
        /// Ten videos every fifteen minutes is about 960 a day, which clears a large library in a
        /// day or two without the burst that earns an anti-bot answer.
        /// </remarks>
        public int StreamDescriptionBatchSize { get; set; } = 10;

        /// <summary>
        /// Gets or sets how many hours a video whose description failed is left alone before
        /// another pass tries it again.
        /// </summary>
        public int StreamDescriptionRetryHours { get; set; } = 24;

        // ── Sync ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets how often, in minutes, sources are polled for a broadcast in progress.
        /// Zero or less disables the probe.
        /// </summary>
        /// <remarks>
        /// The sync alone cannot find a live in time: it runs per source at
        /// <see cref="SourceDefinition.SyncIntervalMinutes"/>, 720 by default.
        /// </remarks>
        public int LiveProbeIntervalMinutes { get; set; } = 5;

        // ── Sources ───────────────────────────────────────────────────────────────

        /// <summary>Gets or sets the list of configured sync sources.</summary>
        [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Setter required for XML deserialization by Jellyfin.")]
        [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "List<T> required for Jellyfin XML serialization of plugin configuration collections.")]
        public List<SourceDefinition> Sources { get; set; } = [];
    }
}

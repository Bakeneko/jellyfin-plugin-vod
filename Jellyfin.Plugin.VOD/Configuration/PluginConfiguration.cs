using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Serialization;

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
        /// Gets or sets how many yt-dlp calls a minute background work may make to <b>one</b>
        /// platform. Sync, stream descriptions and the live probe share this budget, per platform.
        /// </summary>
        /// <remarks>
        /// The limit is imposed on the session, not on the caller, so every background path has to
        /// share one allowance. 120 a minute earned a rate-limit window in production (measured
        /// 2026-10-12); 20 keeps a factor of six of margin and still allows ~28 800 calls a day
        /// per platform. Resolving a video a user just started is deliberately exempt: making a
        /// started video wait behind a backlog would break playback.
        /// </remarks>
        public int YtDlpCallsPerMinute { get; set; } = 20;

        /// <summary>
        /// Gets or sets how many videos one wave fetches before its source goes to a checkup.
        /// </summary>
        /// <remarks>
        /// The scheduling grain, not a brake: the throttle holds the rate. At the default rate of
        /// 20 calls a minute per platform, a wave is exactly one minute of that platform's budget —
        /// so a video added to any source is visible within about a minute, whatever the backlog,
        /// and a source is republished once a minute while it is being served.
        /// <para>
        /// It is also what bounds the first fill. 7449 videos by waves of 20 is about 370 turns of
        /// the loop, each one ending in a checkup that reports progress — which is what makes a
        /// pass of several hours distinguishable from a pass that is stuck.
        /// </para>
        /// </remarks>
        public int WaveSize { get; set; } = 20;

        /// <summary>
        /// Gets or sets the base backoff, in minutes, before a video whose fetch failed is tried
        /// again.
        /// </summary>
        /// <remarks>
        /// Doubled on each consecutive failure and capped at seven days: 15 min, 30 min, 1 h, 2 h…
        /// which reaches the cap on the eleventh attempt, so a permanently broken video costs
        /// about ten calls over eleven days. A failure the platform caused — a rate limit, a
        /// timeout — does not count towards this, or one bad window would push every video of a
        /// source a step further out.
        /// </remarks>
        public int RetryMinutes { get; set; } = 15;

        /// <summary>
        /// Gets or sets <see cref="WaveSize"/> under the element name a file written before the
        /// orchestration rework stored it as.
        /// </summary>
        /// <remarks>
        /// Jellyfin reads a plugin's settings with a plain <c>XmlSerializer</c>, which discards an
        /// element it does not recognise without raising anything: a rename therefore loses
        /// whatever the administrator had set, and the next save on the configuration page makes
        /// the loss permanent. <c>ShouldSerialize…</c> returns false, so a save writes only the
        /// current name and the legacy element disappears from the file on its own.
        /// </remarks>
        [XmlElement("ResolutionWaveSize")]
        public int LegacyResolutionWaveSize
        {
            get => WaveSize;
            set => WaveSize = value;
        }

        /// <summary>
        /// Gets or sets <see cref="RetryMinutes"/> under its pre-rework element name. See
        /// <see cref="LegacyResolutionWaveSize"/>.
        /// </summary>
        [XmlElement("ResolutionRetryMinutes")]
        public int LegacyResolutionRetryMinutes
        {
            get => RetryMinutes;
            set => RetryMinutes = value;
        }

        /// <summary>Keeps the legacy element out of a saved file.</summary>
        /// <returns>Always false.</returns>
        public bool ShouldSerializeLegacyResolutionWaveSize() => false;

        /// <summary>Keeps the legacy element out of a saved file.</summary>
        /// <returns>Always false.</returns>
        public bool ShouldSerializeLegacyResolutionRetryMinutes() => false;

        /// <summary>
        /// Gets or sets how many days may pass between two FULL listings of a source.
        /// </summary>
        /// <remarks>
        /// A short listing sees the head of the listing, so it finds what is new and what has been
        /// retitled near the top. Only a full one catches a DELETION, or a retitle in depth: a
        /// video that left the platform is simply absent, and absence is only visible to a listing
        /// that asked for everything.
        /// <para>
        /// It is therefore the latency of a removal. Measured on the most active source configured
        /// (Arte, about 3 videos a day, capped at 100), the database will hold at most ~21 videos
        /// beyond the cap between two full listings; none of them is published, and the next full
        /// listing retires them.
        /// </para>
        /// </remarks>
        public int FullListingIntervalDays { get; set; } = 7;

        /// <summary>
        /// Gets or sets how many sources one turn of the pass may list.
        /// </summary>
        /// <remarks>
        /// Spreads the listing work instead of spending every source's budget in one turn. The
        /// sources most overdue go first, so a low value delays nothing indefinitely — it only
        /// means a turn is shorter and the loop comes round again.
        /// </remarks>
        public int ListingsPerPass { get; set; } = 10;

        /// <summary>
        /// Gets or sets how many days a video's metadata may go unrefreshed before it is fetched
        /// again.
        /// </summary>
        /// <remarks>
        /// The single freshness clock. It replaces the separate seven-day re-check of videos a
        /// platform had called gone: "unavailable" is a RESULT now, not a state, so it ages like
        /// any other answer and needs no rule of its own.
        /// <para>
        /// Measured: everything on a 30-day clock costs about 248 calls a day on the configured
        /// sources — fewer than the 266 the unavailable re-check alone used to cost.
        /// </para>
        /// </remarks>
        public int MetadataMaxAgeDays { get; set; } = 30;

        /// <summary>
        /// Gets or sets the language to pin on the listing path, as a BCP-47 prefix ("fr", "de").
        /// Empty passes nothing.
        /// </summary>
        /// <remarks>
        /// Measured twice, and contre-intuitive: a flat listing serves the LOCALIZED title and
        /// obeys <c>--extractor-args "youtube:lang=XX"</c>, while a single-video metadata call
        /// always serves the ORIGINAL and ignores it. The full 1.2 MB JSON holds one language and
        /// no variants.
        /// <para>
        /// The point of pinning is to make the listing's answer DETERMINISTIC, so the retitle
        /// detector compares like with like. It is not to align the two paths, which is impossible:
        /// "the original" differs per channel.
        /// </para>
        /// <para>
        /// Empty is a valid choice, not a missing value: nothing is passed to yt-dlp, and the
        /// published title stays whatever the platform calls original. No default is inferred from
        /// the server's locale — that would silently change every published title on an upgrade.
        /// </para>
        /// </remarks>
        public string PreferredMetadataLanguage { get; set; } = string.Empty;

        // ── Live probe ────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets how often, in minutes, sources are polled for a broadcast in progress.
        /// Zero or less disables the probe.
        /// </summary>
        /// <remarks>
        /// The listing alone cannot find a live in time: it runs per source at
        /// <see cref="SourceDefinition.ListingIntervalMinutes"/>, 720 by default.
        /// </remarks>
        public int LiveProbeIntervalMinutes { get; set; } = 5;

        // ── Sources ───────────────────────────────────────────────────────────────

        /// <summary>Gets or sets the list of configured sync sources.</summary>
        [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Setter required for XML deserialization by Jellyfin.")]
        [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "List<T> required for Jellyfin XML serialization of plugin configuration collections.")]
        public List<SourceDefinition> Sources { get; set; } = [];
    }
}

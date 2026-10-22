using System.Xml.Serialization;

namespace Jellyfin.Plugin.VOD.Configuration
{
    /// <summary>A user-configured sync source, persisted as XML inside PluginConfiguration.</summary>
    public sealed class SourceDefinition
    {
        /// <summary>Gets or sets the source URL (YouTube channel, playlist, Arte collection, france.tv video…).</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a display-name override.
        /// When empty, the name resolved from the platform is used.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets a value indicating whether this source participates in scheduled syncs.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets how synced videos are organised in the Jellyfin library.</summary>
        public SourceMode Mode { get; set; } = SourceMode.Series;

        /// <summary>
        /// Gets or sets how many days to retain videos after they disappear from the source.
        /// 0 means keep forever (no date-based pruning).
        /// </summary>
        public int RetentionDays { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of videos to sync per run.
        /// Maps to yt-dlp's <c>--playlist-end</c>. 0 means no limit.
        /// </summary>
        public int MaxVideosPerSource { get; set; }

        /// <summary>
        /// Gets or sets the minimum number of minutes between two SHORT listings of this source.
        /// Defaults to 12 hours, which keeps yt-dlp well clear of platform rate limits. Zero
        /// lists on every pass — only safe for a source you control, since it enumerates as often
        /// as the task fires.
        /// </summary>
        /// <remarks>
        /// Same meaning as the setting it replaces, under a name that still describes the work:
        /// there is no "sync" any more, only a listing. A short listing asks for 30 entries, which
        /// is exactly one YouTube page and therefore one request, so a low value here is far
        /// cheaper than it was.
        /// <para>
        /// The FULL listing has its own, global interval
        /// (<see cref="PluginConfiguration.FullListingIntervalDays"/>): this one cannot catch a
        /// deletion or a retitle below the head of the listing.
        /// </para>
        /// </remarks>
        public int ListingIntervalMinutes { get; set; } = 720;

        /// <summary>
        /// Gets or sets <see cref="ListingIntervalMinutes"/> under the element name a file written
        /// before the orchestration rework stored it as.
        /// </summary>
        /// <remarks>
        /// Jellyfin reads a plugin's settings with a plain <c>XmlSerializer</c>, which discards an
        /// element it does not recognise without raising anything. Without this, a two-day interval
        /// an administrator set after a rate limit silently becomes the 720-minute default and the
        /// source is listed four times more often than configured. <c>ShouldSerialize…</c> returns
        /// false, so a save writes only the current name.
        /// </remarks>
        [XmlElement("SyncIntervalMinutes")]
        public int LegacySyncIntervalMinutes
        {
            get => ListingIntervalMinutes;
            set => ListingIntervalMinutes = value;
        }

        /// <summary>Keeps the legacy element out of a saved file.</summary>
        /// <returns>Always false.</returns>
        public bool ShouldSerializeLegacySyncIntervalMinutes() => false;

        /// <summary>Gets or sets a free-form description shown in the UI.</summary>
        public string Description { get; set; } = string.Empty;
    }
}

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
        /// Gets or sets the minimum number of minutes between two syncs for this source.
        /// Defaults to 12 hours, which keeps yt-dlp well clear of platform rate limits.
        /// 0 disables the throttle and syncs on every SyncTask run — only safe for sources
        /// you control, since it enumerates the playlist as often as the task fires.
        /// </summary>
        public int SyncIntervalMinutes { get; set; } = 720;

        /// <summary>Gets or sets a free-form description shown in the UI.</summary>
        public string Description { get; set; } = string.Empty;
    }
}

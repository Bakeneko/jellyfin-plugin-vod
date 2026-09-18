namespace Jellyfin.Plugin.VOD.Configuration
{
    /// <summary>
    /// Global plugin settings, persisted as XML by Jellyfin.
    /// </summary>
    public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration
    {
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
    }
}

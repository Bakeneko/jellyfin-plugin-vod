namespace Jellyfin.Plugin.VOD.Configuration
{
    /// <summary>Controls how synced videos are organised in the Jellyfin library.</summary>
    public enum SourceMode
    {
        /// <summary>Videos are grouped as episodes under a series / season folder tree.</summary>
        Series,

        /// <summary>Each video is treated as a standalone movie in its own folder.</summary>
        Movies
    }
}

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>The outcome of resolving a page URL with yt-dlp.</summary>
    /// <param name="Url">The URL to hand to the player.</param>
    /// <param name="IsManifest">
    /// <see langword="true"/> when <paramref name="Url"/> is an HLS master playlist.
    /// <see langword="false"/> when it is a bare media URL (video without audio on YouTube/Arte).
    /// </param>
    public sealed record StreamResolution(string Url, bool IsManifest);
}

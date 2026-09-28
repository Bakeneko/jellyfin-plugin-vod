namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>
    /// What <c>GET /VOD/source-info</c> reports about a candidate source URL, so the configuration
    /// page can fill a new row in and show that yt-dlp understood the URL before it is saved.
    /// </summary>
    /// <param name="Name">
    /// The name the sync would publish the series under, resolved by the rule the sync itself uses.
    /// </param>
    /// <param name="Description">The channel or playlist description, when the platform exposes one.</param>
    /// <param name="ThumbnailUrl">Artwork for the source, when the platform exposes one.</param>
    /// <param name="DetectedType">The <see cref="SourceType"/> the URL was recognised as.</param>
    /// <param name="HasEntries">
    /// Whether the probe enumerated at least one video. False means yt-dlp read the URL but found
    /// nothing to sync — worth showing before the row is saved.
    /// </param>
    public sealed record SourceInfo(
        string Name,
        string? Description,
        string? ThumbnailUrl,
        string DetectedType,
        bool HasEntries);
}

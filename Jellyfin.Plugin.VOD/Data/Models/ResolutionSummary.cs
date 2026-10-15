namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>Where one source's videos are in the resolution business, for the configuration page.</summary>
    /// <param name="Resolved">Videos resolved, published or about to be.</param>
    /// <param name="Pending">Videos enumerated and not yet resolved.</param>
    /// <param name="Failed">Videos whose last resolution failed, retried on their backoff.</param>
    /// <param name="Unavailable">Videos the platform says are gone; never published.</param>
    /// <param name="Settled">Whether the source may number and publish its new videos now.</param>
    /// <param name="LastError">The error of the most recently attempted failed video, or null.</param>
    public sealed record ResolutionSummary(
        int Resolved, int Pending, int Failed, int Unavailable, bool Settled, string? LastError);
}

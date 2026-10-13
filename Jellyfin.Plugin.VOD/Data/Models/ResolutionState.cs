namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>Where a video is in the business of having its metadata and streams fetched.</summary>
    /// <remarks>
    /// Stored as text, like <c>live_status</c>, because it is read back as a name and because it is
    /// a predicate: the publication path and the resolver both select on it. Only
    /// <see cref="Resolved"/> may be published — a video with no metadata has no date, no season
    /// and no episode number, so publishing it would file it under Specials and renumber it later,
    /// which loses its watch state.
    /// </remarks>
    public enum ResolutionState
    {
        /// <summary>Discovered by an enumeration, never resolved. Not publishable.</summary>
        Pending,

        /// <summary>Metadata is in the database. Publishable.</summary>
        Resolved,

        /// <summary>
        /// Definitively gone: the listing says so for free (see
        /// <c>PlatformRules.ListingTellsUnavailable</c>), or a platform named a cause on the
        /// whitelist. Not publishable, and re-checked rather than retried.
        /// </summary>
        Unavailable,

        /// <summary>
        /// A failure that is not final — an unrecognised message, a timeout, a platform refusing.
        /// Retried with a growing backoff. The unknown is transient on purpose: retrying a dead
        /// video costs one call, while wrongly retiring a live one is invisible and permanent.
        /// </summary>
        Failed,
    }
}

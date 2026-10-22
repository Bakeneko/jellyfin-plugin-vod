namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>
    /// The stages of one pass over a source, each recording its own result, detail and count of
    /// consecutive failures.
    /// </summary>
    /// <remarks>
    /// One triplet per stage rather than one error field per source: an intermittent failure is
    /// then visible as a count without any history table. The <c>/streams</c> tab that failed 170
    /// times in two days and then stopped is the case this exists for.
    /// </remarks>
    public enum SourceStage
    {
        /// <summary>The 30-entry listing that carries the scheduling clock.</summary>
        ShortListing = 0,

        /// <summary>The capped listing that catches deletions and retitles in depth.</summary>
        FullListing,

        /// <summary>Retiring what left the configuration or the source.</summary>
        Cleanup,

        /// <summary>One wave of metadata fetches and stream descriptions.</summary>
        Wave,

        /// <summary>Recomputing publishability and numbering, then reconciling the library.</summary>
        Checkup,
    }
}

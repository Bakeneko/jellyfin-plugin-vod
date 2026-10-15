namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>
    /// When a video still holds its source back from publication: the one definition the resolver,
    /// the live probe and the configuration page's status all share.
    /// </summary>
    /// <remarks>
    /// A source is settled when no row blocks. Only then does the numbering pass run, because
    /// only then does every video of every day have its publication date, so a day's ranks can
    /// follow publication time and a stored number never has to be recomputed.
    /// </remarks>
    public static class ResolutionSettlement
    {
        /// <summary>
        /// Consecutive failures after which a failed video stops holding its source back. It is
        /// still retried on its own backoff; with the default 15-minute base this bounds the hold
        /// to about 1 h 45 (15 + 30 + 60 minutes).
        /// </summary>
        public const int BlockingFailedAttempts = 3;

        /// <summary>Whether a row in this state keeps its source from being settled.</summary>
        /// <param name="state">The row's resolution state.</param>
        /// <param name="attempts">The row's consecutive failure count.</param>
        /// <returns>True for a pending row, and for a failed row below the threshold.</returns>
        public static bool Blocks(ResolutionState state, int attempts)
            => state == ResolutionState.Pending
               || (state == ResolutionState.Failed && attempts < BlockingFailedAttempts);
    }
}

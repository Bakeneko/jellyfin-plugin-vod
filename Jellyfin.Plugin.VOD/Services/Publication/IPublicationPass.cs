using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>One pass of the publication loop, as the scheduled task sees it.</summary>
    /// <remarks>
    /// The seam exists for the task's own tests. A pass assembled for real needs a database, a
    /// yt-dlp service, a library manager and four steps, so a test of "the task runs the pass,
    /// hands it the progress reporter it was given, and does not swallow a cancellation" would
    /// otherwise have to rebuild the whole fixture of
    /// <c>PublicationServiceTests</c> to observe three facts about six lines of code.
    /// </remarks>
    public interface IPublicationPass
    {
        /// <summary>Runs one pass: turns of the loop until no source has work left.</summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="progress">Where to report liveness, or null.</param>
        /// <param name="now">The reference time for the first turn.</param>
        /// <param name="cancellationToken">A token to cancel the pass between two sources.</param>
        /// <returns>What the pass did.</returns>
        Task<PassReport> RunPassAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            DateTimeOffset now,
            CancellationToken cancellationToken);
    }
}

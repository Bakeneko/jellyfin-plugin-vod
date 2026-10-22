using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// The describing call the publication pass makes, as a seam its tests can replace.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the whole of what the pass uses. <c>ApplyDescribed</c>, which writes a
    /// description onto the library items once they exist, stays on the concrete class: the checkup
    /// takes <see cref="StreamDescriber"/> for it, as the live probe does. Adding it here would be
    /// tidier, but it buys nothing the checkup's tests need — they already build a real describer
    /// over substituted dependencies — and it is not worth churning verified tests for symmetry.
    /// </remarks>
    /// <remarks>
    /// Deliberately narrow: <see cref="StreamDescriber"/> also serves the playback failsafe and the
    /// live probe, which keep using the concrete class.
    /// <para>
    /// This exists for the same reason as <see cref="IYtDlpService"/>: the concrete class is sealed
    /// with no virtual member, so describing could not be stubbed, and a test would have had to
    /// probe real streams through ffprobe.
    /// </para>
    /// </remarks>
    public interface IStreamDescriber
    {
        /// <summary>Describes every offered tier of one video, from a master already in hand.</summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="video">The row, already filled from its metadata answer.</param>
        /// <param name="upstreamMaster">The HLS master's content.</param>
        /// <param name="masterBaseUri">The master's own URI, to resolve relative playlists.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The streams per tier key, or null when nothing could be described. The manifest does not
        /// survive the pass, which is why describing happens here rather than at publication.
        /// </returns>
        Task<IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>?> DescribeVideoAsync(
            PluginConfiguration config,
            VideoRecord video,
            string upstreamMaster,
            Uri masterBaseUri,
            CancellationToken cancellationToken);
    }
}

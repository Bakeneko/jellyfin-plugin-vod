using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Data.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>What describing one video produced.</summary>
    /// <param name="Outcome">How it ended.</param>
    /// <param name="Detail">Why, in plain text, or null on success and on "nothing to do".</param>
    /// <param name="Streams">
    /// The streams per version-group member, or null when there was nothing to describe. The caller
    /// carries this to publication; it is never persisted, because the manifest it came from would
    /// be stale by the next pass.
    /// </param>
    /// <param name="Attempted">
    /// Whether a description was actually attempted. False means the video was never a candidate —
    /// a live stream, no manifest, no offered tier, outside retention — which is NOT a failure and
    /// must not advance a counter.
    /// </param>
    /// <param name="Conclusive">
    /// Whether this answer settles the question until the video's metadata is fetched again.
    /// </param>
    /// <remarks>
    /// <see cref="Conclusive"/> exists to stop a loop, and it is worth knowing which one. A video is
    /// selected for a wave partly BECAUSE it has never been described, so an answer of "there is
    /// nothing to describe" that writes nothing leaves the video due for ever: the metadata fetch
    /// succeeds every turn, which resets its failure counter, so no backoff ever starts and the
    /// pass cannot finish. Measured on the test instance, six percent of YouTube videos are in
    /// exactly that state — several hundred of them.
    /// <para>
    /// True for the two reasons only a fresh metadata answer could change: no manifest URL, and no
    /// offered tier. The caller stamps the describe clock for these, which takes the video out of
    /// the selection until its metadata goes stale — the thirty-day refresh is the retry.
    /// </para>
    /// <para>
    /// False for the two reasons that change on their own and that the selection query already
    /// excludes: a live or scheduled stream, and a video outside its retention window. Stamping
    /// those would be wrong, since a broadcast ends and a retention window can be widened.
    /// </para>
    /// </remarks>
    public sealed record StreamDescription(
        CallOutcome Outcome,
        string? Detail,
        IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>? Streams,
        bool Attempted,
        bool Conclusive = true);
}

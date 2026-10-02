using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>One tier offered to the viewer: the VideoQuality elected for a height.</summary>
    /// <param name="Height">The tier's height, as reported by the platform.</param>
    /// <param name="Codec">The codec elected by the preference order.</param>
    /// <param name="Fps">The elected quality's fps, or null when the platform reports none.</param>
    internal sealed record Tier(int Height, string Codec, int? Fps);

    /// <summary>
    /// Turns the stored quality ladder into the tiers the viewer may choose between.
    /// </summary>
    /// <remarks>
    /// Two tiers at most keeps the menu honest and short. The reference tier makes explicit what the
    /// primary already serves in auto, and the best available is offered only when it differs — so a
    /// source that caps at the reference tier shows one entry, not a duplicate.
    /// </remarks>
    internal static class QualityLadder
    {

        /// <summary>
        /// Codec preference, best first: h264 direct-plays most widely, hevc is commonly decoded
        /// in hardware, and anything else ties, broken by the higher fps.
        /// </summary>
        private static readonly string[] CodecPreference = ["h264", "hevc"];

        /// <summary>
        /// The ceiling the reference tier respects when a platform ships no AVC at all, mirroring
        /// <c>HlsPlaylistFilter.DefaultFallbackHeight</c>.
        /// </summary>
        private const int ReferenceFallbackHeight = 1080;

        /// <summary>
        /// The tier the primary item serves in <em>auto</em>, made explicit: the best AVC, and
        /// failing that the best variant at or below <see cref="ReferenceFallbackHeight"/>.
        /// </summary>
        /// <param name="qualities">The stored ladder, as captured at sync.</param>
        /// <returns>The reference tier, or null when the ladder is empty.</returns>
        /// <remarks>
        /// This mirrors HlsPlaylistFilter.SelectDefault, which orders by BANDWIDTH. VideoQuality
        /// carries no bandwidth, so descending height stands in for it. The substitution assumes that
        /// within one codec a taller variant also carries the higher bitrate — true of every ladder
        /// measured on these platforms, but an assumption rather than an identity.
        /// <para>
        /// Two distinct things can go wrong and only one of them is loud. A tier listed here that the
        /// master does not carry makes SelectExact return nothing, so the route answers 503. A ladder
        /// whose height order disagrees with its bandwidth order instead elects a different reference
        /// tier from the one auto plays: nothing fails, and every menu entry still serves exactly the
        /// tier it names — only the claim that the reference tier mirrors auto goes stale.
        /// </para>
        /// </remarks>
        internal static Tier? ReferenceTier(IReadOnlyList<VideoQuality> qualities)
        {
            if (qualities.Count == 0)
            {
                return null;
            }

            var avc = qualities.Where(q => string.Equals(q.Codec, "h264", StringComparison.Ordinal)).ToList();
            if (avc.Count > 0)
            {
                return Elect(avc);
            }

            // Falling back to the whole ladder rather than giving up: a source whose every height
            // exceeds the ceiling must still be offered something.
            var capped = qualities.Where(q => q.Height <= ReferenceFallbackHeight).ToList();
            return Elect(capped.Count > 0 ? capped : qualities);
        }

        /// <summary>The best tier at the tallest height available, whatever its codec.</summary>
        /// <param name="qualities">The stored ladder, as captured at sync.</param>
        /// <returns>The best tier, or null when the ladder is empty.</returns>
        internal static Tier? BestTier(IReadOnlyList<VideoQuality> qualities)
        {
            if (qualities.Count == 0)
            {
                return null;
            }

            var top = qualities.Max(q => q.Height);
            return Elect(qualities.Where(q => q.Height == top).ToList());
        }

        /// <summary>
        /// The tiers to offer, reference first: the reference tier alone, or the reference tier
        /// followed by the best available when the two differ.
        /// </summary>
        /// <param name="qualities">The stored ladder, as captured at sync.</param>
        /// <returns>Zero, one or two tiers.</returns>
        /// <remarks>
        /// Bounded at two by user decision, which bounds the 1:n item relation at three items per
        /// video and keeps the sync's reconciliation tractable.
        /// </remarks>
        internal static IReadOnlyList<Tier> OfferedTiers(IReadOnlyList<VideoQuality> qualities)
        {
            var reference = ReferenceTier(qualities);
            if (reference is null)
            {
                return [];
            }

            var best = BestTier(qualities);

            // Tier is a record, so this compares height, codec and fps structurally.
            return best is null || best == reference ? [reference] : [reference, best];
        }

        /// <summary>
        /// The label shown in the "Version" menu: the height, the fps when it exceeds 30, and the
        /// codec when it is not h264 — which is how the menu warns that a tier will transcode.
        /// </summary>
        /// <param name="tier">The tier to label.</param>
        internal static string LabelFor(Tier tier)
        {
            var label = tier.Fps > 30
                ? string.Create(CultureInfo.InvariantCulture, $"{tier.Height}p{tier.Fps}")
                : string.Create(CultureInfo.InvariantCulture, $"{tier.Height}p");

            return tier.Codec == "h264"
                ? label
                : $"{label} ({tier.Codec.ToUpperInvariant()})";
        }

        /// <summary>Lower ranks win. Unlisted codecs all tie, and fps breaks the tie.</summary>
        private static int RankOf(string codec)
        {
            var index = Array.IndexOf(CodecPreference, codec);
            return index < 0 ? CodecPreference.Length : index;
        }

        /// <summary>
        /// The winner among candidates: tallest first, then the codec preference, then the higher
        /// fps.
        /// </summary>
        private static Tier Elect(IReadOnlyList<VideoQuality> candidates)
        {
            var elected = candidates
                .OrderByDescending(q => q.Height)
                .ThenBy(q => RankOf(q.Codec))
                .ThenByDescending(q => q.Fps ?? 0)
                .First();

            return new Tier(elected.Height, elected.Codec, elected.Fps);
        }
    }
}

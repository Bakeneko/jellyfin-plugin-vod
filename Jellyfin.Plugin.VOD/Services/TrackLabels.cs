using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>Copies a filtered master's track names onto a described stream list.</summary>
    /// <remarks>
    /// ffprobe does read a rendition's NAME, but files it under <c>MediaStream.Comment</c>
    /// (measured 2026-10-09, spec §8.1 B), which <c>MediaStream.DisplayTitle</c> never consults — so
    /// a probe alone renders Arte's two French tracks as one indistinguishable pair. The NAME
    /// therefore has to be copied into <c>Title</c>, which DisplayTitle leads with.
    /// <para>
    /// Shared between the playback writer and StreamDescriber: both put the same
    /// labels on the same lists, and one pairing rule is what keeps them identical.
    /// </para>
    /// </remarks>
    public static class TrackLabels
    {
        /// <summary>Labels the described streams, and reports how many labels changed.</summary>
        /// <param name="described">The stored or freshly probed streams, mutated in place.</param>
        /// <param name="fromMaster">The streams derived from the filtered master.</param>
        /// <returns>The number of labels this call changed.</returns>
        /// <remarks>
        /// Paired by type and by position within that type. Both lists describe the same master in
        /// the same declaration order, and two audio renditions of one language — Arte ships
        /// "français" beside "français (confort audio)" — cannot be told apart by language or codec.
        /// Pairing by position within a type is also what makes the video's trailing position
        /// harmless. A type the prober reports more of than the master does is left unlabelled.
        /// </remarks>
        public static int Apply(
            IReadOnlyList<MediaStream> described,
            IReadOnlyList<MediaStream> fromMaster)
        {
            ArgumentNullException.ThrowIfNull(described);
            ArgumentNullException.ThrowIfNull(fromMaster);

            var labels = new Dictionary<MediaStreamType, List<string?>>();
            foreach (var stream in fromMaster)
            {
                if (!labels.TryGetValue(stream.Type, out var forType))
                {
                    forType = [];
                    labels[stream.Type] = forType;
                }

                forType.Add(stream.Title);
            }

            var titled = 0;
            var seen = new Dictionary<MediaStreamType, int>();
            foreach (var stream in described)
            {
                var position = seen.GetValueOrDefault(stream.Type);
                seen[stream.Type] = position + 1;

                if (!labels.TryGetValue(stream.Type, out var forType) || position >= forType.Count)
                {
                    continue;
                }

                var label = forType[position];
                if (string.IsNullOrEmpty(label)
                    || IsPlaceholder(label)
                    || string.Equals(stream.Title, label, StringComparison.Ordinal))
                {
                    continue;
                }

                stream.Title = label;
                titled++;
            }

            return titled;
        }

        /// <summary>
        /// Whether a master's track name says nothing <c>MediaStream.DisplayTitle</c> would not say
        /// on its own.
        /// </summary>
        /// <param name="label">The NAME attribute read from the master.</param>
        /// <returns><see langword="true"/> when the label should not be stored.</returns>
        /// <remarks>
        /// YouTube names a lone audio rendition "Default", with no LANGUAGE attribute. Since
        /// DisplayTitle leads with Title when it is set, storing that costs information rather than
        /// adding any: an honest "French - AAC - Stereo - Par défaut" becomes a bare "Default".
        /// </remarks>
        private static bool IsPlaceholder(string label)
            => string.Equals(label.Trim(), "Default", StringComparison.OrdinalIgnoreCase);
    }
}

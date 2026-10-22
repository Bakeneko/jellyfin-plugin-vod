using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.VOD.Configuration;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// A fingerprint of the source configuration fields that the pass must re-list when they change.
    /// </summary>
    /// <remarks>
    /// The fingerprint is compared against the stored <c>ConfigFingerprint</c> on each pass:
    /// a disagreement clears <c>FullListedAt</c>, making a full listing due and eventually
    /// bringing the source back through the checkup. One hash over every relevant field, rather than
    /// one column per field, keeps the comparison to a single equality check and prevents the v3
    /// mistake of only comparing the published mode.
    /// </remarks>
    public static class SourceFingerprint
    {
        /// <summary>
        /// Returns a fingerprint of the five configuration fields that, when changed, must force a
        /// full re-listing.
        /// </summary>
        /// <param name="source">The source definition to fingerprint.</param>
        /// <returns>A SHA-256 hex string that changes when and only when a relevant field changes.</returns>
        /// <remarks>
        /// <para>
        /// Five fields enter the fingerprint, and the rationale for each is the spec's:
        /// <list type="bullet">
        ///   <item><description>
        ///     <c>MaxVideosPerSource</c> changes the listing window: raising it brings in older
        ///     videos, lowering it drops them. A new listing is always due when this changes.
        ///   </description></item>
        ///   <item><description>
        ///     <c>Mode</c>, <c>RetentionDays</c> and <c>Name</c> only need a checkup — but the
        ///     checkup only runs when the source has pending work. A source with nothing to do would
        ///     never be revisited, and the change would silently have no effect until the next
        ///     full-listing cycle. This is the v3 defect (which compared only the published mode),
        ///     here generalised to all three fields.
        ///   </description></item>
        ///   <item><description>
        ///     <c>Enabled</c>: re-enabling a source after a long gap must re-list it because its
        ///     data may be stale.
        ///   </description></item>
        /// </list>
        /// </para>
        /// <para>
        /// <c>Url</c> is NOT included: the URL is the primary key, so a changed URL creates a new
        /// row and the old one is retired by the "left the configuration" rule. Including it here
        /// would be harmless but misleading — one might think a URL change is handled here when it
        /// is handled elsewhere.
        /// </para>
        /// <para>
        /// <c>Description</c> is NOT included: it is an administrator note and is never published,
        /// so no listing is triggered when it changes.
        /// </para>
        /// <para>
        /// All fields are formatted under <see cref="CultureInfo.InvariantCulture"/> so that a
        /// server whose locale changes (e.g. during an OS update) does not falsely signal a
        /// configuration change and re-list every source.
        /// </para>
        /// <para>
        /// The fields are delimited by U+0000 (null byte) before hashing. The XML specification
        /// forbids null bytes in attribute and element text, so a user-supplied <c>Name</c> can
        /// never contain one. This prevents a Name from reaching across the delimiter and colliding
        /// with an adjacent field's value — the ambiguity that plain string concatenation cannot
        /// rule out. The SHA-256 digest is then returned as uppercase hex: compact, printable, and
        /// safe to store verbatim in the database.
        /// </para>
        /// </remarks>
        public static string Of(SourceDefinition source)
        {
            ArgumentNullException.ThrowIfNull(source);

            // Name initialises to string.Empty in SourceDefinition, but XML deserialization may
            // set it to null; treat both as the same empty state.
            var name = source.Name ?? string.Empty;

            // Separate fields with U+0000. XML forbids null bytes in text content, so no
            // user-supplied Name can smuggle one in and shift the boundary between fields.
            var canonical = string.Join(
                '\0',
                source.Enabled.ToString(CultureInfo.InvariantCulture),
                source.Mode.ToString(),                                                          // enum name: compile-time constant, culture-neutral
                source.MaxVideosPerSource.ToString(CultureInfo.InvariantCulture),
                source.RetentionDays.ToString(CultureInfo.InvariantCulture),
                name);

            var bytes = Encoding.UTF8.GetBytes(canonical);
            var hash = SHA256.HashData(bytes);

            // Convert.ToHexString returns uppercase hex without dashes, matching the compact
            // printable format expected for a stored fingerprint.
            return Convert.ToHexString(hash);
        }
    }
}

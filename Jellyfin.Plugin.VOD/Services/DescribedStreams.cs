using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// How a described video's streams are keyed, one entry per version-group member.
    /// </summary>
    /// <remarks>
    /// Was <c>StoredStreams</c>, when a description had to be serialised onto the row to survive
    /// until publication. It no longer does: describing and publishing now happen in the same pass,
    /// so the dictionary travels in memory. What remains is the keying and the deep copy, which the
    /// describer itself needs.
    /// </remarks>
    internal static class DescribedStreams
    {
        /// <summary>The key of the primary item, which serves the default selection.</summary>
        internal const string PrimaryKey = "primary";

        /// <remarks>
        /// Only <see cref="Clone"/> uses these now. Computed get-only members are dropped, because
        /// Jellyfin recomputes them from the stored fields. Default values are dropped too, because
        /// reading them back yields the same default.
        /// </remarks>
        private static readonly JsonSerializerOptions _options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { DropGetOnlyProperties },
            },
        };

        private static void DropGetOnlyProperties(JsonTypeInfo typeInfo)
        {
            for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                if (typeInfo.Properties[i].Set is null)
                {
                    typeInfo.Properties.RemoveAt(i);
                }
            }
        }

        /// <summary>The key of a member: <see cref="PrimaryKey"/>, or "height/codec" for a tier.</summary>
        /// <param name="height">The tier's height, or null for the primary.</param>
        /// <param name="codec">The tier's codec, or null for the primary.</param>
        /// <returns>The member's key. Half a selection is the primary, as it is for the route.</returns>
        internal static string KeyFor(int? height, string? codec)
            => height.HasValue && codec is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{height.Value}/{codec}")
                : PrimaryKey;

        /// <summary>Deep-copies streams, so relabelling the copy cannot reach the original.</summary>
        /// <param name="streams">The streams to copy.</param>
        /// <returns>Independent instances with the same values.</returns>
        internal static List<MediaStream> Clone(IReadOnlyList<MediaStream> streams)
        {
            ArgumentNullException.ThrowIfNull(streams);
            return JsonSerializer.Deserialize<List<MediaStream>>(
                JsonSerializer.Serialize(streams, _options), _options) ?? [];
        }
    }
}

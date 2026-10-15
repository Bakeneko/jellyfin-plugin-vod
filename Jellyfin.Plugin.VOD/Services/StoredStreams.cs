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
    /// The JSON shape of <c>videos.stream_descriptions</c>: each version-group member's media
    /// streams, keyed by the member, exactly as <see cref="StreamDescriber"/> would save them.
    /// </summary>
    internal static class StoredStreams
    {
        /// <summary>The key of the primary item, which serves the default selection.</summary>
        internal const string PrimaryKey = "primary";

        /// <remarks>
        /// The JSON is kept for good, on every video row, so its size is multiplied by the row
        /// count. Computed get-only members are dropped, because Jellyfin recomputes them from the
        /// stored fields. Default values are dropped too, because reading them back yields the same
        /// default. Text stays literal ("français", not escapes): this JSON never reaches HTML.
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

        /// <summary>Serializes every member's streams.</summary>
        /// <param name="byMember">The streams, keyed by <see cref="KeyFor"/>.</param>
        /// <returns>The JSON to store.</returns>
        internal static string Serialize(IReadOnlyDictionary<string, IReadOnlyList<MediaStream>> byMember)
            => JsonSerializer.Serialize(byMember, _options);

        /// <summary>Reads a stored value back.</summary>
        /// <param name="json">The stored JSON, possibly null or hand-edited.</param>
        /// <returns>The streams by member, or null when there is nothing usable.</returns>
        internal static Dictionary<string, List<MediaStream>>? Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<MediaStream>>>(json, _options);
            }
            catch (JsonException)
            {
                // A truncated or hand-edited value must not take publication down: the failsafe at
                // playback describes the item instead.
                return null;
            }
        }

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

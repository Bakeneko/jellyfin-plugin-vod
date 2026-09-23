using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>
    /// Reading of the string arrays yt-dlp reports, shared by the source and video parsers so the two
    /// cannot drift apart on what they accept or how many entries they keep.
    /// </summary>
    internal static class JsonArrays
    {
        /// <summary>
        /// Returns up to <paramref name="limit"/> non-blank strings from an array property, in the
        /// order the platform reported them.
        /// </summary>
        /// <param name="root">The object to read from.</param>
        /// <param name="name">The property holding the array.</param>
        /// <param name="limit">How many entries to keep.</param>
        /// <returns>The strings, or an empty list when the property is absent or not an array.</returns>
        public static IReadOnlyList<string> ReadStrings(JsonElement root, string name, int limit)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
                return [];

            var values = new List<string>();
            foreach (var element in array.EnumerateArray())
            {
                if (values.Count == limit)
                    break;

                if (element.ValueKind != JsonValueKind.String)
                    continue;

                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }

            return values;
        }
    }
}

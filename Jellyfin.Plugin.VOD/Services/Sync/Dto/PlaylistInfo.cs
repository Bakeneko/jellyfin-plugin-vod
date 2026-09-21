using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>The result of a yt-dlp flat-playlist invocation.</summary>
    public sealed class PlaylistInfo
    {
        /// <summary>Gets the playlist/channel title as reported by the platform.</summary>
        public string? Title { get; init; }

        /// <summary>Gets the thumbnail URL of the playlist or channel.</summary>
        public string? ThumbnailUrl { get; init; }

        /// <summary>Gets the playlist description.</summary>
        public string? Description { get; init; }

        /// <summary>Gets the video entries in enumeration order.</summary>
        public IReadOnlyList<PlaylistEntry> Entries { get; init; } = [];

        /// <summary>
        /// Parses the JSON output of <c>yt-dlp --flat-playlist -J</c>.
        /// Returns <see langword="null"/> on parse failure.
        /// </summary>
        public static PlaylistInfo? ParseJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var type = root.TryGetProperty("_type", out var t) ? t.GetString() : null;

                // Single video URL — wrap it as a one-entry playlist
                if (!string.Equals(type, "playlist", System.StringComparison.OrdinalIgnoreCase))
                {
                    var entry = ParseEntry(root);
                    return entry is null ? null : new PlaylistInfo
                    {
                        Title = root.TryGetProperty("title", out var tt) ? tt.GetString() : null,
                        ThumbnailUrl = root.TryGetProperty("thumbnail", out var th) ? th.GetString() : null,
                        Description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
                        Entries = [entry]
                    };
                }

                var entries = new List<PlaylistEntry>();
                if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var e = ParseEntry(el);
                        if (e is not null)
                            entries.Add(e);
                    }
                }

                // Thumbnail: first entry of "thumbnails" array, or top-level "thumbnail"
                string? thumb = null;
                if (root.TryGetProperty("thumbnails", out var thumbs)
                    && thumbs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var th in thumbs.EnumerateArray())
                    {
                        if (th.TryGetProperty("url", out var u))
                        {
                            thumb = u.GetString();
                            break;
                        }
                    }
                }
                if (thumb is null && root.TryGetProperty("thumbnail", out var tTop))
                    thumb = tTop.GetString();

                return new PlaylistInfo
                {
                    Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
                    ThumbnailUrl = thumb,
                    Description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    Entries = entries
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static PlaylistEntry? ParseEntry(JsonElement el)
        {
            // Prefer webpage_url; fall back to url
            string? url = null;
            if (el.TryGetProperty("webpage_url", out var wp) && wp.ValueKind == JsonValueKind.String)
                url = wp.GetString();
            if (string.IsNullOrWhiteSpace(url) && el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                url = u.GetString();

            if (string.IsNullOrWhiteSpace(url) || !el.TryGetProperty("id", out var idEl))
                return null;

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
                return null;

            double? duration = null;
            if (el.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                duration = dur.GetDouble();

            return new PlaylistEntry
            {
                Id = id,
                VideoUrl = url,
                Title = el.TryGetProperty("title", out var title) ? title.GetString() : null,
                IeKey = el.TryGetProperty("ie_key", out var iek) ? (iek.GetString() ?? string.Empty) : string.Empty,
                Duration = duration
            };
        }
    }
}

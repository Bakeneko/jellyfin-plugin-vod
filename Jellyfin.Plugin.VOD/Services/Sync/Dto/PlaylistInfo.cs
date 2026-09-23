using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>The result of a yt-dlp flat-playlist invocation.</summary>
    public sealed class PlaylistInfo
    {
        /// <summary>Gets the playlist/channel title as reported by the platform.</summary>
        public string? Title { get; init; }

        /// <summary>
        /// Gets the portrait-ish image for the playlist or channel, used as the series Primary.
        /// </summary>
        public string? ThumbnailUrl { get; init; }

        /// <summary>
        /// Gets the wide image for the playlist or channel, used as the series Thumb. On YouTube this
        /// is the channel banner; most other sources have none.
        /// </summary>
        public string? BannerUrl { get; init; }

        /// <summary>
        /// Gets the channel name behind the source, when the platform reports one.
        /// </summary>
        /// <remarks>
        /// For a channel tab, yt-dlp's <c>title</c> is the TAB's name — "Le Fossoyeur de Films -
        /// Videos", "… - Live", "… - Shorts" — while <c>channel</c> holds the name alone. For a
        /// playlist the same field holds the playlist's owner, which is a different thing entirely,
        /// so only a channel source may use it: see <see cref="VodNativeId.IsChannel"/>.
        /// </remarks>
        public string? ChannelName { get; init; }

        /// <summary>Gets the channel or playlist tags, capped like a video's.</summary>
        public IReadOnlyList<string> Tags { get; init; } = [];

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

                var (thumb, banner) = PickImages(root);

                return new PlaylistInfo
                {
                    Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
                    ChannelName = ReadString(root, "channel") ?? ReadString(root, "uploader"),
                    Tags = JsonArrays.ReadStrings(root, "tags", VideoMetadata.MaxTags),
                    ThumbnailUrl = thumb,
                    BannerUrl = banner,
                    Description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    Entries = entries
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Splits a yt-dlp "thumbnails" array into the image that belongs in Primary and the one that
        /// belongs in Thumb.
        /// </summary>
        /// <remarks>
        /// A YouTube channel reports no scalar "thumbnail" and lists its banner first (1060x175 up to
        /// 2560x424) with the square avatar last, so taking the first entry — as this did — used a 6:1
        /// banner as the series poster. Squareness is the discriminator rather than yt-dlp's "avatar"
        /// and "banner" ids, which are a naming convention of one extractor.
        /// </remarks>
        private static (string? Primary, string? Banner) PickImages(JsonElement root)
        {
            string? squarest = null;
            long squarestArea = -1;
            string? widest = null;
            double widestRatio = 0;
            string? largest = null;
            long largestArea = -1;

            if (root.TryGetProperty("thumbnails", out var thumbs)
                && thumbs.ValueKind == JsonValueKind.Array)
            {
                foreach (var th in thumbs.EnumerateArray())
                {
                    if (!th.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
                        continue;

                    var url = u.GetString();
                    if (string.IsNullOrEmpty(url))
                        continue;

                    var w = th.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number
                        ? wEl.GetInt32() : 0;
                    var h = th.TryGetProperty("height", out var hEl) && hEl.ValueKind == JsonValueKind.Number
                        ? hEl.GetInt32() : 0;

                    if (w <= 0 || h <= 0)
                    {
                        // Undimensioned entries ("avatar_uncropped", "banner_uncropped") only serve as a
                        // last resort, since nothing says which shape they are.
                        largest ??= url;
                        continue;
                    }

                    long area = (long)w * h;
                    if (area > largestArea)
                    {
                        largestArea = area;
                        largest = url;
                    }

                    var ratio = (double)w / h;

                    // Square enough to crop into a poster without losing the subject.
                    if (ratio <= 1.35 && area > squarestArea)
                    {
                        squarestArea = area;
                        squarest = url;
                    }

                    // Clearly wider than 16:9 is a banner, not a still.
                    if (ratio >= 2.5 && ratio > widestRatio)
                    {
                        widestRatio = ratio;
                        widest = url;
                    }
                }
            }

            string? scalar = root.TryGetProperty("thumbnail", out var tTop) && tTop.ValueKind == JsonValueKind.String
                ? tTop.GetString() : null;

            var primary = scalar ?? squarest ?? largest;

            // Never hand the same image to both slots through the banner path: a source with one
            // landscape still has a Thumb only because the publisher copies it there knowingly.
            var banner = string.Equals(widest, primary, System.StringComparison.Ordinal) ? null : widest;

            return (primary, banner);
        }

        private static string? ReadString(JsonElement root, string name)
            => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;

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

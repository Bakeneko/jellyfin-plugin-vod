using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>Full metadata for one video as returned by <c>yt-dlp -J --no-playlist</c>.</summary>
    public sealed class VideoMetadata
    {
        /// <summary>Gets the native video ID.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Gets the primary title. For Arte episodes this is often the series name.</summary>
        public string? Title { get; init; }

        /// <summary>Gets the alternative title. For Arte episodes this is the episode title.</summary>
        public string? AltTitle { get; init; }

        /// <summary>Gets the video description.</summary>
        public string? Description { get; init; }

        /// <summary>Gets the upload date in YYYYMMDD format.</summary>
        public string? UploadDate { get; init; }

        /// <summary>Gets the upload timestamp as Unix seconds.</summary>
        public long? Timestamp { get; init; }

        /// <summary>Gets the best thumbnail URL.</summary>
        public string? Thumbnail { get; init; }

        /// <summary>Gets the duration in seconds.</summary>
        public double? Duration { get; init; }

        /// <summary>Gets the yt-dlp extractor key (e.g. "Youtube", "ArteTV").</summary>
        public string? ExtractorKey { get; init; }

        /// <summary>Gets the canonical webpage URL for this video.</summary>
        public string? WebpageUrl { get; init; }

        /// <summary>
        /// Gets the <c>_type</c> field. Relevant values: "video" (normal), "url_transparent" (Arte series episode).
        /// </summary>
        public string? Type { get; init; }

        /// <summary>Gets the native season number (Arte url_transparent entries only; yt-dlp never sets this for other sources).</summary>
        public int? SeasonNumber { get; init; }

        /// <summary>Gets the native episode number (Arte url_transparent entries only).</summary>
        public int? EpisodeNumber { get; init; }

        /// <summary>Gets the series name when the entry belongs to a series (Arte).</summary>
        public string? Series { get; init; }

        /// <summary>Gets a value indicating whether this entry is a live stream currently in progress.</summary>
        public bool IsLive { get; init; }

        /// <summary>
        /// Gets a value indicating whether this entry is a scheduled stream that has not yet started.
        /// yt-dlp cannot resolve the stream URL for upcoming entries; playback will fail until the stream begins.
        /// </summary>
        public bool IsUpcoming { get; init; }

        /// <summary>
        /// Gets the scheduled start time as Unix seconds (<c>release_timestamp</c>).
        /// Present for upcoming streams; used as <c>PublishedAt</c> when <c>upload_date</c> is absent.
        /// </summary>
        public long? ReleaseTimestamp { get; init; }

        /// <summary>Gets the unique audio language codes extracted from yt-dlp formats.</summary>
        public IReadOnlyList<string> AudioLanguages { get; init; } = [];

        /// <summary>Gets the subtitle language codes from the yt-dlp subtitles dictionary (not auto-captions).</summary>
        public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];

        /// <summary>
        /// Parses the JSON output of <c>yt-dlp -J --no-playlist</c>.
        /// Returns <see langword="null"/> on parse failure.
        /// </summary>
        public static VideoMetadata? ParseJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    return null;

                // Best thumbnail: prefer "thumbnail" scalar; fallback to last entry of "thumbnails" array
                // (yt-dlp sorts thumbnails lowest-quality first; last is best)
                string? thumb = root.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;
                if (thumb is null && root.TryGetProperty("thumbnails", out var thumbArr)
                    && thumbArr.ValueKind == JsonValueKind.Array)
                {
                    string? last = null;
                    foreach (var t in thumbArr.EnumerateArray())
                    {
                        if (t.TryGetProperty("url", out var u))
                            last = u.GetString();
                    }
                    thumb = last;
                }

                // Audio languages: unique non-null language values from formats with audio
                var audioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("formats", out var formats)
                    && formats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fmt in formats.EnumerateArray())
                    {
                        if (!fmt.TryGetProperty("acodec", out var acodec)) continue;
                        var ac = acodec.GetString();
                        if (string.IsNullOrEmpty(ac) || ac.Equals("none", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (fmt.TryGetProperty("language", out var lang)
                            && lang.ValueKind == JsonValueKind.String)
                        {
                            var l = lang.GetString();
                            if (!string.IsNullOrWhiteSpace(l))
                                audioLangs.Add(l);
                        }
                    }
                }

                // Subtitle languages: keys of "subtitles" dict (not "automatic_captions")
                var subLangs = new List<string>();
                if (root.TryGetProperty("subtitles", out var subs)
                    && subs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in subs.EnumerateObject())
                        subLangs.Add(prop.Name);
                }

                return new VideoMetadata
                {
                    Id = id,
                    Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
                    AltTitle = root.TryGetProperty("alt_title", out var alt) ? alt.GetString() : null,
                    Description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    UploadDate = root.TryGetProperty("upload_date", out var ud) ? ud.GetString() : null,
                    Timestamp = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
                        ? ts.GetInt64() : null,
                    Thumbnail = thumb,
                    Duration = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                        ? dur.GetDouble() : null,
                    ExtractorKey = root.TryGetProperty("extractor_key", out var ek) ? ek.GetString() : null,
                    WebpageUrl = root.TryGetProperty("webpage_url", out var wu) ? wu.GetString() : null,
                    Type = root.TryGetProperty("_type", out var tp) ? tp.GetString() : "video",
                    SeasonNumber = root.TryGetProperty("season_number", out var sn) && sn.ValueKind == JsonValueKind.Number
                        ? sn.GetInt32() : null,
                    EpisodeNumber = root.TryGetProperty("episode_number", out var en) && en.ValueKind == JsonValueKind.Number
                        ? en.GetInt32() : null,
                    Series = root.TryGetProperty("series", out var series) ? series.GetString() : null,
                    IsLive = root.TryGetProperty("is_live", out var il) && il.ValueKind == JsonValueKind.True,
                    IsUpcoming = root.TryGetProperty("is_upcoming", out var iu) && iu.ValueKind == JsonValueKind.True,
                    ReleaseTimestamp = root.TryGetProperty("release_timestamp", out var rt) && rt.ValueKind == JsonValueKind.Number
                        ? rt.GetInt64() : null,
                    AudioLanguages = [.. audioLangs],
                    SubtitleLanguages = subLangs
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

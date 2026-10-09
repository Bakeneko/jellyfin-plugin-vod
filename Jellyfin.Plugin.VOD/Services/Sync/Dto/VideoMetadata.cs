using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>Full metadata for one video as returned by <c>yt-dlp -J --no-playlist</c>.</summary>
    public sealed class VideoMetadata
    {
        /// <summary>Gets the native video ID.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Gets the channels that own or uploaded the video, deduplicated, in reporting order.
        /// </summary>
        /// <remarks>
        /// yt-dlp reports <c>channel</c> and <c>uploader</c> as separate scalars. On YouTube they
        /// usually hold the same name and collapse to one entry, but they diverge on auto-generated
        /// "Topic" channels, where the uploader is the artist behind the channel. A platform that
        /// reports neither — Arte among them — yields an empty list, and the video is published with
        /// no studio rather than a made-up one.
        /// </remarks>
        public IReadOnlyList<string> Channels { get; init; } = [];

        /// <summary>Gets the primary title. For Arte episodes this is often the series name.</summary>
        public string? Title { get; init; }

        /// <summary>The title without the date yt-dlp appends to a live broadcast.</summary>
        public string? FullTitle { get; init; }

        /// <summary>How many formats yt-dlp listed, whatever their kind.</summary>
        public int FormatCount { get; init; }

        /// <summary>Whether the extraction proves nothing can be played.</summary>
        /// <remarks>
        /// <c>--ignore-no-formats-error</c> is required to read a scheduled live, which has no format
        /// until it starts, but it also turns "Private video" and its kin into a success carrying no
        /// format at all — measured 2026-10-08 on ELa4xqlV8sw. A video with no format that is not
        /// scheduled is therefore an anomaly to report, not a record to store.
        /// </remarks>
        public bool IsUnavailable => FormatCount == 0 && LiveStatus != LiveStatus.Upcoming;

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

        /// <summary>Whether the video is broadcasting now, scheduled, or neither.</summary>
        public LiveStatus LiveStatus { get; init; }

        /// <summary>
        /// Gets the scheduled start time as Unix seconds (<c>release_timestamp</c>).
        /// Present for upcoming streams; used as <c>PublishedAt</c> when <c>upload_date</c> is absent.
        /// </summary>
        public long? ReleaseTimestamp { get; init; }

        /// <summary>The video qualities reachable through HLS, ascending by height then codec.</summary>
        public IReadOnlyList<VideoQuality> Qualities { get; init; } = [];

        /// <summary>The audio tracks reachable through HLS, in yt-dlp's reporting order.</summary>
        public IReadOnlyList<AudioTrack> AudioTracks { get; init; } = [];

        /// <summary>Gets the subtitle language codes from the yt-dlp subtitles dictionary (not auto-captions).</summary>
        public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];

        /// <summary>Gets the video's tags, capped at <see cref="MaxTags"/>.</summary>
        public IReadOnlyList<string> Tags { get; init; } = [];

        /// <summary>Gets the video's categories, published as Jellyfin genres.</summary>
        public IReadOnlyList<string> Categories { get; init; } = [];

        /// <summary>
        /// How many tags an item keeps.
        /// </summary>
        /// <remarks>
        /// yt-dlp preserves the order the author declared, and on every sample measured the general
        /// tags come first and the tail degrades into search-engine filler — film titles merely
        /// mentioned in the video, or player nicknames. Keeping the head is therefore keeping the
        /// meaningful ones.
        /// </remarks>
        public const int MaxTags = 15;

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

                // Only HLS formats exist for this plugin: ResolveAsync selects bv*[protocol^=m3u8]
                // and the filter parses an HLS master. This single rule excludes storyboards
                // (mhtml) and every DASH-only format (https) — AV1 among them, which the HLS path
                // cannot serve. It also fixes a latent bug: the HLS audio renditions report
                // "acodec": null, so a predicate keyed on acodec dropped all of them.
                var qualities = new List<VideoQuality>();
                var audioTracks = new List<AudioTrack>();
                var formatCount = 0;
                var seenQualities = new HashSet<(int, string, int?)>();

                if (root.TryGetProperty("formats", out var formats)
                    && formats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fmt in formats.EnumerateArray())
                    {
                        formatCount++;
                        if (!IsHlsFormat(fmt))
                            continue;

                        var vcodec = ReadString(fmt, "vcodec");
                        if (string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            audioTracks.Add(new AudioTrack(
                                ReadString(fmt, "language"), ReadString(fmt, "format_id")));
                            continue;
                        }

                        var height = ReadInt(fmt, "height");
                        var codec = CodecNames.Normalise(vcodec);
                        if (height is null || codec is null)
                            continue;

                        var fps = ReadInt(fmt, "fps");
                        if (seenQualities.Add((height.Value, codec, fps)))
                            qualities.Add(new VideoQuality(height.Value, codec, fps));
                    }
                }

                qualities.Sort((a, b) => a.Height != b.Height
                    ? a.Height.CompareTo(b.Height)
                    : string.CompareOrdinal(a.Codec, b.Codec));

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
                    FullTitle = root.TryGetProperty("fulltitle", out var ft) ? ft.GetString() : null,
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
                    LiveStatus = LiveStatusParser.FromYtDlp(
                        root.TryGetProperty("live_status", out var ls) && ls.ValueKind == JsonValueKind.String
                            ? ls.GetString()
                            : null),
                    ReleaseTimestamp = root.TryGetProperty("release_timestamp", out var rt) && rt.ValueKind == JsonValueKind.Number
                        ? rt.GetInt64() : null,
                    FormatCount = formatCount,
                    Qualities = qualities,
                    AudioTracks = audioTracks,
                    SubtitleLanguages = subLangs,
                    Tags = JsonArrays.ReadStrings(root, "tags", MaxTags),
                    Categories = JsonArrays.ReadStrings(root, "categories", MaxTags),
                    Channels = ReadChannels(root)
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parses the JSON output of <c>yt-dlp -J --playlist-items 1</c> and returns the first entry's metadata.
        /// Returns <see langword="null"/> when <c>entries</c> is absent, empty, or when its first value is null.
        /// </summary>
        /// <remarks>
        /// A flat Twitch listing carries no live status, so a full extraction of the head via
        /// <c>--playlist-items 1</c> answers both "is this channel live" and "what is the video" in a
        /// single call; measured at 2.1 s (spec F4). Without cookies the first entry may be null (spec F2).
        /// </remarks>
        public static VideoMetadata? ParseFirstEntryJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("entries", out var entries)
                    || entries.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Null)
                        return null;

                    // Re-serialise the entry element as a standalone JSON string and parse it via
                    // the main parser, so the field mapping stays in a single place.
                    return ParseJson(entry.GetRawText());
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Whether this format is reachable through the HLS path the plugin serves.</summary>
        private static bool IsHlsFormat(JsonElement fmt)
            => ReadString(fmt, "protocol") is { } p
               && p.Contains("m3u8", StringComparison.OrdinalIgnoreCase);

        private static string? ReadString(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static int? ReadInt(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(v.GetDouble()) : null;

        /// <summary>
        /// The scalar properties holding a channel name, in the order they are preferred.
        /// </summary>
        private static readonly string[] ChannelProperties = ["channel", "uploader"];

        private static List<string> ReadChannels(JsonElement root)
        {
            var channels = new List<string>(ChannelProperties.Length);

            foreach (var property in ChannelProperties)
            {
                if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
                    continue;

                var name = el.GetString()?.Trim();
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!channels.Exists(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
                    channels.Add(name);
            }

            return channels;
        }
    }
}

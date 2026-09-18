using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Derives Jellyfin <see cref="MediaStream"/> descriptors from the filtered master playlist.
    /// </summary>
    /// <remarks>
    /// The filtered master is the right source: it is exactly what the player receives, so Jellyfin
    /// cannot arbitrate direct-play against streams that differ from what is served. The master's
    /// NAME attributes also survive here — ffprobe loses them when probing an HLS source, so track
    /// labels like "English - original" or "français (confort audio)" would otherwise be lost.
    /// </remarks>
    public static class MediaStreamBuilder
    {
        /// <summary>
        /// Builds the stream list described by a filtered master playlist.
        /// Returns video first, then audio, then subtitles, indexed in that order.
        /// </summary>
        public static IReadOnlyList<MediaStream> Build(string master)
        {
            ArgumentNullException.ThrowIfNull(master);

            var streams = new List<MediaStream>();
            var audio = new List<MediaStream>();
            var subtitles = new List<MediaStream>();
            var lines = master.Split('\n', StringSplitOptions.TrimEntries);

            // The variant tag carries audio+video codecs but is written after renditions, so read it first.
            var variant = Array.Find(lines,
                l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
            var codecs = (HlsPlaylistFilter.Attribute(variant ?? string.Empty, "CODECS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var line in lines)
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var res = (HlsPlaylistFilter.Attribute(line, "RESOLUTION") ?? string.Empty)
                        .Split('x', StringSplitOptions.RemoveEmptyEntries);

                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Codec = NormaliseCodec(codecs.Length > 0 ? codecs[0] : null),
                        Width = res.Length == 2 ? ParseInt(res[0]) : null,
                        Height = res.Length == 2 ? ParseInt(res[1]) : null,
                        BitRate = ParseInt(HlsPlaylistFilter.Attribute(line, "BANDWIDTH")),
                        IsDefault = true
                    });
                    continue;
                }

                if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                {
                    continue;
                }

                var type = HlsPlaylistFilter.Attribute(line, "TYPE");
                var isAudio = string.Equals(type, "AUDIO", StringComparison.OrdinalIgnoreCase);
                if (!isAudio && !string.Equals(type, "SUBTITLES", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stream = new MediaStream
                {
                    Type = isAudio ? MediaStreamType.Audio : MediaStreamType.Subtitle,
                    Language = HlsPlaylistFilter.Attribute(line, "LANGUAGE"),
                    Title = HlsPlaylistFilter.Attribute(line, "NAME"),
                    IsDefault = string.Equals(
                        HlsPlaylistFilter.Attribute(line, "DEFAULT"), "YES",
                        StringComparison.OrdinalIgnoreCase),
                    IsForced = string.Equals(
                        HlsPlaylistFilter.Attribute(line, "FORCED"), "YES",
                        StringComparison.OrdinalIgnoreCase)
                };

                if (isAudio)
                {
                    stream.Codec = NormaliseCodec(codecs.Length > 1 ? codecs[1] : null);
                    audio.Add(stream);
                }
                else
                {
                    stream.Codec = "webvtt";
                    subtitles.Add(stream);
                }
            }

            if (streams.Count == 0)
            {
                return [];
            }

            streams.AddRange(audio);
            streams.AddRange(subtitles);
            for (var i = 0; i < streams.Count; i++)
            {
                streams[i].Index = i;
            }

            return streams;
        }

        private static string? NormaliseCodec(string? codec)
        {
            if (string.IsNullOrWhiteSpace(codec))
            {
                return null;
            }

            return codec.Split('.')[0].ToLowerInvariant() switch
            {
                "avc1" or "avc3" => "h264",
                "hev1" or "hvc1" => "hevc",
                "vp09" => "vp9",
                "av01" => "av1",
                "mp4a" => "aac",
                "opus" => "opus",
                "ec-3" => "eac3",
                "ac-3" => "ac3",
                var other => other
            };
        }

        private static int? ParseInt(string? value)
            => int.TryParse(value, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

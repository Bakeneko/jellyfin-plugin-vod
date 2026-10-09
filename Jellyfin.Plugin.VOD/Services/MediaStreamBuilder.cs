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
        /// Builds the stream list described by a filtered master playlist, indexed in the order the
        /// playlist declares them.
        /// </summary>
        /// <remarks>
        /// Playlist order is not a presentation choice: these indices become ffmpeg's <c>-map</c>
        /// arguments, and ffmpeg numbers an HLS master's streams in declaration order — renditions
        /// first, the variant last. Imposing any other order silently maps the wrong stream. Measured
        /// on a YouTube video carrying two audio renditions, where declaring the video at index 0 made
        /// Jellyfin emit <c>-map 0:0 -map 0:1</c> and produce two audio streams and no picture at all.
        /// </remarks>
        public static IReadOnlyList<MediaStream> Build(string master)
        {
            ArgumentNullException.ThrowIfNull(master);

            var streams = new List<MediaStream>();
            var sawVariant = false;
            var sawRendition = false;
            var lines = master.Split('\n', StringSplitOptions.TrimEntries);

            // The variant tag carries audio+video codecs but is written after renditions, so read it first.
            var variant = Array.Find(lines,
                l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
            var (videoCodec, audioCodec) = CodecNames.Split(
                HlsPlaylistFilter.Attribute(variant ?? string.Empty, "CODECS"));

            foreach (var line in lines)
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var res = (HlsPlaylistFilter.Attribute(line, "RESOLUTION") ?? string.Empty)
                        .Split('x', StringSplitOptions.RemoveEmptyEntries);

                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Codec = CodecNames.Normalise(videoCodec),
                        Width = res.Length == 2 ? ParseInt(res[0]) : null,
                        Height = res.Length == 2 ? ParseInt(res[1]) : null,
                        BitRate = ParseInt(HlsPlaylistFilter.Attribute(line, "BANDWIDTH")),
                        IsDefault = true
                    });
                    sawVariant = true;
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

                streams.Add(new MediaStream
                {
                    Type = isAudio ? MediaStreamType.Audio : MediaStreamType.Subtitle,
                    Codec = isAudio
                        ? CodecNames.Normalise(audioCodec)
                        : "webvtt",
                    Language = HlsPlaylistFilter.Attribute(line, "LANGUAGE"),
                    Title = HlsPlaylistFilter.Attribute(line, "NAME"),
                    IsDefault = string.Equals(
                        HlsPlaylistFilter.Attribute(line, "DEFAULT"), "YES",
                        StringComparison.OrdinalIgnoreCase),
                    IsForced = string.Equals(
                        HlsPlaylistFilter.Attribute(line, "FORCED"), "YES",
                        StringComparison.OrdinalIgnoreCase)
                });
                sawRendition = true;
            }

            if (!sawVariant)
            {
                return [];
            }

            // A muxed variant's streams live inside the container, in an order the playlist does not
            // state (a Twitch TS segment is audio, then video, then timed_id3 — spec F10), so any
            // order declared here is a guess that costs the picture when it wins the race against
            // Jellyfin's probe. It also has no rendition NAMEs, which is the only thing this builder
            // contributes beyond a probe, so there is nothing to declare.
            if (!sawRendition)
            {
                return [];
            }

            for (var i = 0; i < streams.Count; i++)
            {
                streams[i].Index = i;
            }

            return streams;
        }

        private static int? ParseInt(string? value)
            => int.TryParse(value, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

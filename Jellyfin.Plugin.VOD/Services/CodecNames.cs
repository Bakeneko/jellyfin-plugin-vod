using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Maps the codec identifiers HLS and yt-dlp use onto the names Jellyfin expects.
    /// </summary>
    /// <remarks>
    /// Shared between the yt-dlp metadata parser and <see cref="MediaStreamBuilder"/>: both read
    /// the same identifiers, and two copies would drift.
    /// </remarks>
    public static class CodecNames
    {
        private static readonly HashSet<string> _audioCodecs =
            ["aac", "opus", "eac3", "ac3", "mp3", "flac", "alac",
             "ac-4", "mhm1", "mha1", "dtsc", "dtse", "dtsx"];

        private static readonly HashSet<string> _textCodecs = ["wvtt", "stpp"];

        /// <summary>Splits a CODECS attribute into its video and audio entries, by kind.</summary>
        /// <param name="codecs">The attribute's value, such as "avc1.640028,mp4a.40.2".</param>
        /// <returns>The raw video and audio identifiers, each null when absent.</returns>
        /// <remarks>
        /// RFC 8216 does not order the CODECS list, so position says nothing about kind: an
        /// audio-first list read by position made every exact tier unreachable. An entry is audio or
        /// text only when it is known to be; anything else is taken as video, which keeps an
        /// unrecognised video codec (Dolby Vision's "dvh1", say) rather than dropping it.
        /// </remarks>
        public static (string? Video, string? Audio) Split(string? codecs)
        {
            string? video = null;
            string? audio = null;

            foreach (var entry in (codecs ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kind = Normalise(entry);
                if (kind is not null && _audioCodecs.Contains(kind))
                {
                    audio ??= entry;
                }
                else if (kind is null || !_textCodecs.Contains(kind))
                {
                    video ??= entry;
                }
            }

            return (video, audio);
        }

        /// <summary>Normalises one codec identifier, or returns null when there is nothing to map.</summary>
        /// <param name="codec">A codec identifier such as "avc1.640028" or "vp09.00.50.08".</param>
        public static string? Normalise(string? codec)
        {
            if (string.IsNullOrWhiteSpace(codec) || codec == "none")
            {
                return null;
            }

            return codec.Split('.')[0].ToLowerInvariant() switch
            {
                "avc1" or "avc3" => "h264",
                "hev1" or "hvc1" => "hevc",
                "vp09" or "vp9" => "vp9",
                "av01" => "av1",
                "mp4a" => "aac",
                "opus" => "opus",
                "ec-3" => "eac3",
                "ac-3" => "ac3",
                var other => other
            };
        }
    }
}

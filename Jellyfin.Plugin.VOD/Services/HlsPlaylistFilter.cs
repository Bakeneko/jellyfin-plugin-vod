using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Rewrites an upstream HLS master playlist into one Jellyfin's ffmpeg handles reliably.
    /// </summary>
    /// <remarks>
    /// Four behaviours make a bare upstream master unusable:
    /// (1) ffmpeg plays the first variant, not the best — Arte would play at 768x432.
    /// (2) YouTube ships ~38 auto-dubbed audio renditions; ffmpeg opens every single one, so
    /// MaxAudioRenditions caps how many reach the served master.
    /// (3) YouTube's timedtext subtitle URIs cause ffmpeg to abort ("Invalid data found…").
    /// (4) Jellyfin probes with -probesize 1G, so every extra rendition costs startup time.
    /// Output: one video variant (exact tier when requested, best AVC otherwise), genuine audio
    /// tracks, usable subtitles.
    /// </remarks>
    public static partial class HlsPlaylistFilter
    {
        private const int MaxAudioRenditions = 8;

        /// <summary>
        /// The ceiling applied to the default tier when no AVC variant exists. Not a setting: the
        /// figure is the highest tier every measured platform ships in AVC, so it is the point
        /// beyond which a default would start costing a transcode.
        /// </summary>
        private const int DefaultFallbackHeight = 1080;

        /// <summary>
        /// Rewrites a master playlist down to a single video variant with its audio and usable subtitles.
        /// Returns <see langword="null"/> if <paramref name="content"/> is not a master playlist,
        /// or when an exact tier was requested but is no longer offered upstream.
        /// </summary>
        /// <param name="content">The upstream master playlist text.</param>
        /// <param name="baseUri">The master's own URL, used to absolutise relative URIs.</param>
        /// <param name="height">The requested height, or null for the default tier.</param>
        /// <param name="codec">The requested codec, or null for the default tier.</param>
        /// <param name="audioLanguages">Language prefixes to keep (e.g. "fr", "en"). Empty = original only.</param>
        /// <param name="subtitleLanguages">Subtitle language prefixes to keep. Empty = keep all.</param>
        /// <param name="rewriteSubtitle">
        /// Maps a subtitle rendition URI to the one to serve, or null to drop. Pass null to keep all.
        /// Used to proxy YouTube's timedtext URIs that ffmpeg cannot parse.
        /// </param>
        public static string? FilterMaster(
            string content,
            Uri baseUri,
            int? height,
            string? codec,
            IReadOnlyCollection<string> audioLanguages,
            IReadOnlyCollection<string> subtitleLanguages,
            Func<string, string?>? rewriteSubtitle = null)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentNullException.ThrowIfNull(baseUri);

            var lines = content.Split('\n', StringSplitOptions.TrimEntries);
            var header = new List<string>();
            var media = new List<MediaRendition>();
            var variants = new List<Variant>();

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0
                    || line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                {
                    media.Add(new MediaRendition(
                        Attribute(line, "TYPE") ?? string.Empty,
                        Attribute(line, "GROUP-ID") ?? string.Empty,
                        Attribute(line, "URI") ?? string.Empty,
                        Attribute(line, "LANGUAGE") ?? string.Empty,
                        string.Equals(Attribute(line, "DEFAULT"), "YES",
                            StringComparison.OrdinalIgnoreCase),
                        line));
                    continue;
                }

                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var uri = NextUri(lines, i);
                    if (uri is not null)
                    {
                        var videoCodec = CodecNames.Split(Attribute(line, "CODECS")).Video;
                        variants.Add(new Variant(
                            ParseBandwidth(line),
                            ParseHeight(line),
                            // Normalised rather than matched on "avc1", so avc3 counts as H.264
                            // here exactly as it does in QualityLadder's reference-tier election.
                            string.Equals(
                                CodecNames.Normalise(videoCodec), "h264", StringComparison.Ordinal),
                            videoCodec,
                            Attribute(line, "AUDIO"),
                            Attribute(line, "SUBTITLES"),
                            line,
                            Absolutise(uri, baseUri)));
                    }

                    continue;
                }

                if (line.StartsWith("#EXT", StringComparison.Ordinal))
                {
                    header.Add(line);
                }
            }

            var best = height is { } h && codec is { } c
                ? SelectExact(variants, h, c)
                : SelectDefault(variants);
            if (best is null)
            {
                return null;
            }

            var audioGroup = media
                .Where(m => m.IsAudio && m.GroupId == best.AudioGroup)
                .ToList();

            // Auto-dubbed renditions are served like any other: the client labels them at playback,
            // so the viewer chooses knowingly. The old URI heuristic also made behaviour differ
            // between platforms, because only YouTube marks these in the URI. MaxAudioRenditions
            // below is what still bounds how many renditions ffmpeg will open.
            var candidates = audioGroup.ToList();

            if (candidates.Exists(m => m.IsOriginal))
            {
                candidates = candidates
                    .Where(m => m.IsOriginal || audioLanguages.Any(l => m.MatchesLanguage(l)))
                    .ToList();
            }

            var audio = candidates
                .OrderByDescending(m => m.IsOriginal)
                .ThenByDescending(m => m.IsDefault)
                .Take(MaxAudioRenditions)
                .ToList();

            if (audio.Count == 0)
            {
                var fallback = audioGroup.Find(m => m.IsDefault) ?? audioGroup.FirstOrDefault();
                if (fallback is not null)
                {
                    audio.Add(fallback);
                }
            }

            var subtitles = new List<MediaRendition>();
            foreach (var rendition in media.Where(m =>
                m.IsSubtitles && m.GroupId == best.SubtitleGroup))
            {
                if (subtitleLanguages.Count > 0 && !subtitleLanguages.Any(l => rendition.MatchesLanguage(l)))
                {
                    continue;
                }

                var absolute = AbsolutiseUriAttribute(rendition.Raw, baseUri);
                if (rewriteSubtitle is null)
                {
                    subtitles.Add(rendition with { Raw = absolute });
                    continue;
                }

                var served = rewriteSubtitle(Absolutise(rendition.Uri, baseUri));
                if (served is not null)
                {
                    subtitles.Add(rendition with { Raw = ReplaceUri(absolute, served), Uri = served });
                }
            }

            var sb = new StringBuilder();
            foreach (var hdr in header)
            {
                sb.Append(hdr).Append('\n');
            }

            for (var i = 0; i < audio.Count; i++)
            {
                sb.Append(AbsolutiseUriAttribute(ForceDefault(audio[i].Raw, i == 0), baseUri))
                  .Append('\n');
            }

            foreach (var s in subtitles)
            {
                sb.Append(s.Raw).Append('\n');
            }

            var variantLine = best.Raw;
            if (audio.Count == 0)
            {
                variantLine = AudioAttribute().Replace(variantLine, string.Empty);
            }

            if (subtitles.Count == 0)
            {
                variantLine = SubtitlesAttribute().Replace(variantLine, string.Empty);
            }

            sb.Append(variantLine).Append('\n');
            sb.Append(best.Uri).Append('\n');

            return sb.ToString();
        }

        /// <summary>The requested tier, or null when the upstream no longer carries it.</summary>
        private static Variant? SelectExact(List<Variant> variants, int height, string codec)
            => variants
                .Where(v => v.Height == height
                    && string.Equals(CodecNames.Normalise(v.VideoCodec), codec, StringComparison.Ordinal))
                .OrderByDescending(v => v.Bandwidth)
                .FirstOrDefault();

        /// <summary>
        /// The default tier: the best AVC variant. When a platform ships none, the best variant at
        /// or below <see cref="DefaultFallbackHeight"/>, so a VP9-only source cannot make 2160p the
        /// default and surprise the viewer with a transcode.
        /// </summary>
        private static Variant? SelectDefault(List<Variant> variants)
        {
            if (variants.Count == 0)
            {
                return null;
            }

            var avc = variants.Where(v => v.IsAvc).ToList();
            if (avc.Count > 0)
            {
                return avc.OrderByDescending(v => v.Bandwidth).First();
            }

            var capped = variants.Where(v => v.Height <= DefaultFallbackHeight).ToList();
            return (capped.Count > 0 ? capped : variants)
                .OrderByDescending(v => v.Bandwidth).First();
        }

        private static string ForceDefault(string line, bool isDefault)
        {
            var wanted = isDefault ? "DEFAULT=YES" : "DEFAULT=NO";
            return DefaultAttribute().IsMatch(line)
                ? DefaultAttribute().Replace(line, wanted)
                : $"{line},{wanted}";
        }

        private static string? NextUri(string[] lines, int index)
        {
            for (var i = index + 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0)
                {
                    continue;
                }

                return lines[i].StartsWith('#') ? null : lines[i];
            }

            return null;
        }

        /// <summary>Reads an HLS attribute (quoted or bare) from a tag line.</summary>
        /// <param name="line">The HLS tag line to parse.</param>
        /// <param name="name">The attribute name to look up.</param>
        /// <returns>The attribute value with quotes stripped, or <see langword="null"/> if not found.</returns>
        internal static string? Attribute(string line, string name)
        {
            var match = Regex.Match(
                line, $"(?:^|[:,]){Regex.Escape(name)}=(\"[^\"]*\"|[^,]*)");

            return match.Success ? match.Groups[1].Value.Trim('"') : null;
        }

        private static long ParseBandwidth(string streamInf)
        {
            var m = BandwidthAttribute().Match(streamInf);
            return m.Success && long.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var v)
                ? v : 0;
        }

        private static int ParseHeight(string streamInf)
        {
            var m = ResolutionAttribute().Match(streamInf);
            return m.Success && int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var v)
                ? v : int.MaxValue;
        }

        private static string Absolutise(string uri, Uri baseUri)
            => Uri.TryCreate(baseUri, uri, out var abs) ? abs.ToString() : uri;

        private static string ReplaceUri(string line, string uri)
        {
            var m = UriAttribute().Match(line);
            return m.Success
                ? line.Replace(m.Value, $"URI=\"{uri}\"", StringComparison.Ordinal)
                : line;
        }

        private static string AbsolutiseUriAttribute(string line, Uri baseUri)
        {
            var m = UriAttribute().Match(line);
            if (!m.Success)
            {
                return line;
            }

            return line.Replace(m.Value,
                $"URI=\"{Absolutise(m.Groups[1].Value, baseUri)}\"",
                StringComparison.Ordinal);
        }

        [GeneratedRegex("BANDWIDTH=(\\d+)")]
        private static partial Regex BandwidthAttribute();

        [GeneratedRegex("RESOLUTION=(\\d+)x(\\d+)")]
        private static partial Regex ResolutionAttribute();

        [GeneratedRegex("URI=\"([^\"]+)\"")]
        private static partial Regex UriAttribute();

        [GeneratedRegex(",?SUBTITLES=\"[^\"]*\"")]
        private static partial Regex SubtitlesAttribute();

        [GeneratedRegex(",?AUDIO=\"[^\"]*\"")]
        private static partial Regex AudioAttribute();

        [GeneratedRegex("DEFAULT=(?:YES|NO)")]
        private static partial Regex DefaultAttribute();

        private sealed record MediaRendition(
            string Type,
            string GroupId,
            string Uri,
            string Language,
            bool IsDefault,
            string Raw)
        {
            /// <summary>Returns whether the rendition's language matches the given prefix.</summary>
            /// <param name="prefix">Language prefix (e.g. "fr", "en").</param>
            public bool MatchesLanguage(string prefix)
                => Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            /// <summary>Gets a value indicating whether this rendition is an audio track.</summary>
            public bool IsAudio
                => string.Equals(Type, "AUDIO", StringComparison.OrdinalIgnoreCase);

            /// <summary>Gets a value indicating whether this rendition is a subtitles track.</summary>
            public bool IsSubtitles
                => string.Equals(Type, "SUBTITLES", StringComparison.OrdinalIgnoreCase);

            /// <summary>YouTube marks the original track in the URI; sites with a single track have no marker.</summary>
            public bool IsOriginal
                => Uri.Contains("acont%3Doriginal", StringComparison.OrdinalIgnoreCase)
                   || Uri.Contains("acont=original", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record Variant(
            long Bandwidth,
            int Height,
            bool IsAvc,
            string? VideoCodec,
            string? AudioGroup,
            string? SubtitleGroup,
            string Raw,
            string Uri);
    }
}

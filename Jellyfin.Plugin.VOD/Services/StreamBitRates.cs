using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Gives a probed video stream the bandwidth its variant declares when the prober could not
    /// measure a bitrate.
    /// </summary>
    /// <remarks>
    /// ffprobe reports no <c>bit_rate</c> for H.264 in MPEG-TS segments — YouTube's AVC variants and
    /// every Twitch VOD — and Jellyfin refuses to copy a video of unknown bitrate whenever the client
    /// caps the video bitrate (<c>EncodingHelper.CanStreamCopyVideo</c>), which the web client
    /// always does. The bandwidth is read from the variant declaration itself rather than from
    /// <see cref="MediaStreamBuilder"/>, which derives nothing from a muxed variant such as
    /// Twitch's. It is a peak that includes the audio, so it overstates the video slightly; a
    /// measured value always wins.
    /// </remarks>
    public static class StreamBitRates
    {
        /// <summary>The BANDWIDTH of the first variant a filtered master declares.</summary>
        /// <param name="filteredMaster">A filtered master playlist.</param>
        /// <returns>The declared bandwidth, or null when there is no variant or no usable value.</returns>
        public static int? VariantBandwidth(string filteredMaster)
        {
            ArgumentNullException.ThrowIfNull(filteredMaster);

            foreach (var line in filteredMaster.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    return int.TryParse(
                            HlsPlaylistFilter.Attribute(line, "BANDWIDTH"),
                            CultureInfo.InvariantCulture,
                            out var bandwidth)
                        && bandwidth > 0
                            ? bandwidth
                            : null;
                }
            }

            return null;
        }

        /// <summary>
        /// Gives every video stream that lacks a bitrate the variant's bandwidth, and reports how many
        /// it filled.
        /// </summary>
        /// <param name="described">The stored or freshly probed streams, mutated in place.</param>
        /// <param name="variantBandwidth">The variant's declared bandwidth, if any.</param>
        /// <returns>The number of bitrates this call filled.</returns>
        public static int FillMissingVideo(IReadOnlyList<MediaStream> described, int? variantBandwidth)
        {
            ArgumentNullException.ThrowIfNull(described);

            if (variantBandwidth is not > 0)
            {
                return 0;
            }

            var filled = 0;
            foreach (var stream in described)
            {
                if (stream.Type == MediaStreamType.Video && stream.BitRate is not > 0)
                {
                    stream.BitRate = variantBandwidth;
                    filled++;
                }
            }

            return filled;
        }
    }
}

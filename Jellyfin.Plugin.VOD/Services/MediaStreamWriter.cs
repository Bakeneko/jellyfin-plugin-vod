using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Labels an item's media streams with the track names its filtered master carries, and gives a
    /// bitrate to those the probe could not measure.
    /// </summary>
    /// <remarks>
    /// ffprobe does read a rendition's NAME, but files it under <c>MediaStream.Comment</c>, which
    /// <c>MediaStream.DisplayTitle</c> never consults — so a probe alone renders Arte's two French
    /// tracks as one indistinguishable "French - AAC - Stereo" pair. The master's NAME goes in
    /// <c>Title</c>, which DisplayTitle leads with. The variant's BANDWIDTH fills a missing bitrate
    /// (<see cref="StreamBitRates"/>): items Jellyfin probed itself lack one for H.264 in MPEG-TS,
    /// and the describer never revisits a described item.
    /// <para>
    /// It contributes nothing else on purpose. Jellyfin probes the source on the same playback this
    /// writer runs in — ffprobe fetching the playlist is what hits the stream route — so the two race
    /// and the last save wins. Replacing the list meant that winning destroyed every field a playlist
    /// cannot carry (Profile, channels, sample rate, pixel format, level, colour), which
    /// <c>StreamBuilder</c> uses to arbitrate direct play against transcoding. Patching only labels
    /// and missing bitrates makes the race harmless in both directions: if the probe lands last they
    /// are restored on the next play, and nothing is ever lost.
    /// </para>
    /// <para>
    /// The writer therefore only ever patches — it never writes the whole list. The initial
    /// description (profile, level, colour, channel count) comes from <c>StreamDescriber</c>, which
    /// probes each tier before it is played.
    /// </para>
    /// </remarks>
    public sealed partial class MediaStreamWriter
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaStreamRepository _repository;
        private readonly ILogger<MediaStreamWriter> _logger;

        /// <summary>Initializes a new instance of the <see cref="MediaStreamWriter"/> class.</summary>
        /// <param name="libraryManager">The library manager used to look up items by VOD provider id.</param>
        /// <param name="repository">The repository used to persist media streams.</param>
        /// <param name="logger">The logger.</param>
        public MediaStreamWriter(
            ILibraryManager libraryManager,
            IMediaStreamRepository repository,
            ILogger<MediaStreamWriter> logger)
        {
            _libraryManager = libraryManager;
            _repository = repository;
            _logger = logger;
        }

        /// <summary>
        /// Labels the streams of every item that serves the given tier, using
        /// <paramref name="streams"/>.
        /// </summary>
        /// <param name="vodUuid">The opaque VOD UUID to look up in the library.</param>
        /// <param name="height">The served tier's height, or null for the primary.</param>
        /// <param name="codec">The served tier's codec, or null for the primary.</param>
        /// <param name="streams">The streams derived from the filtered master.</param>
        /// <param name="variantBandwidth">The bandwidth the filtered master's variant declares, if any.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="false"/> when nothing was saved, including when the item has no
        /// description yet (left to <c>StreamDescriber</c>) or when the labels and bitrates already
        /// matched — this runs on every playback, so an unchanged list is left alone.</returns>
        /// <remarks>
        /// Every member of a version group carries the same VOD provider id, so the query returns
        /// the whole group — and one group per source that publishes this video, since two sources
        /// can publish it in different modes. The tier is then read off each candidate's own path,
        /// which is the only thing that determines what that item actually serves.
        /// </remarks>
        public bool TryWrite(
            string vodUuid,
            int? height,
            string? codec,
            IReadOnlyList<MediaStream> streams,
            int? variantBandwidth,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(vodUuid);
            ArgumentNullException.ThrowIfNull(streams);

            // A muxed variant (Twitch) yields no stream list, yet its bandwidth is still worth writing.
            if (streams.Count == 0 && variantBandwidth is not > 0)
            {
                return false;
            }

            // Without IncludeAlternateVersions this query only ever sees the primary: Jellyfin hides
            // every item carrying a PrimaryVersionId behind the version it belongs to, and the
            // primary's bare path serves no tier, so no alternate could ever be written to.
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                HasAnyProviderId = new Dictionary<string, string> { ["VOD"] = vodUuid },
                IncludeAlternateVersions = true,
                Recursive = true
            });

            if (items.Count == 0)
            {
                LogNoItem(vodUuid);
                return false;
            }

            // Every match, not the first: two sources can publish the same video in different modes,
            // so a Movie in one library and an Episode in another share this VOD id and this path.
            var matched = false;
            var wrote = false;
            foreach (var candidate in items)
            {
                var (candidateHeight, candidateCodec) = VersionGroup.TierOf(candidate.Path);
                if (candidateHeight == height
                    && string.Equals(candidateCodec, codec, StringComparison.Ordinal))
                {
                    matched = true;
                    wrote |= WriteTo(candidate, streams, variantBandwidth, cancellationToken);
                }
            }

            if (!matched)
            {
                LogNoTier(vodUuid, height, codec);
            }

            return wrote;
        }

        /// <summary>Labels one item's probed streams and fills a missing video bitrate.</summary>
        /// <param name="item">An item that serves the requested tier.</param>
        /// <param name="streams">The streams derived from the filtered master.</param>
        /// <param name="variantBandwidth">The bandwidth the filtered master's variant declares, if any.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="true"/> when something was saved.</returns>
        private bool WriteTo(
            BaseItem item,
            IReadOnlyList<MediaStream> streams,
            int? variantBandwidth,
            CancellationToken cancellationToken)
        {
            var described = _repository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
            if (described.Count == 0)
            {
                // Nothing to label yet, and nothing worth writing: a playlist-derived list carries
                // no profile, level, colour or channel count, and Jellyfin would never re-probe a
                // source that has a video stream, so a thin list would be permanent.
                // StreamDescriber writes the real description instead.
                LogNotDescribedYet(item.Name);
                return false;
            }

            var titled = TrackLabels.Apply(described, streams);
            var filled = StreamBitRates.FillMissingVideo(described, variantBandwidth);
            if (titled == 0 && filled == 0)
            {
                return false;
            }

            _repository.SaveMediaStreams(item.Id, described, cancellationToken);
            LogTitled(item.Name, titled, filled);
            return true;
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "{ItemName} has no description yet; leaving it to the stream describer")]
        private partial void LogNotDescribedYet(string? itemName);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "No library item carries VOD id {VodUuid} yet; skipping media stream write")]
        private partial void LogNoItem(string vodUuid);

        [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
            Message = "Labelled {TitledCount} and gave a bitrate to {FilledCount} probed media stream(s) for {ItemName}")]
        private partial void LogTitled(string? itemName, int titledCount, int filledCount);

        [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
            Message = "No item of VOD id {VodUuid} has a stored path serving height {Height} codec {Codec}; skipping media stream write")]
        private partial void LogNoTier(string vodUuid, int? height, string? codec);
    }
}

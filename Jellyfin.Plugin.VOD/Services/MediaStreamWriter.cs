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
    /// Labels an item's media streams with the track names its filtered master carries.
    /// </summary>
    /// <remarks>
    /// ffprobe does read a rendition's NAME, but files it under <c>MediaStream.Comment</c>, which
    /// <c>MediaStream.DisplayTitle</c> never consults — so a probe alone renders Arte's two French
    /// tracks as one indistinguishable "French - AAC - Stereo" pair. The master's NAME is therefore
    /// the only thing this writer contributes, and it goes in <c>Title</c>, which DisplayTitle leads
    /// with.
    /// <para>
    /// It contributes nothing else on purpose. Jellyfin probes the source on the same playback this
    /// writer runs in — ffprobe fetching the playlist is what hits the stream route — so the two race
    /// and the last save wins. Replacing the list meant that winning destroyed every field a playlist
    /// cannot carry (Profile, channels, sample rate, pixel format, level, colour), which
    /// <c>StreamBuilder</c> uses to arbitrate direct play against transcoding. Patching only the
    /// labels makes the race harmless in both directions: if the probe lands last the labels are
    /// restored on the next play, and nothing is ever lost.
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
        /// Labels the streams of the item that serves the given tier, using
        /// <paramref name="streams"/>, or stores them whole when that item has none yet.
        /// </summary>
        /// <param name="vodUuid">The opaque VOD UUID to look up in the library.</param>
        /// <param name="height">The served tier's height, or null for the primary.</param>
        /// <param name="codec">The served tier's codec, or null for the primary.</param>
        /// <param name="streams">The streams derived from the filtered master.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="false"/> when nothing was saved, including when the labels already
        /// matched — this runs on every playback, so an unchanged list is left alone.</returns>
        /// <remarks>
        /// Every member of a version group carries the same VOD provider id, so the query returns
        /// the whole group — at most three items. The tier is then read off each candidate's own
        /// path, which is the only thing that determines what that item actually serves.
        /// </remarks>
        public bool TryWrite(
            string vodUuid,
            int? height,
            string? codec,
            IReadOnlyList<MediaStream> streams,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(vodUuid);
            ArgumentNullException.ThrowIfNull(streams);

            if (streams.Count == 0)
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

            BaseItem? match = null;
            foreach (var candidate in items)
            {
                var (candidateHeight, candidateCodec) = VersionGroup.TierOf(candidate.Path);
                if (candidateHeight == height
                    && string.Equals(candidateCodec, codec, StringComparison.Ordinal))
                {
                    match = candidate;
                    break;
                }
            }

            if (match is null)
            {
                LogNoTier(vodUuid, height, codec);
                return false;
            }

            var probed = _repository.GetMediaStreams(new MediaStreamQuery { ItemId = match.Id });
            if (probed.Count == 0)
            {
                _repository.SaveMediaStreams(match.Id, streams, cancellationToken);
                LogWrote(match.Name, streams.Count);
                return true;
            }

            var titled = ApplyTitles(probed, streams);
            if (titled == 0)
            {
                return false;
            }

            _repository.SaveMediaStreams(match.Id, probed, cancellationToken);
            LogTitled(match.Name, titled);
            return true;
        }

        /// <summary>
        /// Copies the master's track labels onto the probed streams, and reports how many changed.
        /// </summary>
        /// <param name="probed">The stored streams, mutated in place.</param>
        /// <param name="fromMaster">The streams derived from the filtered master.</param>
        /// <returns>The number of labels this call changed.</returns>
        /// <remarks>
        /// Paired by type and by position within that type. Both lists describe the same master in the
        /// same declaration order, and two audio renditions of one language — Arte ships "français"
        /// beside "français (confort audio)" — cannot be told apart by language or codec. A type the
        /// probe reports more of than the master does is simply left unlabelled.
        /// </remarks>
        private static int ApplyTitles(
            IReadOnlyList<MediaStream> probed,
            IReadOnlyList<MediaStream> fromMaster)
        {
            var labels = new Dictionary<MediaStreamType, List<string?>>();
            foreach (var stream in fromMaster)
            {
                if (!labels.TryGetValue(stream.Type, out var forType))
                {
                    forType = [];
                    labels[stream.Type] = forType;
                }

                forType.Add(stream.Title);
            }

            var titled = 0;
            var seen = new Dictionary<MediaStreamType, int>();
            foreach (var stream in probed)
            {
                var position = seen.GetValueOrDefault(stream.Type);
                seen[stream.Type] = position + 1;

                if (!labels.TryGetValue(stream.Type, out var forType) || position >= forType.Count)
                {
                    continue;
                }

                var label = forType[position];
                if (string.IsNullOrEmpty(label)
                    || IsPlaceholder(label)
                    || string.Equals(stream.Title, label, StringComparison.Ordinal))
                {
                    continue;
                }

                stream.Title = label;
                titled++;
            }

            return titled;
        }

        /// <summary>
        /// Whether a master's track name says nothing <c>MediaStream.DisplayTitle</c> would not say on
        /// its own.
        /// </summary>
        /// <param name="label">The NAME attribute read from the master.</param>
        /// <returns><see langword="true"/> when the label should not be stored.</returns>
        /// <remarks>
        /// YouTube names a lone audio rendition "Default", with no LANGUAGE attribute. Since
        /// DisplayTitle leads with Title when it is set, storing that costs information rather than
        /// adding any: an honest "French - AAC - Stereo - Par défaut" becomes a bare "Default".
        /// "Default" is one of the tokens DisplayTitle already emits by itself, which is the test for a
        /// placeholder — not that the track happens to be the only one, since a lone track may still be
        /// worth naming ("Audiodescription", "Version originale").
        /// </remarks>
        private static bool IsPlaceholder(string label)
            => string.Equals(label.Trim(), "Default", StringComparison.OrdinalIgnoreCase);

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Wrote {StreamCount} media streams for {ItemName}")]
        private partial void LogWrote(string? itemName, int streamCount);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "No library item carries VOD id {VodUuid} yet; skipping media stream write")]
        private partial void LogNoItem(string vodUuid);

        [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
            Message = "Labelled {TitledCount} probed media stream(s) for {ItemName}")]
        private partial void LogTitled(string? itemName, int titledCount);

        [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
            Message = "No item of VOD id {VodUuid} has a stored path serving height {Height} codec {Codec}; skipping media stream write")]
        private partial void LogNoTier(string vodUuid, int? height, string? codec);
    }
}

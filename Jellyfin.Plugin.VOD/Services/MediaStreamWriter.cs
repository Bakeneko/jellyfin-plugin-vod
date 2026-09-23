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
    /// Stores the media streams described by the filtered master, so Jellyfin does not have to
    /// rediscover them by probing.
    /// </summary>
    /// <remarks>
    /// Probing an HLS source loses the track labels the master carries. A FullRefresh re-runs
    /// ProbeProvider and overwrites what is stored here — hence writing at playback, which
    /// repairs itself on the next play.
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
        /// Stores <paramref name="streams"/> for the item carrying <paramref name="vodUuid"/>.
        /// Returns <see langword="false"/> when no item carries it.
        /// </summary>
        /// <param name="vodUuid">The opaque VOD UUID to look up in the library.</param>
        /// <param name="streams">The media streams to persist.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="true"/> when streams were written; <see langword="false"/> when no matching item was found.</returns>
        public bool TryWrite(
            string vodUuid, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(vodUuid);
            ArgumentNullException.ThrowIfNull(streams);

            if (streams.Count == 0)
                return false;

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                HasAnyProviderId = new Dictionary<string, string> { ["VOD"] = vodUuid },
                Recursive = true,
                Limit = 1
            });

            if (items.Count == 0)
            {
                LogNoItem(vodUuid);
                return false;
            }

            _repository.SaveMediaStreams(items[0].Id, streams, cancellationToken);
            LogWrote(items[0].Name, streams.Count);
            return true;
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Wrote {StreamCount} media streams for {ItemName}")]
        private partial void LogWrote(string? itemName, int streamCount);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "No library item carries VOD id {VodUuid} yet; skipping media stream write")]
        private partial void LogNoItem(string vodUuid);
    }
}

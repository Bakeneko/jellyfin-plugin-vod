using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// The outcome of a <see cref="StreamDescriber.DescribeGroupAsync"/> call.
    /// </summary>
    /// <param name="Attempted">
    /// The members this call tried to describe: those that had no description and were not already
    /// in flight. Zero means every member was either already described or being described
    /// concurrently; that is not a failure.
    /// </param>
    /// <param name="Described">
    /// The members whose streams were actually saved, whether by probe or by copy from the primary.
    /// </param>
    public readonly record struct DescribeOutcome(int Attempted, int Described);

    /// <summary>
    /// Describes the media streams of a version group's members before anyone plays them.
    /// </summary>
    /// <remarks>
    /// Jellyfin arbitrates direct play against transcoding from the streams stored for the source it
    /// is asked to play, and it only probes when <c>mediaSources[0]</c> of the requested item has no
    /// video stream (<c>MediaSourceManager.cs:185-191</c>). A playback started from the primary item
    /// with a tier's source therefore gets no probe at all: the decision is made blind, and 4K VP9
    /// comes out re-encoded in software. Describing a tier before it is ever played is what removes
    /// that case.
    /// <para>
    /// The description comes from Jellyfin's own prober, called on our own route
    /// (<see cref="IMediaEncoder.GetMediaInfo"/>, the very call <c>FFProbeVideoInfo</c> makes), so
    /// what we store is what Jellyfin would have stored — profile, level, colour, channels — and the
    /// race with its probe becomes harmless. Our route's cache is primed first, so the probe is
    /// served the filtered master from memory and yt-dlp is not run again.
    /// </para>
    /// </remarks>
    public sealed partial class StreamDescriber : IStreamDescriber, IDisposable
    {
        private readonly ILibraryManager _library;
        private readonly IMediaStreamRepository _repository;
        private readonly IMediaEncoder _encoder;
        private readonly PlaylistCache _cache;
        private readonly SubtitleTokens _subtitles;
        private readonly ILogger<StreamDescriber> _logger;

        /// <summary>
        /// One heavy probe at a time, plugin-wide: ffprobe reads up to 25 MB per call
        /// (<c>-probesize 25000000</c>, measured 2026-10-08), and two at once would double that
        /// burst for no gain.
        /// </summary>
        private readonly SemaphoreSlim _probeGate = new(1, 1);

        /// <summary>
        /// The members being described right now. The task and the playback failsafe can reach the
        /// same group at the same moment; this is what keeps one of them from probing a second time.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

        /// <summary>Initializes a new instance of the <see cref="StreamDescriber"/> class.</summary>
        /// <param name="library">Used to list a group's members by VOD provider id.</param>
        /// <param name="repository">Used to read and store media streams.</param>
        /// <param name="encoder">Jellyfin's media prober.</param>
        /// <param name="cache">The playlist cache, primed before each probe.</param>
        /// <param name="subtitles">Rewrites upstream subtitle URIs into routes of ours.</param>
        /// <param name="logger">The logger.</param>
        public StreamDescriber(
            ILibraryManager library,
            IMediaStreamRepository repository,
            IMediaEncoder encoder,
            PlaylistCache cache,
            SubtitleTokens subtitles,
            ILogger<StreamDescriber> logger)
        {
            _library = library;
            _repository = repository;
            _encoder = encoder;
            _cache = cache;
            _subtitles = subtitles;
            _logger = logger;
        }

        /// <summary>
        /// Describes every member of one group that has no description yet, from a master already
        /// in hand.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="vodUuid">The video's opaque VOD uuid.</param>
        /// <param name="upstreamMaster">The complete upstream master playlist.</param>
        /// <param name="masterBaseUri">The URI the master was fetched from, for absolutising.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// A <see cref="DescribeOutcome"/> reporting how many members were attempted and how many
        /// were saved. An outcome with <see cref="DescribeOutcome.Attempted"/> equal to zero means
        /// every member was already described or being described concurrently — not a failure.
        /// </returns>
        /// <remarks>
        /// Members are walked primary first, then tallest tier, then the rest: the primary's
        /// description is what the reference tier may copy instead of paying for a second probe.
        /// </remarks>
        public async Task<DescribeOutcome> DescribeGroupAsync(
            PluginConfiguration config,
            string vodUuid,
            string upstreamMaster,
            Uri masterBaseUri,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(vodUuid);
            ArgumentException.ThrowIfNullOrEmpty(upstreamMaster);
            ArgumentNullException.ThrowIfNull(masterBaseUri);

            var audioLangs = StreamCacheKey.JoinLanguages(config.PreferredAudioLanguages);
            var subLangs = StreamCacheKey.JoinLanguages(config.PreferredSubtitleLanguages);

            var attempted = 0;
            var described = 0;

            // The primary leads, then the tiers tallest first. The order is load-bearing: the
            // reference tier may copy the primary's description instead of paying for a second
            // probe, which only works if the primary has been described by the time we reach it.
            foreach (var member in Members(vodUuid)
                .OrderByDescending(m => m.Tier is null ? int.MaxValue : m.Height!.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (HasVideoStream(member.Item.Id) || !_inFlight.TryAdd(member.Item.Id, 0))
                {
                    continue;
                }

                attempted++;
                try
                {
                    if (await DescribeMemberAsync(
                            config, vodUuid, member, upstreamMaster, masterBaseUri,
                            audioLangs, subLangs, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        described++;
                    }
                }
                finally
                {
                    _inFlight.TryRemove(member.Item.Id, out _);
                }
            }

            return new DescribeOutcome(attempted, described);
        }

        /// <summary>
        /// Describes every member a video will have once published — the primary and each offered
        /// tier — from its stored ladder, without the library.
        /// </summary>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="video">The video being resolved.</param>
        /// <param name="upstreamMaster">The complete upstream master playlist.</param>
        /// <param name="masterBaseUri">The URI the master was fetched from, for absolutising.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Each member's prepared streams keyed by <see cref="DescribedStreams.KeyFor"/>, or null when
        /// a probe failed or came back empty — the resolver then marks the video failed (spec E1).
        /// A tier the master no longer carries is skipped, not a failure.
        /// </returns>
        /// <remarks>
        /// Runs before publication, because publication now waits for the whole source to settle
        /// while the manifest URL only lives for the wave. Our stream route reads only the plugin
        /// database, so the probe works before any library item exists; the members come from the
        /// same ladder the publisher builds them from (<see cref="QualityLadder.OfferedTiers"/>).
        /// </remarks>
        public async Task<IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>?> DescribeVideoAsync(
            PluginConfiguration config,
            VideoRecord video,
            string upstreamMaster,
            Uri masterBaseUri,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(video);
            ArgumentException.ThrowIfNullOrEmpty(upstreamMaster);
            ArgumentNullException.ThrowIfNull(masterBaseUri);

            var audioLangs = StreamCacheKey.JoinLanguages(config.PreferredAudioLanguages);
            var subLangs = StreamCacheKey.JoinLanguages(config.PreferredSubtitleLanguages);

            var tiers = new List<Tier?> { null };
            foreach (var offered in QualityLadder.OfferedTiers(video.Extras.Qualities))
            {
                // Fps is null on purpose: it only ever reaches a tier's display label, never its URL.
                tiers.Add(new Tier(offered.Height, offered.Codec, null));
            }

            var described = new Dictionary<string, IReadOnlyList<MediaStream>>(StringComparer.Ordinal);
            IReadOnlyList<MediaStream>? primaryProbed = null;
            string? primaryVariant = null;

            // The primary leads, then the tiers tallest first: a tier serving the primary's variant
            // copies its description, which only works once the primary has been probed.
            foreach (var tier in tiers.OrderByDescending(t => t is null ? int.MaxValue : t.Height))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filtered = Filter(upstreamMaster, masterBaseUri, tier?.Height, tier?.Codec, audioLangs, subLangs);
                if (filtered is null)
                {
                    LogTierGone(video.Id, tier?.Height, tier?.Codec);
                    continue;
                }

                // Primed under the very key the route reads, so the probe is served this playlist
                // from memory and yt-dlp is not run a second time.
                _cache.Set(
                    StreamCacheKey.For(
                        video.Id, StreamCacheKey.DefaultFormat, tier?.Height, tier?.Codec,
                        audioLangs, subLangs),
                    filtered);

                var key = DescribedStreams.KeyFor(tier?.Height, tier?.Codec);

                if (tier is not null
                    && primaryProbed is not null
                    && SelectedVariant(filtered) is { } mine
                    && string.Equals(mine, primaryVariant, StringComparison.Ordinal))
                {
                    // Cloned: Prepare relabels in place, and the primary's streams must not change.
                    described[key] = Prepare(DescribedStreams.Clone(primaryProbed), filtered);
                    continue;
                }

                var probed = await ProbeAsync(config, video.Id, tier, cancellationToken).ConfigureAwait(false);
                if (probed is null)
                {
                    return null;
                }

                if (tier is null)
                {
                    primaryProbed = DescribedStreams.Clone(probed);
                    primaryVariant = SelectedVariant(filtered);
                }

                described[key] = Prepare(probed, filtered);
            }

            return described;
        }

        /// <summary>
        /// Writes a description this pass produced onto the video's library items.
        /// </summary>
        /// <param name="vodUuid">The video's uuid.</param>
        /// <param name="byMember">
        /// The streams per member, keyed by <see cref="DescribedStreams.KeyFor"/>, as
        /// <see cref="DescribeVideoAsync"/> returned them earlier in the same pass.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>How many members were written.</returns>
        /// <remarks>
        /// Separate from describing because the items may not exist yet when the description is
        /// made: the wave describes, and the checkup a few steps later is what creates the items.
        /// The description is not persisted in between — the manifest it came from would be stale
        /// by the next pass anyway — so an item created in a later pass is made eligible for
        /// describing again instead (see <c>IVodDatabase.ClearDescribedAt</c>).
        /// <para>
        /// A member that already has a video stream is left alone: Jellyfin or the playback
        /// failsafe described it, and that description is at least as fresh as ours.
        /// </para>
        /// <para>
        /// Never throws, except on cancellation: a failure is logged and the members it did not
        /// reach are left to the playback failsafe.
        /// </para>
        /// </remarks>
        public int ApplyDescribed(
            string vodUuid,
            IReadOnlyDictionary<string, IReadOnlyList<MediaStream>> byMember,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(vodUuid);
            ArgumentNullException.ThrowIfNull(byMember);

            if (byMember.Count == 0)
            {
                return 0;
            }

            var applied = 0;
            try
            {
                foreach (var member in Members(vodUuid))
                {
                    if (HasVideoStream(member.Item.Id))
                    {
                        continue;
                    }

                    if (!byMember.TryGetValue(DescribedStreams.KeyFor(member.Height, member.Codec), out var streams)
                        || streams.Count == 0)
                    {
                        continue;
                    }

                    _repository.SaveMediaStreams(member.Item.Id, streams, cancellationToken);
                    LogDescribed(member.Item.Name, streams.Count);
                    applied++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // a description is an optimisation: the playback failsafe covers it
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Called right after a reconcile that succeeded: throwing here would abort that
                // publication, leave its anchor unrecorded and have the wave retried for nothing.
                LogApplyDescribedFailed(ex, vodUuid);
            }

            return applied;
        }

        private async Task<bool> DescribeMemberAsync(
            PluginConfiguration config,
            string vodUuid,
            GroupMember member,
            string upstreamMaster,
            Uri masterBaseUri,
            string audioLangs,
            string subLangs,
            CancellationToken cancellationToken)
        {
            var filtered = Filter(upstreamMaster, masterBaseUri, member, audioLangs, subLangs);
            if (filtered is null)
            {
                // The tier this member serves is no longer offered upstream. Nothing to describe,
                // and nothing to repair here: the next sync rebuilds the ladder.
                LogTierGone(vodUuid, member.Height, member.Codec);
                return false;
            }

            // Primed before the probe, under the very key the route reads: the probe then fetches
            // our route and is served this playlist from memory, so yt-dlp is not run a second time
            // and the route's own writer stays out of the way.
            _cache.Set(
                StreamCacheKey.For(
                    vodUuid, StreamCacheKey.DefaultFormat, member.Height, member.Codec,
                    audioLangs, subLangs),
                filtered);

            if (member.Tier is not null
                && TryCopyFromPrimary(
                    vodUuid, member, filtered, upstreamMaster, masterBaseUri,
                    audioLangs, subLangs, cancellationToken))
            {
                return true;
            }

            var streams = await ProbeAsync(config, vodUuid, member.Tier, cancellationToken)
                .ConfigureAwait(false);
            if (streams is null)
            {
                return false;
            }

            Save(member.Item, streams, filtered, cancellationToken);
            return true;
        }

        /// <summary>Probes one member through our own route, one heavy probe at a time.</summary>
        /// <returns>The streams, or null when the probe failed or found none.</returns>
        private async Task<IReadOnlyList<MediaStream>?> ProbeAsync(
            PluginConfiguration config, string vodUuid, Tier? tier, CancellationToken cancellationToken)
        {
            var url = VersionGroup.PathFor(config.LocalBaseUrl, vodUuid, tier);

            MediaInfo? info;
            await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                info = await _encoder.GetMediaInfo(
                        new MediaInfoRequest
                        {
                            ExtractChapters = false,
                            MediaType = DlnaProfileType.Video,
                            MediaSource = new MediaSourceInfo
                            {
                                Path = url,
                                Protocol = MediaProtocol.Http
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // a failed probe is reported to the caller, never propagated
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogProbeFailed(ex, url);
                return null;
            }
            finally
            {
                _probeGate.Release();
            }

            var streams = info?.MediaStreams;
            if (streams is null || streams.Count == 0)
            {
                // Storing an empty list would mark this member described for ever, and nothing would
                // ever probe it again. Leaving it empty is what makes the next pass retry.
                LogProbeEmpty(url);
                return null;
            }

            return streams;
        }

        /// <summary>A probed description, re-indexed and carrying our track labels.</summary>
        private static List<MediaStream> Prepare(IReadOnlyList<MediaStream> described, string filteredMaster)
        {
            var streams = new List<MediaStream>(described);

            // What FFProbeVideoInfo.Fetch does before saving (:235-238), and the only part of it
            // that applies to us: we have no external tracks and write no item fields.
            for (var i = 0; i < streams.Count; i++)
            {
                streams[i].Index = i;
            }

            TrackLabels.Apply(streams, MediaStreamBuilder.Build(filteredMaster));
            StreamBitRates.FillMissingVideo(streams, StreamBitRates.VariantBandwidth(filteredMaster));
            return streams;
        }

        /// <summary>Stores a probed description, re-indexed and carrying our track labels.</summary>
        private void Save(
            BaseItem item,
            IReadOnlyList<MediaStream> described,
            string filteredMaster,
            CancellationToken cancellationToken)
        {
            _repository.SaveMediaStreams(item.Id, Prepare(described, filteredMaster), cancellationToken);
            LogDescribed(item.Name, described.Count);
        }

        /// <summary>The variant a filtered master retained, as its declaration plus its URI.</summary>
        /// <param name="filteredMaster">A filtered master playlist.</param>
        /// <returns>The two lines identifying the variant, or null when it carries none.</returns>
        /// <remarks>
        /// Two members select the same variant when these two lines match: the declaration carries
        /// BANDWIDTH, RESOLUTION and CODECS, and the URI is the absolutised upstream one, identical
        /// for the same rendition. Comparing whole playlists would not do — each filtering mints
        /// fresh subtitle tokens.
        /// </remarks>
        internal static string? SelectedVariant(string filteredMaster)
        {
            ArgumentNullException.ThrowIfNull(filteredMaster);

            var lines = filteredMaster.Split('\n', StringSplitOptions.TrimEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    return i + 1 < lines.Length ? string.Concat(lines[i], "\n", lines[i + 1]) : lines[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Copies the primary's description onto a tier that serves the very same variant.
        /// </summary>
        /// <returns><see langword="true"/> when the description was copied.</returns>
        /// <remarks>
        /// The primary and the reference tier serve one stream between them, so a second probe would
        /// read 25 MB to learn what we already know. The equality is not assumed: it is checked by
        /// applying both filters to the same master and comparing the variant each retained, because
        /// the ladder elects its reference tier by height as a stand-in for bandwidth
        /// (<c>QualityLadder.ReferenceTier</c> remarks) and the two orders can disagree.
        /// <para>
        /// The streams are re-read from the repository rather than reused from the primary's own
        /// list, so relabelling them cannot reach into the primary's description.
        /// </para>
        /// </remarks>
        private bool TryCopyFromPrimary(
            string vodUuid,
            GroupMember member,
            string filteredMember,
            string upstreamMaster,
            Uri masterBaseUri,
            string audioLangs,
            string subLangs,
            CancellationToken cancellationToken)
        {
            var primary = Members(vodUuid).FirstOrDefault(m => m.Tier is null);
            if (primary is null || !HasVideoStream(primary.Item.Id))
            {
                return false;
            }

            var filteredPrimary = Filter(upstreamMaster, masterBaseUri, primary, audioLangs, subLangs);
            if (filteredPrimary is null)
            {
                return false;
            }

            var mine = SelectedVariant(filteredMember);
            if (mine is null || !string.Equals(mine, SelectedVariant(filteredPrimary), StringComparison.Ordinal))
            {
                return false;
            }

            var copy = _repository.GetMediaStreams(new MediaStreamQuery { ItemId = primary.Item.Id });
            if (copy.Count == 0)
            {
                return false;
            }

            Save(member.Item, copy, filteredMember, cancellationToken);
            LogCopiedFromPrimary(member.Item.Name, primary.Item.Name);
            return true;
        }

        private string? Filter(
            string upstreamMaster,
            Uri masterBaseUri,
            GroupMember member,
            string audioLangs,
            string subLangs)
            => Filter(upstreamMaster, masterBaseUri, member.Height, member.Codec, audioLangs, subLangs);

        private string? Filter(
            string upstreamMaster,
            Uri masterBaseUri,
            int? height,
            string? codec,
            string audioLangs,
            string subLangs)
            => HlsPlaylistFilter.FilterMaster(
                upstreamMaster,
                masterBaseUri,
                height,
                codec,
                audioLangs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                subLangs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _subtitles.Rewrite);

        private bool HasVideoStream(Guid itemId)
        {
            foreach (var stream in _repository.GetMediaStreams(new MediaStreamQuery { ItemId = itemId }))
            {
                if (stream.Type == MediaStreamType.Video)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The group's members and what each one serves, read off its own stored path.
        /// </summary>
        /// <remarks>
        /// Every member of a version group carries the same VOD provider id, and
        /// <c>IncludeAlternateVersions</c> is required or the query only ever returns the primary.
        /// The tier comes from the path's query string, never from the host, which
        /// <c>LocalBaseUrl</c> may have changed under it.
        /// </remarks>
        private List<GroupMember> Members(string vodUuid)
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                HasAnyProviderId = new Dictionary<string, string> { ["VOD"] = vodUuid },
                IncludeAlternateVersions = true,
                Recursive = true
            });

            var members = new List<GroupMember>(items.Count);
            foreach (var item in items)
            {
                var (height, codec) = VersionGroup.TierOf(item.Path);
                members.Add(new GroupMember(item, height, codec));
            }

            return members;
        }

        /// <inheritdoc/>
        public void Dispose() => _probeGate.Dispose();

        /// <summary>One member of a version group, and the tier its path serves.</summary>
        /// <param name="Item">The library item.</param>
        /// <param name="Height">The served height, or null for the primary.</param>
        /// <param name="Codec">The served codec, or null for the primary.</param>
        private sealed record GroupMember(BaseItem Item, int? Height, string? Codec)
        {
            /// <summary>The tier to build a route URL from, or null for the primary.</summary>
            /// <remarks>
            /// Fps is null on purpose: it only ever reaches a tier's display label, never its URL.
            /// </remarks>
            internal Tier? Tier => Height.HasValue && Codec is not null
                ? new Tier(Height.Value, Codec, null)
                : null;
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Described {StreamCount} media stream(s) for {ItemName}")]
        private partial void LogDescribed(string? itemName, int streamCount);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "Upstream no longer offers height {Height} codec {Codec} for {VodUuid}; not describing it")]
        private partial void LogTierGone(string vodUuid, int? height, string? codec);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Probing {Url} failed; leaving it undescribed for a later pass")]
        private partial void LogProbeFailed(Exception exception, string url);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
            Message = "Probing {Url} returned no stream; leaving it undescribed for a later pass")]
        private partial void LogProbeEmpty(string url);

        [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
            Message = "Writing the stored description of {VodUuid} failed; the playback failsafe will describe it")]
        private partial void LogApplyDescribedFailed(Exception exception, string vodUuid);

        [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
            Message = "Copied the description of {PrimaryName} onto {ItemName}: both serve the same variant")]
        private partial void LogCopiedFromPrimary(string? itemName, string? primaryName);
    }
}

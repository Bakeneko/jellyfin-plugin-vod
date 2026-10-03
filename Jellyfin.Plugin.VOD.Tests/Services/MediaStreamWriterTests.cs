using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class MediaStreamWriterTests
    {
        private const string Uuid = "3f9a2b1c-0000-0000-0000-000000000001";

        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly IMediaStreamRepository _repository = Substitute.For<IMediaStreamRepository>();
        private readonly MediaStreamWriter _writer;

        public MediaStreamWriterTests()
        {
            _writer = new MediaStreamWriter(
                _library, _repository, NullLogger<MediaStreamWriter>.Instance);
        }

        private static readonly IReadOnlyList<MediaStream> Streams =
        [
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }
        ];

        private static Movie Item(Guid id, string path)
        {
            var movie = new Movie { Id = id, Path = path };
            movie.SetProviderId("VOD", Uuid);
            return movie;
        }

        private void LibraryHolds(params BaseItem[] items)
            => _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>(items));

        private void AlreadyProbed(params MediaStream[] streams)
            => _repository.GetMediaStreams(Arg.Any<MediaStreamQuery>())
                .Returns(new List<MediaStream>(streams));

        private IReadOnlyList<MediaStream>? Saved()
        {
            IReadOnlyList<MediaStream>? saved = null;
            foreach (var call in _repository.ReceivedCalls())
            {
                if (call.GetMethodInfo().Name == nameof(IMediaStreamRepository.SaveMediaStreams))
                {
                    saved = (IReadOnlyList<MediaStream>)call.GetArguments()[1]!;
                }
            }

            return saved;
        }

        // ffprobe's view of the same master: the technical fields the plugin cannot derive from a
        // playlist, and no track labels — it files the master's NAME under Comment, which Jellyfin's
        // DisplayTitle never consults.
        private static MediaStream Probed(MediaStreamType type, int index, string? language = null)
            => new()
            {
                Type = type,
                Index = index,
                Codec = type == MediaStreamType.Video ? "h264" : "aac",
                Language = language,
                Profile = type == MediaStreamType.Video ? "High" : "LC",
                ChannelLayout = type == MediaStreamType.Audio ? "stereo" : null,
                SampleRate = type == MediaStreamType.Audio ? 44100 : null
            };

        [Fact]
        public void TryWrite_OnlyPatchesTitlesOntoProbedStreams_LeavingTheTechnicalFieldsAlone()
        {
            // The probe and this writer race on the same playback, and whichever saves last wins.
            // Replacing the list meant that when this one won it destroyed everything ffprobe knew and
            // the plugin cannot derive from a playlist — Profile, channels, sample rate, pixel format,
            // level, colour — which Jellyfin's StreamBuilder uses to arbitrate direct play against
            // transcoding. The master's NAME is the one thing we hold that ffprobe does not surface,
            // so that is all we contribute.
            var item = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(item);
            AlreadyProbed(
                Probed(MediaStreamType.Audio, 0, "fr"),
                Probed(MediaStreamType.Audio, 1, "fr"),
                Probed(MediaStreamType.Video, 2));

            var fromMaster = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Index = 0, Title = "français" },
                new() { Type = MediaStreamType.Audio, Index = 1, Title = "français (confort audio)" },
                new() { Type = MediaStreamType.Video, Index = 2 }
            };

            Assert.True(_writer.TryWrite(Uuid, null, null, fromMaster, CancellationToken.None));

            var saved = Saved();
            Assert.NotNull(saved);
            Assert.Equal(3, saved.Count);
            // Paired by type and by order within the type: both lists describe the same master, and
            // two audio renditions of the same language cannot be told apart any other way.
            Assert.Equal("français", saved[0].Title);
            Assert.Equal("français (confort audio)", saved[1].Title);
            // Everything the probe contributed survives.
            Assert.Equal("stereo", saved[0].ChannelLayout);
            Assert.Equal(44100, saved[0].SampleRate);
            Assert.Equal("High", saved[2].Profile);
            Assert.Equal(MediaStreamType.Video, saved[2].Type);
        }

        [Fact]
        public void TryWrite_WritesTheWholeListWhenNothingHasBeenProbedYet()
        {
            // Our list is poorer than a probe's, but it is better than no streams at all, and this is
            // the state a first playback starts from.
            var item = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(item);
            AlreadyProbed();

            Assert.True(_writer.TryWrite(Uuid, null, null, Streams, CancellationToken.None));

            Assert.Same(Streams, Saved());
        }

        [Fact]
        public void TryWrite_IgnoresAPlaceholderLabel_ThatDisplayTitleAlreadyEmits()
        {
            // YouTube names a lone audio rendition NAME="Default", with no LANGUAGE at all. Copying it
            // into Title costs information rather than adding any: DisplayTitle leads with Title, so an
            // honest "French - AAC - Stereo - Par défaut" became a bare "Default". "Default" is already
            // one of the tokens DisplayTitle emits by itself, which is what makes it a placeholder.
            var item = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(item);
            AlreadyProbed(
                Probed(MediaStreamType.Audio, 0, "fr"),
                Probed(MediaStreamType.Video, 1));

            var fromMaster = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Index = 0, Title = "Default" },
                new() { Type = MediaStreamType.Video, Index = 1 }
            };

            Assert.False(_writer.TryWrite(Uuid, null, null, fromMaster, CancellationToken.None));

            _repository.DidNotReceive().SaveMediaStreams(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_WritesEveryItemServingTheTier_NotJustTheFirstFound()
        {
            // Two sources can publish the same video in different modes — a Movie in one library, an
            // Episode in another — so two items carry the same VOD id and the same tier path. Stopping
            // at the first match left the other without streams, and which one won depended on the
            // order the database returned them in.
            var path = $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264";
            var asMovie = Item(Guid.NewGuid(), path);
            var asEpisode = Item(Guid.NewGuid(), path);
            LibraryHolds(asMovie, asEpisode);
            AlreadyProbed();

            Assert.True(_writer.TryWrite(Uuid, 1080, "h264", Streams, CancellationToken.None));

            _repository.Received(1).SaveMediaStreams(
                asMovie.Id, Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
            _repository.Received(1).SaveMediaStreams(
                asEpisode.Id, Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_SavesNothing_WhenTheProbedTitlesAlreadyMatch()
        {
            // This runs on every playback. Re-saving an unchanged list would churn the item and
            // trigger a metadata refresh for nothing.
            var item = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(item);
            var probed = Probed(MediaStreamType.Audio, 0, "fr");
            probed.Title = "français";
            AlreadyProbed(probed, Probed(MediaStreamType.Video, 1));

            var fromMaster = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Index = 0, Title = "français" },
                new() { Type = MediaStreamType.Video, Index = 1 }
            };

            Assert.False(_writer.TryWrite(Uuid, null, null, fromMaster, CancellationToken.None));

            _repository.DidNotReceive().SaveMediaStreams(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_WritesToThePrimary_WhenTheRouteCarriesNoTier()
        {
            var primary = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            var alternate = Item(
                Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(primary, alternate);

            Assert.True(_writer.TryWrite(Uuid, null, null, Streams, CancellationToken.None));

            _repository.Received(1).SaveMediaStreams(primary.Id, Streams, Arg.Any<CancellationToken>());
            _repository.DidNotReceive().SaveMediaStreams(
                alternate.Id, Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_WritesToTheMatchingTier_WhenTheRouteCarriesOne()
        {
            // The whole point: with three items sharing one VOD id, writing the served master's
            // tracks onto an arbitrary one would mislabel two items out of three.
            var primary = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            var hd = Item(
                Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            var uhd = Item(
                Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(primary, hd, uhd);

            Assert.True(_writer.TryWrite(Uuid, 2160, "vp9", Streams, CancellationToken.None));

            _repository.Received(1).SaveMediaStreams(uhd.Id, Streams, Arg.Any<CancellationToken>());
            _repository.DidNotReceive().SaveMediaStreams(
                hd.Id, Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
            _repository.DidNotReceive().SaveMediaStreams(
                primary.Id, Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_MatchesOnTheQueryOnly_WhenTheStoredPrefixIsStale()
        {
            // LocalBaseUrl changed since the item was published. The tier is still exactly right.
            var alternate = Item(
                Guid.NewGuid(),
                $"https://old-host.example.com:9000/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(alternate);

            Assert.True(_writer.TryWrite(Uuid, 1080, "h264", Streams, CancellationToken.None));

            _repository.Received(1).SaveMediaStreams(alternate.Id, Streams, Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_ReturnsFalse_WhenNoItemCarriesThatTier()
        {
            var primary = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(primary);

            Assert.False(_writer.TryWrite(Uuid, 2160, "vp9", Streams, CancellationToken.None));

            _repository.DidNotReceive().SaveMediaStreams(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_ReturnsFalse_WhenNoItemCarriesTheVodId()
        {
            LibraryHolds();

            Assert.False(_writer.TryWrite(Uuid, null, null, Streams, CancellationToken.None));

            _repository.DidNotReceive().SaveMediaStreams(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public void TryWrite_ReturnsFalse_WhenThereAreNoStreamsToWrite()
        {
            Assert.False(_writer.TryWrite(Uuid, null, null, [], CancellationToken.None));
        }

        [Fact]
        public void TryWrite_QueriesWithoutALimit()
        {
            // A Limit of 1 would hand back an arbitrary member of the group and make the match
            // below depend on which one the database happened to return first.
            var primary = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(primary);

            _writer.TryWrite(Uuid, null, null, Streams, CancellationToken.None);

            _library.Received(1).GetItemList(Arg.Is<InternalItemsQuery>(q => q.Limit == null));
        }

        [Fact]
        public void TryWrite_AsksForAlternateVersions()
        {
            // Jellyfin hides every item carrying a PrimaryVersionId from any query that does not ask
            // for alternates (BaseItemRepository.TranslateQuery -> ApplyAlternateVersionFiltering).
            // Without the flag this query only ever sees the primary, whose bare path serves no tier,
            // so no alternate could ever be written to. A substituted ILibraryManager has no such
            // filter, which is why only the flag itself can be pinned here.
            var primary = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(primary);

            _writer.TryWrite(Uuid, null, null, Streams, CancellationToken.None);

            _library.Received(1).GetItemList(
                Arg.Is<InternalItemsQuery>(q => q.IncludeAlternateVersions));
        }

        [Fact]
        public void TryWrite_StillReturnsFalse_WhenTheVodIdIsPresentButTheTierIsNot()
        {
            // Two distinct failures reach the same `false`, and they are logged differently: nothing
            // carries the id at all, versus something carries it but serves another tier. This pins
            // that the second case is reached with the group non-empty, which is what makes the
            // separate message worth having.
            var primary = Item(Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            var hd = Item(
                Guid.NewGuid(), $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(primary, hd);

            Assert.False(_writer.TryWrite(Uuid, 2160, "vp9", Streams, CancellationToken.None));

            _repository.DidNotReceive().SaveMediaStreams(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<MediaStream>>(), Arg.Any<CancellationToken>());
        }
    }
}

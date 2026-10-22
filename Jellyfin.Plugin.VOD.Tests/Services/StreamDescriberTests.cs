using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class StreamDescriberTests
    {
        private const string Uuid = "3f9a2b1c-0000-0000-0000-000000000001";

        private const string Master = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="français",LANGUAGE="fr",DEFAULT=YES,URI="https://cdn.test/a-fr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=3840x2160,CODECS="vp09.00.50.08,mp4a.40.2",AUDIO="a"
            https://cdn.test/v-2160.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="a"
            https://cdn.test/v-1080.m3u8
            """;

        private const string AvcOnlyMaster = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="français",LANGUAGE="fr",DEFAULT=YES,URI="https://cdn.test/a-fr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="a"
            https://cdn.test/v-1080.m3u8
            """;

        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly IMediaStreamRepository _repository = Substitute.For<IMediaStreamRepository>();
        private readonly IMediaEncoder _encoder = Substitute.For<IMediaEncoder>();
        private readonly PlaylistCache _cache = new();
        private readonly StreamDescriber _describer;

        private readonly PluginConfiguration _config = new()
        {
            LocalBaseUrl = "http://localhost:8096"
        };

        public StreamDescriberTests()
            => _describer = new StreamDescriber(
                _library,
                _repository,
                _encoder,
                _cache,
                new SubtitleTokens(_cache),
                NullLogger<StreamDescriber>.Instance);

        private static Movie Item(string path)
        {
            var movie = new Movie { Id = Guid.NewGuid(), Path = path, Name = path };
            movie.SetProviderId("VOD", Uuid);
            return movie;
        }

        private void LibraryHolds(params BaseItem[] items)
            => _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>(items));

        private void HasNoStreams(BaseItem item)
            => _repository.GetMediaStreams(Arg.Is<MediaStreamQuery>(q => q.ItemId.Equals(item.Id)))
                .Returns([]);

        private void HasStreams(BaseItem item, params MediaStream[] streams)
            => _repository.GetMediaStreams(Arg.Is<MediaStreamQuery>(q => q.ItemId.Equals(item.Id)))
                .Returns(new List<MediaStream>(streams));

        private void ProbeAnswers(params MediaStream[] streams)
            => _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new MediaInfo { MediaStreams = new List<MediaStream>(streams) }));

        private IReadOnlyList<MediaStream>? SavedFor(BaseItem item)
        {
            IReadOnlyList<MediaStream>? saved = null;
            foreach (var call in _repository.ReceivedCalls())
            {
                if (call.GetMethodInfo().Name == nameof(IMediaStreamRepository.SaveMediaStreams)
                    && ((Guid)call.GetArguments()[0]!).Equals(item.Id))
                {
                    saved = (IReadOnlyList<MediaStream>)call.GetArguments()[1]!;
                }
            }

            return saved;
        }

        [Fact]
        public async Task DescribeGroupAsync_AnUndescribedTier_IsProbedAndSavedWithOurLabels()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            ProbeAnswers(
                new MediaStream { Type = MediaStreamType.Audio, Index = 7, Codec = "aac" },
                new MediaStream { Type = MediaStreamType.Video, Index = 9, Codec = "vp9", Height = 2160 });

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            var saved = SavedFor(tier);
            Assert.NotNull(saved);
            Assert.Equal(2, saved!.Count);
            Assert.Equal([0, 1], new[] { saved[0].Index, saved[1].Index });
            Assert.Equal("français", saved[0].Title);
            Assert.Equal(MediaStreamType.Video, saved[1].Type);
        }

        [Fact]
        public async Task DescribeGroupAsync_AVideoTheProbeCouldNotMeasure_IsSavedWithTheVariantBandwidth()
        {
            // H.264 in MPEG-TS comes back from ffprobe without a bitrate, and Jellyfin will not copy
            // a video of unknown bitrate for a client that caps it.
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(tier);
            HasNoStreams(tier);
            ProbeAnswers(
                new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", BitRate = 131852 },
                new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 });

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            var saved = SavedFor(tier);
            Assert.NotNull(saved);
            Assert.Equal(3000000, saved![1].BitRate);
        }

        [Fact]
        public async Task DescribeGroupAsync_AMuxedVariant_IsSavedWithItsBandwidth()
        {
            // Twitch: one muxed variant and no rendition, so the master yields no stream list — the
            // bandwidth has to come from the variant declaration itself.
            const string muxed = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=8444342,CODECS="avc1.64002A,mp4a.40.2",RESOLUTION=1920x1080,FRAME-RATE=60.000
                https://cdn.test/chunked.m3u8
                """;
            var primary = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            LibraryHolds(primary);
            HasNoStreams(primary);
            ProbeAnswers(
                new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", BitRate = 192000 },
                new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 },
                new MediaStream { Type = MediaStreamType.Data, Codec = "timed_id3" });

            await _describer.DescribeGroupAsync(
                _config, Uuid, muxed, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            var saved = SavedFor(primary);
            Assert.NotNull(saved);
            Assert.Equal(8444342, saved![1].BitRate);
        }

        [Fact]
        public async Task DescribeGroupAsync_AMemberThatIsAlreadyDescribed_IsLeftAlone()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasStreams(tier, new MediaStream { Type = MediaStreamType.Video, Codec = "vp9" });

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Null(SavedFor(tier));
            await _encoder.DidNotReceive().GetMediaInfo(
                Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DescribeGroupAsync_ProbesTheRouteBuiltFromTheCurrentBaseUrl_NotTheStoredPath()
        {
            // The administrator moved the server after this item was published: its stored path
            // still carries the right tier but the wrong host (VersionGroup.TierOf remarks).
            var tier = Item($"http://old-host:9999/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Codec = "vp9" });

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            await _encoder.Received(1).GetMediaInfo(
                Arg.Is<MediaInfoRequest>(r => r.MediaSource.Path
                    == $"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9"),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DescribeGroupAsync_PrimesTheCacheUnderTheKeyTheRouteReads()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Codec = "vp9" });

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            var key = StreamCacheKey.For(Uuid, StreamCacheKey.DefaultFormat, 2160, "vp9", "", "");
            Assert.True(_cache.TryGet(key, out var primed));
            Assert.Contains("RESOLUTION=3840x2160", primed, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DescribeGroupAsync_ATierTheMasterNoLongerCarries_IsSkippedAndTheRestDescribed()
        {
            var gone = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=4320&codec=av1");
            var present = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(gone, present);
            HasNoStreams(gone);
            HasNoStreams(present);
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Codec = "vp9" });

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Null(SavedFor(gone));
            Assert.NotNull(SavedFor(present));
        }

        [Fact]
        public async Task DescribeGroupAsync_AProbeThatReturnsNoStream_SavesNothing()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            ProbeAnswers();

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Null(SavedFor(tier));
        }

        [Fact]
        public async Task DescribeGroupAsync_AProbeThatThrows_SavesNothingAndDoesNotPropagate()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<MediaInfo>>(_ => throw new InvalidOperationException("ffprobe exploded"));

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Null(SavedFor(tier));
        }

        [Fact]
        public async Task DescribeGroupAsync_AReferenceTierServingThePrimarysVariant_CopiesItsDescription()
        {
            var primary = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            var reference = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(primary, reference);
            HasNoStreams(reference);

            // The primary is already described — by Jellyfin's probe, on an earlier playback.
            HasStreams(
                primary,
                new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac" },
                new MediaStream { Type = MediaStreamType.Video, Index = 1, Codec = "h264", Height = 1080 });

            await _describer.DescribeGroupAsync(
                _config, Uuid, AvcOnlyMaster, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            var saved = SavedFor(reference);
            Assert.NotNull(saved);
            Assert.Equal("h264", saved![1].Codec);
            await _encoder.DidNotReceive().GetMediaInfo(
                Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DescribeGroupAsync_AReferenceTierWhosePrimaryIsUndescribed_IsProbed()
        {
            var primary = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            var reference = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(primary, reference);
            HasNoStreams(primary);
            HasNoStreams(reference);
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Codec = "h264" });

            await _describer.DescribeGroupAsync(
                _config, Uuid, AvcOnlyMaster, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            // The substitute keeps answering "no streams" for the primary even after its probe, as
            // when that probe saved nothing: the reference then cannot copy from a blank and is
            // probed itself. (Live, the primary's save lands first and the reference is copied.)
            await _encoder.Received(2).GetMediaInfo(
                Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
            Assert.NotNull(SavedFor(reference));
        }

        [Fact]
        public async Task DescribeGroupAsync_ATierServingADifferentVariantFromThePrimary_IsProbed()
        {
            var primary = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            var best = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(primary, best);
            HasStreams(primary, new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 });
            HasNoStreams(best);
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Codec = "vp9", Height = 2160 });

            await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            await _encoder.Received(1).GetMediaInfo(
                Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
            var saved = SavedFor(best);
            Assert.NotNull(saved);
            Assert.Equal("vp9", saved![0].Codec);
        }

        [Fact]
        public async Task DescribeGroupAsync_ASuccessfulProbe_ReportsAttemptedOneDescribedOne()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            ProbeAnswers(
                new MediaStream { Type = MediaStreamType.Video, Index = 9, Codec = "vp9", Height = 2160 });

            var outcome = await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Equal(1, outcome.Attempted);
            Assert.Equal(1, outcome.Described);
        }

        [Fact]
        public async Task DescribeGroupAsync_AProbeThatThrows_ReportsAttemptedOneDescribedZero()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<MediaInfo>>(_ => throw new InvalidOperationException("ffprobe exploded"));

            var outcome = await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Equal(1, outcome.Attempted);
            Assert.Equal(0, outcome.Described);
        }

        [Fact]
        public async Task DescribeGroupAsync_AProbeThatReturnsNoStream_ReportsAttemptedOneDescribedZero()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);
            ProbeAnswers(); // empty stream list

            var outcome = await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Equal(1, outcome.Attempted);
            Assert.Equal(0, outcome.Described);
        }

        [Fact]
        public async Task DescribeGroupAsync_AllMembersAlreadyDescribed_ReportsAttemptedZero()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasStreams(tier, new MediaStream { Type = MediaStreamType.Video, Codec = "vp9" });

            var outcome = await _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Equal(0, outcome.Attempted);
        }

        [Fact]
        public async Task DescribeGroupAsync_TwoPassesAtOnce_ProbeTheMemberOnce()
        {
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=2160&codec=vp9");
            LibraryHolds(tier);
            HasNoStreams(tier);

            // Hold the first probe open until both passes have started, so the second one meets a
            // member that is already in flight rather than one that is simply finished.
            var probeEntered = new TaskCompletionSource();
            var releaseProbe = new TaskCompletionSource();
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    probeEntered.TrySetResult();
                    await releaseProbe.Task.ConfigureAwait(false);
                    return new MediaInfo
                    {
                        MediaStreams = new List<MediaStream>
                        {
                            new() { Type = MediaStreamType.Video, Codec = "vp9" }
                        }
                    };
                });

            var first = _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);
            await probeEntered.Task.ConfigureAwait(true);

            var second = _describer.DescribeGroupAsync(
                _config, Uuid, Master, new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);
            await second.ConfigureAwait(true);

            releaseProbe.SetResult();
            await first.ConfigureAwait(true);

            await _encoder.Received(1).GetMediaInfo(
                Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
        }

        private static VideoRecord VideoWith(params VideoQuality[] qualities)
            => new()
            {
                Id = Uuid,
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v",
                Extras = new VideoExtras { Qualities = qualities },
            };

        [Fact]
        public async Task DescribeVideoAsync_DescribesThePrimaryAndEachOfferedTier_WithoutTheLibrary()
        {
            ProbeAnswers(
                new MediaStream { Type = MediaStreamType.Audio, Index = 7, Codec = "aac" },
                new MediaStream { Type = MediaStreamType.Video, Index = 9, Codec = "vp9", Height = 2160 });

            var described = await _describer.DescribeVideoAsync(
                _config,
                VideoWith(new VideoQuality(1080, "h264", 30), new VideoQuality(2160, "vp9", 30)),
                Master,
                new Uri("https://cdn.test/master.m3u8"),
                CancellationToken.None);

            Assert.NotNull(described);
            Assert.Equal(
                new[] { "1080/h264", "2160/vp9", DescribedStreams.PrimaryKey },
                described!.Keys.Order(StringComparer.Ordinal));
            Assert.All(described.Values, streams => Assert.Equal([0, 1], streams.Select(s => s.Index)));
            _library.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
        }

        [Fact]
        public async Task DescribeVideoAsync_ATierServingThePrimarysVariant_IsCopiedNotProbed()
        {
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Height = 1080 });

            var described = await _describer.DescribeVideoAsync(
                _config, VideoWith(new VideoQuality(1080, "h264", 30)), AvcOnlyMaster,
                new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Equal(2, described!.Count);
            await _encoder.Received(1).GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DescribeVideoAsync_ACopiedTier_DoesNotShareStreamsWithThePrimary()
        {
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Height = 1080 });

            var described = await _describer.DescribeVideoAsync(
                _config, VideoWith(new VideoQuality(1080, "h264", 30)), AvcOnlyMaster,
                new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.NotSame(described![DescribedStreams.PrimaryKey][0], described["1080/h264"][0]);
        }

        [Fact]
        public async Task DescribeVideoAsync_ATierTheMasterNoLongerCarries_IsSkippedNotAFailure()
        {
            ProbeAnswers(new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Height = 1080 });

            var described = await _describer.DescribeVideoAsync(
                _config,
                VideoWith(new VideoQuality(1080, "h264", 30), new VideoQuality(2160, "vp9", 30)),
                AvcOnlyMaster,
                new Uri("https://cdn.test/master.m3u8"),
                CancellationToken.None);

            Assert.NotNull(described);
            Assert.False(described!.ContainsKey("2160/vp9"));
        }

        [Fact]
        public async Task DescribeVideoAsync_AProbeThatThrows_FailsTheVideo()
        {
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<MediaInfo>>(_ => throw new InvalidOperationException("ffprobe died"));

            var described = await _describer.DescribeVideoAsync(
                _config, VideoWith(new VideoQuality(1080, "h264", 30)), AvcOnlyMaster,
                new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Null(described);
        }

        [Fact]
        public async Task DescribeVideoAsync_AProbeThatReturnsNoStream_FailsTheVideo()
        {
            ProbeAnswers();

            var described = await _describer.DescribeVideoAsync(
                _config, VideoWith(new VideoQuality(1080, "h264", 30)), AvcOnlyMaster,
                new Uri("https://cdn.test/master.m3u8"), CancellationToken.None);

            Assert.Null(described);
        }

        [Fact]
        public void ApplyDescribed_WritesOnlyMembersWithoutAVideoStream()
        {
            // A member that already has a video stream was described by Jellyfin or by the playback
            // failsafe, and that description is at least as fresh as ours.
            var primary = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8");
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(primary, tier);
            HasStreams(primary, new MediaStream { Type = MediaStreamType.Video, Codec = "h264" });
            HasNoStreams(tier);

            var applied = _describer.ApplyDescribed(
                Uuid,
                new Dictionary<string, IReadOnlyList<MediaStream>>
                {
                    [DescribedStreams.PrimaryKey] =
                        [new MediaStream { Type = MediaStreamType.Video, Codec = "described" }],
                    ["1080/h264"] =
                        [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 }],
                },
                CancellationToken.None);

            Assert.Equal(1, applied);
            Assert.Null(SavedFor(primary));
            Assert.Equal(1080, SavedFor(tier)!.Single().Height);
        }

        [Fact]
        public void ApplyDescribed_WritesNothing_WhenThereIsNoDescription()
        {
            // The caller passes what the wave produced, and a wave that described nothing passes an
            // empty map rather than skipping the call.
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(tier);
            HasNoStreams(tier);

            Assert.Equal(
                0,
                _describer.ApplyDescribed(
                    Uuid,
                    new Dictionary<string, IReadOnlyList<MediaStream>>(),
                    CancellationToken.None));
            Assert.Null(SavedFor(tier));
        }

        [Fact]
        public void ApplyDescribed_WritesNothing_ForAMemberTheDescriptionDoesNotCover()
        {
            // A tier the master no longer carries is absent from the description. Nothing to write,
            // and nothing to repair here: the next listing rebuilds the ladder.
            var tier = Item($"http://localhost:8096/VOD/stream/{Uuid}.m3u8?height=1080&codec=h264");
            LibraryHolds(tier);
            HasNoStreams(tier);

            Assert.Equal(
                0,
                _describer.ApplyDescribed(
                    Uuid,
                    new Dictionary<string, IReadOnlyList<MediaStream>>
                    {
                        ["2160/vp9"] = [new MediaStream { Type = MediaStreamType.Video, Height = 2160 }],
                    },
                    CancellationToken.None));
            Assert.Null(SavedFor(tier));
        }
    }
}

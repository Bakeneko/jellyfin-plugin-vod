using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The checkup: the only step that publishes, and therefore the only one that decides what the
    /// user sees — and what they lose, since an unpublished item takes its watch history with it.
    /// </summary>
    /// <remarks>
    /// These tests publish for real, through a <see cref="LibraryPublisher"/> over a substituted
    /// <see cref="ILibraryManager"/>: the publisher is sealed with non-virtual methods, and the
    /// behaviour under test is exactly what it does to the library. Hence the
    /// <see cref="BaseItemStaticsCollection"/> membership — <c>BaseItem.LibraryManager</c> is a
    /// process-wide static that <c>Folder.AddChild</c> and
    /// <c>Series.CreatePresentationUniqueKey()</c> both go through.
    /// </remarks>
    [Collection(BaseItemStaticsCollection.Name)]
    public sealed class CheckupStepTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        private const string SourceUrl = "https://www.youtube.com/@chan/videos";
        private const string LibraryName = "Films";
        private const string LocalBaseUrl = "http://localhost:8096";
        /// <summary>The name the LISTING resolved, which is the one that must be published.</summary>
        private const string ResolvedName = "Chan, as the platform names it";

        /// <summary>
        /// The administrator's own label on the source. Deliberately different from
        /// <see cref="ResolvedName"/>: publishing this one instead would rename the series the day
        /// a listing finally names it, and two equal fixtures could not tell the two apart.
        /// </summary>
        private const string ConfiguredName = "Chan";

        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(), "vod-tests-checkup-" + Guid.NewGuid().ToString("N"));

        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();

        // The describer gets its own library substitute: ApplyDescribed is observed through the
        // member lookup and the stream repository, and mixing it with the publisher's library
        // would make either assertion ambiguous.
        private readonly ILibraryManager _describerLibrary = Substitute.For<ILibraryManager>();
        private readonly IMediaStreamRepository _streams = Substitute.For<IMediaStreamRepository>();
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();

        private readonly List<BaseItem> _published = [];
        private readonly List<VideoRecord> _rows = [];
        private readonly List<string> _knownVideoIds = [];

        private readonly Folder _anchor;
        private readonly CheckupStep _step;

        private SourceRecord? _record = new() { Url = SourceUrl, Name = ResolvedName };

        public CheckupStepTests()
        {
            var location = Path.Combine(_tempRoot, LibraryName);
            Directory.CreateDirectory(location);
            _anchor = new Folder { Id = Guid.NewGuid(), Path = location };

            BaseItem.LibraryManager = _library;
            _library.GetLibraryOptions(Arg.Any<BaseItem>())
                .Returns(new LibraryOptions { PreferredMetadataLanguage = "en" });
            _library.GetCollectionFolders(Arg.Any<BaseItem>()).Returns([]);
            _library.GetVirtualFolders()
                .Returns([new VirtualFolderInfo { Name = LibraryName, Locations = [location] }]);
            _library.FindByPath(location, true).Returns(_anchor);

            // Deterministic ids, and a library that holds what CreateItem received: a second pass
            // then finds the items the first one published, as it does in production.
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => _published.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(_ => _published.ToList());

            _db.GetVideosBySource(SourceUrl).Returns(_ => _rows.ToList());
            _db.GetVideoIdsWithMetadata().Returns(_ => _knownVideoIds.ToList());
            _db.GetSource(SourceUrl).Returns(_ => _record);

            _describerLibrary.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
            _streams.GetMediaStreams(Arg.Any<MediaStreamQuery>()).Returns([]);

            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                _describerLibrary,
                _streams,
                Substitute.For<IMediaEncoder>(),
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);

            var publisher = new LibraryPublisher(
                _library,
                _db,
                new ArtworkStore(Path.Combine(_tempRoot, "artwork")),
                Substitute.For<IVodLocalizer>(),
                NullLogger<LibraryPublisher>.Instance);

            _step = new CheckupStep(_db, publisher, describer, NullLogger<CheckupStep>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

        private static Guid DeterministicId(string key)
            => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

        private static SourceDefinition Source(
            SourceMode mode = SourceMode.Movies, int retentionDays = 0) => new()
            {
                Url = SourceUrl,
                Enabled = true,
                Mode = mode,
                RetentionDays = retentionDays,
                Name = ConfiguredName,
            };

        private static PluginConfiguration Config(
            string movies = LibraryName, string series = "") => new()
            {
                MoviesLibraryName = movies,
                SeriesLibraryName = series,
                LocalBaseUrl = LocalBaseUrl,
            };

        /// <summary>
        /// A row the checkup will find publishable: metadata fetched, one offered tier, a date.
        /// </summary>
        /// <remarks>
        /// <paramref name="numbered"/> pre-writes the number the checkup would compute, so a test
        /// about numbering can tell "already correct" from "needs a write". Titles carry no digits:
        /// a parseable SxxEyy in a title counts as a supplied number and would skip the date-based
        /// calculation entirely.
        /// </remarks>
        private VideoRecord AddRow(
            string id,
            DateTimeOffset publishedAt,
            bool numbered = true,
            DateTimeOffset? describedAt = null,
            CallOutcome describeResult = CallOutcome.None)
        {
            var row = new VideoRecord
            {
                Id = id,
                SourceUrl = SourceUrl,
                NativeId = "native-" + id,
                VideoUrl = "https://www.youtube.com/watch?v=" + id,
                Title = "A video about " + id,
                MetadataAt = Now.AddDays(-1),
                MetadataResult = CallOutcome.Ok,
                PublishedAt = publishedAt,
                DescribedAt = describedAt,
                DescribeResult = describeResult,
                Extras = new VideoExtras { Qualities = [new VideoQuality(1080, "h264", 30)] },
            };

            if (numbered)
            {
                row.SeasonNumber = publishedAt.Year;
                row.EpisodeNumber = (((publishedAt.Month * 100) + publishedAt.Day) * 1000) + 1;
            }

            _rows.Add(row);
            _knownVideoIds.Add(row.Id);
            return row;
        }

        /// <summary>An item already in the library, carrying a VOD id but matching no expectation.</summary>
        private Movie LibraryItemFor(string vodId)
        {
            var item = new Movie
            {
                Id = Guid.NewGuid(),
                ParentId = _anchor.Id,
                Name = vodId,
                Path = LocalBaseUrl + "/VOD/stream/" + vodId + ".m3u8",
            };
            item.SetProviderId("VOD", vodId);
            _published.Add(item);
            return item;
        }

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>>
            NoDescriptions()
            => new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>>(
                StringComparer.OrdinalIgnoreCase);

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>>
            DescriptionsFor(string videoId, params MediaStream[] primary)
            => new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [videoId] = new Dictionary<string, IReadOnlyList<MediaStream>>(StringComparer.Ordinal)
                {
                    [DescribedStreams.PrimaryKey] = primary,
                },
            };

        private Task<CheckupOutcome> RunAsync(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>>? descriptions = null,
            SourceMode mode = SourceMode.Movies,
            int retentionDays = 0,
            PluginConfiguration? config = null)
            => _step.RunAsync(
                Source(mode, retentionDays),
                descriptions ?? NoDescriptions(),
                config ?? Config(),
                Now,
                CancellationToken.None);

        /// <summary>
        /// The streams the describer saved for one item, read off the repository's received calls
        /// rather than matched by argument: it is the exact list that reached the repository.
        /// </summary>
        private IReadOnlyList<MediaStream>? SavedFor(BaseItem item)
        {
            IReadOnlyList<MediaStream>? saved = null;
            foreach (var call in _streams.ReceivedCalls())
            {
                if (call.GetMethodInfo().Name == nameof(IMediaStreamRepository.SaveMediaStreams)
                    && ((Guid)call.GetArguments()[0]!).Equals(item.Id))
                {
                    saved = (IReadOnlyList<MediaStream>)call.GetArguments()[1]!;
                }
            }

            return saved;
        }

        private Movie DescriberSeesPrimaryMemberOf(string videoId)
        {
            // The describer finds a video's members by its VOD provider id and reads the tier each
            // one serves off its own path; a path with no query is the primary.
            var member = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "primary",
                Path = VersionGroup.PathFor(LocalBaseUrl, videoId, null),
            };
            member.SetProviderId("VOD", videoId);
            _describerLibrary.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(_ => new List<BaseItem> { member });
            return member;
        }

        // ── What the user sees ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task APublishableVideo_IsPublished_AndTheStageResultRecorded()
        {
            var row = AddRow("alpha", Now.AddDays(-1));

            var outcome = await RunAsync();

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Equal(1, outcome.Published);
            Assert.Contains(_published, i => i is Movie && i.GetProviderId("VOD") == row.Id);

            // Every stage records its result, so the configuration page can show a stuck source
            // without a history table.
            _db.Received(1).SetStageResult(SourceUrl, SourceStage.Checkup, CallOutcome.Ok, null);
        }

        [Fact]
        public async Task AVideoPastRetention_IsUnpublished_AndStaysInTheDatabase()
        {
            // Retention is a publication filter, never an erase rule: deleting the row would have
            // the next listing rediscover and refetch the video, which is the call loop the rework
            // exists to remove. The item goes, the row stays.
            var fresh = AddRow("fresh", Now.AddDays(-1));
            var stale = AddRow("stale", Now.AddDays(-40));
            var staleItem = LibraryItemFor(stale.Id);

            var outcome = await RunAsync(retentionDays: 7);

            _library.Received(1).DeleteItem(staleItem, Arg.Any<DeleteOptions>(), Arg.Any<bool>());

            // Exactly one deletion: the fresh video's item must survive, and it is published in the
            // same pass, which is what makes the assertion above more than a count.
            _library.Received(1).DeleteItem(Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            Assert.Contains(_published, i => i is Movie && i.GetProviderId("VOD") == fresh.Id);

            // One publication, not two: the stale video must not be published back in the same
            // breath as its old item is retracted.
            Assert.Equal(1, outcome.Published);
            _db.DidNotReceive().DeleteVideo(Arg.Any<string>());
            Assert.Equal(1, outcome.Unpublished);
        }

        [Fact]
        public async Task AVideoAnotherSourceStillPublishes_IsNotSweptAway()
        {
            // The protected set spans every source, not just this one: an item whose video another
            // source still holds is not ours to delete. The ghost — a video whose row the cleanup
            // deleted earlier this turn — is what makes the sweep visibly alive here.
            AddRow("mine", Now.AddDays(-1));

            const string otherSourceVideoId = "other-source-video";
            _knownVideoIds.Add(otherSourceVideoId);
            var otherItem = LibraryItemFor(otherSourceVideoId);
            var ghostItem = LibraryItemFor("ghost-video");

            await RunAsync();

            _library.DidNotReceive().DeleteItem(otherItem, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
            _library.Received(1).DeleteItem(ghostItem, Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        }

        [Fact]
        public async Task ASeriesSource_PublishesUnderTheNameTheListingResolved()
        {
            var row = AddRow("episode", Now.AddDays(-1));

            var outcome = await RunAsync(
                mode: SourceMode.Series, config: Config(movies: string.Empty, series: LibraryName));

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Contains(_published, i => i is Series && i.Name == ResolvedName);
            // The primary, not the quality alternate it is also published with: the alternate is a
            // second Episode carrying the same VOD id, and only the primary's path has no tier.
            var episode = Assert.Single(
                _published.OfType<Episode>(),
                e => e.GetProviderId("VOD") == row.Id
                     && e.Path == VersionGroup.PathFor(LocalBaseUrl, row.Id, null));
            Assert.Equal(row.SeasonNumber, episode.ParentIndexNumber);
            Assert.Equal(row.EpisodeNumber, episode.IndexNumber);
        }

        [Fact]
        public async Task ALibraryJellyfinHasNeverIndexed_IsIndexedBeforePublishing()
        {
            // A scan skips a library folder that holds no filesystem entry, so a library holding
            // only virtual items is never indexed and the folder the publication hangs off does not
            // exist. On a fresh install nothing would EVER be published. This is why the checkup
            // prepares its target instead of merely resolving it, as the live probe does.
            AddRow("alpha", Now.AddDays(-1));
            var location = Path.Combine(_tempRoot, LibraryName);
            var indexed = false;
            _library.FindByPath(location, true).Returns(_ => indexed ? _anchor : null);
            _library.When(l => l.ValidateTopLibraryFolders(Arg.Any<CancellationToken>()))
                .Do(_ => indexed = true);

            var outcome = await RunAsync();

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Equal(1, outcome.Published);

            // The marker is what makes the folder non-empty, and therefore indexable at all.
            Assert.True(File.Exists(Path.Combine(location, ".keep")));
        }

        // ── Bookkeeping ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheAnchor_IsRecorded_AfterASuccessfulReconcile()
        {
            // Nobody else writes it since the sync service was deleted. Without it the cleanup's
            // "find a retired source by its stored anchor" path is dead code, and a source whose
            // library left the configuration would keep its items for ever.
            AddRow("alpha", Now.AddDays(-1));

            await RunAsync();

            _db.Received(1).SetPublishedAnchorId(SourceUrl, _anchor.Id);
        }

        [Fact]
        public async Task TheFingerprint_IsWritten_AfterASuccessfulReconcile()
        {
            AddRow("alpha", Now.AddDays(-1));
            var expected = SourceFingerprint.Of(Source());

            await RunAsync();

            _db.Received(1).SetConfigFingerprint(SourceUrl, expected);
        }

        [Fact]
        public async Task AReconcileThatThrows_RecordsTheFailure_AndWritesNeitherFingerprintNorAnchor()
        {
            // Writing the fingerprint on a failure would mark a configuration change as taken into
            // account although nothing was published, and the source would only become due again at
            // the next full listing — up to seven days later.
            AddRow("alpha", Now.AddDays(-1));
            _library.When(l => l.GetItemList(Arg.Any<InternalItemsQuery>()))
                .Do(_ => throw new InvalidOperationException("library unavailable"));

            var outcome = await RunAsync();

            Assert.NotEqual(CallOutcome.Ok, outcome.Outcome);
            _db.Received(1).SetStageResult(
                SourceUrl, SourceStage.Checkup,
                Arg.Is<CallOutcome>(o => o != CallOutcome.Ok),
                Arg.Any<string?>());
            _db.DidNotReceive().SetConfigFingerprint(Arg.Any<string>(), Arg.Any<string?>());
            _db.DidNotReceive().SetPublishedAnchorId(Arg.Any<string>(), Arg.Any<Guid?>());
        }

        [Fact]
        public async Task ASourceWithNothingToFetch_StillRepublishes()
        {
            // This is what makes a configuration change take effect: a source the pass only listed
            // still reaches the checkup, and the checkup still republishes and re-stamps the
            // fingerprint. An empty wave passes an empty dictionary, never null.
            AddRow("alpha", Now.AddDays(-1));
            await RunAsync();
            _db.ClearReceivedCalls();

            var outcome = await RunAsync(NoDescriptions());

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            _db.Received(1).SetConfigFingerprint(SourceUrl, SourceFingerprint.Of(Source()));
            _db.Received(1).SetPublishedAnchorId(SourceUrl, _anchor.Id);
            _db.Received(1).SetStageResult(SourceUrl, SourceStage.Checkup, CallOutcome.Ok, null);
        }

        // ── Numbering ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task OnlyARowWhoseNumberChanges_IsWrittenBack()
        {
            // Rewriting a row for an unchanged number would be one needless write per video per
            // turn, for every source, for ever.
            var settled = AddRow("settled", Now.AddDays(-1));
            var unnumbered = AddRow("unnumbered", Now.AddDays(-2), numbered: false);

            var outcome = await RunAsync();

            _db.DidNotReceive().UpsertVideo(settled);
            _db.Received(1).UpsertVideo(unnumbered);
            Assert.Equal(unnumbered.PublishedAt!.Value.Year, unnumbered.SeasonNumber);
            Assert.Equal(1018001, unnumbered.EpisodeNumber);
            Assert.Equal(1, outcome.Renumbered);
        }

        // ── Descriptions ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ACreatedItem_ReceivesTheDescriptionTheWaveJustMade()
        {
            var row = AddRow("alpha", Now.AddDays(-1));
            var member = DescriberSeesPrimaryMemberOf(row.Id);
            var described = new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 };

            var outcome = await RunAsync(DescriptionsFor(row.Id, described));

            var saved = SavedFor(member);
            Assert.NotNull(saved);
            Assert.Equal("h264", Assert.Single(saved!).Codec);
            Assert.Equal(0, outcome.MarkedForRedescribe);
        }

        [Fact]
        public async Task ACreatedItemWithoutADescription_IsMarkedForRedescribe_OnlyIfOneSucceededBefore()
        {
            // A description no longer outlives the pass that made it, so an item recreated between
            // two passes has none in hand. Without this marking it would stay mute until the
            // 30-day clock expired — and Jellyfin decides an undescribed tier's codec blind, which
            // means transcoding.
            //
            // The third row is why this test has three: a video whose describing CONCLUDED that
            // there is nothing to describe — no HLS master, no quality tier — carries a stamped
            // clock and a None result. Marking it would clear a clock that describing will only
            // stamp again with the same None, which costs an extra turn of the pass per video and
            // produces no stream. Measured on the test instance, that is roughly six percent of
            // YouTube videos. Verified by mutation: with the condition back on "described_at is not
            // null", this row is marked and the count is two.
            var onceDescribed = AddRow(
                "described", Now.AddDays(-1),
                describedAt: Now.AddDays(-2), describeResult: CallOutcome.Ok);
            var neverDescribed = AddRow("mute", Now.AddDays(-2));
            var nothingToDescribe = AddRow(
                "no-master", Now.AddDays(-3),
                describedAt: Now.AddDays(-2), describeResult: CallOutcome.None);

            var outcome = await RunAsync();

            _db.Received(1).ClearDescribedAt(onceDescribed.Id);
            _db.DidNotReceive().ClearDescribedAt(neverDescribed.Id);
            _db.DidNotReceive().ClearDescribedAt(nothingToDescribe.Id);
            Assert.Equal(1, outcome.MarkedForRedescribe);
        }

        [Fact]
        public async Task ADescription_IsNotAppliedToAnItemAnEarlierPassCreated()
        {
            // Only the ids this reconcile created are visited. An existing item already has its
            // streams, and looking every row up again would cost a library query per row per turn.
            var row = AddRow("alpha", Now.AddDays(-1));
            var member = DescriberSeesPrimaryMemberOf(row.Id);
            var described = new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 };

            await RunAsync(DescriptionsFor(row.Id, described));
            Assert.NotNull(SavedFor(member));
            _streams.ClearReceivedCalls();
            _describerLibrary.ClearReceivedCalls();
            _db.ClearReceivedCalls();

            await RunAsync(DescriptionsFor(row.Id, described));

            Assert.Null(SavedFor(member));
            _describerLibrary.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
            _db.DidNotReceive().ClearDescribedAt(Arg.Any<string>());
        }

        // ── Incomplete configuration is not a failure ────────────────────────────────────────────

        [Fact]
        public async Task ASourceWhoseModeHasNoLibrary_PublishesNothing_AndRecordsNoFailure()
        {
            // An unconfigured library is an incomplete configuration the page already reports, not
            // a breakdown: counting it as a failure would make the source look broken and advance
            // its consecutive-failure count every turn.
            AddRow("alpha", Now.AddDays(-1));

            var outcome = await RunAsync(config: Config(movies: string.Empty));

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Empty(_published);
            Assert.Equal(0, outcome.Published);
            _db.DidNotReceive().SetStageResult(
                SourceUrl, SourceStage.Checkup,
                Arg.Is<CallOutcome>(o => o != CallOutcome.Ok),
                Arg.Any<string?>());
            _db.DidNotReceive().SetConfigFingerprint(Arg.Any<string>(), Arg.Any<string?>());
            _db.DidNotReceive().SetPublishedAnchorId(Arg.Any<string>(), Arg.Any<Guid?>());
        }

        [Fact]
        public async Task ASourceTheListingNeverNamed_PublishesNothing()
        {
            // Publishing a series with no name creates an item the user cannot identify. The name
            // comes from the listing; its absence means the source has never been listed
            // successfully, so there is nothing to publish yet.
            _record = new SourceRecord { Url = SourceUrl, Name = null };
            AddRow("alpha", Now.AddDays(-1));

            var outcome = await RunAsync();

            Assert.Empty(_published);
            Assert.Equal(0, outcome.Published);
            _db.DidNotReceive().SetConfigFingerprint(Arg.Any<string>(), Arg.Any<string?>());
            _db.DidNotReceive().SetPublishedAnchorId(Arg.Any<string>(), Arg.Any<Guid?>());
            _db.DidNotReceive().SetStageResult(
                SourceUrl, SourceStage.Checkup,
                Arg.Is<CallOutcome>(o => o != CallOutcome.Ok),
                Arg.Any<string?>());
        }
    }
}

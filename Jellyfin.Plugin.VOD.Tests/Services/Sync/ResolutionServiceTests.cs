using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class ResolutionServiceRulesTests
    {
        private static VideoRecord Row(
            string id,
            ResolutionState state,
            DateTimeOffset? publishedAt,
            int attempts = 0,
            int? episode = null)
            => new()
            {
                Id = id,
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v/" + id,
                NativeId = id,
                SyncedAt = DateTimeOffset.UnixEpoch,
                ResolutionState = state,
                ResolutionAttempts = attempts,
                PublishedAt = publishedAt,
                SeasonNumber = episode.HasValue ? publishedAt?.Year : null,
                EpisodeNumber = episode,
            };

        private static readonly DateTimeOffset Day = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void SourceIsSettled_IsFalse_WhileAVideoIsPending()
        {
            var rows = new List<VideoRecord>
            {
                Row("a", ResolutionState.Resolved, Day),
                Row("b", ResolutionState.Pending, null),
            };

            Assert.False(ResolutionService.SourceIsSettled(rows));
        }

        [Fact]
        public void SourceIsSettled_IsFalse_WhileAFailedVideoHasFailedFewerThanThreeTimes()
        {
            var rows = new List<VideoRecord>
            {
                Row("a", ResolutionState.Resolved, Day),
                Row("b", ResolutionState.Failed, null, attempts: 2),
            };

            Assert.False(ResolutionService.SourceIsSettled(rows));
        }

        [Fact]
        public void SourceIsSettled_IsTrue_OnceTheFailedVideoHasFailedThreeTimes()
        {
            // Past the threshold a failing video stops holding its source back; it is still
            // retried on its own backoff and takes the next rank of its day if it ever passes.
            var rows = new List<VideoRecord>
            {
                Row("a", ResolutionState.Resolved, Day),
                Row("b", ResolutionState.Failed, null, attempts: ResolutionSettlement.BlockingFailedAttempts),
            };

            Assert.True(ResolutionService.SourceIsSettled(rows));
        }

        [Fact]
        public void SourceIsSettled_IgnoresUnavailableVideos()
        {
            var rows = new List<VideoRecord>
            {
                Row("a", ResolutionState.Resolved, Day),
                Row("b", ResolutionState.Unavailable, null),
            };

            Assert.True(ResolutionService.SourceIsSettled(rows));
        }

        [Fact]
        public void NumberByPublicationTime_RanksADayByTime_WhateverTheRowOrder()
        {
            // The resolver serves newest first, so the 20:00 video is listed before the 08:00 one.
            // Numbering by time gives the earlier video rank 1 (R-k, lifted).
            var evening = Row("b", ResolutionState.Resolved, Day.AddHours(20));
            var morning = Row("a", ResolutionState.Resolved, Day.AddHours(8));

            var numbered = ResolutionService.NumberByPublicationTime([evening, morning]);

            Assert.Equal(2, numbered.Count);
            Assert.Equal(1013001, morning.EpisodeNumber);
            Assert.Equal(1013002, evening.EpisodeNumber);
            Assert.Equal(2026, morning.SeasonNumber);
        }

        [Fact]
        public void NumberByPublicationTime_ContinuesAfterTheDaysExistingNumbers()
        {
            // R-l: an older video that only arrives after its day was numbered takes the next rank;
            // the existing number is never touched.
            var published = Row("a", ResolutionState.Resolved, Day.AddHours(20), episode: 1013001);
            var late = Row("b", ResolutionState.Resolved, Day.AddHours(7));

            ResolutionService.NumberByPublicationTime([published, late]);

            Assert.Equal(1013001, published.EpisodeNumber);
            Assert.Equal(1013002, late.EpisodeNumber);
        }

        [Fact]
        public void NumberByPublicationTime_LeavesNumberedUndatedAndUnresolvedRowsAlone()
        {
            var numbered = Row("a", ResolutionState.Resolved, Day, episode: 1013001);
            var undated = Row("b", ResolutionState.Resolved, null);
            var failed = Row("c", ResolutionState.Failed, Day.AddHours(3), attempts: 5);

            var changed = ResolutionService.NumberByPublicationTime([numbered, undated, failed]);

            Assert.Empty(changed);
            Assert.Null(undated.EpisodeNumber);
            Assert.Null(failed.EpisodeNumber);
        }

        [Fact]
        public void Publishable_HoldsBackUnnumberedRows_UntilTheSourceIsSettled()
        {
            var now = Day.AddDays(1);
            var rows = new List<VideoRecord>
            {
                Row("numbered", ResolutionState.Resolved, Day.AddDays(-1), episode: 1012001),
                Row("unnumbered", ResolutionState.Resolved, Day),
                Row("undated", ResolutionState.Resolved, null),
                Row("waiting", ResolutionState.Pending, null),
            };

            var publishable = ResolutionService.Publishable(rows, now, retentionDays: 0);

            // What is already numbered stays visible while the source fills.
            Assert.Equal(["numbered"], publishable.Select(v => v.Id));
        }

        [Fact]
        public void Publishable_OffersUndatedRows_OnceTheSourceIsSettled()
        {
            var now = Day.AddDays(1);
            var rows = new List<VideoRecord>
            {
                Row("numbered", ResolutionState.Resolved, Day, episode: 1013001),
                Row("undated", ResolutionState.Resolved, null),
                Row("gone", ResolutionState.Unavailable, null),
            };

            var publishable = ResolutionService.Publishable(rows, now, retentionDays: 0);

            Assert.Equal(["numbered", "undated"], publishable.Select(v => v.Id));
        }

        [Fact]
        public void Publishable_NeverOffersADatedRowWithoutANumber_EvenOnceSettled()
        {
            // The live probe publishes through this filter but never runs the numbering pass. A
            // settled source the resolver has not numbered yet must not have its dated rows
            // published unnumbered: they would be filed under Specials, then renumbered.
            var now = Day.AddDays(1);
            var rows = new List<VideoRecord> { Row("dated", ResolutionState.Resolved, Day) };

            Assert.Empty(ResolutionService.Publishable(rows, now, retentionDays: 0));
        }

        [Fact]
        public void Publishable_DropsRowsOutsideRetention()
        {
            var now = Day.AddDays(1);
            var rows = new List<VideoRecord>
            {
                Row("recent", ResolutionState.Resolved, Day, episode: 1013001),
                Row("too-old", ResolutionState.Resolved, Day.AddDays(-40), episode: 903001),
            };

            Assert.Equal(
                ["recent"],
                ResolutionService.Publishable(rows, now, retentionDays: 30).Select(v => v.Id));
        }

        [Fact]
        public void Publishable_IgnoresRetention_WhenItIsNotConfigured()
        {
            var now = Day.AddDays(1);
            var rows = new List<VideoRecord>
            {
                Row("ancient", ResolutionState.Resolved, Day.AddYears(-5), episode: 1013001),
            };

            Assert.Equal(
                ["ancient"],
                ResolutionService.Publishable(rows, now, retentionDays: 0).Select(v => v.Id));
        }
    }

    public sealed class ResolutionServiceWaveTests
    {
        private static ResolutionService Make(IVodDatabase db)
        {
            var library = Substitute.For<ILibraryManager>();
            library.GetVirtualFolders().Returns([]);
            var localizer = Substitute.For<IVodLocalizer>();
            var artwork = new ArtworkStore(Path.Combine(Path.GetTempPath(), "vod-tests-resolution"));
            var publisher = new LibraryPublisher(
                library, db, artwork, localizer, NullLogger<LibraryPublisher>.Instance);

            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                Substitute.For<ILibraryManager>(),
                Substitute.For<IMediaStreamRepository>(),
                Substitute.For<IMediaEncoder>(),
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);

            return new ResolutionService(
                db,
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                describer,
                publisher,
                new VideoDescriptionStep(Substitute.For<IHttpClientFactory>(), describer),
                NullLogger<ResolutionService>.Instance);
        }

        [Fact]
        public async Task ResolveWaveAsync_DoesNothing_WhenNoSourceIsDue()
        {
            var db = Substitute.For<IVodDatabase>();
            db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns((string?)null);

            var resolved = await Make(db).ResolveWaveAsync(
                new PluginConfiguration(), "Youtube", CancellationToken.None);

            Assert.Equal(0, resolved);
            db.DidNotReceive().GetDueVideos(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>());
        }

        [Fact]
        public async Task ResolveWaveAsync_StampsAndSkips_ASourceTheConfigurationNoLongerHas()
        {
            // B-C2: rows outlive a removed source until the next sync prunes them; resolving them
            // would spend calls on a library nobody asked for. The wave stamps and returns 0.
            // N2: SelectSourceToResolve is called with the already-skipped set; the SQL excludes
            // those URLs so the second call returns null and the loop terminates.
            var db = Substitute.For<IVodDatabase>();
            db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns("https://example.test/removed", (string?)null);

            var resolved = await Make(db).ResolveWaveAsync(
                new PluginConfiguration(), "Youtube", CancellationToken.None);

            Assert.Equal(0, resolved);
            db.Received().SetLastResolvedAt("https://example.test/removed", Arg.Any<DateTimeOffset>());
            db.DidNotReceive().GetDueVideos(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>());
        }

        [Fact]
        public async Task ResolveWaveAsync_SkipsUnconfiguredAndServesNext_WhenSecondCandidateIsConfigured()
        {
            // B-C2: the wave must not burn the whole pass on an unconfigured source when a
            // configured one is also due. Stamp the unconfigured one, then pick the next.
            const string unconfigured = "https://example.test/removed";
            const string configured = "https://example.test/active";

            var db = Substitute.For<IVodDatabase>();
            // First call returns the unconfigured source; second call returns the configured one.
            db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns(unconfigured, configured);
            db.GetDueVideos(configured, Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>())
              .Returns([]);
            db.GetSource(configured).Returns(new SourceRecord { Url = configured, Name = "Active" });
            db.GetVideosBySource(configured).Returns([]);

            var config = new PluginConfiguration
            {
                SeriesLibraryName = "VODs",
                Sources = [new SourceDefinition { Url = configured, Enabled = true, Mode = SourceMode.Series }],
            };

            var resolved = await Make(db).ResolveWaveAsync(config, "Youtube", CancellationToken.None);

            // The unconfigured source was stamped (and skipped).
            db.Received().SetLastResolvedAt(unconfigured, Arg.Any<DateTimeOffset>());
            // The configured source was eventually served (GetDueVideos called).
            db.Received().GetDueVideos(configured, Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>());
            Assert.Equal(0, resolved); // 0 because the due list was empty, but the source was served
        }

        [Fact]
        public async Task ResolveWaveAsync_PassesSkippedSet_ToSelectSourceOnSecondCall()
        {
            // N2: after skipping an unconfigured source, the skipped URL must be in the exclusion
            // set passed to the next SelectSourceToResolve call so the SQL filters it out and a
            // low-due-count disabled source does not permanently head the platform queue.
            const string unconfigured = "https://example.test/removed";
            const string configured = "https://example.test/active";

            var db = Substitute.For<IVodDatabase>();
            db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns(unconfigured, configured);
            db.GetDueVideos(configured, Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>()).Returns([]);
            db.GetSource(configured).Returns(new SourceRecord { Url = configured, Name = "Active" });
            db.GetVideosBySource(configured).Returns([]);

            var config = new PluginConfiguration
            {
                SeriesLibraryName = "VODs",
                Sources = [new SourceDefinition { Url = configured, Enabled = true, Mode = SourceMode.Series }],
            };

            await Make(db).ResolveWaveAsync(config, "Youtube", CancellationToken.None);

            // The second call must carry the skipped URL in the exclusion set.
            db.Received().SelectSourceToResolve(
                "Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(),
                Arg.Is<IReadOnlySet<string>?>(
                    s => s != null && s.Contains(unconfigured, StringComparer.OrdinalIgnoreCase)));
        }

        [Fact]
        public async Task ResolveWaveAsync_StampsAndSkips_ADisabledSource()
        {
            // B-I5: a disabled (paused) source must be treated identically to a removed one.
            // Its 300-video backlog must not consume the whole per-platform budget.
            // N2: after stamping and adding to the skipped set, the second SelectSourceToResolve
            // call passes the exclusion set; the SQL skips the disabled URL and returns null.
            const string disabledUrl = "https://example.test/disabled";
            var db = Substitute.For<IVodDatabase>();
            db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns(disabledUrl, (string?)null);

            var config = new PluginConfiguration
            {
                Sources = [new SourceDefinition { Url = disabledUrl, Enabled = false }],
            };

            var resolved = await Make(db).ResolveWaveAsync(config, "Youtube", CancellationToken.None);

            Assert.Equal(0, resolved);
            db.Received().SetLastResolvedAt(disabledUrl, Arg.Any<DateTimeOffset>());
            db.DidNotReceive().GetDueVideos(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>());
        }

        [Fact]
        public async Task ResolveWaveAsync_StampsLastResolvedAt_WhenPublicationThrows_AndNoProgressWasMade()
        {
            // N3: when a wave resolves zero videos and publication still throws (e.g. the library
            // is not yet indexed), the source must be STAMPED, not cleared.  Clearing would return
            // it to the never-served arm (due_count = 0), where it ranks first on every pass and
            // permanently blocks other sources on the platform lane.
            // Stamping moves it to the due-row arm so it competes fairly on due_count.
            //
            // The complementary branch — wave resolved > 0, publish throws → ClearLastResolvedAt
            // (one retry wave) — cannot be exercised without a real successful YtDlpService call.
            // That seam is deliberately deferred, so that branch is verified by reading only.
            const string sourceUrl = "https://example.test/fail-publish";

            var db = Substitute.For<IVodDatabase>();
            db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns(sourceUrl);
            db.GetDueVideos(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>())
              .Returns([]);
            // GetSource must return a non-null record with a name so PublishAsync does not bail
            // out before reaching GetVideosBySource.
            db.GetSource(sourceUrl)
              .Returns(new SourceRecord { Url = sourceUrl, Name = "Test Series" });
            // GetVideosBySource is the first call inside PublishAsync that can throw after the
            // early-exit guards; throwing here simulates any publication failure.
            db.GetVideosBySource(sourceUrl)
              .Throws(new InvalidOperationException("simulated publish failure"));

            var config = new PluginConfiguration
            {
                SeriesLibraryName = "VODs",
                Sources = [new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series }],
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Make(db).ResolveWaveAsync(config, "Youtube", CancellationToken.None));

            // No forward progress + publication failure → stamp, not clear (N3 fix).
            db.Received().SetLastResolvedAt(sourceUrl, Arg.Any<DateTimeOffset>());
            db.DidNotReceive().ClearLastResolvedAt(sourceUrl);
        }

        [Fact]
        public async Task ResolveWaveAsync_StampsLastResolvedAt_WhenPublicationSucceeds()
        {
            // B-I6 complement: when publication succeeds, the source is stamped normally so it
            // enters the due-row arm (not the never-served arm) on subsequent calls.
            const string sourceUrl = "https://example.test/ok-publish";

            var db = Substitute.For<IVodDatabase>();
            db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns(sourceUrl);
            db.GetDueVideos(sourceUrl, Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>())
              .Returns([]);
            db.GetSource(sourceUrl).Returns(new SourceRecord { Url = sourceUrl, Name = string.Empty });
            db.GetVideosBySource(sourceUrl).Returns([]);

            var config = new PluginConfiguration
            {
                SeriesLibraryName = "VODs",
                Sources = [new SourceDefinition { Url = sourceUrl, Enabled = true, Mode = SourceMode.Series }],
            };

            await Make(db).ResolveWaveAsync(config, "Youtube", CancellationToken.None);

            db.Received().SetLastResolvedAt(sourceUrl, Arg.Any<DateTimeOffset>());
            db.DidNotReceive().ClearLastResolvedAt(sourceUrl);
        }
    }

    /// <summary>
    /// The resolver's publication applies the stream description stored on a row to the items
    /// the reconcile created, and only to those (N2).
    /// </summary>
    [Collection(BaseItemStaticsCollection.Name)]
    public sealed class ResolutionServicePublicationTests : IDisposable
    {
        private const string LibraryName = "VODs";
        private const string SourceUrl = "https://example.test/publish";
        private const string VideoId = "3f9a2b1c-0000-0000-0000-0000000000b1";

        private readonly string _tempRoot =
            Path.Combine(Path.GetTempPath(), "vod-tests-resolution-publish-" + Guid.NewGuid().ToString("N"));

        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly ILibraryManager _describerLibrary = Substitute.For<ILibraryManager>();
        private readonly List<BaseItem> _published = [];

        // A resolved, numbered, described row: publishable as is, with nothing left to resolve.
        private readonly VideoRecord _row = new()
        {
            Id = VideoId,
            SourceUrl = SourceUrl,
            NativeId = "described",
            VideoUrl = "https://example.test/v/described",
            Title = "A described video",
            SyncedAt = DateTimeOffset.UnixEpoch,
            LiveStatus = LiveStatus.NotLive,
            ResolutionState = ResolutionState.Resolved,
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
            SeasonNumber = 2026,
            EpisodeNumber = 1013001,
            StreamDescriptions = StoredStreams.Serialize(new Dictionary<string, IReadOnlyList<MediaStream>>
            {
                [StoredStreams.PrimaryKey] = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 }],
            }),
        };

        public ResolutionServicePublicationTests()
        {
            _db.SelectSourceToResolve("Youtube", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>?>())
              .Returns(SourceUrl);
            _db.GetDueVideos(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<int>())
              .Returns([]);
            _db.GetSource(SourceUrl).Returns(new SourceRecord { Url = SourceUrl, Name = "Publish" });
            _db.GetVideosBySource(SourceUrl).Returns(_ => [_row]);
            _db.GetPublishableVideoIds().Returns([VideoId]);
            _describerLibrary.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        private static Guid DeterministicId(string key)
            => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

        private ResolutionService Make()
        {
            var location = Path.Combine(_tempRoot, LibraryName);
            Directory.CreateDirectory(location);

            _library.GetVirtualFolders()
                .Returns([new VirtualFolderInfo { Name = LibraryName, Locations = [location] }]);
            _library.FindByPath(location, true)
                .Returns(new Folder { Id = Guid.NewGuid(), Path = location });

            // A library that holds what CreateItem received, so a second wave finds what the first
            // one published, as it does in production.
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => _published.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(_ => _published.ToList());
            _library.GetItemById(Arg.Any<Guid>())
                .Returns(call => _published.FirstOrDefault(i => i.Id.Equals(call.Arg<Guid>())));

            // Publishing a movie goes through Folder.AddChild, which reaches this static.
            BaseItem.LibraryManager = _library;

            var publisher = new LibraryPublisher(
                _library, _db,
                new ArtworkStore(Path.Combine(_tempRoot, "artwork")),
                Substitute.For<IVodLocalizer>(),
                NullLogger<LibraryPublisher>.Instance);

            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                _describerLibrary,
                Substitute.For<IMediaStreamRepository>(),
                Substitute.For<IMediaEncoder>(),
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);

            return new ResolutionService(
                _db,
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                describer,
                publisher,
                new VideoDescriptionStep(Substitute.For<IHttpClientFactory>(), describer),
                NullLogger<ResolutionService>.Instance);
        }

        private Task WaveAsync()
            => Make().ResolveWaveAsync(
                new PluginConfiguration
                {
                    MoviesLibraryName = LibraryName,
                    LocalBaseUrl = "http://localhost:8096",
                    Sources = [new SourceDefinition { Url = SourceUrl, Enabled = true, Mode = SourceMode.Movies }],
                },
                "Youtube",
                CancellationToken.None);

        private void AssertApplied(bool expected)
        {
            // ApplyStored looks the video's members up by its VOD provider id; nothing else in a
            // wave with no due row queries the describer's library, so this query is the
            // application.
            var calls = _describerLibrary.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(ILibraryManager.GetItemList)
                    && c.GetArguments()[0] is InternalItemsQuery q
                    && q.HasAnyProviderId != null
                    && q.HasAnyProviderId.TryGetValue("VOD", out var id)
                    && id == VideoId);
            Assert.Equal(expected ? 1 : 0, calls);
        }

        [Fact]
        public async Task ThePublication_AppliesTheStoredDescription_ToTheItemsItCreated()
        {
            await WaveAsync();

            Assert.NotEmpty(_published);
            AssertApplied(expected: true);
        }

        [Fact]
        public async Task ThePublication_DoesNotApplyTheStoredDescriptionAgain_ToAnUnchangedRow()
        {
            await WaveAsync();
            _describerLibrary.ClearReceivedCalls();

            await WaveAsync();

            AssertApplied(expected: false);
        }

        [Fact]
        public async Task ThePublication_StillCompletes_WhenApplyingADescriptionThrows()
        {
            // A description is an optimisation: the playback failsafe covers a member left
            // undescribed. Failing to write one must not abort a publication whose reconcile
            // already succeeded — the anchor would go unrecorded and the wave would be retried.
            _describerLibrary.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(_ => throw new InvalidOperationException("library unavailable"));

            await WaveAsync();

            _db.Received().UpsertSource(Arg.Is<SourceRecord>(s => s.PublishedAnchorId != null));
            _db.Received().SetLastResolvedAt(SourceUrl, Arg.Any<DateTimeOffset>());
        }

        [Fact]
        public async Task ThePublication_RecordsTheModeItPublishedIn()
        {
            // What a later sync compares the configuration against: left unrecorded, flipping the
            // source's mode would never be noticed and the library would keep the old shape.
            await WaveAsync();

            _db.Received().UpsertSource(
                Arg.Is<SourceRecord>(s => s.PublishedMode == SourceMode.Movies));
        }
    }

    public sealed class ResolutionServicePrepareTests : IDisposable
    {
        private const string Source = "https://example.test/s";
        private static readonly DateTimeOffset Day = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        private readonly string _path = Path.GetTempFileName();
        private readonly VodDatabase _db;

        public ResolutionServicePrepareTests()
        {
            _db = new VodDatabase(_path, NullLogger<VodDatabase>.Instance);
            _db.EnsureCreated();
            _db.UpsertSource(new SourceRecord { Url = Source });
        }

        public void Dispose()
        {
            _db.Dispose();
            if (File.Exists(_path))
                File.Delete(_path);
        }

        private void Seed(string id, ResolutionState state, DateTimeOffset? publishedAt)
            => _db.UpsertVideo(new VideoRecord
            {
                Id = id,
                SourceUrl = Source,
                VideoUrl = "https://example.test/v/" + id,
                NativeId = id,
                SyncedAt = DateTimeOffset.UnixEpoch,
                ResolutionState = state,
                PublishedAt = publishedAt,
            });

        [Fact]
        public void PrepareForPublication_NumbersNothing_WhileTheSourceIsNotSettled()
        {
            Seed("a", ResolutionState.Resolved, Day.AddHours(8));
            Seed("b", ResolutionState.Pending, null);

            var publishable = ResolutionService.PrepareForPublication(_db, Source, Day.AddDays(1), 0);

            Assert.Empty(publishable);
            Assert.All(_db.GetVideosBySource(Source), v => Assert.Null(v.EpisodeNumber));
        }

        [Fact]
        public void PrepareForPublication_PersistsChronologicalNumbers_OnceSettled()
        {
            Seed("evening", ResolutionState.Resolved, Day.AddHours(20));
            Seed("morning", ResolutionState.Resolved, Day.AddHours(8));

            var publishable = ResolutionService.PrepareForPublication(_db, Source, Day.AddDays(1), 0);

            Assert.Equal(2, publishable.Count);
            var stored = _db.GetVideosBySource(Source).ToDictionary(v => v.Id);
            Assert.Equal(1013001, stored["morning"].EpisodeNumber);
            Assert.Equal(1013002, stored["evening"].EpisodeNumber);
        }
    }

    public sealed class ResolutionServiceFinishTests
    {
        private const string Master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            https://cdn.test/v-1080.m3u8
            """;

        private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }

        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly IMediaEncoder _encoder = Substitute.For<IMediaEncoder>();

        private ResolutionService Make()
        {
            var library = Substitute.For<ILibraryManager>();
            library.GetVirtualFolders().Returns([]);
            var publisher = new LibraryPublisher(
                library, _db,
                new ArtworkStore(Path.Combine(Path.GetTempPath(), "vod-tests-finish-resolution")),
                Substitute.For<IVodLocalizer>(),
                NullLogger<LibraryPublisher>.Instance);

            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                Substitute.For<ILibraryManager>(),
                Substitute.For<IMediaStreamRepository>(),
                _encoder,
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>())
                .Returns(_ => new HttpClient(new StubHandler(HttpStatusCode.OK, Master)));

            return new ResolutionService(
                _db,
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                describer,
                publisher,
                new VideoDescriptionStep(factory, describer),
                NullLogger<ResolutionService>.Instance);
        }

        [Fact]
        public async Task TheFilledRowIsStored_BeforeItsStreamsAreProbed()
        {
            // I1: the describer probes through our own stream route, which reads the row from the
            // database and refuses a scheduled stream. An aired premiere was listed Upcoming; unless
            // its fresh NotLive status is stored before the probe, every first description fails.
            var events = new List<string>();
            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
                .Do(c =>
                {
                    var row = c.Arg<VideoRecord>();
                    events.Add($"upsert {row.ResolutionState} {row.LiveStatus}");
                });
            _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    events.Add("probe");
                    return Task.FromResult(new MediaInfo
                    {
                        MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 }],
                    });
                });

            var video = new VideoRecord
            {
                Id = "3f9a2b1c-0000-0000-0000-000000000003",
                SourceUrl = "https://example.test/s",
                VideoUrl = "https://example.test/v",
                SyncedAt = DateTimeOffset.UnixEpoch,
                LiveStatus = LiveStatus.NotLive,
                ResolutionState = ResolutionState.Pending,
                PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
                Extras = new VideoExtras { Qualities = [new VideoQuality(1080, "h264", 30)] },
            };

            var resolved = await Make().FinishResolutionAsync(
                new PluginConfiguration { LocalBaseUrl = "http://localhost:8096" },
                video, "https://cdn.test/m.m3u8", 0, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.True(resolved);
            Assert.Equal("upsert Pending NotLive", events[0]);
            Assert.Contains("probe", events);
            Assert.Equal("upsert Resolved NotLive", events[^1]);
        }
    }
}

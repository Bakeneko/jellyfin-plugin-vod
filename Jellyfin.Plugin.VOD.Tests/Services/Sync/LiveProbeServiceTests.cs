using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class LiveProbeServiceTests
    {
        private static VideoMetadata Live(string id, string extractor = "TwitchVod")
            => new() { Id = id, ExtractorKey = extractor, Title = "A stream", LiveStatus = LiveStatus.Live };

        [Fact]
        public async Task ProbeTwitch_PublishesTheInProgressVod_WhenItIsReachable()
        {
            // The in-progress VOD plays from the start of the broadcast and carries no ads, so it is
            // preferred over the channel's own stream whenever it can be read (spec F7).
            var outcome = await LiveProbe.TwitchAsync(
                "https://www.twitch.tv/otplol_/videos",
                root: _ => Task.FromResult(MetadataResult.From(Live("317890898530", "TwitchStream"))),
                firstEntry: _ => Task.FromResult<VideoMetadata?>(Live("2890554613")),
                ct: CancellationToken.None);

            Assert.True(outcome.Succeeded);
            var found = Assert.Single(outcome.Found);
            Assert.Equal("2890554613", found.Id);
        }

        [Fact]
        public async Task ProbeTwitch_PublishesOnlyTheChannelStream_ForALiveOnlySource()
        {
            // A channel-root source declares "the live, nothing else", and the sync publishes the
            // channel stream for it. Preferring the in-progress VOD here published a SECOND item for
            // the same broadcast, measured 2026-10-08 — and the VOD one stuttered at every segment
            // while the stream played smoothly. The VOD is not even asked for: it is a yt-dlp call
            // whose answer this source form never uses.
            var vodAsked = false;
            var outcome = await LiveProbe.TwitchAsync(
                "https://www.twitch.tv/otplol_",
                root: _ => Task.FromResult(MetadataResult.From(Live("317890898530", "TwitchStream"))),
                firstEntry: _ => { vodAsked = true; return Task.FromResult<VideoMetadata?>(Live("2890554613")); },
                ct: CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Equal("317890898530", Assert.Single(outcome.Found).Id);
            Assert.False(vodAsked);
        }

        [Fact]
        public async Task ProbeTwitch_FallsBackToTheChannelStream_WhenTheVodIsSubscriberOnly()
        {
            // Measured: without a subscriber account every VOD call fails (spec F2), while the root
            // answers for everyone (F5). Falling back costs the start of the broadcast and brings
            // ads, which beats publishing nothing.
            var outcome = await LiveProbe.TwitchAsync(
                "https://www.twitch.tv/otplol_/videos",
                root: _ => Task.FromResult(MetadataResult.From(Live("317890898530", "TwitchStream"))),
                firstEntry: _ => Task.FromResult<VideoMetadata?>(null),
                ct: CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Equal("317890898530", Assert.Single(outcome.Found).Id);
        }

        [Fact]
        public async Task ProbeTwitch_FindsNothingAndSucceeds_WhenTheChannelIsOffAir()
        {
            // Measured 2026-10-04: an off-air channel makes yt-dlp exit 1 with "The channel is not
            // currently live" and print no JSON, so there is NO metadata and no NotLive status to
            // read — the state the previous version of this test modelled is one Twitch never
            // produces. Off air is still an answer, not a failure: the pass concluded, it found no
            // live, and it must not spend the second call.
            var secondCallMade = false;
            var outcome = await LiveProbe.TwitchAsync(
                "https://www.twitch.tv/otplol_",
                root: _ => Task.FromResult(MetadataResult.ChannelOffAir),
                firstEntry: _ => { secondCallMade = true; return Task.FromResult<VideoMetadata?>(null); },
                ct: CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Empty(outcome.Found);
            Assert.False(secondCallMade);
        }

        [Fact]
        public async Task ProbeTwitch_DiscardsTheQueryString_WhenDerivingTheChannelRoot()
        {
            // "/videos?filter=archives" is a form the detector accepts. Trimming only a trailing
            // slash left the query in place, so the root call landed on the archive playlist — which
            // had not finished after 120 s against a 60 s timeout — and the second call was built as
            // ".../videos?filter=archives/videos".
            string? askedRoot = null;
            string? askedFirstEntry = null;

            await LiveProbe.TwitchAsync(
                "https://www.twitch.tv/otplol_/videos?filter=archives",
                root: url =>
                {
                    askedRoot = url;
                    return Task.FromResult(MetadataResult.From(Live("317890898530", "TwitchStream")));
                },
                firstEntry: url =>
                {
                    askedFirstEntry = url;
                    return Task.FromResult<VideoMetadata?>(Live("2890554613"));
                },
                ct: CancellationToken.None);

            Assert.Equal("https://www.twitch.tv/otplol_", askedRoot);
            Assert.Equal("https://www.twitch.tv/otplol_/videos", askedFirstEntry);
        }

        [Fact]
        public async Task ProbeTwitch_ReportsFailure_WhenTheRootCallFails()
        {
            // A failed call is not an answer. Treating it as "not live" would retract a broadcast
            // that is still running, every time yt-dlp is rate-limited.
            var outcome = await LiveProbe.TwitchAsync(
                "https://www.twitch.tv/otplol_",
                root: _ => Task.FromResult(MetadataResult.Failed),
                firstEntry: _ => Task.FromResult<VideoMetadata?>(null),
                ct: CancellationToken.None);

            Assert.False(outcome.Succeeded);
            Assert.Empty(outcome.Found);
        }

        [Fact]
        public async Task ProbeTwitch_ReportsFailure_WhenTheAnswerDidNotComeFromTwitch()
        {
            // Only Twitch may conclude "nothing is broadcasting on Twitch". A host that merely looks
            // like Twitch answers through yt-dlp's generic extractor, with no live_status at all, and
            // the "nothing live" that used to follow deleted every item of the source — every pass.
            var outcome = await LiveProbe.TwitchAsync(
                "https://mytwitch.tv/foo",
                root: _ => Task.FromResult(MetadataResult.From(
                    new VideoMetadata { Id = "page", Title = "A web page", ExtractorKey = "generic" })),
                firstEntry: _ => Task.FromResult<VideoMetadata?>(null),
                ct: CancellationToken.None);

            Assert.False(outcome.Succeeded);
            Assert.Empty(outcome.Found);
        }

        [Fact]
        public async Task ProbeYoutube_ReturnsLiveAndUpcomingEntriesFromTheFlatListing()
        {
            // The flat YouTube listing carries live_status per entry, so no detail call is needed to
            // know what is live (spec F13).
            var listing = new PlaylistInfo
            {
                Entries =
                [
                    new PlaylistEntry { Id = "up1", LiveStatus = LiveStatus.Upcoming, VideoUrl = "https://y/up1" },
                    new PlaylistEntry { Id = "lv1", LiveStatus = LiveStatus.Live, VideoUrl = "https://y/lv1" },
                    new PlaylistEntry { Id = "old", LiveStatus = LiveStatus.NotLive, VideoUrl = "https://y/old" }
                ]
            };

            var outcome = await LiveProbe.YoutubeAsync(
                "https://www.youtube.com/@OTP_VODs/streams",
                listing: _ => Task.FromResult<PlaylistInfo?>(listing),
                detail: url => Task.FromResult<VideoMetadata?>(
                    new VideoMetadata { Id = url.EndsWith("lv1", StringComparison.Ordinal) ? "lv1" : "up1", Title = "t", LiveStatus = LiveStatus.Live }),
                ct: CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Equal(2, outcome.Found.Count);
        }

        [Fact]
        public async Task ProbeYoutube_ReportsFailure_WhenTheListingCallFails()
        {
            var outcome = await LiveProbe.YoutubeAsync(
                "https://www.youtube.com/@OTP_VODs/streams",
                listing: _ => Task.FromResult<PlaylistInfo?>(null),
                detail: _ => Task.FromResult<VideoMetadata?>(null),
                ct: CancellationToken.None);

            Assert.False(outcome.Succeeded);
        }

        // ── Deletion for TwitchChannelLive ────────────────────────────────────────────────────────

        [Fact]
        public async Task ProbeSourceCoreAsync_TwitchChannelLive_DeletesFollowedVideos_WhenProbeSucceedsWithNothingLive()
        {
            // TwitchChannelLive is live-only: when the channel-root probe reports the channel is
            // off air (succeeded, nothing found), every video the database was following is stale
            // and must be removed. Leaving them in would keep phantom replay entries until the next
            // full sync, which may be 12 hours away.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/testchannel";
            var sourceRecord = new SourceRecord { Url = sourceUrl, Name = "Test Channel" };
            var trackedId = Guid.NewGuid().ToString();
            var trackedVideo = new VideoRecord
            {
                Id = trackedId,
                SourceUrl = sourceUrl,
                VideoUrl = sourceUrl,
                LiveStatus = LiveStatus.Live
            };

            db.GetVideosBySource(sourceUrl).Returns([trackedVideo]);
            db.GetAllVideoIds().Returns([trackedId]);

            var service = MakeService(db);
            // Empty library name → reconcile step is skipped, avoiding LibraryManager setup.
            var config = new PluginConfiguration { SeriesLibraryName = string.Empty };
            var source = new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series };

            await service.ProbeSourceCoreAsync(
                config, source, SourceType.TwitchChannelLive, sourceRecord,
                ProbeOutcome.Nothing, CancellationToken.None);

            db.Received(1).DeleteVideo(trackedId);
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_TwitchChannelLive_KeepsOnlyTheBroadcastTheRootReports()
        {
            // Measured 2026-10-08: otplol_'s broadcast restarted at 13:40 under a new stream id, and
            // the morning's row stayed live — refreshing it meant asking the channel root, which says
            // "live" about whatever the channel airs now. A successful root answer is complete: the
            // channel airs THIS stream, so every other row of a live-only source is a past broadcast
            // (or the VOD an earlier build preferred) and goes.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/otplol_";
            VideoRecord Row(string id, string nativeId) => new()
            {
                Id = id,
                SourceUrl = sourceUrl,
                VideoUrl = sourceUrl,
                NativeId = nativeId,
                LiveStatus = LiveStatus.Live
            };
            db.GetVideosBySource(sourceUrl).Returns(
            [
                Row("morning", "317938934370"),
                Row("vod", "v2895133300"),
                Row("current", "317940573282")
            ]);
            db.GetAllVideoIds().Returns(["morning", "vod", "current"]);

            await MakeService(db).ProbeSourceCoreAsync(
                new PluginConfiguration { SeriesLibraryName = string.Empty, MoviesLibraryName = string.Empty },
                new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Movies },
                SourceType.TwitchChannelLive,
                new SourceRecord { Url = sourceUrl, Name = "otplol_" },
                new ProbeOutcome(true, [Live("317940573282", "TwitchStream")]),
                CancellationToken.None);

            db.Received(1).DeleteVideo("morning");
            db.Received(1).DeleteVideo("vod");
            db.DidNotReceive().DeleteVideo("current");
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_TwitchChannelLive_DeletesEveryRowOrNone_WhenCancelled()
        {
            // The reconcile is what removes the library items the rows stood for. Throwing between
            // two deletions dropped rows while their items stayed, so the deletion commits as a
            // whole or not at all.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/testchannel";
            var tracked = new[] { "a", "b", "c" }
                .Select(n => new VideoRecord
                {
                    Id = n,
                    SourceUrl = sourceUrl,
                    VideoUrl = sourceUrl,
                    LiveStatus = LiveStatus.Live
                })
                .ToArray();

            db.GetVideosBySource(sourceUrl).Returns(tracked);
            db.GetAllVideoIds().Returns(["a", "b", "c"]);

            using var cts = new CancellationTokenSource();
            db.When(d => d.DeleteVideo(Arg.Any<string>())).Do(_ => cts.Cancel());

            await MakeService(db).ProbeSourceCoreAsync(
                new PluginConfiguration { SeriesLibraryName = string.Empty },
                new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series },
                SourceType.TwitchChannelLive,
                new SourceRecord { Url = sourceUrl, Name = "Test Channel" },
                ProbeOutcome.Nothing,
                cts.Token);

            Assert.Equal(3, db.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IVodDatabase.DeleteVideo)));
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_TwitchChannelLive_DeletesNothing_WhenTheProbeFails()
        {
            // A failed probe is not an answer: null from yt-dlp could be a rate limit or a network
            // blip. Treating it as "not live" would retract a broadcast that is still running.
            // This is the single most important behaviour in the probe: failure must change nothing.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/testchannel";
            var sourceRecord = new SourceRecord { Url = sourceUrl, Name = "Test Channel" };

            var service = MakeService(db);
            var config = new PluginConfiguration { SeriesLibraryName = string.Empty };
            var source = new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series };

            await service.ProbeSourceCoreAsync(
                config, source, SourceType.TwitchChannelLive, sourceRecord,
                ProbeOutcome.Failed, CancellationToken.None);

            db.DidNotReceive().DeleteVideo(Arg.Any<string>());
        }

        // ── Episode numbering of a probe-created video ────────────────────────────────────────────

        [Fact]
        public async Task ProbeSourceCoreAsync_NumbersTheVideoItCreates()
        {
            // Without a number the item lands in Specials and is renumbered up to 12 hours later by
            // the next sync. That is not cosmetic: an episode's user-data key is built from its
            // season and episode numbers, so being renumbered loses the watch state.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/otplol_/videos";
            db.GetVideosBySource(sourceUrl).Returns([]);
            db.GetAllVideoIds().Returns([]);

            VideoRecord? stored = null;
            db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
              .Do(c => stored = c.Arg<VideoRecord>());

            var meta = new VideoMetadata
            {
                Id = "2890554613",
                ExtractorKey = "TwitchVod",
                Title = "A stream",
                FullTitle = "A stream",
                UploadDate = "20261004",
                LiveStatus = LiveStatus.Live
            };

            await MakeService(db).ProbeSourceCoreAsync(
                new PluginConfiguration { SeriesLibraryName = string.Empty },
                new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series },
                SourceType.TwitchVideos,
                new SourceRecord { Url = sourceUrl, Name = "otplol_" },
                new ProbeOutcome(true, [meta]),
                CancellationToken.None);

            Assert.NotNull(stored);
            Assert.Equal(2026, stored!.SeasonNumber);
            Assert.Equal(1004001, stored.EpisodeNumber);
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_UpsertsARepeatedLiveEntryOnce()
        {
            // A flat listing can return the same video twice — observed in production on a YouTube
            // playlist. YoutubeAsync fetches detail per entry, so the pass then carries the same
            // metadata twice. Reading the existing rows once, before the loop, the second copy
            // found nothing in that snapshot and minted a second Guid for the same native id: two
            // rows before the UNIQUE index of schema v7, and a UNIQUE violation that aborts the
            // whole probe pass after it — so the broadcast is not published until the next pass.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.youtube.com/playlist?list=PLdup";
            db.GetVideosBySource(sourceUrl).Returns([]);
            db.GetAllVideoIds().Returns([]);

            var stored = new List<VideoRecord>();
            db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
              .Do(c => stored.Add(c.Arg<VideoRecord>()));

            var meta = new VideoMetadata
            {
                Id = "vdc4kRhxEzY",
                ExtractorKey = "Youtube",
                Title = "A broadcast",
                FullTitle = "A broadcast",
                UploadDate = "20261012",
                LiveStatus = LiveStatus.Live
            };

            await MakeService(db).ProbeSourceCoreAsync(
                new PluginConfiguration { SeriesLibraryName = string.Empty },
                new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series },
                SourceType.YoutubePlaylist,
                new SourceRecord { Url = sourceUrl, Name = "A playlist" },
                new ProbeOutcome(true, [meta, meta]),
                CancellationToken.None);

            Assert.Single(stored);
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_DoesNotSpendADayRank_OnABroadcastItAlreadyNumbered()
        {
            // A pass re-finds a broadcast it numbered earlier and finds a second, new one. The
            // tally is seeded from the stored rows, so advancing it again for the already-numbered
            // one pushes the new broadcast a rank further than the sync would have given it — and
            // nothing renumbers it afterwards, because the date-based branch never overwrites a
            // number a row already carries.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.youtube.com/@live/videos";
            db.GetVideosBySource(sourceUrl).Returns(
            [
                new VideoRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceUrl = sourceUrl,
                    VideoUrl = sourceUrl,
                    NativeId = "first",
                    LiveStatus = LiveStatus.Live,
                    PublishedAt = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero),
                    SeasonNumber = 2026,
                    EpisodeNumber = 1012001
                }
            ]);
            db.GetAllVideoIds().Returns([]);

            var saved = new List<VideoRecord>();
            db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
              .Do(c => saved.Add(c.Arg<VideoRecord>()));

            VideoMetadata Live(string id, string title) => new VideoMetadata
            {
                Id = id,
                ExtractorKey = "Youtube",
                Title = title,
                FullTitle = title,
                UploadDate = "20261012",
                LiveStatus = LiveStatus.Live
            };

            await MakeService(db).ProbeSourceCoreAsync(
                new PluginConfiguration { SeriesLibraryName = string.Empty },
                new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series },
                SourceType.YoutubeChannel,
                new SourceRecord { Url = sourceUrl, Name = "A channel" },
                new ProbeOutcome(true, [Live("first", "Still airing"), Live("second", "A new one")]),
                CancellationToken.None);

            Assert.Equal(1012001, saved[0].EpisodeNumber);
            Assert.Equal(1012002, saved[1].EpisodeNumber);
        }

        [Fact]
        public async Task ProbeSourceCoreAsync_ContinuesTheDaysRanking_LikeTheSyncWould()
        {
            // One numbering rule, shared with the sync: the rank continues after the videos already
            // numbered on that UTC day, so the number the probe gives is the number the sync would
            // have given and no later pass has cause to change it.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/otplol_/videos";
            db.GetVideosBySource(sourceUrl).Returns(
            [
                new VideoRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceUrl = sourceUrl,
                    VideoUrl = sourceUrl,
                    NativeId = "2890000000",
                    PublishedAt = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
                    SeasonNumber = 2026,
                    EpisodeNumber = 1004001
                }
            ]);
            db.GetAllVideoIds().Returns([]);

            VideoRecord? stored = null;
            db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
              .Do(c => stored = c.Arg<VideoRecord>());

            var meta = new VideoMetadata
            {
                Id = "2890554613",
                ExtractorKey = "TwitchVod",
                Title = "A later stream",
                FullTitle = "A later stream",
                UploadDate = "20261004",
                LiveStatus = LiveStatus.Live
            };

            await MakeService(db).ProbeSourceCoreAsync(
                new PluginConfiguration { SeriesLibraryName = string.Empty },
                new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series },
                SourceType.TwitchVideos,
                new SourceRecord { Url = sourceUrl, Name = "otplol_" },
                new ProbeOutcome(true, [meta]),
                CancellationToken.None);

            Assert.Equal(1004002, stored!.EpisodeNumber);
        }

        // ── The gates in ProbeSourceAsync, before any yt-dlp call ─────────────────────────────────

        [Fact]
        public async Task ProbeSourceAsync_ProbesALiveOnlySource_ThatNoSyncHasEverNamed()
        {
            // A twitch.tv/<channel> source added while the channel is off air can never sync: the
            // flat playlist call fails too, so SourceRecord.Name is never written. Requiring a
            // stored name meant every pass skipped the source and the live never appeared, which is
            // the one thing this source form exists to do.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/riotgames";
            db.GetSource(sourceUrl).Returns((SourceRecord?)null);
            db.GetVideosBySource(sourceUrl).Returns([]);
            db.GetAllVideoIds().Returns([]);

            SourceType? probedType = null;
            var config = new PluginConfiguration { MoviesLibraryName = string.Empty };
            var source = new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Movies };

            await MakeService(db).ProbeSourceAsync(
                config,
                source,
                (_, type, _) =>
                {
                    probedType = type;
                    return Task.FromResult(ProbeOutcome.Nothing);
                },
                CancellationToken.None);

            Assert.Equal(SourceType.TwitchChannelLive, probedType);
            db.Received().UpsertSource(Arg.Is<SourceRecord>(r => r.Name == "riotgames"));
        }

        [Fact]
        public async Task ProbeSourceAsync_KeepsTheStoredName_WhenASyncAlreadyWroteOne()
        {
            // The name the sync published is what the library items hang from; the URL-derived
            // fallback must never take it over.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.twitch.tv/riotgames/videos";
            db.GetSource(sourceUrl).Returns(new SourceRecord { Url = sourceUrl, Name = "Riot Games" });
            db.GetVideosBySource(sourceUrl).Returns([]);
            db.GetAllVideoIds().Returns([]);

            SourceType? probedType = null;
            var config = new PluginConfiguration { SeriesLibraryName = string.Empty };
            var source = new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series };

            await MakeService(db).ProbeSourceAsync(
                config,
                source,
                (_, type, _) =>
                {
                    probedType = type;
                    return Task.FromResult(ProbeOutcome.Nothing);
                },
                CancellationToken.None);

            Assert.Equal(SourceType.TwitchVideos, probedType);
            db.Received().UpsertSource(Arg.Is<SourceRecord>(r => r.Name == "Riot Games"));
        }

        [Fact]
        public async Task ProbeSourceAsync_SpendsNoCall_OnASourceWhosePlatformListsNoBroadcast()
        {
            // Arte and france.tv never broadcast, so probing them would spend one yt-dlp process per
            // source per pass to learn nothing.
            var db = Substitute.For<IVodDatabase>();
            const string sourceUrl = "https://www.arte.tv/fr/videos/RC-027708/happy-valley/";
            var probed = false;

            await MakeService(db).ProbeSourceAsync(
                new PluginConfiguration(),
                new SourceDefinition { Url = sourceUrl, Mode = SourceMode.Series },
                (_, _, _) =>
                {
                    probed = true;
                    return Task.FromResult(ProbeOutcome.Nothing);
                },
                CancellationToken.None);

            Assert.False(probed);
            db.DidNotReceive().GetSource(Arg.Any<string>());
        }

        [Fact]
        public async Task ProbeSourceAsync_SpendsNoCall_OnALookalikeTwitchHost()
        {
            // "mytwitch.tv" is not Twitch. It used to detect as TwitchChannelLive, which admitted it
            // to the probe and, through a generic extraction with no live status, to the deletion
            // branch — every five minutes.
            var db = Substitute.For<IVodDatabase>();
            var probed = false;

            await MakeService(db).ProbeSourceAsync(
                new PluginConfiguration(),
                new SourceDefinition { Url = "https://mytwitch.tv/riotgames", Mode = SourceMode.Movies },
                (_, _, _) =>
                {
                    probed = true;
                    return Task.FromResult(ProbeOutcome.Nothing);
                },
                CancellationToken.None);

            Assert.False(probed);
        }

        [Theory]
        [InlineData("https://www.twitch.tv/otplol_", true)]
        [InlineData("https://www.twitch.tv/otplol_/videos", true)]
        [InlineData("https://www.youtube.com/@OTP_VODs/streams", false)]
        public void IsTwitchStrategy_RoutesEachProbeableSourceKind(string url, bool twitch)
        {
            // Every source SupportsLiveProbe admits must reach exactly one strategy.
            Assert.True(PlatformRules.SupportsLiveProbe(url));
            Assert.Equal(twitch, LiveProbeService.IsTwitchStrategy(SourceTypeDetector.Detect(url)));
        }

        private static LiveProbeService MakeService(IVodDatabase db)
        {
            var library = Substitute.For<ILibraryManager>();
            library.GetVirtualFolders().Returns([]);

            var publisher = new LibraryPublisher(
                library,
                db,
                new ArtworkStore(Path.Combine(Path.GetTempPath(), "vod-tests-probe")),
                Substitute.For<IVodLocalizer>(),
                NullLogger<LibraryPublisher>.Instance);

            return new LiveProbeService(
                new YtDlpService(
                    new CookieJar(NullLogger<CookieJar>.Instance),
                    ThrottleClock.Throttle(),
                    NullLogger<YtDlpService>.Instance),
                db,
                publisher,
                NullLogger<LiveProbeService>.Instance);
        }
    }
}

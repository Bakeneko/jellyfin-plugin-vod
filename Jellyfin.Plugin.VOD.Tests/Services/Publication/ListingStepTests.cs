using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The listing step: the only step that enumerates a source, and the one all other steps depend
    /// on — the cleanup reads CleanupAllowed, the wave reads the staleness mark, the checkup
    /// publishes what we ingested. Each test below pins one invariant that, if broken, would
    /// either corrupt the pass or lose data permanently (watch state, library items).
    /// </summary>
    public sealed class ListingStepTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        private const string SourceUrl = "https://www.youtube.com/@testchan/videos";

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A YouTube playlist with two entries. Trimmed from real yt-dlp output: id, url, title,
        /// ie_key, duration are what a YouTube flat listing actually returns.
        /// </summary>
        /// <summary>A flat listing of <paramref name="count"/> entries, ids <c>listed-0…</c>.</summary>
        private static PlaylistInfo PlaylistOf(int count)
        {
            var entries = new List<PlaylistEntry>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(new PlaylistEntry
                {
                    Id = "listed-" + i,
                    VideoUrl = "https://www.youtube.com/watch?v=listed-" + i,
                    Title = "Video " + i,
                    IeKey = "Youtube",
                });
            }

            return new PlaylistInfo { Title = "Test Channel", Entries = entries };
        }

        private static PlaylistInfo TwoEntryYoutubePlaylist(string title0 = "Video One", string title1 = "Video Two")
            => new PlaylistInfo
            {
                Title = "Test Channel",
                ThumbnailUrl = "https://cdn.yt/thumb.jpg",
                BannerUrl = "https://cdn.yt/banner.jpg",
                Tags = ["gaming", "tech"],
                Description = "A test channel",
                PlaylistCount = null,
                Entries =
                [
                    new PlaylistEntry
                    {
                        Id = "PM6lMoClzy8",
                        VideoUrl = "https://www.youtube.com/watch?v=PM6lMoClzy8",
                        Title = title0,
                        IeKey = "Youtube",
                        Duration = 1169,
                    },
                    new PlaylistEntry
                    {
                        Id = "dQw4w9WgXcQ",
                        VideoUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                        Title = title1,
                        IeKey = "Youtube",
                        Duration = 212,
                    },
                ],
            };

        /// <summary>
        /// Arte entries carry exactly four keys and no title or id, measured 2026-08-19. This
        /// fixture is built from the shape documented in PlaylistInfoEntriesTests.
        /// </summary>
        private static PlaylistInfo OneArteEntry()
            => PlaylistInfo.ParseJson("""
            {
              "_type": "playlist",
              "title": "Les idées larges",
              "entries": [
                {
                  "ie_key": "ArteTV",
                  "_type": "url",
                  "url": "https://www.arte.tv/fr/videos/130515-005-A/pourquoi-eclate-t-on-en-sanglots/",
                  "__x_forwarded_for_ip": null
                }
              ]
            }
            """)!;

        private static PlaylistInfo EmptyPlaylist()
            => new PlaylistInfo { Title = "Empty", Entries = [] };

        private static PluginConfiguration Config(string? lang = null)
            => new()
            {
                PreferredMetadataLanguage = lang ?? string.Empty,
            };

        private static SourceDefinition Source(
            int maxVideosPerSource = 100,
            string? name = null)
            => new()
            {
                Url = SourceUrl,
                Enabled = true,
                MaxVideosPerSource = maxVideosPerSource,
                Name = name ?? string.Empty,
            };

        private static PlannedListing ShortPlanned(int maxVideosPerSource = 100, string? name = null)
            => new(Source(maxVideosPerSource, name), Full: false, ListingReason.ShortListingDue);

        /// <summary>
        /// A short listing for a source that HAS a retention window configured.
        /// </summary>
        /// <remarks>
        /// The default fixtures leave <c>RetentionDays</c> at zero, which makes any assertion that
        /// no date cutoff was passed unfalsifiable: a cutoff derived from a retention of zero is
        /// null too. Verified by mutation — deriving a cutoff from retention left the whole suite
        /// green until this fixture existed.
        /// </remarks>
        private static PlannedListing ShortPlannedWithRetention(
            int maxVideosPerSource, int retentionDays)
        {
            var source = Source(maxVideosPerSource);
            source.RetentionDays = retentionDays;
            return new PlannedListing(source, Full: false, ListingReason.ShortListingDue);
        }

        private static PlannedListing FullPlanned(int maxVideosPerSource = 100, string? name = null)
            => new(Source(maxVideosPerSource, name), Full: true, ListingReason.FullListingDue);

        private static VideoRecord KnownVideo(string nativeId, string? listingTitle = null, DateTimeOffset? metadataAt = null)
            => new()
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = SourceUrl,
                NativeId = nativeId,
                ExtractorKey = "Youtube",
                VideoUrl = "https://www.youtube.com/watch?v=" + nativeId,
                Title = listingTitle ?? "Stored Title",
                ListingTitle = listingTitle,
                MetadataAt = metadataAt,
                MetadataResult = metadataAt.HasValue ? CallOutcome.Ok : CallOutcome.None,
                Extras = metadataAt.HasValue
                    ? new VideoExtras { Qualities = [new VideoQuality(1080, "h264", null)] }
                    : new VideoExtras(),
            };

        private ListingStep Make(IVodDatabase db, IYtDlpService ytDlp)
            => new(ytDlp, db, NullLogger<ListingStep>.Instance);

        // ── Entry cap sent to the seam ────────────────────────────────────────────────────────

        [Fact]
        public async Task ShortListing_RequestsExactly30Entries()
        {
            // One YouTube page is 30 entries. Requesting exactly 30 means one HTTP round-trip;
            // requesting more would pay for a second page on the short path, defeating its purpose.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, 30, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>()).Returns(TwoEntryYoutubePlaylist());

            await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            await ytDlp.Received(1).GetPlaylistEntriesAsync(
                SourceUrl, 30, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>());
        }

        [Fact]
        public async Task FullListing_RequestsMaxVideosPerSource()
        {
            // MaxVideosPerSource is the operator's cap. Requesting any other number violates the
            // intent: too few means stale videos escape detection; too many wastes yt-dlp quota.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, 200, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>()).Returns(TwoEntryYoutubePlaylist());

            await Make(db, ytDlp).RunAsync(FullPlanned(200), Config(), Now, default);

            await ytDlp.Received(1).GetPlaylistEntriesAsync(
                SourceUrl, 200, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>());
        }

        [Fact]
        public async Task FullListing_WhenMaxVideosIsZero_RequestsNull()
        {
            // Zero means unlimited throughout this project. Passing zero to yt-dlp's
            // --playlist-end would fetch nothing; null instructs it to fetch everything.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, (int?)null, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>()).Returns(TwoEntryYoutubePlaylist());

            await Make(db, ytDlp).RunAsync(FullPlanned(0), Config(), Now, default);

            await ytDlp.Received(1).GetPlaylistEntriesAsync(
                SourceUrl, (int?)null, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>());
        }

        // ── Metadata language handling ─────────────────────────────────────────────────────────

        [Fact]
        public async Task WhenPreferredLanguageIsEmpty_MetadataLanguageIsNull()
        {
            // Passing an empty string as lang would send a blank --extractor-args argument
            // to yt-dlp, which may cause unexpected behavior. Null means "not set".
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(lang: ""), Now, default);

            await ytDlp.Received(1).GetPlaylistEntriesAsync(
                SourceUrl, 30, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), null);
        }

        [Fact]
        public async Task WhenPreferredLanguageIsSet_ItIsTransmittedAsIs()
        {
            // The listing path is the only one that can serve a localized title. Passing the
            // language here lets yt-dlp select the preferred localization from the platform.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(lang: "fr"), Now, default);

            await ytDlp.Received(1).GetPlaylistEntriesAsync(
                SourceUrl, 30, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), "fr");
        }

        // ── Ingestion ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ShortListing_CreatesTwoNewVideos_AndLeavesExistingVideoUntouched()
        {
            // The listing step discovers new videos and creates rows for them, without touching
            // videos the listing did not see (those are left for the cleanup step to evaluate).
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var existingVideo = KnownVideo("existingNativeId");
            db.GetVideosBySource(SourceUrl).Returns([existingVideo]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Equal(2, outcome.Created);

            // Both new videos must be upserted with the correct native ids.
            db.Received(1).UpsertVideo(Arg.Is<VideoRecord>(v => v.NativeId == "PM6lMoClzy8"));
            db.Received(1).UpsertVideo(Arg.Is<VideoRecord>(v => v.NativeId == "dQw4w9WgXcQ"));

            // The video not in this listing is not touched.
            db.DidNotReceive().UpsertVideo(Arg.Is<VideoRecord>(v => v.NativeId == "existingNativeId"));
        }

        // ── Short-listing escalation ──────────────────────────────────────────────────────────

        [Fact]
        public async Task ShortListing_WhenNoEntryIsKnown_EscalatesToFull()
        {
            // Safety valve: when none of the 30 entries in the short window exist in the database,
            // more than 30 videos must have been published since the last listing. Proceeding with
            // the short window would miss everything older — a full listing is the only remedy.
            // The result must reflect what actually ran (Full=true), not what was planned.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]); // no known videos
            // Short call returns entries, none of which are known → escalation
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, 30, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>()).Returns(TwoEntryYoutubePlaylist());
            // Full call (after escalation) returns the same content
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, Arg.Is<int?>(n => n != 30), null,
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            // Two calls to the seam: one short, one full.
            Assert.Equal(2, ytDlp.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IYtDlpService.GetPlaylistEntriesAsync)));
            Assert.True(outcome.Full);
        }

        [Fact]
        public async Task ShortListing_WhenItEscalates_TheFullCallCarriesTheSameArgumentsAsAPlannedFull()
        {
            // The escalated call's arguments were checked by nothing: the escalation test above
            // counts calls, and the argument tests only cover a listing that was PLANNED full. A
            // wrong cap or a date cutoff on this path would have gone unnoticed.
            //
            // The date cutoff matters more than the cap. Passing one would make the listing stop at
            // the retention horizon, so older videos would be absent from a full listing — and
            // absence from a full listing is what the cleanup deletes. Retention would quietly
            // become an erase rule, and the deleted rows would be rediscovered and refetched on the
            // next pass.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, 30, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>()).Returns(TwoEntryYoutubePlaylist());
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, 200, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>()).Returns(TwoEntryYoutubePlaylist());

            await Make(db, ytDlp).RunAsync(
                ShortPlannedWithRetention(maxVideosPerSource: 200, retentionDays: 30),
                Config(), Now, default);

            await ytDlp.Received(1).GetPlaylistEntriesAsync(
                SourceUrl, 200, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>());
        }

        [Fact]
        public async Task ShortListing_WhenAtLeastOneEntryIsKnown_DoesNotEscalate()
        {
            // One known entry in the short window is evidence that the source is not too far behind
            // to enumerate without a full listing. The escalation would be a wasted round-trip.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]); // one known
            ytDlp.GetPlaylistEntriesAsync(SourceUrl, 30, null, Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<string?>()).Returns(TwoEntryYoutubePlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            // Exactly one call: no escalation.
            Assert.Equal(1, ytDlp.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IYtDlpService.GetPlaylistEntriesAsync)));
            Assert.False(outcome.Full);
        }

        // ── Zero-entry asymmetry ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task ZeroEntries_WhenSourceHasNoKnownVideos_IsSuccessAndWritesTheClock()
        {
            // A channel that has never published a video returns an empty listing legitimately.
            // Treating it as a failure would retry forever and never advance the source's clock,
            // keeping it at the top of the listing queue on every pass.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(EmptyPlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            db.Received(1).SetListingClock(SourceUrl, Arg.Any<bool>(), Arg.Any<DateTimeOffset>());
        }

        [Fact]
        public async Task ZeroEntries_WhenSourceHasKnownVideos_IsFailureAndWritesNoClock()
        {
            // A non-empty source returning zero entries is a platform outage. Writing the clock
            // here would make the source look up to date, and if CleanupAllowed were true the
            // cleanup step would retire every video in the library as "gone from the source".
            // That deletion is irreversible for watch state.
            // This test explicitly verifies that SetListingClock was not called — not only that
            // the outcome is a failure.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("someVideoId")]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(EmptyPlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            Assert.NotEqual(CallOutcome.Ok, outcome.Outcome);
            Assert.False(outcome.CleanupAllowed);
            db.DidNotReceive().SetListingClock(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>());
        }

        [Fact]
        public async Task ANewVideo_GetsTheSameUuid_EveryTimeItIsListedFromScratch()
        {
            // The purge, and every retirement, in one assertion. The uuid is what Jellyfin's item
            // GUID is hashed from, and for a Movie that GUID is the only key Jellyfin writes the
            // played state and the resume position under. Drawn at random, a video retired and
            // re-listed came back as a different item with an empty history — and the same was true
            // of every video after the schema purge.
            //
            // Two runs against two empty databases, as a purge leaves things.
            var first = await CreatedIdsFromAFreshDatabaseAsync();
            var second = await CreatedIdsFromAFreshDatabaseAsync();

            Assert.NotEmpty(first);
            Assert.Equal(first, second);

            // And it really is derived from the source and the platform id, not merely stable
            // within one process.
            Assert.Equal(
                VodNativeId.UuidForVideo(VodNativeId.ForSource(SourceUrl)!, "PM6lMoClzy8"),
                first[0]);
        }

        private async Task<List<string>> CreatedIdsFromAFreshDatabaseAsync()
        {
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var ids = new List<string>();

            db.GetVideosBySource(SourceUrl).Returns([]);
            db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>()))
                .Do(call => ids.Add(call.ArgAt<VideoRecord>(0).Id));

            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            await Make(db, ytDlp).RunAsync(FullPlanned(), Config(), Now, default);
            return ids;
        }

        [Fact]
        public async Task AFullListingThatLostMostOfItsEntries_DoesNotAllowCleanup()
        {
            // A full listing is authoritative and the cleanup deletes every row it did not touch —
            // irreversibly, watch state included. A partially enumerated playlist (a page lost
            // mid-enumeration, a rate limit part-way) is not distinguishable from a channel that
            // really lost its videos, and yt-dlp is not guaranteed to fail the call.
            //
            // Zero entries is already caught. Two entries where forty are stored is the same
            // outage with one page delivered, and it costs 38 videos and their watch state.
            // Withholding the cleanup costs one listing interval of stale rows and self-heals on
            // the next complete answer.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var stored = new List<VideoRecord>();
            for (var i = 0; i < 40; i++)
                stored.Add(KnownVideo("stored-" + i));
            db.GetVideosBySource(SourceUrl).Returns(stored);

            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(FullPlanned(), Config(), Now, default);

            Assert.False(outcome.CleanupAllowed);

            // Still a success: the entries it did carry are real and must be ingested, and a
            // failure here would stop the clocks and strand the source at the top of the queue.
            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            db.Received(1).SetListingClock(SourceUrl, true, Arg.Any<DateTimeOffset>());

            // Said on the page, not only in the log: a source whose cleanup is being withheld
            // looks healthy otherwise, and the withholding is the thing to explain.
            db.Received(1).SetStageResult(
                SourceUrl, SourceStage.FullListing, CallOutcome.Ok,
                Arg.Is<string>(d => d != null && d.Contains("cleanup", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task AFullListingCappedByTheOperator_StillAllowsCleanup()
        {
            // The shortfall check must not fire on a cap the administrator chose. Lowering
            // MaxVideosPerSource is the deliberate way to shrink a source, and the cap is then the
            // expected number of entries — not a sign of a truncated answer. Without this the
            // 38 videos the operator just asked to drop would never be retired.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var stored = new List<VideoRecord>();
            for (var i = 0; i < 40; i++)
                stored.Add(KnownVideo("stored-" + i));
            db.GetVideosBySource(SourceUrl).Returns(stored);

            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            var outcome = await Make(db, ytDlp)
                .RunAsync(FullPlanned(maxVideosPerSource: 2), Config(), Now, default);

            Assert.True(outcome.CleanupAllowed);
        }

        [Theory]
        [InlineData(20, true)]   // exactly half: not a shortfall, cleanup proceeds
        [InlineData(19, false)]  // one below half: withheld
        public async Task TheShortfallThreshold_IsHalfOfWhatIsStored(int listed, bool cleanupAllowed)
        {
            // The boundary, both sides. Without the allowing case nothing distinguishes this guard
            // from one that withholds the cleanup whenever a single video disappears — which would
            // mean no video is ever retired again, silently, and the library only ever grows.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var stored = new List<VideoRecord>();
            for (var i = 0; i < 40; i++)
                stored.Add(KnownVideo("stored-" + i));
            db.GetVideosBySource(SourceUrl).Returns(stored);

            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(PlaylistOf(listed));

            var outcome = await Make(db, ytDlp)
                .RunAsync(FullPlanned(maxVideosPerSource: 0), Config(), Now, default);

            Assert.Equal(cleanupAllowed, outcome.CleanupAllowed);
        }

        [Fact]
        public async Task AFullListingOnASmallSource_IsNotSecondGuessed()
        {
            // Proportions are meaningless on a handful of rows: a six-video source legitimately
            // dropping to two is ordinary, and withholding its cleanup would leave four deleted
            // videos published for ever. The floor is what keeps the guard out of normal churn.
            //
            // Six stored against two listed, not three: the ratio has to be one the proportionality
            // check WOULD refuse, or the floor is not what this test is about and it holds for any
            // threshold at all.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var stored = new List<VideoRecord>();
            for (var i = 0; i < 6; i++)
                stored.Add(KnownVideo("stored-" + i));
            db.GetVideosBySource(SourceUrl).Returns(stored);

            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(PlaylistOf(2));

            var outcome = await Make(db, ytDlp)
                .RunAsync(FullPlanned(maxVideosPerSource: 0), Config(), Now, default);

            Assert.True(outcome.CleanupAllowed);
        }

        // ── Seam failure ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task SeamReturnsNull_RecordsStageResultAndWritesNoClock()
        {
            // A null return from the seam means yt-dlp produced no usable answer (network failure,
            // parse error, timeout). The stage result is always recorded so the failure count
            // advances and the operator can see how many times this has failed. No clock is written
            // so the source stays due on the next pass instead of looking up to date.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns((PlaylistInfo?)null);

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            Assert.NotEqual(CallOutcome.Ok, outcome.Outcome);
            db.Received(1).SetStageResult(
                SourceUrl, Arg.Any<SourceStage>(), Arg.Any<CallOutcome>(), Arg.Any<string?>());
            db.DidNotReceive().SetListingClock(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>());
        }

        // ── Deduplication ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task DuplicateEntry_InSameResponse_ProducesOnlyOneRow()
        {
            // Without deduplication, a platform that returns the same video twice would fail the
            // unique index (source_url, native_id) and crash the whole pass turn in production.
            // The test pins this explicitly because the index violation does not surface in unit
            // tests (the substitute's UpsertVideo is a no-op) — without it, the pass would fail
            // silently in production.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);

            var duplicatePlaylist = new PlaylistInfo
            {
                Title = "Test",
                Entries =
                [
                    new PlaylistEntry
                    {
                        Id = "PM6lMoClzy8",
                        VideoUrl = "https://www.youtube.com/watch?v=PM6lMoClzy8",
                        Title = "Video One",
                        IeKey = "Youtube",
                    },
                    new PlaylistEntry // same id: a duplicate the platform accidentally returned twice
                    {
                        Id = "PM6lMoClzy8",
                        VideoUrl = "https://www.youtube.com/watch?v=PM6lMoClzy8",
                        Title = "Video One",
                        IeKey = "Youtube",
                    },
                ],
            };
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(duplicatePlaylist);

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            // Only one UpsertVideo for this native id, despite two entries in the response.
            Assert.Equal(1, db.ReceivedCalls()
                .Count(c =>
                    c.GetMethodInfo().Name == nameof(IVodDatabase.UpsertVideo)
                    && c.GetArguments()[0] is VideoRecord v
                    && v.NativeId == "PM6lMoClzy8"));
        }

        // ── Arte (no title, no explicit id) ──────────────────────────────────────────────────

        [Fact]
        public async Task ArteEntry_RecoverIdFromUrl_AndUsesNativeIdAsFallbackTitle()
        {
            // Arte entries carry only url, ie_key, _type, and one null field — no id, no title.
            // Without id recovery the entry would be dropped and that source would sync zero
            // videos, silently. Without the title fallback the video would be published under an
            // empty string. Fixture is from PlaylistInfoEntriesTests (the measured shape).
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(OneArteEntry());

            // Arte source: use a matching URL so it's treated as a legitimate source
            const string arteSourceUrl = "https://www.arte.tv/fr/videos/RC-022076/les-idees-larges/";
            var artePlanned = new PlannedListing(
                new SourceDefinition { Url = arteSourceUrl, Enabled = true },
                Full: false, ListingReason.ShortListingDue);
            db.GetVideosBySource(arteSourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(arteSourceUrl, Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(OneArteEntry());

            await Make(db, ytDlp).RunAsync(artePlanned, Config(), Now, default);

            // The id must be recovered from the URL segment (measured: "130515-005-A"),
            // and the title falls back to the native id because Arte carries no title.
            db.Received(1).UpsertVideo(Arg.Is<VideoRecord>(v =>
                v.NativeId == "130515-005-A"
                && v.Title == "130515-005-A"
                && v.SourceUrl == arteSourceUrl));
        }

        // ── Listing count ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task WhenPlatformReportsListingCount_ItIsWritten()
        {
            // The playlist_count is a better change detector than the title: a drop reveals a
            // deletion that looking at the head of the listing cannot see.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            var playlistWithCount = new PlaylistInfo
            {
                Title = "Test",
                PlaylistCount = 42,
                Entries =
                [
                    new PlaylistEntry
                    {
                        Id = "PM6lMoClzy8",
                        VideoUrl = "https://www.youtube.com/watch?v=PM6lMoClzy8",
                        Title = "Video",
                        IeKey = "Youtube",
                    },
                ],
            };
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(playlistWithCount);

            await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            db.Received(1).SetListingCount(SourceUrl, 42);
        }

        [Fact]
        public async Task WhenPlatformDoesNotReportListingCount_NullIsWritten()
        {
            // Null must stay null and never become zero: zero would look like an emptied source,
            // and is treated as a platform outage by the zero-entry asymmetry rule.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist()); // PlaylistCount is null in this fixture

            await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            db.Received(1).SetListingCount(SourceUrl, (int?)null);
        }

        // ── CleanupAllowed ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task CleanupAllowed_IsFalse_ForASuccessfulShortListing()
        {
            // A short listing only refreshes listed_at for its 30 entries. Everything past
            // position 30 would look absent to the cleanup step, which would retire almost the
            // entire source. CleanupAllowed gates this: the cleanup runs only after a full listing.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            Assert.False(outcome.CleanupAllowed);
        }

        [Fact]
        public async Task CleanupAllowed_IsTrue_ForASuccessfulFullListing()
        {
            // Only a full listing has seen everything the source holds. It is safe to retire
            // absent videos only when we know the whole picture.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist());

            var outcome = await Make(db, ytDlp).RunAsync(FullPlanned(), Config(), Now, default);

            Assert.True(outcome.CleanupAllowed);
        }

        [Fact]
        public async Task CleanupAllowed_IsFalse_WhenFullListingFails()
        {
            // A failed listing cannot tell us what the platform currently holds. Allowing cleanup
            // on a failed listing would retire videos that are still on the platform.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns((PlaylistInfo?)null);

            var outcome = await Make(db, ytDlp).RunAsync(FullPlanned(), Config(), Now, default);

            Assert.False(outcome.CleanupAllowed);
        }

        // ── Retitling ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task WhenVideoIsRetitled_ListingTitleAndTitleAreUpdated_AndMetadataIsMarkedStale()
        {
            // When the platform changes a video's title, the listing step detects it and flags the
            // video for a metadata refetch by calling MarkMetadataStale. The wave then fills in the
            // new title from the fresh metadata call.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            var video = KnownVideo("PM6lMoClzy8", listingTitle: "Old Title");
            db.GetVideosBySource(SourceUrl).Returns([video]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist("New Title", "Video Two")); // first entry retitled

            var outcome = await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            Assert.Equal(1, outcome.Retitled);

            // Both listing_title and the published title must be updated.
            db.Received(1).UpsertVideo(Arg.Is<VideoRecord>(v =>
                v.NativeId == "PM6lMoClzy8"
                && v.ListingTitle == "New Title"
                && v.Title == "New Title"));

            // MarkMetadataStale is called so the wave refetches the video's metadata.
            db.Received(1).MarkMetadataStale(video.Id);
        }

        [Fact]
        public async Task Retitle_DoesNotNullMetadataAt_SoVideoRemainsPublishable()
        {
            // Setting metadata_at to NULL would make Publishability refuse the video and cause
            // the checkup to un-publish it at the same turn — destroying the Jellyfin item and
            // the watch state it held — for the sake of a title change.
            // MarkMetadataStale writes UnixEpoch instead: non-null, older than any freshness
            // window, which makes the video due for a refetch while keeping it published.
            // This test protects that invariant end-to-end.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();

            var video = KnownVideo("PM6lMoClzy8", listingTitle: "Old Title",
                metadataAt: Now.AddDays(-1));
            // MetadataResult and Extras are already set by KnownVideo when metadataAt is provided.

            db.GetVideosBySource(SourceUrl).Returns([video]);

            // Simulate what MarkMetadataStale does: it sets metadata_at to UnixEpoch.
            db.When(d => d.MarkMetadataStale(video.Id))
              .Do(_ => video.MetadataAt = DateTimeOffset.UnixEpoch);

            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist("New Title", "Video Two"));

            await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            // UnixEpoch is not null: Publishability will not refuse the video.
            Assert.Equal(DateTimeOffset.UnixEpoch, video.MetadataAt);
            Assert.True(Publishability.IsPublishable(video, retentionDays: 0, Now));
        }

        [Fact]
        public async Task ArteEntryWithNoTitle_NeverTriggersRetitleMarking()
        {
            // Arte carries no title in its flat listing. Treating absence as a retitle would mark
            // ~300 Arte videos stale on every pass and flood the wave with unnecessary fetches.
            // PublishedTitle.HasBeenRetitled guards against this, and this test pins it.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            const string arteSourceUrl = "https://www.arte.tv/fr/videos/RC-022076/les-idees-larges/";
            var artePlanned = new PlannedListing(
                new SourceDefinition { Url = arteSourceUrl, Enabled = true },
                Full: false, ListingReason.ShortListingDue);
            // Existing Arte video with a title set by a previous metadata fetch.
            var existing = new VideoRecord
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = arteSourceUrl,
                NativeId = "130515-005-A",
                ExtractorKey = "ArteTV",
                VideoUrl = "https://www.arte.tv/fr/videos/130515-005-A/pourquoi-eclate-t-on-en-sanglots/",
                Title = "Pourquoi éclate-t-on en sanglots ?",
                ListingTitle = null, // Arte never gave a listing title
            };
            db.GetVideosBySource(arteSourceUrl).Returns([existing]);
            ytDlp.GetPlaylistEntriesAsync(arteSourceUrl, Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(OneArteEntry());

            await Make(db, ytDlp).RunAsync(artePlanned, Config(), Now, default);

            // No retitle: Arte entries have no title, so HasBeenRetitled returns false.
            db.DidNotReceive().MarkMetadataStale(Arg.Any<string>());
        }

        // ── Source metadata written ───────────────────────────────────────────────────────────

        [Fact]
        public async Task SourceDefinitionName_WinsOverResolvedSeriesName()
        {
            // When the operator explicitly sets a name in the configuration, that name is
            // authoritative. The platform-derived name (from the playlist title or channel field)
            // is only a fallback when no explicit name is set.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist()); // playlist title is "Test Channel"

            // Explicitly configured name that should win over "Test Channel".
            await Make(db, ytDlp).RunAsync(
                ShortPlanned(name: "My Custom Channel Name"), Config(), Now, default);

            db.Received(1).UpsertSource(Arg.Is<SourceRecord>(s =>
                s.Url == SourceUrl
                && s.Name == "My Custom Channel Name"));
        }

        [Fact]
        public async Task BothSourceImageUrls_AreWrittenOnTheSourceRecord()
        {
            // The two image URLs (thumbnail and banner) are kept on the source row for potential
            // later use. They are not published to Jellyfin (the series uses the newest video's
            // still instead) but must not be lost.
            var db = Substitute.For<IVodDatabase>();
            var ytDlp = Substitute.For<IYtDlpService>();
            db.GetVideosBySource(SourceUrl).Returns([KnownVideo("PM6lMoClzy8")]);
            ytDlp.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(TwoEntryYoutubePlaylist()); // has ThumbnailUrl and BannerUrl

            await Make(db, ytDlp).RunAsync(ShortPlanned(), Config(), Now, default);

            db.Received(1).UpsertSource(Arg.Is<SourceRecord>(s =>
                s.SeriesThumbnailUrl == "https://cdn.yt/thumb.jpg"
                && s.SeriesBannerUrl == "https://cdn.yt/banner.jpg"));
        }
    }
}

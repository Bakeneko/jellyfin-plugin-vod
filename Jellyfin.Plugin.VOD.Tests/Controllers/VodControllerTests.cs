using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Controllers;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Controllers
{
    /// <summary>
    /// The two decisions the admin routes make. The controller itself takes sealed services and
    /// cannot be constructed under NSubstitute, so the decisions live apart from the wiring.
    /// </summary>
    public sealed class VodControllerTests
    {
        [Fact]
        public void ProjectLibraryOptions_SortsForDisplay()
        {
            var options = VodController.ProjectLibraryOptions(
            [
                new VirtualFolderInfo { Name = "VOD" },
                new VirtualFolderInfo { Name = "Films", CollectionType = CollectionTypeOptions.movies },
                new VirtualFolderInfo { Name = "Séries", CollectionType = CollectionTypeOptions.tvshows }
            ]);

            // Asserting "sorted", not a particular collation: CurrentCulture ordering is what
            // the UI wants, but pinning its exact output would make the test hostage to the CI
            // machine's locale.
            var names = options.Select(o => o.Name).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
            Assert.Equal(3, options.Count);
        }

        [Fact]
        public void ProjectLibraryOptions_ReturnsEmptyRatherThanNull_WhenNoLibraryExists()
        {
            // The configuration page feeds this straight into two <select>s; a null would break the
            // page rather than show an empty dropdown.
            Assert.Empty(VodController.ProjectLibraryOptions([]));
        }

        [Fact]
        public void ProjectLibraryOptions_SkipsALibraryWithNoUsableName()
        {
            var options = VodController.ProjectLibraryOptions(
            [
                new VirtualFolderInfo { Name = "VOD" },
                new VirtualFolderInfo { Name = "   " },
                new VirtualFolderInfo { Name = null! }
            ]);

            Assert.Equal(new[] { "VOD" }, options.Select(o => o.Name));
        }

        [Fact]
        public void ProjectLibraryOptions_CarriesTheAcceptanceOfEachType()
        {
            // The page holds no type rule of its own: it filters each selector on these two flags.
            var options = VodController.ProjectLibraryOptions(
            [
                new VirtualFolderInfo { Name = "Films", CollectionType = CollectionTypeOptions.movies },
                new VirtualFolderInfo { Name = "Mixte" },
                new VirtualFolderInfo { Name = "Musique", CollectionType = CollectionTypeOptions.music },
                new VirtualFolderInfo { Name = "Séries", CollectionType = CollectionTypeOptions.tvshows }
            ]).ToDictionary(o => o.Name, StringComparer.Ordinal);

            Assert.True(options["Films"].AcceptsMovies);
            Assert.False(options["Films"].AcceptsSeries);
            Assert.False(options["Séries"].AcceptsMovies);
            Assert.True(options["Séries"].AcceptsSeries);

            // Only a mixed library appears in both selectors — the invariant the whole design rests on.
            Assert.True(options["Mixte"].AcceptsMovies && options["Mixte"].AcceptsSeries);
            Assert.Null(options["Mixte"].CollectionType);

            Assert.False(options["Musique"].AcceptsMovies || options["Musique"].AcceptsSeries);
            Assert.Equal("music", options["Musique"].CollectionType);
        }

        [Fact]
        public void ProjectLibraryStatus_ReportsNothingWrongForAMatchingType()
        {
            var status = VodController.ProjectLibraryStatus(
                "series", "Séries", needed: true, CollectionTypeOptions.tvshows, error: null);

            Assert.Equal("series", status.Role);
            Assert.Equal("Séries", status.Name);
            Assert.True(status.Needed);
            Assert.Null(status.Error);
            Assert.Null(status.Warning);
        }

        [Fact]
        public void ProjectLibraryStatus_WarnsButDoesNotErrorOnAMismatchedType()
        {
            // Review focus 5: a type that does not fit is cosmetic — it corrupts nothing — so it
            // must not become an error, which under all-or-nothing would stop every source.
            var status = VodController.ProjectLibraryStatus(
                "series", "Musique", needed: true, CollectionTypeOptions.music, error: null);

            Assert.Null(status.Error);
            Assert.NotNull(status.Warning);
            Assert.Contains("Musique", status.Warning, StringComparison.Ordinal);
        }

        [Fact]
        public void ProjectLibraryStatus_KeepsTheErrorAndSkipsTheTypeCheck()
        {
            // A library that does not resolve has no type to judge, and a second message about its
            // type would only bury the one that matters.
            var status = VodController.ProjectLibraryStatus(
                "movies", string.Empty, needed: true, collectionType: null,
                error: "No movies library is configured.");

            Assert.Equal("No movies library is configured.", status.Error);
            Assert.Null(status.Warning);
        }

        [Fact]
        public void ProjectSourceStatus_CarriesWhatTheTableShows()
        {
            var listed = DateTimeOffset.UtcNow.AddHours(-2);

            var rows = VodController.ProjectSourceStatus(
                [
                    new SourceRecord
                    {
                        Url = "https://www.youtube.com/@arte",
                        Name = "ARTE",
                        ShortListedAt = listed,
                        FullListedAt = listed.AddDays(-3),
                        VideoCount = 42
                    }
                ]);

            var row = Assert.Single(rows);
            Assert.Equal("https://www.youtube.com/@arte", row.Url);
            Assert.Equal("ARTE", row.Name);
            Assert.Equal(listed, row.ShortListedAt);
            Assert.Equal(listed.AddDays(-3), row.FullListedAt);
            Assert.Equal(42, row.VideoCount);
        }

        [Fact]
        public void ProjectSourceStatus_KeepsASourceThatHasNeverListed()
        {
            // The point of the column: a source whose every listing has failed still gets a row, so
            // the page can say "never listed" rather than leave it out and look healthy. Why it
            // failed now lives in the stage triplets, which the source detail page reads.
            var rows = VodController.ProjectSourceStatus(
            [
                new SourceRecord { Url = "https://www.youtube.com/@gone" }
            ]);

            var row = Assert.Single(rows);
            Assert.Null(row.ShortListedAt);
            Assert.Null(row.FullListedAt);
            Assert.Equal(0, row.VideoCount);
        }

        [Fact]
        public void ProjectSourceStatus_ReturnsEmptyRatherThanNull_WhenNothingIsConfigured()
            => Assert.Empty(VodController.ProjectSourceStatus([]));

        [Theory]
        [InlineData(null, null, true)]      // no selection: the default rule applies
        [InlineData(1080, "h264", true)]    // a complete selection
        [InlineData(1080, null, false)]     // half a selection is a malformed request
        [InlineData(null, "h264", false)]
        public void IsWellFormedSelection_RequiresBothOrNeither(int? height, string? codec, bool expected)
            => Assert.Equal(expected, VodController.IsWellFormedSelection(height, codec));

        [Fact]
        public void IsStreamable_ReturnsFalseForAnUpcomingBroadcast()
        {
            // A scheduled stream has no formats at all, so resolving it would fail deep in
            // yt-dlp. Refusing at the controller keeps the failure readable.
            var video = new VideoRecord
            {
                Id = "3f9a2b1c-0000-0000-0000-000000000077",
                SourceUrl = "https://www.twitch.tv/channel",
                VideoUrl = "https://www.twitch.tv/channel",
                LiveStatus = LiveStatus.Upcoming
            };
            Assert.False(VodController.IsStreamable(video));
        }

        [Theory]
        [InlineData(StreamCacheKey.DefaultFormat, true)]
        [InlineData("worst", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void DescribesTheServedMaster_OnlyWhenFormatIsDefault(string? format, bool expected)
        {
            // GET /VOD/stream/{id}.m3u8 is unauthenticated: ffmpeg and players call it without a
            // token. StreamDescriber primes its cache entries under the default format and the
            // plugin-configured languages, which are exactly the keys normal playback reads. A
            // request with a custom format (e.g. "worst") would prime those same keys with a
            // different master and then probe it into a permanent, wrong description. The guard
            // therefore only allows the background description when the format matches the default.
            Assert.Equal(expected, VodController.DescribesTheServedMaster(format));
        }

        // ── ProjectSourceStatus — stage triplets ─────────────────────────────────────────────────

        [Fact]
        public void ProjectSourceStatus_CarriesAllFiveStageTriplets()
        {
            // Each stage has a DISTINCT outcome and a DISTINCT failure counter so that swapping two
            // stages in the projection produces a wrong answer, which is the mutation this test
            // catches. Tautological fixtures (all Ok, all 0) cannot catch that.
            var record = new SourceRecord
            {
                Url = "https://www.youtube.com/@arte",
                ShortListingResult = CallOutcome.Ok,
                ShortListingDetail = "short ok",
                ShortListingFailures = 0,
                FullListingResult = CallOutcome.NetworkError,
                FullListingDetail = "full network",
                FullListingFailures = 1,
                CleanupResult = CallOutcome.Timeout,
                CleanupDetail = "cleanup timeout",
                CleanupFailures = 2,
                WaveResult = CallOutcome.RateLimited,
                WaveDetail = "wave rate",
                WaveFailures = 3,
                CheckupResult = CallOutcome.ParseError,
                CheckupDetail = "checkup parse",
                CheckupFailures = 7,
            };

            var row = Assert.Single(VodController.ProjectSourceStatus([record]));

            Assert.Equal(CallOutcome.Ok, row.ShortListingResult);
            Assert.Equal("short ok", row.ShortListingDetail);
            Assert.Equal(0, row.ShortListingFailures);

            Assert.Equal(CallOutcome.NetworkError, row.FullListingResult);
            Assert.Equal("full network", row.FullListingDetail);
            Assert.Equal(1, row.FullListingFailures);

            Assert.Equal(CallOutcome.Timeout, row.CleanupResult);
            Assert.Equal("cleanup timeout", row.CleanupDetail);
            Assert.Equal(2, row.CleanupFailures);

            Assert.Equal(CallOutcome.RateLimited, row.WaveResult);
            Assert.Equal("wave rate", row.WaveDetail);
            Assert.Equal(3, row.WaveFailures);

            Assert.Equal(CallOutcome.ParseError, row.CheckupResult);
            Assert.Equal("checkup parse", row.CheckupDetail);
            Assert.Equal(7, row.CheckupFailures);
        }

        [Fact]
        public void ProjectSourceStatus_CarriesListingCount()
        {
            // ListingCount null and non-null — both cases must round-trip correctly.
            var withCount = new SourceRecord
            {
                Url = "https://www.youtube.com/@a",
                ListingCount = 42,
            };
            var withoutCount = new SourceRecord
            {
                Url = "https://www.youtube.com/@b",
                ListingCount = null,
            };

            var rows = VodController.ProjectSourceStatus([withCount, withoutCount])
                .ToDictionary(r => r.Url, StringComparer.Ordinal);

            Assert.Equal(42, rows["https://www.youtube.com/@a"].ListingCount);
            Assert.Null(rows["https://www.youtube.com/@b"].ListingCount);
        }

        [Fact]
        public void ProjectSourceStatus_CarriesLongDetailVerbatim()
        {
            // The original defect: every detail was reduced to the word "Failed". This test pins
            // that a multi-line detail of several hundred characters arrives unchanged.
            var longDetail = string.Join(
                "\n",
                Enumerable.Range(0, 15).Select(i =>
                    $"ERROR: [youtube] {i:D3}: Sign in to confirm your age. " +
                    "This video may be inappropriate for some users. (etc.)"));

            Assert.True(longDetail.Length > 300, "fixture must be long enough to be a real stress test");
            Assert.Contains("\n", longDetail, StringComparison.Ordinal);

            var record = new SourceRecord
            {
                Url = "https://www.youtube.com/@arte",
                WaveResult = CallOutcome.NeedsAuth,
                WaveDetail = longDetail,
            };

            var row = Assert.Single(VodController.ProjectSourceStatus([record]));
            Assert.Equal(longDetail, row.WaveDetail);
        }

        // ── ProjectVideoStatus — video triplets ──────────────────────────────────────────────────

        [Fact]
        public void ProjectVideoStatus_CarriesBothTriplets_WithDistinctValues()
        {
            // Distinct outcomes and attempt counts for metadata vs. describe — so swapping them in
            // the projection yields a wrong answer and the test catches it.
            var meta = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var desc = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

            var record = new VideoRecord
            {
                Id = "aaaabbbb-0000-0000-0000-000000000001",
                VideoUrl = "https://www.youtube.com/watch?v=aaa",
                SourceUrl = "https://www.youtube.com/@arte",
                MetadataResult = CallOutcome.Ok,
                MetadataDetail = "metadata ok detail",
                MetadataAttempts = 1,
                MetadataAt = meta,
                DescribeResult = CallOutcome.ProbeFailed,
                DescribeDetail = "describe probe failed detail",
                DescribeAttempts = 3,
                DescribedAt = desc,
            };

            var row = Assert.Single(VodController.ProjectVideoStatus([record]));

            Assert.Equal(CallOutcome.Ok, row.MetadataResult);
            Assert.Equal("metadata ok detail", row.MetadataDetail);
            Assert.Equal(1, row.MetadataAttempts);
            Assert.Equal(meta, row.MetadataAt);

            Assert.Equal(CallOutcome.ProbeFailed, row.DescribeResult);
            Assert.Equal("describe probe failed detail", row.DescribeDetail);
            Assert.Equal(3, row.DescribeAttempts);
            Assert.Equal(desc, row.DescribedAt);
        }

        [Fact]
        public void ProjectVideoStatus_CarriesSeasonAndEpisodeNumbers()
        {
            // Season and episode numbers are recomputed at checkup, never frozen — diagnosing a
            // wrong numbering from the page requires seeing the current values.
            var record = new VideoRecord
            {
                Id = "aaaabbbb-0000-0000-0000-000000000002",
                VideoUrl = "https://www.youtube.com/watch?v=bbb",
                SourceUrl = "https://www.youtube.com/@arte",
                SeasonNumber = 3,
                EpisodeNumber = 17,
            };

            var row = Assert.Single(VodController.ProjectVideoStatus([record]));
            Assert.Equal(3, row.SeasonNumber);
            Assert.Equal(17, row.EpisodeNumber);
        }

        [Fact]
        public void ProjectVideoStatus_CarriesNoneDescribeResult()
        {
            // None with a detail is a normal outcome (e.g. "no HLS manifest") — must not be hidden.
            var record = new VideoRecord
            {
                Id = "aaaabbbb-0000-0000-0000-000000000003",
                VideoUrl = "https://www.youtube.com/watch?v=ccc",
                SourceUrl = "https://www.youtube.com/@arte",
                DescribeResult = CallOutcome.None,
                DescribeDetail = "describe: the platform offered no HLS manifest",
            };

            var row = Assert.Single(VodController.ProjectVideoStatus([record]));
            Assert.Equal(CallOutcome.None, row.DescribeResult);
            Assert.Equal("describe: the platform offered no HLS manifest", row.DescribeDetail);
        }

        [Fact]
        public void ProjectVideoStatus_CarriesLongDescribeDetailVerbatim()
        {
            // Same verbatim-detail requirement as for sources: the original defect was truncation.
            var longDetail = string.Join(
                "\n",
                Enumerable.Range(0, 10).Select(i =>
                    $"ffprobe: [line {i:D3}] Cannot find codec parameters for stream #0:{i}: " +
                    "Video: none (Confidence: 100%)"));

            Assert.True(longDetail.Length > 300);

            var record = new VideoRecord
            {
                Id = "aaaabbbb-0000-0000-0000-000000000004",
                VideoUrl = "https://www.youtube.com/watch?v=ddd",
                SourceUrl = "https://www.youtube.com/@arte",
                DescribeResult = CallOutcome.ProbeFailed,
                DescribeDetail = longDetail,
            };

            var row = Assert.Single(VodController.ProjectVideoStatus([record]));
            Assert.Equal(longDetail, row.DescribeDetail);
        }

        // ── BuildWarnings — authentication and listing cap warnings ──────────────────────────────

        [Fact]
        public void BuildWarnings_WarnsWhenNeedsAuthAndCookiesConfigured()
        {
            // CASE 1 (needs_auth + cookies): the session has expired → must warn.
            var config = new PluginConfiguration { CookiesPath = "/etc/cookies.txt" };
            var counts = new Dictionary<CallOutcome, int> { [CallOutcome.NeedsAuth] = 1 };

            var warnings = VodController.BuildWarnings(config, counts);

            Assert.NotEmpty(warnings);
        }

        [Fact]
        public void BuildWarnings_SilentWhenNeedsAuthButNoCookies()
        {
            // CASE 2 (needs_auth, no cookies): age-restricted video on an unconfigured instance
            // is normal, not an anomaly — must NOT warn.
            var config = new PluginConfiguration { CookiesPath = string.Empty };
            var counts = new Dictionary<CallOutcome, int> { [CallOutcome.NeedsAuth] = 5 };

            var warnings = VodController.BuildWarnings(config, counts);

            Assert.DoesNotContain(
                warnings,
                w => w.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                     w.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildWarnings_SilentWhenCookiesConfiguredButNoNeedsAuth()
        {
            // CASE 3 (cookies configured, zero needs_auth): a healthy instance with cookies should
            // NOT warn. A code path that warns whenever cookies are present (ignoring the count)
            // would pass cases 1 and 2 but fail here.
            var config = new PluginConfiguration { CookiesPath = "/etc/cookies.txt" };
            var counts = new Dictionary<CallOutcome, int> { [CallOutcome.Ok] = 42 };

            var warnings = VodController.BuildWarnings(config, counts);

            Assert.DoesNotContain(
                warnings,
                w => w.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                     w.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildWarnings_SilentWhenNeitherAuthNorCookies()
        {
            // CASE 4 (no needs_auth, no cookies): both conditions absent — must NOT warn.
            var config = new PluginConfiguration { CookiesPath = string.Empty };
            var counts = new Dictionary<CallOutcome, int>();

            var warnings = VodController.BuildWarnings(config, counts);

            Assert.DoesNotContain(
                warnings,
                w => w.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                     w.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildWarnings_AuthWarning_DoesNotContainPathOrFileContents()
        {
            // The warning must tell the user WHAT to do, not WHERE the file is: an absolute path
            // in an HTTP response exposes server internals for no benefit.
            var config = new PluginConfiguration { CookiesPath = "/secret/server/path/cookies.txt" };
            var counts = new Dictionary<CallOutcome, int> { [CallOutcome.NeedsAuth] = 1 };

            var warnings = VodController.BuildWarnings(config, counts);
            var authWarning = Assert.Single(
                warnings,
                w => w.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                     w.Contains("cookie", StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain("/secret/server/path/cookies.txt", authWarning, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildWarnings_WarnsWhenListingsPerPassIsZero()
        {
            // At zero, no source is ever listed — the library stays empty and the pass never
            // complains. The warning is the only signal.
            var config = new PluginConfiguration { ListingsPerPass = 0 };
            var warnings = VodController.BuildWarnings(config, new Dictionary<CallOutcome, int>());

            Assert.Contains(warnings, w => w.Contains("ListingsPerPass", StringComparison.Ordinal));
        }

        [Fact]
        public void BuildWarnings_WarnsWhenListingsPerPassIsNegative()
        {
            var config = new PluginConfiguration { ListingsPerPass = -1 };
            var warnings = VodController.BuildWarnings(config, new Dictionary<CallOutcome, int>());

            Assert.Contains(warnings, w => w.Contains("ListingsPerPass", StringComparison.Ordinal));
        }

        [Fact]
        public void BuildWarnings_SilentWhenListingsPerPassIsPositive()
        {
            var config = new PluginConfiguration { ListingsPerPass = 10 };
            var warnings = VodController.BuildWarnings(config, new Dictionary<CallOutcome, int>());

            Assert.DoesNotContain(
                warnings,
                w => w.Contains("ListingsPerPass", StringComparison.Ordinal));
        }

        [Fact]
        public void BuildWarnings_SilentWhenConfigIsNull()
        {
            // A null configuration means the plugin is not yet initialised — no warnings should
            // crash or emit phantom errors for a state we cannot inspect.
            var warnings = VodController.BuildWarnings(null, new Dictionary<CallOutcome, int>());
            Assert.Empty(warnings);
        }

        [Fact]
        public void BuildWarnings_NeedsAuthKeyAbsent_DoesNotWarn()
        {
            // GetOutcomeCounts() omits keys with zero rows. A missing key must not crash or warn.
            var config = new PluginConfiguration { CookiesPath = "/etc/cookies.txt" };
            var counts = new Dictionary<CallOutcome, int>();  // NeedsAuth key is absent

            var warnings = VodController.BuildWarnings(config, counts);

            Assert.DoesNotContain(
                warnings,
                w => w.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                     w.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Integration tests for the two reset routes, exercised against a real SQLite database so that
    /// assertions target what the writes actually do to the columns — not what a substitute records.
    /// </summary>
    public sealed class VodControllerResetTests : IDisposable
    {
        private const string SourceUrl = "https://www.youtube.com/@arte";
        private const string VideoId = "aaaabbbb-cccc-dddd-eeee-000000000001";

        private readonly string _dbPath;
        private readonly VodDatabase _db;
        private readonly string _tempArtwork;
        private readonly VodController _controller;

        public VodControllerResetTests()
        {
            _dbPath = Path.GetTempFileName();
            _db = new VodDatabase(_dbPath, NullLogger<VodDatabase>.Instance);
            _db.EnsureCreated();

            var cache = new PlaylistCache();
            var subtitles = new SubtitleTokens(cache);
            var library = Substitute.For<ILibraryManager>();
            var mediaStreams = Substitute.For<IMediaStreamRepository>();

            var cookieJar = new CookieJar(NullLogger<CookieJar>.Instance);
            var throttle = new YtDlpThrottle(
                TimeProvider.System,
                () => 20,
                (ts, ct) => Task.Delay(ts, ct),
                NullLogger<YtDlpThrottle>.Instance);
            var ytDlp = new YtDlpService(cookieJar, throttle, NullLogger<YtDlpService>.Instance);

            var streamWriter = new MediaStreamWriter(
                library, mediaStreams, NullLogger<MediaStreamWriter>.Instance);
            var describer = new StreamDescriber(
                library, mediaStreams, Substitute.For<IMediaEncoder>(), cache, subtitles,
                NullLogger<StreamDescriber>.Instance);

            _tempArtwork = Path.Combine(
                Path.GetTempPath(), "vod-reset-tests-" + Guid.NewGuid().ToString("N"));
            var publisher = new LibraryPublisher(
                library, _db, new ArtworkStore(_tempArtwork),
                Substitute.For<IVodLocalizer>(), NullLogger<LibraryPublisher>.Instance);

            _controller = new VodController(
                _db, ytDlp, cache, subtitles, streamWriter, describer,
                Substitute.For<IHttpClientFactory>(), library, publisher,
                NullLogger<VodController>.Instance);
        }

        public void Dispose()
        {
            _db.Dispose();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
            if (Directory.Exists(_tempArtwork))
                Directory.Delete(_tempArtwork, recursive: true);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        private void SeedSource(string url = SourceUrl)
            => _db.UpsertSource(new SourceRecord { Url = url, Name = "ARTE" });

        private void SeedVideo(string id = VideoId, string sourceUrl = SourceUrl)
        {
            _db.UpsertVideo(new VideoRecord
            {
                Id = id,
                SourceUrl = sourceUrl,
                NativeId = id,
                ExtractorKey = "Youtube",
                VideoUrl = "https://www.youtube.com/watch?v=" + id,
                Title = "Video " + id,
                FirstSeenAt = DateTimeOffset.UtcNow,
            });
        }

        private void SeedVideoPublished(string id, DateTimeOffset publishedAt)
        {
            _db.UpsertVideo(new VideoRecord
            {
                Id = id,
                SourceUrl = SourceUrl,
                NativeId = id,
                ExtractorKey = "Youtube",
                VideoUrl = "https://www.youtube.com/watch?v=" + id,
                Title = "Video " + id,
                PublishedAt = publishedAt,
                FirstSeenAt = DateTimeOffset.UtcNow,
            });
        }

        // ── ResetVideo ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ResetVideo_LiftsBackoff_MakingVideoImmediatelyEligible()
        {
            // Arrange: a video with 3 consecutive metadata failures a moment ago.
            const int retryMinutes = 15;
            SeedSource();
            SeedVideo();

            var recentAttempt = DateTimeOffset.UtcNow.AddSeconds(-30);
            _db.SetMetadataOutcome(VideoId, CallOutcome.NetworkError, "err1", recentAttempt, countFailure: true);
            _db.SetMetadataOutcome(VideoId, CallOutcome.NetworkError, "err2", recentAttempt, countFailure: true);
            _db.SetMetadataOutcome(VideoId, CallOutcome.NetworkError, "err3", recentAttempt, countFailure: true);

            // Confirm the video is actually in backoff before the reset.
            var before = _db.GetVideoByUuid(VideoId)!;
            Assert.True(
                WaveSelector.IsInBackoff(before, retryMinutes, DateTimeOffset.UtcNow),
                "pre-condition: video must be in backoff before the reset");

            // Act.
            var result = _controller.ResetVideo(VideoId);

            // Assert: 204, and the backoff is lifted.
            Assert.IsType<NoContentResult>(result);
            var after = _db.GetVideoByUuid(VideoId)!;
            Assert.False(
                WaveSelector.IsInBackoff(after, retryMinutes, DateTimeOffset.UtcNow),
                "post-condition: backoff must be gone after the reset");
        }

        [Fact]
        public void ResetVideo_Returns404ForUnknownId_AndLeavesExistingVideoUntouched()
        {
            // Arrange: a real video with a known failure count.
            SeedSource();
            SeedVideo();

            var recentAttempt = DateTimeOffset.UtcNow.AddSeconds(-10);
            _db.SetMetadataOutcome(VideoId, CallOutcome.NetworkError, "err", recentAttempt, countFailure: true);

            var before = _db.GetVideoByUuid(VideoId)!;
            Assert.Equal(1, before.MetadataAttempts); // sanity

            // Act: try to reset a video that does not exist.
            var result = _controller.ResetVideo("ffffffff-ffff-ffff-ffff-ffffffffffff");

            // Assert: 404, and the real video was not modified.
            Assert.IsType<NotFoundObjectResult>(result);
            var after = _db.GetVideoByUuid(VideoId)!;
            Assert.Equal(1, after.MetadataAttempts);
        }

        // ── ResetSource ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ResetSource_ZeroesAllFiveStageFailureCounters()
        {
            // Arrange: each stage gets a DISTINCT failure count (1, 2, 3, 5, 7).
            // If the implementation resets only four out of five, at least one assertion fails.
            SeedSource();

            for (var i = 0; i < 1; i++) _db.SetStageResult(SourceUrl, SourceStage.ShortListing, CallOutcome.NetworkError, "e");
            for (var i = 0; i < 2; i++) _db.SetStageResult(SourceUrl, SourceStage.FullListing, CallOutcome.NetworkError, "e");
            for (var i = 0; i < 3; i++) _db.SetStageResult(SourceUrl, SourceStage.Cleanup, CallOutcome.NetworkError, "e");
            for (var i = 0; i < 5; i++) _db.SetStageResult(SourceUrl, SourceStage.Wave, CallOutcome.NetworkError, "e");
            for (var i = 0; i < 7; i++) _db.SetStageResult(SourceUrl, SourceStage.Checkup, CallOutcome.NetworkError, "e");

            // Confirm the five distinct values are on the row before the reset.
            var before = _db.GetSource(SourceUrl)!;
            Assert.Equal(1, before.ShortListingFailures);
            Assert.Equal(2, before.FullListingFailures);
            Assert.Equal(3, before.CleanupFailures);
            Assert.Equal(5, before.WaveFailures);
            Assert.Equal(7, before.CheckupFailures);

            // Act.
            var result = _controller.ResetSource(SourceUrl);

            // Assert: 204, and all five counters are back to zero.
            Assert.IsType<NoContentResult>(result);
            var after = _db.GetSource(SourceUrl)!;
            Assert.Equal(0, after.ShortListingFailures);
            Assert.Equal(0, after.FullListingFailures);
            Assert.Equal(0, after.CleanupFailures);
            Assert.Equal(0, after.WaveFailures);
            Assert.Equal(0, after.CheckupFailures);
        }

        [Fact]
        public void ResetSource_AlsoLiftsBackoffOnItsVideos()
        {
            // A source reset must propagate to its videos: the whole point is a one-click exit
            // that leaves EVERYTHING eligible again, not just the stage counters.
            const int retryMinutes = 15;
            SeedSource();
            SeedVideo();

            var recentAttempt = DateTimeOffset.UtcNow.AddSeconds(-30);
            _db.SetMetadataOutcome(VideoId, CallOutcome.NetworkError, "err", recentAttempt, countFailure: true);
            _db.SetMetadataOutcome(VideoId, CallOutcome.NetworkError, "err", recentAttempt, countFailure: true);

            var before = _db.GetVideoByUuid(VideoId)!;
            Assert.True(WaveSelector.IsInBackoff(before, retryMinutes, DateTimeOffset.UtcNow),
                "pre-condition: video must be in backoff before the source reset");

            // Act.
            var result = _controller.ResetSource(SourceUrl);

            // Assert: 204, and the video's backoff is gone too.
            Assert.IsType<NoContentResult>(result);
            var after = _db.GetVideoByUuid(VideoId)!;
            Assert.False(WaveSelector.IsInBackoff(after, retryMinutes, DateTimeOffset.UtcNow),
                "post-condition: video backoff must be lifted by the source reset");
        }

        [Fact]
        public void ResetSource_Returns404ForUnknownUrl_AndLeavesExistingSourceUntouched()
        {
            // Arrange: a real source with known failure counts.
            SeedSource();
            for (var i = 0; i < 3; i++) _db.SetStageResult(SourceUrl, SourceStage.Wave, CallOutcome.NetworkError, "e");

            var before = _db.GetSource(SourceUrl)!;
            Assert.Equal(3, before.WaveFailures); // sanity

            // Act: try to reset a source that does not exist.
            var result = _controller.ResetSource("https://nonexistent.example/");

            // Assert: 404, and the real source was not modified.
            Assert.IsType<NotFoundObjectResult>(result);
            var after = _db.GetSource(SourceUrl)!;
            Assert.Equal(3, after.WaveFailures);
        }

        // ── GetSourceDetail — 404 branch (debt from T16) ─────────────────────────────────────────

        [Fact]
        public void GetSourceDetail_Returns404ForUnknownSourceUrl()
        {
            // T16's report noted this branch was not tested. It is three lines once the harness
            // for the reset routes is in place.
            var result = _controller.GetSourceDetail("https://nonexistent.example/");
            Assert.IsType<NotFoundResult>(result.Result);
        }

        [Fact]
        public void GetSourceDetail_PutsTheVideosNeedingAttentionFirst()
        {
            // The panel answers "why did today's video not appear". The database returns its rows
            // oldest publication date first, with undated rows ahead of those, so the one video
            // that failed sits below hundreds of healthy ones and the admin scrolls a channel to
            // find it.
            // The failing video is deliberately last by BOTH remaining sort keys — oldest
            // publication date and highest id — so that neutralising the failure counter puts
            // something else first. Named and dated the other way round, this test holds for an
            // implementation that does not look at the counter at all. Verified by mutation: it
            // did.
            SeedSource();
            SeedVideoPublished("a-fresh", DateTimeOffset.UtcNow.AddDays(-1));
            SeedVideoPublished("b-older", DateTimeOffset.UtcNow.AddDays(-100));
            SeedVideoPublished("z-broken", DateTimeOffset.UtcNow.AddDays(-200));

            var when = DateTimeOffset.UtcNow.AddMinutes(-5);
            _db.SetMetadataOutcome("a-fresh", CallOutcome.Ok, null, when, countFailure: false);
            _db.SetMetadataOutcome("b-older", CallOutcome.Ok, null, when, countFailure: false);
            _db.SetMetadataOutcome("z-broken", CallOutcome.NeedsAuth, "sign in to confirm your age", when, countFailure: true);

            var detail = Assert.IsType<SourceDetail>(
                Assert.IsType<OkObjectResult>(_controller.GetSourceDetail(SourceUrl).Result).Value);

            Assert.Equal(3, detail.TotalVideos);
            Assert.Equal("z-broken", detail.Videos[0].Id);

            // The detail travels intact: it is the whole reason this row is first.
            Assert.Equal("sign in to confirm your age", detail.Videos[0].MetadataDetail);
        }

        [Fact]
        public void GetSourceDetail_CapsTheList_AndSaysHowManyThereAre()
        {
            // The biggest source measured on the author's instance holds 7449 videos, and the page
            // renders every row it is handed as a div plus a two-row table plus a button. Uncapped,
            // one click on Detail ships every video's verbatim stderr in one response and freezes
            // the browser. The total is reported so the panel can say what it is leaving out.
            SeedSource();
            for (var i = 0; i < 250; i++)
                SeedVideo("v-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));

            var detail = Assert.IsType<SourceDetail>(
                Assert.IsType<OkObjectResult>(_controller.GetSourceDetail(SourceUrl).Result).Value);

            Assert.Equal(250, detail.TotalVideos);
            Assert.Equal(200, detail.Videos.Count);
        }
    }
}

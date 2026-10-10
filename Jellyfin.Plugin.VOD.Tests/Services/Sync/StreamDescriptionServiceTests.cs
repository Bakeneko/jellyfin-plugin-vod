using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>Tests for <see cref="StreamDescriptionService"/>.</summary>
    public sealed class StreamDescriptionServiceTests
    {
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly IMediaStreamRepository _repository = Substitute.For<IMediaStreamRepository>();
        private readonly StreamDescriptionService _service;

        /// <summary>Initializes a new instance of the <see cref="StreamDescriptionServiceTests"/> class.</summary>
        public StreamDescriptionServiceTests()
        {
            _db.GetAllSources().Returns([new SourceRecord { Url = "https://example.test/channel" }]);

            // One undescribed item per group: NeedsDescription returns true for any video the batch
            // loop asks about, which is required to exercise the batch-size cap (Finding 3).
            var undescribedItem = new Movie { Id = Guid.NewGuid() };
            _library.GetItemList(Arg.Any<InternalItemsQuery>())
                .Returns(new List<BaseItem> { undescribedItem });
            _repository.GetMediaStreams(Arg.Any<MediaStreamQuery>())
                .Returns(new List<MediaStream>());

            var cache = new PlaylistCache();
            var subtitles = new SubtitleTokens(cache);
            var describer = new StreamDescriber(
                _library,
                _repository,
                Substitute.For<IMediaEncoder>(),
                cache,
                subtitles,
                NullLogger<StreamDescriber>.Instance);

            var ytDlp = new YtDlpService(
                new CookieJar(NullLogger<CookieJar>.Instance),
                NullLogger<YtDlpService>.Instance);

            _service = new StreamDescriptionService(
                _db,
                describer,
                ytDlp,
                Substitute.For<IHttpClientFactory>(),
                NullLogger<StreamDescriptionService>.Instance);
        }

        private static VideoRecord Video(
            string id,
            LiveStatus status = LiveStatus.NotLive,
            DateTimeOffset? publishedAt = null,
            params VideoQuality[] qualities)
            => new()
            {
                Id = id,
                SourceUrl = "https://example.test/channel",
                VideoUrl = $"https://example.test/watch/{id}",
                LiveStatus = status,
                PublishedAt = publishedAt ?? DateTimeOffset.UnixEpoch,
                Extras = new VideoExtras { Version = 1, Qualities = new List<VideoQuality>(qualities) }
            };

        private static readonly VideoQuality[] TwoTiers =
        [
            new(1080, "h264", 30),
            new(2160, "vp9", 30)
        ];

        private static readonly VideoQuality[] OneTier =
        [
            new(1080, "h264", 30)
        ];

        private void DatabaseHolds(params VideoRecord[] videos)
            => _db.GetVideosBySource("https://example.test/channel")
                .Returns(new List<VideoRecord>(videos));

        /// <summary>
        /// Verifies that every finished video published as a version group is a candidate, newest
        /// first — including a group whose only tier is the reference one.
        /// </summary>
        /// <remarks>
        /// Choosing that tier from the primary's page is not probed by Jellyfin, which only probes the
        /// requested item's first source: the playback route's background description then races the
        /// HLS request (measured 2026-10-10, won by 280 ms). A video with no tier has no group, only a
        /// primary, which Jellyfin probes itself.
        /// </remarks>
        [Fact]
        public void Candidates_KeepsEveryFinishedVideoPublishedAsAGroup_NewestFirst()
        {
            DatabaseHolds(
                Video("old", publishedAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), qualities: TwoTiers),
                Video("new", publishedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), qualities: TwoTiers),
                Video("single-tier", publishedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), qualities: OneTier),
                Video("no-tier", publishedAt: new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                Video("live", LiveStatus.Live, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), TwoTiers),
                Video("upcoming", LiveStatus.Upcoming, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), TwoTiers));

            var candidates = _service.Candidates(DateTimeOffset.UtcNow, retryHours: 24);

            Assert.Equal(["single-tier", "new", "old"], candidates.Select(c => c.Id));
        }

        /// <summary>Verifies that a recently-failed video is held back for the retry window.</summary>
        [Fact]
        public void Candidates_AVideoWhoseDescriptionJustFailed_IsHeldBack()
        {
            DatabaseHolds(Video("failing", qualities: TwoTiers));
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

            _service.RememberFailure("failing", now.AddHours(-1));

            Assert.Empty(_service.Candidates(now, retryHours: 24));
            Assert.Single(_service.Candidates(now.AddHours(24), retryHours: 24));
        }

        /// <summary>Verifies that a batch size of zero disables the pass.</summary>
        [Fact]
        public async Task DescribeNextBatchAsync_WithBatchSizeZero_DescribesNothing()
        {
            DatabaseHolds(Video("would-qualify", qualities: TwoTiers));
            var progress = new Progress<double>();

            var described = await _service.DescribeNextBatchAsync(
                new PluginConfiguration { StreamDescriptionBatchSize = 0 },
                progress,
                CancellationToken.None);

            Assert.Equal(0, described);
        }

        /// <summary>
        /// Verifies that a pass describes at most <c>StreamDescriptionBatchSize</c> videos even
        /// when more candidates are available.
        /// </summary>
        /// <remarks>
        /// The count returned by <see cref="StreamDescriptionService.DescribeNextBatchAsync"/> is
        /// the only observable here: whether each description succeeded is irrelevant to the cap,
        /// and the service's resolution path (yt-dlp + HTTP master fetch) cannot be short-circuited
        /// under substitutes. yt-dlp is expected to fail quickly for the fake candidate URLs.
        /// </remarks>
        [Fact]
        public async Task DescribeNextBatchAsync_WithMoreCandidatesThanBatchSize_StopsAtTheCap()
        {
            // Three candidates, batch size one: regardless of probe outcome, exactly one video
            // must be counted per pass.
            DatabaseHolds(
                Video("v1", qualities: TwoTiers),
                Video("v2", qualities: TwoTiers),
                Video("v3", qualities: TwoTiers));

            var described = await _service.DescribeNextBatchAsync(
                new PluginConfiguration { StreamDescriptionBatchSize = 1, StreamDescriptionRetryHours = 24 },
                new Progress<double>(),
                CancellationToken.None);

            Assert.Equal(1, described);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
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
    /// The listing step against a real database file rather than a substitute.
    /// </summary>
    /// <remarks>
    /// Every other test of this step hands it an <see cref="IVodDatabase"/> substitute, which
    /// accepts any write in any order. The database does not: <c>videos.source_url</c> is a foreign
    /// key onto <c>sources(url)</c> and <c>PRAGMA foreign_keys=ON</c> is set, so the ORDER in which
    /// the step writes is part of its contract and no substitute can check it.
    /// <para>
    /// That gap shipped a defect. The step ingested its videos before writing the source row — it
    /// has to, since the name and images come from the same answer as the videos — and on a
    /// database where the source had never been listed, the first video raised
    /// <c>SQLite Error 19: 'FOREIGN KEY constraint failed'</c> and the whole publication task
    /// failed one second in. The suite was green throughout, and a first fill is exactly the case
    /// where no source has a row yet.
    /// </para>
    /// </remarks>
    public sealed class ListingStepDatabaseTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        private const string SourceUrl = "https://www.youtube.com/@testchan/videos";

        private readonly string _dbPath = Path.GetTempFileName();
        private readonly VodDatabase _db;
        private readonly IYtDlpService _ytDlp = Substitute.For<IYtDlpService>();

        public ListingStepDatabaseTests()
        {
            _db = new VodDatabase(_dbPath, NullLogger<VodDatabase>.Instance);
            _db.EnsureCreated();
        }

        public void Dispose()
        {
            _db.Dispose();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private static SourceDefinition Source()
            => new() { Url = SourceUrl, Enabled = true, MaxVideosPerSource = 100 };

        private static PlaylistInfo PlaylistOf(params string[] ids)
        {
            var entries = new List<PlaylistEntry>(ids.Length);
            foreach (var id in ids)
            {
                entries.Add(new PlaylistEntry
                {
                    Id = id,
                    VideoUrl = "https://www.youtube.com/watch?v=" + id,
                    Title = "Video " + id,
                    IeKey = "Youtube",
                    LiveStatus = LiveStatus.NotLive,
                });
            }

            return new PlaylistInfo { Title = "Test Channel", Entries = entries };
        }

        private async Task<ListingOutcome> ListAsync(PlaylistInfo answer)
        {
            _ytDlp.GetPlaylistEntriesAsync(
                    Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(answer);

            var step = new ListingStep(_ytDlp, _db, NullLogger<ListingStep>.Instance);
            return await step.RunAsync(
                new PlannedListing(Source(), Full: true, ListingReason.NeverListed),
                new PluginConfiguration(),
                Now,
                CancellationToken.None);
        }

        [Fact]
        public async Task AFirstListing_OfASourceWithNoRow_IngestsItsVideos()
        {
            // No source row, as a first fill leaves things — and as the schema purge leaves every
            // source. This is the failure that reached the instance.
            Assert.Null(_db.GetSource(SourceUrl));

            var outcome = await ListAsync(PlaylistOf("aaaaaaaaaaa", "bbbbbbbbbbb"));

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Equal(2, outcome.Created);
            Assert.Equal(2, _db.GetVideosBySource(SourceUrl).Count);

            // And the row is filled, not merely brought into being: the name and the count come
            // from the answer that carried the videos, which is why it cannot be written first.
            var record = _db.GetSource(SourceUrl);
            Assert.NotNull(record);
            Assert.Equal("Test Channel", record.Name);
            Assert.Equal(2, record.VideoCount);
            Assert.NotNull(record.FullListedAt);
        }

        [Fact]
        public async Task ASecondListing_DoesNotUndoWhatTheFirstStored()
        {
            // EnsureSource must create and nothing more. If it wrote defaults it would wipe the
            // name, the images and the count on every listing, and the series would be republished
            // under its fallback name each pass.
            await ListAsync(PlaylistOf("aaaaaaaaaaa"));
            var anchor = Guid.NewGuid();
            _db.SetPublishedAnchorId(SourceUrl, anchor);

            var outcome = await ListAsync(PlaylistOf("aaaaaaaaaaa", "bbbbbbbbbbb"));

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Equal(1, outcome.Created);

            var record = _db.GetSource(SourceUrl)!;
            Assert.Equal("Test Channel", record.Name);
            Assert.Equal(2, record.VideoCount);
            Assert.Equal(anchor, record.PublishedAnchorId);
        }
    }
}

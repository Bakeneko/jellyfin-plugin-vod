using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The pass: the loop that drives the five steps until there is no work left.
    /// </summary>
    /// <remarks>
    /// Three of the plan's standing hazards live here, and all three are ways of never stopping or
    /// of blocking something else: the stop test, the per-lane refusal, and the lock's granularity.
    /// Every test below therefore has to distinguish two worlds — and the world to watch out for is
    /// a pass that does NOTHING, which would make half of these assertions pass for the wrong
    /// reason. Each test names the wrong implementation it catches.
    /// <para>
    /// The database substitute here is a small WORKING fake rather than a set of canned answers:
    /// <c>GetDueVideos</c> re-expresses the three due conditions and the backoff (through the
    /// project's own <see cref="WaveSelector.IsInBackoff"/>), and the outcome writers move the
    /// clocks they would move in SQLite. Canned answers would have made "the loop stops" a
    /// property of the fixture instead of a property of the loop — the exact tautology the previous
    /// tasks of this series paid for twice.
    /// </para>
    /// </remarks>
    [Collection(BaseItemStaticsCollection.Name)]
    public sealed class PublicationServiceTests : IDisposable
    {
        // The pass's own clock. Taken from the wall clock, not from a fixed literal: the pass
        // refreshes `now` on every turn after the first, so a fixture pinned in the past would make
        // every source look overdue again on turn 2 and the expected turn counts would depend on
        // the day the suite runs.
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        private readonly string _salt = Guid.NewGuid().ToString("N")[..8];

        private const string LibraryName = "Films";
        private const string LocalBaseUrl = "http://localhost:8096";

        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(), "vod-tests-pass-" + Guid.NewGuid().ToString("N"));

        private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
        private readonly ILibraryManager _describerLibrary = Substitute.For<ILibraryManager>();
        private readonly IMediaStreamRepository _streams = Substitute.For<IMediaStreamRepository>();
        private readonly IVodDatabase _db = Substitute.For<IVodDatabase>();
        private readonly IYtDlpService _ytDlp = Substitute.For<IYtDlpService>();
        private readonly IStreamDescriber _describer = Substitute.For<IStreamDescriber>();
        private readonly RecordingLogger<PublicationService> _log = new();

        private readonly List<BaseItem> _published = [];

        // The fake database's state.
        private readonly Dictionary<string, SourceRecord> _records = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<VideoRecord>> _videos = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _announced = new(StringComparer.OrdinalIgnoreCase);

        // What the platform answers, per video URL, when the test wants something other than success.
        private readonly Dictionary<string, MetadataResult> _metadata = new(StringComparer.OrdinalIgnoreCase);
        private MetadataResult? _metadataForEveryVideo;

        /// <summary>
        /// Every call the pass made that a test cares about the ORDER of, in order: the yt-dlp
        /// calls, the clock clearing, each step's stage result, and each retirement.
        /// </summary>
        private readonly List<string> _trace = [];

        private readonly List<double> _progress = [];

        private readonly Folder _anchor;
        private readonly PublicationService _pass;

        public PublicationServiceTests()
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
            _library.GetNewItemId(Arg.Any<string>(), Arg.Any<Type>())
                .Returns(call => DeterministicId(call.ArgAt<string>(0) + "|" + call.ArgAt<Type>(1).Name));
            _library.When(l => l.CreateItem(Arg.Any<BaseItem>(), Arg.Any<BaseItem>()))
                .Do(call => _published.Add(call.ArgAt<BaseItem>(0)));
            _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(_ => _published.ToList());

            _describerLibrary.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
            _streams.GetMediaStreams(Arg.Any<MediaStreamQuery>()).Returns([]);

            WireDatabase();
            WireYtDlp();

            var cache = new PlaylistCache();
            var checkupDescriber = new StreamDescriber(
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

            _pass = new PublicationService(
                _db,
                new ListingStep(_ytDlp, _db, NullLogger<ListingStep>.Instance),
                new CleanupStep(_db, publisher, NullLogger<CleanupStep>.Instance),
                new WaveStep(_ytDlp, DescriptionStep(), _db, NullLogger<WaveStep>.Instance),
                new CheckupStep(_db, publisher, checkupDescriber, NullLogger<CheckupStep>.Instance),
                _log);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        // ── URLs ─────────────────────────────────────────────────────────────────────────────────
        //
        // Salted per test instance, because SourceLock keeps one semaphore per URL in a
        // process-wide dictionary: a URL shared between two tests would let a lock held by one
        // leak into the other.

        private string Youtube => $"https://www.youtube.com/@chan-{_salt}/videos";

        private string Arte => $"https://www.arte.tv/fr/videos/RC-{_salt}/";

        private string Alpha => $"https://alpha.example/{_salt}";

        private string Beta => $"https://beta.example/{_salt}";

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

        private static Guid DeterministicId(string key)
            => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

        private SourceDefinition Src(
            string url,
            bool enabled = true,
            int retentionDays = 0,
            int listingIntervalMinutes = 720)
            => new()
            {
                Url = url,
                Name = "Chan " + _salt,
                Enabled = enabled,
                Mode = SourceMode.Movies,
                RetentionDays = retentionDays,
                ListingIntervalMinutes = listingIntervalMinutes,
            };

        private PluginConfiguration Config(params SourceDefinition[] sources)
        {
            var config = new PluginConfiguration
            {
                MoviesLibraryName = LibraryName,

                // Left empty on purpose: a source in Movies mode must not drag a second library
                // into the sweep, and an empty name is how the routing says "none configured".
                SeriesLibraryName = string.Empty,
                LocalBaseUrl = LocalBaseUrl,
            };
            config.Sources.AddRange(sources);
            return config;
        }

        /// <summary>
        /// A stored row that makes a source look completely up to date, so that nothing about the
        /// LISTING schedule can put it in the work list and a test can be about the wave alone.
        /// </summary>
        private SourceRecord UpToDate(SourceDefinition source)
        {
            var record = new SourceRecord
            {
                Url = source.Url,
                Key = VodNativeId.ForSource(source.Url),
                Name = "Chan " + _salt,
                FullListedAt = _now.AddMinutes(-1),
                ShortListedAt = _now.AddMinutes(-1),
                ConfigFingerprint = SourceFingerprint.Of(source),
            };
            _records[source.Url] = record;
            return record;
        }

        /// <summary>A row the platform also lists, as an already-known video.</summary>
        private VideoRecord AddVideo(
            string sourceUrl,
            string nativeId,
            bool fetched = false,
            bool described = false,
            DateTimeOffset? fetchedAt = null)
        {
            var row = new VideoRecord
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = sourceUrl,
                NativeId = nativeId,
                ExtractorKey = "Test",
                VideoUrl = VideoUrlFor(sourceUrl, nativeId),
                Title = "A video called " + nativeId,
                ListingTitle = "A video called " + nativeId,
                LiveStatus = LiveStatus.NotLive,
                Extras = new VideoExtras(),
                FirstSeenAt = _now.AddDays(-3),
            };

            if (fetched)
            {
                row.MetadataAt = fetchedAt ?? _now.AddMinutes(-1);
                row.MetadataResult = CallOutcome.Ok;
                row.PublishedAt = _now.AddDays(-2);
                row.Extras = row.Extras with { Qualities = [new VideoQuality(1080, "h264", 30)] };
            }

            // Ok, not merely a stamped clock: the checkup only marks a video for describing again
            // when a description SUCCEEDED before, so a row that left the result at its None
            // default would say "there was nothing to describe" and never be marked.
            if (described)
            {
                row.DescribedAt = fetchedAt ?? _now.AddMinutes(-1);
                row.DescribeResult = CallOutcome.Ok;
            }

            Rows(sourceUrl).Add(row);
            Announced(sourceUrl).Add(nativeId);
            return row;
        }

        /// <summary>A video the platform lists but the database has never seen.</summary>
        private void AnnounceNewVideo(string sourceUrl, string nativeId)
            => Announced(sourceUrl).Add(nativeId);

        private static string VideoUrlFor(string sourceUrl, string nativeId)
            => sourceUrl.TrimEnd('/') + "/watch/" + nativeId;

        private List<VideoRecord> Rows(string sourceUrl)
        {
            if (!_videos.TryGetValue(sourceUrl, out var rows))
                _videos[sourceUrl] = rows = [];
            return rows;
        }

        private List<string> Announced(string sourceUrl)
        {
            if (!_announced.TryGetValue(sourceUrl, out var ids))
                _announced[sourceUrl] = ids = [];
            return ids;
        }

        private IEnumerable<VideoRecord> AllRows() => _videos.Values.SelectMany(v => v);

        private VideoRecord? Row(string id) => AllRows().FirstOrDefault(r => r.Id == id);

        private VideoMetadata OkMeta() => new()
        {
            Id = "native",
            Title = "Metadata Title",
            FormatCount = 1,
            ManifestUrl = "https://cdn.test/master.m3u8",
            LiveStatus = LiveStatus.NotLive,
            Qualities = [new VideoQuality(1080, "h264", 30)],
            UploadDate = _now.AddDays(-2).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
        };

        private static MetadataResult Timeout() => new(MetadataOutcome.Failed, null, TimedOut: true);

        /// <summary>A successful answer that offers no HLS master — the video cannot be described.</summary>
        /// <remarks>
        /// Not a contrivance: measured on the test instance, ninety-four percent of YouTube videos
        /// are described, so roughly six percent are in this state — several hundred of them.
        /// </remarks>
        private MetadataResult WithoutAManifest()
            => MetadataResult.From(new VideoMetadata
            {
                Id = "native",
                Title = "Metadata Title",
                FormatCount = 1,
                ManifestUrl = null,
                LiveStatus = LiveStatus.NotLive,
                Qualities = [new VideoQuality(1080, "h264", 30)],
                UploadDate = _now.AddDays(-2)
                    .ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            });

        private static MetadataResult Unavailable()
            => new(MetadataOutcome.Failed, null, StdErr: "ERROR: Video unavailable");

        // ── The pass under test ──────────────────────────────────────────────────────────────────

        private Task<PassReport> RunAsync(
            PluginConfiguration config, DateTimeOffset? now = null, CancellationToken token = default)
            => _pass.RunPassAsync(
                config,
                new DelegateProgress(value => _progress.Add(value)),
                now ?? _now,
                token);

        // ── The yt-dlp seam ──────────────────────────────────────────────────────────────────────

        private void WireYtDlp()
        {
            _ytDlp.GetPlaylistEntriesAsync(
                    Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(call => ListingFor(call.ArgAt<string>(0)));

            _ytDlp.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => MetadataFor(call.ArgAt<string>(0)));

            // Streams for every video: without them the wave would never describe, described_at
            // would stay null, and the video would stay due for ever — which would make every
            // multi-turn test below run to the turn cap.
            _describer.DescribeVideoAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<VideoRecord>(),
                    Arg.Any<string>(), Arg.Any<Uri>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, IReadOnlyList<MediaStream>>
                {
                    [DescribedStreams.PrimaryKey] =
                        [new MediaStream { Type = MediaStreamType.Video, Width = 1920, Height = 1080 }],
                });
        }

        private PlaylistInfo ListingFor(string sourceUrl)
        {
            _trace.Add("listing:" + sourceUrl);

            var entries = Announced(sourceUrl)
                .Select(nativeId => new PlaylistEntry
                {
                    Id = nativeId,
                    VideoUrl = VideoUrlFor(sourceUrl, nativeId),
                    Title = "A video called " + nativeId,
                    IeKey = "Test",
                    LiveStatus = LiveStatus.NotLive,
                })
                .ToList();

            return new PlaylistInfo { Title = "Chan " + _salt, Entries = entries };
        }

        private MetadataResult MetadataFor(string videoUrl)
        {
            _trace.Add("metadata:" + videoUrl);

            if (_metadata.TryGetValue(videoUrl, out var canned))
                return canned;

            return _metadataForEveryVideo ?? MetadataResult.From(OkMeta());
        }

        private StreamDescriptionStep DescriptionStep()
        {
            const string Master = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
                https://cdn.test/v-1080.m3u8
                """;

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>())
                .Returns(_ => new HttpClient(new StubHandler(Master), disposeHandler: false));

            return new StreamDescriptionStep(factory, _describer);
        }

        // ── The fake database ────────────────────────────────────────────────────────────────────

        private void WireDatabase()
        {
            _db.GetAllSources().Returns(_ => (IReadOnlyList<SourceRecord>)_records.Values.ToList());
            _db.GetSource(Arg.Any<string>())
                .Returns(call => _records.GetValueOrDefault(call.ArgAt<string>(0)));
            _db.GetVideosBySource(Arg.Any<string>())
                .Returns(call => (IReadOnlyList<VideoRecord>)Rows(call.ArgAt<string>(0))
                    .OrderBy(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                    .ToList());
            _db.GetVideoIdsWithMetadata().Returns(_ => (IReadOnlyList<string>)AllRows()
                .Where(r => r.MetadataAt is not null)
                .Select(r => r.Id)
                .ToList());
            _db.GetVideosGoneFromLastFullListing(Arg.Any<string>(), Arg.Any<DateTimeOffset>())
                .Returns(_ => (IReadOnlyList<string>)new List<string>());

            // The three due conditions and the backoff, as the SQL expresses them. The backoff
            // itself comes from the project's own mirror, so this fake cannot be kinder than
            // production about a video that failed.
            _db.GetDueVideos(
                    Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(),
                    Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(call =>
                {
                    var url = call.ArgAt<string>(0);
                    var when = call.ArgAt<DateTimeOffset>(1);
                    var retryMinutes = call.ArgAt<int>(2);
                    var maxAgeDays = call.ArgAt<int>(3);
                    var retentionDays = call.ArgAt<int>(4);
                    var limit = call.ArgAt<int>(5);

                    var due = new List<VideoRecord>();
                    foreach (var row in Rows(url))
                    {
                        if (WaveSelector.IsInBackoff(row, retryMinutes, when))
                            continue;

                        var staleMetadata = row.MetadataAt is null
                            || row.MetadataAt.Value < when.AddDays(-maxAgeDays);

                        var missingDescription = row.DescribedAt is null
                            && row.LiveStatus == LiveStatus.NotLive
                            && (retentionDays <= 0
                                || row.PublishedAt is null
                                || row.PublishedAt.Value >= when.AddDays(-retentionDays));

                        if (staleMetadata || missingDescription)
                            due.Add(row);
                    }

                    return (IReadOnlyList<VideoRecord>)due.Take(Math.Max(0, limit)).ToList();
                });

            _db.When(d => d.UpsertVideo(Arg.Any<VideoRecord>())).Do(call =>
            {
                var row = call.ArgAt<VideoRecord>(0);
                var rows = Rows(row.SourceUrl);
                if (!rows.Any(r => r.Id == row.Id))
                    rows.Add(row);
            });

            _db.When(d => d.UpsertSource(Arg.Any<SourceRecord>())).Do(call =>
            {
                var incoming = call.ArgAt<SourceRecord>(0);
                if (_records.TryGetValue(incoming.Url, out var stored))
                {
                    // The real upsert touches the identity columns only; the clocks, the
                    // fingerprint and the anchor have writers of their own.
                    stored.Name = incoming.Name;
                    stored.SeriesSynopsis = incoming.SeriesSynopsis;
                    stored.SeriesThumbnailUrl = incoming.SeriesThumbnailUrl;
                    stored.SeriesBannerUrl = incoming.SeriesBannerUrl;
                    stored.Tags = incoming.Tags;
                    stored.VideoCount = incoming.VideoCount;
                }
                else
                {
                    _records[incoming.Url] = incoming;
                }
            });

            _db.When(d => d.SetListingClock(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>()))
                .Do(call =>
                {
                    var record = _records.GetValueOrDefault(call.ArgAt<string>(0));
                    if (record is null)
                        return;

                    var when = call.ArgAt<DateTimeOffset>(2);
                    record.ShortListedAt = when;
                    if (call.ArgAt<bool>(1))
                        record.FullListedAt = when;
                });

            _db.When(d => d.ClearFullListedAt(Arg.Any<string>())).Do(call =>
            {
                _trace.Add("clear-full-listed-at:" + call.ArgAt<string>(0));
                var record = _records.GetValueOrDefault(call.ArgAt<string>(0));
                if (record is not null)
                    record.FullListedAt = null;
            });

            _db.When(d => d.SetConfigFingerprint(Arg.Any<string>(), Arg.Any<string>())).Do(call =>
            {
                var record = _records.GetValueOrDefault(call.ArgAt<string>(0));
                if (record is not null)
                    record.ConfigFingerprint = call.ArgAt<string>(1);
            });

            _db.When(d => d.SetMetadataOutcome(
                    Arg.Any<string>(), Arg.Any<CallOutcome>(), Arg.Any<string>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<bool>()))
                .Do(call =>
                {
                    var row = Row(call.ArgAt<string>(0));
                    if (row is null)
                        return;

                    var outcome = call.ArgAt<CallOutcome>(1);
                    row.MetadataAt = call.ArgAt<DateTimeOffset>(3);
                    row.MetadataResult = outcome;
                    row.MetadataDetail = call.ArgAt<string>(2);

                    // Success resets the counter whatever the flag says; a failure advances it only
                    // when it counts against the video. That asymmetry is what makes a rate limit
                    // or a timeout leave the video due — and the loop able to spin.
                    if (outcome == CallOutcome.Ok)
                        row.MetadataAttempts = 0;
                    else if (call.ArgAt<bool>(4))
                        row.MetadataAttempts++;
                });

            _db.When(d => d.SetDescribeOutcome(
                    Arg.Any<string>(), Arg.Any<CallOutcome>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>()))
                .Do(call =>
                {
                    var row = Row(call.ArgAt<string>(0));
                    if (row is null)
                        return;

                    var outcome = call.ArgAt<CallOutcome>(1);
                    row.DescribedAt = call.ArgAt<DateTimeOffset>(3);
                    row.DescribeResult = outcome;
                    row.DescribeDetail = call.ArgAt<string>(2);

                    // Ok and None both reset the counter, as the SQL does: None means the
                    // describing concluded there is nothing to describe, so nothing failed.
                    if (outcome is CallOutcome.Ok or CallOutcome.None)
                        row.DescribeAttempts = 0;
                    else
                        row.DescribeAttempts++;
                });

            _db.When(d => d.ClearDescribedAt(Arg.Any<string>())).Do(call =>
            {
                var row = Row(call.ArgAt<string>(0));
                if (row is null)
                    return;

                row.DescribedAt = null;
                row.DescribeResult = CallOutcome.None;
                row.DescribeDetail = null;
                row.DescribeAttempts = 0;
            });

            _db.When(d => d.MarkMetadataStale(Arg.Any<string>())).Do(call =>
            {
                var row = Row(call.ArgAt<string>(0));
                if (row is not null)
                    row.MetadataAt = DateTimeOffset.UnixEpoch;
            });

            _db.When(d => d.DeleteVideo(Arg.Any<string>())).Do(call =>
            {
                var id = call.ArgAt<string>(0);
                foreach (var rows in _videos.Values)
                    rows.RemoveAll(r => r.Id == id);
            });

            _db.When(d => d.DeleteSource(Arg.Any<string>())).Do(call =>
            {
                var url = call.ArgAt<string>(0);
                _trace.Add("retire:" + url);
                _records.Remove(url);
                _videos.Remove(url);
            });

            _db.When(d => d.SetStageResult(
                    Arg.Any<string>(), Arg.Any<SourceStage>(), Arg.Any<CallOutcome>(), Arg.Any<string>()))
                .Do(call =>
                {
                    var url = call.ArgAt<string>(0);
                    var stage = call.ArgAt<SourceStage>(1);
                    var outcome = call.ArgAt<CallOutcome>(2);
                    var detail = call.ArgAt<string>(3);
                    _trace.Add($"{stage}:{url}");

                    // Created if absent, as the real statement does: the row is otherwise made by a
                    // listing that succeeds, and a source that only ever fails would record nothing.
                    if (!_records.TryGetValue(url, out var record))
                    {
                        record = new SourceRecord { Url = url };
                        _records[url] = record;
                    }

                    var failed = outcome != CallOutcome.Ok;
                    switch (stage)
                    {
                        case SourceStage.ShortListing:
                            record.ShortListingResult = outcome;
                            record.ShortListingDetail = detail;
                            record.ShortListingFailures = failed ? record.ShortListingFailures + 1 : 0;
                            break;
                        case SourceStage.FullListing:
                            record.FullListingResult = outcome;
                            record.FullListingDetail = detail;
                            record.FullListingFailures = failed ? record.FullListingFailures + 1 : 0;
                            break;
                        case SourceStage.Cleanup:
                            record.CleanupResult = outcome;
                            record.CleanupDetail = detail;
                            record.CleanupFailures = failed ? record.CleanupFailures + 1 : 0;
                            break;
                        case SourceStage.Wave:
                            record.WaveResult = outcome;
                            record.WaveDetail = detail;
                            record.WaveFailures = failed ? record.WaveFailures + 1 : 0;
                            break;
                        case SourceStage.Checkup:
                            record.CheckupResult = outcome;
                            record.CheckupDetail = detail;
                            record.CheckupFailures = failed ? record.CheckupFailures + 1 : 0;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(call));
                    }
                });
        }

        // ── Helpers for the assertions ───────────────────────────────────────────────────────────

        private int FirstIndexOf(string traced)
        {
            var index = _trace.IndexOf(traced);
            Assert.True(index >= 0, $"'{traced}' is absent from the trace: {string.Join(", ", _trace)}");
            return index;
        }

        private bool Logged(LogLevel level, string fragment)
            => _log.Entries.Any(e => e.Level == level
                && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Whether the lock for <paramref name="sourceUrl"/> can be taken within
        /// <paramref name="within"/>, from the outside, exactly as the live probe would take it.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT a look inside <see cref="SourceLock"/>: it exposes no state, and a test
        /// that reached into it by reflection would prove an injection scope rather than a
        /// behaviour. Contending for the real lock is the only honest question.
        /// </remarks>
        private static bool LockIsFree(string sourceUrl, TimeSpan within)
        {
            using var cts = new CancellationTokenSource(within);
            try
            {
                using var handle = SourceLock.AcquireAsync(sourceUrl, cts.Token).GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        // ── A pass with nothing to do ────────────────────────────────────────────────────────────

        [Fact]
        public async Task APassWithNothingDue_EndsAfterOneTurn_AndSaysSoAtDebug()
        {
            // Catches a loop whose stop test is wrong in the cheap direction (a second turn that
            // finds the same nothing), and the silence that made an operator conclude, in
            // production, that the task was stuck when it had simply nothing to do.
            var source = Src(Youtube);
            UpToDate(source);
            AddVideo(Youtube, "one", fetched: true, described: true);

            var report = await RunAsync(Config(source));

            Assert.Equal(1, report.Turns);
            Assert.Equal(0, report.SourcesListed);
            Assert.Equal(0, report.VideosFetched);
            Assert.Empty(report.RefusedLanes);
            Assert.False(report.HitTurnCap);

            // Not a single platform call: a source with no work must cost nothing at all.
            Assert.Empty(_trace);
            Assert.True(Logged(LogLevel.Debug, "nothing to do"),
                "A pass that found no work must say so at debug.");
        }

        [Fact]
        public async Task APassWithWorkInTheFirstTurnOnly_RunsExactlyTwoTurns()
        {
            // Catches both halves of the stop test: a loop that stops after the first turn without
            // asking again, and a loop that asks again but cannot tell that the work is done.
            var source = Src(Youtube);
            AnnounceNewVideo(Youtube, "fresh");

            var report = await RunAsync(Config(source));

            Assert.Equal(2, report.Turns);
            Assert.Equal(1, report.SourcesListed);
            Assert.Equal(1, report.VideosFetched);
            Assert.Equal(1, report.VideosPublished);
            Assert.False(report.HitTurnCap);

            // The listing ran once, not once per turn: the second turn had nothing to list.
            Assert.Single(_trace, t => t == "listing:" + Youtube);
        }

        // ── A platform that refuses ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task APlatformThatRefusesEverything_EndsThePass_InsteadOfSpinning()
        {
            // THE test of this task. A timeout deliberately does not count against the video, so it
            // produces no backoff: the video stays due for ever. Without the per-lane refusal the
            // loop would spin at one yt-dlp call per turn until the 2000-turn cap — which is why
            // the assertions below are a turn count and a CALL count, not merely "it returned".
            var source = Src(Youtube);
            UpToDate(source);
            AddVideo(Youtube, "one");
            _metadataForEveryVideo = Timeout();

            var report = await RunAsync(Config(source));

            Assert.Equal(2, report.Turns);
            Assert.False(report.HitTurnCap);
            Assert.Equal([PlatformLane.Youtube], report.RefusedLanes);

            // One call for the whole pass. The lane is refused from the first abandon, so the
            // second turn must not even look at this source again.
            Assert.Single(_trace, t => t.StartsWith("metadata:", StringComparison.Ordinal));

            // The video is still due — nothing was written to make it wait. That is precisely why
            // the refusal has to come from the lane and not from the video's backoff.
            Assert.Equal(0, Rows(Youtube)[0].MetadataAttempts);
        }

        [Fact]
        public async Task AVideoThatCanNeverBeDescribed_DoesNotMakeThePassSpin()
        {
            // A video is selected for a wave partly BECAUSE it has never been described. For a
            // video the platform offers no HLS master for, describing can never succeed — and if
            // nothing is written, its metadata fetch succeeds on every turn, which RESETS its
            // failure counter, so no backoff ever starts and the video stays due for ever.
            //
            // The pass would then run to its 2000-turn cap at one yt-dlp call per turn, until the
            // platform's own rate limit became the stopping mechanism: thousands of calls to learn
            // nothing, which is the anti-pattern this whole rework exists to remove. Measured on
            // the test instance, roughly six percent of YouTube videos are in this state.
            //
            // The fix is that the wave stamps the describe clock with "nothing to describe", which
            // takes the video out of the selection until its metadata goes stale — the thirty-day
            // refresh is the retry. These assertions are a turn count and a CALL count, because
            // "it returned" would hold even while spinning.
            var source = Src(Youtube);
            UpToDate(source);
            AddVideo(Youtube, "no-hls");
            _metadataForEveryVideo = WithoutAManifest();

            var report = await RunAsync(Config(source));

            Assert.Equal(2, report.Turns);
            Assert.False(report.HitTurnCap);
            Assert.Empty(report.RefusedLanes);

            // One call for the whole pass: the second turn must find nothing due.
            Assert.Single(_trace, t => t.StartsWith("metadata:", StringComparison.Ordinal));

            // Stamped, and not counted as a failure — nothing failed.
            var row = Rows(Youtube)[0];
            Assert.NotNull(row.DescribedAt);
            Assert.Equal(CallOutcome.None, row.DescribeResult);
            Assert.Equal(0, row.DescribeAttempts);
            Assert.NotNull(row.DescribeDetail);
        }

        [Fact]
        public async Task AFailedListing_IsAttemptedOnceAndNotOnEveryTurnOfThePass()
        {
            // A listing failure deliberately writes no clock, so that the source stays due instead
            // of looking up to date. But the work list is rebuilt from the database on every turn,
            // and nothing else bounds a listing: the source comes back with the same reason, for
            // ever. An admin who mistypes a channel URL turns every pass into a 2000-turn spin of
            // FULL channel listings — unlimited when MaxVideosPerSource is 0 — which holds the
            // platform's lane for hours and ends in the rate limit this rework exists to avoid.
            //
            // One attempt per source per pass is the bound: the retry is the next run of the
            // scheduled task. The assertion is a CALL count, not a turn count alone, because
            // "the pass returned" holds while spinning.
            var source = Src(Youtube);

            _ytDlp.GetPlaylistEntriesAsync(
                    Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(call =>
                {
                    _trace.Add("listing:" + call.ArgAt<string>(0));
                    return (PlaylistInfo?)null;
                });

            var report = await RunAsync(Config(source));

            Assert.False(report.HitTurnCap);
            Assert.Equal(2, report.Turns);
            Assert.Single(_trace, t => t.StartsWith("listing:", StringComparison.Ordinal));

            // The failure is still recorded: the source must stay due for the NEXT pass, and the
            // page must be able to show why. Bounding the retry must not hide the outage.
            Assert.Equal(0, report.SourcesListed);
            var record = _records[Youtube];
            Assert.Equal(CallOutcome.ExtractorError, record.FullListingResult);
            Assert.Null(record.FullListedAt);
        }

        [Fact]
        public async Task AConfigurationChangeTheCheckupCannotStamp_DoesNotRelistOnEveryTurn()
        {
            // ConfigurationChanged is recomputed from the stored fingerprint on every turn, and the
            // pass clears FullListedAt each time it sees it. The fingerprint is stamped only by a
            // checkup that reaches its end, and a checkup with no library for the source's mode
            // returns early — correctly, since it published nothing.
            //
            // The two together are a loop: clear the clock, list the whole channel, return early
            // without stamping, repeat. Switching a published source to Series mode while
            // SeriesLibraryName is empty is enough, and the page shows nothing wrong.
            //
            // Recomputing the trigger per turn rather than per pass is the defect; not stamping on
            // failure is right and stays.
            var source = Src(Youtube);
            source.Mode = SourceMode.Series;

            var record = UpToDate(source);
            record.ConfigFingerprint = "a fingerprint from before the mode was changed";

            // Already worked, so the wave cannot be what keeps the pass alive: this test is about
            // the listing being planned again and again.
            AddVideo(Youtube, "v-one", fetched: true, described: true);
            AnnounceNewVideo(Youtube, "v-one");

            var report = await RunAsync(Config(source));

            Assert.False(report.HitTurnCap);
            Assert.Equal(2, report.Turns);
            Assert.Single(_trace, t => t.StartsWith("listing:", StringComparison.Ordinal));

            // The fingerprint is still NOT stamped — the checkup could not act, and claiming the
            // change was applied would hide an unpublishable source for ever.
            Assert.Equal("a fingerprint from before the mode was changed", record.ConfigFingerprint);
        }

        [Fact]
        public async Task ARefusedLane_DoesNotStopTheOtherLane_InTheSameTurn()
        {
            // Without this test the "stop the whole pass" variant would pass too. A YouTube rate
            // limit during a first fill must not stop Arte, which is fine: the two platforms have
            // independent budgets.
            //
            // The order matters and is not left to chance: YouTube has a planned listing, so it
            // comes before Arte, which is only in the work list for its due videos. Arte is
            // therefore reached AFTER the refusal happened.
            var youtube = Src(Youtube);
            var arte = Src(Arte);
            AnnounceNewVideo(Youtube, "yt-one");
            UpToDate(arte);
            var arteVideo = AddVideo(Arte, "arte-one");

            _metadata[VideoUrlFor(Youtube, "yt-one")] = Timeout();

            var report = await RunAsync(Config(youtube, arte));

            Assert.Equal([PlatformLane.Youtube], report.RefusedLanes);
            Assert.False(report.HitTurnCap);

            // Arte was worked after the refusal, in the same turn.
            var refusal = FirstIndexOf("metadata:" + VideoUrlFor(Youtube, "yt-one"));
            var arteWave = FirstIndexOf("metadata:" + arteVideo.VideoUrl);
            Assert.True(arteWave > refusal, "Arte must be worked after YouTube refused, not before.");
            Assert.Equal(1, report.VideosFetched);
            Assert.Contains($"{SourceStage.Wave}:{Arte}", _trace);
        }

        // ── The lock ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheSourceLock_IsHeldForOneSourcesWholeBlock_AndReleasedBeforeTheNext()
        {
            // Two worlds at once: a pass that takes no lock at all (the first assertion fails), and
            // a pass that holds one lock over the whole loop (the second fails). The second is the
            // one that matters in production — a first fill runs for hours, and a lock held across
            // it would keep the live probe out for all that time, so a broadcast in progress would
            // never appear.
            var alpha = Src(Alpha);
            var beta = Src(Beta);
            AddVideo(Alpha, "a-one", fetched: true, described: true);
            AddVideo(Beta, "b-one", fetched: true, described: true);

            var observations = new List<(string Source, bool OwnLockFree, bool OtherLockFree)>();

            // Observed from inside the listing call, which runs inside the source's own block.
            _ytDlp.GetPlaylistEntriesAsync(
                    Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(call =>
                {
                    var url = call.ArgAt<string>(0);
                    var other = url == Alpha ? Beta : Alpha;
                    observations.Add((
                        url,
                        LockIsFree(url, TimeSpan.FromMilliseconds(150)),
                        LockIsFree(other, TimeSpan.FromMilliseconds(150))));
                    return ListingFor(url);
                });

            // A deadlock guard, not a timeout for slowness. A pass that failed to release the lock
            // would ask for the same source's semaphore again on a later turn and wait for itself
            // for ever; without this the test would hang the whole suite instead of failing, and a
            // test that hangs is worse than one that fails.
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await _pass.RunPassAsync(
                Config(alpha, beta),
                new DelegateProgress(value => _progress.Add(value)),
                _now,
                guard.Token);

            Assert.Equal(2, observations.Count);

            // Its own lock is held for the whole block — a→e, not merely around the wave. The
            // checkup is the step that reconciles, and reconciling DELETES library items.
            Assert.All(observations, o => Assert.False(
                o.OwnLockFree, $"{o.Source}'s own lock must be held while its block runs."));

            // And the first source's lock is free again by the time the second one is worked.
            Assert.True(observations[1].OtherLockFree,
                "The previous source's lock must be released before the next source is worked.");
        }

        // ── Cancellation ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ACancellationBetweenTwoSources_PropagatesAndLeavesTheFirstSourceFinished()
        {
            // Jellyfin restarts fall mid-pass and the state lives in the database, so a source must
            // end up either untouched or entirely worked. Swallowing the cancellation would make
            // the pass report a success it did not achieve.
            var alpha = Src(Alpha);
            var beta = Src(Beta);
            AddVideo(Alpha, "a-one", fetched: true, described: true);
            AddVideo(Beta, "b-one", fetched: true, described: true);

            using var cts = new CancellationTokenSource();

            // Cancelled from the progress report, which the pass makes once a source is finished:
            // the cancellation therefore lands exactly between two sources.
            var progress = new DelegateProgress(value =>
            {
                _progress.Add(value);
                cts.Cancel();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _pass.RunPassAsync(Config(alpha, beta), progress, _now, cts.Token));

            // The first source was worked to the end, and its clocks stay written.
            Assert.Contains($"{SourceStage.Checkup}:{Alpha}", _trace);
            _db.Received(1).SetListingClock(Alpha, true, _now);

            // The second was not touched at all.
            Assert.DoesNotContain("listing:" + Beta, _trace);
            _db.DidNotReceive().SetListingClock(Beta, Arg.Any<bool>(), Arg.Any<DateTimeOffset>());
        }

        // ── Progress ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Progress_IsReportedForEverySourceOfTheTurn()
        {
            // A pass of several hours that reports nothing is indistinguishable from a blocked one —
            // the symptom that made a working task look stuck in production. Per TURN would not be
            // enough: one turn of a first fill is minutes long.
            var alpha = Src(Alpha);
            var beta = Src(Beta);
            AddVideo(Alpha, "a-one", fetched: true, described: true);
            AddVideo(Beta, "b-one", fetched: true, described: true);

            await RunAsync(Config(alpha, beta));

            // Two sources in the first turn: a report after each, and 100 to finish.
            Assert.True(_progress.Count >= 3, $"Expected a report per source; got {_progress.Count}.");
            Assert.All(_progress, value => Assert.InRange(value, 0d, 100d));
            Assert.Contains(50d, _progress);
            Assert.Equal(100d, _progress[^1]);
        }

        // ── The turn's shape ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AConfigurationChange_ClearsTheFullListingClockBeforeTheListing()
        {
            // Clearing it afterwards would be worse than not clearing it at all: the listing would
            // write the clock and the clearing would then undo a listing that actually ran.
            var source = Src(Youtube);
            var record = UpToDate(source);
            record.ConfigFingerprint = "a fingerprint from another configuration";
            AddVideo(Youtube, "one", fetched: true, described: true);

            var report = await RunAsync(Config(source));

            Assert.Equal(1, report.SourcesListed);
            Assert.True(
                FirstIndexOf("clear-full-listed-at:" + Youtube) < FirstIndexOf("listing:" + Youtube),
                "The full-listing clock must be cleared BEFORE the listing runs.");
        }

        [Fact]
        public async Task AShortListing_DoesNotRunTheVideoCleanup()
        {
            // A short listing touches 30 entries only. Running the cleanup on one would retire
            // everything past position 30 — most of the source, and the watch state attached to it.
            var source = Src(Youtube);
            var record = UpToDate(source);
            record.ShortListedAt = _now.AddDays(-30);
            AddVideo(Youtube, "one", fetched: true, described: true);

            await RunAsync(Config(source));

            // The listing did run, and it was the short one.
            Assert.Contains($"{SourceStage.ShortListing}:{Youtube}", _trace);
            Assert.DoesNotContain($"{SourceStage.Cleanup}:{Youtube}", _trace);
            _db.DidNotReceive().GetVideosGoneFromLastFullListing(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>());
        }

        [Fact]
        public async Task AFullListing_RunsTheVideoCleanup_AtTheListingsOwnTimestamp()
        {
            // The other half of the previous test — without it, "no cleanup" would also be
            // satisfied by a pass that never cleans up at all.
            //
            // The timestamp is the load-bearing part: any time other than the one the listing used
            // for TouchListedAt would make the videos it has just touched look absent, and the
            // cleanup would retire the whole source.
            var source = Src(Youtube);
            UpToDate(source).FullListedAt = _now.AddDays(-30);
            AddVideo(Youtube, "one", fetched: true, described: true);

            await RunAsync(Config(source));

            Assert.Contains($"{SourceStage.FullListing}:{Youtube}", _trace);
            _db.Received(1).GetVideosGoneFromLastFullListing(Youtube, _now);
            Assert.Contains($"{SourceStage.Cleanup}:{Youtube}", _trace);
        }

        [Fact]
        public async Task TheWavesDescriptions_ReachTheCheckupOfTheSameSource()
        {
            // A description does not survive the pass that produced it, so the only way it reaches
            // the item is in memory, from the wave to the checkup of the SAME source. An item
            // created without one is marked for describing again — which is the observable here:
            // the two described videos must NOT be marked, and the third one, which no wave
            // described, must be. Without that third video the assertion would be tautological:
            // "nothing was marked" is also true of a pass that creates nothing.
            var alpha = Src(Alpha);
            var beta = Src(Beta);
            var described1 = AddVideo(Alpha, "a-one");
            var described2 = AddVideo(Beta, "b-one");
            var neverDue = AddVideo(Beta, "b-two", fetched: true, described: true);

            var report = await RunAsync(Config(alpha, beta));

            // Three items published in the first turn, and three fetches: the two videos that were
            // due, plus the third one in a LATER turn — being marked for describing again is what
            // makes it due, and the loop then picks it up. That is the marking working end to end.
            Assert.Equal(3, report.VideosPublished);
            Assert.Equal(3, report.VideosFetched);

            _db.DidNotReceive().ClearDescribedAt(described1.Id);
            _db.DidNotReceive().ClearDescribedAt(described2.Id);
            _db.Received(1).ClearDescribedAt(neverDue.Id);
        }

        [Fact]
        public async Task SourcesGoneFromTheConfiguration_AreRetiredOnceAPass_BeforeAnyPlatformCall()
        {
            // Retirement is global and runs first so the wave does not spend yt-dlp calls on a
            // source about to be discarded.
            var source = Src(Youtube);
            AnnounceNewVideo(Youtube, "one");

            var gone = Src(Arte);
            UpToDate(gone);
            AddVideo(Arte, "old", fetched: true, described: true);

            // Arte is in the database but NOT in the configuration any more.
            var report = await RunAsync(Config(source));

            Assert.Equal(1, report.SourcesRetired);
            _db.Received(1).DeleteSource(Arte);
            Assert.True(FirstIndexOf("retire:" + Arte) < FirstIndexOf("listing:" + Youtube),
                "A source that left the configuration must be retired before any platform call.");
        }

        [Fact]
        public async Task ASourceThatLeftTheConfiguration_IsRetiredEvenWhenNothingElseIsDue()
        {
            // The retirement runs BEFORE the stop test, not after it. Behind it, removing the last
            // source from the configuration would purge nothing at all — the pass would find no
            // work, end, and leave that source's items in the library until something else
            // happened to be due.
            var gone = Src(Arte);
            UpToDate(gone);
            AddVideo(Arte, "old", fetched: true, described: true);

            var report = await RunAsync(Config());

            Assert.Equal(1, report.Turns);
            Assert.Equal(1, report.SourcesRetired);
            _db.Received(1).DeleteSource(Arte);
        }

        [Fact]
        public async Task ADisabledSourceWithDueVideos_IsNotWorked()
        {
            // Disabled is paused: it keeps its items, but it must not spend a single call. Catches
            // a work list built from the due-video query alone, which does not know about Enabled.
            //
            // The enabled source has work of its own, and the two are alike in every other way: a
            // pass that worked neither of them would not satisfy this test either.
            var enabled = Src(Youtube);
            UpToDate(enabled);
            var worked = AddVideo(Youtube, "one");

            var disabled = Src(Arte, enabled: false);
            UpToDate(disabled);
            var paused = AddVideo(Arte, "two");

            var report = await RunAsync(Config(enabled, disabled));

            Assert.Equal(1, report.VideosFetched);
            Assert.Contains("metadata:" + worked.VideoUrl, _trace);
            Assert.DoesNotContain("metadata:" + paused.VideoUrl, _trace);

            // And nothing at all happened for the paused source — no step, no stage result.
            Assert.All(_trace, line => Assert.DoesNotContain(Arte, line, StringComparison.Ordinal));
        }

        // ── The guard-rails ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnEndlessLoop_StopsAtTheTurnCap_AndWarns()
        {
            // An error whose cause is neither a rate limit nor a timeout counts against the video
            // and would normally back it off; here the clock never advances it, so the work never
            // ends. The cap is the last line of defence, and reaching it must be reported rather
            // than passed over: it means the pass stopped with work left.
            var source = Src(Youtube);
            UpToDate(source);
            AddVideo(Youtube, "one");

            // Every failure claims not to count against the video, so no backoff ever starts.
            _metadataForEveryVideo = Unavailable();
            _db.When(d => d.SetMetadataOutcome(
                    Arg.Any<string>(), Arg.Any<CallOutcome>(), Arg.Any<string>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<bool>()))
                .Do(call =>
                {
                    var row = Row(call.ArgAt<string>(0));
                    if (row is not null)
                        row.MetadataAttempts = 0;
                });

            // No library: the checkup returns early, which keeps 2000 turns cheap without changing
            // anything about the loop under test.
            var config = Config(source);
            config.MoviesLibraryName = string.Empty;

            var report = await RunAsync(config);

            Assert.True(report.HitTurnCap, "A loop that cannot end must stop at the cap.");
            Assert.Equal(PublicationService.MaxTurnsPerPass, report.Turns);
            Assert.True(Logged(LogLevel.Warning, "turn cap"),
                "Reaching the cap must be logged as a warning.");
        }

        [Fact]
        public async Task AWaveSizeOfZero_StillMakesProgress_RatherThanSpinning()
        {
            // A wave of zero videos would leave the stop test saying "there is work" for ever while
            // nothing could ever be done about it — one full turn of the loop per nothing.
            var source = Src(Youtube);
            UpToDate(source);
            AddVideo(Youtube, "one");

            var config = Config(source);
            config.WaveSize = 0;

            var report = await RunAsync(config);

            Assert.False(report.HitTurnCap);
            Assert.Equal(1, report.VideosFetched);
        }

        [Fact]
        public async Task TheFirstTurnUsesTheGivenClock_AndEveryLaterTurnReadsItAgain()
        {
            // A turn can last minutes and a first fill hours. A pass that kept its start time would
            // keep selecting the same work against a clock that stopped; one that ignored the
            // parameter could not be tested at all.
            var first = Src(Alpha);
            var second = Src(Beta);

            var longAgo = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

            AnnounceNewVideo(Alpha, "a-one");

            // Beta is up to date as far as listings go, and its one video was last fetched at the
            // pass's start time: fresh on the first turn, thirty days stale once the clock is read
            // again.
            UpToDate(second).FullListedAt = DateTimeOffset.UtcNow;
            _records[Beta].ShortListedAt = DateTimeOffset.UtcNow;
            var betaVideo = AddVideo(Beta, "b-one", fetched: true, described: true, fetchedAt: longAgo);

            var report = await RunAsync(Config(first, second), now: longAgo);

            // The first turn used the parameter, to the tick.
            _db.Received(1).SetListingClock(Alpha, true, longAgo);

            // A later turn read the clock again, which is the only reason Beta's video became due.
            _db.Received().SetMetadataOutcome(
                betaVideo.Id, CallOutcome.Ok, null,
                Arg.Is<DateTimeOffset>(when => when > longAgo.AddYears(1)),
                Arg.Any<bool>());
            Assert.False(report.HitTurnCap);
        }

        // ── Doubles ──────────────────────────────────────────────────────────────────────────────

        private sealed class DelegateProgress(Action<double> onReport) : IProgress<double>
        {
            public void Report(double value) => onReport(value);
        }

        private sealed class StubHandler(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                });
        }

    }
}

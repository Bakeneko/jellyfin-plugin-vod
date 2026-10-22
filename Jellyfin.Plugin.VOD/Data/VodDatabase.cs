using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Data
{
    /// <summary>SQLite-backed implementation of <see cref="IVodDatabase"/>.</summary>
    public sealed partial class VodDatabase : IVodDatabase
    {
        private readonly string _dbPath;
        private readonly ILogger<VodDatabase> _logger;
        private readonly Lock _sync = new();
        private SqliteConnection? _connection;

        /// <summary>Initializes a new instance of the <see cref="VodDatabase"/> class.</summary>
        /// <param name="dbPath">Path to the SQLite file, or <c>:memory:</c> for in-process tests.</param>
        /// <param name="logger">Logger instance.</param>
        public VodDatabase(string dbPath, ILogger<VodDatabase> logger)
        {
            _dbPath = dbPath;
            _logger = logger;
        }

        private SqliteConnection Connection => _connection ??= Open();

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var pragmaWal = conn.CreateCommand();
            pragmaWal.CommandText = "PRAGMA journal_mode=WAL";
            pragmaWal.ExecuteNonQuery();
            using var pragmaFk = conn.CreateCommand();
            pragmaFk.CommandText = "PRAGMA foreign_keys=ON";
            pragmaFk.ExecuteNonQuery();
            return conn;
        }

        /// <summary>
        /// Schema version this build expects, tracked in SQLite's <c>user_version</c>.
        /// Bump it and add the matching case to <see cref="Migrate"/> whenever the schema changes.
        /// </summary>
        private const int SchemaVersion = 9;

        /// <inheritdoc />
        public void EnsureCreated()
        {
            lock (_sync)
            {
                var from = ReadSchemaVersion();

                // A version from the future means an older build is reading a database a newer one
                // wrote. Nothing here can migrate backwards, and the readers assume every column
                // they name exists, so the next MapVideo would throw on a column that version
                // renamed. Said out loud rather than guarded: downgrading is the administrator's
                // decision, and a silent start followed by an unexplained crash is worse than a
                // line that names the cause.
                if (from > SchemaVersion)
                    LogSchemaFromTheFuture(from, SchemaVersion);

                if (from < SchemaVersion)
                {
                    using var txn = Connection.BeginTransaction();
                    try
                    {
                        for (var v = from + 1; v <= SchemaVersion; v++)
                            Migrate(v, txn);

                        txn.Commit();
                    }
                    catch (SqliteException)
                    {
                        // Disposing the transaction rolls it back on its own; this is here to say
                        // so. A step logs what it did as it does it — how many rows it deleted,
                        // for instance — and those lines are only the truth about the statements
                        // that ran. Whether any of it stuck is this line, or the one below it.
                        LogMigrationRolledBack(from);
                        throw;
                    }

                    LogSchemaMigrated(from, SchemaVersion);
                }
            }

            LogSchemaReady(_dbPath);
        }

        /// <summary>
        /// Applies the single step that takes the schema from <paramref name="version"/> - 1 to
        /// <paramref name="version"/>. Steps replay in order on a fresh database, so a step must
        /// keep describing the change it originally made even after later steps supersede it.
        /// Each script stamps its own <c>user_version</c> as its last statement, so the recorded
        /// version cannot drift from the schema it describes.
        /// </summary>
        /// <param name="version">The version this step brings the schema to.</param>
        /// <param name="txn">The transaction every step of a migration shares.</param>
        private void Migrate(int version, SqliteTransaction txn)
        {
            using var cmd = Connection.CreateCommand();
            cmd.Transaction = txn;

            switch (version)
            {
                case <= 8:
                    // Nothing to do. Steps 1 to 8 built the schema the orchestration rework
                    // replaces, and step 9 drops both of its tables unconditionally — so on a fresh
                    // database they would create tables only for step 9 to destroy, and on an
                    // existing one step 9 destroys what they built. Keeping ~200 lines of SQL to
                    // reach a state that is thrown away in the same transaction would be dead
                    // weight that still has to be read and maintained.
                    //
                    // The counter is NOT restarted at 1 instead: 1 < 8 would make step 9 replay on
                    // every launch of an upgraded instance and destroy its data each time.
                    return;

                case 9:
                    // The orchestration rework. Breaking by design: the two tables are dropped and
                    // rebuilt, with no migration from version 8 — every column the old model needed
                    // is gone, and the data it held is re-derivable by one full listing per source.
                    //
                    // The counter stays monotonic even though this is a new lineage. Restarting it
                    // at 1 would make the next start ambiguous: 1 < 8 would replay this step and
                    // destroy the data on EVERY launch.
                    cmd.CommandText = """
                        DROP TABLE IF EXISTS videos;
                        DROP TABLE IF EXISTS sources;

                        CREATE TABLE sources (
                            url                    TEXT PRIMARY KEY,
                            source_key             TEXT,
                            name                   TEXT,
                            series_synopsis        TEXT,
                            series_thumbnail_url   TEXT,
                            series_banner_url      TEXT,
                            tags                   TEXT,
                            published_image_url    TEXT,
                            published_banner_url   TEXT,
                            published_anchor_id    TEXT,
                            config_fingerprint     TEXT,
                            video_count            INTEGER NOT NULL DEFAULT 0,
                            short_listed_at        TEXT,
                            full_listed_at         TEXT,
                            listing_count          INTEGER,
                            short_listing_result   TEXT,
                            short_listing_detail   TEXT,
                            short_listing_failures INTEGER NOT NULL DEFAULT 0,
                            full_listing_result    TEXT,
                            full_listing_detail    TEXT,
                            full_listing_failures  INTEGER NOT NULL DEFAULT 0,
                            cleanup_result         TEXT,
                            cleanup_detail         TEXT,
                            cleanup_failures       INTEGER NOT NULL DEFAULT 0,
                            wave_result            TEXT,
                            wave_detail            TEXT,
                            wave_failures          INTEGER NOT NULL DEFAULT 0,
                            checkup_result         TEXT,
                            checkup_detail         TEXT,
                            checkup_failures       INTEGER NOT NULL DEFAULT 0
                        );

                        CREATE TABLE videos (
                            id                  TEXT PRIMARY KEY,
                            source_url          TEXT NOT NULL REFERENCES sources(url),
                            native_id           TEXT,
                            extractor_key       TEXT,
                            video_url           TEXT NOT NULL,
                            title               TEXT,
                            listing_title       TEXT,
                            description         TEXT,
                            duration_seconds    INTEGER,
                            thumbnail_url       TEXT,
                            published_image_url TEXT,
                            published_at        TEXT,
                            extras              TEXT,
                            live_status         TEXT NOT NULL DEFAULT 'NotLive',
                            season_number       INTEGER,
                            episode_number      INTEGER,
                            listing_position    INTEGER,
                            first_seen_at       TEXT,
                            listed_at           TEXT,
                            metadata_at         TEXT,
                            metadata_result     TEXT,
                            metadata_detail     TEXT,
                            metadata_attempts   INTEGER NOT NULL DEFAULT 0,
                            described_at        TEXT,
                            describe_result     TEXT,
                            describe_detail     TEXT,
                            describe_attempts   INTEGER NOT NULL DEFAULT 0
                        );

                        CREATE UNIQUE INDEX idx_videos_source_native ON videos(source_url, native_id);
                        CREATE INDEX idx_videos_source ON videos(source_url);

                        PRAGMA user_version = 9;
                        """;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"No migration defined for schema version {version}");
            }

            cmd.ExecuteNonQuery();
        }

        private int ReadSchemaVersion()
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return (int)(long)cmd.ExecuteScalar()!;
        }

        /// <inheritdoc />
        public void UpsertSource(SourceRecord source)
        {
            ArgumentNullException.ThrowIfNull(source);

            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();

                // Only the fields a listing owns. Every clock, every stage triplet, the fingerprint
                // and the anchor have a setter of their own, because a caller holding a record
                // loaded earlier in the pass would otherwise write a stale value back over one
                // written since — which is how the old model lost clears mid-pass.
                cmd.CommandText = """
                    INSERT INTO sources
                        (url, source_key, name, series_synopsis, series_thumbnail_url,
                         series_banner_url, tags, video_count)
                    VALUES
                        ($url, $key, $name, $synopsis, $thumbnailUrl,
                         $bannerUrl, $tags, $videoCount)
                    ON CONFLICT(url) DO UPDATE SET
                        source_key           = excluded.source_key,
                        name                 = excluded.name,
                        series_synopsis      = excluded.series_synopsis,
                        series_thumbnail_url = excluded.series_thumbnail_url,
                        series_banner_url    = excluded.series_banner_url,
                        tags                 = excluded.tags,
                        video_count          = excluded.video_count;
                    """;
                cmd.Parameters.AddWithValue("$url", source.Url);
                cmd.Parameters.AddWithValue("$key", (object?)source.Key ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$name", (object?)source.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$synopsis", (object?)source.SeriesSynopsis ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)source.SeriesThumbnailUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bannerUrl", (object?)source.SeriesBannerUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tags", (object?)source.Tags ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$videoCount", source.VideoCount);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public SourceRecord? GetSource(string url)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM sources WHERE url = $url";
                cmd.Parameters.AddWithValue("$url", url);
                using var r = cmd.ExecuteReader();
                return r.Read() ? MapSource(r) : null;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<SourceRecord> GetAllSources()
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM sources ORDER BY url";
                using var r = cmd.ExecuteReader();
                var list = new List<SourceRecord>();
                while (r.Read())
                {
                    list.Add(MapSource(r));
                }

                return list;
            }
        }

        /// <inheritdoc />
        public void UpsertVideo(VideoRecord video)
        {
            ArgumentNullException.ThrowIfNull(video);

            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO videos
                        (id, source_url, native_id, extractor_key, video_url,
                         title, listing_title, description, duration_seconds,
                         thumbnail_url, published_at, extras, live_status,
                         season_number, episode_number, listing_position,
                         first_seen_at, listed_at,
                         metadata_at, metadata_result, metadata_detail, metadata_attempts,
                         described_at, describe_result, describe_detail, describe_attempts)
                    VALUES
                        ($id, $sourceUrl, $nativeId, $extractorKey, $videoUrl,
                         $title, $listingTitle, $description, $durationSeconds,
                         $thumbnailUrl, $publishedAt, $extras, $liveStatus,
                         $seasonNumber, $episodeNumber, $listingPosition,
                         $firstSeenAt, $listedAt,
                         $metadataAt, $metadataResult, $metadataDetail, $metadataAttempts,
                         $describedAt, $describeResult, $describeDetail, $describeAttempts)
                    ON CONFLICT(id) DO UPDATE SET
                        source_url        = excluded.source_url,
                        native_id         = excluded.native_id,
                        extractor_key     = excluded.extractor_key,
                        video_url         = excluded.video_url,
                        title             = excluded.title,
                        listing_title     = excluded.listing_title,
                        description       = excluded.description,
                        duration_seconds  = excluded.duration_seconds,
                        thumbnail_url     = excluded.thumbnail_url,
                        published_at      = excluded.published_at,
                        extras            = excluded.extras,
                        live_status       = excluded.live_status,
                        season_number     = excluded.season_number,
                        episode_number    = excluded.episode_number,
                        listing_position  = excluded.listing_position,
                        listed_at         = excluded.listed_at,
                        metadata_at       = excluded.metadata_at,
                        metadata_result   = excluded.metadata_result,
                        metadata_detail   = excluded.metadata_detail,
                        metadata_attempts = excluded.metadata_attempts,
                        described_at      = excluded.described_at,
                        describe_result   = excluded.describe_result,
                        describe_detail   = excluded.describe_detail,
                        describe_attempts = excluded.describe_attempts,
                        -- Never overwritten: the discovery date already on the row always beats the
                        -- one a later upsert proposes. This COALESCE is what enforces
                        -- "set once, never updated", and it is the published date added.
                        first_seen_at     = COALESCE(videos.first_seen_at, excluded.first_seen_at);
                    """;
                BindVideo(cmd, video);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public VideoRecord? GetVideoByUuid(string uuid)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM videos WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", uuid);
                using var r = cmd.ExecuteReader();
                return r.Read() ? MapVideo(r) : null;
            }
        }

        /// <inheritdoc />
        public void SetListingPositions(string sourceUrl, IReadOnlyDictionary<string, int> positions)
        {
            ArgumentNullException.ThrowIfNull(positions);

            if (positions.Count == 0)
                return;

            lock (_sync)
            {
                using var txn = Connection.BeginTransaction();
                using var cmd = Connection.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText =
                    "UPDATE videos SET listing_position = $position "
                    + "WHERE source_url = $url AND native_id = $nativeId";
                var url = cmd.Parameters.Add("$url", SqliteType.Text);
                var nativeId = cmd.Parameters.Add("$nativeId", SqliteType.Text);
                var position = cmd.Parameters.Add("$position", SqliteType.Integer);
                url.Value = sourceUrl;

                foreach (var (id, rank) in positions)
                {
                    nativeId.Value = id;
                    position.Value = rank;
                    cmd.ExecuteNonQuery();
                }

                txn.Commit();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetVideoIdsWithMetadata()
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText =
                    "SELECT id FROM videos WHERE metadata_at IS NOT NULL AND metadata_result = $ok";
                cmd.Parameters.AddWithValue("$ok", nameof(CallOutcome.Ok));
                using var r = cmd.ExecuteReader();
                var list = new List<string>();
                while (r.Read())
                    list.Add(r.GetString(0));
                return list;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<VideoRecord> GetVideosBySource(string sourceUrl)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText =
                    "SELECT * FROM videos WHERE source_url = $url ORDER BY published_at, id";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                using var r = cmd.ExecuteReader();
                var list = new List<VideoRecord>();
                while (r.Read())
                {
                    list.Add(MapVideo(r));
                }

                return list;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<VideoRecord> GetDueVideos(
            string sourceUrl,
            DateTimeOffset now,
            int retryMinutes,
            int metadataMaxAgeDays,
            int retentionDays,
            int limit)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();

                // Metadata is stale when metadata_at is older than this cutoff.
                var metadataCutoff = now.AddDays(-metadataMaxAgeDays).ToString("O");

                // Condition 3 retention gate: any published_at before this cutoff is outside
                // retention. Zero or less means unlimited — use MinValue so every date passes,
                // matching the "zero means unlimited" convention throughout this project.
                var retentionCutoff = retentionDays > 0
                    ? now.AddDays(-retentionDays).ToString("O")
                    : DateTimeOffset.MinValue.ToString("O");

                // A non-positive retry base means no backoff: clamp to zero so that the SQL
                // expression retryMinutes * POWER(2, n) = 0 for all n, and the backoff deadline
                // is always in the past.
                var retryBase = Math.Max(0, retryMinutes);

                // ONE query serves both the wave (limit = WaveSize) and the loop's stop test
                // (limit = 1). A second "has work" predicate that could diverge from this
                // selection would risk reporting work for a video this query never surfaces —
                // making the pass loop forever. See WaveSelector.IsInBackoff for the C# mirror of
                // the backoff rule; WaveSelectorTests.IsInBackoff_SqlConcordance_AgreesWithDatabase
                // checks that the two cannot drift.
                //
                // Only the metadata pair (metadata_attempts / metadata_at) drives the backoff.
                // describe_attempts cannot drive a backoff at all, for an architectural reason:
                // describing a video requires the stream-manifest URLs that the metadata step
                // fetches via yt-dlp; those URLs are not stored on the row. A describe retry is
                // therefore always a metadata retry — there is no standalone describe retry path.
                // Concretely, when a description fails, SetDescribeOutcome stamps described_at,
                // which removes the video from condition 3 entirely. The video waits until the
                // metadata clock goes stale (bounded by metadataMaxAgeDays), at which point the
                // next wave re-fetches the metadata and re-describes the video in the same breath.
                // Playback's own failsafe describes it sooner if someone plays it in the meantime.
                cmd.CommandText = """
                    SELECT * FROM videos
                    WHERE source_url = $url
                      AND (
                          -- Condition 1: metadata has never been fetched.
                          metadata_at IS NULL
                          -- Condition 2: metadata is stale.
                          OR metadata_at < $metadataCutoff
                          -- Condition 3: description is absent on a video that can and should
                          -- have one. Gated on live_status and retention HERE — selecting a
                          -- live video or one outside retention only to skip it in the
                          -- execution step would fire a yt-dlp call on every wave, which is the
                          -- exact call loop this rework was built to remove.
                          OR (
                              described_at IS NULL
                              AND live_status = $notLive
                              AND (published_at IS NULL OR published_at >= $retentionCutoff)
                          )
                      )
                      AND (
                          -- Not in backoff: no prior failures to back off from, no clock to start
                          -- from, or the computed wait has already elapsed.
                          metadata_attempts = 0
                          OR metadata_at IS NULL
                          OR unixepoch(metadata_at)
                             + CAST(MIN(
                                   CAST($retryMinutes AS REAL) * POWER(2.0, metadata_attempts - 1),
                                   7.0 * 24.0 * 60.0
                               ) AS INTEGER) * 60
                             <= unixepoch($now)
                      )
                    ORDER BY
                        listing_position IS NULL,  -- 0 for a known position, 1 for null: nulls last
                        listing_position,
                        id
                    LIMIT $limit
                    """;

                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$now", now.ToString("O"));
                cmd.Parameters.AddWithValue("$metadataCutoff", metadataCutoff);
                cmd.Parameters.AddWithValue("$retentionCutoff", retentionCutoff);
                cmd.Parameters.AddWithValue("$notLive", nameof(LiveStatus.NotLive));
                cmd.Parameters.AddWithValue("$retryMinutes", retryBase);
                cmd.Parameters.AddWithValue("$limit", limit);

                using var r = cmd.ExecuteReader();
                var list = new List<VideoRecord>();
                while (r.Read())
                    list.Add(MapVideo(r));
                return list;
            }
        }


        /// <inheritdoc />
        public void SetPublishedImageUrl(string videoId, string? url)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "UPDATE videos SET published_image_url = $url WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", videoId);
                cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetSourcePublishedBannerUrl(string sourceUrl, string? url)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "UPDATE sources SET published_banner_url = $url WHERE url = $sourceUrl";
                cmd.Parameters.AddWithValue("$sourceUrl", sourceUrl);
                cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetSourcePublishedImageUrl(string sourceUrl, string? url)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "UPDATE sources SET published_image_url = $url WHERE url = $sourceUrl";
                cmd.Parameters.AddWithValue("$sourceUrl", sourceUrl);
                cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void DeleteVideo(string uuid)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM videos WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", uuid);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void DeleteSource(string url)
        {
            lock (_sync)
            {
                using var txn = Connection.BeginTransaction();
                using var cmd = Connection.CreateCommand();
                cmd.Transaction = txn;

                // videos.source_url is a foreign key onto sources.url, so the children go first.
                cmd.CommandText =
                    "DELETE FROM videos  WHERE source_url = $url;"
                    + "DELETE FROM sources WHERE url        = $url;";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.ExecuteNonQuery();
                txn.Commit();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetAllVideoIds()
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT id FROM videos";
                using var r = cmd.ExecuteReader();
                var list = new List<string>();
                while (r.Read())
                    list.Add(r.GetString(0));
                return list;
            }
        }

        /// <inheritdoc />
        public void TouchListedAt(
            string sourceUrl, IReadOnlyCollection<string> nativeIds, DateTimeOffset when)
        {
            ArgumentNullException.ThrowIfNull(nativeIds);

            if (nativeIds.Count == 0)
                return;

            lock (_sync)
            {
                using var txn = Connection.BeginTransaction();
                using var cmd = Connection.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText =
                    "UPDATE videos SET listed_at = $when WHERE source_url = $url AND native_id = $nativeId";
                var url = cmd.Parameters.Add("$url", SqliteType.Text);
                var nativeId = cmd.Parameters.Add("$nativeId", SqliteType.Text);
                var stamp = cmd.Parameters.Add("$when", SqliteType.Text);
                url.Value = sourceUrl;
                stamp.Value = when.ToString("O");

                foreach (var id in nativeIds)
                {
                    nativeId.Value = id;
                    cmd.ExecuteNonQuery();
                }

                txn.Commit();
            }
        }

        /// <summary>
        /// How long a broadcasting or scheduled row is spared the cleanup after it stops being
        /// written.
        /// </summary>
        /// <remarks>
        /// Not a broadcast's length — every probe pass rewrites a live row's <c>listed_at</c>, so a
        /// week-long stream stays fresh throughout — but the longest plausible gap between two
        /// successful writes of a row that is still live. A week clears the longest broadcasts
        /// anyone runs, any sane probe interval, and a long run of yt-dlp failures on top, while
        /// bounding a stuck row's life to days rather than for ever. Sparing a live row wrongly
        /// removes something someone is watching; sweeping a stale one wrongly costs one
        /// re-publication, so the bound is deliberately generous.
        /// </remarks>
        private static readonly TimeSpan LiveExemptionMaxAge = TimeSpan.FromDays(7);

        /// <inheritdoc />
        public IReadOnlyList<string> GetVideosGoneFromLastFullListing(
            string sourceUrl, DateTimeOffset fullListedAt)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();

                // A row that is broadcasting or scheduled is spared, but only while it is FRESH.
                //
                // Spared, because the probe publishes ids no listing carries — the stream id on a
                // subscriber-only channel, a different id per pass for a live-only source — so a
                // sweep on listed_at alone deleted the row, and with it the library item, in the
                // middle of the broadcast.
                //
                // Only while fresh, because one such row can never return to NotLive: the fallback
                // row's URL is the channel root, which yt-dlp answers for only while the channel is
                // live, so once the broadcast ends its re-fetch fails for ever; and its stream id is
                // absent from the /videos listing, so no listing touches it either. An unbounded
                // exemption left one phantom per broadcast, accumulating.
                //
                // Retention is deliberately NOT checked here. It is a publication filter, not an
                // erase rule: deleting a row that is still in the listing would have it
                // rediscovered and refetched on the next pass, which is the exact call loop this
                // design exists to remove.
                cmd.CommandText =
                    "SELECT id FROM videos WHERE source_url = $url "
                    + "AND (live_status = $notLive OR listed_at IS NULL OR listed_at < $stale) "
                    + "AND (listed_at IS NULL OR listed_at < $cutoff)";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$notLive", nameof(LiveStatus.NotLive));
                cmd.Parameters.AddWithValue("$cutoff", fullListedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$stale", fullListedAt.Subtract(LiveExemptionMaxAge).ToString("O"));
                using var r = cmd.ExecuteReader();
                var list = new List<string>();
                while (r.Read())
                    list.Add(r.GetString(0));
                return list;
            }
        }

        /// <inheritdoc />
        public void EnsureSource(string sourceUrl)
        {
            lock (_sync)
            {
                EnsureSourceRow(sourceUrl);
            }
        }

        /// <summary>Creates the source's row if absent. The caller holds the lock.</summary>
        private void EnsureSourceRow(string sourceUrl)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO sources (url) VALUES ($url) ON CONFLICT(url) DO NOTHING";
            cmd.Parameters.AddWithValue("$url", sourceUrl);
            cmd.ExecuteNonQuery();
        }

        /// <inheritdoc />
        public void SetStageResult(
            string sourceUrl, SourceStage stage, CallOutcome outcome, string? detail)
        {
            lock (_sync)
            {
                // The row is created by a listing that SUCCEEDS, and the first thing a source with
                // a mistyped URL ever produces is a failure. Without this the UPDATE below matches
                // nothing, the failure is dropped, and the configuration page stays blank about
                // the one source the administrator needs explained — the silence this rework
                // exists to end.
                EnsureSourceRow(sourceUrl);

                using var cmd = Connection.CreateCommand();

                // Five literal statements rather than one with the column prefix interpolated. The
                // prefix would come from a switch over an enum, so interpolating it is safe by
                // construction — but CA2100 cannot see that, and a suppression is a claim a reader
                // has to trust where this is a claim the compiler checks.
                //
                // The failure count is CONSECUTIVE: a success zeroes it, anything else advances it.
                // That is what makes an intermittent failure visible as a number, with no history
                // table — the /streams tab that failed 170 times in two days and then stopped is
                // the case this exists for.
                switch (stage)
                {
                    case SourceStage.ShortListing:
                        cmd.CommandText =
                            "UPDATE sources SET short_listing_result = $result, short_listing_detail = $detail, "
                            + "short_listing_failures = CASE WHEN $result = $ok THEN 0 ELSE short_listing_failures + 1 END "
                            + "WHERE url = $url";
                        break;
                    case SourceStage.FullListing:
                        cmd.CommandText =
                            "UPDATE sources SET full_listing_result = $result, full_listing_detail = $detail, "
                            + "full_listing_failures = CASE WHEN $result = $ok THEN 0 ELSE full_listing_failures + 1 END "
                            + "WHERE url = $url";
                        break;
                    case SourceStage.Cleanup:
                        cmd.CommandText =
                            "UPDATE sources SET cleanup_result = $result, cleanup_detail = $detail, "
                            + "cleanup_failures = CASE WHEN $result = $ok THEN 0 ELSE cleanup_failures + 1 END "
                            + "WHERE url = $url";
                        break;
                    case SourceStage.Wave:
                        cmd.CommandText =
                            "UPDATE sources SET wave_result = $result, wave_detail = $detail, "
                            + "wave_failures = CASE WHEN $result = $ok THEN 0 ELSE wave_failures + 1 END "
                            + "WHERE url = $url";
                        break;
                    case SourceStage.Checkup:
                        cmd.CommandText =
                            "UPDATE sources SET checkup_result = $result, checkup_detail = $detail, "
                            + "checkup_failures = CASE WHEN $result = $ok THEN 0 ELSE checkup_failures + 1 END "
                            + "WHERE url = $url";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(stage));
                }

                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$result", outcome.ToString());
                cmd.Parameters.AddWithValue("$ok", nameof(CallOutcome.Ok));
                cmd.Parameters.AddWithValue("$detail", (object?)Truncate(detail) ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetListingClock(string sourceUrl, bool full, DateTimeOffset when)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();

                // A full listing also stamps the short clock: it has just seen everything a short
                // listing would have, so leaving the short clock behind would make the source due
                // for a redundant listing on the very next pass.
                if (full)
                {
                    cmd.CommandText =
                        "UPDATE sources SET full_listed_at = $when, short_listed_at = $when WHERE url = $url";
                }
                else
                {
                    cmd.CommandText = "UPDATE sources SET short_listed_at = $when WHERE url = $url";
                }
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$when", when.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void ClearFullListedAt(string sourceUrl)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "UPDATE sources SET full_listed_at = NULL WHERE url = $url";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetListingCount(string sourceUrl, int? count)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "UPDATE sources SET listing_count = $count WHERE url = $url";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$count", (object?)count ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetConfigFingerprint(string sourceUrl, string? fingerprint)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "UPDATE sources SET config_fingerprint = $fingerprint WHERE url = $url";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$fingerprint", (object?)fingerprint ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetPublishedAnchorId(string sourceUrl, Guid? anchorId)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "UPDATE sources SET published_anchor_id = $anchorId WHERE url = $url";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$anchorId", (object?)anchorId?.ToString("N") ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetMetadataOutcome(
            string videoId, CallOutcome outcome, string? detail, DateTimeOffset when, bool countFailure)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();

                // The clock moves whatever the outcome: a failed fetch has still been attempted, and
                // leaving the clock behind would have the wave pick the same video again at once.
                // The COUNT is what the caller controls, because a rate-limit window is the
                // platform's failure, not the video's — counting it would push every video of a
                // source a step further out for something none of them did.
                cmd.CommandText =
                    "UPDATE videos SET metadata_at = $when, metadata_result = $result, "
                    + "metadata_detail = $detail, metadata_attempts = CASE "
                    + "  WHEN $result = $ok THEN 0 "
                    + "  WHEN $count = 0 THEN metadata_attempts "
                    + "  ELSE metadata_attempts + 1 END "
                    + "WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", videoId);
                cmd.Parameters.AddWithValue("$when", when.ToString("O"));
                cmd.Parameters.AddWithValue("$result", outcome.ToString());
                cmd.Parameters.AddWithValue("$ok", nameof(CallOutcome.Ok));
                cmd.Parameters.AddWithValue("$count", countFailure ? 1 : 0);
                cmd.Parameters.AddWithValue("$detail", (object?)Truncate(detail) ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void SetDescribeOutcome(
            string videoId, CallOutcome outcome, string? detail, DateTimeOffset when)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText =
                    "UPDATE videos SET described_at = $when, describe_result = $result, "
                    + "describe_detail = $detail, describe_attempts = CASE "
                    + "  WHEN $result IN ($ok, $none) THEN 0 ELSE describe_attempts + 1 END "
                    + "WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", videoId);
                cmd.Parameters.AddWithValue("$when", when.ToString("O"));
                cmd.Parameters.AddWithValue("$result", outcome.ToString());
                cmd.Parameters.AddWithValue("$ok", nameof(CallOutcome.Ok));

                // None means "we looked, and there is nothing to describe" — no manifest, or no
                // quality tier. Nothing failed, so nothing is counted; the clock alone is what
                // takes the video out of the selection, and its detail says why.
                cmd.Parameters.AddWithValue("$none", nameof(CallOutcome.None));
                cmd.Parameters.AddWithValue("$detail", (object?)Truncate(detail) ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void ClearDescribedAt(string videoId)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText =
                    "UPDATE videos SET described_at = NULL, describe_result = $none, "
                    + "describe_detail = NULL, describe_attempts = 0 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", videoId);
                cmd.Parameters.AddWithValue("$none", nameof(CallOutcome.None));
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void MarkMetadataStale(string videoId)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();

                // UnixEpoch is older than any freshness window, so the wave will refetch this
                // video. It is NOT null: Publishability requires MetadataAt non-null, and nulling
                // it would un-publish the video at the same turn's checkup — destroying its
                // Jellyfin item and its watch state — for the sake of a title change.
                cmd.CommandText =
                    "UPDATE videos SET metadata_at = $epoch WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", videoId);
                cmd.Parameters.AddWithValue("$epoch", DateTimeOffset.UnixEpoch.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<CallOutcome, int> GetOutcomeCounts()
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText =
                    "SELECT metadata_result, COUNT(*) FROM videos "
                    + "WHERE metadata_result IS NOT NULL GROUP BY metadata_result";
                using var r = cmd.ExecuteReader();
                var counts = new Dictionary<CallOutcome, int>();
                while (r.Read())
                {
                    // An unreadable value is skipped rather than folded into a neighbour: a count
                    // that silently absorbs an unknown result is worse than a count that is short,
                    // because the page would report a cause it cannot have observed.
                    if (Enum.TryParse<CallOutcome>(r.GetString(0), out var outcome) && Enum.IsDefined(outcome))
                        counts[outcome] = counts.GetValueOrDefault(outcome) + r.GetInt32(1);
                }

                return counts;
            }
        }

        /// <inheritdoc />
        public void ResetVideo(string videoId)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();

                // Clocks AND counters, so the video is eligible on the very next wave instead of
                // serving out a backoff that was earned under a cause the administrator has since
                // fixed — re-exported cookies, typically. Nothing here touches YtDlpThrottle: a
                // platform's own backoff is not ours to lift, and lifting it would restart the
                // rate limiting.
                cmd.CommandText = """
                    UPDATE videos SET
                        metadata_at       = NULL,
                        metadata_result   = $none,
                        metadata_detail   = NULL,
                        metadata_attempts = 0,
                        described_at      = NULL,
                        describe_result   = $none,
                        describe_detail   = NULL,
                        describe_attempts = 0
                    WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", videoId);
                cmd.Parameters.AddWithValue("$none", nameof(CallOutcome.None));
                cmd.ExecuteNonQuery();
            }
        }

        /// <inheritdoc />
        public void ResetSource(string sourceUrl)
        {
            lock (_sync)
            {
                using var txn = Connection.BeginTransaction();
                using var cmd = Connection.CreateCommand();
                cmd.Transaction = txn;

                // The stage counters go too. Without them the manual way out the design promises
                // would only work by half: the videos would be eligible again while the source
                // still carried the failure count that made it look broken.
                cmd.CommandText = """
                    UPDATE sources SET
                        short_listed_at        = NULL,
                        full_listed_at         = NULL,
                        short_listing_result   = $none,
                        short_listing_detail   = NULL,
                        short_listing_failures = 0,
                        full_listing_result    = $none,
                        full_listing_detail    = NULL,
                        full_listing_failures  = 0,
                        cleanup_result         = $none,
                        cleanup_detail         = NULL,
                        cleanup_failures       = 0,
                        wave_result            = $none,
                        wave_detail            = NULL,
                        wave_failures          = 0,
                        checkup_result         = $none,
                        checkup_detail         = NULL,
                        checkup_failures       = 0
                    WHERE url = $url;

                    UPDATE videos SET
                        metadata_at       = NULL,
                        metadata_result   = $none,
                        metadata_detail   = NULL,
                        metadata_attempts = 0,
                        described_at      = NULL,
                        describe_result   = $none,
                        describe_detail   = NULL,
                        describe_attempts = 0
                    WHERE source_url = $url;
                    """;
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$none", nameof(CallOutcome.None));
                cmd.ExecuteNonQuery();
                txn.Commit();
            }
        }

        /// <summary>The longest detail a row keeps.</summary>
        /// <remarks>
        /// Long enough for the first line of any stderr yt-dlp produces, and short enough that a
        /// pathological answer cannot bloat every row of a source.
        /// </remarks>
        private const int MaxDetailLength = 500;

        private static string? Truncate(string? detail)
            => detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;

        /// <inheritdoc />
        public void Dispose() => _connection?.Dispose();

        private static void BindVideo(SqliteCommand cmd, VideoRecord v)
        {
            object? N(object? value) => value ?? DBNull.Value;

            cmd.Parameters.AddWithValue("$id", v.Id);
            cmd.Parameters.AddWithValue("$sourceUrl", v.SourceUrl);
            cmd.Parameters.AddWithValue("$nativeId", N(v.NativeId));
            cmd.Parameters.AddWithValue("$extractorKey", N(v.ExtractorKey));
            cmd.Parameters.AddWithValue("$videoUrl", v.VideoUrl);
            cmd.Parameters.AddWithValue("$title", N(v.Title));
            cmd.Parameters.AddWithValue("$listingTitle", N(v.ListingTitle));
            cmd.Parameters.AddWithValue("$description", N(v.Description));
            cmd.Parameters.AddWithValue("$durationSeconds", N(v.DurationSeconds));
            cmd.Parameters.AddWithValue("$thumbnailUrl", N(v.ThumbnailUrl));
            cmd.Parameters.AddWithValue("$publishedAt", N(v.PublishedAt?.ToString("O")));
            cmd.Parameters.AddWithValue("$extras", SerializeExtras(v.Extras));
            cmd.Parameters.AddWithValue("$liveStatus", v.LiveStatus.ToString());
            cmd.Parameters.AddWithValue("$seasonNumber", N(v.SeasonNumber));
            cmd.Parameters.AddWithValue("$episodeNumber", N(v.EpisodeNumber));
            cmd.Parameters.AddWithValue("$listingPosition", N(v.ListingPosition));
            cmd.Parameters.AddWithValue("$firstSeenAt", N(v.FirstSeenAt?.ToString("O")));
            cmd.Parameters.AddWithValue("$listedAt", N(v.ListedAt?.ToString("O")));
            cmd.Parameters.AddWithValue("$metadataAt", N(v.MetadataAt?.ToString("O")));
            cmd.Parameters.AddWithValue("$metadataResult", v.MetadataResult.ToString());
            cmd.Parameters.AddWithValue("$metadataDetail", N(Truncate(v.MetadataDetail)));
            cmd.Parameters.AddWithValue("$metadataAttempts", v.MetadataAttempts);
            cmd.Parameters.AddWithValue("$describedAt", N(v.DescribedAt?.ToString("O")));
            cmd.Parameters.AddWithValue("$describeResult", v.DescribeResult.ToString());
            cmd.Parameters.AddWithValue("$describeDetail", N(Truncate(v.DescribeDetail)));
            cmd.Parameters.AddWithValue("$describeAttempts", v.DescribeAttempts);
        }

        private static SourceRecord MapSource(SqliteDataReader r)
        {
            string? S(string col)
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetString(ord);
            }

            int? I(string col)
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetInt32(ord);
            }

            DateTimeOffset? D(string col)
            {
                var s = S(col);
                return s is null
                    ? null
                    : DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }

            return new SourceRecord
            {
                Url = r.GetString(r.GetOrdinal("url")),
                Key = S("source_key"),
                Name = S("name"),
                SeriesSynopsis = S("series_synopsis"),
                SeriesThumbnailUrl = S("series_thumbnail_url"),
                SeriesBannerUrl = S("series_banner_url"),
                Tags = S("tags"),
                PublishedImageUrl = S("published_image_url"),
                PublishedBannerUrl = S("published_banner_url"),
                PublishedAnchorId = S("published_anchor_id") is { } anchor && Guid.TryParse(anchor, out var id)
                    ? id
                    : null,
                ConfigFingerprint = S("config_fingerprint"),
                VideoCount = r.GetInt32(r.GetOrdinal("video_count")),
                ShortListedAt = D("short_listed_at"),
                FullListedAt = D("full_listed_at"),
                ListingCount = I("listing_count"),
                ShortListingResult = ReadOutcome(S("short_listing_result")),
                ShortListingDetail = S("short_listing_detail"),
                ShortListingFailures = r.GetInt32(r.GetOrdinal("short_listing_failures")),
                FullListingResult = ReadOutcome(S("full_listing_result")),
                FullListingDetail = S("full_listing_detail"),
                FullListingFailures = r.GetInt32(r.GetOrdinal("full_listing_failures")),
                CleanupResult = ReadOutcome(S("cleanup_result")),
                CleanupDetail = S("cleanup_detail"),
                CleanupFailures = r.GetInt32(r.GetOrdinal("cleanup_failures")),
                WaveResult = ReadOutcome(S("wave_result")),
                WaveDetail = S("wave_detail"),
                WaveFailures = r.GetInt32(r.GetOrdinal("wave_failures")),
                CheckupResult = ReadOutcome(S("checkup_result")),
                CheckupDetail = S("checkup_detail"),
                CheckupFailures = r.GetInt32(r.GetOrdinal("checkup_failures")),
            };
        }

        /// <summary>
        /// Reads a stored outcome, answering <see cref="CallOutcome.None"/> for a value this build
        /// does not know.
        /// </summary>
        /// <param name="raw">The stored text, or null.</param>
        /// <returns>The outcome.</returns>
        /// <remarks>
        /// <c>None</c> rather than a guess: an unknown value means a newer build wrote it, and
        /// reading it as "no answer yet" costs one more call, where reading it as any particular
        /// failure would report a cause nothing observed.
        /// </remarks>
        private static CallOutcome ReadOutcome(string? raw)
            => raw is not null && Enum.TryParse<CallOutcome>(raw, out var outcome) && Enum.IsDefined(outcome)
                ? outcome
                : CallOutcome.None;

        private static VideoRecord MapVideo(SqliteDataReader r)
        {
            string? S(string col)
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetString(ord);
            }

            int? I(string col)
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetInt32(ord);
            }

            DateTimeOffset? D(string col)
            {
                var s = S(col);
                return s is null
                    ? null
                    : DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }

            return new VideoRecord
            {
                Id = r.GetString(r.GetOrdinal("id")),
                SourceUrl = r.GetString(r.GetOrdinal("source_url")),
                NativeId = S("native_id"),
                ExtractorKey = S("extractor_key"),
                VideoUrl = r.GetString(r.GetOrdinal("video_url")),
                Title = S("title"),
                ListingTitle = S("listing_title"),
                Description = S("description"),
                DurationSeconds = I("duration_seconds"),
                ThumbnailUrl = S("thumbnail_url"),
                PublishedImageUrl = S("published_image_url"),
                PublishedAt = D("published_at"),
                Extras = ReadExtras(S("extras")),
                LiveStatus = Enum.TryParse<LiveStatus>(S("live_status"), out var ls) && Enum.IsDefined(ls)
                    ? ls
                    : LiveStatus.NotLive,
                SeasonNumber = I("season_number"),
                EpisodeNumber = I("episode_number"),
                ListingPosition = I("listing_position"),
                FirstSeenAt = D("first_seen_at"),
                ListedAt = D("listed_at"),
                MetadataAt = D("metadata_at"),
                MetadataResult = ReadOutcome(S("metadata_result")),
                MetadataDetail = S("metadata_detail"),
                MetadataAttempts = r.GetInt32(r.GetOrdinal("metadata_attempts")),
                DescribedAt = D("described_at"),
                DescribeResult = ReadOutcome(S("describe_result")),
                DescribeDetail = S("describe_detail"),
                DescribeAttempts = r.GetInt32(r.GetOrdinal("describe_attempts")),
            };
        }

        /// <summary>
        /// Reads the extras payload, answering <see cref="VideoExtras.NoPayload"/> when the row has
        /// never been given one. A hand-edited or truncated value must not take the whole row down —
        /// the video is still playable without it — and is reported the same way, since what the
        /// column held cannot be known.
        /// </summary>
        /// <remarks>
        /// The distinction the caller needs is "this row has never been given a payload" versus "its
        /// payload is empty", which is a real answer from the platform. The column being NULL is the
        /// only thing that carries it, so it is surfaced here rather than inferred from content:
        /// a row whose payload is empty because nothing was offered would otherwise be refetched on
        /// every sync for ever.
        /// </remarks>
        private static VideoExtras ReadExtras(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return VideoExtras.NoPayload;

            try
            {
                return JsonSerializer.Deserialize<VideoExtras>(json) ?? VideoExtras.NoPayload;
            }
            catch (JsonException)
            {
                return VideoExtras.NoPayload;
            }
        }

        /// <summary>
        /// Serialises the payload for storage, stamping the current shape version on one that has
        /// none.
        /// </summary>
        /// <remarks>
        /// Version 0 is the read side's marker for "never given a payload", so it must not reach the
        /// column: a row written back as it was read — which is what a probe that resolved nothing
        /// does — has to come back as a stored payload, or it is probed again on every sync for ever.
        /// Normalising at the single write boundary keeps that unrepresentable, whatever the caller
        /// hands over.
        /// </remarks>
        private static string SerializeExtras(VideoExtras extras)
            => JsonSerializer.Serialize(
                extras.Version > 0 ? extras : extras with { Version = VideoExtras.CurrentVersion });

        [LoggerMessage(EventId = 100, Level = LogLevel.Information,
            Message = "VOD database schema ready at {DbPath}")]
        private partial void LogSchemaReady(string dbPath);

        [LoggerMessage(EventId = 101, Level = LogLevel.Information,
            Message = "VOD database migrated from schema v{FromVersion} to v{ToVersion}")]
        private partial void LogSchemaMigrated(int fromVersion, int toVersion);

        [LoggerMessage(EventId = 102, Level = LogLevel.Warning,
            Message = "VOD database is at schema v{FromVersion} but this build knows v{KnownVersion}: "
                + "it was written by a newer plugin, and reads may fail on columns this build does "
                + "not know about")]
        private partial void LogSchemaFromTheFuture(int fromVersion, int knownVersion);

        [LoggerMessage(EventId = 103, Level = LogLevel.Error,
            Message = "VOD database migration from schema v{FromVersion} rolled back: the schema "
                + "and its rows are unchanged, and the next start will try again")]
        private partial void LogMigrationRolledBack(int fromVersion);
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
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
        private const int SchemaVersion = 8;

        /// <inheritdoc />
        public void EnsureCreated()
        {
            lock (_sync)
            {
                var from = ReadSchemaVersion();
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
                case 1:
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS sources (
                            url                  TEXT PRIMARY KEY,
                            name                 TEXT,
                            series_thumbnail_url TEXT,
                            series_banner_url    TEXT,
                            tags                 TEXT,
                            published_image_url  TEXT,
                            published_banner_url TEXT,
                            series_synopsis      TEXT,
                            last_synced_at       TEXT,
                            last_error           TEXT,
                            video_count          INTEGER NOT NULL DEFAULT 0
                        );

                        CREATE TABLE IF NOT EXISTS videos (
                            id               TEXT PRIMARY KEY,
                            source_url       TEXT NOT NULL REFERENCES sources(url),
                            extractor_key    TEXT,
                            native_id        TEXT,
                            video_url        TEXT NOT NULL,
                            title            TEXT,
                            published_at     TEXT,
                            duration_seconds INTEGER,
                            season_number    INTEGER,
                            episode_number   INTEGER,
                            synced_at        TEXT NOT NULL,
                            audio_langs      TEXT,
                            subtitle_langs   TEXT,
                            thumbnail_url    TEXT,
                            published_image_url TEXT,
                            tags             TEXT,
                            categories       TEXT,
                            description      TEXT,
                            is_live          INTEGER NOT NULL DEFAULT 0
                        );

                        CREATE INDEX IF NOT EXISTS idx_videos_source_url ON videos(source_url);
                        CREATE INDEX IF NOT EXISTS idx_videos_synced_at  ON videos(synced_at);
                        CREATE INDEX IF NOT EXISTS idx_videos_native_id  ON videos(source_url, native_id);

                        INSERT OR IGNORE INTO sources (url, name) VALUES ('__debug__', 'Debug (internal)');

                        PRAGMA user_version = 1;
                        """;
                    break;

                case 2:
                    cmd.CommandText = """
                        ALTER TABLE videos ADD COLUMN channels TEXT;

                        PRAGMA user_version = 2;
                        """;
                    break;

                case 3:
                    // Backfilled from synced_at, which is the LATEST sync rather than the first: an
                    // approximation, and the only one available for a row that predates this column.
                    // Far better than null, which would leave the published date added undefined.
                    cmd.CommandText = """
                        ALTER TABLE videos ADD COLUMN first_seen_at TEXT;

                        UPDATE videos SET first_seen_at = synced_at WHERE first_seen_at IS NULL;

                        PRAGMA user_version = 3;
                        """;
                    break;

                case 4:
                    // No backfill: there is no honest value to invent for a row that predates the
                    // column. Null means "unknown", and the sweep falls back to the configured
                    // libraries, which is the best effort that existed before it.
                    cmd.CommandText = """
                        ALTER TABLE sources ADD COLUMN published_anchor_id TEXT;

                        PRAGMA user_version = 4;
                        """;
                    break;

                case 5:
                    // The five JSON columns collapse into one typed payload; none of them was ever
                    // used in a predicate, an ORDER BY or a targeted UPDATE, so none needs a column.
                    // No backfill: a resync repopulates, and the project is not released.
                    // The '__debug__' source row was inserted by v1 and filtered out of every
                    // listing ever since — it existed only to be excluded.
                    // videos.source_url is a foreign key onto sources.url (PRAGMA foreign_keys=ON),
                    // so the children must be deleted before the parent — same order as DeleteSource.
                    // Reversing the order raises SQLITE_CONSTRAINT and strands the database at v4.
                    cmd.CommandText = """
                        ALTER TABLE videos ADD COLUMN extras TEXT;

                        DELETE FROM videos  WHERE source_url = '__debug__';
                        DELETE FROM sources WHERE url        = '__debug__';

                        ALTER TABLE videos DROP COLUMN audio_langs;
                        ALTER TABLE videos DROP COLUMN subtitle_langs;
                        ALTER TABLE videos DROP COLUMN tags;
                        ALTER TABLE videos DROP COLUMN categories;
                        ALTER TABLE videos DROP COLUMN channels;

                        PRAGMA user_version = 5;
                        """;
                    break;

                case 6:
                    // is_live could not tell a scheduled stream from a finished one. The replacement
                    // is stored as text rather than an int because it is read back as an enum name,
                    // and it is a column rather than an extras field because it is a predicate: the
                    // sync's skip-set and the live probe both select on it.
                    // No backfill: the project is not released, and the next pass over a row sets it.
                    cmd.CommandText = """
                        ALTER TABLE videos ADD COLUMN live_status TEXT NOT NULL DEFAULT 'NotLive';
                        ALTER TABLE videos DROP COLUMN is_live;

                        PRAGMA user_version = 6;
                        """;
                    break;

                case 7:
                    // ProcessEntries built its native-id map before its loop and never updated it,
                    // so a listing that returned the same video twice created two rows, one Guid
                    // each — observed in production with identical first_seen_at values. Every
                    // later sync of that source then threw on the map build, permanently.
                    // The rows are deleted oldest-wins (rowid breaks a tie, which the observed
                    // case needs), then the v1 index is rebuilt as UNIQUE under the same name, so
                    // the defect can no longer be silent. NULL native ids are left alone: SQLite
                    // treats NULLs as distinct in a UNIQUE index, and they are not duplicates of
                    // one another.
                    // The DELETE runs on its own command rather than in the script below, for its
                    // affected-row count: this is the first migration of a released build that
                    // destroys rows, and a user upgrading has no other way to learn what it took.
                    using (var dedupe = Connection.CreateCommand())
                    {
                        dedupe.Transaction = txn;
                        dedupe.CommandText = """
                            DELETE FROM videos
                            WHERE native_id IS NOT NULL
                              AND rowid NOT IN (
                                  SELECT rowid FROM (
                                      SELECT rowid,
                                             ROW_NUMBER() OVER (
                                                 PARTITION BY source_url, native_id
                                                 ORDER BY COALESCE(first_seen_at, '9999') ASC, rowid ASC
                                             ) AS rank
                                      FROM videos
                                      WHERE native_id IS NOT NULL
                                  )
                                  WHERE rank = 1
                              );
                            """;

                        var dropped = dedupe.ExecuteNonQuery();
                        if (dropped > 0)
                        {
                            LogDroppedDuplicateVideos(dropped);
                        }
                    }

                    cmd.CommandText = """
                        DROP INDEX IF EXISTS idx_videos_native_id;
                        CREATE UNIQUE INDEX idx_videos_native_id ON videos(source_url, native_id);

                        PRAGMA user_version = 7;
                        """;
                    break;

                case 8:
                    // The five columns the deferred-resolution design is built on, plus the
                    // source-level timestamp the configuration page shows.
                    // The backfill is the honest reading of what a v7 database holds: a row with
                    // metadata in extras has already been paid for, so it is resolved; one without
                    // has never been resolved, so it is pending and the resolver will take it. No
                    // re-download, nothing lost. listing_position stays NULL until the next
                    // enumeration, which costs nothing — those rows are already resolved.
                    cmd.CommandText = """
                        ALTER TABLE videos ADD COLUMN resolution_state        TEXT NOT NULL DEFAULT 'Pending';
                        ALTER TABLE videos ADD COLUMN resolution_attempted_at TEXT;
                        ALTER TABLE videos ADD COLUMN resolution_attempts     INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE videos ADD COLUMN resolution_error        TEXT;
                        ALTER TABLE videos ADD COLUMN listing_position        INTEGER;

                        ALTER TABLE sources ADD COLUMN last_resolved_at TEXT;

                        UPDATE videos SET resolution_state = 'Resolved'
                        WHERE extras IS NOT NULL AND extras <> '';

                        CREATE INDEX IF NOT EXISTS idx_videos_resolution
                            ON videos(extractor_key, resolution_state);

                        PRAGMA user_version = 8;
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
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO sources
                        (url, name, series_thumbnail_url, series_banner_url, tags, series_synopsis,
                         last_synced_at, last_error, video_count, published_anchor_id,
                         last_resolved_at)
                    VALUES
                        ($url, $name, $thumbnailUrl, $bannerUrl, $tags, $synopsis,
                         $lastSyncedAt, $lastError, $videoCount, $publishedAnchorId,
                         $lastResolvedAt)
                    ON CONFLICT(url) DO UPDATE SET
                        name                 = excluded.name,
                        series_thumbnail_url = excluded.series_thumbnail_url,
                        series_banner_url    = excluded.series_banner_url,
                        tags                 = excluded.tags,
                        series_synopsis      = excluded.series_synopsis,
                        last_synced_at       = excluded.last_synced_at,
                        last_resolved_at     = excluded.last_resolved_at,
                        last_error           = excluded.last_error,
                        video_count          = excluded.video_count,
                        published_anchor_id  = excluded.published_anchor_id;
                    """;
                cmd.Parameters.AddWithValue("$url", source.Url);
                cmd.Parameters.AddWithValue("$name", (object?)source.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)source.SeriesThumbnailUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bannerUrl", (object?)source.SeriesBannerUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tags", (object?)source.Tags ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$synopsis", (object?)source.SeriesSynopsis ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lastSyncedAt", (object?)source.LastSyncedAt?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue(
                    "$lastResolvedAt", (object?)source.LastResolvedAt?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lastError", (object?)source.LastError ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$videoCount", source.VideoCount);
                cmd.Parameters.AddWithValue(
                    "$publishedAnchorId",
                    (object?)source.PublishedAnchorId?.ToString("N") ?? DBNull.Value);
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
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO videos
                        (id, source_url, extractor_key, native_id, video_url, title,
                         published_at, duration_seconds,
                         season_number, episode_number, synced_at,
                         thumbnail_url, extras,
                         description, live_status, first_seen_at,
                         resolution_state, resolution_attempted_at, resolution_attempts,
                         resolution_error, listing_position)
                    VALUES
                        ($id, $sourceUrl, $extractorKey, $nativeId, $videoUrl, $title,
                         $publishedAt, $durationSeconds,
                         $seasonNumber, $episodeNumber, $syncedAt,
                         $thumbnailUrl, $extras,
                         $description, $liveStatus, $firstSeenAt,
                         $resolutionState, $resolutionAttemptedAt, $resolutionAttempts,
                         $resolutionError, $listingPosition)
                    ON CONFLICT(id) DO UPDATE SET
                        source_url       = excluded.source_url,
                        extractor_key    = excluded.extractor_key,
                        native_id        = excluded.native_id,
                        video_url        = excluded.video_url,
                        title            = excluded.title,
                        published_at     = excluded.published_at,
                        duration_seconds = excluded.duration_seconds,
                        season_number    = excluded.season_number,
                        episode_number   = excluded.episode_number,
                        synced_at        = excluded.synced_at,
                        thumbnail_url    = excluded.thumbnail_url,
                        extras           = excluded.extras,
                        description      = excluded.description,
                        live_status      = excluded.live_status,
                        resolution_state        = excluded.resolution_state,
                        resolution_attempted_at = excluded.resolution_attempted_at,
                        resolution_attempts     = excluded.resolution_attempts,
                        resolution_error        = excluded.resolution_error,
                        listing_position        = excluded.listing_position,
                        -- Never overwritten: an existing discovery date always beats the one the new
                        -- upsert proposes. This COALESCE is what enforces "set once, never updated".
                        first_seen_at    = COALESCE(videos.first_seen_at, excluded.first_seen_at);
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
        public IReadOnlyList<VideoRecord> GetPublishableVideosBySource(string sourceUrl)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText =
                    "SELECT * FROM videos WHERE source_url = $url AND resolution_state = $resolved "
                    + "ORDER BY published_at, id";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$resolved", nameof(ResolutionState.Resolved));
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
        public IReadOnlyList<string> GetPublishableVideoIds()
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT id FROM videos WHERE resolution_state = $resolved";
                cmd.Parameters.AddWithValue("$resolved", nameof(ResolutionState.Resolved));
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
        public VideoRecord? GetVideoByNativeId(string sourceUrl, string nativeId)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM videos WHERE source_url = $url AND native_id = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$id", nativeId);
                using var r = cmd.ExecuteReader();
                return r.Read() ? MapVideo(r) : null;
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
        public void TouchVideos(string sourceUrl, IReadOnlyCollection<string> nativeIds, DateTimeOffset syncedAt)
        {
            if (nativeIds.Count == 0) return;
            lock (_sync)
            {
                using var txn = Connection.BeginTransaction();
                using var cmd = Connection.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText =
                    "UPDATE videos SET synced_at = $syncedAt WHERE source_url = $url AND native_id = $nativeId";
                var pSyncedAt = cmd.CreateParameter();
                pSyncedAt.ParameterName = "$syncedAt";
                pSyncedAt.Value = syncedAt.ToString("O");
                cmd.Parameters.Add(pSyncedAt);
                var pUrl = cmd.CreateParameter();
                pUrl.ParameterName = "$url";
                pUrl.Value = sourceUrl;
                cmd.Parameters.Add(pUrl);
                var pNativeId = cmd.CreateParameter();
                pNativeId.ParameterName = "$nativeId";
                cmd.Parameters.Add(pNativeId);
                foreach (var id in nativeIds)
                {
                    pNativeId.Value = id;
                    cmd.ExecuteNonQuery();
                }
                txn.Commit();
            }
        }

        /// <summary>
        /// How long a row may claim to be broadcasting or scheduled without being written again
        /// before the orphan sweep stops sparing it.
        /// </summary>
        /// <remarks>
        /// The quantity this has to clear is not a broadcast's length — every probe pass rewrites a
        /// live row's <c>synced_at</c>, so a week-long stream stays fresh throughout — but the
        /// longest plausible gap between two successful writes of a row that is still live. A week
        /// clears the longest broadcasts anyone runs (multi-day charity marathons), any sane probe
        /// interval, and a long run of yt-dlp failures on top, while bounding a stuck row's life to
        /// days rather than for ever. Sparing a live row wrongly removes something the user is
        /// watching; sweeping a stale one wrongly costs one re-publication on the next pass, so the
        /// bound is deliberately generous.
        /// </remarks>
        private static readonly TimeSpan LiveExemptionMaxAge = TimeSpan.FromDays(7);

        /// <inheritdoc />
        public IReadOnlyList<string> GetOrphanedVideoUuids(string sourceUrl, DateTimeOffset syncedBefore, DateTimeOffset? retentionCutoff = null)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                // A row that is broadcasting or scheduled is spared, but only while it is FRESH.
                //
                // Spared, because the probe publishes ids the sync's listing does not carry — the
                // stream id on a subscriber-only channel, a different id per pass for a live-only
                // source — so synced_at alone deleted the row, and with it the library item, in the
                // middle of the broadcast.
                //
                // Only while fresh, because one such row can never return to NotLive: the fallback
                // row's URL is the channel root, which yt-dlp answers for only while the channel is
                // live, so once the broadcast ends its re-fetch fails for ever; and its stream id is
                // absent from the /videos listing, so no sync touches it either. An unbounded
                // exemption left one phantom per broadcast, accumulating. Freshness separates the
                // two cases: a live row is rewritten on every probe pass, while a stuck row stops
                // being written the moment its re-fetch starts failing.
                var sql = retentionCutoff.HasValue
                    ? "SELECT id FROM videos WHERE source_url = $url AND (live_status = $notLive OR synced_at < $stale) AND (synced_at < $cutoff OR (published_at IS NOT NULL AND published_at < $retention))"
                    : "SELECT id FROM videos WHERE source_url = $url AND (live_status = $notLive OR synced_at < $stale) AND synced_at < $cutoff";
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$notLive", nameof(LiveStatus.NotLive));
                cmd.Parameters.AddWithValue("$cutoff", syncedBefore.ToString("O"));
                cmd.Parameters.AddWithValue("$stale", syncedBefore.Subtract(LiveExemptionMaxAge).ToString("O"));
                if (retentionCutoff.HasValue)
                    cmd.Parameters.AddWithValue("$retention", retentionCutoff.Value.ToString("O"));
                using var r = cmd.ExecuteReader();
                var list = new List<string>();
                while (r.Read())
                    list.Add(r.GetString(0));
                return list;
            }
        }

        /// <inheritdoc />
        public void Dispose() => _connection?.Dispose();

        private static void BindVideo(SqliteCommand cmd, VideoRecord v)
        {
            cmd.Parameters.AddWithValue("$id", v.Id);
            cmd.Parameters.AddWithValue("$sourceUrl", v.SourceUrl);
            cmd.Parameters.AddWithValue("$extractorKey", (object?)v.ExtractorKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nativeId", (object?)v.NativeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$videoUrl", v.VideoUrl);
            cmd.Parameters.AddWithValue("$title", (object?)v.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$publishedAt", (object?)v.PublishedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$durationSeconds", (object?)v.DurationSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seasonNumber", (object?)v.SeasonNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$episodeNumber", (object?)v.EpisodeNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$syncedAt", v.SyncedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)v.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$extras", SerializeExtras(v.Extras));
            cmd.Parameters.AddWithValue("$firstSeenAt", (object?)v.FirstSeenAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)v.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$liveStatus", v.LiveStatus.ToString());
            cmd.Parameters.AddWithValue("$resolutionState", v.ResolutionState.ToString());
            cmd.Parameters.AddWithValue(
                "$resolutionAttemptedAt", (object?)v.ResolutionAttemptedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$resolutionAttempts", v.ResolutionAttempts);
            cmd.Parameters.AddWithValue("$resolutionError", (object?)v.ResolutionError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$listingPosition", (object?)v.ListingPosition ?? DBNull.Value);
        }

        private static SourceRecord MapSource(SqliteDataReader r)
        {
            string? S(string col)
            {
                var ord = r.GetOrdinal(col);
                return r.IsDBNull(ord) ? null : r.GetString(ord);
            }

            DateTimeOffset? D(string col)
            {
                var s = S(col);
                return s is null ? null : DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }

            Guid? G(string col)
            {
                var s = S(col);
                return s is not null && Guid.TryParse(s, out var id) ? id : null;
            }

            return new SourceRecord
            {
                Url = r.GetString(r.GetOrdinal("url")),
                Name = S("name"),
                SeriesThumbnailUrl = S("series_thumbnail_url"),
                SeriesBannerUrl = S("series_banner_url"),
                Tags = S("tags"),
                PublishedImageUrl = S("published_image_url"),
                PublishedBannerUrl = S("published_banner_url"),
                SeriesSynopsis = S("series_synopsis"),
                LastSyncedAt = D("last_synced_at"),
                LastError = S("last_error"),
                VideoCount = r.GetInt32(r.GetOrdinal("video_count")),
                PublishedAnchorId = G("published_anchor_id"),
                LastResolvedAt = D("last_resolved_at")
            };
        }

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
                return s is null ? null : DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }

            return new VideoRecord
            {
                Id = r.GetString(r.GetOrdinal("id")),
                SourceUrl = r.GetString(r.GetOrdinal("source_url")),
                ExtractorKey = S("extractor_key"),
                NativeId = S("native_id"),
                VideoUrl = r.GetString(r.GetOrdinal("video_url")),
                Title = S("title"),
                PublishedAt = D("published_at"),
                DurationSeconds = I("duration_seconds"),
                SeasonNumber = I("season_number"),
                EpisodeNumber = I("episode_number"),
                SyncedAt = DateTimeOffset.Parse(
                    r.GetString(r.GetOrdinal("synced_at")), null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                ThumbnailUrl = S("thumbnail_url"),
                Extras = ReadExtras(S("extras")),
                FirstSeenAt = D("first_seen_at"),
                PublishedImageUrl = S("published_image_url"),
                Description = S("description"),
                LiveStatus = Enum.TryParse<LiveStatus>(S("live_status"), out var ls) && Enum.IsDefined(ls)
                    ? ls
                    : LiveStatus.NotLive,

                // An unparseable state means a newer build wrote it; pending is the safe reading,
                // since it only costs one resolution and never publishes anything wrong.
                ResolutionState =
                    Enum.TryParse<ResolutionState>(S("resolution_state"), out var rs) && Enum.IsDefined(rs)
                        ? rs
                        : ResolutionState.Pending,
                ResolutionAttemptedAt = D("resolution_attempted_at"),
                ResolutionAttempts = r.GetInt32(r.GetOrdinal("resolution_attempts")),
                ResolutionError = S("resolution_error"),
                ListingPosition = I("listing_position"),
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
            Message = "Migration v7 deleted {Dropped} duplicate video row(s): a listing had "
                + "returned the same video twice and each repeat had been stored separately")]
        private partial void LogDroppedDuplicateVideos(int dropped);

        [LoggerMessage(EventId = 103, Level = LogLevel.Error,
            Message = "VOD database migration from schema v{FromVersion} rolled back: the schema "
                + "and its rows are unchanged, and the next start will try again")]
        private partial void LogMigrationRolledBack(int fromVersion);
    }
}

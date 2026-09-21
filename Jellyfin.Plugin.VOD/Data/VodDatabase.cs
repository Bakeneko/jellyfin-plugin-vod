using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.VOD.Data.Models;
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
        private const int SchemaVersion = 1;

        /// <inheritdoc />
        public void EnsureCreated()
        {
            lock (_sync)
            {
                var from = ReadSchemaVersion();
                if (from < SchemaVersion)
                {
                    using var txn = Connection.BeginTransaction();
                    for (var v = from + 1; v <= SchemaVersion; v++)
                        Migrate(v, txn);

                    txn.Commit();
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
                            series_path          TEXT,
                            series_thumbnail_url TEXT,
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
                            date_is_fallback INTEGER NOT NULL DEFAULT 0,
                            published_at     TEXT,
                            duration_seconds INTEGER,
                            series_path      TEXT,
                            season_number    INTEGER,
                            episode_number   INTEGER,
                            strm_path        TEXT,
                            synced_at        TEXT NOT NULL,
                            audio_langs      TEXT,
                            subtitle_langs   TEXT,
                            thumbnail_url    TEXT,
                            description      TEXT,
                            available_until  TEXT,
                            source_type      TEXT,
                            is_live          INTEGER NOT NULL DEFAULT 0
                        );

                        CREATE INDEX IF NOT EXISTS idx_videos_source_url ON videos(source_url);
                        CREATE INDEX IF NOT EXISTS idx_videos_synced_at  ON videos(synced_at);
                        CREATE INDEX IF NOT EXISTS idx_videos_native_id  ON videos(source_url, native_id);

                        INSERT OR IGNORE INTO sources (url, name) VALUES ('__debug__', 'Debug (internal)');

                        PRAGMA user_version = 1;
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
                        (url, name, series_path, series_thumbnail_url, series_synopsis,
                         last_synced_at, last_error, video_count)
                    VALUES
                        ($url, $name, $seriesPath, $thumbnailUrl, $synopsis,
                         $lastSyncedAt, $lastError, $videoCount)
                    ON CONFLICT(url) DO UPDATE SET
                        name                 = excluded.name,
                        series_path          = excluded.series_path,
                        series_thumbnail_url = excluded.series_thumbnail_url,
                        series_synopsis      = excluded.series_synopsis,
                        last_synced_at       = excluded.last_synced_at,
                        last_error           = excluded.last_error,
                        video_count          = excluded.video_count;
                    """;
                cmd.Parameters.AddWithValue("$url", source.Url);
                cmd.Parameters.AddWithValue("$name", (object?)source.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$seriesPath", (object?)source.SeriesPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)source.SeriesThumbnailUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$synopsis", (object?)source.SeriesSynopsis ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lastSyncedAt", (object?)source.LastSyncedAt?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lastError", (object?)source.LastError ?? DBNull.Value);
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
                cmd.CommandText = "SELECT * FROM sources WHERE url != '__debug__' ORDER BY url";
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
                         date_is_fallback, published_at, duration_seconds, series_path,
                         season_number, episode_number, strm_path, synced_at,
                         audio_langs, subtitle_langs, thumbnail_url, description,
                         available_until, source_type, is_live)
                    VALUES
                        ($id, $sourceUrl, $extractorKey, $nativeId, $videoUrl, $title,
                         $dateIsFallback, $publishedAt, $durationSeconds, $seriesPath,
                         $seasonNumber, $episodeNumber, $strmPath, $syncedAt,
                         $audioLangs, $subtitleLangs, $thumbnailUrl, $description,
                         $availableUntil, $sourceType, $isLive)
                    ON CONFLICT(id) DO UPDATE SET
                        source_url       = excluded.source_url,
                        extractor_key    = excluded.extractor_key,
                        native_id        = excluded.native_id,
                        video_url        = excluded.video_url,
                        title            = excluded.title,
                        date_is_fallback = excluded.date_is_fallback,
                        published_at     = excluded.published_at,
                        duration_seconds = excluded.duration_seconds,
                        series_path      = excluded.series_path,
                        season_number    = excluded.season_number,
                        episode_number   = excluded.episode_number,
                        strm_path        = excluded.strm_path,
                        synced_at        = excluded.synced_at,
                        audio_langs      = excluded.audio_langs,
                        subtitle_langs   = excluded.subtitle_langs,
                        thumbnail_url    = excluded.thumbnail_url,
                        description      = excluded.description,
                        available_until  = excluded.available_until,
                        source_type      = excluded.source_type,
                        is_live          = excluded.is_live;
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

        /// <inheritdoc />
        public IReadOnlyList<string> GetOrphanedVideoUuids(string sourceUrl, DateTimeOffset syncedBefore, DateTimeOffset? retentionCutoff = null)
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                var sql = retentionCutoff.HasValue
                    ? "SELECT id FROM videos WHERE source_url = $url AND (synced_at < $cutoff OR (published_at IS NOT NULL AND published_at < $retention))"
                    : "SELECT id FROM videos WHERE source_url = $url AND synced_at < $cutoff";
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$url", sourceUrl);
                cmd.Parameters.AddWithValue("$cutoff", syncedBefore.ToString("O"));
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
        public IReadOnlyList<string> GetAllStrmPaths()
        {
            lock (_sync)
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT strm_path FROM videos WHERE strm_path IS NOT NULL";
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
            cmd.Parameters.AddWithValue("$dateIsFallback", v.DateIsFallback ? 1 : 0);
            cmd.Parameters.AddWithValue("$publishedAt", (object?)v.PublishedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$durationSeconds", (object?)v.DurationSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seriesPath", (object?)v.SeriesPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seasonNumber", (object?)v.SeasonNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$episodeNumber", (object?)v.EpisodeNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$strmPath", (object?)v.StrmPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$syncedAt", v.SyncedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$audioLangs", (object?)v.AudioLangs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subtitleLangs", (object?)v.SubtitleLangs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)v.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)v.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$availableUntil", (object?)v.AvailableUntil?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sourceType", (object?)v.SourceType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isLive", v.IsLive ? 1 : 0);
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

            return new SourceRecord
            {
                Url = r.GetString(r.GetOrdinal("url")),
                Name = S("name"),
                SeriesPath = S("series_path"),
                SeriesThumbnailUrl = S("series_thumbnail_url"),
                SeriesSynopsis = S("series_synopsis"),
                LastSyncedAt = D("last_synced_at"),
                LastError = S("last_error"),
                VideoCount = r.GetInt32(r.GetOrdinal("video_count"))
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
                DateIsFallback = r.GetInt32(r.GetOrdinal("date_is_fallback")) != 0,
                PublishedAt = D("published_at"),
                DurationSeconds = I("duration_seconds"),
                SeriesPath = S("series_path"),
                SeasonNumber = I("season_number"),
                EpisodeNumber = I("episode_number"),
                StrmPath = S("strm_path"),
                SyncedAt = DateTimeOffset.Parse(
                    r.GetString(r.GetOrdinal("synced_at")), null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                AudioLangs = S("audio_langs"),
                SubtitleLangs = S("subtitle_langs"),
                ThumbnailUrl = S("thumbnail_url"),
                Description = S("description"),
                AvailableUntil = D("available_until"),
                SourceType = S("source_type"),
                IsLive = r.GetInt32(r.GetOrdinal("is_live")) != 0
            };
        }

        [LoggerMessage(EventId = 100, Level = LogLevel.Information,
            Message = "VOD database schema ready at {DbPath}")]
        private partial void LogSchemaReady(string dbPath);

        [LoggerMessage(EventId = 101, Level = LogLevel.Information,
            Message = "VOD database migrated from schema v{FromVersion} to v{ToVersion}")]
        private partial void LogSchemaMigrated(int fromVersion, int toVersion);
    }
}

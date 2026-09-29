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
        private const int SchemaVersion = 4;

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
                         last_synced_at, last_error, video_count, published_anchor_id)
                    VALUES
                        ($url, $name, $thumbnailUrl, $bannerUrl, $tags, $synopsis,
                         $lastSyncedAt, $lastError, $videoCount, $publishedAnchorId)
                    ON CONFLICT(url) DO UPDATE SET
                        name                 = excluded.name,
                        series_thumbnail_url = excluded.series_thumbnail_url,
                        series_banner_url    = excluded.series_banner_url,
                        tags                 = excluded.tags,
                        series_synopsis      = excluded.series_synopsis,
                        last_synced_at       = excluded.last_synced_at,
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
                         published_at, duration_seconds,
                         season_number, episode_number, synced_at,
                         audio_langs, subtitle_langs, thumbnail_url, tags, categories, channels,
                         description, is_live, first_seen_at)
                    VALUES
                        ($id, $sourceUrl, $extractorKey, $nativeId, $videoUrl, $title,
                         $publishedAt, $durationSeconds,
                         $seasonNumber, $episodeNumber, $syncedAt,
                         $audioLangs, $subtitleLangs, $thumbnailUrl, $tags, $categories, $channels,
                         $description, $isLive, $firstSeenAt)
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
                        audio_langs      = excluded.audio_langs,
                        subtitle_langs   = excluded.subtitle_langs,
                        thumbnail_url    = excluded.thumbnail_url,
                        tags             = excluded.tags,
                        categories       = excluded.categories,
                        channels         = excluded.channels,
                        description      = excluded.description,
                        is_live          = excluded.is_live,
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
            cmd.Parameters.AddWithValue("$audioLangs", (object?)v.AudioLangs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subtitleLangs", (object?)v.SubtitleLangs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)v.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", (object?)v.Tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$categories", (object?)v.Categories ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$channels", (object?)v.Channels ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$firstSeenAt", (object?)v.FirstSeenAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)v.Description ?? DBNull.Value);
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
                PublishedAnchorId = G("published_anchor_id")
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
                AudioLangs = S("audio_langs"),
                SubtitleLangs = S("subtitle_langs"),
                ThumbnailUrl = S("thumbnail_url"),
                Tags = S("tags"),
                Categories = S("categories"),
                Channels = S("channels"),
                FirstSeenAt = D("first_seen_at"),
                PublishedImageUrl = S("published_image_url"),
                Description = S("description"),
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

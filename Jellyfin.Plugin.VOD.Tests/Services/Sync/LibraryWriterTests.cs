using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class LibraryWriterTests : IDisposable
    {
        private readonly string _base;
        private readonly LibraryWriter _writer;

        public LibraryWriterTests()
        {
            _base = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_base);
            _writer = new LibraryWriter(NullLogger<LibraryWriter>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(_base))
                Directory.Delete(_base, recursive: true);
        }

        // ── Sanitize ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Hello/World",       "Hello_World")]
        [InlineData("A:B*C?D\"E<F>G|H",  "A_B_C_D_E_F_G_H")]
        [InlineData("Normal title",      "Normal title")]
        public void Sanitize_ReplacesInvalidChars(string input, string expected)
        {
            Assert.Equal(expected, LibraryWriter.Sanitize(input));
        }

        [Fact]
        public void Sanitize_TruncatesAtMaxLength()
        {
            var longName = new string('A', 250);
            var result = LibraryWriter.Sanitize(longName, 200);
            Assert.Equal(200, result.Length);
        }

        [Fact]
        public void Sanitize_ShortName_NotTruncated()
        {
            Assert.Equal("abc", LibraryWriter.Sanitize("abc", 200));
        }

        // ── BuildStrmPath — Series mode (no NativeId → fallback [uuid] prefix) ─

        [Fact]
        public void BuildStrmPath_SeriesMode_ReturnsExpectedPath()
        {
            // No NativeId/ExtractorKey → fallback [uuid] prefix.
            // Source URL matches youtube_ch_arte for series folder.
            var video = new VideoRecord
            {
                Id = "test-uuid-001",
                SourceUrl = "https://www.youtube.com/@arte",
                Title = "My Episode",
                SeasonNumber = 2026,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Series, "Arte", _base);

            var expected = Path.Combine(
                _base,
                "[imdbid-youtube_ch_arte] Arte",
                "Season 2026",
                "[test-uuid-001] My Episode.strm");
            Assert.Equal(expected, path);
        }

        [Fact]
        public void BuildStrmPath_SeriesMode_WithNativeId_IncludesImdbPrefixInEpisodeAndSeriesFolder()
        {
            // NativeId + ExtractorKey set → both series folder and episode file carry [imdbid-...].
            var video = new VideoRecord
            {
                Id = "arte-uuid",
                NativeId = "126957-001-A",
                ExtractorKey = "ArteTV",
                SourceUrl = "https://www.arte.tv/fr/videos/RC-027708/happy-valley/",
                Title = "Happy Valley",
                SeasonNumber = 1,
                EpisodeNumber = 1,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Series, "Happy Valley", _base);

            var expected = Path.Combine(
                _base,
                "[imdbid-arte_pl_RC-027708] Happy Valley",
                "Season 01",
                "[imdbid-arte_v_126957-001-A][arte-uuid] S01E001 - Happy Valley.strm");
            Assert.Equal(expected, path);
        }

        [Fact]
        public void BuildStrmPath_SeriesMode_DateBasedEpisode_UsesPaddedNumberPrefix()
        {
            // Date-based episodes (>= 101001): S{year}E{episode:D7} prefix.
            // April 23, rank 1 = (4*100+23)*1000+1 = 423001 → "S2026E0423001"
            var video = new VideoRecord
            {
                Id = "yt-uuid",
                SourceUrl = "https://www.youtube.com/@deadwattsofficiel/videos",
                Title = "Dead Watts EP42",
                SeasonNumber = 2026,
                EpisodeNumber = 423001,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Series, "Dead Watts", _base);

            Assert.Contains("[imdbid-youtube_ch_deadwattsofficiel] Dead Watts", path, StringComparison.Ordinal);
            Assert.Contains("Season 2026", path, StringComparison.Ordinal);
            Assert.Contains("[yt-uuid] S2026E0423001 - Dead Watts EP42", path, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildStrmPath_SeriesMode_WithEpisodeNumber_AddsSePrefix()
        {
            // Arte series S1E3 — no NativeId on video, source ID from URL.
            var video = new VideoRecord
            {
                Id = "arte-uuid",
                SourceUrl = "https://www.arte.tv/fr/videos/RC-027708/happy-valley/",
                Title = "Happy Valley",
                SeasonNumber = 1,
                EpisodeNumber = 3,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Series, "Happy Valley", _base);

            var expected = Path.Combine(
                _base,
                "[imdbid-arte_pl_RC-027708] Happy Valley",
                "Season 01",
                "[arte-uuid] S01E003 - Happy Valley.strm");
            Assert.Equal(expected, path);
        }

        [Fact]
        public void BuildStrmPath_SeriesMode_TwoDigitSeason_PadsSeasonFolder()
        {
            var video = new VideoRecord
            {
                Id = "arte-s2",
                SourceUrl = "https://www.arte.tv/fr/videos/RC-027708/happy-valley/",
                Title = "Happy Valley",
                SeasonNumber = 2,
                EpisodeNumber = 1,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Series, "Happy Valley", _base);

            Assert.Contains("Season 02", path, StringComparison.Ordinal);
            Assert.Contains("S02E001", path, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildStrmPath_SeriesMode_NoSeason_FallsBackToUnknown()
        {
            var video = new VideoRecord
            {
                Id = "uuid-x",
                SourceUrl = "https://www.youtube.com/@arte",
                Title = "No Date",
                SeasonNumber = null,
                SyncedAt = DateTimeOffset.UtcNow
            };
            var path = _writer.BuildStrmPath(video, SourceMode.Series, "Arte", _base);
            Assert.Contains("Season Unknown", path, StringComparison.Ordinal);
        }

        // ── BuildStrmPath — Movies mode ────────────────────────────────────────

        [Fact]
        public void BuildStrmPath_MoviesMode_DistinctSeriesName_IncludesPrefix()
        {
            // No NativeId → fallback [uuid] prefix.
            // "Le Film" doesn't start with "Karambolage" → series prefix included.
            var video = new VideoRecord
            {
                Id = "movie-uuid",
                SourceUrl = "https://www.arte.tv/fr/videos/RC-024539/karambolage/",
                Title = "Le Film",
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Movies, "Karambolage", _base);

            var folderName = "[movie-uuid] Karambolage - Le Film";
            var expected = Path.Combine(_base, folderName, folderName + ".strm");
            Assert.Equal(expected, path);
        }

        [Fact]
        public void BuildStrmPath_MoviesMode_WithNativeId_IncludesImdbIdPrefix()
        {
            // NativeId + ExtractorKey → [imdbid-youtube_v_...][uuid] prefix.
            var video = new VideoRecord
            {
                Id = "7a6b5d17-f229-477c-abb3-d3db1199a7c3",
                NativeId = "PM6lMoClzy8",
                ExtractorKey = "Youtube",
                SourceUrl = "https://www.youtube.com/watch?v=PM6lMoClzy8",
                Title = "4 minutes pour comprendre les MITES ALIMENTAIRES",
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(
                video, SourceMode.Movies, "Terrapodia", _base);

            var folderName = "[imdbid-youtube_v_PM6lMoClzy8][7a6b5d17-f229-477c-abb3-d3db1199a7c3] Terrapodia - 4 minutes pour comprendre les MITES ALIMENTAIRES";
            var expected = Path.Combine(_base, folderName, folderName + ".strm");
            Assert.Equal(expected, path);
        }

        [Fact]
        public void BuildStrmPath_MoviesMode_SeriesNameEqualsTitle_OmitsPrefix()
        {
            var title = "La femme qui murmure à l'oreille des baleines bleues";
            var video = new VideoRecord
            {
                Id = "film-uuid",
                SourceUrl = "https://www.arte.tv/fr/videos/128788-000-A/film/",
                Title = title,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Movies, title, _base);

            // seriesName == title → no redundant series prefix
            var folderName = $"[film-uuid] {title}";
            Assert.Equal(Path.Combine(_base, folderName, folderName + ".strm"), path);
        }

        [Fact]
        public void BuildStrmPath_MoviesMode_TitleStartsWithSeriesName_OmitsPrefix()
        {
            var seriesName = "La femme qui murmure";
            var title = "La femme qui murmure à l'oreille des baleines bleues";
            var video = new VideoRecord
            {
                Id = "film2-uuid",
                SourceUrl = "https://www.arte.tv/fr/videos/128788-000-A/film/",
                Title = title,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Movies, seriesName, _base);

            // title already starts with seriesName → no prefix
            var folderName = $"[film2-uuid] {title}";
            Assert.Equal(Path.Combine(_base, folderName, folderName + ".strm"), path);
        }

        // ── WriteStrm ─────────────────────────────────────────────────────────

        [Fact]
        public void WriteStrm_CreatesFileWithStreamUrl()
        {
            var video = new VideoRecord
            {
                Id = "w-uuid",
                SourceUrl = "https://www.youtube.com/@arte",
                Title = "Test",
                SeasonNumber = 2026,
                SyncedAt = DateTimeOffset.UtcNow
            };
            var streamUrl = "http://localhost:8096/VOD/stream/w-uuid.m3u8";

            var strmPath = _writer.WriteStrm(video, SourceMode.Series, "Arte", _base, streamUrl);

            Assert.True(File.Exists(strmPath));
            Assert.Equal(streamUrl, File.ReadAllText(strmPath).Trim());
        }

        // ── DeleteStrm + empty dir pruning ────────────────────────────────────

        [Fact]
        public void DeleteStrm_RemovesFileAndEmptyParentDirs()
        {
            var video = new VideoRecord
            {
                Id = "del-uuid",
                SourceUrl = "https://www.youtube.com/@arte",
                Title = "ToDelete",
                SeasonNumber = 2026,
                SyncedAt = DateTimeOffset.UtcNow
            };
            var strmPath = _writer.WriteStrm(video, SourceMode.Series, "Arte", _base,
                "http://localhost:8096/VOD/stream/del-uuid.m3u8");

            Assert.True(File.Exists(strmPath));

            _writer.DeleteStrm(strmPath, _base);

            Assert.False(File.Exists(strmPath));
            // Season folder should be pruned (was empty)
            Assert.False(Directory.Exists(Path.GetDirectoryName(strmPath)));
        }

        [Fact]
        public void DeleteStrm_WithTrailingSlashBase_DoesNotDeleteBase()
        {
            var video = new VideoRecord
            {
                Id = "trailing-slash-uuid",
                SourceUrl = "https://www.youtube.com/@arte",
                Title = "Trailing",
                SeasonNumber = 2026,
                SyncedAt = DateTimeOffset.UtcNow
            };
            var baseWithSlash = _base + Path.DirectorySeparatorChar;
            var strmPath = _writer.WriteStrm(video, SourceMode.Series, "Arte", _base, "http://localhost/stream.m3u8");
            _writer.DeleteStrm(strmPath, baseWithSlash);
            // Library root must survive
            Assert.True(Directory.Exists(_base));
        }

        [Fact]
        public void DeleteStrm_DoesNotPruneDirsWithOtherContent()
        {
            var v1 = new VideoRecord { Id = "keep-1", SourceUrl = "https://www.youtube.com/@arte", Title = "Keep",   SeasonNumber = 2026, SyncedAt = DateTimeOffset.UtcNow };
            var v2 = new VideoRecord { Id = "keep-2", SourceUrl = "https://www.youtube.com/@arte", Title = "Delete", SeasonNumber = 2026, SyncedAt = DateTimeOffset.UtcNow };
            var url = "http://localhost:8096/VOD/stream/{0}.m3u8";

            _writer.WriteStrm(v1, SourceMode.Series, "Arte", _base, string.Format(url, "keep-1"));
            var strmToDelete = _writer.WriteStrm(v2, SourceMode.Series, "Arte", _base, string.Format(url, "keep-2"));

            _writer.DeleteStrm(strmToDelete, _base);

            Assert.False(File.Exists(strmToDelete));
            // Season folder still exists because v1's file is there
            Assert.True(Directory.Exists(Path.GetDirectoryName(strmToDelete)));
        }

        // ── BuildStrmPath — filename length ───────────────────────────────────

        [Fact]
        public void BuildStrmPath_MoviesMode_LongTitleDoesNotExceed255ByteFilename()
        {
            // No NativeId → shorter prefix; verify truncation still works
            var longTitle = "[LIVE] 🔴 LEC SUMMER SPLIT - KC vs MKOI EN DIRECT DE NICE - PLAYOFFS FINALE LOWER - BO5 2026-09-19 16_34";
            var video = new VideoRecord
            {
                Id = "8ff144d8-a32c-40cb-b32c-8b56a017932e",
                SourceUrl = "https://www.youtube.com/watch?v=wIYJQV3WL0k",
                Title = longTitle,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var path = _writer.BuildStrmPath(video, SourceMode.Movies, longTitle, _base);

            var strmFileName = Path.GetFileName(path);
            var dirName = Path.GetFileName(Path.GetDirectoryName(path)!);
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(strmFileName) <= 255,
                $".strm filename exceeds 255 bytes: {System.Text.Encoding.UTF8.GetByteCount(strmFileName)} bytes");
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(dirName) <= 255,
                $"directory name exceeds 255 bytes: {System.Text.Encoding.UTF8.GetByteCount(dirName)} bytes");
        }

        // ── WriteStrm — Movies mode ghost folder cleanup ──────────────────────

        [Fact]
        public void WriteStrm_MoviesMode_TitleChange_DeletesOldFolderEvenWithJellyfinMetadata()
        {
            // Live stream renamed between syncs: old folder contains Jellyfin .nfo,
            // must still be deleted so no ghost entry appears in the library.
            var uuid = "live-uuid-ghost";
            var oldTitle = "[LIVE] My Stream";
            var newTitle = "[LIVE] My Stream – renamed";

            var video = new VideoRecord
            {
                Id = uuid,
                SourceUrl = "https://www.youtube.com/watch?v=abc",
                Title = oldTitle,
                SyncedAt = DateTimeOffset.UtcNow
            };

            var oldStrmPath = _writer.WriteStrm(video, SourceMode.Movies, oldTitle, _base, "http://localhost/stream.m3u8");
            var oldDir = Path.GetDirectoryName(oldStrmPath)!;

            File.WriteAllText(Path.Combine(oldDir, "movie.nfo"), "<movie/>");

            video.Title = newTitle;
            video.StrmPath = oldStrmPath;

            var newStrmPath = _writer.WriteStrm(video, SourceMode.Movies, newTitle, _base, "http://localhost/stream.m3u8");

            Assert.True(File.Exists(newStrmPath));
            Assert.False(Directory.Exists(oldDir), $"Ghost folder should be deleted: {oldDir}");
        }

        // ── ReconcileLibrary ──────────────────────────────────────────────────

        [Fact]
        public void ReconcileLibrary_NoOp_WhenBasePathDoesNotExist()
        {
            var missing = Path.Combine(_base, "nonexistent");
            _writer.ReconcileLibrary(missing, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void ReconcileLibrary_KnownPaths_AreKept()
        {
            var video = new VideoRecord
            {
                Id = "known-uuid",
                SourceUrl = "https://www.youtube.com/@arte",
                Title = "Known",
                SeasonNumber = 2026,
                SyncedAt = DateTimeOffset.UtcNow
            };
            var strmPath = _writer.WriteStrm(video, SourceMode.Series, "Arte", _base, "http://localhost/stream.m3u8");
            var knownPaths = new HashSet<string>([Path.GetFullPath(strmPath)], StringComparer.OrdinalIgnoreCase);

            _writer.ReconcileLibrary(_base, knownPaths);

            Assert.True(File.Exists(strmPath));
        }

        [Fact]
        public void ReconcileLibrary_UnknownStrmFile_IsDeleted()
        {
            var parasiteDir = Path.Combine(_base, "ParasiteShow", "Season 1");
            Directory.CreateDirectory(parasiteDir);
            var parasiteStrm = Path.Combine(parasiteDir, "Parasite Episode [fake-uuid].strm");
            File.WriteAllText(parasiteStrm, "http://example.com/fake.m3u8");

            _writer.ReconcileLibrary(_base, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.False(File.Exists(parasiteStrm));
        }

        [Fact]
        public void ReconcileLibrary_UnknownStrmFile_EmptyAncestorDirsPruned()
        {
            var parasiteDir = Path.Combine(_base, "Ghost", "Season 1");
            Directory.CreateDirectory(parasiteDir);
            var parasiteStrm = Path.Combine(parasiteDir, "Ghost Episode [x].strm");
            File.WriteAllText(parasiteStrm, "http://example.com/ghost.m3u8");

            _writer.ReconcileLibrary(_base, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.False(File.Exists(parasiteStrm));
            Assert.False(Directory.Exists(parasiteDir));
            Assert.False(Directory.Exists(Path.Combine(_base, "Ghost")));
        }

        [Fact]
        public void ReconcileLibrary_KnownAndUnknown_OnlyUnknownDeleted()
        {
            var known = new VideoRecord
            {
                Id = "known-2",
                SourceUrl = "https://www.youtube.com/@arte",
                Title = "Known Video",
                SeasonNumber = 2026,
                SyncedAt = DateTimeOffset.UtcNow
            };
            var knownPath = _writer.WriteStrm(known, SourceMode.Series, "Arte", _base, "http://localhost/known.m3u8");

            var parasiteDir = Path.Combine(_base, "Rogue", "Season 1");
            Directory.CreateDirectory(parasiteDir);
            var parasiteStrm = Path.Combine(parasiteDir, "Rogue [rogue-uuid].strm");
            File.WriteAllText(parasiteStrm, "http://example.com/rogue.m3u8");

            var knownPaths = new HashSet<string>([Path.GetFullPath(knownPath)], StringComparer.OrdinalIgnoreCase);
            _writer.ReconcileLibrary(_base, knownPaths);

            Assert.True(File.Exists(knownPath));
            Assert.False(File.Exists(parasiteStrm));
        }

        [Fact]
        public void ReconcileLibrary_NonStrmFilesInLibrary_AreIgnored()
        {
            var artworkDir = Path.Combine(_base, "SomeSeries");
            Directory.CreateDirectory(artworkDir);
            var artwork = Path.Combine(artworkDir, "poster.jpg");
            File.WriteAllText(artwork, "fake jpeg");

            _writer.ReconcileLibrary(_base, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.True(File.Exists(artwork));
        }

        [Fact]
        public void ReconcileLibrary_PreexistingEmptyDirs_ArePruned()
        {
            var ghostSeason = Path.Combine(_base, "[imdbid-youtube_v_abc123][old-uuid] Old Title");
            Directory.CreateDirectory(ghostSeason);

            _writer.ReconcileLibrary(_base, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.False(Directory.Exists(ghostSeason));
        }

        [Fact]
        public void ReconcileLibrary_DirsWithNonStrmContent_AreNotPruned()
        {
            var seriesDir = Path.Combine(_base, "[imdbid-youtube_ch_arte] Arte");
            Directory.CreateDirectory(seriesDir);
            File.WriteAllText(Path.Combine(seriesDir, "folder.jpg"), "artwork");

            _writer.ReconcileLibrary(_base, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.True(Directory.Exists(seriesDir));
        }
    }
}

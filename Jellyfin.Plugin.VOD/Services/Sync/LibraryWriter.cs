using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Writes and removes .strm files in a Jellyfin-compatible library tree.
    /// Does not read or cache any plugin configuration — callers pass paths explicitly.
    /// </summary>
    public sealed partial class LibraryWriter
    {
        private static readonly char[] InvalidPathChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];
        private readonly ILogger<LibraryWriter> _logger;

        /// <summary>Initializes a new instance of the <see cref="LibraryWriter"/> class.</summary>
        public LibraryWriter(ILogger<LibraryWriter> logger) => _logger = logger;

        /// <summary>
        /// Replaces characters that are invalid in file/directory names and truncates to <paramref name="maxLength"/>.
        /// </summary>
        public static string Sanitize(string name, int maxLength = 200)
        {
            if (string.IsNullOrEmpty(name))
                return "_";

            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(InvalidPathChars, chars[i]) >= 0)
                    chars[i] = '_';
            }

            var result = new string(chars);
            return result.Length > maxLength ? result[..maxLength] : result;
        }

        /// <summary>
        /// Computes the absolute path where the .strm file for <paramref name="video"/> should live.
        /// Does NOT create any directories or files.
        /// </summary>
        /// <remarks>
        /// Path schemes (see project ledger § Décisions de nommage 2026-09-20):
        /// <list type="bullet">
        /// <item>Movies: <c>[imdbid-{videoId}][{uuid}] [Series - ]Title/…​.strm</c></item>
        /// <item>Series folder: <c>[imdbid-{sourceId}] SeriesName/</c></item>
        /// <item>Episode: <c>[imdbid-{videoId}][{uuid}] S01E001 - Title.strm</c></item>
        /// </list>
        /// Falls back to <c>[{uuid}]</c> alone when no native ID is available.
        /// </remarks>
        public string BuildStrmPath(
            VideoRecord video,
            SourceMode mode,
            string seriesName,
            string libraryBasePath)
        {
            var safeSeries = Sanitize(seriesName);
            var safeTitle = Sanitize(video.Title ?? video.Id, 155);
            var uuid = video.Id;

            // Build the "[imdbid-{id}][{uuid}]" prefix shared by both modes
            var videoNativeId = VodNativeId.ForVideo(video.ExtractorKey, video.NativeId);
            var idPrefix = videoNativeId is not null
                ? $"[imdbid-{Sanitize(videoNativeId)}][{uuid}]"
                : $"[{uuid}]";

            if (mode == SourceMode.Movies)
            {
                // Include "{safeSeries} - " only when it adds context not already in the title.
                var includeSeriesPrefix = !safeTitle.StartsWith(safeSeries, StringComparison.OrdinalIgnoreCase);
                var seriesPart = includeSeriesPrefix ? $"{safeSeries} - " : string.Empty;

                // Both Linux (255-byte filename limit) and Windows (255 UTF-16-unit limit) apply.
                // The .strm filename = folderName + ".strm" (5 extra bytes), so cap at 250.
                var titleBudget = 250
                    - Encoding.UTF8.GetByteCount($"{idPrefix} {seriesPart}");
                if (titleBudget > 0)
                    safeTitle = TruncateToUtf8Bytes(safeTitle, titleBudget);
                var folderName = $"{idPrefix} {seriesPart}{safeTitle}";
                return Path.Combine(libraryBasePath, folderName, folderName + ".strm");
            }

            // Series mode
            // Series folder: "[imdbid-{sourceId}] SeriesName" — no video UUID, source ID only
            var sourceNativeId = VodNativeId.ForSource(video.SourceUrl);
            var seriesFolderName = sourceNativeId is not null
                ? $"[imdbid-{Sanitize(sourceNativeId)}] {safeSeries}"
                : safeSeries;

            var season = video.SeasonNumber.HasValue
                ? $"Season {video.SeasonNumber.Value:D2}"
                : "Season Unknown";

            // Episode S/E tag: native ≤ 9999 → S01E001 ; date-based ≥ 101001 → S2026E0423001
            var seTag = video.SeasonNumber.HasValue && video.EpisodeNumber.HasValue
                ? video.EpisodeNumber.Value <= 9999
                    ? $"S{video.SeasonNumber.Value:D2}E{video.EpisodeNumber.Value:D3}"
                    : $"S{video.SeasonNumber.Value:D2}E{video.EpisodeNumber.Value:D7}"
                : null;

            var episodePart = seTag is not null ? $"{seTag} - {safeTitle}" : safeTitle;
            var fileName = $"{idPrefix} {episodePart}.strm";
            return Path.Combine(libraryBasePath, seriesFolderName, season, fileName);
        }

        /// <summary>
        /// Writes the .strm file at the path returned by <see cref="BuildStrmPath"/>, creating
        /// parent directories as needed. If the video already has a <see cref="VideoRecord.StrmPath"/>
        /// that differs from the computed path, the old file and any empty ancestor directories
        /// are removed first.
        /// Returns the absolute path of the written file.
        /// </summary>
        public string WriteStrm(
            VideoRecord video,
            SourceMode mode,
            string seriesName,
            string libraryBasePath,
            string streamUrl)
        {
            var targetPath = BuildStrmPath(video, mode, seriesName, libraryBasePath);

            // Remove old file/folder if the title (and therefore path) changed.
            // In Movies mode each video owns its folder → delete the whole directory so that
            // Jellyfin metadata files (.nfo, posters) written by a previous scan don't leave
            // a ghost entry in the library after a title change (e.g. a live stream renamed
            // once it goes offline).
            if (!string.IsNullOrEmpty(video.StrmPath)
                && !string.Equals(video.StrmPath, targetPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(video.StrmPath))
            {
                try
                {
                    if (IsDirectVideoFolder(video.StrmPath, libraryBasePath))
                    {
                        var oldDir = Path.GetDirectoryName(Path.GetFullPath(video.StrmPath))!;
                        TryDeleteDirectory(oldDir);
                        LogDeletedOldStrm(video.StrmPath, targetPath);
                    }
                    else
                    {
                        File.Delete(video.StrmPath);
                        LogDeletedOldStrm(video.StrmPath, targetPath);
                        PruneEmptyAncestors(video.StrmPath, libraryBasePath);
                    }
                }
                catch (IOException ex)
                {
                    LogDeleteFailed(video.StrmPath, ex);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, streamUrl, System.Text.Encoding.UTF8);
            LogWroteStrm(targetPath);
            return targetPath;
        }

        /// <summary>
        /// Deletes <paramref name="strmPath"/> (and its parent folder when in Movies mode)
        /// then prunes empty ancestor directories up to (but not including) <paramref name="libraryBasePath"/>.
        /// </summary>
        public void DeleteStrm(string strmPath, string libraryBasePath)
        {
            if (!File.Exists(strmPath))
                return;

            if (IsDirectVideoFolder(strmPath, libraryBasePath))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(strmPath))!;
                TryDeleteDirectory(dir);
                LogDeletedOldStrm(strmPath, "(cleanup)");
                return;
            }

            try
            {
                File.Delete(strmPath);
                LogDeletedOldStrm(strmPath, "(cleanup)");
            }
            catch (IOException ex)
            {
                LogDeleteFailed(strmPath, ex);
                return;
            }

            PruneEmptyAncestors(strmPath, libraryBasePath);
        }

        /// <summary>
        /// Scans <paramref name="libraryBasePath"/> for .strm files not present in
        /// <paramref name="knownPaths"/> and deletes them, then prunes all empty subdirectories.
        /// </summary>
        public void ReconcileLibrary(string libraryBasePath, IReadOnlySet<string> knownPaths)
        {
            if (!Directory.Exists(libraryBasePath))
                return;

            foreach (var file in Directory.EnumerateFiles(libraryBasePath, "*.strm", SearchOption.AllDirectories))
            {
                var normalized = Path.GetFullPath(file);
                if (!knownPaths.Contains(normalized))
                {
                    LogDeletedOrphanFile(normalized);
                    DeleteStrm(normalized, libraryBasePath);
                }
            }

            // Prune any empty subdirectories left behind (e.g. from previous syncs before PruneEmptyAncestors)
            PruneEmptyDirectories(libraryBasePath);
        }

        // Returns true when strmPath's parent directory is a direct child of libraryBasePath.
        // This identifies Movies-mode video folders (each video owns one folder directly under
        // the library root), as opposed to Series-mode where the parent is a Season sub-folder.
        private static bool IsDirectVideoFolder(string strmPath, string libraryBasePath)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryBasePath));
            var dir = Path.GetDirectoryName(Path.GetFullPath(strmPath));
            if (dir is null) return false;
            var parent = Path.GetDirectoryName(dir);
            return parent is not null
                && string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteDirectory(string dirPath)
        {
            try
            {
                if (Directory.Exists(dirPath))
                    Directory.Delete(dirPath, recursive: true);
            }
            catch (IOException) { }
        }

        private static void PruneEmptyDirectories(string libraryBasePath)
        {
            // Process deepest directories first so parent dirs are pruned after their children
            foreach (var dir in Directory.EnumerateDirectories(libraryBasePath, "*", SearchOption.AllDirectories)
                                         .OrderByDescending(static d => d.Length))
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    try { Directory.Delete(dir); }
                    catch (IOException) { }
                }
            }
        }

        private static void PruneEmptyAncestors(string strmPath, string libraryBasePath)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryBasePath));
            var currentDir = Path.GetDirectoryName(Path.GetFullPath(strmPath));

            while (!string.IsNullOrEmpty(currentDir)
                && currentDir.Length > root.Length
                && currentDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                if (Directory.Exists(currentDir) && Directory.GetFileSystemEntries(currentDir).Length == 0)
                {
                    try { Directory.Delete(currentDir); }
                    catch (IOException) { break; }
                }
                else
                {
                    break;
                }
                currentDir = Path.GetDirectoryName(currentDir);
            }
        }

        /// <summary>Truncates <paramref name="s"/> so its UTF-8 encoding is at most <paramref name="maxBytes"/> bytes,
        /// respecting multi-byte character boundaries.</summary>
        private static string TruncateToUtf8Bytes(string s, int maxBytes)
        {
            if (maxBytes <= 0)
                return string.Empty;
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length <= maxBytes)
                return s;
            // Walk back from the cut point past any UTF-8 continuation bytes (0x80–0xBF)
            var end = maxBytes;
            while (end > 0 && (bytes[end] & 0xC0) == 0x80)
                end--;
            return Encoding.UTF8.GetString(bytes, 0, end);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Wrote .strm: {Path}")]
        private partial void LogWroteStrm(string path);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Deleted old .strm {OldPath} → {NewPath}")]
        private partial void LogDeletedOldStrm(string oldPath, string newPath);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Could not delete .strm {Path}")]
        private partial void LogDeleteFailed(string path, Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information,
            Message = "Reconcile: deleting untracked .strm {Path}")]
        private partial void LogDeletedOrphanFile(string path);
    }
}

using System;
using System.IO;

namespace Jellyfin.Plugin.VOD.Imaging
{
    /// <summary>
    /// Writes generated posters to disk and hands back their paths, so an item with no artwork of its
    /// own can still point at a real file.
    /// </summary>
    /// <remarks>
    /// Files land in the plugin's own data folder, next to its database, and are named after the
    /// artwork hash. That keeps the count down to one file per distinct key rather than one per item:
    /// every season of a source with the same key shares a poster, and a poster already on disk is
    /// never rewritten.
    /// </remarks>
    public sealed class ArtworkStore
    {
        private readonly string _directory;

        /// <summary>Initializes a new instance of the <see cref="ArtworkStore"/> class.</summary>
        /// <param name="dataFolderPath">The plugin's data folder.</param>
        public ArtworkStore(string dataFolderPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataFolderPath);
            _directory = Path.Combine(dataFolderPath, "artwork");
        }

        /// <summary>
        /// Returns the path of the poster for <paramref name="key"/>, generating it the first time.
        /// </summary>
        /// <remarks>
        /// The poster is write-once: if a file for the hash already exists it is returned as-is,
        /// even when <paramref name="caption"/> has changed since it was first generated. Captions
        /// are stable (season names and series names come from the sync record), so this is
        /// intentional — artwork sourced from upstream replaces generated posters altogether.
        /// </remarks>
        /// <param name="key">A stable artwork key, e.g. <c>"youtube_ch_arte|S2"</c>.</param>
        /// <param name="caption">Text to draw on the poster, e.g. a season name.</param>
        /// <returns>An absolute path to a PNG file.</returns>
        public string GetOrCreate(string key, string? caption = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            var hash = ArtworkGenerator.HashOf(key);
            var path = Path.Combine(_directory, hash + ".png");

            if (File.Exists(path))
                return path;

            Directory.CreateDirectory(_directory);

            // Written through a per-call temporary file: a sync interrupted mid-write would otherwise
            // leave a truncated PNG that File.Exists happily returns for ever after. The unique suffix
            // prevents two concurrent calls for the same key from sharing the same .tmp path.
            var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, ArtworkGenerator.Generate(hash, caption));
            File.Move(temp, path, overwrite: true);

            return path;
        }
    }
}

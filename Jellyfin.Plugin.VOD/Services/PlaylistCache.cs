using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Short-lived in-memory cache of the playlists served to players.
    /// </summary>
    /// <remarks>
    /// A single playback opens the stream endpoint two to three times (Jellyfin probe + ffmpeg open).
    /// Without a cache, each open re-runs yt-dlp (2–3 s on YouTube). Entries hold signed CDN URLs
    /// that expire upstream, so the default lifetime is short: long enough to collapse the opens of
    /// one playback, far shorter than the CDN signatures.
    /// </remarks>
    public sealed class PlaylistCache
    {
        private static readonly TimeSpan _defaultLifetime = TimeSpan.FromMinutes(5);

        private readonly ConcurrentDictionary<string, Entry> _entries =
            new(StringComparer.Ordinal);

        /// <summary>Returns a cached playlist if one is still fresh.</summary>
        public bool TryGet(string key, out string playlist)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            {
                playlist = entry.Playlist;
                return true;
            }

            playlist = string.Empty;
            return false;
        }

        /// <summary>Stores a playlist, replacing any previous entry for the same key.</summary>
        /// <param name="key">The cache key.</param>
        /// <param name="playlist">The playlist content.</param>
        /// <param name="lifetime">
        /// How long the entry stays fresh. Defaults to 5 minutes. Subtitle tokens should use a
        /// longer lifetime (e.g. 1 hour) since a viewer may enable subtitles mid-playback.
        /// </param>
        public void Set(string key, string playlist, TimeSpan? lifetime = null)
        {
            Prune();
            _entries[key] = new Entry(playlist, DateTime.UtcNow.Add(lifetime ?? _defaultLifetime));
        }

        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in _entries)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    _entries.TryRemove(pair.Key, out _);
                }
            }
        }

        private sealed record Entry(string Playlist, DateTime ExpiresAt);
    }
}

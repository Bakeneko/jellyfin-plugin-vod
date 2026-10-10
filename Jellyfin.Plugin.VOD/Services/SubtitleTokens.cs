using System;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Stands an opaque token in front of an upstream subtitle URI, and resolves it back.
    /// </summary>
    /// <remarks>
    /// YouTube serves subtitles from a <c>timedtext</c> endpoint whose response ffmpeg refuses as a
    /// playlist, so the route proxies it. The token keeps the signed upstream URI out of the
    /// playlist we hand out, and its lifetime is an hour rather than the playlist's five minutes
    /// because a viewer may turn subtitles on long into playback.
    /// <para>
    /// Extracted from the controller because StreamDescriber filters masters too, and
    /// a second rewriting rule would mint tokens the subtitle route cannot resolve.
    /// </para>
    /// </remarks>
    public sealed class SubtitleTokens
    {
        private static readonly TimeSpan _lifetime = TimeSpan.FromHours(1);

        private readonly PlaylistCache _cache;

        /// <summary>Initializes a new instance of the <see cref="SubtitleTokens"/> class.</summary>
        /// <param name="cache">The cache the tokens are stored in.</param>
        public SubtitleTokens(PlaylistCache cache) => _cache = cache;

        /// <summary>Replaces a subtitle URI with a route of ours, or leaves it as it is.</summary>
        /// <param name="upstreamUri">The absolute URI read from the upstream master.</param>
        /// <returns>
        /// A relative subtitle route for a <c>timedtext</c> URI, the input unchanged otherwise.
        /// </returns>
        /// <remarks>
        /// The returned route is relative to <c>/VOD/stream/{id}</c> on purpose: a cached master may
        /// be served to a different caller than the one that built it — the container's ffmpeg and
        /// Jellyfin's own probe reach the plugin on different hosts — so no host may be baked in.
        /// </remarks>
        public string? Rewrite(string upstreamUri)
        {
            ArgumentNullException.ThrowIfNull(upstreamUri);

            if (!upstreamUri.Contains("timedtext", StringComparison.OrdinalIgnoreCase))
            {
                return upstreamUri;
            }

            var token = Guid.NewGuid().ToString("N");
            _cache.Set(KeyFor(token), upstreamUri, _lifetime);
            return $"../subtitles/{token}.m3u8";
        }

        /// <summary>Resolves a token back to the upstream URI it stands for.</summary>
        /// <param name="token">The token read from the route.</param>
        /// <param name="upstreamUri">The upstream URI, or an empty string when unknown or expired.</param>
        /// <returns><see langword="true"/> when the token is still known.</returns>
        public bool TryResolve(string token, out string upstreamUri)
            => _cache.TryGet(KeyFor(token), out upstreamUri);

        private static string KeyFor(string token) => $"sub:{token}";
    }
}

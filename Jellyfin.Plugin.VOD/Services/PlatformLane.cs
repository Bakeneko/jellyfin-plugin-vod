using System;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Which platform's rate limit a yt-dlp call spends, as a stable key.
    /// </summary>
    /// <remarks>
    /// The throttle partitions on the platform, but yt-dlp's <c>ie_key</c> is only known once a
    /// call has returned and a lane has to be chosen before calling. The host is the pre-call
    /// stand-in, and the mapping is deliberately coarse: one lane per rate limit, not one per
    /// source kind. Kept separate from <see cref="Sync.SourceTypeDetector"/> on purpose — that
    /// answers "what kind of source is this", this answers "whose budget am I spending".
    /// </remarks>
    public static class PlatformLane
    {
        /// <summary>The lane a video started by a user resolves on.</summary>
        /// <remarks>
        /// Its own lane so that the throttle exemption for playback is not quietly undone by the
        /// cookie file: a background call holds the cookie copy of ITS lane for the couple of
        /// seconds a yt-dlp process takes, and a started video must not wait for that.
        /// </remarks>
        public const string Playback = "playback";

        /// <summary>The lane an administrator's own action resolves on.</summary>
        /// <remarks>
        /// Checking a URL from the configuration page is one call, made by someone watching a
        /// spinner. Left on its platform's lane it would queue behind a backoff of up to an hour
        /// and the page would simply hang. Exempted for the same reason as playback: the volume —
        /// a handful of calls — is not comparable to the thousands a sync makes.
        /// </remarks>
        public const string Interactive = "interactive";

        /// <summary>The lane that serves everything whose host says nothing.</summary>
        public const string Generic = "generic";

        /// <summary>The lane the given URL belongs to.</summary>
        /// <param name="url">A page URL, or null.</param>
        /// <returns>The lane key — never null, never empty.</returns>
        public static string For(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || string.IsNullOrEmpty(uri.Host))
            {
                return Generic;
            }

            var host = uri.Host.ToLowerInvariant();

            // Matched on the registrable domain, not on a suffix of the host: `EndsWith` would
            // hand `evilyoutube.com` YouTube's budget. Reducing to the last two labels also means
            // subdomains need no special case — music.youtube.com and clips.twitch.tv land on
            // their platform's lane by construction.
            return RegistrableDomain(host) switch
            {
                "youtube.com" or "youtu.be" => "youtube",
                "arte.tv" => "arte",
                "twitch.tv" => "twitch",

                // An unknown platform keeps its FULL host, not its domain: two unrelated sites
                // under one public suffix — anything .co.uk — must not share a budget, and we have
                // no public-suffix list to tell a domain from a suffix.
                _ => host,
            };
        }

        private static string RegistrableDomain(string host)
        {
            var lastDot = host.LastIndexOf('.');
            if (lastDot <= 0)
                return host;

            var previousDot = host.LastIndexOf('.', lastDot - 1);
            return previousDot < 0 ? host : host[(previousDot + 1)..];
        }
    }
}

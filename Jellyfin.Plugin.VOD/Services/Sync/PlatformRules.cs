using System;
using System.Globalization;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// The per-platform rules that have no home in the existing lookup tables: which field holds a
    /// usable title, and where a live broadcast's artwork comes from.
    /// </summary>
    /// <remarks>
    /// yt-dlp reports different shapes per platform, so these rules are platform-specific by nature
    /// rather than by accident. Everything a platform shares with the others — URL recognition, id
    /// prefixes, provider names — stays in SourceTypeDetector, VodNativeId and LibraryPublisher.
    /// </remarks>
    public static class PlatformRules
    {
        /// <summary>
        /// Whether a flat listing from this extractor is informative enough that an entry it says
        /// nothing about can be called gone — without spending a call to find out.
        /// </summary>
        /// <param name="ieKey">The extractor key the listing entry carries.</param>
        /// <returns><see langword="true"/> only for a platform measured to answer this way.</returns>
        /// <remarks>
        /// Measured on 2026-10-12, one platform at a time, because the three disagree on all of it:
        /// <list type="bullet">
        /// <item>
        /// <b>YouTube — true.</b> A dead entry comes back as a template of nulls: no title,
        /// duration, view count, channel, uploader or availability, only id, url, ie_key and
        /// thumbnails. Exactly 10 of 43 entries of the playlist under diagnosis had a title, and
        /// those were exactly the 10 YouTube itself said were watchable ("33 unavailable videos
        /// have been hidden"). So an untitled entry is gone, for free.
        /// </item>
        /// <item>
        /// <b>Arte — false.</b> The flat listing carries only ie_key and url: 0 titles out of 79.
        /// The absence of a title means nothing whatsoever.
        /// </item>
        /// <item>
        /// <b>Twitch — false.</b> All 15 of 15 entries titled. A deleted VOD disappears from the
        /// listing instead, which the orphan sweep already handles.
        /// </item>
        /// <item><b>Anything else — false</b>, the prudent answer.</item>
        /// </list>
        /// <b>Never</b> replace this with a heuristic such as "the listing is informative when at
        /// least one entry has a title": applied to Arte, that declares all 79 videos of every
        /// collection dead. Adding a platform to this list is a measurement, not a deduction.
        /// </remarks>
        public static bool ListingTellsUnavailable(string? ieKey)
            => ieKey is not null
                && ieKey.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="meta"/> was extracted by a Twitch extractor.</summary>
        /// <param name="meta">The metadata yt-dlp returned.</param>
        /// <returns><see langword="true"/> for <c>TwitchStream</c> and <c>TwitchVod</c>.</returns>
        /// <remarks>
        /// Used by <c>SyncService</c> to bypass Arte's title resolution for every Twitch video,
        /// including a <c>TwitchVod</c> that carries no <c>fulltitle</c> — for which value equality
        /// on the title would accidentally allow Arte's logic to run.
        /// </remarks>
        public static bool IsTwitchExtractor(VideoMetadata meta)
        {
            ArgumentNullException.ThrowIfNull(meta);

            return string.Equals(meta.ExtractorKey, "TwitchStream", StringComparison.OrdinalIgnoreCase)
                || string.Equals(meta.ExtractorKey, "TwitchVod", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The title to store for a video.</summary>
        /// <param name="meta">The metadata yt-dlp returned.</param>
        /// <returns>The title, or null when the metadata carries none.</returns>
        /// <remarks>
        /// Twitch needs two exceptions, both measured 2026-10-03 (spec F15): yt-dlp appends the
        /// broadcast date to a live VOD's <c>title</c>, so <c>fulltitle</c> is used instead and the
        /// item is not renamed when the broadcast ends; and a channel stream's <c>title</c> is
        /// generic ("otplol_ (live) …") while the stream's real name sits in <c>description</c>.
        /// </remarks>
        public static string? TitleFor(VideoMetadata meta)
        {
            ArgumentNullException.ThrowIfNull(meta);

            if (string.Equals(meta.ExtractorKey, "TwitchStream", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(meta.Description) ? meta.Description : meta.Title;

            if (string.Equals(meta.ExtractorKey, "TwitchVod", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(meta.FullTitle) ? meta.FullTitle : meta.Title;

            return meta.Title;
        }

        /// <summary>Whether a source of this kind can be polled for a broadcast in progress.</summary>
        /// <param name="sourceUrl">The configured source URL.</param>
        /// <returns><see langword="true"/> when polling it can find a live.</returns>
        /// <remarks>
        /// Arte and france.tv list no broadcast, so polling them would spend a yt-dlp call per pass
        /// to learn nothing. The same holds for a YouTube channel's other tabs and for a playlist: a
        /// channel broadcasts on /streams, and polling the rest cost about 1 400 calls a day for the
        /// user's five /videos channels (measured 2026-10-08) — the kind of volume that brings
        /// YouTube's bot check down on the sync as well.
        /// </remarks>
        public static bool SupportsLiveProbe(string sourceUrl)
            => SourceTypeDetector.Detect(sourceUrl) switch
            {
                SourceType.TwitchChannelLive or SourceType.TwitchVideos => true,
                SourceType.YoutubeChannel => IsStreamsTab(sourceUrl),
                _ => false
            };

        /// <summary>The artwork URL to store, falling back to the platform's live preview.</summary>
        /// <param name="meta">The metadata yt-dlp returned.</param>
        /// <param name="sourceUrl">The source the video belongs to, which names the channel.</param>
        /// <returns>A URL, or null when neither the metadata nor the platform offers one.</returns>
        /// <remarks>
        /// An in-progress Twitch VOD carries no real thumbnail: measured empty on 2026-10-03 (spec
        /// F16), and on 2026-10-08 a grey "processing" placeholder served from Twitch's /_404/ path —
        /// which is most likely what the "present" thumbnail of 2026-10-04 was. Twitch publishes a
        /// live preview per channel and refreshes it during the broadcast, so it stands in whenever
        /// a LIVE video's metadata has nothing better.
        /// <para>
        /// Only while live: once the broadcast ends Twitch serves its offline banner on that URL, so
        /// a finished archive with no thumbnail would show a picture of an offline channel for ever.
        /// </para>
        /// </remarks>
        public static string? LiveThumbnailFor(VideoMetadata meta, string sourceUrl)
        {
            ArgumentNullException.ThrowIfNull(meta);

            if (!string.IsNullOrWhiteSpace(meta.Thumbnail) && !IsTwitchPlaceholder(meta.Thumbnail))
                return meta.Thumbnail;

            if (meta.LiveStatus != LiveStatus.Live)
                return null;

            return TwitchUrls.ChannelFrom(sourceUrl) is { } channel
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{channel}-1280x720.jpg")
                : null;
        }

        private static bool IsStreamsTab(string sourceUrl)
            => Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
               && uri.AbsolutePath.TrimEnd('/').EndsWith("/streams", StringComparison.OrdinalIgnoreCase);

        private static bool IsTwitchPlaceholder(string thumbnail)
            => Uri.TryCreate(thumbnail, UriKind.Absolute, out var uri)
               && TwitchUrls.IsTwitchHost(uri.Host)
               && uri.AbsolutePath.StartsWith("/_404/", StringComparison.OrdinalIgnoreCase);
    }
}

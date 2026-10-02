using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// One item of a video's version group: the primary when <see cref="Tier"/> is null, an
    /// alternate otherwise.
    /// </summary>
    /// <param name="Tier">The tier this member serves, or null for the primary.</param>
    /// <param name="ItemKey">The string fed to GetNewItemId, hashed into the item's Guid.</param>
    /// <param name="Name">What the "Version" menu shows, because version names come from item.Name.</param>
    /// <param name="Path">The stream URL, which alone determines what is actually served.</param>
    internal sealed record VersionMember(Tier? Tier, string ItemKey, string Name, string Path);

    /// <summary>
    /// The plan of record for one video's items: what exists, how each is identified, what it is
    /// called and what it serves.
    /// </summary>
    /// <remarks>
    /// Both the publisher and MediaStreamWriter derive from this, which is what keeps a member's
    /// name, path and id key from drifting apart between the two.
    /// </remarks>
    internal static class VersionGroup
    {
        /// <summary>
        /// The id key for one member. The tier must reach the key: two items cannot share a Guid,
        /// and the key is the only input GetNewItemId takes besides the type.
        /// </summary>
        /// <param name="videoUuid">The video's opaque VOD uuid.</param>
        /// <param name="tier">The tier, or null for the primary.</param>
        /// <returns>The id key.</returns>
        /// <remarks>
        /// The primary's key is deliberately unchanged from before version groups existed. Keeping it
        /// is what stops already-published items from being recreated under new ids, which would
        /// discard their watch state.
        /// </remarks>
        internal static string KeyFor(string videoUuid, Tier? tier)
            => tier is null
                ? $"vod:{videoUuid}"
                : string.Create(
                    CultureInfo.InvariantCulture, $"vod:{videoUuid}:{tier.Height}:{tier.Codec}");

        /// <summary>The stream URL for one member.</summary>
        /// <param name="localBaseUrl">The configured base URL Jellyfin reaches this plugin on.</param>
        /// <param name="videoUuid">The video's opaque VOD uuid.</param>
        /// <param name="tier">The tier, or null for the primary.</param>
        /// <returns>An absolute URL.</returns>
        /// <remarks>
        /// The primary's URL carries no parameters on purpose: the route then applies its own
        /// default rule, which is what makes the primary the "auto" entry of the menu.
        /// </remarks>
        internal static string PathFor(string localBaseUrl, string videoUuid, Tier? tier)
        {
            var root = $"{localBaseUrl.TrimEnd('/')}/VOD/stream/{videoUuid}.m3u8";
            return tier is null
                ? root
                : string.Create(
                    CultureInfo.InvariantCulture, $"{root}?height={tier.Height}&codec={tier.Codec}");
        }

        /// <summary>The name one alternate carries.</summary>
        /// <param name="tierLabel">The tier's own label, such as "1080p".</param>
        /// <param name="videoTitle">The video's title, which the primary carries unchanged.</param>
        /// <returns>The alternate's name.</returns>
        /// <remarks>
        /// This name is the only string Jellyfin has for an alternate. The version menu falls back to
        /// <c>item.Name</c> here because <c>BaseItem.GetMediaSourceName</c> derives a short label from
        /// the file name, which needs <c>IsFileProtocol</c> — and these items are served over HTTP. The
        /// playback history shows the same name. So one string serves both surfaces, and a bare
        /// "1080p" reads as a title wherever an alternate appears outside the menu. The tier leads so
        /// the discriminating token stays visible when the UI truncates.
        /// </remarks>
        internal static string NameFor(string tierLabel, string? videoTitle)
            => string.IsNullOrEmpty(videoTitle) ? tierLabel : $"[{tierLabel}] {videoTitle}";

        /// <summary>Reads a tier back out of a stored path.</summary>
        /// <param name="path">An item's stored path, possibly null.</param>
        /// <returns>The height and codec, both null when the path carries no tier.</returns>
        /// <remarks>
        /// Only the query parameters are read. The prefix comes from LocalBaseUrl, which an
        /// administrator may change between two syncs, so a stored path's host and port can be stale
        /// while its tier is still exactly right.
        /// </remarks>
        internal static (int? Height, string? Codec) TierOf(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return (null, null);
            }

            var query = path.IndexOf('?', StringComparison.Ordinal);
            if (query < 0)
            {
                return (null, null);
            }

            int? height = null;
            string? codec = null;

            foreach (var pair in path[(query + 1)..].Split('&'))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    continue;
                }

                var name = pair[..equals];
                var value = pair[(equals + 1)..];

                if (string.Equals(name, "height", StringComparison.Ordinal)
                    && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                {
                    height = parsed;
                }
                else if (string.Equals(name, "codec", StringComparison.Ordinal) && value.Length > 0)
                {
                    codec = value;
                }
            }

            // Half a selection identifies nothing: the route rejects it, so a path carrying one is
            // not a tier item.
            return height.HasValue && codec is not null ? (height, codec) : (null, null);
        }

        /// <summary>
        /// Every item this video should have: the primary first, then one alternate per offered
        /// tier in ascending height.
        /// </summary>
        /// <param name="video">The stored video record.</param>
        /// <param name="localBaseUrl">The configured base URL Jellyfin reaches this plugin on.</param>
        /// <returns>One to three members, the primary at index 0.</returns>
        internal static IReadOnlyList<VersionMember> For(VideoRecord video, string localBaseUrl)
        {
            ArgumentNullException.ThrowIfNull(video);

            var members = new List<VersionMember>
            {
                new(null, KeyFor(video.Id, null), video.Title ?? string.Empty,
                    PathFor(localBaseUrl, video.Id, null))
            };

            foreach (var tier in QualityLadder.OfferedTiers(video.Extras.Qualities))
            {
                members.Add(new VersionMember(
                    tier,
                    KeyFor(video.Id, tier),
                    NameFor(QualityLadder.LabelFor(tier), video.Title),
                    PathFor(localBaseUrl, video.Id, tier)));
            }

            return members;
        }
    }
}

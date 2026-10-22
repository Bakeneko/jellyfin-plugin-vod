using System;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// Applies a yt-dlp metadata answer to a row. The one place the field mapping lives, shared by
    /// the wave and by the live probe so neither grows its own copy.
    /// </summary>
    public static class MetadataFill
    {
        /// <summary>Writes a metadata answer onto a row.</summary>
        /// <param name="record">The row, created or loaded.</param>
        /// <param name="meta">The answer.</param>
        /// <param name="sourceUrl">The owning source's URL, which some platform rules need.</param>
        /// <param name="publishedTitle">The title to publish, already chosen by the caller.</param>
        /// <param name="now">The reference time.</param>
        /// <remarks>
        /// Does NOT write <see cref="VideoRecord.ListingTitle"/>: that comes from a listing, never
        /// from a metadata answer, because the two paths do not serve the same language and mixing
        /// them would make the retitle detector fire on every pass.
        /// <para>
        /// Does NOT write the season or the episode either. Numbering belongs to the checkup, which
        /// recomputes a whole day at once; a number written here would be computed from one row in
        /// isolation.
        /// </para>
        /// </remarks>
        public static void Apply(
            VideoRecord record,
            VideoMetadata meta,
            string sourceUrl,
            string publishedTitle,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(meta);

            record.Title = publishedTitle;
            record.Description = meta.Description;
            record.ThumbnailUrl = PlatformRules.LiveThumbnailFor(meta, sourceUrl);
            record.Extras = record.Extras with
            {
                Version = VideoExtras.CurrentVersion,
                Tags = meta.Tags,
                Categories = meta.Categories,
                Channels = meta.Channels,
                SubtitleLanguages = meta.SubtitleLanguages,
                Qualities = meta.Qualities,
                AudioTracks = meta.AudioTracks,
            };
            record.DurationSeconds = meta.Duration.HasValue ? (int)meta.Duration.Value : null;
            record.LiveStatus = meta.LiveStatus;
            record.PublishedAt = PublishedAtFor(meta, ParseDate(meta), record.PublishedAt, now);

            // A platform-announced number is written here and nowhere else. The checkup recognises
            // it later by its shape — it does not decode as a date-based number — because by then
            // the metadata answer is gone. See DateNumbering.Supplied.
            if (meta.SeasonNumber.HasValue && meta.EpisodeNumber.HasValue)
            {
                record.SeasonNumber = meta.SeasonNumber.Value;
                record.EpisodeNumber = meta.EpisodeNumber.Value;
            }
        }

        /// <summary>The publication date to store.</summary>
        /// <param name="meta">The answer.</param>
        /// <param name="parsed">What <see cref="ParseDate"/> read from it.</param>
        /// <param name="stored">The date already on the row, or null.</param>
        /// <param name="now">The reference time, used when nothing else answers.</param>
        /// <returns>The date to store.</returns>
        /// <remarks>
        /// A scheduled stream's date is its airing time, read from <c>release_timestamp</c> and
        /// rewritten on every fetch because a schedule can move. Two reasons it cannot come from
        /// the normal path: <see cref="ParseDate"/> prefers <c>upload_date</c>, which for a
        /// scheduled stream is the day it was created, and a stored date otherwise wins, which
        /// would pin a stale schedule.
        /// </remarks>
        internal static DateTimeOffset? PublishedAtFor(
            VideoMetadata meta, DateTimeOffset? parsed, DateTimeOffset? stored, DateTimeOffset now)
        {
            if (meta.LiveStatus == LiveStatus.Upcoming && meta.ReleaseTimestamp.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(meta.ReleaseTimestamp.Value);

            return stored ?? parsed ?? now;
        }

        /// <summary>The publication date a metadata answer carries, or null.</summary>
        /// <param name="meta">The answer.</param>
        /// <returns>The date, or null when the answer carries none.</returns>
        internal static DateTimeOffset? ParseDate(VideoMetadata meta)
        {
            ArgumentNullException.ThrowIfNull(meta);

            if (meta.UploadDate is { Length: 8 } ud
                && int.TryParse(ud[..4], out var year)
                && int.TryParse(ud[4..6], out var month)
                && int.TryParse(ud[6..8], out var day))
            {
                try
                {
                    return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Not a real calendar date; fall through to the timestamps below.
                }
            }

            if (meta.ReleaseTimestamp.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(meta.ReleaseTimestamp.Value);

            if (meta.Timestamp.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(meta.Timestamp.Value);

            return null;
        }
    }
}

using System.Collections.Generic;

namespace Jellyfin.Plugin.VOD.Data.Models
{
    /// <summary>
    /// The per-video payload that needs no column of its own: fields that never appear in a query
    /// predicate, an ORDER BY, or a targeted UPDATE. Serialised into videos.extras.
    /// </summary>
    public sealed record VideoExtras
    {
        /// <summary>The shape every payload written today carries.</summary>
        public const int CurrentVersion = 1;

        /// <summary>
        /// What a row that has never been given a payload reads back as: the column is NULL, or it
        /// holds something no longer parseable. Distinct from an empty payload, which is a real
        /// answer — the platform offered nothing — and must not be fetched again.
        /// </summary>
        public static VideoExtras NoPayload { get; } = new() { Version = 0 };

        /// <summary>
        /// Payload shape version, so it can evolve without a schema migration. Independent of the
        /// schema's PRAGMA user_version, which is 5 while this is 1. Zero is reserved for
        /// <see cref="NoPayload"/> and is never persisted.
        /// </summary>
        public int Version { get; init; } = CurrentVersion;

        /// <summary>The video qualities the platform offers, ascending by height.</summary>
        public IReadOnlyList<VideoQuality> Qualities { get; init; } = [];

        /// <summary>The audio tracks the platform offers, in yt-dlp's reporting order.</summary>
        public IReadOnlyList<AudioTrack> AudioTracks { get; init; } = [];

        /// <summary>Subtitle language codes from yt-dlp's subtitles dictionary, not auto-captions.</summary>
        public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];

        /// <summary>Free-text tags, published as Jellyfin tags. Capped at VideoMetadata.MaxTags.</summary>
        public IReadOnlyList<string> Tags { get; init; } = [];

        /// <summary>Platform categories, published as Jellyfin genres.</summary>
        public IReadOnlyList<string> Categories { get; init; } = [];

        /// <summary>Owning channels, published as Jellyfin studios, qualified at publish time.</summary>
        public IReadOnlyList<string> Channels { get; init; } = [];
    }

    /// <summary>One video quality the platform offers, as the HLS master will carry it.</summary>
    /// <param name="Height">Pixel height, e.g. 1080. Serves as the tier key.</param>
    /// <param name="Codec">Normalised codec ("h264", "vp9", "hevc"). Decides the transcoding risk.</param>
    /// <param name="Fps">Frames per second when reported. Distinguishes 720p from 720p60 on Twitch.</param>
    public sealed record VideoQuality(int Height, string Codec, int? Fps);

    /// <summary>One audio track the platform offers.</summary>
    /// <param name="Language">ISO code, e.g. "fr". NOT unique: Arte ships two fr tracks.</param>
    /// <param name="Label">
    /// yt-dlp's format_id, the only field distinguishing same-language tracks. Mangled
    /// ("français__confort_audio_"): a seed, replaced by the master's real NAME at first play.
    /// </param>
    public sealed record AudioTrack(string? Language, string? Label);
}

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>What a metadata call concluded, beyond the metadata itself.</summary>
    public enum MetadataOutcome
    {
        /// <summary>yt-dlp returned metadata.</summary>
        Succeeded,

        /// <summary>
        /// yt-dlp ran, read the channel, and reported that it is not broadcasting. There is no
        /// metadata for a channel that is off air, so this is an answer rather than a failure —
        /// the one non-zero exit the caller is allowed to draw a conclusion from.
        /// </summary>
        ChannelOffAir,

        /// <summary>
        /// The call produced no answer: a rate limit, a network blip, a broken extractor,
        /// unparseable output. The caller must change nothing, because every one of these looks
        /// alike from here and "nothing live" would retract a broadcast that is still running.
        /// </summary>
        Failed
    }

    /// <summary>
    /// The result of a metadata call that may legitimately come back without metadata.
    /// </summary>
    /// <param name="Outcome">What the call concluded.</param>
    /// <param name="Metadata">The metadata, present only when the call succeeded.</param>
    public sealed record MetadataResult(MetadataOutcome Outcome, VideoMetadata? Metadata)
    {
        /// <summary>A call that produced no answer.</summary>
        public static MetadataResult Failed { get; } = new(MetadataOutcome.Failed, null);

        /// <summary>A call that reported the channel is not broadcasting.</summary>
        public static MetadataResult ChannelOffAir { get; } = new(MetadataOutcome.ChannelOffAir, null);

        /// <summary>A call that returned metadata.</summary>
        /// <param name="metadata">The metadata yt-dlp returned.</param>
        /// <returns>A succeeded result carrying <paramref name="metadata"/>.</returns>
        public static MetadataResult From(VideoMetadata metadata)
            => new(MetadataOutcome.Succeeded, metadata);
    }
}

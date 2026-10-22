using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>
    /// What one configured source last did, as the configuration table shows it.
    /// </summary>
    /// <param name="Url">The source URL, which is also its key in the database.</param>
    /// <param name="Name">The name the series was published under, when a listing got that far.</param>
    /// <param name="ShortListedAt">When the last short listing succeeded, or null.</param>
    /// <param name="FullListedAt">
    /// When the last full listing succeeded, or null. Null means no listing has ever run.
    /// </param>
    /// <param name="VideoCount">How many videos the database currently holds for this source.</param>
    /// <param name="ListingCount">
    /// The last <c>playlist_count</c> the platform reported, where it reports one at all.
    /// </param>
    /// <param name="ShortListingResult">The result of the last short listing.</param>
    /// <param name="ShortListingDetail">Why the last short listing ended as it did.</param>
    /// <param name="ShortListingFailures">Consecutive short-listing failures.</param>
    /// <param name="FullListingResult">The result of the last full listing.</param>
    /// <param name="FullListingDetail">Why the last full listing ended as it did.</param>
    /// <param name="FullListingFailures">Consecutive full-listing failures.</param>
    /// <param name="CleanupResult">The result of the last cleanup.</param>
    /// <param name="CleanupDetail">Why the last cleanup ended as it did.</param>
    /// <param name="CleanupFailures">Consecutive cleanup failures.</param>
    /// <param name="WaveResult">The result of the last wave.</param>
    /// <param name="WaveDetail">Why the last wave ended as it did.</param>
    /// <param name="WaveFailures">Consecutive wave failures.</param>
    /// <param name="CheckupResult">The result of the last checkup.</param>
    /// <param name="CheckupDetail">Why the last checkup ended as it did.</param>
    /// <param name="CheckupFailures">Consecutive checkup failures.</param>
    public sealed record SourceStatus(
        string Url,
        string? Name,
        DateTimeOffset? ShortListedAt,
        DateTimeOffset? FullListedAt,
        int VideoCount,
        int? ListingCount,
        CallOutcome ShortListingResult,
        string? ShortListingDetail,
        int ShortListingFailures,
        CallOutcome FullListingResult,
        string? FullListingDetail,
        int FullListingFailures,
        CallOutcome CleanupResult,
        string? CleanupDetail,
        int CleanupFailures,
        CallOutcome WaveResult,
        string? WaveDetail,
        int WaveFailures,
        CallOutcome CheckupResult,
        string? CheckupDetail,
        int CheckupFailures);

    /// <summary>
    /// What happened to one video, as the source detail page shows it.
    /// </summary>
    /// <param name="Id">The internal uuid (primary key and Jellyfin provider id).</param>
    /// <param name="Title">The published title, or the raw title when not yet published.</param>
    /// <param name="VideoUrl">The video page URL.</param>
    /// <param name="LiveStatus">Broadcasting, scheduled, or neither.</param>
    /// <param name="PublishedAt">The publication date, or null when not yet known.</param>
    /// <param name="SeasonNumber">The published season number, when set.</param>
    /// <param name="EpisodeNumber">The published episode number, when set.</param>
    /// <param name="MetadataResult">How the last metadata fetch ended.</param>
    /// <param name="MetadataDetail">Why it ended that way, in plain text.</param>
    /// <param name="MetadataAttempts">Consecutive metadata failures.</param>
    /// <param name="MetadataAt">When the metadata fetch last ran.</param>
    /// <param name="DescribeResult">How the last stream description ended.</param>
    /// <param name="DescribeDetail">Why it ended that way, in plain text.</param>
    /// <param name="DescribeAttempts">Consecutive description failures.</param>
    /// <param name="DescribedAt">When the stream description last ran.</param>
    public sealed record VideoStatus(
        string Id,
        string? Title,
        string VideoUrl,
        LiveStatus LiveStatus,
        DateTimeOffset? PublishedAt,
        int? SeasonNumber,
        int? EpisodeNumber,
        CallOutcome MetadataResult,
        string? MetadataDetail,
        int MetadataAttempts,
        DateTimeOffset? MetadataAt,
        CallOutcome DescribeResult,
        string? DescribeDetail,
        int DescribeAttempts,
        DateTimeOffset? DescribedAt);

    /// <summary>
    /// One source's videos, ordered and capped for the detail panel.
    /// </summary>
    /// <param name="TotalVideos">
    /// How many videos the source has in all, so the page can say what it is not showing.
    /// </param>
    /// <param name="Videos">
    /// The videos that need attention first, then the most recent. The panel answers "why did
    /// today's video not appear", and the database's own order — oldest publication date first,
    /// nulls ahead of those — buries the answer at the bottom of a channel. Capped, because a
    /// source here can hold several thousand videos and the panel renders every row it is given.
    /// </param>
    public sealed record SourceDetail(
        int TotalVideos,
        IReadOnlyList<VideoStatus> Videos);

    /// <summary>
    /// What one configured library is worth right now, as the configuration page shows it.
    /// </summary>
    /// <param name="Role">Which setting this row describes: <c>movies</c> or <c>series</c>.</param>
    /// <param name="Name">The configured name, blank included — <paramref name="Error"/> then says why that is a problem.</param>
    /// <param name="Needed">
    /// Whether at least one ENABLED source publishes into this library. False means a problem here
    /// stops nothing, so the page can keep quiet about it.
    /// </param>
    /// <param name="Error">
    /// Why nothing can be published here: no name, no library by that name, no location, not indexed.
    /// Non-null together with <paramref name="Needed"/> means the next pass fails entirely.
    /// </param>
    /// <param name="Warning">
    /// The library's collection type does not fit this role. Blocks nothing — it warns that the
    /// items will display badly.
    /// </param>
    public sealed record LibraryStatus(
        string Role,
        string Name,
        bool Needed,
        string? Error,
        string? Warning);

    /// <summary>
    /// The sync health of the whole plugin, returned by <c>GET /VOD/sources/status</c>.
    /// </summary>
    /// <param name="Libraries">One row per library role, so the page can say WHICH one is broken.</param>
    /// <param name="Sources">One row per source the database knows about.</param>
    /// <param name="Collisions">
    /// Sets of configured sources deriving one key, which would silently share a series. Empty in
    /// a healthy configuration.
    /// </param>
    /// <param name="Warnings">
    /// Configuration warnings that are silent by construction — situations where the plugin
    /// runs without complaint but nothing useful happens. Each entry is a human-readable message
    /// the configuration page should surface prominently.
    /// </param>
    public sealed record SourcesStatus(
        IReadOnlyList<LibraryStatus> Libraries,
        IReadOnlyList<SourceStatus> Sources,
        IReadOnlyList<SourceKeyCollision> Collisions,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// One library the configuration page may offer, with what it is allowed to hold.
    /// </summary>
    /// <param name="Name">The library name, as it will be stored in the configuration.</param>
    /// <param name="CollectionType">
    /// The collection type's name (<c>movies</c>, <c>tvshows</c>, <c>mixed</c>…) or null for a mixed
    /// library. Displayed, never interpreted by the page.
    /// </param>
    /// <param name="AcceptsMovies">Whether this library may be picked as the movies target.</param>
    /// <param name="AcceptsSeries">Whether this library may be picked as the series target.</param>
    public sealed record LibraryOption(
        string Name,
        string? CollectionType,
        bool AcceptsMovies,
        bool AcceptsSeries);

    /// <summary>
    /// Several configured sources that derive the same key, and therefore share one series.
    /// </summary>
    /// <remarks>
    /// Reported to the configuration page because the failure is otherwise invisible: the sources
    /// rename the shared series and un-claim each other's episodes on every publication, without
    /// an error anywhere. Diagnosed in production on 2026-10-19 after 597 episodes were found
    /// filed under the wrong series.
    /// </remarks>
    /// <param name="Key">The derived key the sources share.</param>
    /// <param name="Urls">The configured URLs that derive it, in configuration order.</param>
    public sealed record SourceKeyCollision(string Key, IReadOnlyList<string> Urls);
}

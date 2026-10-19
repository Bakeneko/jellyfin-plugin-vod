using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.VOD.Services.Sync.Dto
{
    /// <summary>
    /// What one configured source last did, as the configuration table shows it.
    /// </summary>
    /// <param name="Url">The source URL, which is also its key in the database.</param>
    /// <param name="Name">The name the series was published under, when a sync got that far.</param>
    /// <param name="LastSyncedAt">When the source last completed a sync, or null if it never has.</param>
    /// <param name="LastError">
    /// Why the last attempt failed, or null when the last attempt succeeded. The database has
    /// recorded this since the sync layer shipped; this is what finally reads it back.
    /// </param>
    /// <param name="VideoCount">How many videos the database currently holds for this source.</param>
    /// <param name="Resolved">Videos resolved.</param>
    /// <param name="Pending">Videos enumerated and not yet resolved.</param>
    /// <param name="Failed">Videos whose last resolution failed.</param>
    /// <param name="Unavailable">Videos the platform says are gone.</param>
    /// <param name="Settled">
    /// Whether the source may number and publish its new videos now; false while a video is
    /// pending, or failed fewer than three times.
    /// </param>
    /// <param name="LastResolutionError">The latest resolution failure, or null.</param>
    public sealed record SourceStatus(
        string Url,
        string? Name,
        DateTimeOffset? LastSyncedAt,
        string? LastError,
        int VideoCount,
        int Resolved,
        int Pending,
        int Failed,
        int Unavailable,
        bool Settled,
        string? LastResolutionError);

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
    public sealed record SourcesStatus(
        IReadOnlyList<LibraryStatus> Libraries,
        IReadOnlyList<SourceStatus> Sources,
        IReadOnlyList<SourceKeyCollision> Collisions);

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

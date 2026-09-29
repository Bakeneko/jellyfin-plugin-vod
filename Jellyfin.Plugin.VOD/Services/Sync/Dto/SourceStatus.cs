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
    public sealed record SourceStatus(
        string Url,
        string? Name,
        DateTimeOffset? LastSyncedAt,
        string? LastError,
        int VideoCount);

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
    public sealed record SourcesStatus(
        IReadOnlyList<LibraryStatus> Libraries,
        IReadOnlyList<SourceStatus> Sources);

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
}

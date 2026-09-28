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
    /// The sync health of the whole plugin, returned by <c>GET /VOD/sources/status</c>.
    /// </summary>
    /// <param name="LibraryName">The library the plugin is configured to publish into.</param>
    /// <param name="LibraryError">
    /// Why that library cannot be used right now, or null when it resolves. Non-null means every
    /// source will fail on the next run, whatever their own rows say.
    /// </param>
    /// <param name="Sources">One row per source the database knows about.</param>
    public sealed record SourcesStatus(
        string LibraryName,
        string? LibraryError,
        IReadOnlyList<SourceStatus> Sources);
}

using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// A resolved publication target: the library the plugin was pointed at, the folder items hang
    /// off, and what that library is typed as.
    /// </summary>
    /// <param name="LibraryName">
    /// The configured name that resolved this target. Carried so errors and logs can say WHICH of the
    /// two libraries is at fault.
    /// </param>
    /// <param name="Anchor">The library's physical folder — what new items are added to.</param>
    /// <param name="CollectionType">
    /// The library's collection type as Jellyfin exposes it, or null for a mixed library. Sole input
    /// of <see cref="IsMixed"/>.
    /// </param>
    public sealed record LibraryTarget(
        string LibraryName,
        Folder Anchor,
        CollectionTypeOptions? CollectionType)
    {
        /// <summary>
        /// Gets a value indicating whether this library holds movies and series together, and
        /// therefore defaults to the "Folders" view.
        /// </summary>
        /// <remarks>
        /// The only thing the date-added option needs: the problem it works around exists only in a
        /// mixed library. Mixedness is a property of the collection type, never of two configured
        /// names happening to be equal.
        /// </remarks>
        public bool IsMixed => CollectionType is null or CollectionTypeOptions.mixed;
    }
}

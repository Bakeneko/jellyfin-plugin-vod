using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Which Jellyfin collection types can hold movies, and which can hold series.
    /// </summary>
    /// <remarks>
    /// One definition, shared by the library list the configuration page offers and by the warning
    /// the status route reports. Jellyfin reads a library's type from a <c>&lt;type&gt;.collection</c>
    /// marker file inside its user-view folder (<c>LibraryManager.GetCollectionType</c>); a mixed
    /// library has no marker at all, so its type is <c>null</c> rather than
    /// <see cref="CollectionTypeOptions.mixed"/>. The enum member exists and is accepted here for
    /// safety, but it is not what a mixed library records in practice.
    /// </remarks>
    internal static class LibraryTypeRules
    {
        /// <summary>Returns whether a library of this type can hold published movies.</summary>
        internal static bool AcceptsMovies(CollectionTypeOptions? type)
            => type is null or CollectionTypeOptions.movies or CollectionTypeOptions.mixed;

        /// <summary>Returns whether a library of this type can hold published series.</summary>
        internal static bool AcceptsSeries(CollectionTypeOptions? type)
            => type is null or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed;
    }
}

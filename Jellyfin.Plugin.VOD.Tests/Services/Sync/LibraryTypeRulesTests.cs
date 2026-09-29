using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// A library with no marker file has CollectionType null, which is how Jellyfin records a mixed
    /// library — measured on a live instance, where Films carries movies.collection, Séries carries
    /// tvshows.collection, and the mixed VODs carries none. Both roles must therefore accept null.
    /// </summary>
    public sealed class LibraryTypeRulesTests
    {
        [Theory]
        [InlineData(null, true)]
        [InlineData(CollectionTypeOptions.mixed, true)]
        [InlineData(CollectionTypeOptions.movies, true)]
        [InlineData(CollectionTypeOptions.tvshows, false)]
        [InlineData(CollectionTypeOptions.music, false)]
        public void AcceptsMovies_FollowsTheCollectionType(CollectionTypeOptions? type, bool expected)
            => Assert.Equal(expected, LibraryTypeRules.AcceptsMovies(type));

        [Theory]
        [InlineData(null, true)]
        [InlineData(CollectionTypeOptions.mixed, true)]
        [InlineData(CollectionTypeOptions.tvshows, true)]
        [InlineData(CollectionTypeOptions.movies, false)]
        [InlineData(CollectionTypeOptions.books, false)]
        public void AcceptsSeries_FollowsTheCollectionType(CollectionTypeOptions? type, bool expected)
            => Assert.Equal(expected, LibraryTypeRules.AcceptsSeries(type));

        [Fact]
        public void OnlyAMixedLibraryAcceptsBoth()
        {
            // The invariant the configuration page relies on: a library can appear in BOTH selectors
            // only when it is mixed. That is what makes "one target for both modes" mechanically
            // imply "that target is mixed", with no inference from the names being equal.
            Assert.True(LibraryTypeRules.AcceptsMovies(null) && LibraryTypeRules.AcceptsSeries(null));
            Assert.True(LibraryTypeRules.AcceptsMovies(CollectionTypeOptions.mixed)
                        && LibraryTypeRules.AcceptsSeries(CollectionTypeOptions.mixed));
            Assert.False(LibraryTypeRules.AcceptsMovies(CollectionTypeOptions.tvshows));
            Assert.False(LibraryTypeRules.AcceptsSeries(CollectionTypeOptions.movies));
        }
    }
}

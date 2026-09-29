using System.Linq;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    /// <summary>
    /// Which library a source publishes into, and which libraries a pass must prepare. Shared by
    /// the sync service and the status route, so the two cannot drift apart.
    /// </summary>
    public sealed class LibraryRoutingTests
    {
        private static PluginConfiguration Config(params (SourceMode Mode, bool Enabled)[] sources)
        {
            var config = new PluginConfiguration
            {
                MoviesLibraryName = "Films",
                SeriesLibraryName = "Séries"
            };

            foreach (var (mode, enabled) in sources)
            {
                config.Sources.Add(new SourceDefinition
                {
                    Url = $"https://example.test/{mode}/{config.Sources.Count}",
                    Mode = mode,
                    Enabled = enabled
                });
            }

            return config;
        }

        [Fact]
        public void NameFor_RoutesByMode()
        {
            var config = Config();
            Assert.Equal("Films", LibraryRouting.NameFor(config, SourceMode.Movies));
            Assert.Equal("Séries", LibraryRouting.NameFor(config, SourceMode.Series));
        }

        [Fact]
        public void IsNeeded_IgnoresDisabledSources()
        {
            // A disabled source is paused, not removed: it must not drag its library into the
            // preparation, because a pass that needs nothing from it must not fail on it.
            var config = Config((SourceMode.Movies, false), (SourceMode.Series, true));
            Assert.False(LibraryRouting.IsNeeded(config, SourceMode.Movies));
            Assert.True(LibraryRouting.IsNeeded(config, SourceMode.Series));
        }

        [Fact]
        public void NeededLibraries_SkipsALibraryNoEnabledSourceTargets()
        {
            var config = Config((SourceMode.Series, true));
            var needed = LibraryRouting.NeededLibraries(config);

            var only = Assert.Single(needed);
            Assert.Equal(LibraryRouting.SeriesRole, only.Role);
            Assert.Equal("Séries", only.Name);
        }

        [Fact]
        public void NeededLibraries_CollapsesOneLibraryUsedByBothModes()
        {
            // The mixed setup: both roles point at the same library, so it is prepared once.
            var config = Config((SourceMode.Movies, true), (SourceMode.Series, true));
            config.MoviesLibraryName = "VOD";
            config.SeriesLibraryName = "vod";

            var only = Assert.Single(LibraryRouting.NeededLibraries(config));
            Assert.Equal(LibraryRouting.MoviesRole, only.Role);
        }

        [Fact]
        public void NeededLibraries_KeepsAnEmptyNameSoTheCallerCanRefuseIt()
        {
            // Review focus 4: an unconfigured library that an enabled source needs must reach the
            // caller as a needed entry, so the failure can name the role instead of producing
            // "No library named ''."
            var config = Config((SourceMode.Movies, true), (SourceMode.Series, true));
            config.MoviesLibraryName = string.Empty;

            var movies = LibraryRouting.NeededLibraries(config)
                .Single(n => n.Role == LibraryRouting.MoviesRole);

            Assert.Equal(string.Empty, movies.Name);
        }

        [Fact]
        public void ConfiguredNames_DropsEmptyAndDuplicateNames()
        {
            var config = Config();
            config.MoviesLibraryName = "VOD";
            config.SeriesLibraryName = "vod";
            Assert.Equal(new[] { "VOD" }, LibraryRouting.ConfiguredNames(config));

            config.SeriesLibraryName = string.Empty;
            Assert.Equal(new[] { "VOD" }, LibraryRouting.ConfiguredNames(config));
        }
    }
}

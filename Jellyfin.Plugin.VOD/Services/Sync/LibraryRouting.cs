using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Configuration;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Which library each source mode publishes into, and which libraries a sync pass must prepare.
    /// </summary>
    /// <remarks>
    /// Pure and shared: the sync service decides which libraries a pass must prepare, and the
    /// controller decides what to report about each role. Those are the same question asked twice,
    /// and a second copy of the answer would drift.
    /// </remarks>
    internal static class LibraryRouting
    {
        /// <summary>The role name reported for the movies library.</summary>
        internal const string MoviesRole = "movies";

        /// <summary>The role name reported for the series library.</summary>
        internal const string SeriesRole = "series";

        /// <summary>Returns the role name a source mode publishes under.</summary>
        internal static string RoleFor(SourceMode mode)
            => mode == SourceMode.Movies ? MoviesRole : SeriesRole;

        /// <summary>Returns the configured library name a source mode publishes into.</summary>
        internal static string NameFor(PluginConfiguration config, SourceMode mode)
            => mode == SourceMode.Movies ? config.MoviesLibraryName : config.SeriesLibraryName;

        /// <summary>
        /// Returns whether at least one ENABLED source publishes in this mode.
        /// </summary>
        /// <remarks>
        /// Disabled is paused, not removed: such a source keeps its items but must not drag its
        /// library into the preparation, or a library nothing currently syncs into could stop a pass.
        /// </remarks>
        internal static bool IsNeeded(PluginConfiguration config, SourceMode mode)
            => config.Sources.Exists(s => s.Enabled && s.Mode == mode);

        /// <summary>
        /// Returns the distinct libraries at least one enabled source publishes into, with the role
        /// that needs each. An empty name is KEPT, so the caller can refuse it by role rather than
        /// letting it fail later as "No library named ''."
        /// </summary>
        internal static IReadOnlyList<(string Role, string Name)> NeededLibraries(
            PluginConfiguration config)
        {
            var needed = new List<(string Role, string Name)>();

            foreach (var mode in new[] { SourceMode.Movies, SourceMode.Series })
            {
                if (!IsNeeded(config, mode))
                    continue;

                var name = NameFor(config, mode);
                if (needed.Exists(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                needed.Add((RoleFor(mode), name));
            }

            return needed;
        }

        /// <summary>
        /// Returns the distinct configured library names, empty ones dropped, whether or not any
        /// source currently targets them. Used by retirement, which must look wherever a removed
        /// source could have published.
        /// </summary>
        internal static IReadOnlyList<string> ConfiguredNames(PluginConfiguration config)
        {
            var names = new List<string>();

            foreach (var name in new[] { config.MoviesLibraryName, config.SeriesLibraryName })
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                names.Add(name);
            }

            return names;
        }
    }
}

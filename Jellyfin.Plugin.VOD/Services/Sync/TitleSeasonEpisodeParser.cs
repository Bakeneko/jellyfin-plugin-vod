using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>
    /// Parses season and episode numbers from video titles that embed them as text.
    /// </summary>
    /// <remarks>
    /// Arte titles follow the pattern "Series Name - Saison N (E/Total)".
    /// The same format appears across Arte language variants: Saison (fr), Season (en),
    /// Staffel (de), Temporada (es/pt), Stagione (it).
    /// </remarks>
    public static partial class TitleSeasonEpisodeParser
    {
        // Matches "Saison 1 (2/6)", "Season 2 (3/8)", "Staffel 1 (1/4)", etc.
        [GeneratedRegex(
            @"(?:Saison|Season|Staffel|Temporada|Stagione)\s+(\d+)\s*\((\d+)/\d+\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex SeasonEpisodeRegex();

        /// <summary>
        /// Tries to extract season and episode numbers from <paramref name="title"/>.
        /// Returns <see langword="true"/> and sets <paramref name="season"/> /
        /// <paramref name="episode"/> on success; otherwise returns <see langword="false"/>.
        /// </summary>
        public static bool TryParse(string? title, out int season, out int episode)
        {
            season = 0;
            episode = 0;
            if (string.IsNullOrEmpty(title))
                return false;
            var m = SeasonEpisodeRegex().Match(title);
            if (!m.Success)
                return false;
            season = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }
    }
}

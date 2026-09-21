using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class TitleSeasonEpisodeParserTests
    {
        [Theory]
        [InlineData("Happy Valley - Saison 1 (1/6)",   1, 1)]
        [InlineData("Happy Valley - Saison 1 (6/6)",   1, 6)]
        [InlineData("Happy Valley - Saison 2 (3/6)",   2, 3)]
        [InlineData("Happy Valley - Saison 3 (6/6)",   3, 6)]
        [InlineData("Some Show - Season 2 (4/8)",      2, 4)]
        [InlineData("Eine Serie - Staffel 3 (2/4)",    3, 2)]
        [InlineData("Una Serie - Temporada 1 (5/6)",   1, 5)]
        [InlineData("Una Serie - Stagione 2 (3/4)",    2, 3)]
        [InlineData("lowercase saison 1 (2/6)",        1, 2)]
        public void TryParse_ValidArteTitle_ReturnsTrueAndCorrectValues(
            string title, int expectedSeason, int expectedEpisode)
        {
            var result = TitleSeasonEpisodeParser.TryParse(title, out var season, out var episode);

            Assert.True(result);
            Assert.Equal(expectedSeason, season);
            Assert.Equal(expectedEpisode, episode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Happy Valley")]
        [InlineData("Happy Valley - Saison 1")]          // no episode part
        [InlineData("Some show (1/6)")]                   // no season keyword
        [InlineData("Dead Watts - Episode 42")]
        public void TryParse_NoMatch_ReturnsFalse(string? title)
        {
            var result = TitleSeasonEpisodeParser.TryParse(title, out var season, out var episode);

            Assert.False(result);
            Assert.Equal(0, season);
            Assert.Equal(0, episode);
        }
    }
}

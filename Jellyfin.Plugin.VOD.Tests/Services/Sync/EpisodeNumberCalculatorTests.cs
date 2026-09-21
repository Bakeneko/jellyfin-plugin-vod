using System;
using Jellyfin.Plugin.VOD.Services.Sync;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class EpisodeNumberCalculatorTests
    {
        [Fact]
        public void TryCalculate_FirstEpisodeOnJanFirst_ReturnsExpected()
        {
            var d = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var result = EpisodeNumberCalculator.TryCalculate(d, 1);
            Assert.NotNull(result);
            Assert.Equal(2026, result.Value.season);
            // (1 * 100 + 1) * 1000 + 1 = 101_001
            Assert.Equal(101_001, result.Value.episode);
        }

        [Fact]
        public void TryCalculate_ThirdEpisodeOnSep18_ReturnsExpected()
        {
            var d = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
            var result = EpisodeNumberCalculator.TryCalculate(d, 3);
            Assert.NotNull(result);
            Assert.Equal(2026, result.Value.season);
            // (9 * 100 + 18) * 1000 + 3 = 918_003
            Assert.Equal(918_003, result.Value.episode);
        }

        [Fact]
        public void TryCalculate_MaxValidRankDec31_ReturnsExpected()
        {
            var d = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
            var result = EpisodeNumberCalculator.TryCalculate(d, 999);
            Assert.NotNull(result);
            // (12 * 100 + 31) * 1000 + 999 = 1_231_999
            Assert.Equal(1_231_999, result.Value.episode);
        }

        [Fact]
        public void TryCalculate_RankOver999_ReturnsNull()
        {
            var d = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
            Assert.Null(EpisodeNumberCalculator.TryCalculate(d, 1000));
        }

        [Fact]
        public void TryCalculate_RankZero_ReturnsNull()
        {
            var d = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
            Assert.Null(EpisodeNumberCalculator.TryCalculate(d, 0));
        }

        [Fact]
        public void TryCalculate_NegativeRank_ReturnsNull()
        {
            var d = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
            Assert.Null(EpisodeNumberCalculator.TryCalculate(d, -1));
        }
    }
}

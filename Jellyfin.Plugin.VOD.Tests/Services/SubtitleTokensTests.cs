using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class SubtitleTokensTests
    {
        private readonly PlaylistCache _cache = new();
        private readonly SubtitleTokens _tokens;

        public SubtitleTokensTests() => _tokens = new SubtitleTokens(_cache);

        [Fact]
        public void Rewrite_ANonSubtitleUri_IsLeftAlone()
        {
            const string Uri = "https://example.test/audio/playlist.m3u8";

            Assert.Equal(Uri, _tokens.Rewrite(Uri));
        }

        [Fact]
        public void Rewrite_ATimedtextUri_ReturnsARelativeTokenRouteThatResolvesBack()
        {
            const string Uri = "https://www.youtube.com/api/timedtext?v=x&lang=fr";

            var rewritten = _tokens.Rewrite(Uri);

            Assert.StartsWith("../subtitles/", rewritten, System.StringComparison.Ordinal);
            Assert.EndsWith(".m3u8", rewritten, System.StringComparison.Ordinal);

            var token = rewritten!["../subtitles/".Length..^".m3u8".Length];
            Assert.True(_tokens.TryResolve(token, out var resolved));
            Assert.Equal(Uri, resolved);
        }

        [Fact]
        public void TryResolve_AnUnknownToken_Fails()
        {
            Assert.False(_tokens.TryResolve("deadbeef", out _));
        }
    }
}

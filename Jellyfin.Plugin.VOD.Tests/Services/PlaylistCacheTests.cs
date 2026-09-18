using System;
using System.Threading;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class PlaylistCacheTests
    {
        [Fact]
        public void TryGet_MissOnEmpty_ReturnsFalse()
        {
            var cache = new PlaylistCache();
            Assert.False(cache.TryGet("missing", out _));
        }

        [Fact]
        public void TryGet_AfterSet_ReturnsPlaylist()
        {
            var cache = new PlaylistCache();
            cache.Set("k", "#EXTM3U\n");
            Assert.True(cache.TryGet("k", out var playlist));
            Assert.Equal("#EXTM3U\n", playlist);
        }

        [Fact]
        public void TryGet_AfterExpiry_ReturnsFalse()
        {
            var cache = new PlaylistCache();
            cache.Set("k", "data", TimeSpan.FromMilliseconds(50));
            Thread.Sleep(100);
            Assert.False(cache.TryGet("k", out _));
        }

        [Fact]
        public void Set_OverwritesPrevious()
        {
            var cache = new PlaylistCache();
            cache.Set("k", "v1");
            cache.Set("k", "v2");
            Assert.True(cache.TryGet("k", out var v));
            Assert.Equal("v2", v);
        }
    }
}

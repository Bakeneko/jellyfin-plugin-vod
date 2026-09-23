using System;
using System.IO;
using Jellyfin.Plugin.VOD.Imaging;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Imaging
{
    public sealed class ArtworkGeneratorTests
    {
        [Fact]
        public void HashOf_PinsTheExactDigest()
        {
            // The first four bytes of the SHA-256 of the key, lowercase hex. Pinned rather than
            // recomputed: changing it renames every generated poster file already on disk.
            Assert.Equal("5616d6c8", ArtworkGenerator.HashOf("youtube_ch_arte|S2"));
        }

        [Fact]
        public void HashOf_IsEightHexCharacters()
        {
            var hash = ArtworkGenerator.HashOf("youtube_ch_arte");

            Assert.Equal(8, hash.Length);
            Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        }

        [Fact]
        public void HashOf_IsCaseSensitive()
        {
            // Two YouTube channel ids differing only in case are different channels, so they must not
            // end up sharing a poster.
            Assert.NotEqual(
                ArtworkGenerator.HashOf("youtube_ch_UCabc"),
                ArtworkGenerator.HashOf("youtube_ch_ucabc"));
        }

        [Fact]
        public void HashOf_DistinguishesSeasonsOfOneSource()
            => Assert.NotEqual(
                ArtworkGenerator.HashOf("youtube_ch_arte|S1"),
                ArtworkGenerator.HashOf("youtube_ch_arte|S2"));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void HashOf_RejectsBlankKeys(string? key)
            => Assert.ThrowsAny<ArgumentException>(() => ArtworkGenerator.HashOf(key!));

        [Fact]
        public void Generate_EmitsAPngSignature()
        {
            var bytes = ArtworkGenerator.Generate(ArtworkGenerator.HashOf("k"));

            Assert.True(bytes.Length > 8);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        }

        [Fact]
        public void Generate_DiffersBetweenKeys()
        {
            var a = ArtworkGenerator.Generate(ArtworkGenerator.HashOf("youtube_ch_arte|S1"));
            var b = ArtworkGenerator.Generate(ArtworkGenerator.HashOf("youtube_ch_arte|S2"));

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Generate_DrawsTheCaption()
        {
            var hash = ArtworkGenerator.HashOf("youtube_ch_arte");

            Assert.NotEqual(ArtworkGenerator.Generate(hash), ArtworkGenerator.Generate(hash, "Arte"));
        }

        [Fact]
        public void Generate_ShrinksALongCaptionRatherThanFailing()
        {
            // Long channel names are common; this drives the font down to the 14f floor.
            var caption = new string('M', 200);

            var bytes = ArtworkGenerator.Generate(ArtworkGenerator.HashOf("long"), caption);

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        }

        [Theory]
        [InlineData("\u30a2\u30eb\u30c6\u30fb\u30c9\u30ad\u30e5\u30e1\u30f3\u30bf\u30ea\u30fc")]
        [InlineData("Arte \U0001F3AC")]
        [InlineData("\u4e2d\u6587\u9891\u9053 \U0001F1E8\U0001F1F3")]
        public void Generate_HandlesNonLatinCaptions(string caption)
        {
            // The glyphs may well be missing from the chosen typeface; that must degrade to tofu,
            // not throw out of the sync task.
            var bytes = ArtworkGenerator.Generate(ArtworkGenerator.HashOf("cjk"), caption);

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        }
    }

    public sealed class ArtworkStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "vod-artwork-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void GetOrCreate_WritesAPngAtAStablePath()
        {
            var store = new ArtworkStore(_dir);

            var first = store.GetOrCreate("youtube_ch_arte|S1");
            var second = store.GetOrCreate("youtube_ch_arte|S1");

            Assert.Equal(first, second);
            Assert.True(File.Exists(first));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(first)[..4]);
        }

        [Fact]
        public void GetOrCreate_LeavesNoTemporaryFileBehind()
        {
            var store = new ArtworkStore(_dir);
            store.GetOrCreate("k");

            Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "artwork"), "*.tmp"));
        }

        [Fact]
        public void GetOrCreate_DrawsTheCaptionItWasGiven()
        {
            var store = new ArtworkStore(_dir);

            var plain = store.GetOrCreate("plain");
            var captioned = store.GetOrCreate("captioned", "Arte");

            Assert.NotEqual(File.ReadAllBytes(plain), File.ReadAllBytes(captioned));
        }

        [Fact]
        public void GetOrCreate_GivesDifferentKeysDifferentFiles()
        {
            var store = new ArtworkStore(_dir);

            Assert.NotEqual(store.GetOrCreate("a|S1"), store.GetOrCreate("a|S2"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
    }
}

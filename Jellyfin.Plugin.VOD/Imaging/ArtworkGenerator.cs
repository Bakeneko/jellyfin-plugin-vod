using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Jellyfin.Plugin.VOD.Imaging
{
    /// <summary>
    /// Produces a placeholder poster, in 2:3, for items no source supplies an image for.
    /// </summary>
    /// <remarks>
    /// Text is drawn with SkiaSharp, which the Jellyfin server already loads — the plugin references
    /// it at compile time only. Jellyfin's own <c>IImageEncoder.CreateImageCollage</c> was the obvious
    /// route and does not fit: it only draws a caption on a landscape strip collage (ratio 1.4 and
    /// up), and a 2:3 request falls through to a caption-free square collage, which Jellyfin's source
    /// marks <c>// TODO: Create Poster collage capability</c>.
    ///
    /// A plain coloured field with the name across the middle. The colour is derived from the key so
    /// that two posters differ at a glance in a library grid, and is kept dark enough for white text.
    /// The hash is case-sensitive on purpose: youtube_ch_UCabc and youtube_ch_ucabc are different
    /// channels and must not share a poster.
    /// </remarks>
    public static class ArtworkGenerator
    {
        private const int Width = 400;
        private const int Height = 600;

        /// <summary>
        /// Returns the stable short hash identifying an artwork key.
        /// </summary>
        /// <param name="key">The artwork key, e.g. "youtube_ch_arte|S2".</param>
        /// <returns>Eight lowercase hexadecimal characters.</returns>
        public static string HashOf(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));

            return Convert.ToHexString(digest, 0, 4).ToLowerInvariant();
        }

        /// <summary>
        /// Generates the poster for an artwork hash, optionally captioned.
        /// </summary>
        /// <param name="hash">The value returned by <see cref="HashOf"/>.</param>
        /// <param name="caption">Text to draw, e.g. a season name. Omitted when absent.</param>
        /// <returns>A PNG image.</returns>
        public static byte[] Generate(string hash, string? caption = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var seed = uint.Parse(hash.PadRight(8, '0')[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(ColourFrom(seed));

            if (!string.IsNullOrWhiteSpace(caption))
                DrawCaption(canvas, caption);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            return data.ToArray();
        }

        /// <summary>
        /// Draws the caption in the middle of the poster, shrinking it until it fits the width.
        /// </summary>
        private static void DrawCaption(SKCanvas canvas, string caption)
        {
            const float Margin = 24f;

            // Scoped with using: MeasureText and DrawText can throw, and a trailing Dispose() call
            // would leak the typeface when they do.
            using var typeface = SKTypeface.FromFamilyName(
                "sans-serif", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.Default;

            using var font = new SKFont(typeface, 44f);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };

            // Long channel names are common, so the size is reduced rather than the text clipped.
            while (font.Size > 14f && font.MeasureText(caption) > Width - (Margin * 2))
                font.Size -= 2f;

            // Centred on the optical middle: Ascent is negative, so subtracting half the sum of the
            // two metrics moves the baseline down by half the glyph height.
            var y = (Height / 2f) - ((font.Metrics.Ascent + font.Metrics.Descent) / 2f);
            canvas.DrawText(caption, Width / 2f, y, SKTextAlign.Center, font, paint);
        }

        /// <summary>
        /// Derives a colour dark enough for white text to sit on it.
        /// </summary>
        private static SKColor ColourFrom(uint seed)
            => new(
                (byte)(40 + (seed & 0x7F)),
                (byte)(40 + ((seed >> 8) & 0x7F)),
                (byte)(40 + ((seed >> 16) & 0x7F)));
    }
}

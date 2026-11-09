using System;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Xunit;
using Xunit.Abstractions;

namespace Core.Tests;

public class ImageContentAnalyzerTests(ITestOutputHelper output)
{
    private const int W = 1280;
    private const int H = 720;

    /// <summary>
    /// A modern UI capture: window tint with an acrylic-style noise texture, a smooth drop-shadow ramp and text with
    /// per-channel (ClearType-like) anti-aliasing. Thousands of exact colours, yet visually flat.
    /// </summary>
    private static RasterImage UiScreenshot(bool dark, bool noisyTint = true, bool subpixelText = true)
    {
        var pixels = new byte[W * H * 4];
        var (bgR, bgG, bgB) = dark ? (32, 34, 38) : (243, 244, 247);
        var (fgR, fgG, fgB) = dark ? (228, 230, 232) : (24, 26, 30);

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                var sidebar = x < W / 5;
                var r = bgR + (sidebar ? (dark ? 8 : -8) : 0);
                var g = bgG + (sidebar ? (dark ? 8 : -8) : 0);
                var b = bgB + (sidebar ? (dark ? 8 : -8) : 0);

                if (noisyTint)
                {
                    r += Noise(x, y, 1) % 5 - 2;
                    g += Noise(x, y, 2) % 5 - 2;
                    b += Noise(x, y, 3) % 5 - 2;
                }

                if (y < 28)
                {
                    var shade = (int)((28 - y) * (dark ? 0.9 : 1.6));
                    r -= shade; g -= shade; b -= shade;
                }

                // Text rows: ~25% of the row pixels carry glyph ink, half of those partially covered.
                if (!sidebar && y % 22 is >= 6 and < 16 && y > 40 && Noise(x / 2, y / 3, 9) % 4 == 0)
                {
                    var partial = subpixelText && Noise(x, y, 4) % 2 == 0;
                    var cr = partial ? Noise(x, y, 5) % 256 : 255;
                    var cg = partial ? Noise(x, y, 6) % 256 : 255;
                    var cb = partial ? Noise(x, y, 7) % 256 : 255;
                    r = (r * (255 - cr) + fgR * cr) / 255;
                    g = (g * (255 - cg) + fgG * cg) / 255;
                    b = (b * (255 - cb) + fgB * cb) / 255;
                }

                pixels[i] = (byte)Math.Clamp(r, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(g, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(b, 0, 255);
                pixels[i + 3] = 255;
            }
        }

        return new RasterImage(W, H, pixels);
    }

    /// <summary>A very dark photograph (night sky, concert): mostly near-black with grain and a few bright regions.</summary>
    private static RasterImage LowKeyPhoto()
    {
        var pixels = TestImages.PhotoLikeRgba(W, H, seed: 12);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                pixels[i + c] = (byte)(pixels[i + c] * pixels[i + c] / 255 / 6); // crush into the shadows
            }
        }

        return new RasterImage(W, H, pixels);
    }

    /// <summary>A sky gradient with sensor noise: smooth and low-contrast, but a photograph.</summary>
    private static RasterImage NoisySkyPhoto()
    {
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                var t = y / (double)H;
                pixels[i] = (byte)Math.Clamp(40 + 130 * t + Noise(x, y, 1) % 7 - 3, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(90 + 120 * t + Noise(x, y, 2) % 7 - 3, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(170 + 70 * t + Noise(x, y, 3) % 7 - 3, 0, 255);
                pixels[i + 3] = 255;
            }
        }

        return new RasterImage(W, H, pixels);
    }

    private static int Noise(int x, int y, int salt)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + salt * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (int)(h & 0x7fffffff);
        }
    }

    private static ImageContentProfile Analyse(RasterImage image) => ImageContentAnalyzer.Analyse(image, hasAlpha: false);

    [Fact]
    public void NoisyDarkUiCaptureIsAGraphicEvenThoughItsExactColoursAreScattered()
    {
        var profile = Analyse(UiScreenshot(dark: true));

        // The failure the exact histogram had: acrylic noise + sub-pixel text leave the 48 commonest exact colours with a
        // third of the pixels, so the old rule could not call this flat.
        Assert.True(profile.TopColorCoverage < 0.90, $"exact coverage {profile.TopColorCoverage:F3}");
        Assert.True(profile.QuantizedTopColorCoverage > 0.93, $"quantised coverage {profile.QuantizedTopColorCoverage:F3}");
        Assert.Equal(ImageContentKind.Graphic, profile.Kind);
        Assert.False(profile.IsPhotographic);
    }

    [Fact]
    public void LightUiCaptureWithNoiseIsStillADocument()
    {
        Assert.Equal(ImageContentKind.Document, Analyse(UiScreenshot(dark: false)).Kind);
    }

    [Fact]
    public void CleanUiCaptureStaysAGraphic()
    {
        var profile = Analyse(UiScreenshot(dark: true, noisyTint: false));

        Assert.Equal(ImageContentKind.Graphic, profile.Kind);
        Assert.True(profile.TopColorCoverage >= 0.90);
    }

    [Fact]
    public void PhotographsAreNotMistakenForGraphics()
    {
        var textured = Analyse(new RasterImage(W, H, TestImages.PhotoLikeRgba(W, H)));
        var sky = Analyse(NoisySkyPhoto());

        Assert.Equal(ImageContentKind.Photograph, textured.Kind);
        Assert.True(textured.QuantizedTopColorCoverage < 0.30, $"{textured.QuantizedTopColorCoverage:F3}");

        // Close to the threshold on coverage alone; it is the missing hard edges that keep it a photograph.
        Assert.Equal(ImageContentKind.Photograph, sky.Kind);
        Assert.True(sky.QuantizedTopColorCoverage < 0.93, $"{sky.QuantizedTopColorCoverage:F3}");
    }

    [Theory]
    [InlineData(0.00, false)]   // smooth: coverage alone must not make it flat
    [InlineData(0.019, false)]
    [InlineData(0.02, true)]      // hard edges present: text, panels, lines
    [InlineData(0.19, true)]
    public void QuantisedCoverageOnlyCountsWhenHardEdgesExist(double edgeDensity, bool expectGraphic)
    {
        // Exact colours scattered (0.3 coverage, 0.3 unique) so no other flat-colour rule fires; only the quantised one can.
        var kind = ImageContentAnalyzer.DetermineKind(edgeDensity, uniqueColorRatio: 0.30, topCoverage: 0.30, quantizedCoverage: 0.95, whitespaceRatio: 0.0, luminanceVariance: 0.03, hasAlpha: false);

        Assert.Equal(expectGraphic ? ImageContentKind.Graphic : ImageContentKind.Photograph, kind);
    }

    [Theory]
    [InlineData(0.92)]
    [InlineData(0.50)]
    public void QuantisedCoverageBelowTheThresholdChangesNothing(double quantizedCoverage)
    {
        var kind = ImageContentAnalyzer.DetermineKind(0.2, uniqueColorRatio: 0.30, topCoverage: 0.30, quantizedCoverage, 0.0, 0.03, false);

        Assert.Equal(ImageContentKind.Photograph, kind);
    }

    [Fact]
    public void QuantisedCoverageNeverFallsBelowExactCoverage()
    {
        foreach (var image in new[] { UiScreenshot(true), UiScreenshot(false), NoisySkyPhoto(), LowKeyPhoto(), new RasterImage(W, H, TestImages.PhotoLikeRgba(W, H)) })
        {
            var profile = Analyse(image);
            Assert.InRange(profile.QuantizedTopColorCoverage, profile.TopColorCoverage - 1e-9, 1.0 + 1e-9);
        }
    }

    [Fact]
    public void QuantisedCoverageIsTheShareOfTheFortyEightLargestBuckets()
    {
        // 64 distinct 5-bit colours with strictly decreasing areas (shades inside one bucket must merge). Width 160 keeps
        // the analyser's sampling stride at 1, so every pixel is counted.
        const int width = 160, height = 100, colours = 64;
        var share = new int[colours];
        var total = width * height;
        var remaining = total;
        for (var i = 0; i < colours - 1; i++)
        {
            share[i] = Math.Max(1, remaining * 2 / 5);
            remaining -= share[i];
        }

        share[colours - 1] = Math.Max(1, remaining);
        // Largest-first shares sum to `total` only approximately; rebuild the exact figure from what is painted.
        var pixels = new byte[width * height * 4];
        var cursor = 0;
        var painted = new int[colours];
        for (var colour = 0; colour < colours && cursor < width * height; colour++)
        {
            var count = colour == colours - 1 ? width * height - cursor : Math.Min(share[colour], width * height - cursor);
            for (var k = 0; k < count; k++, cursor++)
            {
                var i = cursor * 4;
                var shade = k % 8; // eight exact shades that all land in the same 5-bit bucket
                pixels[i] = (byte)(colour % 8 * 32 + shade);
                pixels[i + 1] = (byte)(colour / 8 % 8 * 32 + shade);
                pixels[i + 2] = (byte)(colour / 64 * 32 + shade);
                pixels[i + 3] = 255;
            }

            painted[colour] = count;
        }

        Array.Sort(painted);
        Array.Reverse(painted);
        double expected = 0;
        for (var i = 0; i < 48; i++)
        {
            expected += painted[i];
        }

        expected /= width * height;
        var profile = Analyse(new RasterImage(width, height, pixels));

        Assert.Equal(expected, profile.QuantizedTopColorCoverage, 6);
    }

    [Fact]
    public void RealApplicationScreenshotsInDocsAreClassifiedAsNonPhotographic()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "images"));
        if (!System.IO.Directory.Exists(root))
        {
            return;
        }

        var files = System.IO.Directory.GetFiles(root, "*.png");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var raster = ImageIO.DecodeToRaster(file);
            var hasAlpha = raster.HasVisibleAlpha();
            var profile = ImageContentAnalyzer.Analyse(raster, hasAlpha);
            output.WriteLine($"{System.IO.Path.GetFileName(file),-28} kind={profile.Kind,-10} exact={profile.TopColorCoverage:F3} quant5={profile.QuantizedTopColorCoverage:F3} edge={profile.EdgeDensity:F3} var={profile.BrightnessVariance:F4} white={profile.WhitespaceRatio:F3} unique={profile.UniqueColorRatio:F3} alpha={hasAlpha}");
            Assert.False(profile.IsPhotographic, $"{System.IO.Path.GetFileName(file)} was classified as Photograph (exact={profile.TopColorCoverage:F3}, quant5={profile.QuantizedTopColorCoverage:F3}, edge={profile.EdgeDensity:F3})");

            if (string.Equals(System.IO.Path.GetFileName(file), "hero.png", StringComparison.OrdinalIgnoreCase))
            {
                // hero.png has soft drop shadows and a dark backdrop beside a light window: exact 48-colour coverage is only
                // ~0.71 and luminance variance is high (~0.19), so every unquantised rule misses it, while 5-bit quantisation
                // reaches ~0.98 and classifies it as Graphic.
                var withoutQuantized = ImageContentAnalyzer.DetermineKind(
                    profile.EdgeDensity, profile.UniqueColorRatio, profile.TopColorCoverage,
                    quantizedCoverage: 0, profile.WhitespaceRatio, profile.BrightnessVariance, hasAlpha);
                Assert.Equal(ImageContentKind.Photograph, withoutQuantized);
                Assert.Equal(ImageContentKind.Graphic, profile.Kind);
            }
        }
    }
}

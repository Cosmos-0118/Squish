using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using ImageMagick;
using Xunit;

namespace Core.Tests;

public sealed class ImageOptimiserTests
{
    private const int Width = 640;
    private const int Height = 480;

    private static readonly ImageOptimiserOptions BuiltIn = new()
    {
        AdvancedCodecs = AdvancedCodecPreferences.Disabled,
        CropSuggestions = CropSuggestionOptions.Disabled
    };

    // ---- JPEG ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReEncodesHighQualityJpegWithinThePerceptualTarget()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height);
        var source = TestImages.SaveJpeg(pixels, Width, Height, quality: 97);
        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.NotNull(output);
            Assert.True(new FileInfo(output!.Value.Value).Length < new FileInfo(source.Value).Length * 0.8);

            var reference = ImageIO.DecodeToRaster(source.Value);
            var candidate = ImageIO.DecodeToRaster(output.Value.Value);
            var score = PerceptualReference.Create(reference).Score(candidate);
            Assert.True(score >= BuiltIn.PerceptualGuard.MinimumScore - 0.5, $"score {score} below target");
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task AggressiveModeAcceptsLowerQualityThanNormalMode()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 11);
        var source = TestImages.SaveJpeg(pixels, Width, Height, quality: 98);
        var normalCopy = FilePath.TempFile("normal", "jpg", addUniqueSuffix: true);
        var aggressiveCopy = FilePath.TempFile("aggressive", "jpg", addUniqueSuffix: true);
        File.Copy(source.Value, normalCopy.Value);
        File.Copy(source.Value, aggressiveCopy.Value);
        try
        {
            var normal = await RunAsync(new ImageOptimiser(BuiltIn), normalCopy);
            var aggressive = await RunAsync(new ImageOptimiser(BuiltIn), aggressiveCopy, new Dictionary<string, object?> { [OptimisationMetadata.ImageAggressive] = true });

            Assert.Equal(OptimisationStatus.Succeeded, normal.Status);
            Assert.Equal(OptimisationStatus.Succeeded, aggressive.Status);
            Assert.True(new FileInfo(aggressive.OutputPath!.Value.Value).Length <= new FileInfo(normal.OutputPath!.Value.Value).Length);
        }
        finally
        {
            TestImages.TryDelete(source, normalCopy, aggressiveCopy);
        }
    }

    [Fact]
    public async Task KeepsOriginalWhenNothingCanBeSavedWithoutVisibleLoss()
    {
        // Random noise cannot be compressed without destroying it; the guard must keep the original.
        var noise = new byte[Width * Height * 4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(noise);
        for (var i = 3; i < noise.Length; i += 4)
        {
            noise[i] = 255;
        }

        // Entropy-optimised progressive source: re-encoding at the same quality cannot save bytes on its own.
        var source = TestImages.SaveJpeg(noise, Width, Height, quality: 92, image =>
        {
            image.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
            image.Settings.Interlace = Interlace.Plane;
        });
        var before = File.ReadAllBytes(source.Value);
        try
        {
            // At the default target a full-chroma re-encode of noise can legitimately pass (~81), so ask for a score that
            // no re-encode below the source quality can reach.
            var strict = BuiltIn with { PerceptualGuard = PerceptualGuardOptions.Default with { MinimumScore = 90 } };
            var result = await RunAsync(new ImageOptimiser(strict), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Equal(source.Value, result.OutputPath!.Value.Value);
            Assert.Equal(before, File.ReadAllBytes(source.Value));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task OptimisingTwiceIsStable()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 5);
        var source = TestImages.SaveJpeg(pixels, Width, Height, quality: 97);
        try
        {
            var first = await RunAsync(new ImageOptimiser(BuiltIn), source, ReplaceInPlace());
            Assert.Equal(OptimisationStatus.Succeeded, first.Status);
            var sizeAfterFirst = new FileInfo(source.Value).Length;

            var second = await RunAsync(new ImageOptimiser(BuiltIn), source, ReplaceInPlace());
            Assert.Equal(OptimisationStatus.Succeeded, second.Status);
            Assert.Equal(sizeAfterFirst, new FileInfo(source.Value).Length);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task KeepsPicturesUprightWhenMetadataIsStripped()
    {
        // A 640x480 landscape stored with orientation "rotate 90 CW" displays as 480x640 portrait. After optimisation the
        // picture must still display that way, either because the pixels were rotated or because a bare orientation
        // tag survived - but nothing else from the EXIF block may.
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 9);
        var source = TestImages.SaveJpeg(pixels, Width, Height, quality: 97);
        var exifProfile = new ExifProfile();
        exifProfile.SetValue(ExifTag.Make, "TestCam");
        exifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        var exifBytes = exifProfile.ToByteArray()![6..];
        File.WriteAllBytes(source.Value, JpegSegments.Rewrite(File.ReadAllBytes(source.Value), new MetadataBlobs(exifBytes, null, null, null)));
        var output = default(FilePath?);
        try
        {
            var sourceExif = JpegSegments.Extract(File.ReadAllBytes(source.Value)).Exif;
            Assert.Equal(6, JpegSegments.ReadOrientation(sourceExif!));

            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            using var oriented = ImageIO.Decode(output!.Value.Value, SourceFormat.Jpeg, autoOrient: true);
            Assert.Equal((Height, Width), (oriented.Width, oriented.Height));

            using var raw = new MagickImage(output.Value.Value);
            Assert.Null(raw.GetExifProfile()?.GetValue(ExifTag.Make));
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task StripsGpsButKeepsDescriptionWhenPreservingMetadata()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 13);
        var source = TestImages.SaveJpeg(pixels, Width, Height, quality: 97, image =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.ImageDescription, "Squish Windows Test");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(37, 1), new Rational(25, 1), new Rational(19, 1) });
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(122, 1), new Rational(5, 1), new Rational(2, 1) });
            image.SetProfile(exif);
        });
        var output = default(FilePath?);
        try
        {
            var options = BuiltIn with { MetadataPolicy = new MetadataPolicyOptions { PreserveMetadata = true, StripGpsMetadata = true } };
            var result = await RunAsync(new ImageOptimiser(options), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            using var image = new MagickImage(output!.Value.Value);
            var profile = image.GetExifProfile();
            Assert.NotNull(profile);
            Assert.Equal("Squish Windows Test", profile!.GetValue(ExifTag.ImageDescription)?.Value);
            Assert.Null(profile.GetValue(ExifTag.GPSLatitude));
            Assert.Null(profile.GetValue(ExifTag.GPSLongitude));
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task DefaultPolicyRemovesAllExifButKeepsNonSrgbColourProfile()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 17);
        var source = TestImages.SaveJpeg(pixels, Width, Height, quality: 97, image =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            image.SetProfile(exif);
            image.SetProfile(new ColorProfile(TestIccProfile.Create()));
        });
        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            using var image = new MagickImage(output!.Value.Value);
            Assert.Null(image.GetExifProfile());
            var icc = image.GetColorProfile();
            Assert.NotNull(icc);
            Assert.Equal(TestIccProfile.Description, icc!.Description);
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task DownscalesRetinaImages()
    {
        var width = 2600;
        var height = 1700;
        var pixels = TestImages.PhotoLikeRgba(width, height, seed: 21);
        var source = TestImages.SaveJpeg(pixels, width, height, quality: 95);
        var output = default(FilePath?);
        try
        {
            var options = BuiltIn with { DownscaleRetina = true, RetinaLongEdgePixels = 1024 };
            var result = await RunAsync(new ImageOptimiser(options), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            var (w, h) = TestImages.Dimensions(output!.Value.Value);
            Assert.Equal(1024, Math.Max(w, h));
            Assert.InRange((double)w / h, (double)width / height - 0.01, (double)width / height + 0.01);
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task RejectsImagesExceedingPixelLimit()
    {
        var source = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(200, 200), 200, 200, 90);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn with { MaxInputPixels = 10_000, MaxDimension = null }), source);

            Assert.Equal(OptimisationStatus.Failed, result.Status);
            Assert.Contains("pixel limit", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task RejectsImagesExceedingDimensionLimit()
    {
        var source = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(200, 80), 200, 80, 90);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn with { MaxDimension = 100, MaxInputPixels = 1_000_000 }), source);

            Assert.Equal(OptimisationStatus.Failed, result.Status);
            Assert.Contains("dimension limit", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task LeavesCmykJpegUntouched()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 33);
        var source = TestImages.Save(pixels, Width, Height, "jpg", image =>
        {
            image.Alpha(AlphaAction.Off);
            image.ColorSpace = ColorSpace.CMYK;
            image.Quality = 97;
        });
        var before = File.ReadAllBytes(source.Value);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Equal(source.Value, result.OutputPath!.Value.Value);
            Assert.Equal(before, File.ReadAllBytes(source.Value));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [ToolFact("jpegtran")]
    public async Task LosslessJpegRecompressionKeepsEveryPixel()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 4);
        var source = TestImages.SaveJpeg(pixels, Width, Height, quality: 80);
        var output = default(FilePath?);
        try
        {
            // An unreachable perceptual target disables every lossy candidate, leaving only the lossless pass.
            var options = new ImageOptimiserOptions
            {
                CropSuggestions = CropSuggestionOptions.Disabled,
                PerceptualGuard = PerceptualGuardOptions.Default with { MinimumScore = 99.99 }
            };
            var result = await RunAsync(new ImageOptimiser(options), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.True(new FileInfo(output!.Value.Value).Length < new FileInfo(source.Value).Length);
            Assert.Equal(TestImages.ReadRgba(source.Value), TestImages.ReadRgba(output.Value.Value));
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [ToolFact("mozjpeg")]
    public async Task MozJpegBeatsTheBuiltInEncoderAtTheSameQualityTarget()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 15);
        var sourceA = TestImages.SaveJpeg(pixels, Width, Height, quality: 98);
        var sourceB = FilePath.TempFile("squish-b", "jpg", addUniqueSuffix: true);
        File.Copy(sourceA.Value, sourceB.Value);
        try
        {
            var builtIn = await RunAsync(new ImageOptimiser(BuiltIn), sourceA);
            var moz = await RunAsync(new ImageOptimiser(new ImageOptimiserOptions
            {
                AdvancedCodecs = AdvancedCodecPreferences.Disabled with { EnableMozJpeg = true }
            }), sourceB);

            Assert.True(new FileInfo(moz.OutputPath!.Value.Value).Length <= new FileInfo(builtIn.OutputPath!.Value.Value).Length);
            Assert.Contains("mozjpeg", moz.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestImages.TryDelete(sourceA, sourceB);
        }
    }

    // ---- PNG -------------------------------------------------------------------------------------------

    [Fact]
    public async Task PngRecompressionIsPixelIdenticalAndKeepsTransparency()
    {
        var pixels = TestImages.GraphicRgba(Width, Height, transparentBackground: true);
        var source = TestImages.SavePng(pixels, Width, Height);
        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.EndsWith(".png", output!.Value.Value, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(output.Value.Value).Length < new FileInfo(source.Value).Length);
            Assert.Equal(TestImages.ReadRgba(source.Value), TestImages.ReadRgba(output.Value.Value));
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [ToolFact("oxipng")]
    public async Task OxipngShrinksPngFurtherThanTheBuiltInEncoder()
    {
        var pixels = TestImages.GraphicRgba(Width, Height);
        var sourceA = TestImages.SavePng(pixels, Width, Height);
        var sourceB = FilePath.TempFile("squish-b", "png", addUniqueSuffix: true);
        File.Copy(sourceA.Value, sourceB.Value);
        try
        {
            var builtIn = await RunAsync(new ImageOptimiser(BuiltIn), sourceA);
            var oxi = await RunAsync(new ImageOptimiser(new ImageOptimiserOptions
            {
                AdvancedCodecs = AdvancedCodecPreferences.Disabled with { EnableOxipng = true }
            }), sourceB);

            Assert.True(new FileInfo(oxi.OutputPath!.Value.Value).Length <= new FileInfo(builtIn.OutputPath!.Value.Value).Length);
            Assert.Equal(TestImages.ReadRgba(sourceA.Value), TestImages.ReadRgba(oxi.OutputPath.Value.Value));
        }
        finally
        {
            TestImages.TryDelete(sourceA, sourceB);
        }
    }

    [ToolFact("pngquant")]
    public async Task AggressiveModeQuantisesGraphicPngsWhenTheyStayWithinTheTarget()
    {
        var pixels = TestImages.GraphicRgba(Width, Height);
        var source = TestImages.SavePng(pixels, Width, Height);
        var output = default(FilePath?);
        try
        {
            var options = new ImageOptimiserOptions { AdvancedCodecs = AdvancedCodecPreferences.Disabled with { EnablePngquant = true } };
            var lossless = await RunAsync(new ImageOptimiser(BuiltIn), CopyOf(source));
            var result = await RunAsync(new ImageOptimiser(options), source, new Dictionary<string, object?> { [OptimisationMetadata.ImageAggressive] = true });

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.True(new FileInfo(output!.Value.Value).Length <= new FileInfo(lossless.OutputPath!.Value.Value).Length);
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [ToolFact("pngquant")]
    public async Task NormalModeQuantisesGraphicPngsWithinTheTarget()
    {
        // Anti-aliased edges give a screenshot-like picture with thousands of colours that still fits a palette.
        var source = TestImages.SavePng(TestImages.GraphicRgba(Width, Height), Width, Height, image => image.GaussianBlur(0, 1.2));
        var reference = ImageIO.DecodeToRaster(source.Value);
        var output = default(FilePath?);
        try
        {
            var options = new ImageOptimiserOptions { AdvancedCodecs = AdvancedCodecPreferences.Disabled with { EnablePngquant = true } };
            var lossless = await RunAsync(new ImageOptimiser(BuiltIn), CopyOf(source));
            var result = await RunAsync(new ImageOptimiser(options), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.Contains("palette", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(output!.Value.Value).Length < new FileInfo(lossless.OutputPath!.Value.Value).Length);
            Assert.True(PngInfo.TryRead(output.Value.Value)!.IsIndexed);

            var score = PerceptualReference.Create(reference).Score(ImageIO.DecodeToRaster(output.Value.Value));
            Assert.True(score >= options.PerceptualGuard.MinimumScore, $"score {score} below target");
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [ToolFact("pngquant")]
    public async Task NormalModeDoesNotRequantiseAnIndexedPng()
    {
        var pixels = TestImages.GraphicRgba(Width, Height);
        var source = TestImages.SavePng(pixels, Width, Height, image =>
        {
            image.Quantize(new QuantizeSettings { Colors = 64, DitherMethod = DitherMethod.No });
            image.Settings.SetDefine(MagickFormat.Png, "format", "png8");
        });
        var output = default(FilePath?);
        try
        {
            Assert.True(PngInfo.TryRead(source.Value)!.IsIndexed);
            var options = new ImageOptimiserOptions { AdvancedCodecs = AdvancedCodecPreferences.Disabled with { EnablePngquant = true } };
            var result = await RunAsync(new ImageOptimiser(options), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.DoesNotContain("palette", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TestImages.ReadRgba(source.Value), TestImages.ReadRgba(output!.Value.Value));
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Theory]
    [InlineData("2x2,1x1,1x1", true)]
    [InlineData("1x1,1x1,1x1", false)]
    [InlineData("2x1,1x1,1x1", false)]
    [InlineData("1x2,1x2,1x2", false)]
    [InlineData("1x1", false)]
    [InlineData(null, null)]
    public void ParsesJpegChromaSubsampling(string? samplingFactor, bool? expected)
    {
        Assert.Equal(expected, ImageIO.IsChroma420(samplingFactor));
    }

    [Fact]
    public async Task AnimatedPngIsLeftUntouched()
    {
        var source = FilePath.TempFile("apng", "png", addUniqueSuffix: true);
        using (var image = new MagickImage(MagickColors.Red, 64, 64))
        {
            // Magick's APNG writer shells out to ffmpeg on Windows, so mark a plain PNG as animated by hand.
            image.Write(source.Value, MagickFormat.Png);
        }

        File.WriteAllBytes(source.Value, WithAnimationControlChunk(File.ReadAllBytes(source.Value)));

        var before = File.ReadAllBytes(source.Value);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Equal(source.Value, result.OutputPath!.Value.Value);
            Assert.Equal(before, File.ReadAllBytes(source.Value));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    private static byte[] WithAnimationControlChunk(byte[] png)
    {
        const int afterIhdr = 8 + 4 + 4 + 13 + 4;
        var chunk = new byte[4 + 4 + 8 + 4];
        chunk[3] = 8;
        "acTL"u8.CopyTo(chunk.AsSpan(4));
        chunk[11] = 3; // frame count
        var crc = 0xFFFFFFFFu;
        foreach (var b in chunk.AsSpan(4, 12))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        crc = ~crc;
        chunk[16] = (byte)(crc >> 24);
        chunk[17] = (byte)(crc >> 16);
        chunk[18] = (byte)(crc >> 8);
        chunk[19] = (byte)crc;

        return [.. png.AsSpan(0, afterIhdr), .. chunk, .. png.AsSpan(afterIhdr)];
    }

    // ---- GIF -------------------------------------------------------------------------------------------

    [Fact]
    public async Task OptimisedAnimatedGifKeepsAllFrames()
    {
        var source = FilePath.TempFile("anim", "gif", addUniqueSuffix: true);
        using (var frames = new MagickImageCollection())
        {
            for (var i = 0; i < 6; i++)
            {
                var frame = new MagickImage(MagickColors.White, 120, 90);
                frame.Draw(new ImageMagick.Drawing.DrawableFillColor(MagickColors.Orange), new ImageMagick.Drawing.DrawableCircle(20 + i * 15, 45, 35 + i * 15, 45));
                frame.AnimationDelay = 8;
                frames.Add(frame);
            }

            frames.Write(source.Value, MagickFormat.Gif);
        }

        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn with { RequireSizeImprovement = false }), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.Equal(6, new MagickImageCollection(output!.Value.Value).Count);
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    // ---- Conversions & output planning -------------------------------------------------------------------

    [Fact]
    public async Task ConvertsPhotographicBmpToJpeg()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 25);
        var source = TestImages.Save(pixels, Width, Height, "bmp", image => image.Alpha(AlphaAction.Off));
        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.EndsWith(".jpg", output!.Value.Value, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(output.Value.Value).Length < new FileInfo(source.Value).Length / 4);
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task ConvertsGraphicBmpToPngInsteadOfJpeg()
    {
        var pixels = TestImages.GraphicRgba(Width, Height);
        var source = TestImages.Save(pixels, Width, Height, "bmp", image => image.Alpha(AlphaAction.Off));
        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.EndsWith(".png", output!.Value.Value, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TestImages.ReadRgba(source.Value), TestImages.ReadRgba(output.Value.Value));
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task ConvertsTiffToPng()
    {
        var pixels = TestImages.GraphicRgba(Width, Height);
        var source = TestImages.Save(pixels, Width, Height, "tif");
        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.EndsWith(".png", output!.Value.Value, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public async Task ConvertsWebpPhotoToJpegAndKeepsAlphaWebpAsPng()
    {
        var photo = TestImages.PhotoLikeRgba(Width, Height, seed: 27);
        var webpPhoto = TestImages.Save(photo, Width, Height, "webp", image =>
        {
            image.Alpha(AlphaAction.Off);
            image.Quality = 90;
        });
        var alpha = TestImages.GraphicRgba(Width, Height, transparentBackground: true);
        var webpAlpha = TestImages.Save(alpha, Width, Height, "webp", image => image.Settings.SetDefine(MagickFormat.WebP, "lossless", "true"));
        FilePath? first = null, second = null;
        try
        {
            var a = await RunAsync(new ImageOptimiser(BuiltIn), webpPhoto);
            var b = await RunAsync(new ImageOptimiser(BuiltIn), webpAlpha);

            first = a.OutputPath;
            second = b.OutputPath;
            Assert.EndsWith(".jpg", first!.Value.Value, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".png", second!.Value.Value, StringComparison.OrdinalIgnoreCase);
            Assert.True(ImageIO.DecodeToRaster(second.Value.Value).HasVisibleAlpha());
        }
        finally
        {
            TestImages.TryDelete(webpPhoto, webpAlpha, first, second);
        }
    }

    [Fact]
    public async Task ReplacesOriginalWhenMetadataRequestsInPlace()
    {
        var source = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(Width, Height, seed: 2), Width, Height, 97);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source, ReplaceInPlace());

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Equal(source.Value, result.OutputPath?.Value);
            Assert.False(File.Exists(source.Parent.Append($"{source.Stem}.squish.jpg").Value));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task ConversionKeepsOriginalWhenDeleteFlagNotSet()
    {
        var source = TestImages.Save(TestImages.PhotoLikeRgba(Width, Height, seed: 8), Width, Height, "bmp", image => image.Alpha(AlphaAction.Off));
        var expected = source.Parent.Append($"{source.Stem}.jpg");
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source, ReplaceInPlace());

            Assert.Equal(expected.Value, result.OutputPath?.Value);
            Assert.True(File.Exists(source.Value));
        }
        finally
        {
            TestImages.TryDelete(source, expected);
        }
    }

    [Fact]
    public async Task ConversionDeletesOriginalWhenDeleteFlagSet()
    {
        var source = TestImages.Save(TestImages.PhotoLikeRgba(Width, Height, seed: 8), Width, Height, "bmp", image => image.Alpha(AlphaAction.Off));
        var expected = source.Parent.Append($"{source.Stem}.jpg");
        try
        {
            var metadata = ReplaceInPlace();
            metadata[OptimisationMetadata.OutputDeleteConvertedSource] = true;
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source, metadata);

            Assert.Equal(expected.Value, result.OutputPath?.Value);
            Assert.False(File.Exists(source.Value));
        }
        finally
        {
            TestImages.TryDelete(source, expected);
        }
    }

    // ---- Robustness ------------------------------------------------------------------------------------

    [Fact]
    public async Task CorruptImagesFailGracefullyWithoutTouchingTheFile()
    {
        var source = FilePath.TempFile("corrupt", "jpg", addUniqueSuffix: true);
        File.WriteAllBytes(source.Value, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00 });
        var before = File.ReadAllBytes(source.Value);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.NotEqual(OptimisationStatus.Cancelled, result.Status);
            Assert.Equal(before, File.ReadAllBytes(source.Value));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Theory]
    [InlineData("push graphic-context\nviewbox 0 0 64 64\nfill 'url(https://example.invalid/x.png)'\npop graphic-context\n")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"8\"><rect width=\"8\" height=\"8\"/></svg>")]
    [InlineData("<image><read filename=\"/etc/passwd\"/><write filename=\"/tmp/pwned.png\"/></image>")]
    public async Task NeverFeedsNonImageContentToImageMagickDelegates(string payload)
    {
        // ImageMagick will happily interpret MVG/SVG/MSL scripts by file *content*; the optimiser must only ever
        // decode formats it identified from real image magic bytes.
        var source = FilePath.TempFile("script", "png", addUniqueSuffix: true);
        File.WriteAllText(source.Value, payload);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);

            Assert.Equal(OptimisationStatus.Failed, result.Status);
            Assert.False(File.Exists("/tmp/pwned.png"));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task MislabelledImagesAreHandledByTheirRealFormat()
    {
        var pixels = TestImages.PhotoLikeRgba(Width, Height, seed: 31);
        var png = TestImages.SavePng(pixels, Width, Height);
        var disguised = FilePath.TempFile("disguised", "jpg", addUniqueSuffix: true);
        File.Copy(png.Value, disguised.Value);
        var output = default(FilePath?);
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), disguised);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            output = result.OutputPath;
            Assert.True(ImageFormatSniffer.Sniff(output!.Value.Value) == SourceFormat.Png);
        }
        finally
        {
            TestImages.TryDelete(png, disguised, output);
        }
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        var source = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1600, 1200, seed: 6), 1600, 1200, 97);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                var optimiser = new ImageOptimiser(BuiltIn);
                var request = new OptimisationRequest(ItemType.Image, source);
                await optimiser.OptimiseAsync(request, NewContext(request), cts.Token);
            });
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task MissingFilesReportAFailure()
    {
        var source = FilePath.TempFile("missing", "png", addUniqueSuffix: true);
        var result = await RunAsync(new ImageOptimiser(BuiltIn), source);
        Assert.Equal(OptimisationStatus.Failed, result.Status);
    }

    [Fact]
    public async Task UnsupportedExtensionsAreRejected()
    {
        var source = FilePath.TempFile("notes", "txt", addUniqueSuffix: true);
        File.WriteAllText(source.Value, "hello");
        try
        {
            var result = await RunAsync(new ImageOptimiser(BuiltIn), source);
            Assert.Equal(OptimisationStatus.Unsupported, result.Status);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static Dictionary<string, object?> ReplaceInPlace() => new(StringComparer.Ordinal)
    {
        [OptimisationMetadata.OutputReplaceOriginal] = true
    };

    private static FilePath CopyOf(FilePath source)
    {
        var copy = FilePath.TempFile("copy", source.Extension, addUniqueSuffix: true);
        File.Copy(source.Value, copy.Value);
        return copy;
    }

    private static OptimiserExecutionContext NewContext(OptimisationRequest request)
        => new(request.RequestId, _ => { });

    private static async Task<OptimisationResult> RunAsync(ImageOptimiser optimiser, FilePath source, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var request = new OptimisationRequest(ItemType.Image, source, metadata: metadata);
        return await optimiser.OptimiseAsync(request, NewContext(request), CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
    }
}

using System;
using System.IO;
using System.Linq;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using ImageMagick;
using Xunit;

namespace Core.Tests;

public sealed class ImagingTests
{
    // ---- format sniffing --------------------------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "Jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, "Png")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "Gif")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, "Bmp")]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0 }, "Tiff")]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0, 42 }, "Tiff")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "WebP")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, "Avif")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, "Heic")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g' }, "Unknown")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'q', (byte)'t', (byte)' ', (byte)' ' }, "Unknown")]
    [InlineData(new byte[0], "Unknown")]
    public void SniffsFormatsFromMagicBytes(byte[] header, string expected)
        => Assert.Equal(Enum.Parse<SourceFormat>(expected), ImageFormatSniffer.Sniff(header));

    // ---- JPEG segment handling ----------------------------------------------------------------------------

    [Fact]
    public void JpegMetadataRoundTripsThroughRewrite()
    {
        var jpeg = File.ReadAllBytes(TestImages.SaveJpeg(TestImages.PhotoLikeRgba(96, 64), 96, 64, 85).Value);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "RoundTrip");
        var exifBytes = exif.ToByteArray()![6..];
        var icc = TestIccProfile.Create();
        var blobs = new MetadataBlobs(exifBytes, icc, "<x:xmpmeta/>"u8.ToArray(), null);

        var rewritten = JpegSegments.Rewrite(jpeg, blobs, addSquishMarker: true);
        var extracted = JpegSegments.Extract(rewritten);

        Assert.Equal(exifBytes, extracted.Exif);
        Assert.Equal(icc, extracted.Icc);
        Assert.Equal("<x:xmpmeta/>"u8.ToArray(), extracted.Xmp);
        Assert.True(JpegSegments.HasSquishMarker(rewritten));
        Assert.False(JpegSegments.HasSquishMarker(jpeg));

        // Pixel data must be untouched: decoding yields the same picture.
        Assert.Equal(TestImages.ReadRgba(WriteTemp(jpeg)), TestImages.ReadRgba(WriteTemp(rewritten)));
    }

    [Fact]
    public void RewriteWithEmptyMetadataStripsEverything()
    {
        var jpeg = File.ReadAllBytes(TestImages.SaveJpeg(TestImages.PhotoLikeRgba(96, 64), 96, 64, 85, image =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "ToBeRemoved");
            image.SetProfile(exif);
        }).Value);
        Assert.NotNull(JpegSegments.Extract(jpeg).Exif);

        var stripped = JpegSegments.Rewrite(jpeg, MetadataBlobs.Empty);

        Assert.True(JpegSegments.Extract(stripped).IsEmpty);
        Assert.True(stripped.Length < jpeg.Length);
    }

    [Fact]
    public void LargeIccProfilesAreSplitAcrossSegmentsAndReassembled()
    {
        var jpeg = File.ReadAllBytes(TestImages.SaveJpeg(TestImages.PhotoLikeRgba(64, 64), 64, 64, 85).Value);
        var icc = new byte[150_000];
        new Random(1).NextBytes(icc);

        var rewritten = JpegSegments.Rewrite(jpeg, new MetadataBlobs(null, icc, null, null));

        Assert.Equal(icc, JpegSegments.Extract(rewritten).Icc);
    }

    [Fact]
    public void OrientationIsReadFromBothByteOrders()
    {
        Assert.Equal(6, JpegSegments.ReadOrientation(JpegSegments.BuildOrientationOnlyExif(6)));
        Assert.Equal(8, JpegSegments.ReadOrientation(JpegSegments.BuildOrientationOnlyExif(8)));

        var bigEndian = new byte[]
        {
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
            0, 1,
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 3, 0, 0,
            0, 0, 0, 0
        };
        Assert.Equal(3, JpegSegments.ReadOrientation(bigEndian));
        Assert.Equal(1, JpegSegments.ReadOrientation(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void RewriteRejectsNonJpegInput()
        => Assert.Throws<InvalidDataException>(() => JpegSegments.Rewrite(new byte[] { 1, 2, 3, 4, 5 }, MetadataBlobs.Empty));

    // ---- metadata policy ----------------------------------------------------------------------------------

    [Fact]
    public void DefaultPolicyDropsEverythingExceptNonSrgbProfiles()
    {
        var exif = BuildExif(withGps: true);
        var blobs = new MetadataBlobs(exif, TestIccProfile.Create(), "<xmp/>"u8.ToArray(), new byte[] { 1 });

        var plan = MetadataPlan.Resolve(blobs, MetadataPolicyOptions.Default, orientationAlreadyApplied: true);

        Assert.Null(plan.Exif);
        Assert.Null(plan.Xmp);
        Assert.Null(plan.Iptc);
        Assert.NotNull(plan.Icc);
    }

    [Fact]
    public void ProfilesCanBeDroppedOnRequest()
    {
        var blobs = new MetadataBlobs(null, TestIccProfile.Create(), null, null);
        var plan = MetadataPlan.Resolve(blobs, MetadataPolicyOptions.Default with { PreserveColorProfiles = false }, orientationAlreadyApplied: true);
        Assert.Null(plan.Icc);
    }

    [Fact]
    public void PreservingMetadataStillRemovesGpsAndThumbnails()
    {
        var blobs = new MetadataBlobs(BuildExif(withGps: true), null, "<xmp/>"u8.ToArray(), null);
        var policy = new MetadataPolicyOptions { PreserveMetadata = true, StripGpsMetadata = true };

        var plan = MetadataPlan.Resolve(blobs, policy, orientationAlreadyApplied: true);

        Assert.NotNull(plan.Exif);
        var profile = new ExifProfile(new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 }.Concat(plan.Exif!).ToArray());
        Assert.Equal("TestCam", profile.GetValue(ExifTag.Make)?.Value);
        Assert.Null(profile.GetValue(ExifTag.GPSLatitude));
        Assert.Null(plan.Xmp);
    }

    [Fact]
    public void AppliedOrientationIsResetInPreservedExif()
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "TestCam");
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        var blobs = new MetadataBlobs(exif.ToByteArray()![6..], null, null, null);

        var plan = MetadataPlan.Resolve(blobs, new MetadataPolicyOptions { PreserveMetadata = true }, orientationAlreadyApplied: true);

        Assert.Equal(1, JpegSegments.ReadOrientation(plan.Exif!));
    }

    [Fact]
    public void SrgbProfilesAreRecognisedAsRedundant()
    {
        Assert.False(MetadataPlan.IsSrgbEquivalent(TestIccProfile.Create()));
        Assert.False(MetadataPlan.IsSrgbEquivalent(new byte[] { 1, 2, 3 }));
    }

    // ---- content analysis ------------------------------------------------------------------------------------

    [Fact]
    public void ClassifiesPhotographsAndFlatGraphics()
    {
        var photo = new RasterImage(320, 240, TestImages.PhotoLikeRgba(320, 240));
        var graphic = new RasterImage(320, 240, TestImages.GraphicRgba(320, 240));

        Assert.Equal(ImageContentKind.Photograph, ImageContentAnalyzer.Analyse(photo, hasAlpha: false).Kind);
        Assert.NotEqual(ImageContentKind.Photograph, ImageContentAnalyzer.Analyse(graphic, hasAlpha: false).Kind);
    }

    // ---- perceptual reference ----------------------------------------------------------------------------------

    [Fact]
    public void SmallImagesAreComparedWholeAndLargeOnesOnTiles()
    {
        var small = new RasterImage(256, 256, TestImages.PhotoLikeRgba(256, 256));
        var large = new RasterImage(2000, 1500, TestImages.PhotoLikeRgba(2000, 1500));

        var whole = PerceptualReference.Create(small);
        var tiled = PerceptualReference.Create(large);

        Assert.True(whole.IsWholeImage);
        Assert.Single(whole.Tiles);
        Assert.False(tiled.IsWholeImage);
        Assert.InRange(tiled.Tiles.Count, 2, PerceptualReference.DefaultMaxTiles);
        Assert.All(tiled.Tiles, tile =>
        {
            Assert.Equal(0, tile.X % 16);
            Assert.Equal(0, tile.Y % 16);
            Assert.InRange(tile.X + tile.Width, 1, large.Width);
            Assert.InRange(tile.Y + tile.Height, 1, large.Height);
        });
    }

    [Fact]
    public void MosaicScoreMatchesTileScoreForIdenticalContent()
    {
        var large = new RasterImage(2000, 1500, TestImages.PhotoLikeRgba(2000, 1500));
        var reference = PerceptualReference.Create(large);

        var mosaic = reference.BuildMosaic();

        Assert.Equal(100.0, reference.ScoreMosaic(mosaic), 3);
        Assert.Equal(100.0, reference.Score(large), 3);
    }

    [Fact]
    public void DegradedCandidatesScoreLowerThanFaithfulOnes()
    {
        var pixels = TestImages.PhotoLikeRgba(512, 384);
        var reference = new RasterImage(512, 384, pixels);
        var perceptual = PerceptualReference.Create(reference);

        double Score(int quality)
        {
            var path = TestImages.SaveJpeg(pixels, 512, 384, quality);
            try
            {
                return perceptual.Score(ImageIO.DecodeToRaster(path.Value));
            }
            finally
            {
                TestImages.TryDelete(path);
            }
        }

        var high = Score(95);
        var low = Score(30);
        Assert.True(high > low + 5, $"expected q95 ({high:F1}) to beat q30 ({low:F1}) clearly");
    }

    [Fact]
    public async System.Threading.Tasks.Task LossySearchFindsTheLowestPassingQuality()
    {
        // Synthetic monotonic response: score = quality - 10.
        var calls = 0;
        var found = await LossySearch.FindLowestPassingAsync(
            lowest: 30,
            highest: 95,
            targetScore: 72,
            evaluate: (quality, _) =>
            {
                calls++;
                return System.Threading.Tasks.Task.FromResult<double?>(quality - 10);
            },
            maxIterations: 10,
            System.Threading.CancellationToken.None);

        Assert.Equal(82, found);
        Assert.True(calls <= 9, $"bisection should need few evaluations, used {calls}");
    }

    [Fact]
    public async System.Threading.Tasks.Task LossySearchGivesUpWhenEvenTheCeilingFails()
    {
        var found = await LossySearch.FindLowestPassingAsync(
            30, 95, 90,
            (quality, _) => System.Threading.Tasks.Task.FromResult<double?>(quality / 2.0),
            10,
            System.Threading.CancellationToken.None);

        Assert.Null(found);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static byte[] BuildExif(bool withGps)
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "TestCam");
        if (withGps)
        {
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(1, 1), new Rational(2, 1), new Rational(3, 1) });
        }

        return exif.ToByteArray()![6..];
    }

    private static string WriteTemp(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), "squish-seg-" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, data);
        return path;
    }
}

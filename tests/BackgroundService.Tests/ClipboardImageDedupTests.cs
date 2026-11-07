using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Squish.BackgroundService.Clipboard;
using Xunit;

namespace BackgroundService.Tests;

public sealed class ClipboardImageDedupTests
{
    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static Bitmap Gradient(int width, int height, PixelFormat format = PixelFormat.Format32bppArgb, int seed = 0)
    {
        var bitmap = new Bitmap(width, height, format);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(255, (x * 7 + seed) % 256, (y * 5) % 256, (x + y) % 256));
            }
        }

        return bitmap;
    }

    [Fact]
    public void IdenticalBytesMatch()
    {
        using var bitmap = Gradient(32, 24);
        var bytes = EncodePng(bitmap);

        Assert.True(ClipboardImageSignature.Create(bytes).IsSameImageAs(ClipboardImageSignature.Create((byte[])bytes.Clone())));
    }

    [Fact]
    public void SamePixelsEncodedDifferentlyStillMatch()
    {
        // 24bpp and 32bpp bitmaps give different PNG bytes (and the second has an alpha channel) for the same picture.
        using var opaque = Gradient(40, 30, PixelFormat.Format24bppRgb);
        using var withAlpha = Gradient(40, 30, PixelFormat.Format32bppArgb);
        var first = EncodePng(opaque);
        var second = EncodePng(withAlpha);
        Assert.NotEqual(first, second);

        var a = ClipboardImageSignature.Create(first);
        var b = ClipboardImageSignature.Create(second);

        Assert.NotEqual(a.ByteHash, b.ByteHash);
        Assert.NotNull(a.PixelHash);
        Assert.True(a.IsSameImageAs(b));
    }

    [Fact]
    public void DifferentPixelsDoNotMatch()
    {
        using var one = Gradient(40, 30, seed: 0);
        using var two = Gradient(40, 30, seed: 3);

        Assert.False(ClipboardImageSignature.Create(EncodePng(one)).IsSameImageAs(ClipboardImageSignature.Create(EncodePng(two))));
    }

    [Fact]
    public void DifferentSizesDoNotMatch()
    {
        using var one = Gradient(40, 30);
        using var two = Gradient(41, 30);

        Assert.False(ClipboardImageSignature.Create(EncodePng(one)).IsSameImageAs(ClipboardImageSignature.Create(EncodePng(two))));
    }

    [Fact]
    public void ReadsDimensionsFromThePngHeader()
    {
        using var bitmap = Gradient(17, 9);
        var signature = ClipboardImageSignature.Create(EncodePng(bitmap));

        Assert.Equal(17, signature.Width);
        Assert.Equal(9, signature.Height);
    }

    [Fact]
    public void GarbageBytesStillGetAByteHashButNoPixelHash()
    {
        var signature = ClipboardImageSignature.Create(new byte[] { 1, 2, 3, 4 });

        Assert.Equal(0, signature.Width);
        Assert.Null(signature.PixelHash);
        Assert.False(string.IsNullOrEmpty(signature.ByteHash));
    }

    [Fact]
    public void OversizedImagesAreNotDecodedJustToBeHashed()
    {
        // Header claims a huge image; the pixel hash must be skipped without trying to decode it.
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 10_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 10_000);

        var signature = ClipboardImageSignature.Create(bytes);

        Assert.Equal(10_000, signature.Width);
        Assert.Null(signature.PixelHash);
    }

    private static ClipboardImageSignature Sig(int seed)
    {
        using var bitmap = Gradient(16, 16, seed: seed);
        return ClipboardImageSignature.Create(EncodePng(bitmap));
    }

    [Fact]
    public void ARepeatIsFoundWhileTheFirstIsInFlight()
    {
        var tracker = new ClipboardImageTracker();
        var entry = tracker.Add(Sig(0), sequence: 10);
        tracker.MarkQueued(entry, "req-1");

        var duplicate = tracker.FindDuplicate(Sig(0), sequence: 11);

        Assert.Same(entry, duplicate);
        Assert.True(tracker.IsLatestSequence(10, 11));
        Assert.False(tracker.IsLatestSequence(10, 10));
    }

    [Fact]
    public void ADifferentImageIsNotARepeat()
    {
        var tracker = new ClipboardImageTracker();
        tracker.MarkQueued(tracker.Add(Sig(0), 10), "req-1");

        Assert.Null(tracker.FindDuplicate(Sig(5), 11));
    }

    [Fact]
    public void AFinishedImageIsStillARepeatBrieflyThenForgotten()
    {
        var time = new ManualTime();
        var tracker = new ClipboardImageTracker(TimeSpan.FromSeconds(3), time);
        tracker.MarkQueued(tracker.Add(Sig(0), 10), "req-1");
        tracker.Finish("req-1", keep: true);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(tracker.FindDuplicate(Sig(0), 11));

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(tracker.FindDuplicate(Sig(0), 12));
    }

    [Fact]
    public void AnImageStillBeingOptimisedIsNeverForgotten()
    {
        var time = new ManualTime();
        var tracker = new ClipboardImageTracker(TimeSpan.FromSeconds(3), time);
        tracker.MarkQueued(tracker.Add(Sig(0), 10), "req-1");

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.NotNull(tracker.FindDuplicate(Sig(0), 11));
    }

    [Fact]
    public void AFailedImageCanBeTriedAgain()
    {
        var tracker = new ClipboardImageTracker();
        tracker.MarkQueued(tracker.Add(Sig(0), 10), "req-1");
        tracker.Finish("req-1", keep: false);

        Assert.Null(tracker.FindDuplicate(Sig(0), 11));
    }

    [Fact]
    public void SupersedingReturnsOnlyRunningRequestsAndForgetsThem()
    {
        var tracker = new ClipboardImageTracker();
        tracker.MarkQueued(tracker.Add(Sig(0), 10), "running");
        tracker.MarkQueued(tracker.Add(Sig(1), 11), "done");
        tracker.Finish("done", keep: true);

        var cancelled = tracker.SupersedeInFlight();

        Assert.Equal(new[] { "running" }, cancelled);
        Assert.Null(tracker.FindDuplicate(Sig(0), 12));
        Assert.Empty(tracker.SupersedeInFlight());
    }

    [Fact]
    public void AnImageThatWasNeverQueuedIsNotCancelled()
    {
        var tracker = new ClipboardImageTracker();
        tracker.Add(Sig(0), 10);

        Assert.Empty(tracker.SupersedeInFlight());
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Squish.BackgroundService.Automation;
using Squish.BackgroundService.Clipboard;
using Squish.Core.Shared;
using Xunit;

namespace BackgroundService.Tests;

internal static class TestImages
{
    public static byte[] Png(int width, int height, PixelFormat format = PixelFormat.Format32bppArgb, int seed = 0)
    {
        using var bitmap = new Bitmap(width, height, format);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(255, (x * 7 + seed) % 256, (y * 5) % 256, (x + y) % 256));
            }
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

public sealed class ScreenshotDedupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("shot-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FileAndClipboardEncodingsOfTheSamePictureShareAPixelKey()
    {
        var clipboard = TestImages.Png(48, 32, PixelFormat.Format24bppRgb);
        var file = TestImages.Png(48, 32, PixelFormat.Format32bppArgb);
        Assert.NotEqual(clipboard, file);

        var path = Path.Combine(_dir, "shot.png");
        File.WriteAllBytes(path, file);

        var signature = ClipboardImageSignature.Create(clipboard);
        var fromFile = ImagePixelHasher.TryHashFile(path);

        Assert.NotNull(signature.PixelHash);
        Assert.NotNull(fromFile);
        Assert.Equal(new ImagePixels(signature.Width, signature.Height, signature.PixelHash!).Key, fromFile!.Value.Key);
    }

    [Fact]
    public void DifferentPicturesHaveDifferentKeys()
    {
        var a = Path.Combine(_dir, "a.png");
        var b = Path.Combine(_dir, "b.png");
        File.WriteAllBytes(a, TestImages.Png(32, 32, seed: 0));
        File.WriteAllBytes(b, TestImages.Png(32, 32, seed: 1));

        Assert.NotEqual(ImagePixelHasher.TryHashFile(a)!.Value.Key, ImagePixelHasher.TryHashFile(b)!.Value.Key);
    }

    [Fact]
    public void NonImagesAndMissingFilesHashToNull()
    {
        var text = Path.Combine(_dir, "t.png");
        File.WriteAllText(text, "not an image");

        Assert.Null(ImagePixelHasher.TryHashFile(text));
        Assert.Null(ImagePixelHasher.TryHashFile(Path.Combine(_dir, "missing.png")));
    }

    [Fact]
    public void ImagePixelsAreRememberedOnlyBriefly()
    {
        var registry = new OptimisedFileRegistry(imageWindow: TimeSpan.FromMilliseconds(100));
        Assert.False(registry.HasRecentImagePixels);

        registry.RegisterImagePixels("1x1:AB");
        Assert.True(registry.HasRecentImagePixels);
        Assert.True(registry.WasImagePixelsRecentlyHandled("1x1:AB"));
        Assert.False(registry.WasImagePixelsRecentlyHandled("1x1:CD"));

        Thread.Sleep(250);
        Assert.False(registry.WasImagePixelsRecentlyHandled("1x1:AB"));
        Assert.False(registry.HasRecentImagePixels);
    }

    [Fact]
    public void RecentWatcherImagesAreNewestFirstAndExpire()
    {
        var registry = new OptimisedFileRegistry(imageWindow: TimeSpan.FromMilliseconds(300));
        registry.NoteWatcherImage(FilePath.From(Path.Combine(_dir, "old.png")));
        Thread.Sleep(20);
        registry.NoteWatcherImage(FilePath.From(Path.Combine(_dir, "new.png")));

        var recent = registry.RecentWatcherImages();
        Assert.Equal(2, recent.Count);
        Assert.EndsWith("new.png", recent[0]);

        Thread.Sleep(450);
        Assert.Empty(registry.RecentWatcherImages());
    }
}

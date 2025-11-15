using System;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace ClopWindows.Core.Optimizers;

internal readonly record struct ImageSaveProfile(string Description, string Extension, Func<IImageEncoder> EncoderFactory, bool IsLossy)
{
    public static ImageSaveProfile ForJpeg(string description, int quality) => new(description, "jpg", () => new JpegEncoder
    {
        Quality = Math.Clamp(quality, 30, 100)
    }, true);

    public static ImageSaveProfile ForPng(string description, PngCompressionLevel compression = PngCompressionLevel.BestCompression) => new(description, "png", () => new PngEncoder
    {
        CompressionLevel = compression,
        FilterMethod = PngFilterMethod.Adaptive,
    }, false);

    public static ImageSaveProfile ForGif(string description) => new(description, "gif", () => new GifEncoder(), false);

    public IImageEncoder CreateEncoder() => EncoderFactory();
}

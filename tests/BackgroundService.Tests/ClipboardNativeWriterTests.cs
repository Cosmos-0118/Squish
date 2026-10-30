using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Squish.BackgroundService.Clipboard;
using Xunit;

namespace BackgroundService.Tests;

public sealed class ClipboardNativeWriterTests
{
    [Fact]
    public void ImagePayloadIsOneBitmapAndDoesNotPublishTheMarkerAsText()
    {
        using var bitmap = new Bitmap(4, 4);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, bitmap);
        data.SetData(ClipboardFormats.OptimisationStatus, false, "true");

        var payloads = ClipboardNativeWriter.BuildPayloads(data);

        Assert.Single(payloads, payload => payload.Format == ClipboardNativeWriter.CfDib);
        Assert.DoesNotContain(payloads, payload => payload.Format == ClipboardNativeWriter.CfUnicodeText);
        var headerSize = BitConverter.ToInt32(payloads.Single(payload => payload.Format == ClipboardNativeWriter.CfDib).Data, 0);
        Assert.Contains(headerSize, new[] { 40, 108, 124 });
    }

    [Fact]
    public void PngAndBitmapStayOneImageUpdate()
    {
        using var bitmap = new Bitmap(2, 2);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var data = new DataObject();
        data.SetData("PNG", false, new MemoryStream(png, writable: false));
        data.SetData(DataFormats.Bitmap, true, bitmap);
        data.SetData(ClipboardFormats.OptimisationStatus, false, "true");

        var payloads = ClipboardNativeWriter.BuildPayloads(data);

        Assert.Single(payloads, payload => payload.Format == ClipboardNativeWriter.CfDib);
        Assert.Single(payloads, payload => payload.Data.AsSpan().SequenceEqual(png));
        Assert.DoesNotContain(payloads, payload => payload.Format == ClipboardNativeWriter.CfUnicodeText);
    }

    [Fact]
    public void TextIsPublishedOnceAsUnicode()
    {
        var data = new DataObject();
        data.SetData(DataFormats.Text, "written-by-us");

        var payloads = ClipboardNativeWriter.BuildPayloads(data);

        var text = Assert.Single(payloads);
        Assert.Equal(ClipboardNativeWriter.CfUnicodeText, text.Format);
        Assert.Equal("written-by-us", Encoding.Unicode.GetString(text.Data).TrimEnd('\0'));
    }

    [Fact]
    public void FileDropCarriesEveryPathOnce()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\a.png", @"C:\b.png" });
        data.SetData(DataFormats.Text, "C:\\a.png\r\nC:\\b.png");
        data.SetData(ClipboardFormats.OptimisationStatus, false, "true");

        var payloads = ClipboardNativeWriter.BuildPayloads(data);

        Assert.Single(payloads, payload => payload.Format == ClipboardNativeWriter.CfHdrop);
        Assert.Single(payloads, payload => payload.Format == ClipboardNativeWriter.CfUnicodeText);
        var drop = payloads.Single(payload => payload.Format == ClipboardNativeWriter.CfHdrop).Data;
        Assert.Equal(20, BitConverter.ToInt32(drop, 0));
        Assert.Equal(1, BitConverter.ToInt32(drop, 16));
        var listing = Encoding.Unicode.GetString(drop, 20, drop.Length - 20);
        Assert.Contains(@"C:\a.png", listing);
        Assert.Contains(@"C:\b.png", listing);
    }
}

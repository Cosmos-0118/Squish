using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class IsoBmffColourTests
{
    private static byte[] Box(string type, params byte[][] children)
    {
        var payload = children.SelectMany(c => c).ToArray();
        var size = 8 + payload.Length;
        return new byte[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size }
            .Concat(Encoding.ASCII.GetBytes(type)).Concat(payload).ToArray();
    }

    private static byte[] Nclx(int cp, int tc, int mc, bool fullRange = true)
        => Box("colr", Encoding.ASCII.GetBytes("nclx"),
            new[] { (byte)(cp >> 8), (byte)cp, (byte)(tc >> 8), (byte)tc, (byte)(mc >> 8), (byte)mc, (byte)(fullRange ? 0x80 : 0) });

    /// <summary>ftyp + meta(version/flags, hdlr-like filler, iprp(ipco(filler, colr))) laid out like an AVIF header.</summary>
    private static byte[] AvifLike(byte[] colr)
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("avif"), new byte[8]);
        var filler = Box("ispe", new byte[12]);
        var ipco = Box("ipco", filler, colr);
        var meta = Box("meta", new byte[4], Box("hdlr", new byte[20]), Box("iprp", ipco));
        return ftyp.Concat(meta).Concat(Box("mdat", new byte[64])).ToArray();
    }

    [Theory]
    [InlineData(1, 13, 6, false)]   // avifenc default: sRGB
    [InlineData(1, 1, 1, false)]    // BT.709
    [InlineData(2, 2, 2, false)]    // unspecified
    [InlineData(9, 16, 9, true)]    // BT.2020 + PQ (HDR10)
    [InlineData(9, 18, 9, true)]    // BT.2020 + HLG
    [InlineData(12, 13, 6, true)]   // Display P3 primaries with an sRGB curve
    [InlineData(1, 8, 6, true)]     // linear transfer
    public void ReadsCicpAndClassifiesIt(int cp, int tc, int mc, bool beyondSrgb)
    {
        var info = IsoBmffColour.TryRead(AvifLike(Nclx(cp, tc, mc)));

        Assert.NotNull(info);
        Assert.Equal(new IsoColourInfo(cp, tc, mc), info!.Value);
        Assert.Equal(beyondSrgb, IsoBmffColour.IsBeyondSrgb(info.Value));
    }

    [Fact]
    public void IgnoresIccColourBoxesAndFilesWithoutMeta()
    {
        var icc = Box("colr", Encoding.ASCII.GetBytes("prof"), new byte[32]);
        Assert.Null(IsoBmffColour.TryRead(AvifLike(icc)));
        Assert.Null(IsoBmffColour.TryRead(Box("ftyp", Encoding.ASCII.GetBytes("avif"), new byte[8])));
        Assert.Null(IsoBmffColour.TryRead(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void SurvivesTruncatedAndGarbageInput()
    {
        var file = AvifLike(Nclx(9, 16, 9));
        for (var length = 0; length < file.Length; length += 3)
        {
            // Must never throw, whatever prefix is read.
            _ = IsoBmffColour.TryRead(file.AsSpan(0, length));
        }

        var garbage = new byte[512];
        new Random(7).NextBytes(garbage);
        _ = IsoBmffColour.TryRead(garbage);
        // A box that claims to be larger than the file must not loop or throw.
        _ = IsoBmffColour.TryRead(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'m', (byte)'e', (byte)'t', (byte)'a', 0, 0, 0, 0 });
    }

    // ---- end to end with the real encoder ---------------------------------------------------------------

    private static async Task<FilePath> EncodeAvifAsync(string avifenc, FilePath png, params string[] extra)
    {
        var avif = FilePath.TempFile("cicp", "avif", addUniqueSuffix: true);
        var args = new List<string> { "-q", "60", "-s", "8" };
        args.AddRange(extra);
        args.Add(png.Value);
        args.Add(avif.Value);
        var psi = new ProcessStartInfo(avifenc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return avif;
    }

    private static async Task<OptimisationResult> RunAsync(FilePath source)
    {
        var request = new OptimisationRequest(ItemType.Image, source);
        var context = (OptimiserExecutionContext)Activator.CreateInstance(
            typeof(OptimiserExecutionContext),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object[] { request.RequestId, (Action<OptimisationProgress>)(_ => { }) },
            null)!;
        return await new ImageOptimiser().OptimiseAsync(request, context, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
    }

    [ToolFact("avifenc")]
    public async Task HdrTaggedAvifIsLeftUntouchedButSdrAvifStillOptimises()
    {
        var avifenc = ToolAvailability.Avifenc!;
        var png = TestImages.SavePng(TestImages.PhotoLikeRgba(640, 480, seed: 5), 640, 480);
        FilePath? hdr = null, sdr = null, hdrCopy = null;
        try
        {
            hdr = await EncodeAvifAsync(avifenc, png, "--depth", "10", "--cicp", "9/16/9");
            sdr = await EncodeAvifAsync(avifenc, png);
            var before = File.ReadAllBytes(hdr.Value.Value);

            var hdrResult = await RunAsync(hdr.Value);
            var sdrResult = await RunAsync(sdr.Value);

            Assert.Equal(OptimisationStatus.Succeeded, hdrResult.Status);
            Assert.Contains("CICP 9/16/9", hdrResult.Message);
            Assert.Equal(before, File.ReadAllBytes(hdr.Value.Value));
            Assert.False(File.Exists(hdr.Value.Parent.Append($"{hdr.Value.Stem}.squish.avif").Value));
            Assert.False(File.Exists(hdr.Value.Parent.Append($"{hdr.Value.Stem}.squish.jpg").Value));

            // The sRGB-tagged file is not caught by the guard: it is optimised as before.
            Assert.Equal(OptimisationStatus.Succeeded, sdrResult.Status);
            Assert.DoesNotContain("CICP", sdrResult.Message ?? string.Empty);
            hdrCopy = sdrResult.OutputPath;
        }
        finally
        {
            TestImages.TryDelete(png, hdr, sdr, hdrCopy);
        }
    }
}

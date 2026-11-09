using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Documents;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>What happens to the user's file when something goes wrong.</summary>
public sealed class ReliabilityTests
{
    private static readonly IReadOnlyDictionary<string, object?> ReplaceInPlace = new Dictionary<string, object?>
    {
        [OptimisationMetadata.OutputReplaceOriginal] = true
    };

    [Fact]
    public async Task ADamagedArchiveEntryFailsTheRunInsteadOfBeingCopiedIntoAValidLookingPackage()
    {
        var photo = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1000, 750, seed: 5), 1000, 750, quality: 95);
        var source = DocumentFixtures.Docx(File.ReadAllBytes(photo.Value), "image1.jpeg", "image/jpeg", DocumentFixtures.DocumentWithPictureXml(string.Empty));
        CorruptStoredChecksum(source.Value, "word/media/image1.jpeg");
        var before = File.ReadAllBytes(source.Value);
        try
        {
            var request = new OptimisationRequest(ItemType.Document, source, metadata: ReplaceInPlace);
            var context = new OptimiserExecutionContext(request.RequestId, _ => { });
            var result = await new DocumentOptimiser().OptimiseAsync(request, context, CancellationToken.None);

            Assert.Equal(OptimisationStatus.Failed, result.Status);
            Assert.Equal(before, File.ReadAllBytes(source.Value));
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    [Fact]
    public void ReplacingAFileLeavesNoTemporaryNeighbourAndKeepsTheContentWhole()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squish-safewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "new.bin");
            var destination = Path.Combine(directory, "target.bin");
            File.WriteAllBytes(source, Enumerable.Repeat((byte)7, 100_000).ToArray());
            File.WriteAllBytes(destination, new byte[10]);

            SafeFileWriter.CopyOver(source, destination);

            Assert.Equal(100_000, new FileInfo(destination).Length);
            Assert.Equal(["new.bin", "target.bin"], Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)!).Order().ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AFailedReplaceLeavesTheDestinationExactlyAsItWas()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squish-safewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "new.bin");
            var destination = Path.Combine(directory, "target.bin");
            File.WriteAllBytes(source, new byte[5000]);
            var original = Encoding.ASCII.GetBytes("precious original content");
            File.WriteAllBytes(destination, original);

            // Another program holds the file open without allowing it to be replaced.
            using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() => SafeFileWriter.CopyOver(source, destination));
            }

            Assert.Equal(original, File.ReadAllBytes(destination));
            Assert.Equal(["new.bin", "target.bin"], Directory.GetFiles(directory).Select(f => Path.GetFileName(f)!).Order().ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PixelBudgetLetsSmallPicturesRunTogetherAndALargeOneRunAlone()
    {
        var budget = new PixelBudget(12);
        var running = 0;
        var peak = 0;

        async Task Work(long pixels)
        {
            using var lease = await budget.AcquireAsync(pixels, CancellationToken.None);
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(60);
            Interlocked.Decrement(ref running);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Work(1_000_000)));
        Assert.True(peak >= 6, $"small pictures should overlap, peak {peak}");

        peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Work(30_000_000)));
        Assert.Equal(1, peak);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>Flips the CRC-32 recorded for one entry in the central directory (the data itself stays readable).</summary>
    private static void CorruptStoredChecksum(string path, string entryName)
    {
        var bytes = File.ReadAllBytes(path);
        var name = Encoding.UTF8.GetBytes(entryName);
        for (var i = 0; i + 46 + name.Length < bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02
                && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
            {
                bytes[i + 16] ^= 0xFF;
                File.WriteAllBytes(path, bytes);
                return;
            }
        }

        throw new InvalidOperationException("entry not found");
    }
}

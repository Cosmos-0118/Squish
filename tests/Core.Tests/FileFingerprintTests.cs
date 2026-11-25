using System;
using System.IO;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public class FileFingerprintTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("squish-fp-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private FilePath Write(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return FilePath.From(path);
    }

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(FileFingerprint.FullHashLimit)]
    [InlineData(FileFingerprint.FullHashLimit + 1)]
    [InlineData(5 * 1024 * 1024)]
    public void FileAndBytesProduceTheSameFingerprint(int length)
    {
        var data = Random(length, 1);
        var path = Write("a.bin", data);

        Assert.True(FileFingerprint.TryCreate(path, out var fromFile));
        Assert.Equal(FileFingerprint.FromBytes(data), fromFile);
    }

    [Fact]
    public void SmallFilesHashEveryByte()
    {
        var data = Random(2048, 2);
        var other = (byte[])data.Clone();
        other[1000] ^= 0xFF;

        Assert.NotEqual(FileFingerprint.FromBytes(data), FileFingerprint.FromBytes(other));
    }

    [Fact]
    public void LargeFilesNoticeChangesInSampledRegionsAndLength()
    {
        var data = Random(3 * 1024 * 1024, 3);
        var baseline = FileFingerprint.FromBytes(data);

        var tail = (byte[])data.Clone();
        tail[^10] ^= 0xFF;
        Assert.NotEqual(baseline, FileFingerprint.FromBytes(tail));

        Assert.NotEqual(baseline, FileFingerprint.FromBytes(data.AsSpan(0, data.Length - 1)));
    }

    [Fact]
    public void MissingFileFails()
    {
        Assert.False(FileFingerprint.TryCreate(FilePath.From(Path.Combine(_dir, "nope.bin")), out _));
    }

    [Fact]
    public void MarkerSurvivesTimestampChangeWhenContentIsSame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Write("m.bin", Random(4096, 4));
        Assert.True(SquishOptimisationMarker.TryMark(path));
        Assert.True(SquishOptimisationMarker.HasValidMarker(path));

        File.SetLastWriteTimeUtc(path.Value, DateTime.UtcNow.AddHours(-5));
        Assert.True(SquishOptimisationMarker.HasValidMarker(path));
        // Refreshed in place: still valid on the next look without needing the content check.
        Assert.True(SquishOptimisationMarker.HasValidMarker(path));
    }

    [Fact]
    public void MarkerIsInvalidatedWhenContentChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Random(4096, 5);
        var path = Write("c.bin", data);
        var stamp = File.GetLastWriteTimeUtc(path.Value);
        Assert.True(SquishOptimisationMarker.TryMark(path));

        data[100] ^= 0xFF;
        File.WriteAllBytes(path.Value, data);
        File.SetLastWriteTimeUtc(path.Value, stamp.AddMinutes(-3));

        Assert.False(SquishOptimisationMarker.HasValidMarker(path));
    }
}

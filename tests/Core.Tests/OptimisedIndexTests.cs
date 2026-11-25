using System;
using System.IO;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public class OptimisedIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("squish-idx-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string IndexFile => Path.Combine(_dir, "state", "optimised.idx");

    private FilePath Write(string name, int seed, int length = 4096)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return FilePath.From(path);
    }

    [Fact]
    public void RecordedFileIsKnownAndSurvivesARestart()
    {
        var file = Write("a.jpg", 1);
        var index = new OptimisedIndex(IndexFile);
        Assert.False(index.IsKnown(file));

        Assert.True(index.Record(file));
        Assert.True(index.IsKnown(file));

        // A new instance reads the file the first one wrote: the memory outlives the process.
        Assert.True(new OptimisedIndex(IndexFile).IsKnown(file));
    }

    [Fact]
    public void ChangedContentIsNotKnown()
    {
        var file = Write("b.jpg", 2);
        var index = new OptimisedIndex(IndexFile);
        index.Record(file);

        var changed = new byte[4096];
        new Random(99).NextBytes(changed);
        File.WriteAllBytes(file.Value, changed);

        Assert.False(index.IsKnown(file));
    }

    [Fact]
    public void ChangedSizeIsNotKnown()
    {
        var file = Write("c.jpg", 3);
        var index = new OptimisedIndex(IndexFile);
        index.Record(file);

        File.AppendAllText(file.Value, "x");

        Assert.False(index.IsKnown(file));
    }

    [Fact]
    public void TimestampOnlyChangeIsStillKnown()
    {
        var file = Write("d.jpg", 4);
        var index = new OptimisedIndex(IndexFile);
        index.Record(file);

        File.SetLastWriteTimeUtc(file.Value, DateTime.UtcNow.AddDays(-3));

        Assert.True(index.IsKnown(file));
        // The refreshed timestamp was persisted too.
        Assert.True(new OptimisedIndex(IndexFile).IsKnown(file));
    }

    [Fact]
    public void ForgetRemovesTheEntryEvenAfterARestart()
    {
        var file = Write("e.jpg", 5);
        var index = new OptimisedIndex(IndexFile);
        index.Record(file);
        index.Forget(file);

        Assert.False(index.IsKnown(file));
        Assert.False(new OptimisedIndex(IndexFile).IsKnown(file));
    }

    [Fact]
    public void KeepsOnlyTheNewestEntriesWithinCapacity()
    {
        var index = new OptimisedIndex(IndexFile, capacity: 16);
        var files = new FilePath[40];
        for (var i = 0; i < files.Length; i++)
        {
            files[i] = Write($"f{i}.jpg", 100 + i, 64);
            index.Record(files[i]);
        }

        Assert.True(index.Count <= 16 + 16 / 8);
        Assert.True(index.IsKnown(files[^1]));
        Assert.False(index.IsKnown(files[0]));

        var reloaded = new OptimisedIndex(IndexFile, capacity: 16);
        Assert.True(reloaded.IsKnown(files[^1]));
        Assert.True(new FileInfo(IndexFile).Length <= 32L * 16 * 3);
    }

    [Fact]
    public void CorruptOrMissingIndexFileMeansNoMemoryNotAnError()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexFile)!);
        File.WriteAllBytes(IndexFile, new byte[] { 1, 2, 3, 4, 5 });
        var file = Write("g.jpg", 7);

        var index = new OptimisedIndex(IndexFile);
        Assert.False(index.IsKnown(file));
        Assert.True(index.Record(file));
        Assert.True(index.IsKnown(file));

        // Realigned on load, so the record written after the junk is still readable after a restart.
        Assert.True(new OptimisedIndex(IndexFile).IsKnown(file));
    }
}

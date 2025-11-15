using System;
using System.Collections.Generic;
using System.IO;
using ClopWindows.CliBridge;
using Xunit;

namespace CliBridge.Tests;

public class TargetResolverTests : IDisposable
{
    private readonly string _root;

    public TargetResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CliBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ResolveHonoursRecursiveFlag()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested"));
        var files = new[]
        {
            Path.Combine(_root, "sample.jpg"),
            Path.Combine(nested.FullName, "clip.mp4"),
            Path.Combine(_root, "doc.pdf")
        };
        foreach (var file in files)
        {
            File.WriteAllText(file, "test");
        }

        var options = new OptimiseCommandOptions
        {
            Items = new List<string> { _root },
            Recursive = true,
            IncludeTypes = new[] { "image", "video" },
            ExcludeTypes = Array.Empty<string>(),
            SkipErrors = false
        };

        var resolver = new TargetResolver(options);
        var targets = resolver.Resolve();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Path.Value.EndsWith("sample.jpg", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Path.Value.EndsWith("clip.mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveSkipsMissingEntriesWhenFlagEnabled()
    {
        var missing = Path.Combine(_root, "missing.png");
        var options = new OptimiseCommandOptions
        {
            Items = new[] { missing },
            SkipErrors = true
        };

        var resolver = new TargetResolver(options);
        var targets = resolver.Resolve();

        Assert.Empty(targets);
    }

    [Fact]
    public void ResolveThrowsWhenMissingEntriesAndSkipDisabled()
    {
        var missing = Path.Combine(_root, "missing.png");
        var options = new OptimiseCommandOptions
        {
            Items = new[] { missing },
            SkipErrors = false
        };

        var resolver = new TargetResolver(options);

        Assert.Throws<FileNotFoundException>(() => resolver.Resolve());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}

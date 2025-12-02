using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClopWindows.Core.Optimizers;
using Xunit;

namespace Core.Tests;

[Collection("ToolResolution")]
public sealed class ToolResolutionTests
{
    [Fact]
    public void VideoOptimiserOptions_UsesEnvironmentVariableForFfmpeg()
    {
        using var tempExe = new TemporaryExecutable("ffmpeg-env");
        using var tempProbe = new TemporaryExecutable("ffprobe-env");
        using var primaryEnv = new EnvironmentVariableScope("CLOP_FFMPEG", tempExe.Path);
        using var secondaryEnv = new EnvironmentVariableScope("FFMPEG_EXECUTABLE", null);
        using var probePrimaryEnv = new EnvironmentVariableScope("CLOP_FFPROBE", tempProbe.Path);
        using var probeSecondaryEnv = new EnvironmentVariableScope("FFPROBE_EXECUTABLE", null);

        var options = new VideoOptimiserOptions();

        Assert.Equal(tempExe.Path, options.FfmpegPath);
        Assert.Equal(tempProbe.Path, options.FfprobePath);
    }

    [Fact]
    public void VideoOptimiserOptions_FindsBundledFfmpeg()
    {
        using var primaryEnv = new EnvironmentVariableScope("CLOP_FFMPEG", null);
        using var secondaryEnv = new EnvironmentVariableScope("FFMPEG_EXECUTABLE", null);
        using var probePrimaryEnv = new EnvironmentVariableScope("CLOP_FFPROBE", null);
        using var probeSecondaryEnv = new EnvironmentVariableScope("FFPROBE_EXECUTABLE", null);
        using var sandbox = new ToolSandbox();

        var expected = sandbox.CreateFile("ffmpeg", "bin", "ffmpeg.exe");
        var expectedProbe = sandbox.CreateFile("ffmpeg", "bin", "ffprobe.exe");

        var options = new VideoOptimiserOptions();

        Assert.Equal(expected, options.FfmpegPath);
        Assert.Equal(expectedProbe, options.FfprobePath);
    }

    [Fact]
    public void PdfOptimiserOptions_FindsBundledGhostscriptExecutable()
    {
        using var env1 = new EnvironmentVariableScope("CLOP_GS", null);
        using var env2 = new EnvironmentVariableScope("GS_EXECUTABLE", null);
        using var sandbox = new ToolSandbox();

        var expected = sandbox.CreateFile("ghostscript", "bin", "gswin64c.exe");

        var options = new PdfOptimiserOptions();

        Assert.Equal(expected, options.GhostscriptPath);
    }

    [Fact]
    public void PdfOptimiserOptions_FindsBundledGhostscriptResources()
    {
        using var env1 = new EnvironmentVariableScope("CLOP_GS_LIB", null);
        using var env2 = new EnvironmentVariableScope("GS_LIB", null);
        using var sandbox = new ToolSandbox();

        var expected = sandbox.CreateDirectory("ghostscript", "Resource", "Init");

        var options = new PdfOptimiserOptions();

        Assert.Equal(expected, options.GhostscriptResourceDirectory);
    }
}

[CollectionDefinition("ToolResolution", DisableParallelization = true)]
public sealed class ToolResolutionCollectionDefinition
{
}

internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _originalValue;

    public EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _originalValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}

internal sealed class TemporaryExecutable : IDisposable
{
    public TemporaryExecutable(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.exe");
        File.WriteAllText(Path, "stub");
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}

internal sealed class ToolSandbox : IDisposable
{
    private readonly string _toolsRoot = Path.Combine(AppContext.BaseDirectory, "tools");
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);

    public string CreateFile(params string[] segments)
    {
        var path = Path.Combine(new[] { _toolsRoot }.Concat(segments).ToArray());
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "stub");
        TrackRoot(segments);
        return path;
    }

    public string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine(new[] { _toolsRoot }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        TrackRoot(segments);
        return path;
    }

    private void TrackRoot(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var root = Path.Combine(_toolsRoot, segments[0]);
        _roots.Add(root);
    }

    public void Dispose()
    {
        foreach (var root in _roots.OrderByDescending(r => r.Length))
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        try
        {
            if (Directory.Exists(_toolsRoot) && !Directory.EnumerateFileSystemEntries(_toolsRoot).Any())
            {
                Directory.Delete(_toolsRoot);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}

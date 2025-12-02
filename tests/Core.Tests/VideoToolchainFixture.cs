using System;
using Xunit;

namespace Core.Tests;

[CollectionDefinition("VideoTools", DisableParallelization = true)]
public sealed class VideoToolsCollectionDefinition : ICollectionFixture<VideoToolchainFixture>
{
}

public sealed class VideoToolchainFixture : IDisposable
{
    private readonly ToolSandbox _sandbox;
    private readonly EnvironmentVariableScope _ffmpegEnv;
    private readonly EnvironmentVariableScope _ffprobeEnv;

    public VideoToolchainFixture()
    {
        _sandbox = new ToolSandbox();
        var ffmpegPath = _sandbox.CreateFile("ffmpeg", "bin", "ffmpeg.exe");
        var ffprobePath = _sandbox.CreateFile("ffmpeg", "bin", "ffprobe.exe");
        _ffmpegEnv = new EnvironmentVariableScope("CLOP_FFMPEG", ffmpegPath);
        _ffprobeEnv = new EnvironmentVariableScope("CLOP_FFPROBE", ffprobePath);
    }

    public void Dispose()
    {
        _ffmpegEnv.Dispose();
        _ffprobeEnv.Dispose();
        _sandbox.Dispose();
    }
}

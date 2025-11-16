using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class VideoOptimiserTests : IDisposable
{
    private readonly List<FilePath> _filesToCleanup = new();

    [Fact]
    public async Task TranscodesVideoAndProducesOutput()
    {
        var source = Track(CreateSampleVideo());
        var toolchain = new FakeVideoToolchain();
        var options = VideoOptimiserOptions.Default with { RequireSmallerSize = false };
        var probe = new FakeVideoMetadataProbe();
        var optimiser = new VideoOptimiser(options, toolchain, probe);

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        var optimised = result.OutputPath!.Value;
        Assert.True(File.Exists(optimised.Value));
        Track(optimised);
        Assert.Single(toolchain.TranscodePlans);
        Assert.Equal(source, toolchain.TranscodePlans.Single().SourcePath);
    }

    [Fact]
    public async Task HonoursMetadataOverrides()
    {
        var source = Track(CreateSampleVideo(sizeBytes: 4096));
        var toolchain = new FakeVideoToolchain();
        var optimiser = new VideoOptimiser(VideoOptimiserOptions.Default, toolchain, new FakeVideoMetadataProbe());

        var metadata = new Dictionary<string, object?>
        {
            ["video.targetFps"] = 30,
            ["video.maxWidth"] = 640,
            ["video.mode"] = "video"
        };

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source, metadata: metadata));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Track(result.OutputPath ?? source);

        var plan = Assert.Single(toolchain.TranscodePlans);
        Assert.Equal(30, plan.TargetFps);
        Assert.Equal(640, plan.MaxWidth);
        Assert.True(plan.CapFps);
    }

    [Fact]
    public async Task ConvertsToGifWhenRequested()
    {
        var source = Track(CreateSampleVideo());
        var toolchain = new FakeVideoToolchain();
        var optimiser = new VideoOptimiser(VideoOptimiserOptions.Default, toolchain, new FakeVideoMetadataProbe());

        var metadata = new Dictionary<string, object?>
        {
            ["video.mode"] = "gif"
        };

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source, metadata: metadata));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        var gif = result.OutputPath!.Value;
        Track(gif);
        Assert.EndsWith(".gif", gif.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Single(toolchain.AnimatedPlans);
        Assert.Empty(toolchain.TranscodePlans);
    }

    [Fact]
    public async Task AggressiveModePrefersAv1WhenHardwareAvailable()
    {
        var source = Track(CreateSampleVideo());
        var hardware = new VideoHardwareCapabilities(
            SupportsNvenc: true,
            SupportsAmf: false,
            SupportsQsv: false,
            SupportsDxva: true,
            SupportsAv1Nvenc: true,
            SupportsAv1Amf: false,
            SupportsAv1Qsv: false,
            SupportsHevcNvenc: true,
            SupportsHevcAmf: false,
            SupportsHevcQsv: false);

        var options = VideoOptimiserOptions.Default with
        {
            ForceMp4 = false,
            PreferAv1WhenAggressive = true,
            HardwareOverride = hardware,
            RequireSmallerSize = false
        };

        var toolchain = new FakeVideoToolchain();
        var optimiser = new VideoOptimiser(options, toolchain, new FakeVideoMetadataProbe());

        var metadata = new Dictionary<string, object?>
        {
            ["video.aggressive"] = true
        };

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source, metadata: metadata));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        var plan = Assert.Single(toolchain.TranscodePlans);
        Assert.Equal(VideoCodec.Av1, plan.Encoder.Codec);
        Assert.Equal("mkv", plan.OutputExtension);
    }

    [Fact]
    public async Task AudioNormalizationTriggersReencode()
    {
        var source = Track(CreateSampleVideo(sizeBytes: 8192));
        var toolchain = new FakeVideoToolchain();
        var options = VideoOptimiserOptions.Default with { RequireSmallerSize = false };
        var optimiser = new VideoOptimiser(options, toolchain, new FakeVideoMetadataProbe());

        var metadata = new Dictionary<string, object?>
        {
            ["video.audioNormalize"] = true,
            ["video.audioCodec"] = "aac"
        };

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source, metadata: metadata));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        var plan = Assert.Single(toolchain.TranscodePlans);
        Assert.False(plan.Audio.RemoveAudio);
        Assert.False(plan.Audio.CopyStream);
        Assert.True(plan.Audio.NormalizeLoudness);
        Assert.Equal(options.AudioEncoderAac, plan.Audio.Encoder);
    }

    [Fact]
    public async Task AnimatedFormatHonoursMetadataPreference()
    {
        var source = Track(CreateSampleVideo());
        var toolchain = new FakeVideoToolchain();
        var optimiser = new VideoOptimiser(VideoOptimiserOptions.Default with { RequireSmallerSize = false }, toolchain, new FakeVideoMetadataProbe());

        var metadata = new Dictionary<string, object?>
        {
            ["video.mode"] = "gif",
            ["video.animatedFormat"] = "webp"
        };

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source, metadata: metadata));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.EndsWith(".webp", result.OutputPath!.Value.Value, StringComparison.OrdinalIgnoreCase);
        var plan = Assert.Single(toolchain.AnimatedPlans);
        Assert.Equal(AnimatedExportFormat.AnimatedWebp, plan.AnimatedFormat);
    }

    [Fact]
    public async Task RemuxesCompatibleMovWithoutTranscode()
    {
        var source = Track(CreateSampleVideo(extension: ".mov"));
        var toolchain = new FakeVideoToolchain();
        var probe = new FakeVideoMetadataProbe
        {
            Result = CreateProbeInfo("h264", "aac", "mov")
        };

        var options = VideoOptimiserOptions.Default with
        {
            RequireSmallerSize = true,
            CapFps = false,
            EnableFrameDecimation = false,
            EnableAudioNormalization = false
        };
        var optimiser = new VideoOptimiser(options, toolchain, probe);

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Track(result.OutputPath!.Value);
        Assert.Empty(toolchain.TranscodePlans);
        var remuxPlan = Assert.Single(toolchain.RemuxPlans);
        Assert.True(remuxPlan.Remux.Enabled);
        Assert.Equal(RemuxReason.ContainerNormalisation, remuxPlan.Remux.Reason);
        Assert.EndsWith(".mp4", result.OutputPath!.Value.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebmSourcesPreferVp9AndKeepExtension()
    {
        var source = Track(CreateSampleVideo(extension: ".webm"));
        var toolchain = new FakeVideoToolchain();
        var probe = new FakeVideoMetadataProbe
        {
            Result = CreateProbeInfo("vp9", "opus", "webm")
        };

        var options = VideoOptimiserOptions.Default with { ForceMp4 = true, RequireSmallerSize = false };
        var optimiser = new VideoOptimiser(options, toolchain, probe);

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Video, source));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        var plan = Assert.Single(toolchain.TranscodePlans);
        Assert.Equal(VideoCodec.Vp9, plan.Encoder.Codec);
        Assert.Equal("webm", plan.OutputExtension);
    }

    public void Dispose()
    {
        foreach (var file in _filesToCleanup)
        {
            TryDelete(file);
        }
    }

    private FilePath Track(FilePath path)
    {
        _filesToCleanup.Add(path);
        return path;
    }

    private static FilePath CreateSampleVideo(int sizeBytes = 2048, string extension = ".mp4")
    {
        var path = FilePath.TempFile("video-optimiser-test", extension, addUniqueSuffix: true);
        path.EnsureParentDirectoryExists();
        var buffer = new byte[sizeBytes];
        new Random().NextBytes(buffer);
        File.WriteAllBytes(path.Value, buffer);
        return path;
    }

    private static void TryDelete(FilePath path)
    {
        try
        {
            if (File.Exists(path.Value))
            {
                File.Delete(path.Value);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static VideoProbeInfo CreateProbeInfo(string videoCodec, string audioCodec, string format)
    {
        var video = new VideoStreamInfo(videoCodec, "", "yuv420p", "bt709", 1920, 1080, 6_000_000, 30, false, false);
        var audio = new AudioStreamInfo(audioCodec, "", 2, 48_000, 256_000);
        return new VideoProbeInfo(format, format, 10, 6_500_000, 8_000_000, video, audio);
    }

    private sealed class FakeVideoToolchain : IVideoToolchain
    {
        public List<VideoOptimiserPlan> TranscodePlans { get; } = new();
        public List<VideoOptimiserPlan> AnimatedPlans { get; } = new();
        public List<VideoOptimiserPlan> RemuxPlans { get; } = new();

        public Task<ToolchainResult> TranscodeAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            TranscodePlans.Add(plan);
            context.ReportProgress(50, "fake transcode");
            File.WriteAllBytes(tempOutput.Value, Enumerable.Repeat((byte)0x1, 512).ToArray());
            return Task.FromResult(ToolchainResult.Successful());
        }

        public Task<ToolchainResult> ConvertToAnimatedAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            AnimatedPlans.Add(plan);
            context.ReportProgress(50, "fake animation");
            File.WriteAllBytes(tempOutput.Value, Enumerable.Repeat((byte)0x2, 256).ToArray());
            return Task.FromResult(ToolchainResult.Successful());
        }

        public Task<ToolchainResult> RemuxAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            RemuxPlans.Add(plan);
            context.ReportProgress(25, "fake remux");
            File.WriteAllBytes(tempOutput.Value, Enumerable.Repeat((byte)0x3, 128).ToArray());
            return Task.FromResult(ToolchainResult.Successful());
        }
    }

    private sealed class FakeVideoMetadataProbe : IVideoMetadataProbe
    {
        public VideoProbeInfo? Result { get; set; }

        public Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken) => Task.FromResult(Result);
    }
}
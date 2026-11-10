using System;
using System.Runtime.InteropServices;

namespace Squish.Core.Optimizers;

/// <summary>Whether the machine is plugged in. Software AV1 on battery costs several times the energy of the media engine.</summary>
public enum VideoPowerState
{
    Unknown = 0,
    Ac,
    Battery,
    BatterySaver
}

/// <summary>How much picture a job has to encode.</summary>
/// <param name="TotalMegapixels">Pixels per frame times frames, after the plan's scaling and frame-rate cap, in millions.</param>
internal readonly record struct VideoWorkload(double TotalMegapixels)
{
    /// <summary>
    /// Measures the job from the probe and the plan's own resize, frame-rate cap and speed change, or returns null when the
    /// probe lacks the geometry or duration (the automatic choice then stays on the software encoder).
    /// </summary>
    public static VideoWorkload? Measure(VideoProbeInfo? probe, bool capFps, int targetFps, int? maxWidth, int? maxHeight, double? playbackSpeed)
    {
        if (probe?.Video is not { Width: > 0, Height: > 0 } video || probe.DurationSeconds is not > 0)
        {
            return null;
        }

        double width = video.Width.Value;
        double height = video.Height.Value;

        var scale = 1.0;
        if (maxWidth is > 0)
        {
            scale = Math.Min(scale, maxWidth.Value / width);
        }

        if (maxHeight is > 0)
        {
            scale = Math.Min(scale, maxHeight.Value / height);
        }

        var fps = video.FrameRate is > 1 ? video.FrameRate.Value : 30.0;
        if (capFps && targetFps > 0 && fps > targetFps)
        {
            fps = targetFps;
        }

        var seconds = probe.DurationSeconds.Value;
        if (playbackSpeed is > 0 && Math.Abs(playbackSpeed.Value - 1.0) > 0.01)
        {
            seconds /= playbackSpeed.Value;
        }

        return new VideoWorkload(width * height * scale * scale * fps * seconds / 1_000_000d);
    }
}

internal readonly record struct AutoEncoderDecision(bool UseHardware, string Reason);

/// <summary>
/// Decides, for the "Auto" encoder preset, whether a job goes to the GPU or stays on software SVT-AV1.
/// </summary>
/// <remarks>
/// Software SVT-AV1 gives the smallest files, so it stays the choice while the wait is short. The GPU takes over when the
/// job is big enough that the software encode becomes a long, fan-spinning, battery-draining wait, and the better the GPU's
/// AV1 silicon, the smaller that job has to be. Thresholds are in megapixels of work (1080p30 is ~62 MP per second):
/// 15 s of 1080p30 is ~930 MP, 30 s of 1080p60 is ~3,700 MP.
/// </remarks>
internal static class VideoAutoEncoderPolicy
{
    public static AutoEncoderDecision Decide(VideoOptimiserOptions options, VideoHardwareCapabilities hardware, VideoWorkload? workload, VideoPowerState power)
    {
        if (!options.EnableAdaptiveAutoEncoder)
        {
            return new(false, "adaptive selection disabled");
        }

        if (!options.UseHardwareAcceleration)
        {
            return new(false, "hardware acceleration disabled");
        }

        var tier = hardware.EncoderTier;
        if (tier == GpuEncoderTier.None)
        {
            return new(false, "no working GPU encoder");
        }

        if (workload is not { } job)
        {
            return new(false, "workload unknown");
        }

        var megapixels = job.TotalMegapixels;
        var onBattery = power is VideoPowerState.Battery or VideoPowerState.BatterySaver;

        // Battery: the media engine does the same work for a fraction of the energy and finishes several times sooner, but
        // only worth leaving software for from a real video upwards, and only on a GPU that can encode HEVC or better.
        if (onBattery && tier >= GpuEncoderTier.Modern && megapixels >= options.AutoBatteryGpuMinMegapixels)
        {
            return new(true, $"on {power}, {megapixels:0} MP");
        }

        if (megapixels <= options.AutoCpuMaxMegapixels && !onBattery)
        {
            return new(false, $"{megapixels:0} MP is quick in software");
        }

        var threshold = tier switch
        {
            GpuEncoderTier.Flagship => options.AutoGpuMinMegapixelsFlagship,
            GpuEncoderTier.Av1 => options.AutoGpuMinMegapixelsAv1,
            GpuEncoderTier.Modern => options.AutoGpuMinMegapixelsModern,
            _ => options.AutoGpuMinMegapixelsLegacy
        };

        return megapixels > threshold
            ? new(true, $"{megapixels:0} MP exceeds the {tier} threshold of {threshold:0} MP")
            : new(false, $"{megapixels:0} MP is within software range for {tier} (threshold {threshold:0} MP)");
    }
}

internal static class VideoPowerStatus
{
    /// <summary>Current power source; <see cref="VideoPowerState.Unknown"/> where it cannot be read (non-Windows, no battery API).</summary>
    public static VideoPowerState Current()
    {
        if (!OperatingSystem.IsWindows())
        {
            return VideoPowerState.Unknown;
        }

        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                return VideoPowerState.Unknown;
            }

            // ACLineStatus: 0 offline (battery), 1 online, 255 unknown. SystemStatusFlag bit 0: battery saver on.
            if (status.ACLineStatus == 0)
            {
                return (status.SystemStatusFlag & 1) != 0 ? VideoPowerState.BatterySaver : VideoPowerState.Battery;
            }

            return status.ACLineStatus == 1 ? VideoPowerState.Ac : VideoPowerState.Unknown;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return VideoPowerState.Unknown;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

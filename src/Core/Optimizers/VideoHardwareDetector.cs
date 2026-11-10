using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Processes;
using Squish.Core.Shared;

namespace Squish.Core.Optimizers;

/// <summary>
/// Finds out which GPU video encoders actually work on this machine.
/// </summary>
/// <remarks>
/// <c>ffmpeg -encoders</c> only lists the wrappers compiled into the binary (every vendor's AV1 encoder is always
/// there) and a driver DLL only proves that <i>some</i> GPU of that vendor exists. Neither says whether this silicon can
/// encode AV1. So every encoder ffmpeg lists is exercised once with a tiny throw-away encode, in parallel, and the
/// outcome is cached in memory and on disk, keyed by the ffmpeg build and the installed GPUs and drivers.
/// </remarks>
public static class VideoHardwareDetector
{
    // Large enough for every vendor: hevc_amf refuses 320x240 ("Init() failed with error 5") and NVENC refuses tiny frames.
    private const string ProbeFrameSize = "640x360";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private static readonly object Gate = new();
    private static VideoHardwareCapabilities? _cached;
    private static string? _cachedKey;

    /// <summary>Where the probe outcome is persisted; tests point this at a scratch file or disable it with null.</summary>
    internal static string? CacheFileOverride { get; set; }

    internal static bool DiskCacheEnabled { get; set; } = true;

    public static VideoHardwareCapabilities Detect(string ffmpegPath, bool probe)
    {
        if (!probe)
        {
            return VideoHardwareCapabilities.CpuOnly;
        }

        var adapters = GpuAdapters.Read();
        var cacheKey = BuildCacheKey(ffmpegPath, adapters);

        lock (Gate)
        {
            if (_cached is not null && string.Equals(_cachedKey, cacheKey, StringComparison.Ordinal))
            {
                return _cached;
            }

            try
            {
                var fromDisk = TryReadDiskCache(cacheKey);
                if (fromDisk is not null)
                {
                    _cached = fromDisk;
                    _cachedKey = cacheKey;
                    return fromDisk;
                }

                var encoders = RunList(ffmpegPath, "-encoders");
                var hwAccels = RunList(ffmpegPath, "-hwaccels");

                var allProbesConclusive = true;
                var caps = BuildCapabilities(
                    encoders,
                    hwAccels,
                    adapters.Select(adapter => adapter.Name).ToArray(),
                    HasVendorLibrary,
                    encoder => ProbeEncoder(ffmpegPath, encoder, ref allProbesConclusive));

                _cached = caps;
                _cachedKey = cacheKey;

                // A timed-out probe says nothing about the hardware, so it must not be remembered across runs.
                if (allProbesConclusive)
                {
                    TryWriteDiskCache(cacheKey, caps);
                }

                Log.Info($"Video hardware: {Describe(caps)}");
                return caps;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to probe ffmpeg hardware capabilities: {ex.Message}");
                _cached = VideoHardwareCapabilities.CpuOnly;
                _cachedKey = cacheKey;
                return _cached;
            }
        }
    }

    /// <summary>Forgets the in-memory result so the next <see cref="Detect"/> probes (or re-reads the disk cache) again.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _cached = null;
            _cachedKey = null;
        }
    }

    /// <summary>
    /// Pure assembly of the capability record. <paramref name="encodesOnHardware"/> answers whether a real throw-away
    /// encode with the named ffmpeg encoder succeeds; it is only asked for encoders ffmpeg lists and the machine could
    /// plausibly have (matching GPU vendor), so the common case spawns very few processes.
    /// </summary>
    internal static VideoHardwareCapabilities BuildCapabilities(
        IReadOnlyCollection<string> encoderLines,
        IReadOnlyCollection<string> hwAccelLines,
        IReadOnlyList<string> adapterNames,
        Func<string, bool> hasVendorLibrary,
        Func<string, bool> encodesOnHardware)
    {
        bool Listed(string encoder) => encoderLines.Any(line => ContainsWord(line, encoder));

        var adaptersKnown = adapterNames.Count > 0;
        bool AdapterMentions(params string[] needles)
            => adapterNames.Any(name => needles.Any(needle => name.Contains(needle, StringComparison.OrdinalIgnoreCase)));

        // Cheap pre-filters so a machine without the vendor's GPU never spawns that vendor's probes. QSV is deliberately
        // not tied to a DLL in System32: modern Intel drivers (Arc, 12th gen and newer, Core Ultra) keep their oneVPL
        // runtime in the DriverStore, where a System32 lookup never finds it.
        var mayHaveNvidia = hasVendorLibrary("nvcuda.dll") || hasVendorLibrary("nvEncodeAPI64.dll") || AdapterMentions("NVIDIA", "GeForce", "Quadro");
        var mayHaveAmd = hasVendorLibrary("amfrt64.dll") || hasVendorLibrary("amfrt32.dll") || AdapterMentions("AMD", "Radeon", "ATI");
        var mayHaveIntel = !adaptersKnown || AdapterMentions("Intel");

        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        var toProbe = new List<string>();
        void Plan(string encoder, bool plausible)
        {
            if (plausible && Listed(encoder))
            {
                toProbe.Add(encoder);
            }
        }

        foreach (var codec in new[] { "h264", "hevc", "av1" })
        {
            Plan($"{codec}_nvenc", mayHaveNvidia);
            Plan($"{codec}_amf", mayHaveAmd);
            Plan($"{codec}_qsv", mayHaveIntel);
        }

        Plan("vp9_qsv", mayHaveIntel);

        foreach (var encoder in toProbe)
        {
            results[encoder] = false;
        }

        // Independent hardware units: run every probe at once.
        Parallel.ForEach(toProbe, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, toProbe.Count) }, encoder =>
        {
            var works = false;
            try
            {
                works = encodesOnHardware(encoder);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning($"Hardware encoder probe for {encoder} failed: {ex.Message}");
            }

            lock (results)
            {
                results[encoder] = works;
            }
        });

        bool Works(string encoder) => results.TryGetValue(encoder, out var ok) && ok;

        var av1Hardware = Works("av1_nvenc") || Works("av1_amf") || Works("av1_qsv");

        return new VideoHardwareCapabilities(
            SupportsNvenc: Works("h264_nvenc") || Works("hevc_nvenc") || Works("av1_nvenc"),
            SupportsAmf: Works("h264_amf") || Works("hevc_amf") || Works("av1_amf"),
            SupportsQsv: Works("h264_qsv") || Works("hevc_qsv") || Works("av1_qsv") || Works("vp9_qsv"),
            SupportsDxva: hwAccelLines.Any(line => line.Contains("d3d11", StringComparison.OrdinalIgnoreCase) || line.Contains("dxva2", StringComparison.OrdinalIgnoreCase)),
            SupportsAv1Nvenc: Works("av1_nvenc"),
            SupportsAv1Amf: Works("av1_amf"),
            SupportsAv1Qsv: Works("av1_qsv"),
            SupportsHevcNvenc: Works("hevc_nvenc"),
            SupportsHevcAmf: Works("hevc_amf"),
            SupportsHevcQsv: Works("hevc_qsv"),
            SupportsSoftwareAv1: Listed("libsvtav1"))
        {
            SupportsH264Nvenc = Works("h264_nvenc"),
            SupportsH264Amf = Works("h264_amf"),
            SupportsH264Qsv = Works("h264_qsv"),
            SupportsVp9Qsv = Works("vp9_qsv"),
            FlagshipAv1Silicon = av1Hardware && GpuAdapters.IsFlagshipAv1(adapterNames)
        };
    }

    private static bool ContainsWord(string line, string encoder)
    {
        // "av1_nvenc" must not match inside another name; lines look like " V....D av1_nvenc   NVIDIA NVENC av1 encoder".
        var index = line.IndexOf(encoder, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !IsNameChar(line[index - 1]);
            var afterIndex = index + encoder.Length;
            var after = afterIndex >= line.Length || !IsNameChar(line[afterIndex]);
            if (before && after)
            {
                return true;
            }

            index = line.IndexOf(encoder, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool ProbeEncoder(string ffmpegPath, string encoder, ref bool conclusive)
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", $"color=c=black:s={ProbeFrameSize}:r=30",
            "-frames:v", "2", "-an", "-c:v", encoder, "-f", "null", "-"
        };

        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            var result = ProcessRunner.Run(ffmpegPath, args, ProcessRunnerOptions.Create(throwOnError: false), timeout.Token);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            conclusive = false;
            Log.Warning($"Hardware encoder probe for {encoder} timed out.");
            return false;
        }
    }

    private static IReadOnlyCollection<string> RunList(string ffmpegPath, string flag)
    {
        var result = ProcessRunner.Run(ffmpegPath, new[] { "-hide_banner", flag }, ProcessRunnerOptions.Create(throwOnError: false));
        return result.ExitCode == 0 ? ParseLines(result.StandardOutput) : Array.Empty<string>();
    }

    private static string Describe(VideoHardwareCapabilities caps)
    {
        var parts = new List<string>();
        if (caps.SupportsNvenc) parts.Add("NVENC" + (caps.SupportsAv1Nvenc ? "+AV1" : string.Empty));
        if (caps.SupportsAmf) parts.Add("AMF" + (caps.SupportsAv1Amf ? "+AV1" : string.Empty));
        if (caps.SupportsQsv) parts.Add("QSV" + (caps.SupportsAv1Qsv ? "+AV1" : string.Empty));
        return parts.Count == 0 ? "no working GPU encoder" : $"{string.Join(", ", parts)} (tier {(int)caps.EncoderTier} {caps.EncoderTier})";
    }

    // ---- disk cache ---------------------------------------------------------------------------------------------

    private sealed record DiskCache(string Key, VideoHardwareCapabilities Capabilities);

    private static string? CacheFilePath()
    {
        if (!DiskCacheEnabled)
        {
            return null;
        }

        if (CacheFileOverride is not null)
        {
            return CacheFileOverride;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Squish", "hw-caps-cache.json");
    }

    private static VideoHardwareCapabilities? TryReadDiskCache(string key)
    {
        try
        {
            var path = CacheFilePath();
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<DiskCache>(File.ReadAllText(path));
            return cache is not null && string.Equals(cache.Key, key, StringComparison.Ordinal) ? cache.Capabilities : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static void TryWriteDiskCache(string key, VideoHardwareCapabilities caps)
    {
        try
        {
            var path = CacheFilePath();
            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new DiskCache(key, caps)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The cache is an optimisation only.
        }
    }

    private static string BuildCacheKey(string ffmpegPath, IReadOnlyList<GpuAdapter> adapters)
    {
        var gpus = string.Join(';', adapters.Select(adapter => $"{adapter.Name}@{adapter.DriverVersion}"));
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return $"|{gpus}";
        }

        try
        {
            var resolved = ToolLocator.ResolveOnPath(ffmpegPath) ?? ffmpegPath;
            var info = new FileInfo(resolved);
            var timestamp = info.Exists ? info.LastWriteTimeUtc.Ticks : 0L;
            return $"v2|{resolved}|{timestamp}|{gpus}";
        }
        catch
        {
            return $"v2|{ffmpegPath}|{gpus}";
        }
    }

    private static IReadOnlyCollection<string> ParseLines(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return Array.Empty<string>();
        }

        var lines = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        return new ReadOnlyCollection<string>(lines);
    }

    private static bool HasVendorLibrary(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // System32 only: the default search order would also probe the app and current directories (DLL planting).
            if (NativeLibrary.TryLoad(libraryName, typeof(VideoHardwareDetector).Assembly, DllImportSearchPath.System32, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // ignored — absence means the driver is missing.
        }

        return false;
    }
}

internal sealed record GpuAdapter(string Name, string DriverVersion);

/// <summary>Display adapter names and driver versions, read from the registry class key (no WMI, no extra packages).</summary>
internal static class GpuAdapters
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static IReadOnlyList<GpuAdapter> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<GpuAdapter>();
        }

        try
        {
            return ReadWindows();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<GpuAdapter>();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<GpuAdapter> ReadWindows()
    {
        var adapters = new List<GpuAdapter>();
        using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (classKey is null)
        {
            return adapters;
        }

        foreach (var name in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var adapterKey = classKey.OpenSubKey(name);
            var description = adapterKey?.GetValue("DriverDesc") as string;
            if (string.IsNullOrWhiteSpace(description) || description.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || description.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            adapters.Add(new GpuAdapter(description.Trim(), (adapterKey?.GetValue("DriverVersion") as string) ?? string.Empty));
        }

        return adapters;
    }

    /// <summary>
    /// Newest AV1 encoder silicon: NVIDIA Blackwell (RTX 50, RTX PRO; 9th-gen NVENC) and Intel Xe2 (Arc B-series, Lunar
    /// Lake). Only consulted once a real AV1 encode has succeeded, so a name match can never enable an encoder the
    /// hardware lacks.
    /// </summary>
    public static bool IsFlagshipAv1(IReadOnlyList<string> adapterNames)
        => adapterNames.Any(name =>
            System.Text.RegularExpressions.Regex.IsMatch(name, @"\bRTX\s*5\d{3}\b|\bRTX\s*PRO\s*[2-6]\d{3}\b|\bArc\W*(TM\W*)?(Pro\s*)?B\d{2,3}\b|Lunar Lake", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
}

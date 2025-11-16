using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Processes;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public interface IVideoMetadataProbe
{
    Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken);
}

internal sealed class FfprobeMetadataProbe : IVideoMetadataProbe
{
    private readonly VideoOptimiserOptions _options;

    public FfprobeMetadataProbe(VideoOptimiserOptions options)
    {
        _options = options;
    }

    public async Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken)
    {
        if (!_options.EnableMetadataProbe)
        {
            return null;
        }

        var args = new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_streams",
            "-show_format",
            source.Value
        };

        try
        {
            var result = await ProcessRunner.RunAsync(_options.FfprobePath, args, ProcessRunnerOptions.Create(throwOnError: false), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            return VideoProbeParser.TryParse(result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning($"ffprobe failed: {ex.Message}");
            return null;
        }
    }
}

internal static class NullVideoMetadataProbe
{
    public static IVideoMetadataProbe Instance { get; } = new NullProbe();

    private sealed class NullProbe : IVideoMetadataProbe
    {
        public Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken) => Task.FromResult<VideoProbeInfo?>(null);
    }
}

internal static class VideoProbeParser
{
    public static VideoProbeInfo? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            VideoStreamInfo? video = null;
            AudioStreamInfo? audio = null;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var typeElement) ? typeElement.GetString() : null;
                    if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase) && video is null)
                    {
                        video = ReadVideoStream(stream);
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase) && audio is null)
                    {
                        audio = ReadAudioStream(stream);
                    }
                }
            }

            if (!root.TryGetProperty("format", out var formatElement))
            {
                return new VideoProbeInfo(null, null, null, null, null, video, audio);
            }

            var duration = ParseDouble(formatElement, "duration");
            var bitRate = ParseLong(formatElement, "bit_rate");
            var size = ParseLong(formatElement, "size");
            var formatName = formatElement.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
            var longName = formatElement.TryGetProperty("format_long_name", out var fln) ? fln.GetString() : null;

            return new VideoProbeInfo(formatName, longName, duration, bitRate, size, video, audio);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to parse ffprobe json: {ex.Message}");
            return null;
        }
    }

    private static VideoStreamInfo ReadVideoStream(JsonElement stream)
    {
        var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
        var profile = stream.TryGetProperty("profile", out var profileElement) ? profileElement.GetString() : null;
        var pixFmt = stream.TryGetProperty("pix_fmt", out var pix) ? pix.GetString() : null;
        var colorSpace = stream.TryGetProperty("color_space", out var color) ? color.GetString() : null;
        var width = ParseInt(stream, "width");
        var height = ParseInt(stream, "height");
        var bitrate = ParseLong(stream, "bit_rate");
        var avgFrameRate = ParseFraction(stream.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString() : null);
        var rFrameRate = ParseFraction(stream.TryGetProperty("r_frame_rate", out var rfr) ? rfr.GetString() : null);
        var frameRate = avgFrameRate ?? rFrameRate;
        var isHdr = stream.TryGetProperty("color_transfer", out var transfer) && transfer.GetString()?.Contains("smpte", StringComparison.OrdinalIgnoreCase) == true;
        var isInterlaced = stream.TryGetProperty("field_order", out var field) && !string.Equals(field.GetString(), "progressive", StringComparison.OrdinalIgnoreCase);

        return new VideoStreamInfo(codec, profile, pixFmt, colorSpace, width, height, bitrate, frameRate, isHdr, isInterlaced);
    }

    private static AudioStreamInfo ReadAudioStream(JsonElement stream)
    {
        var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
        var profile = stream.TryGetProperty("profile", out var profileElement) ? profileElement.GetString() : null;
        var channels = ParseInt(stream, "channels");
        var sampleRate = ParseInt(stream, "sample_rate");
        var bitrate = ParseLong(stream, "bit_rate");
        return new AudioStreamInfo(codec, profile, channels, sampleRate, bitrate);
    }

    private static double? ParseFraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, out var parsed))
        {
            return parsed;
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        if (double.TryParse(parts[0], out var numerator) && double.TryParse(parts[1], out var denominator) && denominator != 0)
        {
            return numerator / denominator;
        }

        return null;
    }

    private static double? ParseDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return double.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static long? ParseLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out var parsed) ? parsed : null;
        }

        return long.TryParse(value.GetString(), out var result) ? result : null;
    }

    private static int? ParseInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt32(out var parsed) ? parsed : null;
        }

        return int.TryParse(value.GetString(), out var result) ? result : null;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace ClopWindows.Integrations.Explorer;

/// <summary>
/// Sends automation intents to the background service via the named pipe exposed by <see cref="ShortcutsBridge"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AutomationClient
{
    private const string PipeName = "clop-automation";
    private const int ConnectTimeoutMs = 4000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static AutomationClientResult Optimise(IEnumerable<string> paths, bool recursive)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containsDirectories = false;

        foreach (var candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath))
                {
                    containsDirectories = true;
                    distinctPaths.Add(fullPath);
                }
                else if (File.Exists(fullPath))
                {
                    distinctPaths.Add(fullPath);
                }
            }
            catch (Exception)
            {
                // Ignore malformed paths; Explorer generally only passes valid items.
            }
        }

        if (distinctPaths.Count == 0)
        {
            return AutomationClientResult.NoOp("No supported files were selected.");
        }

        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            client.Connect(ConnectTimeoutMs);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("Clop background service is not running.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Unable to reach the Clop automation bridge.", ex);
        }

        var payload = new AutomationOptimisePayload
        {
            Paths = distinctPaths.ToList(),
            Recursive = recursive || containsDirectories,
            Aggressive = false,
            RemoveAudio = false,
            PlaybackSpeedFactor = null,
            IncludeTypes = new List<string>(),
            ExcludeTypes = new List<string>()
        };

        var request = new AutomationRequest
        {
            Intent = AutomationIntents.Optimise,
            RequestId = Guid.NewGuid().ToString("N"),
            KeepAlive = false,
            Payload = payload
        };

        SendRequest(client, request);
        var response = ReceiveResponse(client);
        return InterpretResponse(response);
    }

    private static void SendRequest(NamedPipeClientStream client, AutomationRequest request)
    {
        var json = JsonSerializer.Serialize(request, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        client.Write(bytes, 0, bytes.Length);
        client.WaitForPipeDrain();
    }

    private static AutomationResponse ReceiveResponse(NamedPipeClientStream client)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            var read = client.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            ms.Write(buffer, 0, read);
            if (client.IsMessageComplete)
            {
                break;
            }
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());
        var response = JsonSerializer.Deserialize<AutomationResponse>(json, SerializerOptions);
        if (response is null)
        {
            throw new InvalidOperationException("The Clop automation bridge returned an empty response.");
        }

        return response;
    }

    private static AutomationClientResult InterpretResponse(AutomationResponse response)
    {
        var summary = Summarise(response.Data, out var succeeded, out var failed, out var firstFailureMessage);
        var status = response.Status?.ToLowerInvariant() ?? string.Empty;

        if (status == "ok")
        {
            return AutomationClientResult.SuccessResult(succeeded, failed, summary ?? response.Message);
        }

        if (status == "partial")
        {
            var message = summary ?? response.Message ?? firstFailureMessage ?? "Some items could not be optimised.";
            return AutomationClientResult.PartialResult(succeeded, failed, message);
        }

        var errorMessage = response.Message ?? firstFailureMessage ?? "The automation request was rejected.";
        if (!string.IsNullOrWhiteSpace(summary))
        {
            errorMessage = summary + (string.IsNullOrWhiteSpace(errorMessage) ? string.Empty : $" {errorMessage}");
        }

        return AutomationClientResult.Failure(errorMessage, succeeded, failed);
    }

    private static string? Summarise(JsonElement? data, out int succeeded, out int failed, out string? firstFailureMessage)
    {
        succeeded = 0;
        failed = 0;
        firstFailureMessage = null;

        if (data is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        if (!element.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in results.EnumerateArray())
        {
            var status = item.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                succeeded++;
            }
            else
            {
                failed++;
                if (firstFailureMessage is null && item.TryGetProperty("message", out var messageElement))
                {
                    firstFailureMessage = messageElement.GetString();
                }
            }
        }

        if (succeeded == 0 && failed == 0)
        {
            return null;
        }

        return failed == 0
            ? $"Optimised {succeeded} item(s)."
            : $"Optimised {succeeded} item(s); {failed} failed.";
    }

    private sealed record AutomationRequest
    {
        public string Intent { get; init; } = string.Empty;
        public string? RequestId { get; init; }
        public bool KeepAlive { get; init; }
        public AutomationOptimisePayload? Payload { get; init; }
    }

    private sealed record AutomationOptimisePayload
    {
        public List<string> Paths { get; init; } = new();
        public bool Recursive { get; init; }
        public bool Aggressive { get; init; }
        public bool RemoveAudio { get; init; }
        public double? PlaybackSpeedFactor { get; init; }
        public List<string> IncludeTypes { get; init; } = new();
        public List<string> ExcludeTypes { get; init; } = new();
    }

    private sealed record AutomationResponse
    {
        public string Status { get; init; } = string.Empty;
        public string? Message { get; init; }
        public JsonElement? Data { get; init; }
    }

    private static class AutomationIntents
    {
        public const string Optimise = "automation.optimise";
    }
}

internal sealed record AutomationClientResult(bool Success, bool Partial, string? Message, int Succeeded, int Failed)
{
    public static AutomationClientResult NoOp(string? message) => new(true, false, message, 0, 0);

    public static AutomationClientResult SuccessResult(int succeeded, int failed, string? message) =>
        new(true, false, message, succeeded, failed);

    public static AutomationClientResult PartialResult(int succeeded, int failed, string? message) =>
        new(false, true, message, succeeded, failed);

    public static AutomationClientResult Failure(string message, int succeeded, int failed) =>
        new(false, false, message, succeeded, failed);
}

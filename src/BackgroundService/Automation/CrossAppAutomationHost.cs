using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;
using Microsoft.Extensions.Logging;

namespace ClopWindows.BackgroundService.Automation;

[SupportedOSPlatform("windows")]
public sealed class CrossAppAutomationHost : IAsyncDisposable
{
    private const string DefaultPrefixPath = "clop";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly OptimisationCoordinator _coordinator;
    private readonly DirectoryOptimisationService _directoryOptimisations;
    private readonly ILogger<CrossAppAutomationHost> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private HttpListener? _listener;
    private Task? _listenerTask;
    private CancellationToken _runToken;
    private bool _isRunning;

    private bool _enabled;
    private bool _allowTeamsCards;
    private int _port;
    private string? _accessToken;

    public CrossAppAutomationHost(OptimisationCoordinator coordinator, DirectoryOptimisationService directoryOptimisations, ILogger<CrossAppAutomationHost> logger)
    {
        _coordinator = coordinator;
        _directoryOptimisations = directoryOptimisations;
        _logger = logger;
        SettingsHost.EnsureInitialized();
        RefreshSettings();
        SettingsHost.SettingChanged += OnSettingChanged;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        _runToken = linked.Token;
        _isRunning = true;

        TryStartListener();

        try
        {
            await Task.Delay(Timeout.Infinite, _runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }

        await StopListenerAsync().ConfigureAwait(false);
        _isRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        SettingsHost.SettingChanged -= OnSettingChanged;
        await StopListenerAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void RefreshSettings()
    {
        _enabled = SettingsHost.Get(SettingsRegistry.EnableCrossAppAutomation);
        _allowTeamsCards = SettingsHost.Get(SettingsRegistry.EnableTeamsAdaptiveCards);
        var port = SettingsHost.Get(SettingsRegistry.AutomationHttpPort);
        _port = Math.Clamp(port, 1024, 65535);
        _accessToken = SettingsHost.Get(SettingsRegistry.AutomationAccessToken);
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!IsAutomationSetting(e.Name))
        {
            return;
        }

        RefreshSettings();
        if (!_isRunning)
        {
            return;
        }

        _ = RestartListenerAsync();
    }

    private static bool IsAutomationSetting(string name) =>
        string.Equals(name, SettingsRegistry.EnableCrossAppAutomation.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.AutomationHttpPort.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.AutomationAccessToken.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.EnableTeamsAdaptiveCards.Name, StringComparison.Ordinal);

    private async Task RestartListenerAsync()
    {
        await StopListenerAsync().ConfigureAwait(false);
        TryStartListener();
    }

    private void TryStartListener()
    {
        if (!_enabled)
        {
            _logger.LogDebug("Cross-app automation host is disabled; listener not started.");
            return;
        }

        lock (_gate)
        {
            if (_listener is not null)
            {
                return;
            }

            try
            {
                var listener = new HttpListener
                {
                    IgnoreWriteExceptions = true,
                    AuthenticationSchemes = AuthenticationSchemes.Anonymous
                };

                listener.Prefixes.Add($"http://127.0.0.1:{_port}/{DefaultPrefixPath}/");
                listener.Prefixes.Add($"http://localhost:{_port}/{DefaultPrefixPath}/");

                listener.Start();
                _listener = listener;
                _listenerTask = Task.Run(() => ListenLoopAsync(listener, _runToken), CancellationToken.None);
                _logger.LogInformation("Cross-app automation host listening on http://127.0.0.1:{Port}/{Path}/", _port, DefaultPrefixPath);
            }
            catch (HttpListenerException ex)
            {
                _listener = null;
                _listenerTask = null;
                _logger.LogWarning(ex, "Failed to start cross-app automation listener on port {Port}.", _port);
            }
            catch (Exception ex)
            {
                _listener = null;
                _listenerTask = null;
                _logger.LogError(ex, "Unhandled exception starting cross-app automation host.");
            }
        }
    }

    private async Task StopListenerAsync()
    {
        HttpListener? listener;
        Task? listenerTask;

        lock (_gate)
        {
            listener = _listener;
            listenerTask = _listenerTask;
            _listener = null;
            _listenerTask = null;
        }

        if (listener is not null)
        {
            try
            {
                listener.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while stopping automation HttpListener.");
            }
        }

        if (listenerTask is not null)
        {
            try
            {
                await listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (HttpListenerException)
            {
                // listener aborted
            }
            catch (ObjectDisposedException)
            {
                // listener disposed
            }
        }
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (context is null)
            {
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        using var _ = context.Response;

        if (!context.Request.IsLocal)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, new { error = "remote_access_blocked" }).ConfigureAwait(false);
            return;
        }

        if (!Authorize(context.Request))
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.Unauthorized, new { error = "unauthorized" }).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        if (!path.StartsWith($"/{DefaultPrefixPath}/", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.NotFound, new { error = "not_found" }).ConfigureAwait(false);
            return;
        }

        path = path[$"/{DefaultPrefixPath}".Length..];

        try
        {
            if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                switch (path.ToLowerInvariant())
                {
                    case "/optimise":
                        await HandleOptimiseEndpointAsync(context.Request, context.Response, origin: "powerAutomate").ConfigureAwait(false);
                        return;
                    case "/share":
                        await HandleShareEndpointAsync(context.Request, context.Response).ConfigureAwait(false);
                        return;
                    case "/teams/card":
                        await HandleTeamsCardAsync(context.Request, context.Response).ConfigureAwait(false);
                        return;
                }
            }
            else if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && string.Equals(path, "/status", StringComparison.OrdinalIgnoreCase))
            {
                await HandleStatusEndpointAsync(context.Response).ConfigureAwait(false);
                return;
            }

            await WriteStatusAsync(context.Response, HttpStatusCode.NotFound, new { error = "route_not_found" }).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON payload received from automation endpoint {Path}.", path);
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_json", message = ex.Message }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled automation endpoint error for {Path}.", path);
            await WriteStatusAsync(context.Response, HttpStatusCode.InternalServerError, new { error = "internal_error", message = ex.Message }).ConfigureAwait(false);
        }
    }

    private bool Authorize(HttpListenerRequest request)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            return true;
        }

        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = header[7..].Trim();
        if (presented.Length == 0)
        {
            return false;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(_accessToken);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    private async Task HandleOptimiseEndpointAsync(HttpListenerRequest request, HttpListenerResponse response, string origin)
    {
        var payload = await JsonSerializer.DeserializeAsync<AutomationOptimisePayload>(request.InputStream, SerializerOptions, _runToken).ConfigureAwait(false);
        if (payload is null)
        {
            await WriteStatusAsync(response, HttpStatusCode.BadRequest, new { error = "invalid_payload" }).ConfigureAwait(false);
            return;
        }

        var summary = await RunAutomationAsync(payload, origin, null, _runToken).ConfigureAwait(false);
        await WriteStatusAsync(response, HttpStatusCode.OK, summary).ConfigureAwait(false);
    }

    private async Task HandleShareEndpointAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var payload = await JsonSerializer.DeserializeAsync<ShareAutomationPayload>(request.InputStream, SerializerOptions, _runToken).ConfigureAwait(false);
        if (payload is null)
        {
            await WriteStatusAsync(response, HttpStatusCode.BadRequest, new { error = "invalid_payload" }).ConfigureAwait(false);
            return;
        }

        var summary = await RunAutomationAsync(payload, origin: "share", payload.SourceApp, _runToken).ConfigureAwait(false);
        await WriteStatusAsync(response, HttpStatusCode.OK, summary).ConfigureAwait(false);
    }

    private async Task HandleTeamsCardAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!_allowTeamsCards)
        {
            await WriteStatusAsync(response, HttpStatusCode.Forbidden, new { error = "teams_cards_disabled" }).ConfigureAwait(false);
            return;
        }

        var payload = await JsonSerializer.DeserializeAsync<TeamsCardRequest>(request.InputStream, SerializerOptions, _runToken).ConfigureAwait(false);
        if (payload is null || payload.Results.Count == 0)
        {
            await WriteStatusAsync(response, HttpStatusCode.BadRequest, new { error = "invalid_payload" }).ConfigureAwait(false);
            return;
        }

        var card = TeamsAdaptiveCardFactory.Create(payload, payload.Results, payload.Title);
        await WriteStatusAsync(response, HttpStatusCode.OK, new { card }).ConfigureAwait(false);
    }

    private Task HandleStatusEndpointAsync(HttpListenerResponse response)
    {
        var status = new
        {
            enabled = _enabled,
            port = _port,
            teamsCards = _allowTeamsCards,
            requiresToken = !string.IsNullOrWhiteSpace(_accessToken)
        };

        return WriteStatusAsync(response, HttpStatusCode.OK, status);
    }

    private async Task<object> RunAutomationAsync(AutomationOptimisePayload payload, string origin, string? originIdentifier, CancellationToken token)
    {
        var targets = AutomationTargetResolver.ResolveTargets(payload, _logger);
        if (targets.Count == 0)
        {
            return new { status = "not_found", results = Array.Empty<object>() };
        }

        var metadataBase = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "automation",
            ["automation.origin"] = origin
        };

        if (!string.IsNullOrWhiteSpace(originIdentifier))
        {
            metadataBase["automation.originId"] = originIdentifier;
        }

        if (payload.Aggressive)
        {
            metadataBase["automation.aggressive"] = true;
        }

        if (payload.RemoveAudio)
        {
            metadataBase["automation.removeAudio"] = true;
        }

        if (payload.PlaybackSpeedFactor.HasValue)
        {
            metadataBase["automation.playbackSpeed"] = payload.PlaybackSpeedFactor.Value;
        }

        var tickets = new List<(OptimisationTicket Ticket, AutomationTarget Target)>(targets.Count);
        foreach (var target in targets)
        {
            var metadata = new Dictionary<string, object?>(metadataBase, StringComparer.OrdinalIgnoreCase)
            {
                ["automation.targetType"] = target.Type.ToString()
            };

            var request = new OptimisationRequest(target.Type, target.Path, metadata: metadata);
            tickets.Add((_coordinator.Enqueue(request, token), target));
        }

        if (tickets.Count == 0)
        {
            return new { status = "unsupported", results = Array.Empty<object>() };
        }

        var completions = await Task.WhenAll(tickets.Select(t => t.Ticket.Completion)).ConfigureAwait(false);
        var results = new List<AutomationOptimiseResultPayload>(completions.Length);
        var failures = 0;

        for (var i = 0; i < completions.Length; i++)
        {
            var result = completions[i];
            var target = tickets[i].Target;

            if (result.Status == OptimisationStatus.Succeeded)
            {
                _directoryOptimisations.RegisterExternalOptimisation(target.Path);
            }
            else
            {
                failures++;
            }

            results.Add(ConvertResult(result, target));
        }

        var outcome = failures == 0 ? "ok" : failures == tickets.Count ? "failed" : "partial";
        return new { status = outcome, results };
    }

    private static AutomationOptimiseResultPayload ConvertResult(OptimisationResult result, AutomationTarget target)
    {
        FilePath? output = result.OutputPath;
        var directory = output?.Parent.Value;

        return new AutomationOptimiseResultPayload(
            result.RequestId,
            target.Path.Value,
            output?.Value,
            result.Status.ToString(),
            result.Message,
            directory);
    }

    private static Task WriteStatusAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return response.OutputStream.WriteAsync(bytes.AsMemory(), CancellationToken.None).AsTask();
    }

    private sealed record ShareAutomationPayload : AutomationOptimisePayload
    {
        public string? SourceApp { get; init; }
    }

    private sealed record TeamsCardRequest
    {
        public string? Title { get; init; }
        public List<AutomationOptimiseResultPayload> Results { get; init; } = new();
    }

    private sealed record AutomationOptimiseResultPayload(string RequestId, string SourcePath, string? OutputPath, string Status, string? Message, string? Directory);

    private static class TeamsAdaptiveCardFactory
    {
        public static object Create(TeamsCardRequest request, IReadOnlyCollection<AutomationOptimiseResultPayload> results, string? title)
        {
            var cardTitle = string.IsNullOrWhiteSpace(title) ? "Clop Optimisation Summary" : title;
            var body = new List<object>
            {
                new { type = "TextBlock", size = "Medium", weight = "Bolder", text = cardTitle },
                new { type = "TextBlock", isSubtle = true, spacing = "Small", text = $"Processed {results.Count} file(s)" }
            };

            foreach (var result in results)
            {
                var statusSymbol = result.Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) ? "✅" : "⚠️";
                body.Add(new
                {
                    type = "TextBlock",
                    wrap = true,
                    text = $"{statusSymbol} {Path.GetFileName(result.SourcePath)} — {result.Status}" + (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" ({result.Message})")
                });
            }

            var actions = new List<object>();
            var firstOutput = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.OutputPath));
            if (firstOutput is not null && !string.IsNullOrWhiteSpace(firstOutput.OutputPath))
            {
                var uri = BuildFileUri(firstOutput.OutputPath!);
                if (uri is not null)
                {
                    actions.Add(new { type = "Action.OpenUrl", title = "Open Output", url = uri });
                }
            }

            var card = new Dictionary<string, object?>
            {
                ["type"] = "AdaptiveCard",
                ["version"] = "1.5",
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["body"] = body
            };

            if (actions.Count > 0)
            {
                card["actions"] = actions;
            }

            return card;
        }

        private static string? BuildFileUri(string path)
        {
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var existing) && existing.IsAbsoluteUri)
                {
                    return existing.AbsoluteUri;
                }

                var builder = new UriBuilder
                {
                    Scheme = Uri.UriSchemeFile,
                    Host = string.Empty,
                    Path = path
                };

                return builder.Uri.AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }
    }
}

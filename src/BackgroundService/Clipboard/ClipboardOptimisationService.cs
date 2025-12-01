using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.BackgroundService.Automation;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;
using Microsoft.Extensions.Logging;
using WinFormsClipboard = System.Windows.Forms.Clipboard;
using DataFormats = System.Windows.Forms.DataFormats;

namespace ClopWindows.BackgroundService.Clipboard;

[SupportedOSPlatform("windows")]
public sealed class ClipboardOptimisationService : IAsyncDisposable
{
    private readonly OptimisationCoordinator _coordinator;
    private readonly ClipboardMonitor _monitor;
    private readonly OptimisedFileRegistry _optimisedFiles;
    private readonly ILogger<ClipboardOptimisationService> _logger;
    private readonly ConcurrentDictionary<string, ClipboardItem> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlightPaths = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private readonly object _settingsGate = new();

    private bool _monitorRunning;
    private volatile bool _enableClipboard;
    private volatile bool _paused;
    private volatile bool _autoCopy;
    private volatile bool _copyImageFilePath;
    private volatile bool _useCustomTemplate;
    private volatile bool _optimiseVideoClipboard;
    private volatile bool _optimisePdfClipboard;
    private volatile bool _optimiseClipboardFileDrops;
    private volatile bool _optimiseImagePathClipboard;
    private string _customTemplate = string.Empty;
    private int _suppressNextNotification;

    public ClipboardOptimisationService(
        OptimisationCoordinator coordinator,
        ClipboardMonitor monitor,
        OptimisedFileRegistry optimisedFiles,
        ILogger<ClipboardOptimisationService> logger)
    {
        _coordinator = coordinator;
        _monitor = monitor;
        _optimisedFiles = optimisedFiles;
        _logger = logger;
        SettingsHost.EnsureInitialized();
        RefreshSettings(updateMonitor: false);

        _monitor.ClipboardChanged += HandleClipboardChanged;
        _coordinator.RequestCompleted += OnCoordinatorRequestCompleted;
        _coordinator.RequestFailed += OnCoordinatorRequestFailed;
        SettingsHost.SettingChanged += OnSettingChanged;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        UpdateMonitorState();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        lock (_stateGate)
        {
            if (_monitorRunning)
            {
                _monitor.Stop();
                _monitorRunning = false;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        SettingsHost.SettingChanged -= OnSettingChanged;
        _monitor.ClipboardChanged -= HandleClipboardChanged;
        _coordinator.RequestCompleted -= OnCoordinatorRequestCompleted;
        _coordinator.RequestFailed -= OnCoordinatorRequestFailed;

        lock (_stateGate)
        {
            if (_monitorRunning)
            {
                _monitor.Stop();
                _monitorRunning = false;
            }
        }

        return ValueTask.CompletedTask;
    }

    private void HandleClipboardChanged(object? sender, ClipboardSnapshot snapshot)
    {
        _ = Task.Run(() => ProcessSnapshotSafe(snapshot));
    }

    private void ProcessSnapshotSafe(ClipboardSnapshot snapshot)
    {
        try
        {
            ProcessSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing clipboard snapshot.");
        }
    }

    private void OnCoordinatorRequestCompleted(object? sender, OptimisationCompletedEventArgs e)
    {
        _ = HandleCompletionAsync(e.Result);
    }

    private void OnCoordinatorRequestFailed(object? sender, OptimisationCompletedEventArgs e)
    {
        _ = HandleCompletionAsync(e.Result);
    }

    private async Task HandleCompletionAsync(OptimisationResult result)
    {
        if (!_pending.TryRemove(result.RequestId, out var item))
        {
            _logger.LogDebug("No pending clipboard context found for request {RequestId}", result.RequestId);
            return;
        }

        _inFlightPaths.TryRemove(item.SourcePath.Value, out _);

        try
        {
            var sourcePath = item.SourcePath;
            var finalPath = sourcePath;

            if (result.Status == OptimisationStatus.Succeeded)
            {
                var output = result.OutputPath ?? sourcePath;
                finalPath = output;

                if (item.IsImage && item.Origin == ClipboardOrigin.Bitmap && _copyImageFilePath && _useCustomTemplate)
                {
                    finalPath = RenameOptimisedClipboardImage(output);
                }

                if (_autoCopy)
                {
                    if (item.IsImage)
                    {
                        await CopyImageToClipboardAsync(finalPath).ConfigureAwait(false);
                    }
                    else if (item.IsVideo || item.IsPdf)
                    {
                        await CopyFileToClipboardAsync(finalPath).ConfigureAwait(false);
                    }
                }

                RegisterOptimisedResult(finalPath);

                if (item.IsTemporary && !PathsEqual(sourcePath, finalPath))
                {
                    TryDeleteFile(sourcePath);
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    _logger.LogWarning("Clipboard optimisation {RequestId} finished with status {Status}: {Message}", result.RequestId, result.Status, result.Message);
                }
                else
                {
                    _logger.LogWarning("Clipboard optimisation {RequestId} finished with status {Status}", result.RequestId, result.Status);
                }

                if (item.IsTemporary)
                {
                    TryDeleteFile(sourcePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed finalising clipboard optimisation {RequestId}", result.RequestId);
        }
    }

    private void ProcessSnapshot(ClipboardSnapshot snapshot)
    {
        if (!_enableClipboard)
        {
            return;
        }

        if (_paused)
        {
            _logger.LogTrace("Clipboard watcher paused; ignoring clipboard change.");
            return;
        }

        if (Interlocked.Exchange(ref _suppressNextNotification, 0) == 1)
        {
            _logger.LogTrace("Clipboard snapshot suppressed by internal flag.");
            return;
        }

        if (!snapshot.HasContent)
        {
            return;
        }

        if (snapshot.HasOptimisationMarker)
        {
            _logger.LogTrace("Skipping clipboard entry tagged with Clop marker.");
            return;
        }

        var items = BuildItems(snapshot);
        if (items.Count == 0)
        {
            _logger.LogTrace("Clipboard change did not contain optimisable content.");
            return;
        }

        foreach (var item in items)
        {
            EnqueueItem(item);
        }
    }

    private List<ClipboardItem> BuildItems(ClipboardSnapshot snapshot)
    {
        var results = new List<ClipboardItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_optimiseClipboardFileDrops)
        {
            foreach (var rawPath in snapshot.FilePaths)
            {
                var pathText = rawPath.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(pathText))
                {
                    continue;
                }

                if (!File.Exists(pathText))
                {
                    continue;
                }

                if (!seen.Add(pathText))
                {
                    continue;
                }

                var filePath = FilePath.From(pathText);
                if (ShouldSkipSourceFile(filePath))
                {
                    continue;
                }

                if (MediaFormats.IsImage(filePath))
                {
                    results.Add(new ClipboardItem(filePath, ItemType.Image, ClipboardOrigin.FileDrop, false, true));
                }
                else if (MediaFormats.IsVideo(filePath))
                {
                    if (_optimiseVideoClipboard)
                    {
                        results.Add(new ClipboardItem(filePath, ItemType.Video, ClipboardOrigin.FileDrop, false, false));
                    }
                }
                else if (MediaFormats.IsPdf(filePath))
                {
                    if (_optimisePdfClipboard)
                    {
                        results.Add(new ClipboardItem(filePath, ItemType.Pdf, ClipboardOrigin.FileDrop, false, false));
                    }
                }
            }
        }

        if (snapshot.ImageBytes is { Length: > 0 } bytes)
        {
            var tempPath = PersistClipboardImage(bytes);
            if (tempPath is not null)
            {
                results.Add(new ClipboardItem(tempPath.Value, ItemType.Image, ClipboardOrigin.Bitmap, true, true));
            }
        }

        if (_optimiseImagePathClipboard && !string.IsNullOrWhiteSpace(snapshot.Text))
        {
            var candidates = snapshot.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Trim('\"'))
                .Where(line => !string.IsNullOrWhiteSpace(line));

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate) || !seen.Add(candidate))
                {
                    continue;
                }

                var filePath = FilePath.From(candidate);
                if (ShouldSkipSourceFile(filePath))
                {
                    continue;
                }

                if (MediaFormats.IsImage(filePath))
                {
                    results.Add(new ClipboardItem(filePath, ItemType.Image, ClipboardOrigin.TextPath, false, true));
                    continue;
                }

                if (_optimisePdfClipboard && MediaFormats.IsPdf(filePath))
                {
                    results.Add(new ClipboardItem(filePath, ItemType.Pdf, ClipboardOrigin.TextPath, false, false));
                    continue;
                }

                if (_optimiseVideoClipboard && MediaFormats.IsVideo(filePath))
                {
                    results.Add(new ClipboardItem(filePath, ItemType.Video, ClipboardOrigin.TextPath, false, false));
                }
            }
        }

        return results;
    }

    private bool ShouldSkipSourceFile(FilePath filePath)
    {
        if (ClopFileGuards.IsClopGenerated(filePath))
        {
            _logger.LogTrace("Skipping clipboard source {Path}; detected Clop output.", filePath.Value);
            return true;
        }

        if (_optimisedFiles.WasPathRecentlyOptimised(filePath))
        {
            _logger.LogTrace("Skipping clipboard source {Path}; recently optimised.", filePath.Value);
            return true;
        }

        return false;
    }

    private FilePath? PersistClipboardImage(byte[] bytes)
    {
        try
        {
            var directory = FilePath.Images;
            directory.EnsurePathExists();
            var fileName = $"clipboard-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{NanoId.New(6)}.png";
            var path = directory.Append(fileName);
            File.WriteAllBytes(path.Value, bytes);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist clipboard image for optimisation.");
            return null;
        }
    }

    private void EnqueueItem(ClipboardItem item)
    {
        if (!_inFlightPaths.TryAdd(item.SourcePath.Value, 0))
        {
            _logger.LogTrace("Clipboard item {Path} is already queued; skipping", item.SourcePath.Value);
            if (item.IsTemporary)
            {
                TryDeleteFile(item.SourcePath);
            }
            return;
        }

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = "clipboard",
            ["clipboard.origin"] = item.Origin.ToString()
        };

        OutputBehaviourSettings.ApplyTo(metadata);

        var request = new OptimisationRequest(item.ItemType, item.SourcePath, metadata: metadata);
        _pending[request.RequestId] = item;
        var ticket = _coordinator.Enqueue(request);

        ticket.Completion.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private FilePath RenameOptimisedClipboardImage(FilePath currentPath)
    {
        string template;
        lock (_settingsGate)
        {
            template = _customTemplate;
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            return currentPath;
        }

        try
        {
            int autoNumber;
            lock (_settingsGate)
            {
                autoNumber = SettingsHost.Get(SettingsRegistry.LastAutoIncrementingNumber);
            }

            var generated = FilePathGenerator.Generate(template, currentPath, ref autoNumber, createDirectories: true);
            if (generated is null)
            {
                _logger.LogWarning("Clipboard naming template '{Template}' produced an invalid path; keeping {Path}", template, currentPath.Value);
                return currentPath;
            }

            var target = generated.Value;
            if (string.IsNullOrWhiteSpace(target.Extension))
            {
                var extension = currentPath.Extension ?? "png";
                target = target.WithExtension(extension);
            }

            target = EnsureUniquePath(target);

            if (!string.Equals(target.Value, currentPath.Value, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(target.Parent.Value);
                File.Move(currentPath.Value, target.Value, overwrite: false);
                currentPath = target;
            }

            SettingsHost.Set(SettingsRegistry.LastAutoIncrementingNumber, autoNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply clipboard naming template to {Path}", currentPath.Value);
        }

        return currentPath;
    }

    private async Task CopyImageToClipboardAsync(FilePath path)
    {
        Interlocked.Exchange(ref _suppressNextNotification, 1);

        try
        {
            // Preload image bytes outside STA thread to keep operations fast.
            byte[] imageBytes = await File.ReadAllBytesAsync(path.Value).ConfigureAwait(false);

            await _monitor.RunOnStaAsync(async () =>
            {
                const int maxAttempts = 3;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        using var bitmapStream = new MemoryStream(imageBytes, writable: false);
                        using var bitmap = new System.Drawing.Bitmap(bitmapStream);
                        var data = new System.Windows.Forms.DataObject();
                        data.SetData(DataFormats.Bitmap, true, bitmap);
                        if (_copyImageFilePath)
                        {
                            var files = new StringCollection { path.Value };
                            data.SetFileDropList(files);
                            data.SetData(DataFormats.Text, path.Value);
                        }
                        data.SetData(ClipboardFormats.OptimisationStatus, "true");

                        WinFormsClipboard.SetDataObject(data, true);
                        return;
                    }
                    catch (System.Runtime.InteropServices.ExternalException) when (attempt < maxAttempts)
                    {
                        await Task.Delay(50).ConfigureAwait(true);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy optimised clipboard image {Path} to clipboard", path.Value);
        }
    }

    private async Task CopyFileToClipboardAsync(FilePath path)
    {
        Interlocked.Exchange(ref _suppressNextNotification, 1);

        try
        {
            var populated = new StringCollection { path.Value };

            await _monitor.RunOnStaAsync(async () =>
            {
                const int maxAttempts = 3;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        var data = new System.Windows.Forms.DataObject();
                        data.SetFileDropList(populated);
                        data.SetData(DataFormats.Text, path.Value);
                        data.SetData(ClipboardFormats.OptimisationStatus, "true");

                        WinFormsClipboard.SetDataObject(data, true);
                        return;
                    }
                    catch (System.Runtime.InteropServices.ExternalException) when (attempt < maxAttempts)
                    {
                        await Task.Delay(50).ConfigureAwait(true);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy optimised clipboard file {Path} to clipboard", path.Value);
        }
    }

    private static FilePath EnsureUniquePath(FilePath desired)
    {
        if (!File.Exists(desired.Value))
        {
            return desired;
        }

        var directory = desired.Parent;
        var stem = desired.Stem;
        var extension = desired.Extension;
        var counter = 2;

        while (true)
        {
            var fileName = extension is null
                ? $"{stem}-{counter}"
                : $"{stem}-{counter}.{extension}";
            var candidate = directory.Append(fileName);
            if (!File.Exists(candidate.Value))
            {
                return candidate;
            }
            counter++;
        }
    }

    private static void TryDeleteFile(FilePath path)
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
            // Best effort cleanup
        }
    }

    private void RegisterOptimisedResult(FilePath path)
    {
        if (!path.Exists)
        {
            return;
        }

        if (_optimisedFiles.TryRegisterFingerprint(path))
        {
            return;
        }

        _optimisedFiles.RegisterPath(path);
    }

    private static bool PathsEqual(FilePath left, FilePath right) => string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!IsClipboardSetting(e.Name))
        {
            return;
        }

        RefreshSettings(updateMonitor: true);
    }

    private bool IsClipboardSetting(string name)
    {
        return string.Equals(name, SettingsRegistry.EnableClipboardOptimiser.Name, StringComparison.Ordinal) ||
              string.Equals(name, SettingsRegistry.PauseAutomaticOptimisations.Name, StringComparison.Ordinal) ||
              string.Equals(name, SettingsRegistry.AutoCopyToClipboard.Name, StringComparison.Ordinal) ||
              string.Equals(name, SettingsRegistry.CopyImageFilePath.Name, StringComparison.Ordinal) ||
              string.Equals(name, SettingsRegistry.UseCustomNameTemplateForClipboardImages.Name, StringComparison.Ordinal) ||
              string.Equals(name, SettingsRegistry.CustomNameTemplateForClipboardImages.Name, StringComparison.Ordinal) ||
              string.Equals(name, SettingsRegistry.OptimiseVideoClipboard.Name, StringComparison.Ordinal) ||
            string.Equals(name, SettingsRegistry.OptimiseClipboardFileDrops.Name, StringComparison.Ordinal) ||
            string.Equals(name, SettingsRegistry.OptimisePdfClipboard.Name, StringComparison.Ordinal) ||
              string.Equals(name, SettingsRegistry.OptimiseImagePathClipboard.Name, StringComparison.Ordinal);
    }

    private void RefreshSettings(bool updateMonitor)
    {
        lock (_settingsGate)
        {
            _enableClipboard = SettingsHost.Get(SettingsRegistry.EnableClipboardOptimiser);
            _paused = SettingsHost.Get(SettingsRegistry.PauseAutomaticOptimisations);
            _autoCopy = SettingsHost.Get(SettingsRegistry.AutoCopyToClipboard);
            _copyImageFilePath = SettingsHost.Get(SettingsRegistry.CopyImageFilePath);
            _useCustomTemplate = SettingsHost.Get(SettingsRegistry.UseCustomNameTemplateForClipboardImages);
            _customTemplate = SettingsHost.Get(SettingsRegistry.CustomNameTemplateForClipboardImages) ?? string.Empty;
            _optimiseVideoClipboard = SettingsHost.Get(SettingsRegistry.OptimiseVideoClipboard);
            _optimisePdfClipboard = SettingsHost.Get(SettingsRegistry.OptimisePdfClipboard);
            _optimiseClipboardFileDrops = SettingsHost.Get(SettingsRegistry.OptimiseClipboardFileDrops);
            _optimiseImagePathClipboard = SettingsHost.Get(SettingsRegistry.OptimiseImagePathClipboard);
        }

        if (updateMonitor)
        {
            UpdateMonitorState();
        }
    }

    private void UpdateMonitorState()
    {
        lock (_stateGate)
        {
            var shouldRun = _enableClipboard && !_paused;
            if (shouldRun && !_monitorRunning)
            {
                _monitor.Start();
                _monitorRunning = true;
                _logger.LogInformation("Clipboard watcher started.");
            }
            else if (!shouldRun && _monitorRunning)
            {
                _monitor.Stop();
                _monitorRunning = false;
                _logger.LogInformation("Clipboard watcher stopped.");
            }
        }
    }
}

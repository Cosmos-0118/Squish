using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Channels;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Squish.BackgroundService.Automation;
using Squish.Core.Optimizers;
using Squish.Core.Settings;
using Squish.Core.Shared;
using Microsoft.Extensions.Logging;
using WinFormsClipboard = System.Windows.Forms.Clipboard;
using DataFormats = System.Windows.Forms.DataFormats;

namespace Squish.BackgroundService.Clipboard;

[SupportedOSPlatform("windows")]
public sealed class ClipboardOptimisationService : IAsyncDisposable
{
    private readonly OptimisationCoordinator _coordinator;
    private readonly ClipboardMonitor _monitor;
    private readonly OptimisedFileRegistry _optimisedFiles;
    private readonly ILogger<ClipboardOptimisationService> _logger;
    private readonly ConcurrentDictionary<string, ClipboardItem> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlightPaths = new(StringComparer.Ordinal);
    // origin snapshot sequence -> sequence produced by our latest write for it (several files from one copy)
    private readonly ConcurrentDictionary<uint, uint> _ownWrites = new();
    private readonly object _stateGate = new();
    private readonly object _settingsGate = new();
    // Snapshots are handled one at a time, in the order the user copied them. If processing ever falls behind, the
    // oldest are dropped: only the most recent clipboard contents are interesting.
    private volatile Channel<ClipboardSnapshot>? _snapshots;

    private bool _monitorRunning;
    private volatile bool _enableClipboard;
    private volatile bool _paused;
    private volatile bool _autoCopy;
    private volatile bool _copyImageFilePath;
    private volatile bool _useCustomTemplate;
    private volatile bool _optimiseVideoClipboard;
    private volatile bool _optimiseClipboardFileDrops;
    private volatile bool _optimiseImagePathClipboard;
    private string _customTemplate = string.Empty;

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
        SettingsHost.SettingChanged += OnSettingChanged;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<ClipboardSnapshot>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
        _snapshots = channel;

        UpdateMonitorState();
        var consumer = Task.Run(() => ConsumeSnapshotsAsync(channel.Reader, cancellationToken), CancellationToken.None);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _snapshots = null;
            channel.Writer.TryComplete();

            lock (_stateGate)
            {
                if (_monitorRunning)
                {
                    _monitor.Stop();
                    _monitorRunning = false;
                }
            }

            try
            {
                await consumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        SettingsHost.SettingChanged -= OnSettingChanged;
        _monitor.ClipboardChanged -= HandleClipboardChanged;

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
        // Runs on the monitor's message-loop thread: hand off immediately.
        _snapshots?.Writer.TryWrite(snapshot);
    }

    private async Task ConsumeSnapshotsAsync(ChannelReader<ClipboardSnapshot> reader, CancellationToken token)
    {
        await foreach (var snapshot in reader.ReadAllAsync(token).ConfigureAwait(false))
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

                if (_autoCopy && !CanWriteBack(item))
                {
                    _logger.LogDebug("Clipboard changed while {Path} was being optimised; leaving the newer clipboard contents alone.", sourcePath.Value);
                }
                else if (_autoCopy)
                {
                    if (item.IsImage)
                    {
                        await CopyImageToClipboardAsync(finalPath, item).ConfigureAwait(false);
                    }
                    else if (item.IsVideo)
                    {
                        await CopyFileToClipboardAsync(finalPath, item).ConfigureAwait(false);
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

    /// <summary>
    /// Optimising can take seconds. If the user copied something else meanwhile, writing our result back would
    /// silently replace what they just copied.
    /// </summary>
    private bool CanWriteBack(ClipboardItem item)
    {
        if (item.OriginSequence == 0)
        {
            return true;
        }

        var current = _monitor.CurrentSequence;
        return current == item.OriginSequence ||
               (_ownWrites.TryGetValue(item.OriginSequence, out var ownSequence) && current == ownSequence);
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

        if (!snapshot.HasContent)
        {
            return;
        }

        if (snapshot.HasOptimisationMarker)
        {
            _logger.LogTrace("Skipping clipboard entry tagged with Squish marker.");
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
                    results.Add(new ClipboardItem(filePath, ItemType.Image, ClipboardOrigin.FileDrop, false, true, snapshot.Sequence));
                }
                else if (MediaFormats.IsVideo(filePath))
                {
                    if (_optimiseVideoClipboard)
                    {
                        results.Add(new ClipboardItem(filePath, ItemType.Video, ClipboardOrigin.FileDrop, false, false, snapshot.Sequence));
                    }
                }
            }
        }

        // Apps that copy an image file also publish a preview bitmap of it; optimising both would do the work twice.
        if (results.Count == 0 && snapshot.ImageBytes is { Length: > 0 } bytes)
        {
            var tempPath = PersistClipboardImage(bytes);
            if (tempPath is not null)
            {
                results.Add(new ClipboardItem(tempPath.Value, ItemType.Image, ClipboardOrigin.Bitmap, true, true, snapshot.Sequence));
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
                    results.Add(new ClipboardItem(filePath, ItemType.Image, ClipboardOrigin.TextPath, false, true, snapshot.Sequence));
                    continue;
                }

                if (_optimiseVideoClipboard && MediaFormats.IsVideo(filePath))
                {
                    results.Add(new ClipboardItem(filePath, ItemType.Video, ClipboardOrigin.TextPath, false, false, snapshot.Sequence));
                }
            }
        }

        return results;
    }

    private bool ShouldSkipSourceFile(FilePath filePath)
    {
        if (SquishFileGuards.IsSquishGenerated(filePath))
        {
            _logger.LogTrace("Skipping clipboard source {Path}; detected Squish output.", filePath.Value);
            return true;
        }

        if (SquishOptimisationMarker.HasValidMarker(filePath))
        {
            _logger.LogTrace("Skipping clipboard source {Path}; optimisation marker present.", filePath.Value);
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
        VideoEncoderPresetSettings.ApplyTo(metadata);
        AggressiveOptimisationHelper.Apply(item.ItemType, item.SourcePath, metadata);

        var request = new OptimisationRequest(item.ItemType, item.SourcePath, metadata: metadata);
        var requestId = request.RequestId;
        _pending[requestId] = item;

        OptimisationTicket ticket;
        try
        {
            ticket = _coordinator.Enqueue(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue clipboard optimisation for {Path}.", item.SourcePath.Value);
            _ = HandleCompletionAsync(OptimisationResult.Failure(requestId, ex.Message));
            return;
        }

        // The ticket is the single source of truth: it completes for success, failure, cancellation and the
        // "could not queue" path alike, so the in-flight bookkeeping cannot leak. HandleCompletionAsync is idempotent.
        _ = ticket.Completion.ContinueWith(
            static (task, state) =>
            {
                var (service, id) = ((ClipboardOptimisationService, string))state!;
                var result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : task.IsCanceled ? OptimisationResult.Cancelled(id) : OptimisationResult.Failure(id, task.Exception?.GetBaseException().Message);
                return service.HandleCompletionAsync(result);
            },
            (this, requestId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

    private async Task CopyImageToClipboardAsync(FilePath path, ClipboardItem item)
    {
        System.Drawing.Bitmap? bitmap = null;
        MemoryStream? bitmapStream = null;

        try
        {
            // Read the bytes before touching the STA thread so the clipboard thread only does clipboard work.
            var imageBytes = await File.ReadAllBytesAsync(path.Value).ConfigureAwait(false);
            var copyPath = _copyImageFilePath;

            var sequence = await _monitor.WriteAsync(
                () =>
                {
                    bitmapStream = new MemoryStream(imageBytes, writable: false);
                    bitmap = new System.Drawing.Bitmap(bitmapStream);
                    var data = new System.Windows.Forms.DataObject();
                    data.SetData(DataFormats.Bitmap, true, bitmap);
                    if (copyPath)
                    {
                        data.SetFileDropList(new StringCollection { path.Value });
                        data.SetData(DataFormats.Text, path.Value);
                    }

                    data.SetData(ClipboardFormats.OptimisationStatus, "true");
                    return data;
                },
                afterWrite: () =>
                {
                    bitmap?.Dispose();
                    bitmapStream?.Dispose();
                }).ConfigureAwait(false);

            RememberOwnWrite(item, sequence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy optimised clipboard image {Path} to clipboard", path.Value);
        }
    }

    private async Task CopyFileToClipboardAsync(FilePath path, ClipboardItem item)
    {
        try
        {
            var sequence = await _monitor.WriteAsync(() =>
            {
                var data = new System.Windows.Forms.DataObject();
                data.SetFileDropList(new StringCollection { path.Value });
                data.SetData(DataFormats.Text, path.Value);
                data.SetData(ClipboardFormats.OptimisationStatus, "true");
                return data;
            }).ConfigureAwait(false);

            RememberOwnWrite(item, sequence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy optimised clipboard file {Path} to clipboard", path.Value);
        }
    }

    private void RememberOwnWrite(ClipboardItem item, uint sequence)
    {
        if (item.OriginSequence == 0)
        {
            return;
        }

        if (_ownWrites.Count > 64)
        {
            _ownWrites.Clear();
        }

        _ownWrites[item.OriginSequence] = sequence;
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
            _optimiseClipboardFileDrops = SettingsHost.Get(SettingsRegistry.OptimiseClipboardFileDrops);
            _optimiseImagePathClipboard = SettingsHost.Get(SettingsRegistry.OptimiseImagePathClipboard);

            // Only read what we will use: decoding a large text blob is wasted work otherwise.
            _monitor.CaptureOptions = new ClipboardCaptureOptions(
                Files: _optimiseClipboardFileDrops,
                Image: true,
                Text: _optimiseImagePathClipboard);
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

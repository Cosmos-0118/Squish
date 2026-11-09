using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Settings;
using Squish.Core.Shared;
using Microsoft.Extensions.Logging;

namespace Squish.BackgroundService;

public sealed class WorkdirCleanupService : IAsyncDisposable
{
    private readonly ILogger<WorkdirCleanupService> _logger;

    private readonly object _settingsGate = new();
    private readonly SemaphoreSlim _intervalChanged = new(0, 1);
    private CleanupInterval _interval;

    public WorkdirCleanupService(ILogger<WorkdirCleanupService> logger)
    {
        _logger = logger;
        SettingsHost.EnsureInitialized();
        _interval = SettingsHost.Get(SettingsRegistry.WorkdirCleanupInterval);
        SettingsHost.SettingChanged += OnSettingChanged;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Scratch directories of finished or crashed jobs are not user data and are swept on every cycle (and so once at
            // start-up) whatever the workdir interval is; only directories of dead processes, or hours old, are touched.
            await SweepTempRunsAsync(cancellationToken).ConfigureAwait(false);

            var interval = GetInterval();
            TimeSpan wait;

            if (interval == CleanupInterval.Never)
            {
                wait = TimeSpan.FromMinutes(5);
            }
            else
            {
                try
                {
                    // Directory walking is blocking I/O: keep it off the caller's thread.
                    await Task.Run(() => CleanupStaleWorkdirArtifacts(interval), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Workdir cleanup cycle failed.");
                }

                wait = TimeSpan.FromSeconds((int)interval);
            }

            try
            {
                // Wakes early when the interval setting changes, so switching from "monthly" to "hourly" takes effect now.
                await _intervalChanged.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepTempRunsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await Task.Run(() => TempRuns.SweepStale(), cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("Temp sweep removed {Count} abandoned scratch entries.", removed);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the loop's own cancellation check ends the run.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Temp sweep failed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        SettingsHost.SettingChanged -= OnSettingChanged;
        return ValueTask.CompletedTask;
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!string.Equals(e.Name, SettingsRegistry.WorkdirCleanupInterval.Name, StringComparison.Ordinal))
        {
            return;
        }

        lock (_settingsGate)
        {
            _interval = SettingsHost.Get(SettingsRegistry.WorkdirCleanupInterval);
        }

        try
        {
            _intervalChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // a wake-up is already pending
        }
    }

    private CleanupInterval GetInterval()
    {
        lock (_settingsGate)
        {
            return _interval;
        }
    }

    private void CleanupStaleWorkdirArtifacts(CleanupInterval interval)
    {
        var root = SquishPaths.WorkRoot;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds((int)interval);

        var folders = new[]
        {
            root.Append("images"),
            root.Append("conversions"),
            root.Append("for-resize"),
            root.Append("for-filters"),
            root.Append("segmentation-cache")
        };

        var deletedFiles = 0;
        var deletedDirectories = 0;

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Value))
            {
                continue;
            }

            deletedFiles += DeleteStaleFiles(folder.Value, cutoff);
            deletedDirectories += DeleteStaleDirectories(folder.Value, cutoff);
        }

        if (deletedFiles > 0 || deletedDirectories > 0)
        {
            _logger.LogInformation(
                "Workdir cleanup removed {FileCount} files and {DirectoryCount} directories older than {CutoffUtc}.",
                deletedFiles,
                deletedDirectories,
                cutoff);
        }
    }

    private static int DeleteStaleFiles(string root, DateTimeOffset cutoff)
    {
        var deleted = 0;
        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            return 0;
        }

        foreach (var file in files)
        {
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                if (lastWriteUtc > cutoff.UtcDateTime)
                {
                    continue;
                }

                File.Delete(file);
                deleted++;
            }
            catch
            {
                // best effort
            }
        }

        return deleted;
    }

    private static int DeleteStaleDirectories(string root, DateTimeOffset cutoff)
    {
        var deleted = 0;
        IEnumerable<string> directories;

        try
        {
            directories = Directory
                .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length)
                .ToArray();
        }
        catch
        {
            return 0;
        }

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    continue;
                }

                var lastWriteUtc = Directory.GetLastWriteTimeUtc(directory);
                if (lastWriteUtc > cutoff.UtcDateTime)
                {
                    continue;
                }

                Directory.Delete(directory, recursive: false);
                deleted++;
            }
            catch
            {
                // best effort
            }
        }

        return deleted;
    }
}

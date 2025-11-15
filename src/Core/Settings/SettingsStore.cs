using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Settings;

public sealed class SettingsStore : IDisposable
{
    private readonly string _configPath;
    private readonly object _gate = new();
    private SettingsDocument _document = SettingsDocument.CreateNew();
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly FileSystemWatcher? _configWatcher;
    private readonly Timer? _reloadTimer;
    private bool _disposed;
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(250);

    public SettingsStore(string? configDirectory = null)
    {
        var baseDirectory = configDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clop");
        Directory.CreateDirectory(baseDirectory);
        _configPath = Path.Combine(baseDirectory, "config.json");
        Load();
        _reloadTimer = CreateReloadTimer();
        _configWatcher = CreateWatcher();
    }

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public T Get<T>(SettingKey<T> key)
    {
        if (_values.TryGetValue(key.Name, out var value) && value is T typed)
        {
            return typed;
        }
        return key.DefaultValue;
    }

    public void Set<T>(SettingKey<T> key, T value)
    {
        lock (_gate)
        {
            _values[key.Name] = value;
            _document.SetValueNode(key.Name, key.Serialize(value));
            Persist();
        }

        if (key.Name == SettingsRegistry.Workdir.Name && value is string workdir)
        {
            ApplyWorkdir(workdir);
        }

        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key.Name, value));
    }

    public SettingsSnapshot Snapshot()
    {
        var map = SettingsRegistry.AllKeys.ToDictionary(k => k.Name, k => _values.TryGetValue(k.Name, out var value) ? value : k.DefaultValue);
        return new SettingsSnapshot(map);
    }

    private void Load()
    {
        lock (_gate)
        {
            _document = SettingsDocument.Load(_configPath);
            SettingsMigrations.Run(_document);
            _ = ApplyDocument(_document);
            Persist();
            ApplyWorkdir(Get(SettingsRegistry.Workdir));
        }
    }

    private void Persist()
    {
        _document.Save(_configPath);
    }

    private static void ApplyWorkdir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            var filePath = FilePath.From(path).EnsurePathExists();
            FilePath.Workdir = filePath;
        }
        catch
        {
            // ignore invalid custom workdirs
        }
    }

    private FileSystemWatcher? CreateWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            var fileName = Path.GetFileName(_configPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(directory!, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Attributes | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnConfigFileChanged;
            watcher.Created += OnConfigFileChanged;
            watcher.Renamed += OnConfigFileRenamed;
            watcher.Deleted += OnConfigFileDeleted;
            return watcher;
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to watch settings file: {ex.Message}");
            return null;
        }
    }

    private Timer? CreateReloadTimer()
    {
        try
        {
            return new Timer(static state => ((SettingsStore)state!).ReloadFromDisk(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch
        {
            return null;
        }
    }

    private void ScheduleReload()
    {
        if (_disposed || _reloadTimer is null)
        {
            return;
        }

        try
        {
            _reloadTimer.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void OnConfigFileRenamed(object sender, RenamedEventArgs e) => ScheduleReload();

    private void OnConfigFileDeleted(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void ReloadFromDisk()
    {
        if (_disposed)
        {
            return;
        }

        Dictionary<string, object?>? changes = null;

        try
        {
            lock (_gate)
            {
                var document = SettingsDocument.Load(_configPath);
                SettingsMigrations.Run(document);
                changes = ApplyDocument(document);
                _document = document;
            }
        }
        catch (IOException)
        {
            ScheduleReload();
            return;
        }
        catch (UnauthorizedAccessException)
        {
            ScheduleReload();
            return;
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to reload settings: {ex.Message}");
            return;
        }

        if (changes is null || changes.Count == 0)
        {
            return;
        }

        if (changes.TryGetValue(SettingsRegistry.Workdir.Name, out var value) && value is string workdir)
        {
            ApplyWorkdir(workdir);
        }

        foreach (var change in changes)
        {
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(change.Key, change.Value));
        }
    }

    private Dictionary<string, object?>? ApplyDocument(SettingsDocument document)
    {
        Dictionary<string, object?>? changes = null;

        foreach (var key in SettingsRegistry.AllKeys)
        {
            var value = key.Deserialize(document.GetValueNode(key.Name));
            if (_values.TryGetValue(key.Name, out var current) && AreEquivalent(key, current, value))
            {
                continue;
            }

            _values[key.Name] = value;
            document.SetValueNode(key.Name, key.Serialize(value));
            changes ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            changes[key.Name] = value;
        }

        return changes;
    }

    private static bool AreEquivalent(ISettingKey key, object? current, object? candidate)
    {
        if (ReferenceEquals(current, candidate))
        {
            return true;
        }

        if (current is null || candidate is null)
        {
            return false;
        }

        try
        {
            var left = key.Serialize(current);
            var right = key.Serialize(candidate);
            return JsonNode.DeepEquals(left, right);
        }
        catch
        {
            return current.Equals(candidate);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_configWatcher is not null)
        {
            _configWatcher.Changed -= OnConfigFileChanged;
            _configWatcher.Created -= OnConfigFileChanged;
            _configWatcher.Renamed -= OnConfigFileRenamed;
            _configWatcher.Deleted -= OnConfigFileDeleted;
            _configWatcher.Dispose();
        }
        _reloadTimer?.Dispose();
    }
}

public sealed class SettingChangedEventArgs : EventArgs
{
    public SettingChangedEventArgs(string name, object? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public object? Value { get; }
}

public sealed record SettingsSnapshot(IReadOnlyDictionary<string, object?> Values);

using System.Collections.Concurrent;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Settings;

public sealed class SettingsStore
{
    private readonly string _configPath;
    private readonly object _gate = new();
    private SettingsDocument _document = SettingsDocument.CreateNew();
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);

    public SettingsStore(string? configDirectory = null)
    {
        var baseDirectory = configDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clop");
        Directory.CreateDirectory(baseDirectory);
        _configPath = Path.Combine(baseDirectory, "config.json");
        Load();
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

            foreach (var key in SettingsRegistry.AllKeys)
            {
                var value = key.Deserialize(_document.GetValueNode(key.Name));
                _values[key.Name] = value;
                _document.SetValueNode(key.Name, key.Serialize(value));
            }

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

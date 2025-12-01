using System;
using System.Collections.Concurrent;
using ClopWindows.Core.Shared;

namespace ClopWindows.BackgroundService.Automation;

/// <summary>
/// Tracks recently optimised files using both their file paths and content fingerprints so
/// clipboard and watcher flows can short-circuit duplicate work.
/// </summary>
public sealed class OptimisedFileRegistry
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _fingerprints = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public OptimisedFileRegistry(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromMinutes(2);
    }

    public bool HasFingerprintCandidates => !_fingerprints.IsEmpty;

    public void RegisterPath(FilePath path)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return;
        }

        _paths[path.Value] = DateTimeOffset.UtcNow;
    }

    public void RegisterFingerprint(FileFingerprint fingerprint)
    {
        if (fingerprint.IsEmpty)
        {
            return;
        }

        var key = fingerprint.Key;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        _fingerprints[key] = DateTimeOffset.UtcNow;
    }

    public bool TryRegisterFingerprint(FilePath path)
    {
        if (!FileFingerprint.TryCreate(path, out var fingerprint))
        {
            return false;
        }

        RegisterFingerprint(fingerprint);
        return true;
    }

    public bool WasPathRecentlyOptimised(FilePath path)
    {
        return !string.IsNullOrWhiteSpace(path.Value) && CheckRecent(_paths, path.Value);
    }

    public bool WasFingerprintRecentlyOptimised(FileFingerprint fingerprint)
    {
        return !fingerprint.IsEmpty && CheckRecent(_fingerprints, fingerprint.Key);
    }

    public void Cleanup()
    {
        CleanupStore(_paths);
        CleanupStore(_fingerprints);
    }

    private bool CheckRecent(ConcurrentDictionary<string, DateTimeOffset> store, string key)
    {
        if (store.TryGetValue(key, out var timestamp))
        {
            if (DateTimeOffset.UtcNow - timestamp <= _window)
            {
                return true;
            }

            store.TryRemove(key, out _);
        }

        return false;
    }

    private void CleanupStore(ConcurrentDictionary<string, DateTimeOffset> store)
    {
        if (store.IsEmpty)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _window;
        foreach (var entry in store)
        {
            if (entry.Value < cutoff)
            {
                store.TryRemove(entry.Key, out _);
            }
        }
    }
}

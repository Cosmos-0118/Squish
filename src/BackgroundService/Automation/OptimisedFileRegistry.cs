using System;
using System.Collections.Concurrent;
using System.Threading;
using Squish.Core.Shared;

namespace Squish.BackgroundService.Automation;

/// <summary>
/// Tracks recently optimised files using both their file paths and content fingerprints so
/// clipboard and watcher flows can short-circuit duplicate work.
/// </summary>
public sealed class OptimisedFileRegistry
{
    private static readonly TimeSpan CleanupThrottle = TimeSpan.FromSeconds(5);
    private const int ForceCleanupThreshold = 4096;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _fingerprints = new(StringComparer.Ordinal);
    // Lengths of registered fingerprints. Hashing a file is a full read, so callers can rule out a match from the
    // length alone before paying for it.
    private readonly ConcurrentDictionary<long, DateTimeOffset> _fingerprintLengths = new();
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private long _lastCleanupTicks;

    public OptimisedFileRegistry(TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        _window = window ?? TimeSpan.FromMinutes(2);
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool HasFingerprintCandidates => !_fingerprints.IsEmpty;

    public int TrackedPathCount => _paths.Count;

    public void RegisterPath(FilePath path)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return;
        }

        _paths[path.Value] = _time.GetUtcNow();
        if (_paths.Count > ForceCleanupThreshold)
        {
            Cleanup(force: true);
        }
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

        var now = _time.GetUtcNow();
        _fingerprints[key] = now;
        _fingerprintLengths[fingerprint.Length] = now;
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

    /// <summary>
    /// True when a recent fingerprint with exactly this length exists, i.e. hashing the file could possibly match.
    /// </summary>
    public bool MayMatchFingerprintLength(long length)
    {
        return !_fingerprintLengths.IsEmpty && CheckRecent(_fingerprintLengths, length);
    }

    public void Cleanup() => Cleanup(force: false);

    private void Cleanup(bool force)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (!force && nowTicks - last < CleanupThrottle.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupTicks, nowTicks, last) != last)
        {
            return;
        }

        CleanupStore(_paths);
        CleanupStore(_fingerprints);
        CleanupStore(_fingerprintLengths);
    }

    private bool CheckRecent<TKey>(ConcurrentDictionary<TKey, DateTimeOffset> store, TKey key)
        where TKey : notnull
    {
        if (store.TryGetValue(key, out var timestamp))
        {
            if (_time.GetUtcNow() - timestamp <= _window)
            {
                return true;
            }

            store.TryRemove(new System.Collections.Generic.KeyValuePair<TKey, DateTimeOffset>(key, timestamp));
        }

        return false;
    }

    private void CleanupStore<TKey>(ConcurrentDictionary<TKey, DateTimeOffset> store)
        where TKey : notnull
    {
        if (store.IsEmpty)
        {
            return;
        }

        var cutoff = _time.GetUtcNow() - _window;
        foreach (var entry in store)
        {
            if (entry.Value < cutoff)
            {
                // Compare-and-remove: do not discard an entry that was just refreshed by another thread.
                store.TryRemove(entry);
            }
        }
    }
}

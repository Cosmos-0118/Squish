using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    // A screenshot reaches the clipboard and the screenshots folder within moments of each other, so image-pixel
    // matches only need to be remembered briefly; the encoded-bytes fingerprints above cover the long window.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _imagePixels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _watcherImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _imageWindow;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private long _lastCleanupTicks;

    public OptimisedFileRegistry(TimeSpan? window = null, TimeProvider? timeProvider = null, TimeSpan? imageWindow = null)
    {
        _window = window ?? TimeSpan.FromMinutes(2);
        _imageWindow = imageWindow ?? TimeSpan.FromSeconds(10);
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

    /// <summary>
    /// The clipboard accepted an image with these decoded pixels (an <c>ImagePixels.Key</c>). A file with the same pixels
    /// that shows up in a watched folder a moment later is the same screenshot and needs no second optimisation.
    /// </summary>
    public void RegisterImagePixels(string pixelsKey)
    {
        if (!string.IsNullOrEmpty(pixelsKey))
        {
            _imagePixels[pixelsKey] = _time.GetUtcNow();
        }
    }

    /// <summary>True while a clipboard image was registered recently, i.e. watcher files are worth decoding to compare.</summary>
    public bool HasRecentImagePixels
    {
        get
        {
            if (_imagePixels.IsEmpty)
            {
                return false;
            }

            var cutoff = _time.GetUtcNow() - _imageWindow;
            foreach (var entry in _imagePixels)
            {
                if (entry.Value >= cutoff)
                {
                    return true;
                }

                _imagePixels.TryRemove(entry);
            }

            return false;
        }
    }

    public bool WasImagePixelsRecentlyHandled(string pixelsKey)
    {
        return !string.IsNullOrEmpty(pixelsKey) && CheckRecent(_imagePixels, pixelsKey, _imageWindow);
    }

    /// <summary>The folder watcher started optimising this image file; the clipboard can compare its pixels against a new clipboard image.</summary>
    public void NoteWatcherImage(FilePath path)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return;
        }

        _watcherImages[path.Value] = _time.GetUtcNow();
        if (_watcherImages.Count > 64)
        {
            CleanupStore(_watcherImages, _imageWindow);
        }
    }

    /// <summary>Image files the watcher picked up within the image window, newest first.</summary>
    public IReadOnlyList<string> RecentWatcherImages(int max = 4)
    {
        if (_watcherImages.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var cutoff = _time.GetUtcNow() - _imageWindow;
        var fresh = new List<KeyValuePair<string, DateTimeOffset>>();
        foreach (var entry in _watcherImages)
        {
            if (entry.Value >= cutoff)
            {
                fresh.Add(entry);
            }
            else
            {
                _watcherImages.TryRemove(entry);
            }
        }

        fresh.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        var result = new List<string>(Math.Min(max, fresh.Count));
        for (var i = 0; i < fresh.Count && i < max; i++)
        {
            result.Add(fresh[i].Key);
        }

        return result;
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
        CleanupStore(_imagePixels, _imageWindow);
        CleanupStore(_watcherImages, _imageWindow);
    }

    private bool CheckRecent<TKey>(ConcurrentDictionary<TKey, DateTimeOffset> store, TKey key, TimeSpan? window = null)
        where TKey : notnull
    {
        if (store.TryGetValue(key, out var timestamp))
        {
            if (_time.GetUtcNow() - timestamp <= (window ?? _window))
            {
                return true;
            }

            store.TryRemove(new System.Collections.Generic.KeyValuePair<TKey, DateTimeOffset>(key, timestamp));
        }

        return false;
    }

    private void CleanupStore<TKey>(ConcurrentDictionary<TKey, DateTimeOffset> store, TimeSpan? window = null)
        where TKey : notnull
    {
        if (store.IsEmpty)
        {
            return;
        }

        var cutoff = _time.GetUtcNow() - (window ?? _window);
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

using System.Collections.Generic;
using System.Runtime.Versioning;
using Squish.Core.Shared;

namespace Squish.BackgroundService.Clipboard;

/// <summary>One clipboard image the service has accepted for optimisation.</summary>
internal sealed class ClipboardImageEntry
{
    internal ClipboardImageEntry(ClipboardImageSignature signature, uint originSequence, DateTimeOffset now)
    {
        Signature = signature;
        OriginSequence = originSequence;
        LatestSequence = originSequence;
        CreatedAt = now;
        LastActivity = now;
    }

    public ClipboardImageSignature Signature { get; }

    /// <summary>Clipboard sequence of the snapshot that started the optimisation.</summary>
    public uint OriginSequence { get; }

    // The members below are only read and written under the tracker's lock.

    /// <summary>
    /// Sequence of the newest snapshot recognised as this same image. The clipboard moving to it is not the user
    /// copying something else, so the optimised result may still be written back.
    /// </summary>
    internal uint LatestSequence { get; set; }

    internal string? RequestId { get; set; }

    internal bool Finished { get; set; }

    /// <summary>The optimised file this image ended up as, once finished. A repeat of the image reuses it instead of saving another copy.</summary>
    internal FilePath? ResultPath { get; set; }

    internal DateTimeOffset CreatedAt { get; }

    internal DateTimeOffset LastActivity { get; set; }
}

/// <summary>
/// Remembers the clipboard images that are being optimised, or finished a moment ago, so that the repeated clipboard
/// writes one screenshot produces collapse into a single optimisation run.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ClipboardImageTracker
{
    /// <summary>
    /// How long a finished image still counts as "the same snip". An image that is still being optimised matches for as
    /// long as it takes; this only covers writes that land just after it completes.
    /// </summary>
    internal static readonly TimeSpan DefaultSettleWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a finished image that produced a file is remembered. Copying the same picture again inside this window
    /// puts the already optimised file back on the clipboard instead of saving and optimising a second identical file.
    /// </summary>
    internal static readonly TimeSpan DefaultReuseWindow = TimeSpan.FromMinutes(2);

    // Guards against entries leaking if a completion is somehow never reported.
    private static readonly TimeSpan MaxUnfinishedAge = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 32;

    private readonly object _gate = new();
    private readonly List<ClipboardImageEntry> _entries = new();
    private readonly TimeSpan _settleWindow;
    private readonly TimeSpan _reuseWindow;
    private readonly TimeProvider _time;

    public ClipboardImageTracker(TimeSpan? settleWindow = null, TimeProvider? timeProvider = null, TimeSpan? reuseWindow = null)
    {
        _settleWindow = settleWindow ?? DefaultSettleWindow;
        _reuseWindow = reuseWindow ?? DefaultReuseWindow;
        _time = timeProvider ?? TimeProvider.System;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the tracked image that <paramref name="signature"/> repeats, if any, and records that the clipboard is
    /// now at <paramref name="sequence"/> with that same image.
    /// </summary>
    public ClipboardImageEntry? FindDuplicate(ClipboardImageSignature signature, uint sequence)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);

            foreach (var entry in _entries)
            {
                if (!entry.Signature.IsSameImageAs(signature))
                {
                    continue;
                }

                entry.LatestSequence = sequence;
                entry.LastActivity = now;
                return entry;
            }

            return null;
        }
    }

    public ClipboardImageEntry Add(ClipboardImageSignature signature, uint sequence)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);

            var entry = new ClipboardImageEntry(signature, sequence, now);
            _entries.Add(entry);
            return entry;
        }
    }

    public void MarkQueued(ClipboardImageEntry entry, string requestId)
    {
        lock (_gate)
        {
            entry.RequestId = requestId;
        }
    }

    /// <summary>Forgets an entry that never produced a request.</summary>
    public void Discard(ClipboardImageEntry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
        }
    }

    /// <summary>
    /// A newer, different clipboard image arrived. Returns the requests still running for older images and forgets
    /// those images, so copying one of them again later is treated as new.
    /// </summary>
    public IReadOnlyList<string> SupersedeInFlight()
    {
        lock (_gate)
        {
            var requestIds = new List<string>();
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Finished || entry.RequestId is null)
                {
                    continue;
                }

                requestIds.Add(entry.RequestId);
                _entries.RemoveAt(i);
            }

            return requestIds;
        }
    }

    /// <summary>
    /// The optimisation for <paramref name="requestId"/> ended. A success stays on record for the settle window so a
    /// late repeat of the same image is still ignored; anything else is forgotten so a retry is possible.
    /// </summary>
    public void Finish(string requestId, bool keep, FilePath? resultPath = null)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => string.Equals(e.RequestId, requestId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            if (!keep)
            {
                _entries.RemoveAt(index);
                return;
            }

            var entry = _entries[index];
            entry.Finished = true;
            entry.ResultPath = resultPath;
            entry.LastActivity = _time.GetUtcNow();
        }
    }

    /// <summary>The optimised file of a finished image, or null while it is still being optimised or produced no file.</summary>
    public FilePath? GetResult(ClipboardImageEntry entry)
    {
        lock (_gate)
        {
            return entry.Finished ? entry.ResultPath : null;
        }
    }

    /// <summary>
    /// True when the clipboard is at the sequence of the newest snapshot that repeated the image whose optimisation
    /// began at <paramref name="originSequence"/>.
    /// </summary>
    public bool IsLatestSequence(uint originSequence, uint currentSequence)
    {
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (entry.OriginSequence == originSequence)
                {
                    return entry.LatestSequence == currentSequence;
                }
            }

            return false;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        _entries.RemoveAll(e => e.Finished
            ? now - e.LastActivity > (e.ResultPath is null ? _settleWindow : TimeSpan.FromTicks(Math.Max(_settleWindow.Ticks, _reuseWindow.Ticks)))
            : now - e.CreatedAt > MaxUnfinishedAge);

        while (_entries.Count >= MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }
}

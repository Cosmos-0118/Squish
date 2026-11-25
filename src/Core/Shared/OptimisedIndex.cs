using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;

namespace Squish.Core.Shared;

/// <summary>
/// A small on-disk memory of files Squish has finished, kept next to its other state. The NTFS marker on the file is the
/// primary record, but it cannot exist on FAT32/exFAT/network shares and cloud sync clients sometimes strip it; without
/// this index those files would be optimised again every time they are seen. Entries are (path hash, size, last-write
/// time, content fingerprint), so a file that really changed is never mistaken for a finished one.
/// </summary>
/// <remarks>
/// Append-only file of fixed 32-byte records, loaded into memory on first use; later records win, a negative length is a
/// tombstone. The newest <see cref="DefaultCapacity"/> entries are kept (about 2 MB), and the file is compacted when it has
/// grown to well past that. Every operation is best effort: an unreadable or unwritable index just means no memory.
/// </remarks>
internal sealed class OptimisedIndex
{
    public const int DefaultCapacity = 65_536;
    private const int RecordSize = 32;

    private static readonly Lazy<OptimisedIndex> DefaultInstance = new(() =>
        new OptimisedIndex(Path.Combine(SquishPaths.WorkRoot.Value, "state", "optimised.idx")));

    private readonly object _gate = new();
    private readonly string _file;
    private readonly int _capacity;
    private Dictionary<ulong, Entry>? _entries;
    private long _sequence;

    public OptimisedIndex(string file, int capacity = DefaultCapacity)
    {
        _file = file;
        _capacity = Math.Max(16, capacity);
    }

    public static OptimisedIndex Default => DefaultInstance.Value;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Entries().Count;
            }
        }
    }

    /// <summary>Remembers the file as it is now. Returns false if it cannot be read.</summary>
    public bool Record(FilePath path)
    {
        if (string.IsNullOrWhiteSpace(path.Value) || !FileFingerprint.TryCreate(path, out var fingerprint))
        {
            return false;
        }

        try
        {
            var ticks = new FileInfo(path.Value).LastWriteTimeUtc.Ticks;
            var entry = new Entry(fingerprint.Length, ticks, FingerprintBits(fingerprint), 0);
            lock (_gate)
            {
                Upsert(HashPath(path.Value), entry);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True if the file is still the one that was recorded. A changed timestamp alone does not matter when the content is the same.</summary>
    public bool IsKnown(FilePath path)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return false;
        }

        var key = HashPath(path.Value);
        Entry entry;
        lock (_gate)
        {
            if (!Entries().TryGetValue(key, out entry))
            {
                return false;
            }
        }

        try
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists || info.Length != entry.Length)
            {
                return false;
            }

            if (info.LastWriteTimeUtc.Ticks == entry.Ticks)
            {
                return true;
            }

            // Same size, new timestamp (sync client, SMB rounding, backup tool): look at the content.
            if (FileFingerprint.TryCreate(path, out var current) && FingerprintBits(current) == entry.Fingerprint)
            {
                lock (_gate)
                {
                    Upsert(key, entry with { Ticks = info.LastWriteTimeUtc.Ticks });
                }

                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Forget(FilePath path)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return;
        }

        var key = HashPath(path.Value);
        lock (_gate)
        {
            if (Entries().Remove(key))
            {
                Append(key, new Entry(-1, 0, 0, 0));
            }
        }
    }

    private void Upsert(ulong key, Entry entry)
    {
        var entries = Entries();
        entries[key] = entry with { Sequence = ++_sequence };
        Append(key, entry);

        if (entries.Count > _capacity + _capacity / 8)
        {
            Trim(entries);
            Compact(entries);
        }
    }

    private void Trim(Dictionary<ulong, Entry> entries)
    {
        foreach (var stale in entries.OrderBy(e => e.Value.Sequence).Take(entries.Count - _capacity).Select(e => e.Key).ToList())
        {
            entries.Remove(stale);
        }
    }

    private Dictionary<ulong, Entry> Entries()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var entries = new Dictionary<ulong, Entry>();
        long records = 0;
        var misaligned = false;
        try
        {
            if (File.Exists(_file))
            {
                using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                misaligned = stream.Length % RecordSize != 0;
                var buffer = new byte[RecordSize * 1024];
                int read;
                var carry = 0;
                while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
                {
                    var available = carry + read;
                    var offset = 0;
                    for (; offset + RecordSize <= available; offset += RecordSize)
                    {
                        var key = BitConverter.ToUInt64(buffer, offset);
                        var length = BitConverter.ToInt64(buffer, offset + 8);
                        records++;
                        if (length < 0)
                        {
                            entries.Remove(key);
                        }
                        else
                        {
                            entries[key] = new Entry(length, BitConverter.ToInt64(buffer, offset + 16), BitConverter.ToUInt64(buffer, offset + 24), ++_sequence);
                        }
                    }

                    carry = available - offset;
                    Buffer.BlockCopy(buffer, offset, buffer, 0, carry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable index: start empty
        }

        _entries = entries;
        if (entries.Count > _capacity)
        {
            Trim(entries);
        }

        // A torn or foreign tail would shift every later record; rewrite the file so appends stay aligned.
        if (misaligned || records > (long)_capacity * 2)
        {
            Compact(entries);
        }

        return entries;
    }

    private void Append(ulong key, Entry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var record = new byte[RecordSize];
            WriteRecord(record, key, entry);

            // One small write per record, so the app and the CLI can append to the same file at the same time.
            using var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(record, 0, record.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private void Compact(Dictionary<ulong, Entry> entries)
    {
        var temp = _file + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var record = new byte[RecordSize];
                foreach (var pair in entries.OrderBy(e => e.Value.Sequence))
                {
                    WriteRecord(record, pair.Key, pair.Value);
                    stream.Write(record, 0, record.Length);
                }
            }

            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // leave it; the next compaction overwrites it
            }
        }
    }

    private static void WriteRecord(byte[] record, ulong key, Entry entry)
    {
        BitConverter.TryWriteBytes(record.AsSpan(0, 8), key);
        BitConverter.TryWriteBytes(record.AsSpan(8, 8), entry.Length);
        BitConverter.TryWriteBytes(record.AsSpan(16, 8), entry.Ticks);
        BitConverter.TryWriteBytes(record.AsSpan(24, 8), entry.Fingerprint);
    }

    private static ulong HashPath(string path) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));

    private static ulong FingerprintBits(FileFingerprint fingerprint) =>
        BitConverter.ToUInt64(Convert.FromHexString(fingerprint.Hash.AsSpan(0, 16)));

    private readonly record struct Entry(long Length, long Ticks, ulong Fingerprint, long Sequence);
}

using System.Globalization;
using System.Text;
using Squish.Core.Shared;

namespace Squish.Core.Stats;

/// <summary>
/// Append-only history of optimised files, one line per file: <c>unixSeconds,kind,originalBytes,optimisedBytes,seconds</c>.
/// Plain text on purpose: a crash mid-write can only damage the last line, which is skipped on load.
/// </summary>
public sealed class OptimisationStatsStore
{
    private readonly object _gate = new();
    private readonly string _filePath;

    public OptimisationStatsStore(string? directory = null)
    {
        var folder = directory ?? Path.Combine(SquishPaths.WorkRoot.Value, "stats");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "history.csv");
    }

    public IReadOnlyList<OptimisationRecord> Load()
    {
        lock (_gate)
        {
            var records = new List<OptimisationRecord>();
            if (!File.Exists(_filePath))
            {
                return records;
            }

            try
            {
                foreach (var line in File.ReadLines(_filePath, Encoding.UTF8))
                {
                    if (TryParse(line, out var record))
                    {
                        records.Add(record);
                    }
                }
            }
            catch (IOException)
            {
                // Unreadable history just means an empty summary; never block the app on it.
            }

            return records;
        }
    }

    public void Append(OptimisationRecord record)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{record.Timestamp.ToUnixTimeSeconds()},{(int)record.Kind},{record.OriginalBytes},{record.OptimisedBytes},{record.Seconds:0.###}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_filePath, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Losing one statistics line is better than failing an optimisation that already succeeded.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_filePath);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static bool TryParse(string line, out OptimisationRecord record)
    {
        record = null!;
        var parts = line.Split(',');
        if (parts.Length != 5)
        {
            return false;
        }

        var culture = CultureInfo.InvariantCulture;
        if (!long.TryParse(parts[0], NumberStyles.Integer, culture, out var seconds) ||
            !int.TryParse(parts[1], NumberStyles.Integer, culture, out var kind) ||
            !Enum.IsDefined(typeof(StatsKind), kind) ||
            !long.TryParse(parts[2], NumberStyles.Integer, culture, out var original) ||
            !long.TryParse(parts[3], NumberStyles.Integer, culture, out var optimised) ||
            !double.TryParse(parts[4], NumberStyles.Float, culture, out var duration))
        {
            return false;
        }

        try
        {
            record = new OptimisationRecord(DateTimeOffset.FromUnixTimeSeconds(seconds), (StatsKind)kind, original, optimised, duration);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

using System.Text.Json.Serialization;

namespace ClopWindows.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CleanupInterval
{
    Every10Minutes = 600,
    Hourly = 3600,
    Every12Hours = 43200,
    Daily = 86_400,
    Every3Days = 259_200,
    Weekly = 604_800,
    Monthly = 2_592_000,
    Never = 0
}

public static class CleanupIntervalExtensions
{
    public static string Title(this CleanupInterval interval) => interval switch
    {
        CleanupInterval.Every10Minutes => "10 minutes",
        CleanupInterval.Hourly => "1 hour",
        CleanupInterval.Every12Hours => "12 hours",
        CleanupInterval.Daily => "1 day",
        CleanupInterval.Every3Days => "3 days",
        CleanupInterval.Weekly => "1 week",
        CleanupInterval.Monthly => "1 month",
        CleanupInterval.Never => "Never",
        _ => interval.ToString()
    };
}

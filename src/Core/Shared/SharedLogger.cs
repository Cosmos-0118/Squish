using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ClopWindows.Core.Shared;

public static class SharedLogger
{
    public static Action<string, string, object?>? Sink { get; set; }

    public static void Verbose(string message, object? context = null) => Write("VERBOSE", message, context);
    public static void Debug(string message, object? context = null) => Write("DEBUG", message, context);
    public static void Info(string message, object? context = null) => Write("INFO", message, context);
    public static void Warning(string message, object? context = null) => Write("WARN", message, context);
    public static void Error(string message, object? context = null) => Write("ERROR", message, context);

    public static void TraceCalls([CallerMemberName] string? member = null)
    {
        var stack = Environment.StackTrace;
        Write("TRACE", member ?? string.Empty, stack);
    }

    private static void Write(string level, string message, object? context)
    {
        var contextSuffix = context is null ? string.Empty : $" {context}";
        var payload = $"[{DateTime.UtcNow:O}] [{level}] {message}{contextSuffix}";
        Trace.WriteLine(payload);
        Sink?.Invoke(level, message, context);
    }
}

public static class Log
{
    public static void Verbose(string message, object? context = null) => SharedLogger.Verbose(message, context);
    public static void Debug(string message, object? context = null) => SharedLogger.Debug(message, context);
    public static void Info(string message, object? context = null) => SharedLogger.Info(message, context);
    public static void Warning(string message, object? context = null) => SharedLogger.Warning(message, context);
    public static void Error(string message, object? context = null) => SharedLogger.Error(message, context);
    public static void TraceCalls() => SharedLogger.TraceCalls();
}

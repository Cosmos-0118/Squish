using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClopWindows.Core.Shared.Logging;

public static class SharedLogging
{
    public static IDisposable EnableSharedLogger(string componentName)
    {
        return SharedLogScope.Enable(componentName);
    }

    public static ILoggingBuilder AddSharedFileLogger(this ILoggingBuilder builder, string componentName, LogLevel minimumLevel = LogLevel.Information)
    {
        var logFilePath = LogFileLocator.GetLogFilePath(componentName);
        builder.Services.AddSingleton<ILoggerProvider>(_ => new SimpleFileLoggerProvider(logFilePath));
        builder.SetMinimumLevel(minimumLevel);
        return builder;
    }

    private sealed class SharedLogScope : IDisposable
    {
        private static readonly object ScopeGate = new();
        private static SharedLogScope? _active;

        private readonly string _componentName;
        private readonly string _filePath;
        private readonly object _writeGate = new();
        private bool _disposed;

        private SharedLogScope(string componentName, string filePath)
        {
            _componentName = componentName;
            _filePath = filePath;
        }

        public static SharedLogScope Enable(string componentName)
        {
            var filePath = LogFileLocator.GetLogFilePath(componentName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var scope = new SharedLogScope(componentName, filePath);
            lock (ScopeGate)
            {
                _active?.DisposeInternal();
                _active = scope;
                SharedLogger.Sink = scope.Write;
            }

            return scope;
        }

        private void Write(string level, string message, object? context)
        {
            if (_disposed)
            {
                return;
            }

            var contextText = context is null ? string.Empty : $" {context}";
            var line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:O} [{1}] {2}: {3}{4}",
                DateTimeOffset.UtcNow,
                level,
                _componentName,
                message,
                contextText);

            lock (_writeGate)
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
        }

        private void DisposeInternal()
        {
            _disposed = true;
        }

        public void Dispose()
        {
            lock (ScopeGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (ReferenceEquals(_active, this))
                {
                    SharedLogger.Sink = null;
                    _active = null;
                }
            }
        }
    }
}

using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ClopWindows.Core.Shared.Logging;

internal sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private bool _disposed;

    public SimpleFileLoggerProvider(string filePath)
    {
        _filePath = filePath;
    }

    public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(this, categoryName);

    internal void WriteLine(string message)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            File.AppendAllText(_filePath, message + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private sealed class SimpleFileLogger : ILogger
    {
        private readonly SimpleFileLoggerProvider _provider;
        private readonly string _categoryName;

        public SimpleFileLogger(SimpleFileLoggerProvider provider, string categoryName)
        {
            _provider = provider;
            _categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:O} [{1}] {2}: {3}",
                DateTimeOffset.UtcNow,
                logLevel,
                _categoryName,
                message);

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.WriteLine(line);
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }
}

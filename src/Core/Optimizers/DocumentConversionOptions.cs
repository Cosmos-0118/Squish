using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed record DocumentConversionOptions
{
    public static DocumentConversionOptions Default { get; } = new();

    public Func<bool>? EnabledEvaluator { get; init; } = null;

    public string ConverterExecutablePath { get; init; } = ResolveDefaultConverter();

    public TimeSpan ConversionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public IReadOnlySet<string> ConvertibleExtensions { get; init; } =
        new HashSet<string>(MediaFormats.DocumentExtensionNames, StringComparer.OrdinalIgnoreCase);

    public bool PreserveSourceTimestamps { get; init; } = true;

    private static string ResolveDefaultConverter()
    {
        var env = Environment.GetEnvironmentVariable("CLOP_SOFFICE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            new[] { "tools", "libreoffice", "program", "soffice.com" },
            new[] { "tools", "libreoffice", "program", "soffice.exe" },
            new[] { "tools", "LibreOffice", "program", "soffice.com" },
            new[] { "tools", "LibreOffice", "program", "soffice.exe" }
        };

        foreach (var candidate in candidates)
        {
            var resolved = ToolLocator.EnumeratePossibleFiles(baseDir, candidate).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        var onPath = ToolLocator.ResolveOnPath("soffice.com") ?? ToolLocator.ResolveOnPath("soffice.exe");
        if (!string.IsNullOrWhiteSpace(onPath))
        {
            return onPath;
        }

        return "soffice.com";
    }

    public bool IsEnabled()
    {
        if (EnabledEvaluator is null)
        {
            return true;
        }

        try
        {
            return EnabledEvaluator();
        }
        catch
        {
            return true;
        }
    }
}

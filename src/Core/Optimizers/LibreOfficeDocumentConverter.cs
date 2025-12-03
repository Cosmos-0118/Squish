using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Processes;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed class LibreOfficeDocumentConverter : IDocumentConverter
{
    public bool Supports(FilePath path, DocumentConversionOptions options)
    {
        if (options.ConvertibleExtensions.Count == 0)
        {
            return MediaFormats.IsDocument(path);
        }

        var extension = path.Extension;
        return !string.IsNullOrWhiteSpace(extension) && options.ConvertibleExtensions.Contains(extension.TrimStart('.'));
    }

    public async Task<DocumentConversionResult> ConvertToPdfAsync(
        FilePath source,
        FilePath workingDirectory,
        DocumentConversionOptions options,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workingDirectory.Value))
        {
            Directory.CreateDirectory(workingDirectory.Value);
        }

        var arguments = BuildArguments(source, workingDirectory);
        var runnerOptions = ProcessRunnerOptions.Create(workingDirectory, throwOnError: false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ConversionTimeout);

        var result = await ProcessRunner.RunAsync(options.ConverterExecutablePath, arguments, runnerOptions, timeoutCts.Token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            return DocumentConversionResult.Failed(string.IsNullOrWhiteSpace(error) ? "Document conversion failed." : error.Trim(), workingDirectory);
        }

        var expected = workingDirectory.Append($"{source.Stem}.pdf");
        if (!File.Exists(expected.Value))
        {
            return DocumentConversionResult.Failed("Document converter did not produce a PDF.", workingDirectory);
        }

        return DocumentConversionResult.Succeeded(expected, workingDirectory);
    }

    private static IReadOnlyList<string> BuildArguments(FilePath source, FilePath workingDirectory)
    {
        return new List<string>
        {
            "--headless",
            "--nocrashreport",
            "--nolockcheck",
            "--nodefault",
            "--nologo",
            "--nofirststartwizard",
            "--convert-to",
            "pdf",
            "--outdir",
            workingDirectory.Value,
            source.Value
        };
    }
}

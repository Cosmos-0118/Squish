using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Processes;
using Squish.Core.Shared;

namespace Squish.Core.Pdf;

/// <summary>Runs the bundled <c>qpdf.exe</c>. Exit code 3 means "finished with warnings": the output file is still written and valid.</summary>
internal sealed class QpdfRunner
{
    private static readonly ProcessRunnerOptions Quiet = ProcessRunnerOptions.Create(throwOnError: false);
    private readonly string _executable;

    public QpdfRunner(string executable)
    {
        _executable = executable;
    }

    public static string? Resolve(string? configuredPath = null)
        => ExternalToolResolver.Resolve("qpdf", "qpdf", "SQUISH_QPDF", configuredPath);

    public static bool Succeeded(ProcessResult result) => result.ExitCode is 0 or 3;

    public Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken token)
        => ProcessRunner.RunAsync(_executable, arguments, Quiet, token);

    /// <summary>Output options shared by every rewrite: maximum deflate, objects packed into streams.</summary>
    public static IEnumerable<string> WriteOptions(bool linearize)
    {
        yield return "--recompress-flate";
        yield return "--compression-level=9";
        yield return "--compress-streams=y";
        yield return "--object-streams=generate";
        if (linearize)
        {
            yield return "--linearize";
        }
    }
}

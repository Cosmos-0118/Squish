using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Writes a long argument list to a file and returns the <c>@file</c> argument qpdf expands from it, so the number of
    /// object selectors is not bound by the operating system's command line limit.
    /// </summary>
    public static async Task<string> WriteArgumentFileAsync(string path, IEnumerable<string> arguments, CancellationToken token)
    {
        await File.WriteAllLinesAsync(path, arguments, token).ConfigureAwait(false);
        return "@" + path;
    }

    /// <summary>
    /// Rewrites <paramref name="input"/> with the shared write options and returns the smaller result. When the document holds
    /// predictor-coded pictures it writes both with and without <c>--recompress-flate</c> (concurrently) and keeps the better
    /// one, because neither choice wins for every file: recompressing helps content streams and fonts, but flattens predictors.
    /// </summary>
    /// <returns>The path of the written file, or null when qpdf could not write any variant.</returns>
    public async Task<string?> WriteBestAsync(string input, string outputBase, IReadOnlyList<string> arguments, bool linearize, bool predictorsPresent, CancellationToken token)
    {
        var variants = predictorsPresent ? new[] { false, true } : new[] { true };
        var written = await Task.WhenAll(variants.Select(async recompress =>
        {
            var output = $"{outputBase}-{(recompress ? "flate" : "keep")}.pdf";
            var all = new List<string> { input, output };
            all.AddRange(arguments);
            all.AddRange(WriteOptions(linearize, recompress));
            var result = await RunAsync(all, token).ConfigureAwait(false);
            if (Succeeded(result) && File.Exists(output))
            {
                return output;
            }

            Log.Warning($"qpdf could not write {output}: {result.StandardError}");
            return null;
        })).ConfigureAwait(false);

        return written.Where(path => path is not null).MinBy(path => new FileInfo(path!).Length);
    }

    /// <summary>
    /// Output options shared by every rewrite: maximum deflate, objects packed into streams.
    /// <paramref name="recompressFlate"/> re-deflates streams that are already Flate, but qpdf then also decodes PNG/TIFF
    /// predictors and writes the data back without them, which makes predictor-coded pictures much larger. A rewrite that must
    /// keep predictors leaves it off (see <see cref="WriteBestAsync"/>).
    /// </summary>
    public static IEnumerable<string> WriteOptions(bool linearize, bool recompressFlate = true)
    {
        if (recompressFlate)
        {
            yield return "--recompress-flate";
        }

        yield return "--compression-level=9";
        yield return "--compress-streams=y";
        yield return "--object-streams=generate";
        if (linearize)
        {
            yield return "--linearize";
        }
    }
}

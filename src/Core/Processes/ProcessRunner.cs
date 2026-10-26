using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Shared;

namespace Squish.Core.Processes;

public static class ProcessRunner
{
    public static Task<ProcessResult> RunAsync(string command, IEnumerable<string> arguments, ProcessRunnerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var argsArray = arguments?.ToArray() ?? Array.Empty<string>();
        return RunInternalAsync(command, argsArray, options ?? ProcessRunnerOptions.Default, cancellationToken);
    }

    public static ProcessResult Run(string command, IEnumerable<string> arguments, ProcessRunnerOptions? options = null, CancellationToken cancellationToken = default)
        => RunAsync(command, arguments, options, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Child processes are only ever started from an absolute path. Bare command names are looked up on the
    /// rooted <c>PATH</c> only, so neither the working directory nor a relative path can supply the binary.
    /// </summary>
    private static string ResolveExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command must not be empty.", nameof(command));
        }

        var resolved = ToolLocator.ResolveOnPath(command);
        if (resolved is null)
        {
            throw new FileNotFoundException($"Unable to locate executable '{command}'. Provide an absolute path or install it on PATH.", command);
        }

        return resolved;
    }

    private static async Task<ProcessResult> RunInternalAsync(string command, IReadOnlyList<string> args, ProcessRunnerOptions options, CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable(command);
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory?.Value ?? Path.GetTempPath()
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var kvp in options.EnvironmentVariables)
        {
            startInfo.Environment[kvp.Key] = kvp.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutCompletion = new TaskCompletionSource();
        var stderrCompletion = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutCompletion.TrySetResult();
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrCompletion.TrySetResult();
            }
            else
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{command}'");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdoutCompletion.Task, stderrCompletion.Task).ConfigureAwait(false);

        var result = new ProcessResult(command, args, process.ExitCode, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd());
        if (options.ThrowOnError && result.ExitCode != 0)
        {
            Log.Error(result.ToString());
            throw new ProcessExecutionException(result);
        }

        return result;
    }
}

public sealed record ProcessResult(string Command, IReadOnlyList<string> Arguments, int ExitCode, string StandardOutput, string StandardError)
{
    public string CommandLine => $"{Command} {string.Join(' ', Arguments)}";

    public override string ToString() => $"{CommandLine} exited with {ExitCode}\nSTDOUT:\n{StandardOutput}\nSTDERR:\n{StandardError}";
}

public sealed class ProcessExecutionException : Exception
{
    public ProcessExecutionException(ProcessResult result)
        : base(result.ToString())
    {
        Result = result;
    }

    public ProcessResult Result { get; }
}

public sealed record ProcessRunnerOptions(FilePath? WorkingDirectory, IReadOnlyDictionary<string, string> EnvironmentVariables, bool ThrowOnError)
{
    public static ProcessRunnerOptions Default { get; } = new(null, new Dictionary<string, string>(), true);

    public static ProcessRunnerOptions Create(FilePath? workingDirectory = null, IDictionary<string, string>? env = null, bool throwOnError = true)
        => new(workingDirectory, env is null ? new Dictionary<string, string>() : new Dictionary<string, string>(env), throwOnError);
}

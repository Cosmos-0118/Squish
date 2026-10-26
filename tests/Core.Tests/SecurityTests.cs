using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using Squish.Core.IPC;
using Squish.Core.Processes;
using Squish.Core.Security;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class SecurityTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const int Port = 58732;

    // ---- local automation HTTP API ---------------------------------------------------------------------

    [Fact]
    public void GeneratedTokensAreLongAndUnique()
    {
        var a = AutomationRequestGuard.GenerateToken();
        var b = AutomationRequestGuard.GenerateToken();

        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Theory]
    [InlineData("127.0.0.1:58732", true)]
    [InlineData("localhost:58732", true)]
    [InlineData("LOCALHOST:58732", true)]
    [InlineData("[::1]:58732", true)]
    [InlineData("evil.example:58732", false)]
    [InlineData("127.0.0.1.evil.example:58732", false)]
    [InlineData("127.0.0.1:9999", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyLoopbackHostHeadersAreAccepted(string? host, bool expected)
        => Assert.Equal(expected, AutomationRequestGuard.IsAllowedHost(host, Port));

    [Fact]
    public void AuthorisedNativeClientsAreAllowed()
    {
        var verdict = Evaluate(host: "127.0.0.1:58732", auth: "Bearer " + Token, contentType: "application/json; charset=utf-8");
        Assert.Equal(AutomationRequestVerdict.Allowed, verdict);
    }

    [Fact]
    public void RequestsWithoutAValidTokenAreRejectedEvenWhenNoTokenWasEverConfigured()
    {
        Assert.Equal(AutomationRequestVerdict.Unauthorized, Evaluate(auth: null));
        Assert.Equal(AutomationRequestVerdict.Unauthorized, Evaluate(auth: "Bearer wrong"));
        Assert.Equal(AutomationRequestVerdict.Unauthorized, Evaluate(auth: "Basic " + Token));
        Assert.Equal(AutomationRequestVerdict.Unauthorized, Evaluate(auth: "Bearer "));
        Assert.Equal(AutomationRequestVerdict.Unauthorized, Evaluate(auth: "Bearer " + Token, token: string.Empty));
    }

    [Fact]
    public void BrowserDrivenRequestsAreRefusedEvenWithAValidToken()
    {
        // CSRF: a web page can POST text/plain to http://127.0.0.1:port without a CORS pre-flight.
        Assert.Equal(AutomationRequestVerdict.ForbiddenBrowserOrigin, Evaluate(origin: "https://evil.example", auth: "Bearer " + Token));
        Assert.Equal(AutomationRequestVerdict.ForbiddenBrowserOrigin, Evaluate(secFetchSite: "cross-site", auth: "Bearer " + Token));
    }

    [Fact]
    public void DnsRebindingIsRefusedByHostHeaderValidation()
    {
        Assert.Equal(AutomationRequestVerdict.ForbiddenHost, Evaluate(host: "attacker.example:58732", auth: "Bearer " + Token));
    }

    [Fact]
    public void PostBodiesMustBeJson()
    {
        Assert.Equal(AutomationRequestVerdict.UnsupportedMediaType, Evaluate(auth: "Bearer " + Token, contentType: "text/plain"));
        Assert.Equal(AutomationRequestVerdict.UnsupportedMediaType, Evaluate(auth: "Bearer " + Token, contentType: null));
        Assert.Equal(AutomationRequestVerdict.Allowed, Evaluate(method: "GET", auth: "Bearer " + Token, contentType: null));
    }

    private static AutomationRequestVerdict Evaluate(
        string method = "POST",
        string? host = "127.0.0.1:58732",
        string? origin = null,
        string? secFetchSite = null,
        string? auth = null,
        string? contentType = "application/json",
        string token = Token)
        => AutomationRequestGuard.Evaluate(method, host, origin, secFetchSite, auth, contentType, token, Port);

    // ---- process launching --------------------------------------------------------------------------------

    [Fact]
    public async Task ProcessRunnerRefusesToResolveCommandsFromTheWorkingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squish-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var plantedName = "squish-planted-" + Guid.NewGuid().ToString("N");
        var planted = Path.Combine(directory, OperatingSystem.IsWindows() ? plantedName + ".exe" : plantedName);
        File.WriteAllText(planted, "#!/bin/sh\necho pwned\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                ProcessRunner.RunAsync(plantedName, Array.Empty<string>(), ProcessRunnerOptions.Create(FilePath.From(directory), throwOnError: false)));
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                ProcessRunner.RunAsync("./" + plantedName, Array.Empty<string>(), ProcessRunnerOptions.Create(FilePath.From(directory), throwOnError: false)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessRunnerPassesArgumentsWithoutShellInterpretation()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the probe command below is POSIX; Windows CI exercises ArgumentList through the encoder tests
        }

        var result = await ProcessRunner.RunAsync("/bin/echo", new[] { "a; touch /tmp/squish-injected", "$(whoami)", "`id`" });

        Assert.Equal("a; touch /tmp/squish-injected $(whoami) `id`", result.StandardOutput.Trim());
        Assert.False(File.Exists("/tmp/squish-injected"));
    }

    [Fact]
    public void ToolResolverIgnoresRelativeAndCurrentDirectoryPathEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squish-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var name = "squish-fake-tool-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(directory, ExternalToolResolver.FileName(name)), "x");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            Environment.SetEnvironmentVariable("PATH", "." + Path.PathSeparator + directory.Replace(Path.GetFullPath(directory), "relative"));

            Assert.Null(ExternalToolResolver.Resolve(name, "none"));
            Assert.Null(ToolLocator.ResolveOnPath(name));
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- IPC --------------------------------------------------------------------------------------------

    [Fact]
    public async Task NamedPipeChannelRejectsOversizedLengthPrefixes()
    {
        var pipeName = "squish-test-" + Guid.NewGuid().ToString("N");
        await using var channel = new NamedPipeChannel(pipeName);
        var handled = new TaskCompletionSource<bool>();
        channel.StartListening(_ =>
        {
            handled.TrySetResult(true);
            return ValueTask.FromResult<byte[]?>(Array.Empty<byte>());
        });

        // A hostile client announces a 2 GiB message. The server must drop it instead of allocating.
        await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(2000);
            var length = BitConverter.GetBytes(int.MaxValue);
            await client.WriteAsync(length);
            await client.FlushAsync();
        }

        var completed = await Task.WhenAny(handled.Task, Task.Delay(500));
        Assert.NotSame(handled.Task, completed);

        // ...and the listener keeps serving well-behaved clients afterwards.
        var response = await channel.SendAndWaitAsync(new byte[] { 1, 2, 3 }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        Assert.NotNull(response);
    }
}

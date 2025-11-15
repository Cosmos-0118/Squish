using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ClopWindows.Core.IPC;
using ClopWindows.Core.Shared;
using Xunit;

namespace Core.Tests;

public class FilePathTests
{
    [Fact]
    public void SafeFilename_ReplacesInvalidCharacters()
    {
        const string input = "foo:bar?/baz.png";
        var sanitized = input.SafeFilename();
        Assert.Equal("foo_bar__baz.png", sanitized);
    }

    [Fact]
    public void FilePathGenerator_ReplacesTokensAndCreatesCounter()
    {
        var template = FilePath.From(Path.Combine(Path.GetTempPath(), "%f-%i"));
        var source = FilePath.From(Path.Combine(Path.GetTempPath(), "image.png"));
        var counter = 0;

        var generated = FilePathGenerator.Generate(template, source, ref counter, createDirectories: false);

        Assert.Contains(source.Stem, generated.Value, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".png", generated.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counter);
    }

    [Fact]
    public void NanoId_GeneratesExpectedLength()
    {
        const int size = 10;
        var id = NanoId.New(NanoIdAlphabet.UrlSafe, size);
        Assert.Equal(size, id.Length);
    }

    [Fact]
    public async Task NamedPipeChannel_RoundTripsPayload()
    {
        var pipeName = "clop-test-" + Guid.NewGuid().ToString("N");
        await using var channel = new NamedPipeChannel(pipeName);
        var completion = new TaskCompletionSource<byte[]>();

        channel.StartListening(async data =>
        {
            completion.TrySetResult(data);
            return await ValueTask.FromResult(data);
        });

        var payload = Encoding.UTF8.GetBytes("hello");
        var response = await channel.SendAndWaitAsync(payload, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        Assert.NotNull(response);
        Assert.Equal(payload, response);

        var received = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(payload, received);
    }

    [Fact]
    public void ClopPaths_OverridesWorkdirAndCreatesFolders()
    {
        var tempDir = FilePath.TempFile("clop-tests", "tmp", addUniqueSuffix: true).Parent;
        FilePath.Workdir = tempDir;

        Assert.True(FilePath.Workdir.Exists);
        Assert.True(FilePath.ProcessLogs.Exists);

        var logFile = FilePath.ProcessLogs.Append("demo.log");
        logFile.EnsureParentDirectoryExists();
        Assert.True(FilePath.ProcessLogs.Exists);
    }
}

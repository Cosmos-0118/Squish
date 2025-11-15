using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly FilePath _originalWorkdir = FilePath.Workdir;

    [Fact]
    public void LoadsDefaultsWhenConfigIsMissing()
    {
        using var temp = new TempDirectory();
        using var store = new SettingsStore(temp.Path);
        var defaultValue = store.Get(SettingsRegistry.EnableFloatingResults);

        Assert.True(defaultValue);
        Assert.True(File.Exists(Path.Combine(temp.Path, "config.json")));
    }

    [Fact]
    public void PersistsValuesAcrossInstances()
    {
        using var temp = new TempDirectory();
        using var store = new SettingsStore(temp.Path);

        store.Set(SettingsRegistry.EnableFloatingResults, false);

        using var rehydrated = new SettingsStore(temp.Path);
        Assert.False(rehydrated.Get(SettingsRegistry.EnableFloatingResults));
    }

    [Fact]
    public void RunsClopIgnoreMigration()
    {
        using var temp = new TempDirectory();
        var watchDir = Directory.CreateDirectory(Path.Combine(temp.Path, "watched"));
        var originalIgnore = Path.Combine(watchDir.FullName, ".clopignore");
        File.WriteAllText(originalIgnore, "ignored*");

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["values"] = new JsonObject
            {
                [SettingsRegistry.ImageDirs.Name] = new JsonArray(watchDir.FullName),
                [SettingsRegistry.VideoDirs.Name] = new JsonArray(watchDir.FullName)
            }
        };
        File.WriteAllText(Path.Combine(temp.Path, "config.json"), root.ToJsonString());

        using var _ = new SettingsStore(temp.Path);

        Assert.False(File.Exists(originalIgnore));
        Assert.True(File.Exists(Path.Combine(watchDir.FullName, ".clopignore-images")));
        Assert.True(File.Exists(Path.Combine(watchDir.FullName, ".clopignore-videos")));
    }

    [Fact]
    public async Task NotifiesWhenConfigChangesExternally()
    {
        using var temp = new TempDirectory();
        using var background = new SettingsStore(temp.Path);
        using var ui = new SettingsStore(temp.Path);

        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        background.SettingChanged += (sender, args) =>
        {
            if (args.Name == SettingsRegistry.PdfDirs.Name && args.Value is string[] paths)
            {
                tcs.TrySetResult(paths);
            }
        };

        var downloads = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(downloads);
        ui.Set(SettingsRegistry.PdfDirs, new[] { downloads });

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(completed, tcs.Task), "Background store did not observe external file change.");
        var observed = await tcs.Task;
        Assert.Contains(downloads, observed);
    }

    public void Dispose()
    {
        FilePath.Workdir = _originalWorkdir;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clop-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}

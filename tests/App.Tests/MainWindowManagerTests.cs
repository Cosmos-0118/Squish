using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Squish.App.Services;
using Xunit;

namespace Squish.App.Tests;

// Window creation needs a WPF Application on an STA thread, so these cover the paths that must not touch WPF:
// shutting down, and being asked to show a window when no Application exists.
public sealed class MainWindowManagerTests
{
    private static MainWindowManager Create()
        => new(new ServiceCollection().BuildServiceProvider(), NullLogger<MainWindowManager>.Instance);

    [Fact]
    public void ShowAfterExitIsRequestedCreatesNoWindow()
    {
        var manager = Create();
        manager.NotifyExiting();

        manager.Show();

        Assert.False(manager.IsOpen);
    }

    [Fact]
    public void ShowOnDispatcherWithoutAnApplicationIsANoOp()
    {
        var manager = Create();

        manager.ShowOnDispatcher();

        Assert.False(manager.IsOpen);
    }

    [Fact]
    public void OnlyTheMainWindowBlocksIdleTrimming()
    {
        try
        {
            IdleTrim.Configure();
            var skip = Squish.Core.Shared.WorkingSetTrimmer.ShouldSkip!;

            IdleTrim.MainWindowVisible = false;
            IdleTrim.HudVisible = true;
            Assert.False(skip());

            IdleTrim.MainWindowVisible = true;
            Assert.True(skip());
        }
        finally
        {
            IdleTrim.MainWindowVisible = false;
            IdleTrim.HudVisible = false;
            Squish.Core.Shared.WorkingSetTrimmer.ShouldSkip = null;
        }
    }
}

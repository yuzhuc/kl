using Xunit;

namespace Kl.Gui.Tests;

public sealed class TerminalSettingsTests
{
    [Fact]
    public void TerminalSettingsRoundTrip()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.TerminalShellIndex = 2;
        vm.SetTerminalPosition("right");
        vm.ShowTerminalPanelControl = false;
        vm.IsTerminalPanelOpen = true;
        vm.SetTerminalPanelSize(340);
        vm.SaveAppearanceSettings();

        var saved = store.Load();
        Assert.Equal("git-bash", saved.TerminalShell);
        Assert.Equal("right", saved.TerminalPosition);
        Assert.False(saved.ShowTerminalPanelControl);
        Assert.True(saved.TerminalPanelOpen);
        Assert.Equal(340, saved.TerminalPanelSize);

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.Equal(2, reloaded.TerminalShellIndex);
        Assert.Equal("git-bash", reloaded.TerminalShell);
        Assert.True(reloaded.IsTerminalRight);
        Assert.False(reloaded.IsTerminalBottom);
        Assert.False(reloaded.ShowTerminalPanelControl);
        Assert.True(reloaded.IsTerminalPanelOpen);
        Assert.Equal(340, reloaded.TerminalPanelSize);
    }

    [Fact]
    public void TerminalPanelSizeIsClamped()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.SetTerminalPanelSize(50);
        Assert.Equal(180, vm.TerminalPanelSize);

        vm.SetTerminalPanelSize(5000);
        Assert.Equal(600, vm.TerminalPanelSize);

        vm.SetTerminalPanelSize(0);
        Assert.Equal(180, vm.TerminalPanelSize);
    }
}

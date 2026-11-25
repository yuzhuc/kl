using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kl.Client;
using Kl.Gui.Services;
using Kl.Gui.Tests;
using Kl.Gui.ViewModels;

namespace Kl.Gui.HeadlessTests;

[Collection("avalonia")]
public sealed class MainWindowInteractionTests
{
    private readonly AvaloniaFixture _avalonia;

    public MainWindowInteractionTests(AvaloniaFixture avalonia)
    {
        _avalonia = avalonia;
    }

    [Fact]
    public void AuditRefreshButtonCallsCore() => _avalonia.Run(() =>
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var vm = TestData.CreateViewModel(temp, store, core);
        var window = new MainWindow(vm);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.OpenSettings(SettingsPane.Audit);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        core.AuditLimit = 0;

        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(control => control.Name == "AuditRefreshButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (core.AuditLimit == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, core.AuditLimit);
        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    });

    [Fact]
    public void PluginReloadButtonCallsCore() => _avalonia.Run(() =>
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        core.PluginListJson =
            """{"plugins":[{"id":"demo","version":"1.0","kind":"tool","enabled":true,"healthy":true}]}""";
        var vm = TestData.CreateViewModel(temp, store, core);
        var window = new MainWindow(vm);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.OpenSettings(SettingsPane.Plugins);
        vm.RefreshPluginsAsync().GetAwaiter().GetResult();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .First(control => control.Content as string == "重新载入" && control.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (core.ReloadedPlugins.Count == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("demo", core.ReloadedPlugins);
        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    });

    [Fact]
    public void RemoteCoreShowsNoticeAndDisablesLocalButtons() => _avalonia.Run(() =>
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { IsRemoteCore = true };
        var vm = TestData.CreateViewModel(temp, store, core);
        var window = new MainWindow(vm);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.OpenSettings(SettingsPane.Git);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var notice = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(control => control.Text == vm.RemoteCoreNotice
                && control.IsEffectivelyVisible);
        Assert.NotNull(notice);

        var refresh = window.GetVisualDescendants()
            .OfType<Button>()
            .First(control => control.Content as string == "刷新" && control.IsEffectivelyVisible);
        Assert.False(refresh.IsEnabled);

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    });

    [Fact]
    public void ComposerChipWidthFollowsTheModelName() => _avalonia.Run(() =>
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var snapshot = new CoreCatalogSnapshot(
            true,
            string.Empty,
            "test-core",
            1,
            "deepseek",
            "DS",
            ["DS", "deepseek-experimental-ultra-long-name"],
            true,
            Array.Empty<CoreSessionSummary>())
        {
            ModelOptions =
            [
                new CoreModelOption("deepseek", "DS", true, true)
                {
                    ReasoningEffort = "medium",
                    ReasoningEfforts = ["low", "medium", "high"]
                },
                new CoreModelOption("deepseek", "deepseek-experimental-ultra-long-name", true, true)
                {
                    ReasoningEffort = "medium",
                    ReasoningEfforts = ["low", "medium", "high"]
                }
            ]
        };
        var core = new FakeCoreSessionService { Snapshot = snapshot };
        var vm = TestData.CreateViewModel(temp, store, core);
        var window = new MainWindow(vm);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        vm.TestConnectionAsync().GetAwaiter().GetResult();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var chip = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(control => control.Name == "ReasoningChipButton");
        var shortRow = vm.ComposerModels.Single(row => row.Choice.Contains("DS"));
        var longRow = vm.ComposerModels.Single(row => row.Choice.Contains("ultra-long"));

        vm.SelectComposerModel(shortRow);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var shortWidth = chip.Bounds.Width;

        vm.SelectComposerModel(longRow);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var longWidth = chip.Bounds.Width;
        Assert.True(longWidth > shortWidth + 40, $"long={longWidth} short={shortWidth}");

        vm.SetComposerReasoningEffort("low");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var lowWidth = chip.Bounds.Width;
        vm.SetComposerReasoningEffort("high");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(lowWidth, chip.Bounds.Width, 1);

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    });
}

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kl.Gui.Tests;
using Kl.Gui.ViewModels;

namespace Kl.Gui.HeadlessTests;

[Collection("avalonia")]
public sealed class MainWindowHeadlessTests
{
    private readonly AvaloniaFixture _avalonia;

    public MainWindowHeadlessTests(AvaloniaFixture avalonia)
    {
        _avalonia = avalonia;
    }

    [Fact]
    public void WindowShowsAndEverySettingsPaneRenders() => _avalonia.Run(() =>
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var window = new MainWindow(vm);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);

        foreach (var pane in Enum.GetValues<SettingsPane>())
        {
            vm.OpenSettings(pane);
            Dispatcher.UIThread.RunJobs();

            var visible = window.GetLogicalDescendants()
                .OfType<StackPanel>()
                .Where(panel => panel.Classes.Contains("settings-pane") && panel.IsVisible)
                .ToList();

            Assert.Single(visible);
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    });

    [Fact]
    public void SettingsSearchHidesNonMatchingCards() => _avalonia.Run(() =>
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var window = new MainWindow(vm);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        vm.OpenSettings(SettingsPane.General);
        Dispatcher.UIThread.RunJobs();

        var search = window.FindControl<TextBox>("SettingsSearchBox");
        Assert.NotNull(search);
        search.Text = "no-such-setting";
        Dispatcher.UIThread.RunJobs();

        var noMatch = window.FindControl<TextBlock>("SettingsNoMatch");
        Assert.NotNull(noMatch);
        Assert.True(noMatch.IsVisible);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    });
}

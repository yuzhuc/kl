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

    [Fact]
    public void LanguageSwitchUpdatesXamlAndCachedLabels() => _avalonia.Run(() =>
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService())!;
        vm.LanguageIndex = 1;
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        List<string?> Texts() => window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .ToList();

        bool HasButtonContent(string text) => window.GetLogicalDescendants()
            .OfType<Button>()
            .Any(button => Equals(button.Content, text));
        bool HasPlaceholder(string text) => window.GetLogicalDescendants()
            .OfType<TextBox>()
            .Any(box => box.PlaceholderText == text);

        Assert.Contains("创建、学习和探索", Texts());
        Assert.Contains("构建、调试和发布", Texts());
        Assert.True(HasButtonContent("文件"));
        Assert.True(HasPlaceholder("搜索会话"));

        vm.LanguageIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Create, learn and explore", Texts());
        Assert.Contains("Build, debug and ship", Texts());
        Assert.DoesNotContain("创建、学习和探索", Texts());
        Assert.True(HasButtonContent("File"));
        Assert.True(HasPlaceholder("Search sessions"));

        vm.LanguageIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("创建、学习和探索", Texts());
        Assert.DoesNotContain("Create, learn and explore", Texts());
        Assert.True(HasButtonContent("文件"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    });
}

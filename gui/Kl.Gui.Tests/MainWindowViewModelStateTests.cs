using Kl.Gui.Services;
using Kl.Gui.ViewModels;

namespace Kl.Gui.Tests;

/// <summary>
/// View-model behaviour that stays local to the GUI shell: navigation, search,
/// automation editing, plugins, attachments and settings drafts. Core calls are
/// stubbed by <see cref="FakeCoreSessionService"/>.
/// </summary>
public sealed class MainWindowViewModelStateTests
{
    private static MainWindowViewModel CreateWithSessions(
        TempDirectory temp,
        StateStore store,
        FakeCoreSessionService core,
        params string[] titles)
    {
        TestData.Seed(store, temp.Path);
        core.Snapshot = new CoreCatalogSnapshot(
            true,
            string.Empty,
            "test-core",
            1,
            "fake",
            "fake-model",
            ["fake-model"],
            true,
            titles
                .Select((title, index) => TestData.Summary($"sess-{index + 1}", temp.Path, title))
                .ToArray());
        return TestData.CreateViewModel(temp, store, core);
    }

    [Fact]
    public void AutomationValidationEditAndDeletePersist()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.BeginCreateAutomation();
        vm.SaveAutomation();
        Assert.True(vm.HasAutomationDialogStatus);
        Assert.Empty(vm.Automations);

        vm.AutomationName = "初始任务";
        vm.AutomationPrompt = "初始指令";
        vm.SelectedAutomationInterval = vm.AutomationIntervals[0];
        vm.SaveAutomation();
        var automation = Assert.Single(vm.Automations);

        vm.BeginEditAutomation(automation);
        vm.AutomationName = "修改后的任务";
        vm.AutomationPrompt = "修改后的指令";
        vm.SelectedAutomationInterval = vm.AutomationIntervals[2];
        vm.SaveAutomation();

        var edited = Assert.Single(store.Load().Automations);
        Assert.Equal("修改后的任务", edited.Name);
        Assert.Equal("修改后的指令", edited.Prompt);
        Assert.Equal(360, edited.IntervalMinutes);

        vm.BeginDeleteAutomation(automation);
        vm.ConfirmDeleteAutomation();
        Assert.Empty(vm.Automations);
        Assert.Empty(store.Load().Automations);
    }

    [Fact]
    public void AutomationFiltersAndSearchUpdateVisibility()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.BeginCreateAutomation();
        vm.AutomationName = "活动任务";
        vm.AutomationPrompt = "检查状态";
        vm.SelectedAutomationInterval = vm.AutomationIntervals[0];
        vm.SaveAutomation();
        var active = Assert.Single(vm.Automations);
        vm.ToggleAutomation(active);

        vm.BeginCreateAutomation();
        vm.AutomationName = "暂停任务";
        vm.AutomationPrompt = "等待恢复";
        vm.SelectedAutomationInterval = vm.AutomationIntervals[0];
        vm.SaveAutomation();
        var paused = vm.Automations.Single(item => item.Name == "暂停任务");

        vm.SetAutomationFilter(AutomationFilter.Active);
        Assert.True(active.IsVisible);
        Assert.False(paused.IsVisible);
        Assert.False(vm.ShowNoAutomations);

        vm.SetAutomationFilter(AutomationFilter.Ended);
        Assert.False(active.IsVisible);
        Assert.True(paused.IsVisible);

        vm.AutomationSearch = "不存在";
        Assert.True(vm.ShowNoAutomations);

        vm.AutomationSearch = "暂停";
        Assert.False(vm.ShowNoAutomations);
        Assert.Equal("2 个任务，1 个已启用。", vm.AutomationPageSummary);
    }

    [Fact]
    public void SessionSearchRenameAndDeleteStayLocal()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var vm = CreateWithSessions(temp, store, new FakeCoreSessionService(), "标题A", "标题B");
        var workspace = Assert.Single(vm.Workspaces);
        Assert.Equal(2, workspace.Sessions.Count);

        vm.SearchText = "不存在";
        Assert.True(vm.ShowNoSearchResults);
        Assert.False(vm.ShowNoSessions);

        vm.SearchText = "标题A";
        Assert.True(workspace.Sessions.Single(item => item.Title == "标题A").IsVisible);
        Assert.False(workspace.Sessions.Single(item => item.Title == "标题B").IsVisible);

        vm.SearchText = string.Empty;
        var first = workspace.Sessions.Single(item => item.Title == "标题A");
        vm.BeginRename(first);
        vm.RenameTitle = "重命名后的会话";
        vm.ConfirmRename();
        Assert.Equal("重命名后的会话", first.Title);
        Assert.Contains(store.Load().Workspaces[0].Sessions, item => item.Title == "重命名后的会话");

        vm.BeginDelete(first);
        vm.ConfirmDelete();
        Assert.Single(vm.Workspaces[0].Sessions);
        Assert.Single(store.Load().Workspaces[0].Sessions);
    }

    [Fact]
    public void NavigationAndSettingsPanesUpdateViewState()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.OpenSettings(SettingsPane.Models);
        Assert.True(vm.IsSettingsOpen);
        Assert.True(vm.IsSettingsModels);
        Assert.Equal("模型", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.About);
        Assert.True(vm.IsSettingsAbout);
        Assert.Contains("快捷键", vm.SettingsPaneTitle);

        vm.Navigate(AppSection.Projects);
        Assert.True(vm.IsProjectsPage);
        Assert.False(vm.IsChatPage);
        Assert.False(vm.IsSettingsOpen);

        vm.Navigate(AppSection.Automation);
        Assert.True(vm.IsAutomationPage);
        Assert.Equal("自动化任务", vm.SectionTitle);

        vm.ResetSettingsDefaults();
        Assert.Equal("完全访问", vm.SettingsPermissionMode);
        Assert.True(vm.IsSettingsStatusSuccess);

        vm.CloseSettings();
        Assert.False(vm.IsSettingsOpen);
    }

    [Fact]
    public void CodeReviewActionPreparesDraftPrompt()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.StartCodeReviewDraft();

        Assert.True(vm.IsChatPage);
        Assert.Empty(vm.Messages);
        Assert.Contains("请审查", vm.Composer);
    }

    [Fact]
    public void InteractionModeAndPinsPersist()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.SetInteractionMode(workMode: false);
        Assert.True(vm.IsChatMode);
        Assert.Equal("Chat", vm.ActiveModeLabel);
        Assert.Equal("chat", store.Load().InteractionMode);

        vm.ToggleAddProjectPinned();
        vm.ToggleCodeReviewPinned();
        Assert.True(vm.IsAddProjectPinned);
        Assert.True(vm.IsCodeReviewPinned);
        var loaded = store.Load();
        Assert.True(loaded.IsAddProjectPinned);
        Assert.True(loaded.IsCodeReviewPinned);
    }

    [Fact]
    public async Task EmptyDraftIsNotPersisted()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        await vm.NewSessionAsync();

        Assert.Empty(vm.Messages);
        Assert.Empty(store.Load().Workspaces[0].Sessions);
    }

    [Fact]
    public void WorkspaceCanBeAddedAndBecomesActive()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var secondPath = Path.Combine(temp.Path, "second-workspace");
        Directory.CreateDirectory(secondPath);

        Assert.True(vm.AddWorkspace(secondPath));

        Assert.Equal(2, vm.Workspaces.Count);
        var selected = vm.Workspaces.Single(workspace => workspace.Path == secondPath);
        Assert.True(selected.IsSelected);
        Assert.Equal(selected.Id, store.Load().ActiveWorkspaceId);
        Assert.Equal(2, vm.WorkspaceCount);
    }

    [Fact]
    public void PluginImportCopiesManifestAndAssets()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var source = Path.Combine(temp.Path, "source-plugin");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "plugin.json"),
            """{"name":"Imported Plugin","version":"2.0.0","description":"Imported"}""");
        File.WriteAllText(Path.Combine(source, "asset.txt"), "asset");

        Assert.True(vm.ImportPlugin(source));
        var plugin = Assert.Single(vm.Plugins);
        Assert.Equal("Imported Plugin", plugin.Name);
        Assert.True(File.Exists(Path.Combine(temp.PluginDirectory, "source-plugin", "asset.txt")));
        Assert.True(vm.HasPlugins);
        Assert.False(vm.ShowNoPlugins);

        var invalid = Path.Combine(temp.Path, "plain-directory");
        Directory.CreateDirectory(invalid);
        Assert.False(vm.ImportPlugin(invalid));
        Assert.Contains("plugin.json", vm.ErrorMessage);
    }

    [Fact]
    public void AttachmentLimitsRejectExtraAndUnsupportedFiles()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        var paths = new List<string>();
        for (var index = 0; index < 9; index++)
        {
            var path = Path.Combine(temp.Path, $"file-{index}.md");
            File.WriteAllText(path, "x");
            paths.Add(path);
        }

        vm.AddAttachments(paths);

        Assert.Equal(8, vm.PendingAttachments.Count);
        Assert.True(vm.HasPendingAttachments);
        Assert.Contains("最多附加 8 个文件", vm.ErrorMessage);

        var png = Path.Combine(temp.Path, "image.png");
        File.WriteAllText(png, "x");
        var second = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        second.AddAttachments([png]);
        Assert.Empty(second.PendingAttachments);
        Assert.Contains("暂不支持该格式", second.ErrorMessage);

        var third = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        third.AddAttachments([Path.Combine(temp.Path, "missing.md")]);
        Assert.Empty(third.PendingAttachments);
        Assert.Contains("文件不存在", third.ErrorMessage);
    }

    [Fact]
    public void ReportAndDismissErrorToggleBanner()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.ReportError("失败");
        Assert.True(vm.HasError);
        Assert.Equal("失败", vm.ErrorMessage);

        vm.DismissError();
        Assert.False(vm.HasError);
    }
}

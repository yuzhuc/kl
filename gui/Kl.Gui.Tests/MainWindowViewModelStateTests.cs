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
        Assert.Equal("模型与连接", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Notifications);
        Assert.True(vm.IsSettingsNotifications);
        Assert.Equal("通知", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Appearance);
        Assert.Equal("外观", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Memory);
        Assert.True(vm.IsSettingsMemory);
        Assert.Equal("记忆", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Archived);
        Assert.Equal("已归档的聊天", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Shortcuts);
        Assert.Equal("键盘快捷键", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Usage);
        Assert.True(vm.IsSettingsUsage);
        Assert.Equal("使用情况和计费", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Integration);
        Assert.True(vm.IsSettingsIntegration);
        Assert.Equal("集成", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Data);
        Assert.Equal("存储", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.DataManagement);
        Assert.True(vm.IsSettingsDataManagement);
        Assert.Equal("数据管理", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Config);
        Assert.True(vm.IsSettingsConfig);
        Assert.Equal("配置", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Import);
        Assert.Equal("导入", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Profile);
        Assert.Equal("个人资料", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Account);
        Assert.True(vm.IsSettingsAccount);
        Assert.Equal("账户", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Ads);
        Assert.Equal("广告设置", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Parental);
        Assert.Equal("家长控制", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Trusted);
        Assert.Equal("受信任联系人", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Voice);
        Assert.Equal("语音", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Storage);
        Assert.Equal("存储空间", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Personalization);
        Assert.Equal("个性化", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Mini);
        Assert.Equal("迷你界面和智能伙伴", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Plugins);
        Assert.True(vm.IsSettingsPlugins);
        Assert.Equal("插件", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Passwords);
        Assert.Equal("密码", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.ComputerUse);
        Assert.Equal("电脑操控", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Browser);
        Assert.Equal("浏览器", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.CloudPc);
        Assert.Equal("云电脑", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Hooks);
        Assert.Equal("钩子", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Connections);
        Assert.Equal("连接", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.CodeReview);
        Assert.Equal("代码审查", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Git);
        Assert.Equal("Git", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Environments);
        Assert.Equal("环境", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Worktrees);
        Assert.Equal("Worktrees", vm.SettingsPaneTitle);

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
    public async Task NewSessionKeepsUnsentDraftComposer()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.Composer = "写一半的提示词";
        await vm.NewSessionAsync();
        Assert.Equal("写一半的提示词", vm.Composer);

        vm.StartCodeReviewDraft();
        await vm.NewSessionAsync();
        Assert.Empty(vm.Composer);
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
    public void PluginSearchFiltersByNameAndDescription()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        Directory.CreateDirectory(Path.Combine(temp.PluginDirectory, "alpha"));
        Directory.CreateDirectory(Path.Combine(temp.PluginDirectory, "beta"));
        File.WriteAllText(
            Path.Combine(temp.PluginDirectory, "alpha", "plugin.json"),
            """{"name":"Alpha","version":"1.0.0","description":"取色工具"}""");
        File.WriteAllText(
            Path.Combine(temp.PluginDirectory, "beta", "plugin.json"),
            """{"name":"Beta","version":"1.0.0","description":"terminal helper"}""");
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        vm.RefreshPlugins();

        Assert.Equal(2, vm.Plugins.Count);

        vm.PluginSearch = "term";
        Assert.Equal("Beta", Assert.Single(vm.Plugins).Name);
        Assert.False(vm.ShowNoPluginMatches);

        vm.PluginSearch = "missing";
        Assert.Empty(vm.Plugins);
        Assert.True(vm.ShowNoPluginMatches);
        Assert.False(vm.ShowNoPlugins);

        vm.PluginSearch = "";
        Assert.Equal(2, vm.Plugins.Count);
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
    public void NotificationFilterAndSidebarWidthPresetsPersist()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.SetNotificationFilter("pinned");
        Assert.True(vm.IsNotificationFilterPinned);
        Assert.Equal("pinned", store.Load().NotificationFilter);

        vm.SetSidebarWidth(360);
        Assert.True(vm.IsSidebarWide);
        Assert.False(vm.IsSidebarNarrow);
        Assert.Equal(360, store.Load().SidebarWidth);

        vm.SetSidebarWidth(238);
        Assert.True(vm.IsSidebarNarrow);
        Assert.False(vm.IsSidebarWide);

        vm.RestoreLastSession = false;
        Assert.False(store.Load().RestoreLastSession);
    }

    [Fact]
    public void UsagePageAggregatesPersistedSessionUsage()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var first = TestData.StoredSession("s-usage-1", "会话一");
        first.InputTokens = 120;
        first.OutputTokens = 30;
        first.CostUsd = 0.002;
        var second = TestData.StoredSession("s-usage-2", "会话二");
        second.InputTokens = 80;
        second.OutputTokens = 20;
        second.CostUsd = 0.001;
        TestData.Seed(store, temp.Path, first, second);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                "fake",
                "fake-model",
                ["fake-model"],
                true,
                [
                    TestData.Summary("s-usage-1", temp.Path, "会话一"),
                    TestData.Summary("s-usage-2", temp.Path, "会话二")
                ])
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.SelectSettingsPane(SettingsPane.Usage);

        Assert.True(vm.IsSettingsUsage);
        Assert.Equal("使用情况和计费", vm.SettingsPaneTitle);
        Assert.Equal(200, vm.UsageTotalInputTokens);
        Assert.Equal(50, vm.UsageTotalOutputTokens);
        Assert.Equal(0.003, vm.UsageTotalCostUsd, 6);
        Assert.True(vm.ShowUsageRows);
        Assert.Equal(new[] { "会话一", "会话二" }, vm.UsageRows.Select(row => row.Title));
    }

    [Fact]
    public async Task MemoryPageListsReadsWritesAndDeletesThroughCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var session = TestData.StoredSession("s-mem-1", "记忆会话");
        TestData.Seed(store, temp.Path, session);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                "fake",
                "fake-model",
                ["fake-model"],
                true,
                [TestData.Summary("s-mem-1", temp.Path, "记忆会话")])
        };
        core.RequestResponses["memory.list"] =
            """{"entries":[{"name":"notes.md","scope":"workspace","size_bytes":7}]}""";
        core.RequestResponses["memory.read"] =
            """{"name":"notes.md","scope":"workspace","content":"记住 42"}""";
        core.RequestResponses["memory.write"] =
            """{"name":"notes.md","scope":"workspace","size_bytes":9}""";
        core.RequestResponses["memory.delete"] =
            """{"name":"notes.md","scope":"workspace","deleted":true}""";
        var vm = TestData.CreateViewModel(temp, store, core);
        await vm.OpenSessionAsync(vm.Workspaces[0].Sessions[0]);

        vm.SelectSettingsPane(SettingsPane.Memory);
        await vm.RefreshMemoryAsync();

        var entry = Assert.Single(vm.MemoryEntries);
        Assert.Equal("notes.md", entry.Name);
        Assert.Equal("7 B", entry.SizeLabel);
        Assert.True(vm.ShowMemoryEntries);

        await vm.LoadMemoryAsync("notes.md");
        Assert.Equal("notes.md", vm.MemoryName);
        Assert.Equal("记住 42", vm.MemoryContent);

        vm.MemoryContent = "更新后的内容";
        await vm.SaveMemoryAsync();
        Assert.Contains(core.Requests, request => request.Method == "memory.write");

        vm.StartNewMemory();
        Assert.Equal(string.Empty, vm.MemoryName);
        Assert.False(vm.CanSaveMemory);

        vm.MemoryName = "notes.md";
        await vm.DeleteMemoryAsync();
        Assert.Equal(string.Empty, vm.MemoryName);
        Assert.Contains(core.Requests, request => request.Method == "memory.delete");
    }

    [Fact]
    public async Task IntegrationPageLoadsMcpToolsFromCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            McpToolsJson = """{"tools":[{"name":"mcp__fs__read","description":"读取文件","source":"mcp"}]}"""
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.SelectSettingsPane(SettingsPane.Integration);
        await vm.RefreshMcpToolsAsync();

        Assert.True(vm.IsSettingsIntegration);
        Assert.Equal("集成", vm.SettingsPaneTitle);
        var tool = Assert.Single(vm.McpTools);
        Assert.Equal("mcp__fs__read", tool.Name);
        Assert.Equal("读取文件", tool.Description);
        Assert.True(vm.ShowMcpTools);
        Assert.True(core.McpListCalls > 0);
    }

    [Fact]
    public async Task ConfigPageListsAgentSettingsFromCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            ConfigJson = """
                {"path":"C:/kl/config.toml",
                 "config":{"agent":{"provider":"anthropic","model":"claude","approval_mode":"suggest","reasoning_effort":"medium"}},
                 "providers":{"providers":[{"id":"anthropic"},{"id":"openai"}]}}
                """
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.SelectSettingsPane(SettingsPane.Config);
        await vm.RefreshConfigAsync();

        Assert.EndsWith("config.toml", vm.ConfigPathText);
        Assert.True(core.ConfigCalls > 0);
        Assert.Contains(vm.ConfigRows, row => row.Label == "Provider" && row.Value == "anthropic");
        Assert.Contains(vm.ConfigRows, row => row.Label == "审批模式" && row.Value == "suggest");
        Assert.Contains(vm.ConfigRows, row => row.Label == "提供方" && row.Value == "anthropic、openai");
    }

    [Fact]
    public async Task SkillsPageLoadsSkillsForActiveWorkspace()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            SkillsJson = """{"skills":[{"name":"deploy","description":"部署到预发环境"}]}"""
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.Navigate(AppSection.Plugins);
        vm.ShowSkillsList();
        await vm.RefreshSkillsAsync();

        Assert.True(vm.IsSkillsList);
        Assert.False(vm.IsPluginsList);
        Assert.Equal("技能", vm.SectionTitle);
        var skill = Assert.Single(vm.Skills);
        Assert.Equal("deploy", skill.Name);
        Assert.Equal("部署到预发环境", skill.Description);
        Assert.True(vm.ShowSkills);
        Assert.Contains(vm.Workspaces[0].Path, core.SkillWorkspaces);

        vm.ShowPluginsList();
        Assert.False(vm.IsSkillsList);
        Assert.True(vm.IsPluginsList);
        Assert.Equal("插件", vm.SectionTitle);
    }

    [Fact]
    public void EverySettingsPaneHasATitle()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        foreach (var pane in Enum.GetValues<SettingsPane>())
        {
            vm.SelectSettingsPane(pane);
            Assert.False(string.IsNullOrWhiteSpace(vm.SettingsPaneTitle), pane.ToString());
        }
    }

    [Fact]
    public void SettingsDetailSheetOpensAndCloses()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.OpenSettingsDetail("源许可证", "该功能目前只有界面。");
        Assert.True(vm.IsSettingsDetailOpen);
        Assert.Equal("源许可证", vm.SettingsDetailTitle);
        Assert.Equal("该功能目前只有界面。", vm.SettingsDetailBody);

        vm.CloseSettingsDetail();
        Assert.False(vm.IsSettingsDetailOpen);

        vm.OpenSettingsDetail("历史", "占位");
        vm.CloseDialogs();
        Assert.False(vm.IsSettingsDetailOpen);
    }

    [Fact]
    public void SettingsPaneIsRememberedAcrossViewModels()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var first = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        first.OpenSettings(SettingsPane.Voice);
        Assert.Equal("Voice", store.Load().LastSettingsPane);

        var second = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        second.OpenSettings();
        Assert.True(second.IsSettingsVoice);
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

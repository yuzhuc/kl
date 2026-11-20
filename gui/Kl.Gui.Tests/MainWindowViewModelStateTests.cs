using System.Text.Json;
using Kl.Client;
using Kl.Gui.Models;
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
    public void DeletingASessionDeletesItInCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSessions(temp, store, core, "要删除");
        var session = vm.Workspaces.SelectMany(workspace => workspace.Sessions)
            .Single(item => item.Id == "sess-1");

        vm.BeginDelete(session);
        vm.ConfirmDelete();

        Assert.Equal("sess-1", Assert.Single(core.DeleteCalls));
        Assert.DoesNotContain(vm.Workspaces.SelectMany(workspace => workspace.Sessions),
            item => item.Id == "sess-1");
    }

    [Fact]
    public void CleanupDialogListsOnlyOldChats()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(
            store,
            temp.Path,
            TestData.StoredSession("sess-1", "旧聊天"),
            TestData.StoredSession("sess-2", "新聊天"));
        var core = new FakeCoreSessionService();
        core.Snapshot = new CoreCatalogSnapshot(
            true,
            string.Empty,
            "test-core",
            1,
            "fake",
            "fake-model",
            ["fake-model"],
            true,
            [
                TestData.Summary("sess-1", temp.Path, "旧聊天", updatedAt:
                    DateTimeOffset.Now.AddDays(-45).ToString("o")),
                TestData.Summary("sess-2", temp.Path, "新聊天", updatedAt:
                    DateTimeOffset.Now.AddDays(-2).ToString("o"))
            ]);
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.OpenCleanupDialog();

        Assert.True(vm.IsCleanupDialogOpen);
        Assert.Equal("sess-1", Assert.Single(vm.CleanupSessions).Id);
    }

    [Fact]
    public void PaletteSearchesPromptsFromTheHistoryFile()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var vm = CreateWithSessions(temp, store, new FakeCoreSessionService(), "月度汇报", "发布清单");
        File.WriteAllText(
            System.IO.Path.Combine(temp.Path, "history.jsonl"),
            "{\"session_id\":\"sess-2\",\"ts\":1,\"text\":\"请检查 flurble 的构建脚本\"}\n");

        vm.SearchText = "flurble";
        vm.RefreshPaletteSessions();

        var match = Assert.Single(vm.PaletteSessions);
        Assert.Equal("sess-2", match.Id);
    }

    [Fact]
    public async Task StorageUsageSumsRealHomeFolders()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var sessions = System.IO.Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(System.IO.Path.Combine(sessions, "a.jsonl"), new string('x', 2048));
        var attachments = System.IO.Path.Combine(temp.Path, "attachments", "sess-1");
        Directory.CreateDirectory(attachments);
        File.WriteAllText(System.IO.Path.Combine(attachments, "1-notes.txt"), new string('y', 1024));

        await vm.RefreshStorageAsync();

        Assert.Contains(vm.StorageFolders, folder => folder.Name.StartsWith("聊天") && folder.Bytes >= 2048);
        Assert.Contains(vm.StorageFolders, folder => folder.Name == "附件" && folder.Bytes == 1024);
        Assert.StartsWith("KL 占用 ", vm.StorageTotalLabel);
        Assert.True(vm.HasStorageUsage);
        Assert.Contains("KB", vm.StorageChatSizeLabel);
        Assert.Equal("1 KB", vm.StorageAttachmentSizeLabel);
    }

    [Fact]
    public void RemoteCoreSettingsPersistEndpointAndToken()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var tokenPath = System.IO.Path.Combine(temp.Path, "credentials", "core.token");

        vm.RemoteCoreEndpointDraft = "tcp://10.0.0.5:8787";
        vm.RemoteCoreTokenDraft = "s3cret";
        vm.SaveRemoteCoreSettings();

        Assert.Contains("已保存", vm.RemoteCoreStatus);
        Assert.Equal("tcp://10.0.0.5:8787", store.Load().RemoteCoreEndpoint);
        Assert.Equal("s3cret", File.ReadAllText(tokenPath));

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.Equal("tcp://10.0.0.5:8787", reloaded.RemoteCoreEndpointDraft);
        Assert.Equal("s3cret", reloaded.RemoteCoreTokenDraft);

        vm.RemoteCoreEndpointDraft = "build-box";
        vm.SaveRemoteCoreSettings();
        Assert.Contains("格式", vm.RemoteCoreStatus);

        vm.RemoteCoreEndpointDraft = "tcp://10.0.0.5:8787";
        vm.RemoteCoreTokenDraft = string.Empty;
        vm.SaveRemoteCoreSettings();
        Assert.Contains("令牌", vm.RemoteCoreStatus);

        vm.ClearRemoteCoreSettings();
        Assert.Null(store.Load().RemoteCoreEndpoint);
        Assert.False(File.Exists(tokenPath));
    }

    [Fact]
    public void RemoteCoreTlsFingerprintIsRequiredAndPersisted()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.RemoteCoreEndpointDraft = "tls://10.0.0.5:8787";
        vm.RemoteCoreTokenDraft = "s3cret";
        vm.SaveRemoteCoreSettings();
        Assert.Contains("指纹", vm.RemoteCoreStatus);
        Assert.Null(store.Load().RemoteCoreEndpoint);

        vm.RemoteCoreTlsFingerprintDraft = "xyz";
        vm.SaveRemoteCoreSettings();
        Assert.Contains("64", vm.RemoteCoreStatus);

        var fingerprint = new string('a', 64);
        vm.RemoteCoreTlsFingerprintDraft = fingerprint;
        vm.SaveRemoteCoreSettings();
        Assert.Contains("已保存", vm.RemoteCoreStatus);
        Assert.Equal(fingerprint.ToUpperInvariant(), store.Load().RemoteCoreTlsFingerprint);

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.Equal(fingerprint.ToUpperInvariant(), reloaded.RemoteCoreTlsFingerprintDraft);

        vm.ClearRemoteCoreSettings();
        Assert.Null(store.Load().RemoteCoreTlsFingerprint);
    }

    [Fact]
    public void AgentsEditorRoundTripsTheHomeAgentsFile()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.OpenAgentsEditor();
        Assert.True(vm.IsAgentsEditorOpen);
        Assert.Equal(string.Empty, vm.AgentsDraft);
        Assert.False(vm.HasAgentsStatus);

        vm.AgentsDraft = "- 始终使用中文回复。";
        vm.SaveAgents();
        Assert.Contains("已保存", vm.AgentsStatus);
        var path = System.IO.Path.Combine(temp.Path, "AGENTS.md");
        Assert.Equal("- 始终使用中文回复。", File.ReadAllText(path));

        vm.CloseAgentsEditor();
        Assert.False(vm.IsAgentsEditorOpen);
        vm.OpenAgentsEditor();
        Assert.Equal("- 始终使用中文回复。", vm.AgentsDraft);
    }

    [Fact]
    public void ArchiveSessionMirrorsTheFlagIntoCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSessions(temp, store, core, "归档我");
        var session = vm.Workspaces.SelectMany(workspace => workspace.Sessions)
            .Single(item => item.Id == "sess-1");

        vm.ArchiveSession(session);
        Assert.True(session.IsArchived);
        Assert.Equal(("sess-1", true), Assert.Single(core.ArchiveCalls));

        vm.UnarchiveSession(session);
        Assert.False(session.IsArchived);
        Assert.Equal(("sess-1", false), core.ArchiveCalls[1]);
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
    public async Task SessionSearchRenameAndDeleteUpdateStateAndCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSessions(temp, store, core, "标题A", "标题B");
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
        var firstId = first.Id;
        vm.BeginRename(first);
        vm.RenameTitle = "重命名后的会话";
        vm.ConfirmRename();
        await Task.Yield();
        Assert.Equal("重命名后的会话", first.Title);
        Assert.Equal((firstId, "重命名后的会话"), Assert.Single(core.RenameCalls));
        Assert.Contains(store.Load().Workspaces[0].Sessions, item => item.Title == "重命名后的会话");

        vm.BeginDelete(first);
        vm.ConfirmDelete();
        Assert.Single(vm.Workspaces[0].Sessions);
        Assert.Single(store.Load().Workspaces[0].Sessions);
    }

    [Fact]
    public async Task RenameFailureRestoresTheLocalTitle()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService
        {
            RenameFailure = new InvalidOperationException("core refused")
        };
        var vm = CreateWithSessions(temp, store, core, "标题A", "标题B");
        var first = Assert.Single(vm.Workspaces).Sessions.Single(item => item.Title == "标题A");

        vm.BeginRename(first);
        vm.RenameTitle = "不会生效";
        vm.ConfirmRename();
        await Task.Yield();

        Assert.Equal("标题A", first.Title);
        Assert.Contains(store.Load().Workspaces[0].Sessions, item => item.Title == "标题A");
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

        vm.SelectSettingsPane(SettingsPane.Providers);
        Assert.True(vm.IsSettingsProviders);
        Assert.Equal("模型提供商", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Usage);
        Assert.True(vm.IsSettingsUsage);
        Assert.Equal("使用情况和计费", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Config);
        Assert.True(vm.IsSettingsConfig);
        Assert.Equal("配置", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Storage);
        Assert.Equal("存储空间", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Personalization);
        Assert.Equal("个性化", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Plugins);
        Assert.True(vm.IsSettingsPlugins);
        Assert.Equal("插件", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Passwords);
        Assert.Equal("密码", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Hooks);
        Assert.Equal("钩子", vm.SettingsPaneTitle);

        vm.SelectSettingsPane(SettingsPane.Connections);
        Assert.Equal("连接", vm.SettingsPaneTitle);

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
    public void LastAssistantCodeBlockUsesTheNewestFence()
    {
        Assert.Equal(
            "second();\nstill();",
            MainWindowViewModel.ExtractLastCodeBlock(
                "intro\n```cs\nfirst();\n```\ntext\n```ts\nsecond();\nstill();\n```"));
        Assert.Null(MainWindowViewModel.ExtractLastCodeBlock("no code here"));
        Assert.Null(MainWindowViewModel.ExtractLastCodeBlock("```\nunclosed"));
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
    public async Task PluginImportRegistersTheDirectoryWithCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var vm = TestData.CreateViewModel(temp, store, core);
        var source = Path.Combine(temp.Path, "installable");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "plugin.json"),
            """{"name":"Installable","version":"1.0.0","description":"x"}""");

        Assert.True(vm.ImportPlugin(source));
        var target = Assert.IsType<string>(vm.LastImportedPluginPath);
        await vm.InstallPluginAsync(target);

        Assert.Equal(target, Assert.Single(core.InstalledPlugins));
        Assert.Contains("已载入", vm.PluginStatus);
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
        Assert.Equal(360, vm.SidebarWidth);
        Assert.Equal(360, store.Load().SidebarWidth);

        vm.SetSidebarWidth(238);
        Assert.Equal(238, vm.SidebarWidth);

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

        vm.SelectSettingsPane(SettingsPane.Plugins);
        await vm.RefreshMcpToolsAsync();

        Assert.True(vm.IsSettingsPlugins);
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
    public async Task AgentSettingsLoadAndSaveThroughCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            AgentConfigJson = """
                {"reasoning_effort":"high","web_search":false,"network_access":false,
                 "output_verbosity":"concise","reasoning_summary":"detailed",
                 "memory_enabled":false,"plugins_enabled":false,
                 "custom_instructions":"be terse","personal_style":"professional",
                 "warmth":"high","enthusiasm":"low","emoji_usage":"more",
                 "visual_answers":"off","heading_style":"detailed","quick_answers":false}
                """
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        await vm.RefreshAgentSettingsAsync();

        Assert.False(vm.WebSearch);
        Assert.False(vm.NetworkAccess);
        Assert.False(vm.MemoryEnabled);
        Assert.False(vm.PluginsEnabled);
        Assert.Equal(1, vm.OutputVerbosityIndex);
        Assert.Equal(2, vm.ReasoningSummaryIndex);
        Assert.Equal("be terse", vm.CustomInstructionsDraft);
        Assert.Equal(2, vm.PersonalStyleIndex);
        Assert.Equal(2, vm.WarmthIndex);
        Assert.Equal(1, vm.EnthusiasmIndex);
        Assert.Equal(2, vm.EmojiIndex);
        Assert.Equal(1, vm.VisualAnswersIndex);
        Assert.Equal(2, vm.HeadingStyleIndex);
        Assert.False(vm.QuickAnswers);

        vm.WebSearch = true;
        vm.NetworkAccess = true;
        vm.MemoryEnabled = true;
        vm.PluginsEnabled = true;
        vm.OutputVerbosityIndex = 2;
        vm.ReasoningSummaryIndex = 1;
        vm.CustomInstructionsDraft = "  answer briefly  ";
        vm.PersonalStyleIndex = 1;
        vm.WarmthIndex = 1;
        vm.EnthusiasmIndex = 2;
        vm.EmojiIndex = 1;
        vm.VisualAnswersIndex = 2;
        vm.HeadingStyleIndex = 1;
        vm.QuickAnswers = true;
        await vm.SaveCustomInstructionsAsync();

        Assert.Contains(core.AgentConfigSets, patch => ReadBool(patch, "web_search"));
        Assert.Contains(core.AgentConfigSets, patch => ReadBool(patch, "network_access"));
        Assert.Contains(core.AgentConfigSets, patch => ReadBool(patch, "memory_enabled"));
        Assert.Contains(core.AgentConfigSets, patch => ReadBool(patch, "plugins_enabled"));
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "output_verbosity") == "detailed");
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "reasoning_summary") == "off");
        Assert.Contains(core.AgentConfigSets,
            patch => ReadString(patch, "custom_instructions") == "  answer briefly  ");
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "personal_style") == "friendly");
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "warmth") == "low");
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "enthusiasm") == "high");
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "emoji_usage") == "less");
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "visual_answers") == "on");
        Assert.Contains(core.AgentConfigSets, patch => ReadString(patch, "heading_style") == "concise");
        Assert.Contains(core.AgentConfigSets, patch => ReadBool(patch, "quick_answers"));
    }

    private static bool ReadBool(JsonElement patch, string name) =>
        patch.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement patch, string name) =>
        patch.TryGetProperty(name, out var value) ? value.GetString() : null;

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
    public void RestoredSettingsPanesOpenDirectly()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.OpenSettings(SettingsPane.Storage);
        Assert.True(vm.IsSettingsStorage);
        Assert.Equal("存储空间", vm.SettingsPaneTitle);

        Assert.Equal("Storage", store.Load().LastSettingsPane);
    }

    [Fact]
    public void RemovedSettingsPaneFallsBackToGeneral()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var state = store.Load();
        state.LastSettingsPane = "Browser";
        store.Save(state);

        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        vm.OpenSettings();
        Assert.True(vm.IsSettingsGeneral);
    }

    [Fact]
    public void SettingsPaneIsRememberedAcrossViewModels()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var first = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        first.OpenSettings(SettingsPane.Appearance);
        Assert.Equal("Appearance", store.Load().LastSettingsPane);

        var second = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        second.OpenSettings();
        Assert.True(second.IsSettingsAppearance);
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

    [Fact]
    public void KeyboardShortcutsCanBeReboundPersistedAndReset()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var row = vm.ShortcutRows.Single(item => item.ActionId == "newChat");
        Assert.Equal("Ctrl+N", row.Gesture);

        vm.BeginShortcutCapture(row);
        Assert.True(vm.IsShortcutCapturing);
        Assert.True(vm.ApplyShortcutGesture("Ctrl+K"));

        Assert.False(vm.IsShortcutCapturing);
        Assert.Equal("Ctrl+K", row.Gesture);
        Assert.Equal("Ctrl+K", store.Load().ShortcutBindings["newChat"]);
        Assert.Equal(row, vm.FindShortcut("Ctrl+K"));

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var reloadedRow = reloaded.ShortcutRows.Single(item => item.ActionId == "newChat");
        Assert.Equal("Ctrl+K", reloadedRow.Gesture);
        reloaded.ResetShortcut(reloadedRow);
        Assert.Equal("Ctrl+N", reloadedRow.Gesture);
        Assert.False(store.Load().ShortcutBindings.ContainsKey("newChat"));
    }

    [Fact]
    public void KeyboardShortcutRejectsDuplicateAndFiltersRows()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var row = vm.ShortcutRows.Single(item => item.ActionId == "newChat");

        vm.BeginShortcutCapture(row);
        Assert.False(vm.ApplyShortcutGesture("Ctrl+Shift+A"));
        Assert.True(vm.IsShortcutCapturing);
        Assert.Contains("归档聊天", vm.ShortcutCaptureStatus);
        Assert.Equal("Ctrl+N", row.Gesture);

        vm.ShortcutSearch = "归档";
        Assert.False(row.IsVisible);
        Assert.True(vm.ShortcutRows.Single(item => item.ActionId == "archiveChat").IsVisible);
        Assert.False(vm.ShowNoShortcutMatches);

        vm.ShortcutSearch = "不存在";
        Assert.True(vm.ShowNoShortcutMatches);
    }

    [Fact]
    public void RealSettingsPersistAndRestore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.SendShortcutIndex = 1;
        vm.NumberShortcutsIndex = 1;
        vm.CloseWindowActionIndex = 2;
        vm.AutoArchiveIndex = 2;
        vm.TurnNotificationIndex = 1;
        vm.ApprovalNotifications = false;
        vm.QuestionNotifications = false;
        vm.NotificationSound = false;
        vm.ApprovalModeIndex = 1;
        vm.AccentColorIndex = 2;
        vm.CodeFontSize = 15;
        vm.SetReduceMotionMode("on");
        vm.UiFontIndex = 1;
        vm.ContentFontIndex = 2;
        vm.CodeFontIndex = 1;
        vm.UiFontWeightIndex = 2;
        vm.ContentFontWeightIndex = 1;
        vm.CodeFontWeightIndex = 2;
        vm.Contrast = 70;
        vm.TranslucentSidebar = true;
        vm.CompressHistory = false;
        vm.GitBranchPrefix = "team/";
        vm.SaveAppearanceSettings();

        var saved = store.Load();
        Assert.Equal("ctrlEnter", saved.SendShortcut);
        Assert.False(saved.NumberShortcuts);
        Assert.Equal("never", saved.CloseWindowAction);
        Assert.Equal(30, saved.AutoArchiveDays);
        Assert.Equal("always", saved.TurnNotifications);
        Assert.False(saved.ApprovalNotifications);
        Assert.False(saved.QuestionNotifications);
        Assert.False(saved.NotificationSound);
        Assert.Equal("帮我批准", saved.PermissionMode);
        Assert.Equal("#179E8A", saved.AccentColor);
        Assert.Equal(15, saved.ContentCodeFontSize);
        Assert.Equal("on", saved.ReduceMotion);
        Assert.Equal(1, saved.UiFontIndex);
        Assert.Equal(2, saved.ContentFontIndex);
        Assert.Equal(1, saved.CodeFontIndex);
        Assert.Equal(2, saved.UiFontWeightIndex);
        Assert.Equal(1, saved.ContentFontWeightIndex);
        Assert.Equal(2, saved.CodeFontWeightIndex);
        Assert.Equal(70, saved.Contrast);
        Assert.True(saved.TranslucentSidebar);
        Assert.False(saved.CompressHistory);
        Assert.Equal("team/", saved.GitBranchPrefix);

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.Equal(1, reloaded.SendShortcutIndex);
        Assert.Equal(1, reloaded.NumberShortcutsIndex);
        Assert.False(reloaded.NumberShortcuts);
        Assert.Equal(2, reloaded.CloseWindowActionIndex);
        Assert.Equal(2, reloaded.AutoArchiveIndex);
        Assert.Equal(1, reloaded.TurnNotificationIndex);
        Assert.False(reloaded.ApprovalNotifications);
        Assert.False(reloaded.QuestionNotifications);
        Assert.False(reloaded.NotificationSound);
        Assert.False(reloaded.IsPermissionFull);
        Assert.False(reloaded.SendsOnEnter);
        Assert.Equal(2, reloaded.AccentColorIndex);
        Assert.Equal(15, reloaded.CodeFontSize);
        Assert.True(reloaded.IsReduceMotionOn);
        Assert.True(reloaded.ReduceMotion);
        Assert.Equal(1, reloaded.UiFontIndex);
        Assert.Equal(2, reloaded.ContentFontIndex);
        Assert.Equal(1, reloaded.CodeFontIndex);
        Assert.Equal(2, reloaded.UiFontWeightIndex);
        Assert.Equal(1, reloaded.ContentFontWeightIndex);
        Assert.Equal(2, reloaded.CodeFontWeightIndex);
        Assert.Equal(70, reloaded.Contrast);
        Assert.True(reloaded.TranslucentSidebar);
        Assert.False(reloaded.CompressHistory);
        Assert.Equal("team/", reloaded.GitBranchPrefix);
    }

    [Fact]
    public void InvalidSettingsFallBackToSafeDefaults()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var state = store.Load();
        state.SendShortcut = "bogus";
        state.CloseWindowAction = "sometimes";
        state.TurnNotifications = "often";
        state.AutoArchiveDays = -5;
        state.UiFontIndex = 99;
        state.ContentFontIndex = -1;
        state.CodeFontIndex = 9;
        state.UiFontWeightIndex = 9;
        state.ContentFontWeightIndex = -2;
        state.CodeFontWeightIndex = 7;
        state.Contrast = 500;
        store.Save(state);

        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.Equal(0, vm.SendShortcutIndex);
        Assert.Equal(0, vm.CloseWindowActionIndex);
        Assert.Equal(0, vm.TurnNotificationIndex);
        Assert.Equal(0, vm.AutoArchiveIndex);
        Assert.True(vm.SendsOnEnter);
        Assert.Equal(1, vm.UiFontIndex);
        Assert.Equal(0, vm.ContentFontIndex);
        Assert.Equal(2, vm.CodeFontIndex);
        Assert.Equal(2, vm.UiFontWeightIndex);
        Assert.Equal(0, vm.ContentFontWeightIndex);
        Assert.Equal(2, vm.CodeFontWeightIndex);
        Assert.Equal(100, vm.Contrast);
    }

    [Fact]
    public async Task AutoArchiveMovesOnlyInactiveChats()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(
            store,
            temp.Path,
            TestData.StoredSession("sess-old", "旧聊天"),
            TestData.StoredSession("sess-new", "新聊天"));
        var state = store.Load();
        state.AutoArchiveDays = 30;
        store.Save(state);
        var core = new FakeCoreSessionService();
        core.Snapshot = new CoreCatalogSnapshot(
            true,
            string.Empty,
            "test-core",
            1,
            "fake",
            "fake-model",
            ["fake-model"],
            true,
            [
                TestData.Summary("sess-old", temp.Path, "旧聊天", updatedAt:
                    DateTimeOffset.Now.AddDays(-45).ToString("o")),
                TestData.Summary("sess-new", temp.Path, "新聊天", updatedAt:
                    DateTimeOffset.Now.AddDays(-2).ToString("o"))
            ]);

        var vm = TestData.CreateViewModel(temp, store, core);
        for (var i = 0; i < 50 && core.ArchiveCalls.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(("sess-old", true), Assert.Single(core.ArchiveCalls));
        Assert.True(vm.Workspaces.SelectMany(workspace => workspace.Sessions)
            .Single(item => item.Id == "sess-old").IsArchived);
        Assert.False(vm.Workspaces.SelectMany(workspace => workspace.Sessions)
            .Single(item => item.Id == "sess-new").IsArchived);
    }

    [Fact]
    public void FinishedTurnsAndApprovalsRequestWindowAttention()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var vm = TestData.CreateViewModel(temp, store, core);
        var kinds = new List<AttentionKind>();
        vm.AttentionRequested += (kind, _) => kinds.Add(kind);

        core.Raise(new SessionEvent(SessionEventKind.TurnCompleted, Status: "completed"));
        core.Raise(new SessionEvent(SessionEventKind.ApprovalRequested, ApprovalId: "a-1"));
        core.Raise(new SessionEvent(
            SessionEventKind.ApprovalRequested,
            Name: "ask_user",
            ApprovalId: "a-2"));

        Assert.Equal(
            new[] { AttentionKind.TurnCompleted, AttentionKind.Approval, AttentionKind.Question },
            kinds);
    }

    [Fact]
    public async Task CorePluginsMergeWithLocalManifestsAndToggle()
    {
        using var temp = new TempDirectory();
        var pluginDirectory = System.IO.Path.Combine(temp.PluginDirectory, "demo");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            System.IO.Path.Combine(pluginDirectory, "plugin.json"),
            "{\"name\":\"demo\",\"version\":\"1.2\",\"description\":\"示例插件\"}");

        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            PluginListJson = """
                {"plugins":[{"id":"demo","version":"1.2","kind":"tool","enabled":true,"healthy":true}]}
                """
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        await vm.RefreshPluginsAsync();

        var plugin = Assert.Single(vm.Plugins);
        Assert.Equal("demo", plugin.Id);
        Assert.True(plugin.Enabled);
        Assert.Equal("已启用", plugin.StatusText);
        Assert.Equal("示例插件", plugin.Description);

        await vm.SetPluginEnabledAsync(plugin, false);

        Assert.False(plugin.Enabled);
        Assert.Equal("已停用", plugin.StatusText);
        Assert.Equal(("demo", false), Assert.Single(core.PluginEnabledCalls));
    }

    [Fact]
    public async Task WorktreesSettingsListGitWorktrees()
    {
        using var temp = new TempDirectory();
        var projectPath = System.IO.Path.Combine(temp.Path, "project");
        var worktreePath = System.IO.Path.Combine(temp.Path, "wt");
        Directory.CreateDirectory(projectPath);
        RunGitTestCommand(projectPath, "init -q");
        RunGitTestCommand(projectPath, "-c user.name=Test -c user.email=test@example.com commit --allow-empty -m init");
        RunGitTestCommand(projectPath, $"worktree add -q -b feature \"{worktreePath}\"");

        var store = temp.CreateStore();
        TestData.Seed(store, projectPath);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        await vm.RefreshWorktreesAsync();

        Assert.True(vm.HasWorktrees);
        Assert.Equal(2, vm.Worktrees.Count);
        Assert.Contains(vm.Worktrees, row => row.Branch == "feature");
    }

    [Fact]
    public async Task ReviewSettingsPersistAndRunParsesFindings()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.ReviewModeIndex = 2;
        vm.ReviewFullFiles = true;
        vm.ReviewInstructions = "check concurrency";

        var saved = store.Load();
        Assert.Equal("strict", saved.ReviewMode);
        Assert.True(saved.ReviewFullFiles);
        Assert.Equal("check concurrency", saved.ReviewInstructions);

        await vm.RunReviewAsync();

        Assert.True(core.ReviewCalls > 0);
        var finding = Assert.Single(vm.ReviewFindings);
        Assert.Equal("high", finding.Severity);
        Assert.Equal("src/a.rs:3", finding.Location);
        Assert.Equal("secret-openai", finding.Rule);
        Assert.True(finding.IsHigh);
        Assert.Contains("1 finding", vm.ReviewStatusText);
    }

    [Fact]
    public async Task WorktreeSettingsPersistAndCreateDelete()
    {
        using var temp = new TempDirectory();
        var projectPath = System.IO.Path.Combine(temp.Path, "project");
        var root = System.IO.Path.Combine(temp.Path, "worktrees");
        Directory.CreateDirectory(projectPath);
        RunGitTestCommand(projectPath, "init -q");
        RunGitTestCommand(projectPath, "-c user.name=Test -c user.email=test@example.com commit --allow-empty -m init");

        var store = temp.CreateStore();
        TestData.Seed(store, projectPath);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.WorktreeRoot = root;
        vm.WorktreeFetchBeforeCreate = false;
        vm.WorktreeConfirmDelete = true;
        vm.GitBranchPrefix = "kl/";
        var saved = store.Load();
        Assert.Equal(root, saved.WorktreeRoot);
        Assert.False(saved.WorktreeFetchBeforeCreate);
        Assert.True(saved.WorktreeConfirmDelete);

        vm.NewWorktreeName = "bad/name";
        Assert.False(vm.CanCreateWorktree);
        await vm.CreateWorktreeAsync();
        Assert.Contains("名称", vm.WorktreesStatusText);

        vm.NewWorktreeName = "demo";
        Assert.True(vm.CanCreateWorktree);
        await vm.CreateWorktreeAsync();
        var created = Assert.Single(vm.Worktrees, row => row.Path.EndsWith("demo", StringComparison.Ordinal));
        Assert.True(Directory.Exists(created.Path));
        Assert.Equal("kl/demo", created.Branch);

        vm.GitBranchPrefix = "bad prefix";
        vm.NewWorktreeName = "other";
        await vm.CreateWorktreeAsync();
        Assert.Contains("前缀", vm.WorktreesStatusText);

        vm.GitBranchPrefix = "kl/";
        vm.RequestDeleteWorktree(created);
        Assert.True(vm.IsWorktreeDeleteConfirmOpen);
        await vm.ConfirmDeleteWorktreeAsync();
        Assert.DoesNotContain(vm.Worktrees, row => row.Path.EndsWith("demo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteCoreDisablesLocalGitWorktreeAndImport()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { IsRemoteCore = true };
        var vm = TestData.CreateViewModel(temp, store, core);

        Assert.True(vm.IsRemoteCore);
        Assert.Contains("远程", vm.RemoteCoreNotice);

        await vm.RefreshGitAsync();
        Assert.Contains("远程", vm.GitStatusText);
        Assert.Equal("—", vm.GitBranchText);

        await vm.RefreshWorktreesAsync();
        Assert.Contains("远程", vm.WorktreesStatusText);
        Assert.Empty(vm.Worktrees);

        vm.NewWorktreeName = "demo";
        await vm.CreateWorktreeAsync();
        Assert.Contains("远程", vm.WorktreesStatusText);

        await vm.DiscoverImportsAsync();
        Assert.Contains("远程", vm.ImportStatusText);

        Assert.False(vm.ImportPlugin(temp.Path));
        Assert.Contains("远程", vm.ErrorMessage);
    }

    [Fact]
    public async Task AgentPresetsAndQualityAreReadFromCoreAndApplied()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var vm = TestData.CreateViewModel(temp, store, core);

        await vm.RefreshAgentPresetsAsync();

        Assert.Equal(3, vm.AgentPresetOptions.Count);
        Assert.Equal(0, vm.AgentPresetIndex);

        vm.AgentPresetIndex = 2;
        Assert.Equal(("plan", string.Empty), core.TurnPreferences);

        vm.QualityModeIndex = 3;
        Assert.Equal(("plan", "deep"), core.TurnPreferences);

        var saved = store.Load();
        Assert.Equal("plan", saved.AgentPreset);
        Assert.Equal("deep", saved.QualityMode);
    }

    [Fact]
    public async Task AuditPageReadsTailRecords()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var vm = TestData.CreateViewModel(temp, store, core);

        await vm.RefreshAuditAsync();

        Assert.Equal(200, core.AuditLimit);
        var row = Assert.Single(vm.AuditRecords);
        Assert.Equal("shell · execute · allow", row.Summary);
        Assert.Equal("成功", row.OkLabel);
        Assert.True(vm.ShowAuditRecords);
    }

    [Fact]
    public async Task SkillBodyLoadsThroughCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path, TestData.StoredSession("s1", "会话"));
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
                [TestData.Summary("s1", temp.Path, "会话")])
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));
        await vm.OpenSkillAsync("demo");

        Assert.Equal(["demo"], core.RequestedSkillBodies);
        Assert.True(vm.ShowSkillDetail);
        Assert.Equal("demo", vm.SelectedSkillName);
        Assert.Contains("Demo skill", vm.SelectedSkillContent);

        vm.CloseSkillDetail();
        Assert.False(vm.ShowSkillDetail);
    }

    private static void RunGitTestCommand(string workspace, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workspace}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit(10000);
    }

    [Fact]
    public async Task GitSettingsReportTheActiveProject()
    {
        using var temp = new TempDirectory();
        var projectPath = System.IO.Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectPath);
        using (var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"init -q \"{projectPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        }))
        {
            init?.WaitForExit();
        }
        File.WriteAllText(System.IO.Path.Combine(projectPath, "a.txt"), "one");

        var store = temp.CreateStore();
        TestData.Seed(store, projectPath);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        await vm.RefreshGitAsync();

        Assert.NotEqual("—", vm.GitBranchText);
        Assert.Contains("未提交更改", vm.GitStatusText);
    }

    [Fact]
    public async Task RemoteCoreTestReportsCurrentCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.RemoteCoreEndpointDraft = string.Empty;
        await vm.TestRemoteCoreConnectionAsync();

        Assert.Contains("当前 Core 可用", vm.RemoteCoreStatus);
        Assert.Contains("test-core", vm.RemoteCoreStatus);
    }

    [Fact]
    public async Task RemoteCoreTestRejectsInvalidEndpoint()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.RemoteCoreEndpointDraft = "bad";
        await vm.TestRemoteCoreConnectionAsync();

        Assert.Contains("格式", vm.RemoteCoreStatus);
    }

    [Fact]
    public async Task RemoteCoreTestRequiresTokenForRemoteHost()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.RemoteCoreEndpointDraft = "tcp://example.com:7878";
        vm.RemoteCoreTokenDraft = string.Empty;
        await vm.TestRemoteCoreConnectionAsync();

        Assert.Contains("令牌", vm.RemoteCoreStatus);
    }

    [Fact]
    public void HooksSettingsReportTheLocalConfig()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.RefreshHooks();
        Assert.Contains("未找到", vm.HooksStatusText);

        File.WriteAllText(
            System.IO.Path.Combine(temp.Path, "hooks.toml"),
            "[hooks]" + Environment.NewLine + "pre_tool = []" + Environment.NewLine);
        vm.RefreshHooks();
        Assert.Contains("已配置 2 行", vm.HooksStatusText);
    }

    [Fact]
    public void ProjectAgentsEditorWritesIntoTheWorkspace()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var projectPath = System.IO.Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectPath);
        TestData.Seed(store, projectPath);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var workspace = vm.Workspaces.First();

        vm.OpenProjectAgentsEditor(workspace);
        Assert.Contains("项目指令", vm.AgentsEditorHint);
        vm.AgentsDraft = "- 使用 xUnit。";
        vm.SaveAgents();

        Assert.Equal("- 使用 xUnit。", File.ReadAllText(System.IO.Path.Combine(projectPath, "AGENTS.md")));
        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "AGENTS.md")));
        Assert.Contains("已保存", vm.AgentsStatus);

        vm.CloseAgentsEditor();
        vm.OpenAgentsEditor();
        Assert.Contains("全局指令", vm.AgentsEditorHint);
        Assert.Equal(string.Empty, vm.AgentsDraft);
    }

    [Fact]
    public async Task FollowUpMessagesQueueAndSendAfterTheTurn()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var firstTurn = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTurn = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        core.TurnHandler = (_, _) =>
            Interlocked.Increment(ref calls) == 1 ? firstTurn.Task : secondTurn.Task;
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.Composer = "第一条";
        var running = vm.SendAsync();
        Assert.True(vm.IsBusy);

        vm.Composer = "第二条";
        Assert.True(vm.CanQueueSend);
        await vm.SendAsync();

        Assert.Equal(1, vm.QueuedTurnCount);
        Assert.True(vm.HasQueuedTurns);
        Assert.Equal(string.Empty, vm.Composer);
        Assert.Contains("已排队 1 条", vm.ComposerPlaceholder);

        firstTurn.SetResult(new TurnOutcome("completed", string.Empty));
        for (var i = 0; i < 100 && core.TurnPrompts.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2, core.TurnPrompts.Count);
        Assert.Contains("第二条", core.TurnPrompts[1]);
        Assert.False(vm.HasQueuedTurns);

        secondTurn.SetResult(new TurnOutcome("completed", string.Empty));
        await running;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ConfigPaneReadsCoreSandboxMode()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            ConfigJson = """
                {"path":"C:/kl/config.toml","config":{"sandbox":{"mode":"strict"},"agent":{"provider":"p","model":"m"}}}
                """
        };
        var vm = TestData.CreateViewModel(temp, store, core);

        await vm.RefreshConfigAsync();

        Assert.Equal("严格隔离", vm.CoreSandboxText);
        Assert.Contains(vm.ConfigRows, row => row.Label == "模型" && row.Value == "m");
    }

    [Fact]
    public void SavedLibraryPromptsAppearOnTheHomeScreen()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        Assert.False(vm.HasHomeSuggestions);
        vm.LibraryDraftTitle = "审查";
        vm.LibraryDraftContent = "请审查这段代码";
        vm.SaveLibraryItem();

        var suggestion = Assert.Single(vm.HomeSuggestions);
        Assert.Equal("审查", suggestion.Title);

        vm.DeleteLibraryItem(suggestion);
        Assert.False(vm.HasHomeSuggestions);
    }

    [Fact]
    public async Task DeleteAllMemoriesRemovesEveryUserEntry()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        core.RequestResponses["memory.list"] =
            "{\"entries\":[{\"name\":\"pref\",\"size_bytes\":12},{\"name\":\"style\",\"size_bytes\":7}]}";
        await core.OpenSessionAsync(
            new CoreSessionRequest { Workspace = temp.Path, SessionId = "sess-1" },
            null,
            CancellationToken.None);
        var vm = TestData.CreateViewModel(temp, store, core);

        await vm.DeleteAllMemoriesAsync();

        var deletes = core.Requests
            .Where(request => request.Method == "memory.delete")
            .Select(request => request.Parameters?.ToString() ?? string.Empty)
            .ToList();
        Assert.Equal(2, deletes.Count);
        Assert.Contains("pref", deletes[0]);
        Assert.Contains("style", deletes[1]);
    }

    [Fact]
    public void UsageTotalsCountEveryMeteredChatAndChartTheLatestTwelve()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var sessions = Enumerable.Range(1, 25)
            .Select(index => new SessionState
            {
                Id = $"sess-{index}",
                Title = $"聊天 {index}",
                CreatedAt = DateTimeOffset.Now.AddDays(-index),
                UpdatedAt = DateTimeOffset.Now.AddDays(-index),
                InputTokens = 10,
                OutputTokens = 10,
                CostUsd = 0.01
            })
            .ToArray();
        TestData.Seed(store, temp.Path, sessions);
        var core = new FakeCoreSessionService();
        core.Snapshot = new CoreCatalogSnapshot(
            true,
            string.Empty,
            "test-core",
            1,
            "fake",
            "fake-model",
            ["fake-model"],
            true,
            sessions
                .Select(session => TestData.Summary(
                    session.Id,
                    temp.Path,
                    session.Title,
                    updatedAt: session.UpdatedAt.ToString("o")))
                .ToArray());
        var vm = TestData.CreateViewModel(temp, store, core);

        vm.RefreshUsage();

        Assert.Equal(250, vm.UsageTotalInputTokens);
        Assert.Equal(250, vm.UsageTotalOutputTokens);
        Assert.Equal(20, vm.UsageRows.Count);
        Assert.Equal(12, vm.UsageBars.Count);
        Assert.True(vm.HasUsageBars);
    }

    [Fact]
    public void ImportingABackupReplacesStateAndKeepsThePreviousCopy()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var importPath = Path.Combine(temp.Path, "import.json");
        File.WriteAllText(importPath,
            "{\"Workspaces\":[{\"Name\":\"导入的项目\",\"Path\":\"D:/work\",\"Sessions\":[]}]}");

        Assert.True(vm.ImportStateBackup(importPath), vm.BackupStatusText);
        Assert.Contains("重启", vm.BackupStatusText);
        var importedState = File.ReadAllText(store.Path);
        vm.SetSidebarWidth(300);
        Assert.Equal(importedState, File.ReadAllText(store.Path));
        Assert.Equal("导入的项目", Assert.Single(store.Load().Workspaces).Name);
        Assert.Single(Directory.GetFiles(temp.Path, "state.json.before-import-*"));
    }

    [Fact]
    public void ImportingAnUnrelatedJsonFileLeavesStateUntouched()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var original = File.ReadAllText(store.Path);
        var importPath = Path.Combine(temp.Path, "other.json");
        File.WriteAllText(importPath, "{\"hello\":\"world\"}");

        Assert.False(vm.ImportStateBackup(importPath));
        Assert.StartsWith("导入失败", vm.BackupStatusText);
        Assert.Equal(original, File.ReadAllText(store.Path));
        Assert.Empty(Directory.GetFiles(temp.Path, "state.json.before-import-*"));
    }

    [Fact]
    public void ModelsPaneExposesTheConnectionSettings()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.OpenSettings(SettingsPane.Models);
        Assert.True(vm.IsSettingsModels);
        Assert.Equal("模型与连接", vm.SettingsPaneTitle);
    }

    [Fact]
    public void GeneralFileTargetAndAutomationNotificationsPersist()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.FileOpenTargetIndex = 1;
        vm.AutomationNotifications = false;

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.Equal(1, reloaded.FileOpenTargetIndex);
        Assert.False(reloaded.AutomationNotifications);
    }

    [Fact]
    public void ImportPaneIsAvailableAgain()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        vm.OpenSettings(SettingsPane.Import);

        Assert.True(vm.IsSettingsImport);
        Assert.Equal("导入", vm.SettingsPaneTitle);
    }
}

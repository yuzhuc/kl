using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;
using Kl.Gui.ViewModels;
using System.Text.Json;

namespace Kl.Gui.Tests;

/// <summary>
/// View-model integration tests for the kl-core architecture. A fake
/// <see cref="ICoreSessionService"/> plus an inline dispatcher replace the real
/// stdio transport, so every assertion is about the GUI's own state machine.
/// </summary>
public sealed class MainWindowViewModelCoreTests
{
    private static (MainWindowViewModel Vm, FakeCoreSessionService Core) Create(
        TempDirectory temp,
        StateStore store,
        FakeCoreSessionService? core = null)
    {
        var fake = core ?? new FakeCoreSessionService();
        return (TestData.CreateViewModel(temp, store, fake), fake);
    }

    private static MainWindowViewModel CreateWithSession(
        TempDirectory temp,
        StateStore store,
        FakeCoreSessionService core,
        string historyJson,
        bool isWriter = true)
    {
        TestData.Seed(store, temp.Path, TestData.StoredSession("sess-1", "旧会话"));
        core.Snapshot = new CoreCatalogSnapshot(
            true,
            string.Empty,
            "test-core",
            1,
            "fake",
            "fake-model",
            ["fake-model"],
            true,
            [TestData.Summary("sess-1", temp.Path, "旧会话")]);
        core.HistoryJson = historyJson;
        core.OpenResult = new CoreConnectResult(
            true,
            string.Empty,
            "sess-1",
            temp.Path,
            "fake",
            "fake-model",
            isWriter);
        return TestData.CreateViewModel(temp, store, core);
    }

    [Fact]
    public async Task DraftFirstSendCreatesCoreSessionAndPersistsTheCoreSessionId()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.Composer = "第一条消息";
        await vm.SendAsync();

        var open = Assert.Single(core.OpenRequests);
        Assert.Null(open.SessionId);
        Assert.Equal(temp.Path, open.Workspace);
        Assert.Equal("第一条消息", Assert.Single(core.TurnPrompts));

        var saved = Assert.Single(store.Load().Workspaces[0].Sessions);
        Assert.Equal("core-session-1", saved.Id);
        Assert.Equal("第一条消息", saved.Title);
        Assert.Equal("user", saved.Messages[0].Kind);
        Assert.Equal("第一条消息", saved.Messages[0].Text);
        Assert.Equal("core-session-1", Assert.Single(vm.Workspaces[0].Sessions).Id);
        Assert.NotNull(vm.ActiveSessionItem);
        Assert.Equal("core-session-1", vm.ActiveSessionItem.Id);
    }

    [Fact]
    public async Task SendWithAttachmentEmbedsFileContentInTheCoreTurnPrompt()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var attachmentPath = Path.Combine(temp.Path, "context.md");
        await File.WriteAllTextAsync(attachmentPath, "# 项目上下文\n附件内容 42");
        var (vm, core) = Create(temp, store);

        vm.AddAttachments([attachmentPath]);
        vm.Composer = "看附件";
        await vm.SendAsync();

        var prompt = Assert.Single(core.TurnPrompts);
        Assert.Contains("<attachment", prompt);
        Assert.Contains("context.md", prompt);
        Assert.Contains("附件内容 42", prompt);

        var user = Assert.Single(
            store.Load().Workspaces[0].Sessions[0].Messages,
            message => message.Kind == "user");
        Assert.Contains("context.md", user.Text);
        Assert.Contains("附件内容 42", user.Detail);
    }

    [Fact]
    public async Task CoreEventsMapToTranscriptItemsAndUsage()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        core.TurnHandler = (_, _) =>
        {
            core.Raise(new SessionEvent(SessionEventKind.TurnStarted));
            core.Raise(new SessionEvent(SessionEventKind.ReasoningDelta, Text: "先想"));
            core.Raise(new SessionEvent(SessionEventKind.AssistantDelta, Text: "答"));
            core.Raise(new SessionEvent(
                SessionEventKind.ToolStarted,
                Name: "shell",
                Args: """{"cmd":"ls"}""",
                CallId: "c1"));
            core.Raise(new SessionEvent(SessionEventKind.ToolOutput, Text: "out", CallId: "c1"));
            core.Raise(new SessionEvent(
                SessionEventKind.ToolCompleted,
                Name: "shell",
                Summary: "ok",
                Ok: true,
                CallId: "c1"));
            core.Raise(new SessionEvent(
                SessionEventKind.TurnCompleted,
                InputTokens: 10,
                OutputTokens: 20,
                CostUsd: 0.5,
                Status: "completed"));
            return Task.FromResult(new TurnOutcome("completed", string.Empty));
        };
        var (vm, _) = Create(temp, store, core);

        vm.Composer = "跑一下";
        await vm.SendAsync();

        Assert.Collection(
            vm.Messages,
            item =>
            {
                Assert.True(item.IsUser);
                Assert.Equal("跑一下", item.Text);
            },
            item =>
            {
                Assert.True(item.IsReasoning);
                Assert.Equal("先想", item.Text);
                Assert.False(item.IsRunning);
            },
            item =>
            {
                Assert.True(item.IsAssistant);
                Assert.Equal("答", item.Text);
            },
            item =>
            {
                Assert.True(item.IsTool);
                Assert.Equal("已运行", item.ActivityPrefix);
                Assert.False(string.IsNullOrWhiteSpace(item.ActivityCommand));
                Assert.Equal("out", item.Detail);
                Assert.Equal("ok", item.Summary);
                Assert.Equal("成功", item.StatusText);
            });

        Assert.Contains("tokens 10/20", vm.UsageText);
        Assert.Contains("$0.500000", vm.UsageText);
        Assert.False(vm.IsBusy);

        var saved = Assert.Single(store.Load().Workspaces[0].Sessions);
        Assert.Equal(
            new[] { "user", "reasoning", "assistant", "tool" },
            saved.Messages.Select(message => message.Kind));
        Assert.Equal(10, saved.InputTokens);
        Assert.Equal(20, saved.OutputTokens);
        Assert.Equal(0.5, saved.CostUsd);
    }

    [Fact]
    public async Task ApprovalRequestedOpensPanelAndRespondApprovalResolvesCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        core.Raise(new SessionEvent(
            SessionEventKind.ApprovalRequested,
            Name: "shell",
            Args: """{"cmd":"rm"}""",
            ApprovalId: "a-1",
            Action: "run",
            Resource: @"D:\ws",
            Reason: "需要执行命令"));

        Assert.True(vm.IsApprovalOpen);
        Assert.True(vm.HasApprovalArgs);
        Assert.Equal("审批：shell", vm.ApprovalTitle);
        Assert.Equal("run", vm.ApprovalAction);
        Assert.Equal(@"D:\ws", vm.ApprovalResource);
        Assert.Equal("需要执行命令", vm.ApprovalReason);

        await vm.RespondApprovalAsync("allow_session");

        var decision = Assert.Single(core.ApprovalDecisions);
        Assert.Equal("a-1", decision.ApprovalId);
        Assert.Equal("allow_session", decision.Decision);
        Assert.False(vm.IsApprovalOpen);
    }

    [Fact]
    public async Task ApprovalResolvedEventClosesPanelAndStaleResponsesDoNotReachCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        core.Raise(new SessionEvent(SessionEventKind.ApprovalRequested, ApprovalId: "a-2", Name: "shell"));
        Assert.True(vm.IsApprovalOpen);
        core.Raise(new SessionEvent(SessionEventKind.ApprovalResolved, ApprovalId: "a-2", Decision: "deny"));
        Assert.False(vm.IsApprovalOpen);

        await vm.RespondApprovalAsync("allow");
        Assert.Empty(core.ApprovalDecisions);
    }

    [Fact]
    public async Task ExpiredApprovalShowsError()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);
        core.ApprovalResolver = (_, _) => false;

        core.Raise(new SessionEvent(SessionEventKind.ApprovalRequested, ApprovalId: "a-3", Name: "shell"));
        await vm.RespondApprovalAsync("allow");

        Assert.Equal("审批请求已过期。", vm.ErrorMessage);
    }

    [Fact]
    public async Task TurnFailedAddsErrorAndLeavesBusyState()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        core.TurnHandler = (_, _) =>
        {
            core.Raise(new SessionEvent(SessionEventKind.TurnStarted));
            core.Raise(new SessionEvent(SessionEventKind.TurnFailed, Text: "模型超时"));
            return Task.FromResult(new TurnOutcome("failed", string.Empty));
        };
        var (vm, _) = Create(temp, store, core);

        vm.Composer = "会失败";
        await vm.SendAsync();

        Assert.Contains(vm.Messages, item => item.IsError && item.Text == "模型超时");
        Assert.Equal("模型超时", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ErrorEventMarksCoreUnavailable()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        core.TurnHandler = (_, _) =>
        {
            core.Raise(new SessionEvent(SessionEventKind.Error, Text: "core 断开"));
            return Task.FromResult(new TurnOutcome("completed", string.Empty));
        };
        var (vm, _) = Create(temp, store, core);

        vm.Composer = "跑";
        await vm.SendAsync();

        Assert.Contains(vm.Messages, item => item.IsError && item.Text == "core 断开");
        Assert.False(vm.IsCoreReady);
        Assert.Contains("core 断开", vm.CoreBannerText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CancelRequestsCoreCancellationAndRecordsStoppedTurn()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new FakeCoreSessionService();
        core.TurnHandler = async (_, cancellationToken) =>
        {
            core.Raise(new SessionEvent(SessionEventKind.TurnStarted));
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TurnOutcome("cancelled", string.Empty);
        };
        var (vm, _) = Create(temp, store, core);

        vm.Composer = "取消我";
        var sendTask = vm.SendAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.CancelAsync();
        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, core.CancelCalls);
        Assert.False(vm.IsBusy);
        Assert.Contains(vm.Messages, item => item.IsError && item.Text.Contains("已停止"));
    }

    [Fact]
    public async Task OpenSessionLoadsHistoryAndAppliesPermissionMode()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(
            temp,
            store,
            core,
            """
            {"messages":[
                {"role":"user","text":"历史提问"},
                {"role":"assistant","text":"从核心历史","reasoning":"旧思考","seq":2},
                {"role":"tool","text":"",
                 "tool":{"name":"shell","ok":true,"summary":"done","output":"out","diff":"+a"}}
            ]}
            """);

        var item = Assert.Single(vm.Workspaces[0].Sessions);
        await vm.OpenSessionAsync(item);

        Assert.Equal("sess-1", Assert.Single(core.OpenRequests).SessionId);
        Assert.Equal(200, Assert.Single(core.HistoryLimits));
        Assert.Contains(core.Requests, request => request.Method == Protocol.PermissionsGet);
        Assert.DoesNotContain(core.Requests, request => request.Method == Protocol.PermissionsSet);

        Assert.Collection(
            vm.Messages,
            message => Assert.Equal("历史提问", message.Text),
            message =>
            {
                Assert.True(message.IsReasoning);
                Assert.Equal("旧思考", message.Text);
            },
            message => Assert.Equal("从核心历史", message.Text),
            message =>
            {
                Assert.True(message.IsTool);
                Assert.Equal("已运行", message.ActivityPrefix);
                Assert.Equal("done", message.ActivityCommand);
                Assert.Contains("out", message.Detail);
                Assert.Contains("+a", message.Detail);
                Assert.Equal("done", message.Summary);
                Assert.Equal("成功", message.StatusText);
            });
        Assert.Equal("sess-1", vm.ActiveSessionItem!.Id);
        Assert.False(vm.IsCoreBusy);
    }

    [Fact]
    public async Task ObserverSessionMarksReadOnlyAndBlocksSending()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""", isWriter: false);

        var item = Assert.Single(vm.Workspaces[0].Sessions);
        await vm.OpenSessionAsync(item);

        Assert.True(vm.IsCoreReadOnly);
        Assert.True(vm.ShowObserverNotice);
        Assert.Contains("只读", vm.CoreBannerText);
        vm.Composer = "观察者不能发送";
        Assert.False(vm.CanSend);
    }

    [Fact]
    public async Task TakeOverWriteRestoresSendingForAnObserver()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService { WriterAllowed = true };
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""", isWriter: false);
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));
        Assert.True(vm.IsCoreReadOnly);
        Assert.Equal("接管写入", vm.CoreBannerActionText);

        await vm.TakeOverWriteAsync();

        Assert.Equal(1, core.WriterCalls);
        Assert.False(vm.IsCoreReadOnly);
        Assert.Empty(vm.CoreBannerText);
        vm.Composer = "观察者接管后可以发送";
        Assert.True(vm.CanSend);
    }

    [Fact]
    public async Task TakeOverWriteKeepsObserverNoticeWhenTheLeaseIsHeld()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""", isWriter: false);
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        await vm.TakeOverWriteAsync();

        Assert.True(vm.IsCoreReadOnly);
        Assert.Contains("仍在写入", vm.CoreBannerText);
    }

    [Fact]
    public async Task ConnectionLossAutoReconnectsAndReloadsHistoryWhenReopened()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));
        var historyCalls = core.HistoryLimits.Count;
        core.ReconnectResult = new CoreReconnectResult(true, false, string.Empty);

        core.Raise(new SessionEvent(
            SessionEventKind.Error,
            Text: "core connection closed",
            ConnectionLost: true));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (core.ReconnectCalls == 0 || !vm.IsCoreReady)
        {
            Assert.True(DateTime.UtcNow < deadline, "auto reconnect did not finish in time");
            await Task.Delay(50);
        }

        Assert.Equal(1, core.ReconnectCalls);
        Assert.Empty(vm.CoreBannerText);
        Assert.True(core.HistoryLimits.Count > historyCalls);
        Assert.False(vm.IsCoreRetrying);
    }

    [Fact]
    public async Task RetryCoreAsyncRestoresAnOpenSession()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        await vm.RetryCoreAsync();

        Assert.Equal(1, core.ReconnectCalls);
        Assert.True(vm.IsCoreReady);
        Assert.False(vm.IsCoreRetrying);
    }

    [Fact]
    public async Task ForkActiveSessionCreatesABranchRow()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));
        core.OpenResult = null;

        await vm.ForkActiveSessionAsync();

        Assert.Equal(1, core.ForkCalls);
        var sessions = vm.Workspaces[0].Sessions.ToList();
        Assert.Equal(2, sessions.Count);
        Assert.Equal("forked-session-1", vm.ActiveSessionItem!.Id);
        Assert.Contains("分支", Assert.Single(vm.Workspaces[0].State.Sessions, item => item.Id == "forked-session-1").Title);
    }

    [Fact]
    public async Task CompactActiveSessionCallsCoreAndReloadsHistory()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));
        var historyCalls = core.HistoryLimits.Count;

        await vm.CompactActiveSessionAsync();

        Assert.Equal(1, core.CompactCalls);
        Assert.Contains("压缩", vm.CoreStatusText);
        Assert.True(core.HistoryLimits.Count > historyCalls);
        Assert.Contains(vm.Messages, message => message.Header.Contains("上下文已压缩"));
    }

    [Fact]
    public async Task RunDoctorListsChecksAndSummary()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var (vm, _) = Create(temp, store, core);

        await vm.RunDoctorAsync();

        Assert.Equal(1, core.DoctorCalls);
        Assert.Contains("core", Assert.Single(vm.DoctorChecks));
        Assert.Contains("诊断通过", vm.DoctorSummary);
        Assert.True(vm.CanRunDoctor);
    }

    [Fact]
    public void SameNamedProjectsShowTheirPaths()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path, TestData.StoredSession("sess-1", "会话"));
        var state = store.Load();
        state.Workspaces.Add(new WorkspaceState { Name = "kl", Path = "C:/one/kl" });
        state.Workspaces.Add(new WorkspaceState { Name = "kl", Path = "C:/two/kl" });
        state.Workspaces.Add(new WorkspaceState { Name = "nook", Path = "C:/three/nook" });
        store.Save(state);

        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        var duplicates = vm.Workspaces.Where(workspace => workspace.Name == "kl").ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.All(duplicates, workspace => Assert.True(workspace.HasPathLabel));
        Assert.Equal("C:/one/kl", duplicates[0].PathLabel);
        Assert.Equal("C:/two/kl", duplicates[1].PathLabel);
        Assert.False(vm.Workspaces.Single(workspace => workspace.Name == "nook").HasPathLabel);
    }

    [Fact]
    public void LibrarySavesPersistsInsertsAndDeletes()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.False(vm.HasLibraryItems);

        vm.LibraryDraftTitle = "提交规范";
        vm.LibraryDraftContent = "先跑测试再提交";
        vm.SaveLibraryItem();

        var item = Assert.Single(vm.LibraryItems);
        Assert.Equal("提交规范", item.Title);
        Assert.True(vm.HasLibraryItems);
        Assert.Empty(vm.LibraryDraftContent);

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.Equal("提交规范", Assert.Single(reloaded.LibraryItems).Title);

        vm.InsertLibraryItem(item);
        Assert.Contains("先跑测试再提交", vm.Composer);
        Assert.True(vm.IsChatPage);

        vm.DeleteLibraryItem(item);
        Assert.Empty(vm.LibraryItems);
        Assert.Empty(store.Load().LibraryItems);
    }

    [Fact]
    public async Task SaveModelSendsTheChosenProviderAndModelToCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                "fake",
                "fake-model",
                ["fake-model", "other-model"],
                true,
                Array.Empty<CoreSessionSummary>())
            {
                ModelOptions =
                [
                    new CoreModelOption("fake", "fake-model", true, true),
                    new CoreModelOption("other", "other-model", true, false)
                ]
            }
        };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        Assert.Equal(2, vm.SettingsModelChoices.Count);

        Assert.Equal(2, vm.ProviderCards.Count);
        var fake = Assert.Single(vm.ProviderCards, row => row.Id == "fake");
        Assert.True(fake.IsDefault);
        Assert.True(fake.Enabled);
        Assert.Equal("Scripted / Fake", fake.Name);
        Assert.True(fake.HasNote);
        Assert.False(fake.HasWebsite);
        var other = Assert.Single(vm.ProviderCards, row => row.Id == "other");
        Assert.False(other.IsDefault);
        Assert.True(other.Enabled);
        Assert.Equal("other", other.Name);
        Assert.True(vm.HasProviderCards);
        Assert.False(vm.ShowNoProviderCards);

        vm.SettingsSelectedModel = "other / other-model";
        await vm.SaveModelAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ConfigSetDefaultModel);
        Assert.Equal("other", FakeCoreSessionService.ReadParameter(request.Parameters, "provider"));
        Assert.Equal("other-model", FakeCoreSessionService.ReadParameter(request.Parameters, "model"));
        Assert.True(vm.IsSettingsStatusSuccess);
    }

    [Fact]
    public async Task ComposerModelPickerSwitchesTheTurnModel()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                "fake",
                "fake-model",
                ["fake-model", "other-model"],
                true,
                Array.Empty<CoreSessionSummary>())
            {
                ModelOptions =
                [
                    new CoreModelOption("fake", "fake-model", true, true),
                    new CoreModelOption("other", "other-model", true, false)
                ]
            }
        };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();

        Assert.Equal(2, vm.ComposerModels.Count);
        var fakeRow = Assert.Single(vm.ComposerModels, row => row.Choice == "fake / fake-model");
        var otherRow = Assert.Single(vm.ComposerModels, row => row.Choice == "other / other-model");
        Assert.True(fakeRow.IsSelected);
        Assert.False(otherRow.IsSelected);

        vm.SelectComposerModel(otherRow);

        Assert.False(fakeRow.IsSelected);
        Assert.True(otherRow.IsSelected);
        Assert.Equal(("other", "other-model"), core.TurnModel);
        Assert.Equal("Other Model", vm.ProviderModelLabel);
    }

    [Fact]
    public async Task AddProviderWritesUpsertAndKeyToCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        core.RequestResponses[Protocol.ProviderUpsert] =
            """{"provider":"deepseek","path":"providers/deepseek.toml","models":0,"enabled":true}""";
        core.RequestResponses[Protocol.ProviderSetApiKey] =
            """{"provider":"deepseek","key_configured":true}""";
        var (vm, _) = Create(temp, store, core);

        vm.OpenProviderForm();
        Assert.True(vm.IsProviderFormOpen);
        Assert.Empty(vm.ProviderDraftName);
        vm.ProviderDraftName = "DeepSeek";
        vm.ProviderDraftNote = "团队共享额度";
        vm.ProviderDraftWebsite = "https://www.deepseek.com";
        vm.ProviderDraftChatUrl = "https://api.deepseek.com";
        vm.ProviderChatSuffixIndex = 1;
        vm.ProviderDraftApiKey = "sk-test";
        await vm.SaveProviderAsync();

        Assert.False(vm.IsProviderFormOpen);
        Assert.False(vm.HasProviderFormStatus);
        var upsert = Assert.Single(core.Requests, request => request.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(upsert.Parameters);
        Assert.Equal("deepseek", json.GetProperty("id").GetString());
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, json.GetProperty("models").GetArrayLength());
        var protocols = json.GetProperty("protocols");
        Assert.Equal(
            "https://api.deepseek.com/chat/completions",
            protocols.GetProperty("openai-chat").GetString());
        Assert.Equal(
            "https://api.deepseek.com/responses",
            protocols.GetProperty("openai-responses").GetString());
        Assert.Equal(
            "https://api.deepseek.com/messages",
            protocols.GetProperty("anthropic").GetString());
        var key = Assert.Single(core.Requests, request => request.Method == Protocol.ProviderSetApiKey);
        Assert.Equal("sk-test", FakeCoreSessionService.ReadParameter(key.Parameters, "key"));
        var profile = store.Load().ProviderProfiles["deepseek"];
        Assert.Equal("DeepSeek", profile.DisplayName);
        Assert.Equal("团队共享额度", profile.Note);
        Assert.Equal("https://www.deepseek.com", profile.Website);
        Assert.Contains("已写入 deepseek", vm.ProviderStatusText);
    }

    [Fact]
    public async Task AddProviderRejectsInvalidBaseUrl()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.OpenProviderForm();
        vm.ProviderDraftName = "DeepSeek";
        vm.ProviderDraftChatUrl = "api.deepseek.com";
        await vm.SaveProviderAsync();

        Assert.True(vm.IsProviderFormOpen);
        Assert.Contains("请求地址", vm.ProviderFormStatus);
        Assert.Empty(core.Requests);
    }

    [Fact]
    public async Task AddProviderDefaultsToShortSuffix()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.OpenProviderForm();
        Assert.Equal(1, vm.ProviderChatSuffixIndex);
        vm.ProviderDraftName = "default-suffix";
        vm.ProviderDraftChatUrl = "https://api.deepseek.com";
        await vm.SaveProviderAsync();

        var upsert = Assert.Single(core.Requests, request => request.Method == Protocol.ProviderUpsert);
        var protocols = JsonSerializer.SerializeToElement(upsert.Parameters).GetProperty("protocols");
        Assert.Equal(
            "https://api.deepseek.com/chat/completions",
            protocols.GetProperty("openai-chat").GetString());
        Assert.Equal(
            "https://api.deepseek.com/messages",
            protocols.GetProperty("anthropic").GetString());
        Assert.Equal(
            "https://api.deepseek.com/responses",
            protocols.GetProperty("openai-responses").GetString());
    }

    [Fact]
    public async Task AddProviderNullSuffixKeepsTheEnteredUrl()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.OpenProviderForm();
        vm.ProviderDraftName = "null-suffix";
        vm.ProviderDraftChatUrl = "https://api.deepseek.com/custom";
        vm.ProviderChatSuffixIndex = 0;
        await vm.SaveProviderAsync();

        var upsert = Assert.Single(core.Requests, request => request.Method == Protocol.ProviderUpsert);
        var protocols = JsonSerializer.SerializeToElement(upsert.Parameters).GetProperty("protocols");
        Assert.Equal(
            "https://api.deepseek.com/custom",
            protocols.GetProperty("openai-chat").GetString());
        Assert.Equal(
            "https://api.deepseek.com/custom",
            protocols.GetProperty("anthropic").GetString());
        Assert.Equal(
            "https://api.deepseek.com/custom",
            protocols.GetProperty("openai-responses").GetString());
    }

    [Theory]
    [InlineData(
        1,
        "https://api.deepseek.com/chat/completions",
        "https://api.deepseek.com/messages",
        "https://api.deepseek.com/responses")]
    [InlineData(
        2,
        "https://api.deepseek.com/v1/chat/completions",
        "https://api.deepseek.com/v1/messages",
        "https://api.deepseek.com/v1/responses")]
    public async Task AddProviderBuildsAllProtocolUrlsFromSuffix(
        int suffixIndex,
        string chatUrl,
        string anthropicUrl,
        string responsesUrl)
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.OpenProviderForm();
        vm.ProviderDraftName = "suffix";
        vm.ProviderDraftChatUrl = "https://api.deepseek.com";
        vm.ProviderChatSuffixIndex = suffixIndex;
        await vm.SaveProviderAsync();

        var upsert = Assert.Single(core.Requests, request => request.Method == Protocol.ProviderUpsert);
        var protocols = JsonSerializer.SerializeToElement(upsert.Parameters).GetProperty("protocols");
        Assert.Equal(chatUrl, protocols.GetProperty("openai-chat").GetString());
        Assert.Equal(anthropicUrl, protocols.GetProperty("anthropic").GetString());
        Assert.Equal(responsesUrl, protocols.GetProperty("openai-responses").GetString());
    }

    [Fact]
    public async Task ProviderProfileEditPersistsNameNoteAndWebsite()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var snapshot = Snapshot("deepseek", "deepseek-chat");
        var core = new FakeCoreSessionService { Snapshot = snapshot };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();

        var row = Assert.Single(vm.ProviderCards);
        Assert.Equal("DeepSeek", row.Name);
        vm.OpenProviderDetail(row);
        vm.ProviderEditName = "DeepSeek 团队";
        vm.ProviderEditNote = "共享额度";
        vm.ProviderEditWebsite = "https://www.deepseek.com";
        Assert.True(vm.SaveProviderProfile());

        Assert.Equal("DeepSeek 团队", row.Name);
        Assert.Equal("共享额度", row.Note);
        Assert.Equal("https://www.deepseek.com", row.Website);
        var saved = store.Load().ProviderProfiles["deepseek"];
        Assert.Equal("DeepSeek 团队", saved.DisplayName);
        Assert.Equal("共享额度", saved.Note);
        Assert.Equal("https://www.deepseek.com", saved.Website);

        var reloaded = TestData.CreateViewModel(
            temp,
            store,
            new FakeCoreSessionService { Snapshot = snapshot });
        await reloaded.TestConnectionAsync();
        var reloadedRow = Assert.Single(reloaded.ProviderCards);
        Assert.Equal("DeepSeek 团队", reloadedRow.Name);
        Assert.Equal("共享额度", reloadedRow.Note);
        Assert.Equal("https://www.deepseek.com", reloadedRow.Website);
    }

    [Fact]
    public async Task ProviderProfileRejectsMalformedWebsite()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, _) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();

        var row = Assert.Single(vm.ProviderCards);
        vm.OpenProviderDetail(row);
        vm.ProviderEditWebsite = "deepseek.com";
        Assert.False(vm.SaveProviderProfile());
        Assert.True(vm.IsProviderDetailOpen);
        Assert.Contains("官网链接", vm.ProviderEditStatus);
    }

    [Fact]
    public async Task ProviderToggleWritesEnabledStateToCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();
        var row = Assert.Single(vm.ProviderCards);

        row.Enabled = false;
        await vm.SetProviderEnabledAsync(row, false);

        Assert.False(row.Enabled);
        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderSetEnabled);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        Assert.Equal("deepseek", json.GetProperty("id").GetString());
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Contains("已停用", vm.ProviderStatusText);
    }

    private static CoreCatalogSnapshot Snapshot(string provider, string model) =>
        new(
            true,
            string.Empty,
            "test-core",
            1,
            provider,
            model,
            [model],
            true,
            Array.Empty<CoreSessionSummary>())
        {
            ModelOptions =
            [
                new CoreModelOption(provider, model, true, true)
                {
                    Protocol = "openai-chat",
                    Url = "https://api.example.com/chat/completions",
                    ContextWindow = 128000,
                    CompactThreshold = 0.8,
                    MaxOutputTokens = 8192
                }
            ]
        };

    [Fact]
    public async Task FetchingProviderModelsFillsTheAddModelForm()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = Snapshot("deepseek", "deepseek-chat")
        };
        core.RequestResponses[Protocol.ProviderModels] =
            """{"provider":"deepseek","protocol":"openai-responses","url":"https://api.deepseek.com/v1/responses","models":["deepseek-reasoner","deepseek-chat","deepseek-reasoner"]}""";
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        vm.OpenAddProviderModel();

        await vm.FetchProviderModelsAsync();

        Assert.Equal(
            new[] { "deepseek-reasoner", "deepseek-chat" },
            vm.ProviderFetchedModels);
        Assert.Equal("deepseek-reasoner", vm.SelectedProviderFetchedModel);
        Assert.Equal("deepseek-reasoner", vm.ProviderModelDraftId);
        Assert.Equal(2, vm.ProviderModelProtocolIndex);
        Assert.Equal("https://api.deepseek.com/v1/responses", vm.ProviderModelDraftUrl);
        Assert.Contains("已获取 2 个模型", vm.ProviderModelFetchStatus);
    }

    [Fact]
    public async Task ProviderDetailAddsAModelAndWritesUpsert()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();

        var provider = Assert.Single(vm.ProviderCards);
        vm.OpenProviderDetail(provider);
        Assert.True(vm.IsProviderDetailOpen);
        Assert.False(vm.IsProviderListOpen);
        Assert.Equal("DeepSeek", vm.ProviderDetailName);
        var existing = Assert.Single(vm.ProviderModels);
        Assert.Equal("deepseek-chat", existing.Id);
        Assert.Equal("deepseek-chat", existing.DisplayName);
        Assert.Equal("openai-chat", existing.Protocol);
        Assert.Equal("https://api.example.com/chat/completions", existing.Url);

        vm.OpenAddProviderModel();
        vm.ProviderModelDraftName = "DeepSeek Reasoner";
        vm.ProviderModelDraftId = "deepseek-reasoner";
        vm.ProviderModelDraftUrl = "https://api.deepseek.com/chat/completions";
        vm.ProviderModelDraftContextWindow = "64000";
        vm.ProviderModelDraftMaxOutput = "8192";
        await vm.SaveProviderModelAsync();

        Assert.False(vm.IsProviderModelFormOpen);
        Assert.Equal(2, vm.ProviderModels.Count);
        var added = Assert.Single(vm.ProviderModels, model => model.Id == "deepseek-reasoner");
        Assert.Equal("DeepSeek Reasoner", added.DisplayName);
        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        var models = json.GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.Contains(
            models.EnumerateArray(),
            model => model.GetProperty("id").GetString() == "deepseek-reasoner"
                && model.GetProperty("name").GetString() == "DeepSeek Reasoner");
        Assert.False(vm.HasProviderDetailStatus);
        Assert.Equal(2, provider.Models.Count);
    }

    [Fact]
    public async Task ProviderDetailLoadsConnectionFieldsAndSavesProtocols()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();

        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        Assert.Equal("https://api.example.com/chat/completions", vm.ProviderDraftChatUrl);
        Assert.Equal(0, vm.ProviderChatSuffixIndex);
        Assert.True(vm.ProviderChatProtocolEnabled);
        Assert.False(vm.ProviderAnthropicProtocolEnabled);

        vm.ProviderDraftChatUrl = "https://api.deepseek.com/v1/chat/completions";
        vm.ProviderDraftAnthropicUrl = "https://api.deepseek.com/messages";
        vm.ProviderAnthropicProtocolEnabled = true;
        await vm.SaveProviderConnectionAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        var protocols = json.GetProperty("protocols");
        Assert.Equal(
            "https://api.deepseek.com/v1/chat/completions",
            protocols.GetProperty("openai-chat").GetString());
        Assert.Equal(
            "https://api.deepseek.com/messages",
            protocols.GetProperty("anthropic").GetString());
        Assert.False(protocols.TryGetProperty("openai-responses", out _));
        var model = Assert.Single(json.GetProperty("models").EnumerateArray());
        Assert.Equal("deepseek-chat", model.GetProperty("id").GetString());
        Assert.Equal(
            "https://api.example.com/chat/completions",
            model.GetProperty("url").GetString());
        Assert.Contains("连接配置已写入", vm.ProviderConnectionStatus);
    }

    [Fact]
    public async Task ProviderDetailShowsConfiguredUrlsWithoutModels()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                false,
                Array.Empty<CoreSessionSummary>())
            {
                ProviderProtocols =
                [
                    new CoreProviderProtocols(
                        "deepseek",
                        "https://api.deepseek.com/chat/completions",
                        "https://api.deepseek.com/messages",
                        string.Empty)
                ]
            }
        };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();

        var provider = Assert.Single(vm.ProviderCards);
        Assert.Equal("deepseek", provider.Id);
        Assert.Empty(provider.Models);
        vm.OpenProviderDetail(provider);

        Assert.Equal("https://api.deepseek.com/chat/completions", vm.ProviderDraftChatUrl);
        Assert.Equal("https://api.deepseek.com/messages", vm.ProviderDraftAnthropicUrl);
        Assert.Empty(vm.ProviderDraftResponsesUrl);
        Assert.True(vm.ProviderChatProtocolEnabled);
        Assert.True(vm.ProviderAnthropicProtocolEnabled);
        Assert.False(vm.ProviderResponsesProtocolEnabled);
    }

    [Fact]
    public async Task ModelUrlFollowsProtocolAndSupportsCustom()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                false,
                Array.Empty<CoreSessionSummary>())
            {
                ProviderProtocols =
                [
                    new CoreProviderProtocols(
                        "deepseek",
                        "https://api.deepseek.com/chat/completions",
                        "https://api.deepseek.com/messages",
                        string.Empty)
                ]
            }
        };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        vm.OpenAddProviderModel();

        Assert.Equal("https://api.deepseek.com/chat/completions", vm.ProviderModelDraftUrl);

        vm.ProviderModelProtocolIndex = 1;
        Assert.Equal("https://api.deepseek.com/messages", vm.ProviderModelDraftUrl);

        vm.ProviderModelProtocolIndex = 3;
        Assert.Equal("https://api.deepseek.com/messages", vm.ProviderModelDraftUrl);

        vm.ProviderModelDraftId = "custom-model";
        vm.ProviderModelDraftUrl = "https://custom.example/v1/messages";
        await vm.SaveProviderModelAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        var model = Assert.Single(json.GetProperty("models").EnumerateArray());
        Assert.Equal("anthropic", model.GetProperty("protocol").GetString());
        Assert.Equal("https://custom.example/v1/messages", model.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ProviderModelToggleSavesEnabledState()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));

        var model = Assert.Single(vm.ProviderModels);
        await vm.SetProviderModelEnabledAsync(model, false);

        Assert.False(model.Enabled);
        Assert.True(Assert.Single(vm.ProviderCards).Enabled);
        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        var saved = Assert.Single(json.GetProperty("models").EnumerateArray());
        Assert.False(saved.GetProperty("enabled").GetBoolean());
        Assert.Equal("default", saved.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ProviderModelReasoningLevelIsSaved()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        vm.OpenAddProviderModel();
        Assert.Equal(
            new[] { "low", "medium", "high", "max" },
            vm.ProviderModelDefaultReasoningChoices);
        vm.ProviderModelDraftId = "deepseek-reasoner";
        vm.SelectedProviderModelDefaultReasoning = "high";
        await vm.SaveProviderModelAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        var saved = Assert.Single(
            json.GetProperty("models").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "deepseek-reasoner");
        Assert.Equal("high", saved.GetProperty("reasoning_effort").GetString());
        Assert.Equal(
            new[] { "low", "medium", "high", "max" },
            saved.GetProperty("reasoning_efforts")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.True(saved.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ProviderModelRejectsMalformedUrl()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        vm.OpenAddProviderModel();
        vm.ProviderModelDraftId = "bad";
        vm.ProviderModelDraftUrl = "not-a-url";
        await vm.SaveProviderModelAsync();

        Assert.True(vm.IsProviderModelFormOpen);
        Assert.Contains("URL", vm.ProviderModelFormStatus);
        Assert.DoesNotContain(core.Requests, item => item.Method == Protocol.ProviderUpsert);
    }

    [Fact]
    public async Task ProviderModelCanRemoveItsLastModel()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));

        await vm.DeleteProviderModelAsync(Assert.Single(vm.ProviderModels));

        Assert.Empty(vm.ProviderModels);
        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        Assert.Equal(0, json.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task ProviderDeleteRequiresConfirmationAndWritesCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));

        vm.OpenProviderDeleteConfirm();

        Assert.True(vm.IsProviderDetailOpen);
        Assert.True(vm.IsProviderDeleteConfirmOpen);
        Assert.DoesNotContain(core.Requests, item => item.Method == Protocol.ProviderDelete);

        vm.CancelProviderDeleteConfirm();

        Assert.False(vm.IsProviderDeleteConfirmOpen);
        Assert.DoesNotContain(core.Requests, item => item.Method == Protocol.ProviderDelete);

        vm.OpenProviderDeleteConfirm();
        await vm.ConfirmDeleteProviderAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderDelete);
        Assert.Equal("deepseek", FakeCoreSessionService.ReadParameter(request.Parameters, "id"));
        Assert.False(vm.IsProviderDetailOpen);
        Assert.False(vm.IsProviderDeleteConfirmOpen);
        Assert.Empty(vm.ProviderStatusText);
    }

    [Fact]
    public async Task RefreshProvidersMergesDisabledProvidersFromConfig()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                false,
                Array.Empty<CoreSessionSummary>()),
            ConfigJson =
                """{"config":{"providers":{"deepseek":{"enabled":false,"path":"providers/deepseek.toml"}}},"providers":{"providers":[]}}"""
        };
        var (vm, _) = Create(temp, store, core);

        await vm.RefreshProvidersAsync(force: true);

        var provider = Assert.Single(vm.ProviderCards);
        Assert.Equal("deepseek", provider.Id);
        Assert.Equal("DeepSeek", provider.Name);
        Assert.False(provider.Enabled);
        Assert.True(provider.HasNote);
    }

    [Fact]
    public async Task ProviderRefreshDoesNotShowStatus()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, _) = Create(temp, store);

        await vm.RefreshProvidersAsync(force: true);
        Assert.Empty(vm.ProviderStatusText);

        await vm.RefreshProvidersAsync(force: true, announce: false);
        Assert.Empty(vm.ProviderStatusText);
    }

    [Fact]
    public async Task TestConnectionWarmsTheProviderCache()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        await vm.TestConnectionAsync();
        await vm.RefreshProvidersAsync();

        Assert.Equal(0, core.ProviderCatalogCalls);
    }

    [Fact]
    public void ProviderPaneOpensWithEmptyStateAndNoLoadingMessage()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = new CoreCatalogSnapshot(
                true,
                string.Empty,
                "test-core",
                1,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                false,
                Array.Empty<CoreSessionSummary>())
        };
        var (vm, _) = Create(temp, store, core);

        vm.SelectSettingsPane(SettingsPane.Providers);

        Assert.True(vm.ShowNoProviderCards);
        Assert.DoesNotContain("正在刷新提供商", vm.ProviderStatusText);
    }

    [Fact]
    public async Task ProviderRefreshIsCachedUntilForced()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        await vm.RefreshProvidersAsync(force: true);
        await vm.RefreshProvidersAsync();
        Assert.Equal(1, core.ProviderCatalogCalls);

        await vm.RefreshProvidersAsync(force: true);
        Assert.Equal(2, core.ProviderCatalogCalls);
    }

    [Fact]
    public void CachedProvidersRenderBeforeCoreAnswers()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var cachePath = Path.Combine(temp.Path, "providers-cache.json");
        new ProviderCacheStore(cachePath).Save(
        [
            new ProviderCacheEntry
            {
                Id = "cached",
                Name = "Cached",
                Note = "缓存备注",
                Website = "https://cached.example",
                Enabled = true,
                KeyConfigured = true,
                Models =
                [
                    new CoreModelOption("cached", "cached-model", true, true)
                    {
                        Protocol = "openai-chat",
                        Url = "https://cached.example/chat/completions",
                        ContextWindow = 64000,
                        CompactThreshold = 0.75,
                        MaxOutputTokens = 4096
                    }
                ]
            }
        ]);
        var core = new FakeCoreSessionService
        {
            Snapshot = CoreCatalogSnapshot.Failed("offline")
        };

        var (vm, _) = Create(temp, store, core);

        var provider = Assert.Single(vm.ProviderCards);
        Assert.Equal("cached", provider.Id);
        Assert.Equal("Cached", provider.Name);
        Assert.Equal("缓存备注", provider.Note);
        Assert.Equal("https://cached.example", provider.Website);
        Assert.True(provider.Enabled);
        Assert.True(provider.KeyConfigured);
        Assert.Single(provider.Models);
        Assert.Equal(0, core.ProviderCatalogCalls);
        Assert.Empty(vm.ProviderStatusText);

        vm.OpenProviderDetail(provider);
        var model = Assert.Single(vm.ProviderModels);
        Assert.Equal("cached-model", model.Id);
        Assert.Equal("openai-chat", model.Protocol);
        Assert.Equal(64000, model.ContextWindow);
    }

    [Fact]
    public async Task ProviderRefreshWritesTheCompleteCache()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = Snapshot("deepseek", "deepseek-chat"),
            ConfigJson =
                """{"config":{"providers":{"ds":{"enabled":false}}}}"""
        };
        var (vm, _) = Create(temp, store, core);

        await vm.RefreshProvidersAsync(force: true);

        var cache = new ProviderCacheStore(
            Path.Combine(temp.Path, "providers-cache.json")).Load();
        Assert.NotNull(cache);
        Assert.Equal(2, cache.Providers.Count);
        var deepseek = Assert.Single(cache.Providers, entry => entry.Id == "deepseek");
        Assert.Equal("openai-chat", Assert.Single(deepseek.Models).Protocol);
        var disabled = Assert.Single(cache.Providers, entry => entry.Id == "ds");
        Assert.False(disabled.Enabled);
        Assert.Empty(disabled.Models);
    }

    [Fact]
    public async Task ProviderListFailureKeepsCachedCardsAndFile()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var cachePath = Path.Combine(temp.Path, "providers-cache.json");
        var cache = new ProviderCacheStore(cachePath);
        cache.Save([new ProviderCacheEntry { Id = "cached", Name = "Cached", Enabled = true }]);
        var core = new FakeCoreSessionService
        {
            Snapshot = CoreCatalogSnapshot.Failed("offline"),
            ProviderListReadResult = false
        };
        var (vm, _) = Create(temp, store, core);
        core.Snapshot = Snapshot("new", "new-model");

        await vm.RefreshProvidersAsync(force: true);

        Assert.Equal("cached", Assert.Single(vm.ProviderCards).Id);
        Assert.Contains("无法刷新提供商", vm.ProviderStatusText);
        Assert.Equal("cached", Assert.Single(Assert.IsType<ProviderCacheFile>(cache.Load()).Providers).Id);
    }

    [Fact]
    public async Task ConfigReadFailureUpdatesCardsButKeepsTheCache()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var cachePath = Path.Combine(temp.Path, "providers-cache.json");
        var cache = new ProviderCacheStore(cachePath);
        cache.Save([new ProviderCacheEntry { Id = "cached", Name = "Cached", Enabled = true }]);
        var core = new FakeCoreSessionService
        {
            Snapshot = CoreCatalogSnapshot.Failed("offline"),
            ConfigReadResult = false
        };
        var (vm, _) = Create(temp, store, core);
        core.Snapshot = Snapshot("new", "new-model");

        await vm.RefreshProvidersAsync(force: true);

        Assert.Equal("new", Assert.Single(vm.ProviderCards).Id);
        Assert.Equal("cached", Assert.Single(Assert.IsType<ProviderCacheFile>(cache.Load()).Providers).Id);
    }

    [Fact]
    public void CorruptProviderCacheLeavesThePageEmpty()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        File.WriteAllText(
            Path.Combine(temp.Path, "providers-cache.json"),
            "not json");
        var core = new FakeCoreSessionService
        {
            Snapshot = CoreCatalogSnapshot.Failed("offline")
        };

        var (vm, _) = Create(temp, store, core);

        Assert.Empty(vm.ProviderCards);
    }

    [Fact]
    public async Task ReasoningEffortIsWrittenThroughCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var (vm, _) = Create(temp, store, core);

        await vm.SetReasoningEffortAsync("high");

        Assert.Equal(["high"], core.ReasoningSets);
        Assert.Equal("high", vm.ReasoningEffort);
        Assert.Equal("high", vm.ReasoningEffortLabel);
        Assert.True(vm.IsReasoningHigh);
        Assert.False(vm.IsReasoningDefault);
    }

    [Fact]
    public async Task ComposerEffortSelectionPersistsPerModel()
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
            "deepseek-chat",
            ["deepseek-chat"],
            true,
            Array.Empty<CoreSessionSummary>())
        {
            ModelOptions =
            [
                new CoreModelOption("deepseek", "deepseek-chat", true, true)
                {
                    ReasoningEffort = "medium",
                    ReasoningEfforts = ["low", "medium", "high"]
                }
            ]
        };
        var (vm, _) = Create(temp, store, new FakeCoreSessionService { Snapshot = snapshot });
        await vm.TestConnectionAsync();

        Assert.Equal("medium", vm.ReasoningEffortDisplayLabel);
        vm.SetComposerReasoningEffort("high");
        Assert.Equal("high", store.Load().ModelReasoningEfforts["deepseek/deepseek-chat"]);

        var reloaded = TestData.CreateViewModel(
            temp,
            store,
            new FakeCoreSessionService { Snapshot = snapshot });
        await reloaded.TestConnectionAsync();
        Assert.Equal("high", reloaded.ReasoningEffortDisplayLabel);
    }

    [Fact]
    public async Task PermissionModeSwitchPersistsAndUpdatesTheChip()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var (vm, _) = Create(temp, store, core);
        Assert.True(vm.IsPermissionFull);

        vm.SetPermissionMode("ask");
        await Task.CompletedTask;

        Assert.Equal("ask", store.Load().PermissionMode);
        Assert.False(vm.IsPermissionFull);
        Assert.True(vm.IsPermissionAsk);
        Assert.Equal("请求批准", vm.PermissionModeLabel);
    }

    [Fact]
    public async Task SaveApiKeyWritesThroughCoreAndClearsTheDraft()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService();
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.SettingsSelectedModel = "fake / fake-model";

        vm.SettingsApiKeyDraft = "sk-test-value";
        await vm.SaveApiKeyAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderSetApiKey);
        Assert.Equal("fake", FakeCoreSessionService.ReadParameter(request.Parameters, "id"));
        Assert.Equal("sk-test-value", FakeCoreSessionService.ReadParameter(request.Parameters, "key"));
        Assert.Empty(vm.SettingsApiKeyDraft);
        Assert.True(vm.IsSettingsStatusSuccess);
    }

    [Fact]
    public async Task ParallelToolCallsRouteOutputByCallId()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        core.Raise(new SessionEvent(SessionEventKind.ToolStarted, Name: "shell", Args: "echo 1", CallId: "call-1"));
        core.Raise(new SessionEvent(SessionEventKind.ToolStarted, Name: "read_file", Args: "a.txt", CallId: "call-2"));
        core.Raise(new SessionEvent(SessionEventKind.ToolOutput, Text: "output-one", CallId: "call-1"));
        core.Raise(new SessionEvent(SessionEventKind.ToolOutput, Text: "output-two", CallId: "call-2"));
        core.Raise(new SessionEvent(
            SessionEventKind.ToolCompleted,
            Name: "shell",
            Summary: "first-done",
            CallId: "call-1"));
        core.Raise(new SessionEvent(
            SessionEventKind.ToolCompleted,
            Name: "read_file",
            Summary: "second-done",
            CallId: "call-2"));

        var tools = vm.Messages
            .SelectMany(message => message.IsToolGroup ? message.Children : [message])
            .Where(message => message.IsTool)
            .ToList();

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, tool => tool.Detail.Contains("output-one") && tool.Summary == "first-done");
        Assert.Contains(tools, tool => tool.Detail.Contains("output-two") && tool.Summary == "second-done");
        Assert.DoesNotContain("output-two", tools[0].Detail);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task NewSessionClosesCoreSessionAndReturnsToDraft()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(
            temp,
            store,
            core,
            """{"messages":[{"role":"assistant","text":"历史"}]}""");
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));
        Assert.NotEmpty(vm.Messages);

        await vm.NewSessionAsync();

        Assert.True(core.CloseCalls >= 1);
        Assert.Empty(vm.Messages);
        Assert.Null(vm.ActiveSessionItem);
        Assert.Null(store.Load().ActiveSessionId);
        Assert.False(vm.IsCoreReadOnly);
    }

    private const string RewindHistoryJson = """
        {"messages":[
          {"role":"user","text":"第一个问题","seq":1},
          {"role":"assistant","text":"第一个回答","seq":2},
          {"role":"user","text":"第二个问题","seq":5},
          {"role":"assistant","text":"第二个回答","seq":6}
        ]}
        """;

    [Fact]
    public async Task RegenerateRewindsToTheUserMessageBeforeTheReply()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, RewindHistoryJson);
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        var assistant = vm.Messages[3];
        Assert.True(assistant.IsAssistant);
        Assert.Equal(6, assistant.Seq);

        await vm.RegenerateAsync(assistant);

        Assert.Equal("第二个问题", Assert.Single(core.TurnPrompts));
        Assert.Equal(5, Assert.Single(core.RewindSeqs));
        Assert.False(vm.IsEditingUserMessage);
        Assert.Equal(3, vm.Messages.Count);
        Assert.Equal("第二个问题", vm.Messages[^1].Text);
        Assert.True(vm.Messages[^1].IsUser);
    }

    [Fact]
    public async Task EditingAUserMessageSendsTheRewoundText()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, RewindHistoryJson);
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        vm.BeginEditUserMessage(vm.Messages[2]);
        Assert.True(vm.IsEditingUserMessage);
        Assert.Equal("第二个问题", vm.Composer);

        vm.Composer = "改过的问题";
        await vm.SendAsync();

        Assert.Equal("改过的问题", Assert.Single(core.TurnPrompts));
        Assert.Equal(5, Assert.Single(core.RewindSeqs));
        Assert.False(vm.IsEditingUserMessage);
    }

    [Fact]
    public async Task ResendingAUserMessageReplacesItAndTheFollowingReplies()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, RewindHistoryJson);
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        await vm.ResendUserAsync(vm.Messages[0]);

        Assert.Equal("第一个问题", Assert.Single(core.TurnPrompts));
        Assert.Equal(1, Assert.Single(core.RewindSeqs));
        var only = Assert.Single(vm.Messages);
        Assert.True(only.IsUser);
        Assert.Equal("第一个问题", only.Text);
    }

    [Fact]
    public async Task ConversationRewoundReloadsTheTranscript()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, RewindHistoryJson);
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));
        Assert.Equal(4, vm.Messages.Count);

        core.HistoryJson = """{"messages":[{"role":"user","text":"rewound","seq":1}]}""";
        core.Raise(new SessionEvent(
            SessionEventKind.ConversationRewound,
            Seq: 9,
            RewindFromSeq: 5));
        await Task.Yield();

        var only = Assert.Single(vm.Messages);
        Assert.True(only.IsUser);
        Assert.Equal("rewound", only.Text);
    }

    [Fact]
    public async Task OpenSessionFailureSurfacesTheCoreError()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");
        core.OpenResult = CoreConnectResult.Failed("会话已被占用");

        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        Assert.False(vm.IsCoreReady);
        Assert.Contains("会话已被占用", vm.CoreBannerText);
        Assert.False(vm.IsCoreBusy);
    }

    [Fact]
    public async Task SendWhenCoreCannotOpenKeepsDraftText()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = CoreCatalogSnapshot.Failed("找不到 kl-core"),
            OpenResult = CoreConnectResult.Failed("启动失败")
        };
        var (vm, _) = Create(temp, store, core);

        vm.Composer = "草稿不能丢";
        await vm.SendAsync();

        Assert.Equal("草稿不能丢", vm.Composer);
        Assert.Empty(core.TurnPrompts);
        Assert.Contains("无法连接 KL Core", vm.CoreBannerText);
        Assert.False(vm.IsCoreReady);
    }

    [Fact]
    public async Task RunAutomationUsesEphemeralTurnAndPersistsResult()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.BeginCreateAutomation();
        vm.AutomationName = "每日摘要";
        vm.AutomationPrompt = "总结工作区";
        vm.SelectedAutomationInterval = vm.AutomationIntervals.Single(option => option.Minutes == 15);
        vm.SaveAutomation();
        var automation = Assert.Single(vm.Automations);

        await vm.RunAutomationAsync(automation);

        Assert.Equal("completed", automation.State.LastStatus);
        Assert.Equal("自动结果", automation.State.LastResult);
        Assert.NotNull(automation.State.LastRunAt);
        var request = Assert.Single(core.EphemeralRequests);
        Assert.Equal(temp.Path, request.Workspace);
        Assert.Equal("总结工作区", request.Prompt);
        var saved = Assert.Single(store.Load().Automations);
        Assert.Equal("每日摘要", saved.Name);
        Assert.Equal("completed", saved.LastStatus);
        Assert.Equal("自动结果", saved.LastResult);
    }

    [Fact]
    public async Task AutomationFailureAndEmptyResultsAreRecorded()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            EphemeralHandler = (_, _, _) =>
                Task.FromException<string>(new InvalidOperationException("核心拒绝执行"))
        };
        var (vm, _) = Create(temp, store, core);

        vm.BeginCreateAutomation();
        vm.AutomationName = "失败任务";
        vm.AutomationPrompt = "执行失败";
        vm.SaveAutomation();
        var failed = Assert.Single(vm.Automations);
        await vm.RunAutomationAsync(failed);

        Assert.Equal("failed", failed.State.LastStatus);
        Assert.Contains("核心拒绝执行", failed.State.LastResult);

        core.EphemeralHandler = null;
        core.EphemeralResult = "   ";
        vm.BeginCreateAutomation();
        vm.AutomationName = "空结果任务";
        vm.AutomationPrompt = "空结果";
        vm.SaveAutomation();
        var empty = vm.Automations.Single(item => item.Name == "空结果任务");
        await vm.RunAutomationAsync(empty);

        Assert.Equal("failed", empty.State.LastStatus);
        Assert.Contains("没有返回可保存", empty.State.LastResult);
    }

    [Fact]
    public async Task DueAutomationsRunOnlyEnabledDueTasks()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.BeginCreateAutomation();
        vm.AutomationName = "到期任务";
        vm.AutomationPrompt = "到点了";
        vm.SelectedAutomationInterval = vm.AutomationIntervals[0];
        vm.SaveAutomation();
        var due = Assert.Single(vm.Automations);
        vm.ToggleAutomation(due);
        due.State.NextRunAt = DateTimeOffset.Now.AddMinutes(-1);

        vm.BeginCreateAutomation();
        vm.AutomationName = "暂停任务";
        vm.AutomationPrompt = "不要跑";
        vm.SelectedAutomationInterval = vm.AutomationIntervals[0];
        vm.SaveAutomation();
        var paused = vm.Automations.Single(item => item.Name == "暂停任务");
        paused.State.NextRunAt = DateTimeOffset.Now.AddMinutes(-1);

        await vm.RunDueAutomationsAsync(DateTimeOffset.Now);

        Assert.Equal("到点了", Assert.Single(core.EphemeralRequests).Prompt);
        Assert.Equal("completed", due.State.LastStatus);

        await vm.RunDueAutomationsAsync(DateTimeOffset.Now);
        Assert.Single(core.EphemeralRequests);
    }

    [Fact]
    public async Task SaveSettingsPushesPermissionModeToCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");
        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        vm.SettingsPermissionMode = "ask";
        await vm.SaveSettingsAsync();

        Assert.True(vm.IsSettingsStatusSuccess);
        Assert.Equal("ask", store.Load().PermissionMode);
        vm.SettingsPermissionMode = "full";
        await vm.SaveSettingsAsync();

        var modes = core.Requests
            .Where(request => request.Method == Protocol.PermissionsSet)
            .Select(request => FakeCoreSessionService.ReadParameter(request.Parameters, "approval_mode"))
            .ToList();
        Assert.Equal(new[] { "ask", "dontAsk" }, modes);
    }

    [Fact]
    public async Task OpenSessionAdoptsCoreApprovalMode()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var core = new FakeCoreSessionService();
        core.RequestResponses[Protocol.PermissionsGet] = """{"approval_mode":"auto"}""";
        var vm = CreateWithSession(temp, store, core, """{"messages":[]}""");

        await vm.OpenSessionAsync(Assert.Single(vm.Workspaces[0].Sessions));

        Assert.Equal("auto", store.Load().PermissionMode);
        Assert.Equal("auto", vm.SettingsPermissionMode);
        Assert.True(vm.IsPermissionAuto);
        Assert.DoesNotContain(core.Requests, request => request.Method == Protocol.PermissionsSet);
    }

    [Fact]
    public async Task SaveSettingsRejectsUnknownPermissionMode()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        vm.SettingsPermissionMode = "不存在的模式";
        await vm.SaveSettingsAsync();

        Assert.True(vm.IsSettingsStatusError);
        Assert.Contains("权限模式", vm.SettingsStatus);
        Assert.Empty(core.Requests);
        Assert.Equal("full", store.Load().PermissionMode);
    }

    [Fact]
    public async Task TestConnectionUsesCoreSnapshot()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, core) = Create(temp, store);

        await vm.TestConnectionAsync();

        Assert.True(vm.IsCoreReady);
        Assert.True(vm.IsSettingsStatusSuccess);
        Assert.Contains("test-core", vm.SettingsStatus);
        Assert.Equal("fake / fake-model", vm.CoreProviderText);
        Assert.True(core.SnapshotCalls >= 2);
    }

    [Fact]
    public async Task TestConnectionReportsCoreFailure()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = CoreCatalogSnapshot.Failed("找不到 kl-core")
        };
        var (vm, _) = Create(temp, store, core);

        await vm.TestConnectionAsync();

        Assert.False(vm.IsCoreReady);
        Assert.True(vm.IsSettingsStatusError);
        Assert.Contains("找不到 kl-core", vm.SettingsStatus);
        Assert.Contains("找不到 kl-core", vm.CoreBannerText);
    }

    [Fact]
    public async Task RetryCoreAsyncReconnectsAfterFailure()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService
        {
            Snapshot = CoreCatalogSnapshot.Failed("首次失败")
        };
        var (vm, _) = Create(temp, store, core);
        Assert.False(vm.IsCoreReady);

        core.Snapshot = new CoreCatalogSnapshot(
            true,
            string.Empty,
            "test-core",
            1,
            "fake",
            "fake-model",
            ["fake-model"],
            true,
            Array.Empty<CoreSessionSummary>());
        await vm.RetryCoreAsync();

        Assert.True(vm.IsCoreReady);
        Assert.False(vm.IsCoreRetrying);
        Assert.True(vm.CanRetryCore);
        Assert.Empty(vm.CoreBannerText);
    }
    [Fact]
    public async Task ReasoningDefaultFollowsAvailabilityChanges()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, _) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        vm.OpenAddProviderModel();

        Assert.Equal(
            new[] { "low", "medium", "high", "max" },
            vm.ProviderModelReasoningLevels.Where(level => level.IsEnabled).Select(level => level.Value));
        Assert.Equal("medium", vm.SelectedProviderModelDefaultReasoning);

        vm.ProviderModelReasoningLevels.Single(level => level.Value == "ultra").IsEnabled = true;
        Assert.Equal("medium", vm.SelectedProviderModelDefaultReasoning);

        vm.ProviderModelReasoningLevels.Single(level => level.Value == "low").IsEnabled = false;
        Assert.Equal("medium", vm.SelectedProviderModelDefaultReasoning);

        vm.ProviderModelReasoningLevels.Single(level => level.Value == "medium").IsEnabled = false;
        Assert.Equal("high", vm.SelectedProviderModelDefaultReasoning);

        vm.SelectedProviderModelDefaultReasoning = "max";
        vm.ProviderModelReasoningLevels.Single(level => level.Value == "max").IsEnabled = false;
        Assert.Equal("ultra", vm.SelectedProviderModelDefaultReasoning);

        vm.ProviderModelReasoningLevels.Single(level => level.Value == "ultra").IsEnabled = false;
        Assert.Equal("high", vm.SelectedProviderModelDefaultReasoning);

        vm.ProviderModelReasoningLevels.Single(level => level.Value == "high").IsEnabled = false;
        Assert.Equal(string.Empty, vm.SelectedProviderModelDefaultReasoning);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("default", "medium")]
    public async Task EditingLegacyModelRestoresDefaultReasoningLevels(
        string savedDefault,
        string expectedDefault)
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var snapshot = Snapshot("deepseek", "deepseek-chat") with
        {
            ModelOptions =
            [
                new CoreModelOption("deepseek", "deepseek-chat", true, true)
                {
                    Protocol = "openai-chat",
                    Url = "https://api.example.com/chat/completions",
                    ContextWindow = 128000,
                    CompactThreshold = 0.8,
                    MaxOutputTokens = 8192,
                    ReasoningEffort = savedDefault
                }
            ]
        };
        var (vm, _) = Create(temp, store, new FakeCoreSessionService { Snapshot = snapshot });
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));

        var model = Assert.Single(vm.ProviderModels);
        vm.OpenEditProviderModel(model);

        Assert.Equal(
            new[] { "low", "medium", "high", "max" },
            vm.ProviderModelReasoningLevels.Where(level => level.IsEnabled).Select(level => level.Value));
        Assert.Equal(expectedDefault, vm.SelectedProviderModelDefaultReasoning);
    }

    [Fact]
    public async Task SavingModelKeepsExistingProviderProtocols()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var snapshot = Snapshot("deepseek", "deepseek-chat") with
        {
            ProviderProtocols =
            [
                new CoreProviderProtocols(
                    "deepseek",
                    "https://api.example.com/chat/completions",
                    "https://api.example.com/messages",
                    string.Empty)
            ]
        };
        var (vm, core) = Create(temp, store, new FakeCoreSessionService { Snapshot = snapshot });
        await vm.TestConnectionAsync();
        var provider = Assert.Single(vm.ProviderCards);
        vm.OpenProviderDetail(provider);
        vm.OpenAddProviderModel();
        vm.ProviderModelDraftId = "deepseek-reasoner";
        await vm.SaveProviderModelAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ProviderUpsert);
        var json = JsonSerializer.SerializeToElement(request.Parameters);
        var protocols = json.GetProperty("protocols");
        Assert.Equal(
            "https://api.example.com/chat/completions",
            protocols.GetProperty("openai-chat").GetString());
        Assert.Equal(
            "https://api.example.com/messages",
            protocols.GetProperty("anthropic").GetString());
        Assert.Equal("https://api.example.com/messages", provider.Protocols["anthropic"]);
    }

    [Fact]
    public async Task SavingConnectionUpdatesTheDetailPageImmediately()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, _) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();
        var provider = Assert.Single(vm.ProviderCards);
        vm.OpenProviderDetail(provider);
        Assert.False(vm.ProviderAnthropicProtocolEnabled);

        vm.ProviderDraftAnthropicUrl = "https://api.example.com/messages";
        vm.ProviderAnthropicProtocolEnabled = true;
        await vm.SaveProviderConnectionAsync();
        vm.CloseProviderDetail();
        vm.OpenProviderDetail(provider);

        Assert.True(vm.ProviderAnthropicProtocolEnabled);
        Assert.Equal("https://api.example.com/messages", vm.ProviderDraftAnthropicUrl);
    }

    [Fact]
    public async Task RefreshRemovesDuplicateProviderCards()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, _) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();
        var provider = Assert.Single(vm.ProviderCards);
        vm.ProviderCards.Add(new ProviderCardRow(
            provider.Id,
            provider.Name,
            provider.Note,
            provider.Website,
            provider.IsDefault,
            provider.Enabled,
            provider.KeyConfigured,
            provider.Models,
            provider.Protocols));
        Assert.Equal(2, vm.ProviderCards.Count);

        await vm.RefreshProvidersAsync(force: true);

        Assert.Single(vm.ProviderCards);
    }

    [Fact]
    public void NonAsciiProviderNamesGetAStableId()
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ProviderIdFromName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var first = (string)method.Invoke(null, ["深度求索"])!;
        var second = (string)method.Invoke(null, ["深度求索"])!;

        Assert.Equal(first, second);
        Assert.StartsWith("provider-", first);
    }

    [Fact]
    public async Task DeletingProviderRemovesAllDuplicateCards()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        var provider = Assert.Single(vm.ProviderCards);
        vm.ProviderCards.Add(new ProviderCardRow(
            provider.Id,
            provider.Name,
            provider.Note,
            provider.Website,
            provider.IsDefault,
            provider.Enabled,
            provider.KeyConfigured,
            provider.Models,
            provider.Protocols));
        vm.OpenProviderDetail(provider);
        vm.OpenProviderDeleteConfirm();
        core.Snapshot = CoreCatalogSnapshot.Failed("停止刷新，验证删除结果");

        await vm.ConfirmDeleteProviderAsync();

        Assert.Empty(vm.ProviderCards);
    }

    [Fact]
    public void DuplicateProviderNamesGetCopySuffix()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        var first = new ProviderCardRow("a", "Same", string.Empty, string.Empty, false, true, true, []);
        var second = new ProviderCardRow("b", "Same", string.Empty, string.Empty, false, true, true, []);
        var third = new ProviderCardRow("c", "Same", string.Empty, string.Empty, false, true, true, []);
        vm.ProviderCards.Add(first);
        vm.ProviderCards.Add(second);
        vm.ProviderCards.Add(third);

        var method = typeof(MainWindowViewModel).GetMethod(
            "EnsureUniqueProviderNames",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(vm, null);

        Assert.Equal("Same", first.Name);
        Assert.Equal("Same copy", second.Name);
        Assert.Equal("Same copycopy", third.Name);
    }

    [Fact]
    public async Task ClosingProviderDetailDuringModelSaveDoesNotCrash()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        var model = Assert.Single(vm.ProviderModels);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.ProviderUpsertGate = gate;

        var saving = vm.SetProviderModelEnabledAsync(model, false);
        vm.CloseProviderDetail();
        gate.SetResult(true);
        await saving;

        Assert.False(model.Enabled);
    }

    [Fact]
    public async Task ProviderDetailHeaderFollowsWebsitePresence()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var (vm, _) = Create(temp, store, new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") });
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));

        Assert.True(vm.HasProviderDetailNote);
        Assert.False(vm.HasProviderDetailWebsite);
        Assert.Equal(
            Avalonia.Layout.Orientation.Vertical,
            vm.ProviderDetailHeaderOrientation);

        vm.ProviderEditWebsite = "https://www.deepseek.com";
        Assert.True(vm.SaveProviderProfile());

        Assert.True(vm.HasProviderDetailWebsite);
        Assert.Equal(
            Avalonia.Layout.Orientation.Horizontal,
            vm.ProviderDetailHeaderOrientation);
    }

    [Fact]
    public async Task SavingModelClosesFormBeforeCoreWrite()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();
        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));
        vm.OpenAddProviderModel();
        vm.ProviderModelDraftId = "deepseek-reasoner";
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.ProviderUpsertGate = gate;

        var saving = vm.SaveProviderModelAsync();

        Assert.False(vm.IsProviderModelFormOpen);
        gate.SetResult(true);
        await saving;
        core.ProviderUpsertGate = null;
        Assert.Contains(vm.ProviderModels, model => model.Id == "deepseek-reasoner");
    }

    [Fact]
    public async Task ProviderDetailNeverReadsLocalKeyAndSavesThroughCore()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var credentials = Path.Combine(temp.Path, "credentials");
        Directory.CreateDirectory(credentials);
        var keyPath = Path.Combine(credentials, "deepseek.key");
        File.WriteAllText(keyPath, "sk-local\n");
        var core = new FakeCoreSessionService { Snapshot = Snapshot("deepseek", "deepseek-chat") };
        var (vm, _) = Create(temp, store, core);
        await vm.TestConnectionAsync();

        vm.OpenProviderDetail(Assert.Single(vm.ProviderCards));

        Assert.Empty(vm.ProviderDetailApiKey);
        vm.ProviderDetailApiKey = "sk-new";
        await vm.SaveProviderDetailApiKeyAsync();

        var key = Assert.Single(core.Requests, request => request.Method == Protocol.ProviderSetApiKey);
        Assert.Equal("deepseek", FakeCoreSessionService.ReadParameter(key.Parameters, "id"));
        Assert.Equal("sk-new", FakeCoreSessionService.ReadParameter(key.Parameters, "key"));
        Assert.Empty(vm.ProviderDetailApiKey);
        Assert.Equal("sk-local\n", File.ReadAllText(keyPath));

        vm.CloseProviderDetail();
        Assert.Empty(vm.ProviderDetailApiKey);
    }

}

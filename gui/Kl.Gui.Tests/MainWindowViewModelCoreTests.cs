using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;
using Kl.Gui.ViewModels;

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
        var permission = Assert.Single(
            core.Requests,
            request => request.Method == Protocol.PermissionsSet);
        Assert.Equal("dontAsk", FakeCoreSessionService.ReadParameter(permission.Parameters, "approval_mode"));

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

        vm.SettingsSelectedModel = "other / other-model";
        await vm.SaveModelAsync();

        var request = Assert.Single(core.Requests, item => item.Method == Protocol.ConfigSetDefaultModel);
        Assert.Equal("other", FakeCoreSessionService.ReadParameter(request.Parameters, "provider"));
        Assert.Equal("other-model", FakeCoreSessionService.ReadParameter(request.Parameters, "model"));
        Assert.True(vm.IsSettingsStatusSuccess);
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
        Assert.Equal("高", vm.ReasoningEffortLabel);
        Assert.True(vm.HasReasoningEffort);
        Assert.True(vm.IsReasoningHigh);
        Assert.False(vm.IsReasoningDefault);
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

        vm.SetPermissionMode("请求批准");
        await Task.CompletedTask;

        Assert.Equal("请求批准", store.Load().PermissionMode);
        Assert.False(vm.IsPermissionFull);
        Assert.True(vm.IsPermissionAsk);
        Assert.Equal("请求批准", vm.PermissionMode);
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

        Assert.Equal("完成", automation.State.LastStatus);
        Assert.Equal("自动结果", automation.State.LastResult);
        Assert.NotNull(automation.State.LastRunAt);
        var request = Assert.Single(core.EphemeralRequests);
        Assert.Equal(temp.Path, request.Workspace);
        Assert.Equal("总结工作区", request.Prompt);
        var saved = Assert.Single(store.Load().Automations);
        Assert.Equal("每日摘要", saved.Name);
        Assert.Equal("完成", saved.LastStatus);
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

        Assert.Equal("失败", failed.State.LastStatus);
        Assert.Contains("核心拒绝执行", failed.State.LastResult);

        core.EphemeralHandler = null;
        core.EphemeralResult = "   ";
        vm.BeginCreateAutomation();
        vm.AutomationName = "空结果任务";
        vm.AutomationPrompt = "空结果";
        vm.SaveAutomation();
        var empty = vm.Automations.Single(item => item.Name == "空结果任务");
        await vm.RunAutomationAsync(empty);

        Assert.Equal("失败", empty.State.LastStatus);
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
        Assert.Equal("完成", due.State.LastStatus);

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

        vm.SettingsPermissionMode = "请求批准";
        await vm.SaveSettingsAsync();

        Assert.True(vm.IsSettingsStatusSuccess);
        Assert.Equal("请求批准", store.Load().PermissionMode);
        vm.SettingsPermissionMode = "完全访问";
        await vm.SaveSettingsAsync();

        var modes = core.Requests
            .Where(request => request.Method == Protocol.PermissionsSet)
            .Select(request => FakeCoreSessionService.ReadParameter(request.Parameters, "approval_mode"))
            .ToList();
        Assert.Equal(new[] { "dontAsk", "ask", "dontAsk" }, modes);
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
        Assert.Equal("完全访问", store.Load().PermissionMode);
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
}

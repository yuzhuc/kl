using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DeepSeekHarness.Gui.Models;
using DeepSeekHarness.Gui.Services;
using DeepSeekHarness.Gui.ViewModels;

namespace DeepSeekHarness.Gui.Tests;

public sealed class StateAndServiceTests
{
    [Fact]
    public void StateStoreRoundTripsWorkspaceAndMessages()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var workspaceId = Guid.NewGuid().ToString("N");
        var sessionId = Guid.NewGuid().ToString("N");
        var state = new AppState
        {
            Endpoint = "http://localhost:9999",
            Model = "test-model",
            ApiKeyEnvironmentVariable = "TEST_KEY",
            PermissionMode = "按需确认",
            InteractionMode = "chat",
            ActiveWorkspaceId = workspaceId,
            ActiveSessionId = sessionId,
            Workspaces =
            [
                new WorkspaceState
                {
                    Id = workspaceId,
                    Name = "demo",
                    Path = temp.Path,
                    Sessions =
                    [
                        new SessionState
                        {
                            Id = sessionId,
                            Title = "持久化会话",
                            Messages =
                            [
                                new StoredMessage { Kind = "user", Text = "你好" },
                                new StoredMessage { Kind = "assistant", Text = "你好，世界" }
                            ]
                        }
                    ]
                }
            ]
        };

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal("http://localhost:9999", loaded.Endpoint);
        Assert.Equal("chat", loaded.InteractionMode);
        Assert.Equal("按需确认", loaded.PermissionMode);
        Assert.Equal("持久化会话", Assert.Single(loaded.Workspaces).Sessions[0].Title);
        Assert.Equal("你好，世界", loaded.Workspaces[0].Sessions[0].Messages[1].Text);
    }

    [Fact]
    public async Task StreamingClientParsesReasoningToolCallsAndContent()
    {
        var sse = string.Join("\n", new[]
        {
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先分析。\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"q\\\":1}\"}}]}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"最终回答\"}}]}",
            "",
            "data: [DONE]",
            ""
        });
        var handler = new CaptureHandler(HttpStatusCode.OK, sse);
        var service = new OpenAiCompatibleChatService(new HttpClient(handler));
        var received = new List<ChatStreamDelta>();

        await foreach (var delta in service.StreamAsync(
            new ChatCompletionSettings("http://localhost:1234/v1", "test-model", "secret"),
            [new ChatRequestMessage("user", "hello")]))
        {
            received.Add(delta);
        }

        Assert.Equal("先分析。", received[0].Reasoning);
        Assert.Equal("lookup", Assert.Single(received[1].ToolCalls!).Name);
        Assert.Equal("最终回答", received[2].Content);
        Assert.Equal("Bearer secret", handler.Authorization);
        Assert.Equal("http://localhost:1234/v1/chat/completions", handler.RequestUri);
        using var requestJson = JsonDocument.Parse(handler.Body!);
        Assert.True(requestJson.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("hello", requestJson.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void LocalPluginCatalogLoadsManifestsAndSkipsInvalidFiles()
    {
        using var temp = new TempDirectory();
        var pluginRoot = Path.Combine(temp.Path, "plugins");
        var validDirectory = Path.Combine(pluginRoot, "sample");
        var invalidDirectory = Path.Combine(pluginRoot, "broken");
        Directory.CreateDirectory(validDirectory);
        Directory.CreateDirectory(invalidDirectory);
        File.WriteAllText(
            Path.Combine(validDirectory, "plugin.json"),
            @"{""name"":""Sample Plugin"",""version"":""1.2.0"",""description"":""Demo""}");
        File.WriteAllText(Path.Combine(invalidDirectory, "plugin.json"), "{not-json");

        var service = new LocalPluginCatalogService(pluginRoot);
        var plugin = Assert.Single(service.Load());

        Assert.Equal("Sample Plugin", plugin.Name);
        Assert.Equal("1.2.0", plugin.Version);
        Assert.Equal("Demo", plugin.Description);
    }

    [Fact]
    public async Task ConnectionTestUsesModelsEndpointAndReportsModelCount()
    {
        var handler = new CaptureHandler(
            HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"model-a\"},{\"id\":\"model-b\"}]}");
        var service = new OpenAiCompatibleConnectionTestService(new HttpClient(handler));

        var result = await service.TestAsync(
            new ChatCompletionSettings("http://localhost:1234/v1/chat/completions", "model", "secret"));

        Assert.True(result.Success);
        Assert.Contains("2 个模型", result.Message);
        Assert.Equal("GET", handler.Method);
        Assert.Equal("http://localhost:1234/v1/models", handler.RequestUri);
        Assert.Equal("Bearer secret", handler.Authorization);
    }

    [Fact]
    public async Task StreamingClientSurfacesHttpErrors()
    {
        var handler = new CaptureHandler(HttpStatusCode.Unauthorized, "invalid key");
        var service = new OpenAiCompatibleChatService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.StreamAsync(
                new ChatCompletionSettings("http://localhost", "model", "key"),
                [new ChatRequestMessage("user", "hello")]))
            {
            }
        });

        Assert.Contains("401", error.Message);
        Assert.Contains("invalid key", error.Message);
    }
}

public sealed class ChatItemMigrationTests
{
    [Fact]
    public void LegacyToolMessageKeepsArgumentsInDetailWithoutDuplicateSummary()
    {
        var item = ChatItemViewModel.FromStored(new StoredMessage
        {
            Kind = "tool",
            Header = "调用 inspect_workspace",
            Text = @"{""path"":""D:\\kl""}",
            Detail = "模型请求调用工具。",
            Summary = "调用 inspect_workspace",
            Status = "完成"
        });

        Assert.Equal("模型请求调用工具。", item.Text);
        Assert.Equal(@"{""path"":""D:\\kl""}", item.Detail);
        Assert.Equal("模型请求调用工具。", item.CollapsedText);
    }
}

public sealed class ViewModelTests
{
    [Fact]
    public async Task EmptyDraftIsNotPersistedUntilFirstMessage()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var vm = CreateViewModel(store, new ScriptedChatService());

        Assert.Empty(store.Load().Workspaces[0].Sessions);
        vm.NewSession();
        Assert.Empty(store.Load().Workspaces[0].Sessions);

        vm.Composer = "第一条消息";
        await vm.SendAsync();

        var saved = store.Load();
        var session = Assert.Single(saved.Workspaces[0].Sessions);
        Assert.Equal("第一条消息", session.Title);
        Assert.Equal("user", session.Messages[0].Kind);
        Assert.Contains(session.Messages, message => message.Kind == "reasoning");
        var assistant = Assert.Single(session.Messages, message => message.Kind == "assistant");
        Assert.Equal("模拟回复：第一条消息", assistant.Text);
    }

    [Fact]
    public async Task SessionSearchRenameSwitchAndDeletePersist()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var vm = CreateViewModel(store, new ScriptedChatService());

        vm.Composer = "第一个会话";
        await vm.SendAsync();
        vm.NewSession();
        vm.Composer = "第二个会话";
        await vm.SendAsync();

        var workspace = Assert.Single(vm.Workspaces);
        Assert.Equal(2, workspace.Sessions.Count);
        var first = workspace.Sessions.Single(item => item.Title == "第一个会话");
        vm.OpenSession(first);
        Assert.Equal(first.Id, store.Load().ActiveSessionId);

        vm.SearchText = "不存在";
        Assert.True(vm.ShowNoSearchResults);
        vm.SearchText = "模拟回复";
        Assert.All(workspace.Sessions, item => Assert.True(item.IsVisible));

        vm.BeginRename(first);
        vm.RenameTitle = "重命名后的会话";
        vm.ConfirmRename();
        Assert.Contains(store.Load().Workspaces[0].Sessions, item => item.Title == "重命名后的会话");

        vm.BeginDelete(first);
        vm.ConfirmDelete();
        var afterDelete = store.Load();
        Assert.Single(afterDelete.Workspaces[0].Sessions);
        Assert.Equal("第二个会话", afterDelete.Workspaces[0].Sessions[0].Title);
    }

    [Fact]
    public async Task TextAttachmentReachesModelAndPersists()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var filePath = Path.Combine(temp.Path, "context.md");
        await File.WriteAllTextAsync(filePath, "# 项目上下文\n附件内容 42");
        var chat = new ScriptedChatService();
        var vm = CreateViewModel(store, chat);

        vm.AddAttachments([filePath]);
        Assert.True(vm.HasPendingAttachments);
        Assert.True(vm.CanSend);
        await vm.SendAsync();

        Assert.False(vm.HasPendingAttachments);
        Assert.Contains("附件内容 42", chat.LastMessages![^1].Content);
        var saved = Assert.Single(store.Load().Workspaces[0].Sessions);
        var user = Assert.Single(saved.Messages, message => message.Kind == "user");
        Assert.Contains("context.md", user.Text);
        Assert.Contains("附件内容 42", user.Detail);
    }

    [Fact]
    public async Task CancellationMarksTurnStoppedAndPersists()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var service = new BlockingChatService();
        var vm = CreateViewModel(store, service);
        vm.Composer = "取消这个请求";

        var sendTask = vm.SendAsync();
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.CancelAsync();
        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsBusy);
        Assert.Contains(vm.Messages, item => item.IsError && item.Text.Contains("已停止"));
        var saved = Assert.Single(store.Load().Workspaces[0].Sessions);
        Assert.Contains(saved.Messages, item => item.Kind == "error" && item.Text.Contains("已停止"));
    }

    [Fact]
    public async Task ServiceFailureIsShownWithoutLosingUserMessage()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var vm = CreateViewModel(store, new FailingChatService());
        vm.Composer = "失败请求";

        await vm.SendAsync();

        Assert.False(vm.IsBusy);
        Assert.True(vm.HasError);
        Assert.Contains("模拟网络错误", vm.ErrorMessage);
        var saved = Assert.Single(store.Load().Workspaces[0].Sessions);
        Assert.Equal("user", saved.Messages[0].Kind);
        Assert.Contains(saved.Messages, item => item.Kind == "error");
    }

    [Fact]
    public async Task ConnectionTestAndSettingsValidationUseDraftValues()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var connection = new RecordingConnectionTestService(
            new ConnectionTestResult(true, "连接成功，检测到 1 个模型。"));
        var vm = CreateViewModel(store, new ScriptedChatService(), connection);

        vm.SettingsEndpoint = "http://127.0.0.1:8765";
        vm.SettingsModel = "draft-model";
        vm.SettingsApiKey = "draft-secret";
        vm.SettingsPermissionMode = "按需确认";
        await vm.TestConnectionAsync();

        Assert.True(vm.IsSettingsStatusSuccess);
        Assert.Equal("http://127.0.0.1:8765", connection.LastSettings!.Endpoint);
        Assert.Equal("draft-model", connection.LastSettings.Model);
        Assert.Equal("draft-secret", connection.LastSettings.ApiKey);
        Assert.Equal("http://localhost:1234", store.Load().Endpoint);

        vm.SettingsEndpoint = "not-a-url";
        await vm.TestConnectionAsync();
        Assert.True(vm.IsSettingsStatusError);

        vm.ResetSettingsDefaults();
        Assert.Equal("https://api.deepseek.com", vm.SettingsEndpoint);
        Assert.Equal("deepseek-chat", vm.SettingsModel);
    }

    [Fact]
    public async Task AutomationTaskPersistsRunsAndSchedules()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var chat = new ScriptedChatService();
        var vm = CreateViewModel(store, chat);

        vm.BeginCreateAutomation();
        vm.AutomationName = "每日摘要";
        vm.AutomationPrompt = "总结工作区";
        vm.SelectedAutomationInterval = vm.AutomationIntervals.Single(option => option.Minutes == 15);
        vm.SaveAutomation();

        var automation = Assert.Single(vm.Automations);
        Assert.False(automation.IsEnabled);
        Assert.Null(automation.State.NextRunAt);

        vm.ToggleAutomation(automation);
        Assert.True(automation.IsEnabled);
        Assert.NotNull(automation.State.NextRunAt);

        await vm.RunAutomationAsync(automation);
        Assert.Equal("完成", automation.State.LastStatus);
        Assert.Contains("总结工作区", automation.State.LastResult);
        Assert.NotNull(automation.State.LastRunAt);

        automation.State.NextRunAt = DateTimeOffset.Now.AddMinutes(-1);
        var callsBeforeDueRun = chat.Calls;
        await vm.RunDueAutomationsAsync(DateTimeOffset.Now);
        Assert.Equal(callsBeforeDueRun + 1, chat.Calls);

        var saved = Assert.Single(store.Load().Automations);
        Assert.Equal("每日摘要", saved.Name);
        Assert.Equal("完成", saved.LastStatus);
        Assert.Contains("总结工作区", saved.LastResult);
    }

    [Fact]
    public void AutomationValidationEditAndDeletePersist()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var vm = CreateViewModel(store, new ScriptedChatService());

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
    public async Task AutomationFailureIsRecordedAndDisabledTaskIsSkipped()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var vm = CreateViewModel(store, new FailingChatService());

        vm.BeginCreateAutomation();
        vm.AutomationName = "失败任务";
        vm.AutomationPrompt = "执行失败";
        vm.SelectedAutomationInterval = vm.AutomationIntervals[1];
        vm.SaveAutomation();
        var failed = Assert.Single(vm.Automations);

        await vm.RunAutomationAsync(failed);
        Assert.Equal("失败", failed.State.LastStatus);
        Assert.Contains("模拟网络错误", failed.State.LastResult);

        await vm.RunDueAutomationsAsync(DateTimeOffset.Now.AddDays(1));
        Assert.Equal("失败", failed.State.LastStatus);
    }

    [Fact]
    public void WorkspaceCanBeAddedSelectedAndPersisted()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var secondPath = Path.Combine(temp.Path, "second-workspace");
        Directory.CreateDirectory(secondPath);
        var vm = CreateViewModel(store, new ScriptedChatService());

        Assert.True(vm.AddWorkspace(secondPath));
        Assert.Equal(2, vm.Workspaces.Count);
        var selected = vm.Workspaces.Single(workspace =>
            string.Equals(workspace.Path, secondPath, StringComparison.OrdinalIgnoreCase));
        Assert.True(selected.IsSelected);
        Assert.Equal(selected.Id, store.Load().ActiveWorkspaceId);

        vm.SelectWorkspace(vm.Workspaces.Single(workspace => workspace.Id != selected.Id));
        Assert.NotEqual(selected.Id, store.Load().ActiveWorkspaceId);
    }

    [Fact]
    public void SettingsModalSectionsAndAutomationFiltersUpdateViewState()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var vm = CreateViewModel(store, new ScriptedChatService());

        vm.OpenSettings();
        Assert.True(vm.IsSettingsOpen);
        Assert.True(vm.IsSettingsGeneral);
        vm.SelectSettingsPane(SettingsPane.Models);
        Assert.True(vm.IsSettingsModels);
        Assert.Equal("模型", vm.SettingsPaneTitle);
        vm.CloseSettings();
        Assert.False(vm.IsSettingsOpen);

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

        vm.SetAutomationFilter(AutomationFilter.Active);
        Assert.True(active.IsVisible);
        Assert.False(vm.Automations.Single(item => item.Name == "暂停任务").IsVisible);
        Assert.False(vm.ShowNoAutomations);

        vm.AutomationSearch = "不存在";
        Assert.True(vm.ShowNoAutomations);
        vm.AutomationSearch = "暂停";
        vm.SetAutomationFilter(AutomationFilter.Ended);
        Assert.True(vm.Automations.Single(item => item.Name == "暂停任务").IsVisible);
    }

    [Fact]
    public void PluginImportCopiesManifestAndAssets()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var pluginRoot = Path.Combine(temp.Path, "managed-plugins");
        var pluginService = new LocalPluginCatalogService(pluginRoot);
        var vm = CreateViewModel(
            store,
            new ScriptedChatService(),
            pluginCatalogService: pluginService);
        var source = Path.Combine(temp.Path, "source-plugin");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "plugin.json"),
            @"{""name"":""Imported Plugin"",""version"":""2.0.0"",""description"":""Imported""}");
        File.WriteAllText(Path.Combine(source, "asset.txt"), "asset");

        Assert.True(vm.ImportPlugin(source));
        Assert.Single(vm.Plugins);
        Assert.Equal("Imported Plugin", vm.Plugins[0].Name);
        Assert.True(File.Exists(Path.Combine(pluginRoot, "source-plugin", "asset.txt")));
    }

    [Fact]
    public void SettingsAndInteractionModePersist()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        Seed(store, temp.Path);
        var vm = CreateViewModel(store, new ScriptedChatService());

        vm.SettingsEndpoint = "http://127.0.0.1:4567/v1/";
        vm.SettingsModel = "custom-model";
        vm.SettingsApiKey = "sk-custom-secret";
        vm.SettingsPermissionMode = "按需确认";
        vm.SettingsContentFontSize = 16;
        vm.SetInteractionMode(workMode: false);
        vm.SaveSettings();

        var loaded = store.Load();
        Assert.Equal("http://127.0.0.1:4567/v1", loaded.Endpoint);
        Assert.Equal("custom-model", loaded.Model);
        Assert.Equal("sk-custom-secret", loaded.ApiKey);
        Assert.Empty(loaded.ApiKeyEnvironmentVariable);
        Assert.Contains("sk-custom-secret", File.ReadAllText(store.Path));
        Assert.Equal("按需确认", loaded.PermissionMode);
        Assert.Equal(16, loaded.ContentFontSize);
        Assert.Equal("chat", loaded.InteractionMode);
    }

    private static MainWindowViewModel CreateViewModel(
        StateStore store,
        IChatCompletionService service,
        IConnectionTestService? connectionTestService = null,
        IPluginCatalogService? pluginCatalogService = null) =>
        new(store, service, _ => "test-key", connectionTestService, pluginCatalogService);

    private static void Seed(StateStore store, string workspacePath)
    {
        var workspaceId = Guid.NewGuid().ToString("N");
        store.Save(new AppState
        {
            Endpoint = "http://localhost:1234",
            Model = "test-model",
            ApiKeyEnvironmentVariable = "TEST_KEY",
            InteractionMode = "work",
            ActiveWorkspaceId = workspaceId,
            Workspaces =
            [
                new WorkspaceState
                {
                    Id = workspaceId,
                    Name = Path.GetFileName(workspacePath),
                    Path = workspacePath
                }
            ]
        });
    }
}

public sealed class ScriptedChatService : IChatCompletionService
{
    public int Calls { get; private set; }
    public IReadOnlyList<ChatRequestMessage>? LastMessages { get; private set; }

    public async IAsyncEnumerable<ChatStreamDelta> StreamAsync(
        ChatCompletionSettings settings,
        IReadOnlyList<ChatRequestMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        LastMessages = messages;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatStreamDelta("先思考。", "模拟回复：" + messages[^1].Content);
    }
}

public sealed class BlockingChatService : IChatCompletionService
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<ChatStreamDelta> StreamAsync(
        ChatCompletionSettings settings,
        IReadOnlyList<ChatRequestMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield return new ChatStreamDelta("", "不会到达");
    }
}

public sealed class FailingChatService : IChatCompletionService
{
    public async IAsyncEnumerable<ChatStreamDelta> StreamAsync(
        ChatCompletionSettings settings,
        IReadOnlyList<ChatRequestMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (settings.Model.Length >= 0)
        {
            throw new HttpRequestException("模拟网络错误");
        }
        yield break;
    }
}

public sealed class RecordingConnectionTestService : IConnectionTestService
{
    private readonly ConnectionTestResult _result;

    public RecordingConnectionTestService(ConnectionTestResult result)
    {
        _result = result;
    }

    public ChatCompletionSettings? LastSettings { get; private set; }

    public Task<ConnectionTestResult> TestAsync(
        ChatCompletionSettings settings,
        CancellationToken cancellationToken = default)
    {
        LastSettings = settings;
        return Task.FromResult(_result);
    }
}

public sealed class CaptureHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _responseBody;

    public CaptureHandler(HttpStatusCode status, string responseBody)
    {
        _status = status;
        _responseBody = responseBody;
    }

    public string? Authorization { get; private set; }
    public string? RequestUri { get; private set; }
    public string? Method { get; private set; }
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Authorization = request.Headers.Authorization?.ToString();
        RequestUri = request.RequestUri?.ToString();
        Method = request.Method.Method;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "text/event-stream")
        };
    }
}

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DeepSeekHarnessGuiTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public StateStore CreateStore() => new(System.IO.Path.Combine(Path, "state.json"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}




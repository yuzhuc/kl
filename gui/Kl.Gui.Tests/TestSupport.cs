using System.Text.Json;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;
using Kl.Gui.ViewModels;

namespace Kl.Gui.Tests;

/// <summary>Throwaway directory used by state-store, plugin and view-model tests.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "KlGuiTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string PluginDirectory => System.IO.Path.Combine(Path, "plugins");

    public StateStore CreateStore() => new(System.IO.Path.Combine(Path, "state.json"));

    public LocalPluginCatalogService CreatePluginCatalog() => new(PluginDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A lingering handle must not fail the test run; the OS temp dir is disposable.
        }
    }
}

internal static class TestData
{
    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Writes a state file with a single workspace and the given sessions.</summary>
    public static string Seed(StateStore store, string workspacePath, params SessionState[] sessions)
    {
        var workspaceId = Guid.NewGuid().ToString("N");
        store.Save(new AppState
        {
            Endpoint = "http://localhost:1234",
            Model = "test-model",
            InteractionMode = "work",
            ActiveWorkspaceId = workspaceId,
            Workspaces =
            [
                new WorkspaceState
                {
                    Id = workspaceId,
                    Name = System.IO.Path.GetFileName(workspacePath),
                    Path = workspacePath,
                    Sessions = sessions.ToList()
                }
            ]
        });
        return workspaceId;
    }

    public static SessionState StoredSession(string id, string title, string text = "旧消息") => new()
    {
        Id = id,
        Title = title,
        CreatedAt = DateTimeOffset.Now.AddDays(-1),
        UpdatedAt = DateTimeOffset.Now.AddDays(-1),
        Messages = [new StoredMessage { Kind = "user", Text = text }]
    };

    public static CoreSessionSummary Summary(
        string sessionId,
        string workspace,
        string? title,
        int messageCount = 2,
        string updatedAt = "2026-11-03T10:00:00+08:00") =>
        new(sessionId, workspace, "fake", "fake-model", updatedAt, messageCount, title, null, null);

    public static MainWindowViewModel CreateViewModel(
        TempDirectory temp,
        StateStore store,
        FakeCoreSessionService core) =>
        new(store, temp.CreatePluginCatalog(), core, action => action());
}

/// <summary>
/// In-memory <see cref="ICoreSessionService"/> used to drive the view-model without
/// spawning kl-core. Events can be raised synchronously through <see cref="Raise"/>.
/// </summary>
internal sealed class FakeCoreSessionService : ICoreSessionService
{
    public bool IsSessionOpen { get; private set; }
    public bool IsWriter { get; private set; } = true;
    public string SessionId { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string Workspace { get; private set; } = string.Empty;
    public long LastSeq { get; set; }
    public CoreSessionPresenter? Presenter => null;

    public event Action<SessionEvent>? EventReceived;

    public string NextSessionId { get; set; } = "core-session-1";
    public CoreCatalogSnapshot Snapshot { get; set; } = new(
        true,
        string.Empty,
        "test-core",
        1,
        "fake",
        "fake-model",
        ["fake-model"],
        true,
        Array.Empty<CoreSessionSummary>());

    public CoreConnectResult? OpenResult { get; set; }
    public string HistoryJson { get; set; } = """{"messages":[]}""";
    public string EphemeralResult { get; set; } = "自动结果";
    public Func<string, CancellationToken, Task<TurnOutcome>>? TurnHandler { get; set; }
    public Func<string, string, CancellationToken, Task<string>>? EphemeralHandler { get; set; }
    public Func<string, string, bool>? ApprovalResolver { get; set; }

    public List<CoreSessionRequest> OpenRequests { get; } = [];
    public List<string> TurnPrompts { get; } = [];
    public List<int> HistoryLimits { get; } = [];
    public CoreReconnectResult ReconnectResult { get; set; } = new(true, true, string.Empty);
    public bool WriterAllowed { get; set; }
    public int ReconnectCalls { get; private set; }
    public int WriterCalls { get; private set; }
    public int ForkCalls { get; private set; }
    public int CompactCalls { get; private set; }
    public int DoctorCalls { get; private set; }
    public string ForkedSessionId { get; set; } = "forked-session-1";
    public string DoctorJson { get; set; } =
        "{\"ok\":true,\"core_version\":\"test-core\",\"checks\":"
        + "[{\"name\":\"core\",\"ok\":true,\"detail\":\"ready\"}]}";
    public List<(string Method, object? Parameters)> Requests { get; } = [];
    public List<(string ApprovalId, string Decision)> ApprovalDecisions { get; } = [];
    public List<(string Workspace, string Prompt)> EphemeralRequests { get; } = [];
    public int SnapshotCalls { get; private set; }
    public int CloseCalls { get; private set; }
    public int CancelCalls { get; private set; }
    public bool Disposed { get; private set; }

    public void Raise(SessionEvent sessionEvent) => EventReceived?.Invoke(sessionEvent);

    public Task<CoreConnectResult> OpenSessionAsync(
        CoreSessionRequest request,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        OpenRequests.Add(request);
        progress?.Invoke("session-ready");
        var result = OpenResult ?? new CoreConnectResult(
            true,
            string.Empty,
            request.SessionId ?? NextSessionId,
            request.Workspace,
            "fake",
            "fake-model",
            true);
        if (result.Success)
        {
            IsSessionOpen = true;
            IsWriter = result.IsWriter;
            SessionId = result.SessionId;
            Workspace = result.Workspace;
            Provider = result.Provider;
            Model = result.Model;
        }

        return Task.FromResult(result);
    }

    public Task CloseSessionAsync()
    {
        CloseCalls++;
        IsSessionOpen = false;
        return Task.CompletedTask;
    }

    public Task<JsonElement> HistoryAsync(int limit, CancellationToken cancellationToken)
    {
        HistoryLimits.Add(limit);
        return Task.FromResult(TestData.Json(HistoryJson));
    }

    public Task<CoreReconnectResult> ReconnectAsync(CancellationToken cancellationToken)
    {
        ReconnectCalls++;
        if (ReconnectResult.Success)
        {
            IsWriter = true;
        }

        return Task.FromResult(ReconnectResult);
    }

    public Task<bool> TryBecomeWriterAsync(CancellationToken cancellationToken)
    {
        WriterCalls++;
        if (WriterAllowed)
        {
            IsWriter = true;
        }

        return Task.FromResult(WriterAllowed);
    }

    public Task<string> ForkAsync(CancellationToken cancellationToken)
    {
        ForkCalls++;
        return Task.FromResult(ForkedSessionId);
    }

    public Task<JsonElement> CompactAsync(CancellationToken cancellationToken)
    {
        CompactCalls++;
        return Task.FromResult(TestData.Json(
            "{\"omitted_messages\":3,\"estimated_tokens\":10,"
            + "\"context_window\":1000,\"compact_threshold\":0.8}"));
    }

    public Task<JsonElement> DoctorAsync(CancellationToken cancellationToken)
    {
        DoctorCalls++;
        return Task.FromResult(TestData.Json(DoctorJson));
    }

    public string ReasoningEffort { get; set; } = "default";
    public int ReasoningGetCalls { get; private set; }
    public List<string> ReasoningSets { get; } = [];

    public Task<string> GetReasoningEffortAsync(CancellationToken cancellationToken)
    {
        ReasoningGetCalls++;
        return Task.FromResult(ReasoningEffort);
    }

    public Task<string> SetReasoningEffortAsync(string effort, CancellationToken cancellationToken)
    {
        ReasoningSets.Add(effort);
        ReasoningEffort = effort;
        return Task.FromResult(effort);
    }

    public Task<CoreCatalogSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        SnapshotCalls++;
        return Task.FromResult(Snapshot);
    }

    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        Requests.Add((method, parameters));
        return Task.FromResult(TestData.Json("{}"));
    }

    public Task<TurnOutcome> RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        TurnPrompts.Add(prompt);
        return TurnHandler is null
            ? Task.FromResult(new TurnOutcome("completed", string.Empty))
            : TurnHandler(prompt, cancellationToken);
    }

    public Task CancelAsync(CancellationToken cancellationToken)
    {
        CancelCalls++;
        return Task.CompletedTask;
    }

    public Task<string> RunEphemeralTurnAsync(
        string workspace,
        string prompt,
        CancellationToken cancellationToken)
    {
        EphemeralRequests.Add((workspace, prompt));
        return EphemeralHandler is null
            ? Task.FromResult(EphemeralResult)
            : EphemeralHandler(workspace, prompt, cancellationToken);
    }

    public bool ResolveApproval(string approvalId, string decision)
    {
        ApprovalDecisions.Add((approvalId, decision));
        return ApprovalResolver?.Invoke(approvalId, decision) ?? true;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public static string? ReadParameter(object? parameters, string name) =>
        parameters?.GetType().GetProperty(name)?.GetValue(parameters) as string;
}

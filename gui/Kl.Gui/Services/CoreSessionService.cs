using System.Text.Json;
using System.Threading.Channels;
using Kl.Client;

namespace Kl.Gui.Services;

/// <summary>Options for opening one core session.</summary>
public sealed class CoreSessionRequest
{
    public string Workspace { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Quality { get; init; }
    public string? HomePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? CorePath { get; init; }
}

public sealed record CoreConnectResult(
    bool Success,
    string Error,
    string SessionId,
    string Workspace,
    string Provider,
    string Model,
    bool IsWriter)
{
    public static CoreConnectResult Failed(string error) =>
        new(false, error, string.Empty, string.Empty, string.Empty, string.Empty, false);
}

/// <summary>
/// Owns the active <see cref="KlSessionClient"/>. The GUI never talks to
/// kl-core directly: this service is the only place that starts, resumes,
/// observes and disposes core sessions.
/// </summary>
public interface ICoreSessionService : IAsyncDisposable
{
    bool IsSessionOpen { get; }
    bool IsWriter { get; }
    string SessionId { get; }
    string Provider { get; }
    string Model { get; }
    string Workspace { get; }
    long LastSeq { get; }
    CoreSessionPresenter? Presenter { get; }

    /// <summary>Raised on a thread-pool thread for every core event.</summary>
    event Action<SessionEvent>? EventReceived;

    Task<CoreConnectResult> OpenSessionAsync(
        CoreSessionRequest request,
        Action<string>? progress,
        CancellationToken cancellationToken);

    Task CloseSessionAsync();

    Task<JsonElement> HistoryAsync(int limit, CancellationToken cancellationToken);

    Task<CoreCatalogSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken);

    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken);

    Task<TurnOutcome> RunTurnAsync(string prompt, CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one isolated prompt in a throwaway core session (used by the
    /// automation page) and returns the final assistant text.
    /// </summary>
    Task<string> RunEphemeralTurnAsync(string workspace, string prompt, CancellationToken cancellationToken);

    bool ResolveApproval(string approvalId, string decision);
}

public sealed class CoreSessionService : ICoreSessionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly KlClientOptions _catalogOptions;
    private KlSessionClient? _session;
    private CoreSessionPresenter? _presenter;
    private CancellationTokenSource? _pumpCancellation;

    public CoreSessionService()
    {
        _catalogOptions = new KlClientOptions();
    }

    public bool IsSessionOpen => _session is not null;
    public bool IsWriter => _session?.IsWriter ?? false;
    public string SessionId => _session?.SessionId ?? string.Empty;
    public string Provider => _session?.Provider ?? string.Empty;
    public string Model => _session?.Model ?? string.Empty;
    public string Workspace => _session?.Workspace ?? string.Empty;
    public long LastSeq => _session?.LastSeq ?? 0;
    public CoreSessionPresenter? Presenter => _presenter;

    public event Action<SessionEvent>? EventReceived;

    public async Task<CoreConnectResult> OpenSessionAsync(
        CoreSessionRequest request,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseSessionCoreAsync().ConfigureAwait(false);

            var presenter = new CoreSessionPresenter();
            var clientOptions = new KlClientOptions
            {
                CorePath = request.CorePath,
                ConfigPath = request.ConfigPath,
                HomePath = request.HomePath
            };
            var sessionOptions = new KlSessionOptions
            {
                SessionId = string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId,
                Workspace = string.IsNullOrWhiteSpace(request.Workspace) ? null : request.Workspace,
                Provider = string.IsNullOrWhiteSpace(request.Provider) ? null : request.Provider,
                Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model,
                Quality = string.IsNullOrWhiteSpace(request.Quality) ? null : request.Quality
            };

            try
            {
                var session = await KlSessionClient.StartAsync(
                    clientOptions,
                    sessionOptions,
                    presenter,
                    cancellationToken,
                    progress).ConfigureAwait(false);

                _presenter = presenter;
                _session = session;
                _pumpCancellation = new CancellationTokenSource();
                _ = PumpEventsAsync(presenter, _pumpCancellation.Token);
                return new CoreConnectResult(
                    true,
                    string.Empty,
                    session.SessionId,
                    session.Workspace,
                    session.Provider,
                    session.Model,
                    session.IsWriter);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                presenter.Complete();
                return CoreConnectResult.Failed(DescribeError(error));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseSessionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSessionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseSessionCoreAsync()
    {
        var pump = _pumpCancellation;
        _pumpCancellation = null;
        pump?.Cancel();

        var presenter = _presenter;
        var session = _session;
        _presenter = null;
        _session = null;

        presenter?.Complete();
        if (session is not null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A dead core connection must not block switching or shutting down.
            }
        }

        pump?.Dispose();
    }

    private async Task PumpEventsAsync(CoreSessionPresenter presenter, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var sessionEvent in presenter.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    EventReceived?.Invoke(sessionEvent);
                }
                catch (Exception)
                {
                    // A UI subscriber failure must not kill the event pump.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    public Task<JsonElement> HistoryAsync(int limit, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("KL Core 会话尚未建立。");
        return session.HistoryAsync(limit, cancellationToken);
    }

    public async Task<CoreCatalogSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        KlCoreClient? client = null;
        try
        {
            client = KlCoreClient.Start(_catalogOptions);
            var handshake = await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);

            JsonElement providerList = default;
            try
            {
                providerList = await client.RequestAsync(Protocol.ProviderList, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A missing provider catalog is not fatal for session browsing.
            }

            JsonElement sessionList = default;
            try
            {
                sessionList = await client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Keep the snapshot usable even when session.list fails.
            }

            return CoreProtocolParser.ParseCatalog(handshake, providerList, sessionList);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CoreCatalogSnapshot.Failed(DescribeError(error));
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("KL Core 会话尚未建立。");
        return session.RequestAsync(method, parameters, cancellationToken);
    }

    public async Task<TurnOutcome> RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("KL Core 会话尚未建立。");
        return await session.RunTurnAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (_session is { } session)
        {
            await session.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> RunEphemeralTurnAsync(
        string workspace,
        string prompt,
        CancellationToken cancellationToken)
    {
        var presenter = new CoreSessionPresenter();
        var session = await KlSessionClient.StartAsync(
            _catalogOptions,
            new KlSessionOptions { Workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace },
            presenter,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await session.RunTurnAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (outcome.Status == "failed")
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(outcome.Text) ? "自动化任务执行失败。" : outcome.Text);
            }

            var history = await session.HistoryAsync(20, cancellationToken).ConfigureAwait(false);
            var messages = CoreProtocolParser.ParseHistory(history);
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                if (string.Equals(messages[index].Role, "assistant", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(messages[index].Text))
                {
                    return messages[index].Text.Trim();
                }
            }

            return outcome.Text.Trim();
        }
        finally
        {
            presenter.Complete();
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The throwaway session may already be dead; never leak it to the caller.
            }
        }
    }

    public bool ResolveApproval(string approvalId, string decision) =>
        _presenter?.ResolveApprovalDecision(approvalId, decision) ?? false;

    public async ValueTask DisposeAsync()
    {
        await CloseSessionAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static string DescribeError(Exception error)
    {
        if (error is FileNotFoundException missingCore)
        {
            return "未找到 kl-core 可执行文件，请先构建 Core 或设置 KL_CORE_PATH。"
                + (string.IsNullOrWhiteSpace(missingCore.Message) ? string.Empty : $" ({missingCore.Message})");
        }

        if (error is JsonRpcException rpc)
        {
            return $"KL Core 返回错误：{rpc.Message}";
        }

        if (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return error.Message;
        }

        return error.Message;
    }
}

/// <summary>
/// No-op core client used by tests and by the view-model when no core service is
/// injected. Production always injects <see cref="CoreSessionService"/>.
/// </summary>
public sealed class UnavailableCoreSessionService : ICoreSessionService
{
    public bool IsSessionOpen => false;
    public bool IsWriter => false;
    public string SessionId => string.Empty;
    public string Provider => string.Empty;
    public string Model => string.Empty;
    public string Workspace => string.Empty;
    public long LastSeq => 0;
    public CoreSessionPresenter? Presenter => null;

    public event Action<SessionEvent>? EventReceived
    {
        add { }
        remove { }
    }

    public Task<CoreConnectResult> OpenSessionAsync(
        CoreSessionRequest request,
        Action<string>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(CoreConnectResult.Failed("KL Core 不可用。"));

    public Task CloseSessionAsync() => Task.CompletedTask;

    public Task<JsonElement> HistoryAsync(int limit, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("KL Core 不可用。");

    public Task<CoreCatalogSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CoreCatalogSnapshot.Failed("KL Core 不可用。"));

    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("KL Core 不可用。");

    public Task<TurnOutcome> RunTurnAsync(string prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("KL Core 不可用。");

    public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> RunEphemeralTurnAsync(string workspace, string prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("KL Core 不可用。");

    public bool ResolveApproval(string approvalId, string decision) => false;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

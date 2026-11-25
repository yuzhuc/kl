using System.Text.Json;
using System.Threading.Channels;
using Kl.Client;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.Services;

/// <summary>Options for opening one core session.</summary>
public sealed record CoreSessionRequest
{
    public string Workspace { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Quality { get; init; }
    public string? Agent { get; init; }
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
/// Outcome of restoring the active session after the core connection dropped.
/// <see cref="Replayed"/> means the transport re-attached and replayed events
/// after LastSeq; otherwise the session was reopened and history must reload.
/// </summary>
public sealed record CoreReconnectResult(bool Success, bool Replayed, string Error)
{
    public static CoreReconnectResult Failed(string error) => new(false, false, error);
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
    /// <summary>True when the resolved transport points at another machine.</summary>
    bool IsRemoteCore { get; }
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

    /// <summary>
    /// Restores the session after the connection dropped: re-attach with LastSeq
    /// replay on the local server transport, or reopen the session on stdio.
    /// </summary>
    Task<CoreReconnectResult> ReconnectAsync(CancellationToken cancellationToken);

    /// <summary>Re-tries the write lease for a read-only observer.</summary>
    Task<bool> TryBecomeWriterAsync(CancellationToken cancellationToken);

    /// <summary>Moves a session between the active and archived stores.</summary>
    Task ArchiveAsync(string sessionId, bool archived, CancellationToken cancellationToken);

    /// <summary>Deletes a session (and its attachments) from the session store.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Renames a session in Core.</summary>
    Task RenameSessionAsync(string sessionId, string name, CancellationToken cancellationToken);

    /// <summary>Forks the open session in Core and returns the new session id.</summary>
    Task<string> ForkAsync(CancellationToken cancellationToken);

    /// <summary>Asks Core to compact the open session's context.</summary>
    Task<JsonElement> CompactAsync(CancellationToken cancellationToken);

    /// <summary>Runs <c>kl.doctor</c> against a fresh Core connection.</summary>
    Task<JsonElement> DoctorAsync(CancellationToken cancellationToken);

    /// <summary>Reads <c>mcp.list</c> from the shared Core connection.</summary>
    Task<JsonElement> ListMcpToolsAsync(CancellationToken cancellationToken);

    /// <summary>Reads <c>plugin.list</c> from the shared Core connection.</summary>
    Task<JsonElement> ListPluginsAsync(CancellationToken cancellationToken);

    /// <summary>Enables or disables a Core plugin.</summary>
    Task<JsonElement> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken);

    /// <summary>Installs a plugin directory through Core.</summary>
    Task<JsonElement> InstallPluginAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reloads one installed plugin through Core.</summary>
    Task<JsonElement> ReloadPluginAsync(string id, CancellationToken cancellationToken);

    /// <summary>Writes a provider through the shared Core connection.</summary>
    Task<JsonElement> UpsertProviderAsync(object parameters, CancellationToken cancellationToken);

    /// <summary>Writes a provider and its optional API key over one Core connection.</summary>
    Task<JsonElement> UpsertProviderWithKeyAsync(
        object parameters,
        string id,
        string? apiKey,
        CancellationToken cancellationToken);

    /// <summary>Stores a provider API key through the shared Core connection.</summary>
    Task<JsonElement> SetProviderApiKeyAsync(string id, string key, CancellationToken cancellationToken);

    /// <summary>Enables or disables a provider through the shared Core connection.</summary>
    Task<JsonElement> SetProviderEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);

    /// <summary>Removes a provider from Core config and its stored credential.</summary>
    Task<JsonElement> DeleteProviderAsync(string id, CancellationToken cancellationToken);

    /// <summary>Asks Core to read the provider's model list endpoint.</summary>
    Task<JsonElement> FetchProviderModelsAsync(string id, CancellationToken cancellationToken);

    /// <summary>Reads the redacted <c>config.get</c> payload from a fresh connection.</summary>
    Task<JsonElement> ReadConfigAsync(CancellationToken cancellationToken);

    /// <summary>Reads provider.list and config.get over a single Core connection.</summary>
    Task<CoreProviderCatalog> LoadProviderCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Reads <c>skill.list</c> for one workspace from a fresh connection.</summary>
    Task<JsonElement> ListSkillsAsync(string workspace, CancellationToken cancellationToken);

    /// <summary>Reads a skill's markdown body for the open session.</summary>
    Task<JsonElement> GetSkillBodyAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists the agent presets Core knows about.</summary>
    Task<JsonElement> ListAgentsAsync(CancellationToken cancellationToken);

    /// <summary>Overrides the agent preset and quality mode for subsequent turns.</summary>
    void SetTurnPreferences(string? agent, string? quality);

    /// <summary>Switches the provider/model used by the current or next session's turns.</summary>
    void SetTurnModel(string? provider, string? model);

    /// <summary>Overrides the model's reasoning effort for subsequent turns.</summary>
    void SetTurnReasoningEffort(string? effort);

    /// <summary>Reads the newest audit records from Core.</summary>
    Task<JsonElement> AuditTailAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Reads Core's global reasoning effort (default/low/medium/high).</summary>
    Task<string> GetReasoningEffortAsync(CancellationToken cancellationToken);

    /// <summary>Writes Core's global reasoning effort and returns the applied value.</summary>
    Task<string> SetReasoningEffortAsync(string effort, CancellationToken cancellationToken);

    /// <summary>Reads the full global agent settings from Core.</summary>
    Task<JsonElement> GetAgentConfigAsync(CancellationToken cancellationToken);

    /// <summary>Writes a partial agent-settings patch and returns the applied settings.</summary>
    Task<JsonElement> SetAgentConfigAsync(object parameters, CancellationToken cancellationToken);

    /// <summary>Runs Core's local/model code review for one workspace.</summary>
    Task<JsonElement> RunReviewAsync(
        string workspace,
        string mode,
        string instructions,
        bool fullFiles,
        CancellationToken cancellationToken);

    Task<JsonElement> HistoryAsync(int limit, CancellationToken cancellationToken);

    Task<CoreCatalogSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken);

    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken);

    Task<TurnOutcome> RunTurnAsync(string prompt, CancellationToken cancellationToken, long? rewindFromSeq = null);

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
    private readonly SemaphoreSlim _providerGate = new(1, 1);
    private readonly SemaphoreSlim _agentConfigGate = new(1, 1);
    private readonly KlClientOptions _catalogOptions;
    private KlSessionClient? _session;
    private KlCoreClient? _providerClient;
    private CoreSessionPresenter? _presenter;
    private CoreSessionRequest? _lastRequest;
    private CancellationTokenSource? _pumpCancellation;
    private string? _agent;
    private string? _quality;
    private string? _turnProvider;
    private string? _turnModel;
    private string? _turnReasoningEffort;

    public CoreSessionService(KlClientOptions? options = null)
    {
        _catalogOptions = options ?? new KlClientOptions();
    }

    public bool IsSessionOpen => _session is not null;
    public bool IsWriter => _session?.IsWriter ?? false;
    public bool IsRemoteCore => IsRemoteEndpoint(KlCoreClient.ResolveEndpoint(_catalogOptions));

    /// <summary>
    /// TCP/TLS endpoints on another host run the core elsewhere. Loopback TCP is
    /// treated as local because the workspace paths are on this machine.
    /// </summary>
    public static bool IsRemoteEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme is not "tcp" and not "tls"))
        {
            return false;
        }
        return !uri.IsLoopback && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
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
            return await OpenSessionCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CoreConnectResult> OpenSessionCoreAsync(
        CoreSessionRequest request,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        await CloseSessionCoreAsync().ConfigureAwait(false);

        var presenter = new CoreSessionPresenter();
        var clientOptions = new KlClientOptions
        {
            CorePath = request.CorePath,
            ConfigPath = request.ConfigPath,
            HomePath = request.HomePath,
            Endpoint = _catalogOptions.Endpoint,
            Token = _catalogOptions.Token,
            TlsFingerprint = _catalogOptions.TlsFingerprint
        };
        var agent = string.IsNullOrWhiteSpace(request.Agent) ? _agent : request.Agent;
        var quality = string.IsNullOrWhiteSpace(request.Quality) ? _quality : request.Quality;
        var isNewSession = string.IsNullOrWhiteSpace(request.SessionId);
        var provider = string.IsNullOrWhiteSpace(request.Provider)
            ? (isNewSession ? _turnProvider : null)
            : request.Provider;
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? (isNewSession ? _turnModel : null)
            : request.Model;
        var sessionOptions = new KlSessionOptions
        {
            SessionId = isNewSession ? null : request.SessionId,
            Workspace = string.IsNullOrWhiteSpace(request.Workspace) ? null : request.Workspace,
            Provider = string.IsNullOrWhiteSpace(provider) ? null : provider,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Quality = string.IsNullOrWhiteSpace(quality) ? null : quality,
            Agent = string.IsNullOrWhiteSpace(agent) ? null : agent,
            ReasoningEffort = _turnReasoningEffort
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
            _lastRequest = request;
            _agent = string.IsNullOrWhiteSpace(agent) ? null : agent;
            _quality = string.IsNullOrWhiteSpace(quality) ? null : quality;
            _turnProvider = null;
            _turnModel = null;
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
        _lastRequest = null;

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

    public async Task<CoreReconnectResult> ReconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = _lastRequest;
            if (_session is null || request is null)
            {
                return CoreReconnectResult.Failed(Loc.T("没有可恢复的 KL Core 会话。"));
            }

            try
            {
                await _session.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                return new CoreReconnectResult(true, true, string.Empty);
            }
            catch (NotSupportedException)
            {
                // stdio transport: the core is a child process, so there is no
                // server to re-attach to. Resume the session from disk instead;
                // that drops in-flight events, so the caller reloads history.
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return CoreReconnectResult.Failed(DescribeError(error));
            }

            // A draft session was created with no id; resume the one the core
            // actually opened instead of starting a second session.
            var resume = request with { SessionId = _session.SessionId };
            await CloseSessionCoreAsync().ConfigureAwait(false);
            var result = await OpenSessionCoreAsync(resume, null, cancellationToken)
                .ConfigureAwait(false);
            return result.Success
                ? new CoreReconnectResult(true, false, string.Empty)
                : CoreReconnectResult.Failed(result.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryBecomeWriterAsync(CancellationToken cancellationToken)
    {
        if (_session is not { } session)
        {
            return false;
        }

        return await session.TryTakeWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string> ForkAsync(CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException(Loc.T("KL Core 会话尚未建立。"));
        return session.ForkAsync(cancellationToken);
    }

    public Task<JsonElement> CompactAsync(CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException(Loc.T("KL Core 会话尚未建立。"));
        return session.RequestAsync(
            Protocol.SessionCompact,
            new { session_id = session.SessionId },
            cancellationToken);
    }

    public async Task<JsonElement> DoctorAsync(CancellationToken cancellationToken)
    {
        KlCoreClient? client = null;
        try
        {
            client = await KlCoreClient.ConnectOrStartAsync(_catalogOptions, cancellationToken)
                .ConfigureAwait(false);
            return await client.DoctorAsync(cancellationToken).ConfigureAwait(false);
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
                    // A one-shot probe must never leak into the caller.
                }
            }
        }
    }

    public Task<JsonElement> ListMcpToolsAsync(CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.McpList, null, cancellationToken);

    public Task<JsonElement> ListPluginsAsync(CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.PluginList, null, cancellationToken);

    public Task<JsonElement> SetPluginEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken) =>
        OneShotRequestAsync(
            enabled ? Protocol.PluginEnable : Protocol.PluginDisable,
            new { id = pluginId },
            cancellationToken);

    public Task<JsonElement> InstallPluginAsync(string path, CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.PluginInstall, new { path }, cancellationToken);

    public Task<JsonElement> ReloadPluginAsync(string id, CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.PluginReload, new { id }, cancellationToken);

    public Task<JsonElement> UpsertProviderAsync(object parameters, CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.ProviderUpsert, parameters, cancellationToken);

    public Task<JsonElement> UpsertProviderWithKeyAsync(
        object parameters,
        string id,
        string? apiKey,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => UpsertProviderWithKeyCoreAsync(
                parameters,
                id,
                apiKey,
                cancellationToken),
            cancellationToken);

    private async Task<JsonElement> UpsertProviderWithKeyCoreAsync(
        object parameters,
        string id,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var client = await GetProviderClientAsync(cancellationToken).ConfigureAwait(false);
                var result = await client.RequestAsync(
                        Protocol.ProviderUpsert,
                        parameters,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    await client.RequestAsync(
                            Protocol.ProviderSetApiKey,
                            new { id, key = apiKey },
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return result;
            }
            catch (Exception error) when (IsProviderTransportError(error))
            {
                await ResetProviderClientAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _providerGate.Release();
        }
    }

    public Task<JsonElement> SetProviderApiKeyAsync(
        string id,
        string key,
        CancellationToken cancellationToken) =>
        OneShotRequestAsync(
            Protocol.ProviderSetApiKey,
            new { id, key },
            cancellationToken);

    public Task<JsonElement> SetProviderEnabledAsync(
        string id,
        bool enabled,
        CancellationToken cancellationToken) =>
        OneShotRequestAsync(
            Protocol.ProviderSetEnabled,
            new { id, enabled },
            cancellationToken);

    public Task<JsonElement> DeleteProviderAsync(
        string id,
        CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.ProviderDelete, new { id }, cancellationToken);

    public Task<JsonElement> FetchProviderModelsAsync(
        string id,
        CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.ProviderModels, new { id }, cancellationToken);

    public Task<JsonElement> ReadConfigAsync(CancellationToken cancellationToken) =>
        OneShotRequestAsync("config.get", null, cancellationToken);

    public Task<CoreProviderCatalog> LoadProviderCatalogAsync(
        CancellationToken cancellationToken) =>
        Task.Run(
            () => LoadProviderCatalogCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<CoreProviderCatalog> LoadProviderCatalogCoreAsync(
        CancellationToken cancellationToken)
    {
        await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KlCoreClient client;
            try
            {
                client = await GetProviderClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return CoreProviderCatalog.Failed(DescribeError(error));
            }

            JsonElement providerList = default;
            var providerListRead = false;
            try
            {
                providerList = await client.RequestAsync(Protocol.ProviderList, null, cancellationToken)
                    .ConfigureAwait(false);
                providerListRead = true;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (IsProviderTransportError(error))
                {
                    await ResetProviderClientAsync().ConfigureAwait(false);
                    return CoreProviderCatalog.Failed(DescribeError(error));
                }
                // The config still reveals configured providers when the catalog fails.
            }

            JsonElement config = default;
            var configRead = false;
            try
            {
                config = await client.RequestAsync("config.get", null, cancellationToken)
                    .ConfigureAwait(false);
                configRead = true;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (IsProviderTransportError(error))
                {
                    await ResetProviderClientAsync().ConfigureAwait(false);
                    return CoreProviderCatalog.Failed(DescribeError(error));
                }
                // Provider cards from the live catalog are still useful.
            }

            return new CoreProviderCatalog(
                CoreProtocolParser.ParseProviderCatalog(providerList),
                config)
            {
                ProviderListRead = providerListRead,
                ConfigRead = configRead
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CoreProviderCatalog.Failed(DescribeError(error));
        }
        finally
        {
            _providerGate.Release();
        }
    }

    public Task<JsonElement> ListSkillsAsync(string workspace, CancellationToken cancellationToken) =>
        OneShotRequestAsync("skill.list", new { workspace }, cancellationToken);

    public Task<JsonElement> GetSkillBodyAsync(string name, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException(Loc.T("KL Core 会话尚未建立。"));
        return session.RequestAsync(
            Protocol.SkillGet,
            new { session_id = session.SessionId, name },
            cancellationToken);
    }

    public Task<JsonElement> ListAgentsAsync(CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.AgentList, null, cancellationToken);

    public void SetTurnPreferences(string? agent, string? quality)
    {
        _agent = string.IsNullOrWhiteSpace(agent) ? null : agent;
        _quality = string.IsNullOrWhiteSpace(quality) ? null : quality;
        _session?.SetTurnPreferences(_agent, _quality);
    }

    public void SetTurnModel(string? provider, string? model)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? null : provider;
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model;
        if (_session is { } session)
        {
            session.SetTurnModel(normalizedProvider, normalizedModel);
            _turnProvider = null;
            _turnModel = null;
            return;
        }

        _turnProvider = normalizedProvider;
        _turnModel = normalizedModel;
    }

    public void SetTurnReasoningEffort(string? effort)
    {
        _turnReasoningEffort = string.IsNullOrWhiteSpace(effort) ? null : effort;
        _session?.SetTurnReasoningEffort(_turnReasoningEffort);
    }

    public Task<JsonElement> AuditTailAsync(int limit, CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.AuditTail, new { limit }, cancellationToken);

    /// <summary>Runs one request over the shared provider Core connection.</summary>
    private Task<JsonElement> OneShotRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => OneShotRequestCoreAsync(method, parameters, cancellationToken),
            cancellationToken);

    private async Task<JsonElement> OneShotRequestCoreAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var client = await GetProviderClientAsync(cancellationToken).ConfigureAwait(false);
                return await client.RequestAsync(method, parameters, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (IsProviderTransportError(error))
            {
                await ResetProviderClientAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _providerGate.Release();
        }
    }

    public async Task<string> GetReasoningEffortAsync(CancellationToken cancellationToken)
    {
        var result = await AgentConfigAsync(null, cancellationToken).ConfigureAwait(false);
        return ReadReasoningEffort(result);
    }

    public async Task<string> SetReasoningEffortAsync(string effort, CancellationToken cancellationToken)
    {
        var result = await AgentConfigAsync(effort, cancellationToken).ConfigureAwait(false);
        return ReadReasoningEffort(result);
    }

    public Task<JsonElement> GetAgentConfigAsync(CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.AgentConfigGet, null, cancellationToken);

    public Task<JsonElement> SetAgentConfigAsync(object parameters, CancellationToken cancellationToken) =>
        OneShotRequestAsync(Protocol.AgentConfigSet, parameters, cancellationToken);

    public Task<JsonElement> RunReviewAsync(
        string workspace,
        string mode,
        string instructions,
        bool fullFiles,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["workspace"] = workspace,
            ["mode"] = mode,
            ["strict"] = string.Equals(mode, "strict", StringComparison.OrdinalIgnoreCase),
            ["full_files"] = fullFiles
        };
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            parameters["instructions"] = instructions;
        }
        return OneShotRequestAsync(Protocol.ReviewRun, parameters, cancellationToken);
    }

    private async Task<JsonElement> AgentConfigAsync(string? effort, CancellationToken cancellationToken)
    {
        // Effort clicks arrive faster than the one-shot core starts; run them in
        // order so the last selection wins instead of racing the config file.
        await _agentConfigGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One-shot core launch/connect blocks its thread; keep the UI thread
            // free for the slider animation while the value is written.
            return await Task.Run(() => RunAgentConfigAsync(effort, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _agentConfigGate.Release();
        }
    }

    private async Task<JsonElement> RunAgentConfigAsync(string? effort, CancellationToken cancellationToken)
    {
        KlCoreClient? client = null;
        try
        {
            client = await KlCoreClient.ConnectOrStartAsync(_catalogOptions, cancellationToken)
                .ConfigureAwait(false);
            return effort is null
                ? await client.RequestAsync(Protocol.AgentConfigGet, null, cancellationToken)
                    .ConfigureAwait(false)
                : await client.RequestAsync(
                        Protocol.AgentConfigSet,
                        new { reasoning_effort = effort },
                        cancellationToken)
                    .ConfigureAwait(false);
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
                    // One-shot agent-config traffic.
                }
            }
        }
    }

    private static string ReadReasoningEffort(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("reasoning_effort", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "default"
                : "default";

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
        var session = _session ?? throw new InvalidOperationException(Loc.T("KL Core 会话尚未建立。"));
        return session.HistoryAsync(limit, cancellationToken);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_session is not null && string.Equals(_session.SessionId, sessionId, StringComparison.Ordinal))
        {
            // Core refuses to delete the open session; drop this connection first.
            await CloseSessionAsync().ConfigureAwait(false);
        }

        var parameters = new { session_id = sessionId };
        if (_session is { } session)
        {
            await session.RequestAsync(Protocol.SessionDelete, parameters, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var client = await KlCoreClient
            .ConnectOrStartAsync(_catalogOptions, cancellationToken)
            .ConfigureAwait(false);
        await client.RequestAsync(Protocol.SessionDelete, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RenameSessionAsync(string sessionId, string name, CancellationToken cancellationToken)
    {
        var parameters = new { session_id = sessionId, name };
        if (_session is { } session)
        {
            await session.RequestAsync(Protocol.SessionRename, parameters, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var client = await KlCoreClient
            .ConnectOrStartAsync(_catalogOptions, cancellationToken)
            .ConfigureAwait(false);
        await client.RequestAsync(Protocol.SessionRename, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ArchiveAsync(string sessionId, bool archived, CancellationToken cancellationToken)
    {
        var parameters = new { session_id = sessionId, archived };
        if (_session is { } session)
        {
            await session.RequestAsync(Protocol.SessionArchive, parameters, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var client = await KlCoreClient
            .ConnectOrStartAsync(_catalogOptions, cancellationToken)
            .ConfigureAwait(false);
        await client.RequestAsync(Protocol.SessionArchive, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CoreCatalogSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        KlCoreClient? client = null;
        try
        {
            client = await KlCoreClient.ConnectOrStartAsync(_catalogOptions, cancellationToken)
                .ConfigureAwait(false);
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

            JsonElement config = default;
            try
            {
                config = await client.RequestAsync("config.get", null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Provider cards from the live catalog are still useful.
            }

            return CoreProtocolParser.ParseCatalog(handshake, providerList, sessionList)
                with { Config = config };
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
        var session = _session ?? throw new InvalidOperationException(Loc.T("KL Core 会话尚未建立。"));
        return session.RequestAsync(method, parameters, cancellationToken);
    }

    public async Task<TurnOutcome> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken,
        long? rewindFromSeq = null)
    {
        var session = _session ?? throw new InvalidOperationException(Loc.T("KL Core 会话尚未建立。"));
        return await session.RunTurnAsync(prompt, cancellationToken, rewindFromSeq).ConfigureAwait(false);
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
                    string.IsNullOrWhiteSpace(outcome.Text) ? Loc.T("自动化任务执行失败。") : outcome.Text);
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
        await ResetProviderClientAsync().ConfigureAwait(false);
        _gate.Dispose();
        _providerGate.Dispose();
        _agentConfigGate.Dispose();
    }

    private async Task<KlCoreClient> GetProviderClientAsync(CancellationToken cancellationToken)
    {
        if (_providerClient is { } client)
        {
            return client;
        }

        var created = await KlCoreClient.ConnectOrStartAsync(_catalogOptions, cancellationToken)
            .ConfigureAwait(false);
        _providerClient = created;
        return created;
    }

    private async Task ResetProviderClientAsync()
    {
        var client = _providerClient;
        _providerClient = null;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A dead provider connection must not break shutdown or the next request.
        }
    }

    private static bool IsProviderTransportError(Exception error) =>
        error is not OperationCanceledException and not JsonRpcException;

    private static string DescribeError(Exception error)
    {
        if (error is FileNotFoundException missingCore)
        {
            return Loc.T("未找到 kl-core 可执行文件，请先构建 Core 或设置 KL_CORE_PATH。")
                + (string.IsNullOrWhiteSpace(missingCore.Message) ? string.Empty : $" ({missingCore.Message})");
        }

        if (error is JsonRpcException rpc)
        {
            return Loc.F("KL Core 返回错误：{0}", (rpc.Message));
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
    public bool IsRemoteCore => false;
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
        Task.FromResult(CoreConnectResult.Failed(Loc.T("KL Core 不可用。")));

    public Task CloseSessionAsync() => Task.CompletedTask;

    public Task<CoreReconnectResult> ReconnectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CoreReconnectResult.Failed(Loc.T("KL Core 不可用。")));

    public Task<bool> TryBecomeWriterAsync(CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<string> ForkAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> CompactAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> DoctorAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> ListMcpToolsAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> ListPluginsAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> InstallPluginAsync(string path, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> ReloadPluginAsync(string id, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> UpsertProviderAsync(object parameters, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> UpsertProviderWithKeyAsync(
        object parameters,
        string id,
        string? apiKey,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> SetProviderApiKeyAsync(string id, string key, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> SetProviderEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> DeleteProviderAsync(string id, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> ReadConfigAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<CoreProviderCatalog> LoadProviderCatalogAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> FetchProviderModelsAsync(
        string id,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> ListSkillsAsync(string workspace, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> GetSkillBodyAsync(string name, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> ListAgentsAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public void SetTurnPreferences(string? agent, string? quality)
    {
    }

    public void SetTurnModel(string? provider, string? model)
    {
    }

    public void SetTurnReasoningEffort(string? effort)
    {
    }

    public Task<JsonElement> AuditTailAsync(int limit, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<string> GetReasoningEffortAsync(CancellationToken cancellationToken) =>
        Task.FromResult("default");

    public Task<string> SetReasoningEffortAsync(string effort, CancellationToken cancellationToken) =>
        Task.FromResult(effort);

    public Task<JsonElement> GetAgentConfigAsync(CancellationToken cancellationToken) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            reasoning_effort = "default",
            web_search = true,
            network_access = true,
            output_verbosity = "default",
            reasoning_summary = "auto",
            memory_enabled = true,
            plugins_enabled = true,
            custom_instructions = string.Empty,
            personal_style = "default",
            warmth = "default",
            enthusiasm = "default",
            emoji_usage = "default",
            visual_answers = "default",
            heading_style = "default",
            quick_answers = true
        }));

    public Task<JsonElement> SetAgentConfigAsync(object parameters, CancellationToken cancellationToken) =>
        Task.FromResult(JsonSerializer.SerializeToElement(parameters));

    public Task<JsonElement> RunReviewAsync(
        string workspace,
        string mode,
        string instructions,
        bool fullFiles,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<JsonElement> HistoryAsync(int limit, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task ArchiveAsync(string sessionId, bool archived, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RenameSessionAsync(string sessionId, string name, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<CoreCatalogSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CoreCatalogSnapshot.Failed(Loc.T("KL Core 不可用。")));

    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task<TurnOutcome> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken,
        long? rewindFromSeq = null) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> RunEphemeralTurnAsync(string workspace, string prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Loc.T("KL Core 不可用。"));

    public bool ResolveApproval(string approvalId, string decision) => false;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

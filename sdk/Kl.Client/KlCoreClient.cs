using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kl.Client;

public sealed record HandshakeInfo(
    string ServerName,
    string ServerVersion,
    int ProtocolVersion,
    int MinProtocolVersion);

public sealed class KlCoreClient : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly JsonRpcConnection _rpc;
    private readonly string? _token;

    private KlCoreClient(Process? process, JsonRpcConnection rpc, string? token)
    {
        _process = process;
        _rpc = rpc;
        _token = token;
    }

    public event Action<JsonElement>? NotificationReceived
    {
        add => _rpc.NotificationReceived += value;
        remove => _rpc.NotificationReceived -= value;
    }

    public event Action<string>? Diagnostic
    {
        add => _rpc.Diagnostic += value;
        remove => _rpc.Diagnostic -= value;
    }

    public static KlCoreClient Start(KlClientOptions options)
    {
        var corePath = CoreLocator.Find(options.CorePath);
        var startInfo = new ProcessStartInfo(corePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            startInfo.Environment["KL_CONFIG"] = Path.GetFullPath(options.ConfigPath);
        }

        if (!string.IsNullOrWhiteSpace(options.HomePath))
        {
            startInfo.Environment["KL_HOME"] = Path.GetFullPath(options.HomePath);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start {corePath}");
        return new KlCoreClient(process, new JsonRpcConnection(process), ResolveToken(options));
    }

    /// <summary>Reads the TLS fingerprint from the options, then the environment.</summary>
    public static string? ResolveTlsFingerprint(KlClientOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TlsFingerprint))
        {
            return options.TlsFingerprint.Trim();
        }
        var fromEnvironment = Environment.GetEnvironmentVariable("KL_CORE_TLS_FINGERPRINT");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    /// <summary>Reads the shared secret from the options, then <c>KL_CORE_TOKEN</c>.</summary>
    public static string? ResolveToken(KlClientOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            return options.Token.Trim();
        }
        var fromEnvironment = Environment.GetEnvironmentVariable("KL_CORE_TOKEN");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    /// <summary>
    /// Resolves the local server endpoint: the explicit option wins, then the
    /// server info file under the home directory. Null means no server.
    /// </summary>
    public static string? ResolveEndpoint(KlClientOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return options.Endpoint.Trim();
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("KL_CORE_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        var home = options.HomePath;
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("KL_HOME");
        }
        if (string.IsNullOrWhiteSpace(home))
        {
            var profile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME");
            home = string.IsNullOrWhiteSpace(profile)
                ? Path.Combine(Directory.GetCurrentDirectory(), ".kl")
                : Path.Combine(profile, ".kl");
        }

        var file = Path.Combine(Path.GetFullPath(home), "server.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (!root.TryGetProperty("endpoint", out var endpoint)
                || endpoint.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            // A stale server file must not send the client to a dead endpoint.
            if (root.TryGetProperty("pid", out var pid)
                && pid.ValueKind == JsonValueKind.Number
                && pid.TryGetInt32(out var processId)
                && !IsProcessAlive(processId))
            {
                return null;
            }

            return endpoint.GetString();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Connects to a running local server and completes the handshake.</summary>
    public static async Task<KlCoreClient> ConnectAsync(
        KlClientOptions options,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveEndpoint(options)
            ?? throw new InvalidOperationException("no core endpoint available");
        var stream = LocalSocketConnection.Connect(
            endpoint,
            ResolveTlsFingerprint(options),
            cancellationToken);
        var client = new KlCoreClient(
            null,
            new JsonRpcConnection(stream, stream),
            ResolveToken(options));
        try
        {
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    /// <summary>
    /// Connects to an explicit or discovered core endpoint. Only a stale local
    /// pipe/socket may fall back to a private stdio child; discovered TCP/TLS
    /// endpoints fail visibly so a remote session never silently runs locally.
    /// </summary>
    public static async Task<KlCoreClient> ConnectOrStartAsync(
        KlClientOptions options,
        CancellationToken cancellationToken = default) =>
        (await ConnectOrStartWithModeAsync(options, cancellationToken).ConfigureAwait(false)).Client;

    /// <summary>
    /// Same as <see cref="ConnectOrStartAsync"/> but reports whether the client
    /// connected to a server endpoint (true) or started a stdio child (false).
    /// </summary>
    internal static async Task<(KlCoreClient Client, bool Connected)> ConnectOrStartWithModeAsync(
        KlClientOptions options,
        CancellationToken cancellationToken = default)
    {
        var explicitEndpoint = ResolveExplicitEndpoint(options);
        if (explicitEndpoint is not null)
        {
            return (await ConnectAsync(options, cancellationToken).ConfigureAwait(false), true);
        }

        var discoveredEndpoint = ResolveEndpoint(options);
        if (discoveredEndpoint is not null)
        {
            if (LocalSocketConnection.TryParseTcp(discoveredEndpoint, out _, out _))
            {
                return (await ConnectAsync(options, cancellationToken).ConfigureAwait(false), true);
            }

            try
            {
                return (await ConnectAsync(options, cancellationToken).ConfigureAwait(false), true);
            }
            catch (Exception error) when (error is IOException
                or SocketException
                or TimeoutException
                or InvalidOperationException)
            {
                // A stale local server must not take the CLI down; use stdio.
            }
        }

        var client = Start(options);
        try
        {
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return (client, false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string? ResolveExplicitEndpoint(KlClientOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return options.Endpoint.Trim();
        }
        var fromEnvironment = Environment.GetEnvironmentVariable("KL_CORE_ENDPOINT");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default) =>
        _rpc.RequestAsync(method, parameters, cancellationToken);

    public async Task<HandshakeInfo> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["client_name"] = Protocol.ClientName,
            ["client_version"] = Protocol.ClientVersion,
            ["protocol_versions"] = new[] { Protocol.Version },
            ["features"] = new[] { "events" }
        };
        if (!string.IsNullOrWhiteSpace(_token))
        {
            parameters["token"] = _token;
        }

        var result = await RequestAsync(Protocol.Handshake, parameters, cancellationToken)
            .ConfigureAwait(false);

        var version = result.GetProperty("protocol_version").GetInt32();
        var minimum = result.GetProperty("min_protocol_version").GetInt32();
        if (version != Protocol.Version || minimum > Protocol.Version)
        {
            throw new InvalidOperationException(
                $"incompatible core protocol {version} (client {Protocol.Version})");
        }

        return new HandshakeInfo(
            result.GetProperty("server_name").GetString() ?? Protocol.CoreName,
            result.GetProperty("server_version").GetString() ?? "unknown",
            version,
            minimum);
    }

    public Task<JsonElement> PingAsync(string? nonce = null, CancellationToken cancellationToken = default)
    {
        var parameters = nonce is null
            ? null
            : new Dictionary<string, object?> { ["nonce"] = nonce };
        return RequestAsync(Protocol.Ping, parameters, cancellationToken);
    }

    public Task<JsonElement> DoctorAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.Doctor, null, cancellationToken);

    public Task<JsonElement> AttachSessionAsync(
        string sessionId,
        long? afterSeq = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["session_id"] = sessionId
        };
        if (afterSeq is not null)
        {
            parameters["after_seq"] = afterSeq.Value;
        }
        return RequestAsync(Protocol.SessionAttach, parameters, cancellationToken);
    }

    public Task<JsonElement> LeaseSessionAsync(
        string sessionId,
        long? leaseMs = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["session_id"] = sessionId
        };
        if (leaseMs is not null)
        {
            parameters["lease_ms"] = leaseMs.Value;
        }
        return RequestAsync(Protocol.SessionLease, parameters, cancellationToken);
    }

    public ChannelReader<JsonElement> Notifications => _rpc.Notifications;

    public Task<JsonElement> CreateSessionAsync(
        string workspace,
        string? provider,
        string? model,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["workspace"] = workspace };
        if (!string.IsNullOrWhiteSpace(provider))
        {
            parameters["provider"] = provider;
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            parameters["model"] = model;
        }
        return RequestAsync(Protocol.SessionCreate, parameters, cancellationToken);
    }

    public Task<JsonElement> ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.SessionResume,
            new Dictionary<string, object?> { ["session_id"] = sessionId },
            cancellationToken);

    public Task<JsonElement> SessionHistoryAsync(
        string sessionId,
        int limit,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.SessionHistory,
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["limit"] = limit
            },
            cancellationToken);

    public Task<JsonElement> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.SessionList, null, cancellationToken);

    public Task<JsonElement> RenameSessionAsync(
        string sessionId,
        string name,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.SessionRename,
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["name"] = name
            },
            cancellationToken);

    public Task<JsonElement> ForkSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.SessionFork,
            new Dictionary<string, object?> { ["session_id"] = sessionId },
            cancellationToken);

    public Task<JsonElement> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.SessionDelete,
            new Dictionary<string, object?> { ["session_id"] = sessionId },
            cancellationToken);

    public Task<JsonElement> StartTurnAsync(
        string sessionId,
        string prompt,
        string? provider,
        string? model,
        string? quality,
        string? agent,
        CancellationToken cancellationToken = default,
        long? rewindFromSeq = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["prompt"] = prompt
        };
        if (!string.IsNullOrWhiteSpace(provider))
        {
            parameters["provider"] = provider;
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            parameters["model"] = model;
        }
        if (!string.IsNullOrWhiteSpace(quality))
        {
            parameters["quality"] = quality;
        }
        if (!string.IsNullOrWhiteSpace(agent))
        {
            parameters["agent"] = agent;
        }
        if (rewindFromSeq is { } rewind)
        {
            parameters["rewind_from_seq"] = rewind;
        }
        return RequestAsync(Protocol.TurnStart, parameters, cancellationToken);
    }

    public Task<JsonElement> ReviewAsync(
        string workspace,
        string? baseRef,
        string? mode,
        bool strict,
        string? instructions = null,
        bool fullFiles = false,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["workspace"] = workspace,
            ["strict"] = strict
        };
        if (!string.IsNullOrWhiteSpace(baseRef))
        {
            parameters["base"] = baseRef;
        }
        if (!string.IsNullOrWhiteSpace(mode))
        {
            parameters["mode"] = mode;
        }
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            parameters["instructions"] = instructions;
        }
        if (fullFiles)
        {
            parameters["full_files"] = true;
        }
        return RequestAsync(Protocol.ReviewRun, parameters, cancellationToken);
    }

    public Task<JsonElement> CancelTurnAsync(
        string sessionId,
        string turnId,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.TurnCancel,
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["turn_id"] = turnId
            },
            cancellationToken);

    public Task<JsonElement> RespondApprovalAsync(
        string sessionId,
        string approvalId,
        bool allow,
        CancellationToken cancellationToken = default) =>
        RespondApprovalAsync(
            sessionId,
            approvalId,
            allow ? "allow" : "deny",
            cancellationToken);

    public Task<JsonElement> RespondApprovalAsync(
        string sessionId,
        string approvalId,
        string decision,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.ApprovalRespond,
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["approval_id"] = approvalId,
                ["decision"] = decision
            },
            cancellationToken);

    public Task<JsonElement> ListProvidersAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.ProviderList, null, cancellationToken);

    public Task<JsonElement> UpsertProviderAsync(
        object parameters,
        CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.ProviderUpsert, parameters, cancellationToken);

    public Task<JsonElement> SetProviderApiKeyAsync(
        string id,
        string key,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.ProviderSetApiKey,
            new Dictionary<string, object?> { ["id"] = id, ["key"] = key },
            cancellationToken);

    public Task<JsonElement> SetProviderEnabledAsync(
        string id,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.ProviderSetEnabled,
            new Dictionary<string, object?> { ["id"] = id, ["enabled"] = enabled },
            cancellationToken);

    public Task<JsonElement> SetDefaultModelAsync(
        string provider,
        string model,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.ConfigSetDefaultModel,
            new Dictionary<string, object?> { ["provider"] = provider, ["model"] = model },
            cancellationToken);

    public Task<JsonElement> AuditTailAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.AuditTail,
            new Dictionary<string, object?> { ["limit"] = limit },
            cancellationToken);

    public Task<JsonElement> GetConfigAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.ConfigGet, null, cancellationToken);

    public Task<JsonElement> PluginListAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.PluginList, null, cancellationToken);

    public Task<JsonElement> PluginInstallAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.PluginInstall,
            new Dictionary<string, object?> { ["path"] = path },
            cancellationToken);

    public Task<JsonElement> PluginReloadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.PluginReload,
            new Dictionary<string, object?> { ["id"] = id },
            cancellationToken);

    public Task<JsonElement> PluginEnableAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.PluginEnable,
            new Dictionary<string, object?> { ["id"] = id },
            cancellationToken);

    public Task<JsonElement> PluginDisableAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.PluginDisable,
            new Dictionary<string, object?> { ["id"] = id },
            cancellationToken);

    public Task<JsonElement> ListMemoryAsync(
        string? sessionId = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            parameters["session_id"] = sessionId;
        }
        if (!string.IsNullOrWhiteSpace(scope))
        {
            parameters["scope"] = scope;
        }
        return RequestAsync(Protocol.MemoryList, parameters, cancellationToken);
    }

    public Task<JsonElement> ReadMemoryAsync(
        string name,
        string? sessionId = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            parameters["session_id"] = sessionId;
        }
        if (!string.IsNullOrWhiteSpace(scope))
        {
            parameters["scope"] = scope;
        }
        return RequestAsync(Protocol.MemoryRead, parameters, cancellationToken);
    }

    public Task<JsonElement> WriteMemoryAsync(
        string name,
        string content,
        string? sessionId = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["content"] = content
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            parameters["session_id"] = sessionId;
        }
        if (!string.IsNullOrWhiteSpace(scope))
        {
            parameters["scope"] = scope;
        }
        return RequestAsync(Protocol.MemoryWrite, parameters, cancellationToken);
    }

    public Task<JsonElement> DeleteMemoryAsync(
        string name,
        string? sessionId = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            parameters["session_id"] = sessionId;
        }
        if (!string.IsNullOrWhiteSpace(scope))
        {
            parameters["scope"] = scope;
        }
        return RequestAsync(Protocol.MemoryDelete, parameters, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _rpc.DisposeAsync().ConfigureAwait(false);
}

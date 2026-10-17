using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kl.Cli;

public sealed record HandshakeInfo(
    string ServerName,
    string ServerVersion,
    int ProtocolVersion,
    int MinProtocolVersion);

public sealed class KlCoreClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly JsonRpcConnection _rpc;

    private KlCoreClient(Process process, JsonRpcConnection rpc)
    {
        _process = process;
        _rpc = rpc;
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

    public static KlCoreClient Start(CliOptions options)
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
        return new KlCoreClient(process, new JsonRpcConnection(process));
    }

    public Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default) =>
        _rpc.RequestAsync(method, parameters, cancellationToken);

    public async Task<HandshakeInfo> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            Protocol.Handshake,
            new Dictionary<string, object?>
            {
                ["client_name"] = Protocol.ClientName,
                ["client_version"] = Protocol.ClientVersion,
                ["protocol_versions"] = new[] { Protocol.Version },
                ["features"] = new[] { "events" }
            },
            cancellationToken).ConfigureAwait(false);

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

    public Task<JsonElement> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.SessionList, null, cancellationToken);

    public Task<JsonElement> StartTurnAsync(
        string sessionId,
        string prompt,
        string? provider,
        string? model,
        CancellationToken cancellationToken = default)
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
        return RequestAsync(Protocol.TurnStart, parameters, cancellationToken);
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
        RequestAsync(
            Protocol.ApprovalRespond,
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["approval_id"] = approvalId,
                ["decision"] = allow ? "allow" : "deny"
            },
            cancellationToken);

    public Task<JsonElement> ListProvidersAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.ProviderList, null, cancellationToken);

    public Task<JsonElement> AuditTailAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            Protocol.AuditTail,
            new Dictionary<string, object?> { ["limit"] = limit },
            cancellationToken);

    public Task<JsonElement> GetConfigAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Protocol.ConfigGet, null, cancellationToken);

    public async ValueTask DisposeAsync() => await _rpc.DisposeAsync().ConfigureAwait(false);
}

namespace Kl.Client;

public sealed class KlClientOptions
{
    public string? CorePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? HomePath { get; init; }

    /// <summary>
    /// Local server endpoint. Empty means "discover from server.json"; when
    /// nothing is found the client falls back to a stdio child process.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Shared secret for a TCP core started with <c>--token</c>. Falls back to
    /// the <c>KL_CORE_TOKEN</c> environment variable.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// SHA-256 fingerprint of the TLS server certificate for <c>tls://</c>
    /// endpoints. Falls back to the <c>KL_CORE_TLS_FINGERPRINT</c> variable.
    /// </summary>
    public string? TlsFingerprint { get; init; }
}

public sealed class KlSessionOptions
{
    public string? SessionId { get; init; }
    public bool SkipInitialReplay { get; init; }
    public string? Workspace { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Quality { get; set; }
    public string? Agent { get; set; }
    public bool Verbose { get; init; }
}

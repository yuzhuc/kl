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
}

public sealed class KlSessionOptions
{
    public string? SessionId { get; init; }
    public string? Workspace { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Quality { get; init; }
    public string? Agent { get; set; }
    public bool Verbose { get; init; }
}

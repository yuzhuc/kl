namespace Kl.Client;

public sealed class KlClientOptions
{
    public string? CorePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? HomePath { get; init; }
}

public sealed class KlSessionOptions
{
    public string? SessionId { get; init; }
    public string? Workspace { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Quality { get; init; }
    public bool Verbose { get; init; }
}
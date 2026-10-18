namespace Kl.Cli;

/// <summary>Wire constants shared with the Rust core and the JSON fixtures.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int MinVersion = 1;
    public const string CoreName = "kl-core";
    public const string ClientName = "kl-cli";
    public const string ClientVersion = "0.1.0";

    public const string Handshake = "kl.handshake";
    public const string Ping = "kl.ping";
    public const string Doctor = "kl.doctor";
    public const string SessionCreate = "session.create";
    public const string SessionResume = "session.resume";
    public const string SessionList = "session.list";
    public const string TurnStart = "turn.start";
    public const string TurnCancel = "turn.cancel";
    public const string ApprovalRespond = "tool.approval.respond";
    public const string ProviderList = "provider.list";
    public const string ProviderUpsert = "provider.upsert";
    public const string ProviderSetApiKey = "provider.set_api_key";
    public const string ProviderSetEnabled = "provider.set_enabled";
    public const string ConfigSetDefaultModel = "config.set_default_model";
    public const string AuditTail = "audit.tail";
    public const string ReviewRun = "review.run";
    public const string ConfigGet = "config.get";
    public const string PluginList = "plugin.list";
    public const string PluginInstall = "plugin.install";
    public const string PluginReload = "plugin.reload";
    public const string PluginEnable = "plugin.enable";
    public const string PluginDisable = "plugin.disable";
    public const string Event = "event";
}

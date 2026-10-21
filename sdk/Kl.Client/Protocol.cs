namespace Kl.Client;

/// <summary>Wire constants shared with the Rust core and the JSON fixtures.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int MinVersion = 1;
    public const string CoreName = "kl-core";
    public const string ClientName = "kl-tui";
    public const string ClientVersion = "0.1.0";

    public const string Handshake = "kl.handshake";
    public const string Ping = "kl.ping";
    public const string Doctor = "kl.doctor";
    public const string ServerInfo = "server.info";
    public const string SessionCreate = "session.create";
    public const string SessionResume = "session.resume";
    public const string SessionHistory = "session.history";
    public const string SessionList = "session.list";
    public const string SessionStatus = "session.status";
    public const string SessionRename = "session.rename";
    public const string SessionDelete = "session.delete";
    public const string SessionCompact = "session.compact";
    public const string SessionFork = "session.fork";
    public const string SessionAttach = "session.attach";
    public const string SessionDetach = "session.detach";
    public const string SessionLease = "session.lease";
    public const string TurnStart = "turn.start";
    public const string TurnCancel = "turn.cancel";
    public const string ApprovalRespond = "tool.approval.respond";
    public const string PermissionsGet = "permissions.get";
    public const string PermissionsSet = "permissions.set";
    public const string ProviderList = "provider.list";
    public const string ProviderUpsert = "provider.upsert";
    public const string ProviderSetApiKey = "provider.set_api_key";
    public const string ProviderSetEnabled = "provider.set_enabled";
    public const string ConfigSetDefaultModel = "config.set_default_model";
    public const string AuditTail = "audit.tail";
    public const string ReviewRun = "review.run";
    public const string ConfigGet = "config.get";
    public const string WorkspaceInit = "workspace.init";
    public const string AgentConfigGet = "agent.config.get";
    public const string AgentConfigSet = "agent.config.set";
    public const string AgentList = "agent.list";
    public const string SkillList = "skill.list";
    public const string SkillGet = "skill.get";
    public const string McpList = "mcp.list";
    public const string PluginList = "plugin.list";
    public const string PluginInstall = "plugin.install";
    public const string PluginReload = "plugin.reload";
    public const string PluginEnable = "plugin.enable";
    public const string PluginDisable = "plugin.disable";
    public const string MemoryList = "memory.list";
    public const string MemoryRead = "memory.read";
    public const string MemoryWrite = "memory.write";
    public const string MemoryDelete = "memory.delete";
    public const string Event = "event";
}

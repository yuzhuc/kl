using System.Text.Json;
using Kl.Client;

namespace Kl.Gui.Services;

/// <summary>One session row returned by <c>session.list</c>.</summary>
public sealed record CoreSessionSummary(
    string SessionId,
    string Workspace,
    string Provider,
    string Model,
    string UpdatedAt,
    int MessageCount,
    string? Task,
    string? Prompt,
    string? LastMessage,
    bool Archived);

/// <summary>One transcript entry returned by <c>session.history</c>.</summary>
public sealed record CoreHistoryMessage(
    string Role,
    string Text,
    string? Timestamp,
    long? Seq,
    string? Reasoning,
    string? ToolName,
    bool? ToolOk,
    string? ToolSummary,
    string? ToolOutput,
    string? ToolDiff);

/// <summary>One provider/model pair from <c>provider.list</c>.</summary>
public sealed record CoreProviderProtocols(
    string Provider,
    string ChatUrl,
    string AnthropicUrl,
    string ResponsesUrl)
{
    public IReadOnlyDictionary<string, string> AsMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ChatUrl))
        {
            map["openai-chat"] = ChatUrl;
        }
        if (!string.IsNullOrWhiteSpace(AnthropicUrl))
        {
            map["anthropic"] = AnthropicUrl;
        }
        if (!string.IsNullOrWhiteSpace(ResponsesUrl))
        {
            map["openai-responses"] = ResponsesUrl;
        }
        return map;
    }
}

public sealed record CoreModelOption(string Provider, string Model, bool Enabled, bool KeyConfigured)
{
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public int ContextWindow { get; init; }
    public double CompactThreshold { get; init; }
    public int MaxOutputTokens { get; init; }
    public string ReasoningEffort { get; init; } = "default";
    public IReadOnlyList<string> ReasoningEfforts { get; init; } = Array.Empty<string>();
    public string? Detail { get; init; }
}

/// <summary>Core handshake + provider catalog + session list gathered from one stdio probe.</summary>
public sealed record CoreCatalogSnapshot(
    bool Success,
    string Error,
    string CoreVersion,
    int ProtocolVersion,
    string Provider,
    string Model,
    IReadOnlyList<string> Models,
    bool KeyConfigured,
    IReadOnlyList<CoreSessionSummary> Sessions)
{
    public IReadOnlyList<CoreModelOption> ModelOptions { get; init; } = Array.Empty<CoreModelOption>();

    /// <summary>Per-provider protocol URLs, independent of whether the provider has models.</summary>
    public IReadOnlyList<CoreProviderProtocols> ProviderProtocols { get; init; } =
        Array.Empty<CoreProviderProtocols>();

    /// <summary>Raw config.get result captured on the same probe, when available.</summary>
    public JsonElement Config { get; init; }

    public static CoreCatalogSnapshot Failed(string error) => new(
        false,
        error,
        string.Empty,
        0,
        string.Empty,
        string.Empty,
        Array.Empty<string>(),
        false,
        Array.Empty<CoreSessionSummary>());
}

/// <summary>Provider catalog and full config read over one throwaway Core connection.</summary>
public sealed record CoreProviderCatalog(CoreCatalogSnapshot Snapshot, JsonElement Config)
{
    public bool ProviderListRead { get; init; }
    public bool ConfigRead { get; init; }

    public static CoreProviderCatalog Failed(string error) =>
        new(CoreCatalogSnapshot.Failed(error), default);
}

public static class CoreProtocolParser
{
    public static IReadOnlyList<CoreSessionSummary> ParseSessions(JsonElement result)
    {
        var sessions = new List<CoreSessionSummary>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("sessions", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return sessions;
        }

        foreach (var value in values.EnumerateArray())
        {
            sessions.Add(new CoreSessionSummary(
                JsonRead.GetString(value, "session_id"),
                JsonRead.GetString(value, "workspace"),
                JsonRead.GetString(value, "provider"),
                JsonRead.GetString(value, "model"),
                JsonRead.GetString(value, "updated_at"),
                GetInt(value, "message_count"),
                JsonRead.GetOptionalString(value, "task"),
                JsonRead.GetOptionalString(value, "prompt"),
                JsonRead.GetOptionalString(value, "last_message"),
                value.TryGetProperty("archived", out var archived)
                    && archived.ValueKind is JsonValueKind.True));
        }

        return sessions;
    }

    public static IReadOnlyList<CoreHistoryMessage> ParseHistory(JsonElement result)
    {
        var messages = new List<CoreHistoryMessage>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("messages", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var value in values.EnumerateArray())
        {
            string? toolName = null;
            bool? toolOk = null;
            string? toolSummary = null;
            string? toolOutput = null;
            string? toolDiff = null;
            if (value.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.Object)
            {
                toolName = JsonRead.GetOptionalString(tool, "name");
                toolOk = tool.TryGetProperty("ok", out var ok)
                    && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? ok.GetBoolean()
                        : null;
                toolSummary = JsonRead.GetOptionalString(tool, "summary");
                toolOutput = JsonRead.GetOptionalString(tool, "output");
                toolDiff = JsonRead.GetOptionalString(tool, "diff");
            }

            messages.Add(new CoreHistoryMessage(
                JsonRead.GetString(value, "role"),
                JsonRead.GetString(value, "text"),
                JsonRead.GetOptionalString(value, "timestamp"),
                value.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var parsedSeq)
                    ? parsedSeq
                    : null,
                JsonRead.GetOptionalString(value, "reasoning"),
                toolName,
                toolOk,
                toolSummary,
                toolOutput,
                toolDiff));
        }

        return messages;
    }

    public static CoreCatalogSnapshot ParseCatalog(
        HandshakeInfo handshake,
        JsonElement providerList,
        JsonElement sessionList) =>
        ParseProviderCatalog(providerList) with
        {
            CoreVersion = handshake.ServerVersion,
            ProtocolVersion = handshake.ProtocolVersion,
            Sessions = ParseSessions(sessionList)
        };

    public static CoreCatalogSnapshot ParseProviderCatalog(JsonElement providerList)
    {
        var activeProvider = JsonRead.GetString(providerList, "active_provider");
        var activeModel = JsonRead.GetString(providerList, "active_model");
        var models = new List<string>();
        var options = new List<CoreModelOption>();
        var providerProtocols = new List<CoreProviderProtocols>();
        var keyConfigured = false;
        if (providerList.ValueKind == JsonValueKind.Object
            && providerList.TryGetProperty("providers", out var protocolArray)
            && protocolArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in protocolArray.EnumerateArray())
            {
                var provider = JsonRead.GetString(entry, "provider");
                if (provider.Length == 0
                    || !entry.TryGetProperty("protocols", out var protocols)
                    || protocols.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                providerProtocols.Add(new CoreProviderProtocols(
                    provider,
                    JsonRead.GetString(protocols, "openai-chat"),
                    JsonRead.GetString(protocols, "anthropic"),
                    JsonRead.GetString(protocols, "openai-responses")));
            }
        }

        if (providerList.ValueKind == JsonValueKind.Object
            && providerList.TryGetProperty("models", out var modelArray)
            && modelArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in modelArray.EnumerateArray())
            {
                var provider = JsonRead.GetString(model, "provider");
                var name = JsonRead.GetString(model, "model");
                if (name.Length > 0)
                {
                    models.Add(name);
                    options.Add(new CoreModelOption(
                        provider,
                        name,
                        !model.TryGetProperty("enabled", out var enabled)
                            || enabled.ValueKind == JsonValueKind.True,
                        model.TryGetProperty("key_configured", out var key)
                            && key.ValueKind == JsonValueKind.True)
                    {
                        Name = JsonRead.GetString(model, "name"),
                        Protocol = JsonRead.GetString(model, "protocol"),
                        Url = JsonRead.GetString(model, "url"),
                        ContextWindow = GetInt(model, "context_window"),
                        CompactThreshold = model.TryGetProperty("compact_threshold", out var threshold)
                            && threshold.TryGetDouble(out var parsedThreshold)
                                ? parsedThreshold
                                : 0.8,
                        MaxOutputTokens = GetInt(model, "max_output_tokens"),
                        ReasoningEffort = NormalizeReasoningEffort(
                            JsonRead.GetString(model, "reasoning_effort")),
                        ReasoningEfforts = model.TryGetProperty("reasoning_efforts", out var efforts)
                            && efforts.ValueKind == JsonValueKind.Array
                                ? efforts.EnumerateArray()
                                    .Where(value => value.ValueKind == JsonValueKind.String)
                                    .Select(value => NormalizeReasoningEffort(value.GetString()))
                                    .Where(value => value != "default")
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList()
                                : Array.Empty<string>(),
                        Detail = JsonRead.GetOptionalString(model, "detail")
                    });
                }

                if (!keyConfigured
                    && string.Equals(provider, activeProvider, StringComparison.OrdinalIgnoreCase)
                    && model.TryGetProperty("key_configured", out var configured)
                    && configured.ValueKind is JsonValueKind.True)
                {
                    keyConfigured = true;
                }
            }
        }

        return new CoreCatalogSnapshot(
            true,
            string.Empty,
            string.Empty,
            0,
            activeProvider,
            activeModel,
            models,
            keyConfigured,
            Array.Empty<CoreSessionSummary>())
        {
            ModelOptions = options,
            ProviderProtocols = providerProtocols
        };
    }

    private static string NormalizeReasoningEffort(string? value) => value switch
    {
        "none" => "none",
        "minimal" => "minimal",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "xhigh",
        "max" => "max",
        "ultra" => "ultra",
        _ => "default"
    };

    public static string DeriveTitle(CoreSessionSummary session)
    {
        var candidate = FirstNonEmpty(session.Task, session.Prompt, session.LastMessage);
        if (candidate.Length == 0)
        {
            return "新会话";
        }

        var singleLine = string.Join(" ", candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 40 ? singleLine : singleLine[..39] + "…";
    }

    public static DateTimeOffset ParseTimestamp(string? value, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : fallback;

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
}

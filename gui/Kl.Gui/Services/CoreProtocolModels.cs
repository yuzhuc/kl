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
    string? LastMessage);

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
public sealed record CoreModelOption(string Provider, string Model, bool Enabled, bool KeyConfigured);

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
                GetString(value, "session_id"),
                GetString(value, "workspace"),
                GetString(value, "provider"),
                GetString(value, "model"),
                GetString(value, "updated_at"),
                GetInt(value, "message_count"),
                GetOptionalString(value, "task"),
                GetOptionalString(value, "prompt"),
                GetOptionalString(value, "last_message")));
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
                toolName = GetOptionalString(tool, "name");
                toolOk = tool.TryGetProperty("ok", out var ok)
                    && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? ok.GetBoolean()
                        : null;
                toolSummary = GetOptionalString(tool, "summary");
                toolOutput = GetOptionalString(tool, "output");
                toolDiff = GetOptionalString(tool, "diff");
            }

            messages.Add(new CoreHistoryMessage(
                GetString(value, "role"),
                GetString(value, "text"),
                GetOptionalString(value, "timestamp"),
                value.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var parsedSeq)
                    ? parsedSeq
                    : null,
                GetOptionalString(value, "reasoning"),
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
        JsonElement sessionList)
    {
        var activeProvider = GetString(providerList, "active_provider");
        var activeModel = GetString(providerList, "active_model");
        var models = new List<string>();
        var options = new List<CoreModelOption>();
        var keyConfigured = false;
        if (providerList.ValueKind == JsonValueKind.Object
            && providerList.TryGetProperty("models", out var modelArray)
            && modelArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in modelArray.EnumerateArray())
            {
                var provider = GetString(model, "provider");
                var name = GetString(model, "model");
                if (name.Length > 0)
                {
                    models.Add(name);
                    options.Add(new CoreModelOption(
                        provider,
                        name,
                        !model.TryGetProperty("enabled", out var enabled)
                            || enabled.ValueKind == JsonValueKind.True,
                        model.TryGetProperty("key_configured", out var key)
                            && key.ValueKind == JsonValueKind.True));
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
            handshake.ServerVersion,
            handshake.ProtocolVersion,
            activeProvider,
            activeModel,
            models,
            keyConfigured,
            ParseSessions(sessionList))
        {
            ModelOptions = options
        };
    }

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

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
}

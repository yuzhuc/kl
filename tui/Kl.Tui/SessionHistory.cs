using System.Text.Json;

namespace Kl.Tui;

public sealed record HistoryMessage(
    string Role,
    string Text,
    string Timestamp,
    long? Seq,
    string? ToolName,
    bool? ToolOk,
    string? ToolSummary,
    string? Reasoning = null,
    string? ToolOutput = null,
    string? ToolDiff = null)
{
    public string ToolDisplay()
    {
        var label = string.Join(
            ": ",
            new[] { ToolName, ToolSummary }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrEmpty(label))
        {
            return Text;
        }
        var marker = ToolOk switch
        {
            true => "[ok] ",
            false => "[fail] ",
            _ => string.Empty
        };
        return marker + label;
    }
}

public static class SessionHistory
{
    public static List<HistoryMessage> ParseHistory(JsonElement result)
    {
        var messages = new List<HistoryMessage>();
        if (!result.TryGetProperty("messages", out var values)
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
            if (value.TryGetProperty("tool", out var tool)
                && tool.ValueKind == JsonValueKind.Object)
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

            messages.Add(new HistoryMessage(
                GetString(value, "role"),
                GetString(value, "text"),
                GetString(value, "timestamp"),
                value.TryGetProperty("seq", out var seq)
                    && seq.TryGetInt64(out var parsedSeq)
                    ? parsedSeq
                    : null,
                toolName,
                toolOk,
                toolSummary,
                GetOptionalString(value, "reasoning"),
                toolOutput,
                toolDiff));
        }
        return messages;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

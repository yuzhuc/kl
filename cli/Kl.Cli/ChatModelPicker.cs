using System.Globalization;
using System.Text.Json;

namespace Kl.Cli;

public sealed record ChatModelOption(
    string Provider,
    string Model,
    string Protocol,
    int ContextWindow,
    double CompactThreshold);

public sealed class ChatModelPickerState
{
    public bool IsOpen { get; set; }
    public List<ChatModelOption> Models { get; } = [];
    public int Selection { get; set; }
    public string? Error { get; set; }

    public ChatModelOption? Selected =>
        Models.Count == 0
            ? null
            : Models[Math.Clamp(Selection, 0, Models.Count - 1)];

    public void Open(
        IEnumerable<ChatModelOption> models,
        string? provider,
        string? model)
    {
        Models.Clear();
        Models.AddRange(models);
        Error = null;
        Selection = Math.Max(
            0,
            Models.FindIndex(option =>
                option.Provider == provider && option.Model == model));
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Error = null;
    }

    public void Move(int delta)
    {
        if (Models.Count == 0)
        {
            Selection = 0;
            return;
        }
        Selection = (Selection + delta) % Models.Count;
        if (Selection < 0)
        {
            Selection += Models.Count;
        }
    }
}

public static class ChatModelPicker
{
    public const int MaxVisible = 8;

    public static List<ChatModelOption> Parse(JsonElement result)
    {
        var models = new List<ChatModelOption>();
        if (!result.TryGetProperty("models", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            models.Add(new ChatModelOption(
                GetString(value, "provider"),
                GetString(value, "model"),
                GetString(value, "protocol"),
                value.TryGetProperty("context_window", out var context)
                    && context.TryGetInt32(out var contextWindow)
                    ? contextWindow
                    : 0,
                value.TryGetProperty("compact_threshold", out var threshold)
                    && threshold.TryGetDouble(out var compactThreshold)
                    ? compactThreshold
                    : 0));
        }
        return models;
    }

    public static List<string> Render(
        ChatModelPickerState state,
        int width,
        int maximumLines,
        UiLanguage language)
    {
        var lines = new List<string>();
        if (maximumLines <= 0)
        {
            return lines;
        }

        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            lines.Add(DiffRender.Ansi.Fit("[model] " + state.Error, width));
            return lines;
        }

        if (state.Models.Count == 0)
        {
            lines.Add(DiffRender.Ansi.Fit("[model] " + ChatTexts.NoModels(language), width));
            return lines;
        }

        lines.Add(DiffRender.Ansi.Fit("[model]", width));
        var visible = Math.Min(MaxVisible, Math.Max(1, maximumLines - 1));
        var selected = Math.Clamp(state.Selection, 0, state.Models.Count - 1);
        var start = Math.Clamp(
            selected - visible + 1,
            0,
            Math.Max(0, state.Models.Count - visible));
        var count = Math.Min(visible, state.Models.Count - start);
        for (var index = start; index < start + count; index++)
        {
            var model = state.Models[index];
            var row =
                $"{(index == selected ? "› " : "  ")}{model.Provider}/{model.Model}  " +
                $"protocol={model.Protocol}  " +
                $"context_window={model.ContextWindow.ToString(CultureInfo.InvariantCulture)}  " +
                $"compact_threshold={model.CompactThreshold.ToString("0.###", CultureInfo.InvariantCulture)}";
            var fitted = DiffRender.Ansi.Fit(row, width);
            lines.Add(index == selected
                ? DiffRender.Ansi.Inverse + fitted + DiffRender.Ansi.Reset
                : fitted);
        }
        return lines;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

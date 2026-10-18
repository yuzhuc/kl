using System.Globalization;

namespace Kl.Cli;

public static class SettingsTuiRenderer
{
    public const string Title = "KL Settings";
    public const string FooterHints =
        "↑/↓ move  Tab/Shift+Tab pane  Enter edit/confirm  Esc back  Ctrl+S save";

    public static string Render(int width, int height, SettingsTuiState state)
    {
        width = Math.Max(20, width);
        height = Math.Max(8, height);
        var bodyHeight = Math.Max(1, height - 3);
        var left = RenderGroups(state);
        var (right, selectedLine) = RenderContent(state, width);
        var visible = Window(right, selectedLine, bodyHeight, state);

        var lines = new List<string>
        {
            Title,
            new string('-', width)
        };
        var leftWidth = Math.Clamp(width / 4, 12, 22);
        var rightWidth = Math.Max(1, width - leftWidth - 3);
        for (var row = 0; row < bodyHeight; row++)
        {
            var leftText = row < left.Count ? left[row] : string.Empty;
            var rightText = row < visible.Count ? visible[row] : string.Empty;
            lines.Add(Fit(leftText, leftWidth) + " | " + Fit(rightText, rightWidth));
        }
        lines.Add(FooterHints);
        return string.Join('\n', lines.Take(height).Select(line => Fit(line, width)));
    }

    private static List<string> RenderGroups(SettingsTuiState state)
    {
        var lines = new List<string> { "Groups", string.Empty };
        for (var index = 0; index < state.Groups.Count; index++)
        {
            var marker = state.Pane == SettingsPane.Groups && index == state.GroupIndex
                ? "> "
                : index == state.GroupIndex
                    ? "* "
                    : "  ";
            lines.Add(marker + state.Groups[index]);
        }
        return lines;
    }

    private static (List<string> Lines, int SelectedLine) RenderContent(
        SettingsTuiState state,
        int width)
    {
        if (state.Group != SettingsGroup.Model)
        {
            return (
            [
                state.Group.ToString(),
                "Coming soon."
            ], -1);
        }

        return state.View switch
        {
            SettingsView.ProviderEditor => RenderProviderEditor(state),
            SettingsView.ModelEditor => RenderModelEditor(state),
            _ => RenderProviderList(state)
        };
    }

    private static (List<string> Lines, int SelectedLine) RenderProviderList(
        SettingsTuiState state)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            lines.Add("[error] " + state.Error);
        }
        else if (!string.IsNullOrWhiteSpace(state.Status))
        {
            lines.Add(state.Status);
        }

        lines.Add("Model");
        lines.Add("Providers");
        var selectedLine = -1;
        if (state.Snapshot.Providers.Count == 0)
        {
            lines.Add("  No providers. Press n to add one.");
        }
        for (var index = 0; index < state.Snapshot.Providers.Count; index++)
        {
            var provider = state.Snapshot.Providers[index];
            var selected = state.Pane == SettingsPane.Content && state.ListRow == index;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            var active = provider.Id == state.Snapshot.ActiveProvider ? "*" : " ";
            var key = provider.KeyConfigured switch
            {
                true => "yes",
                false => "no",
                _ => "?"
            };
            lines.Add(
                $"{(selected ? ">" : " ")} {active} {provider.Id}  " +
                $"{(provider.Enabled ? "enabled" : "disabled")}  key={key}  " +
                $"{provider.ModelCount} models  [{provider.ProtocolSummary}]");
        }

        lines.Add("Models");
        if (state.Snapshot.Models.Count == 0)
        {
            lines.Add("  No visible models. Enable a provider or add one.");
        }
        for (var index = 0; index < state.Snapshot.Models.Count; index++)
        {
            var model = state.Snapshot.Models[index];
            var row = state.Snapshot.Providers.Count + index;
            var selected = state.Pane == SettingsPane.Content && state.ListRow == row;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            var active = model.Provider == state.Snapshot.ActiveProvider
                && model.Id == state.Snapshot.ActiveModel
                ? "*"
                : " ";
            lines.Add(
                $"{(selected ? ">" : " ")} {active} {model.Provider}/{model.Id} " +
                $"[{model.Protocol}] ctx={model.ContextWindow} " +
                $"compact={model.CompactThreshold.ToString("0.###", CultureInfo.InvariantCulture)} " +
                $"out={model.MaxOutputTokens}");
        }

        lines.Add(string.Empty);
        lines.Add(
            "Enter edit / set default  n new  e edit  a add model  Space enable/disable");
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderProviderEditor(
        SettingsTuiState state)
    {
        var draft = state.Draft;
        if (draft is null)
        {
            return (["No provider selected."], -1);
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            lines.Add("[error] " + state.Error);
        }
        else if (!string.IsNullOrWhiteSpace(state.Status))
        {
            lines.Add(state.Status);
        }
        lines.Add(draft.IsNew ? "Provider: new" : $"Provider: {draft.OriginalId} (editing)");

        var selectedLine = -1;
        var rows = state.ProviderEditorRows;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var selected = state.Pane == SettingsPane.Content
                && index == state.ProviderEditorRowIndex;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            var marker = selected ? "> " : "  ";
            var text = row.Kind switch
            {
                ProviderRowKind.Id => draft.IsNew
                    ? "Id: " + Field(draft.Id, selected)
                    : "Id: " + draft.OriginalId + " (fixed)",
                ProviderRowKind.Enabled => "Enabled: [" + (draft.Enabled ? "yes" : "no") + "]",
                ProviderRowKind.Mode => "Address mode: [" +
                    (draft.AddressMode == SettingsAddressMode.Base ? "base" : "full") + "]",
                ProviderRowKind.BaseUrl => "Base URL: " + Field(draft.BaseUrl, selected),
                ProviderRowKind.ChatUrl => "openai-chat URL: " + Field(draft.ChatUrl, selected),
                ProviderRowKind.AnthropicUrl => "anthropic URL: " + Field(draft.AnthropicUrl, selected),
                ProviderRowKind.ResponsesUrl => "openai-responses URL: " + Field(draft.ResponsesUrl, selected),
                ProviderRowKind.ApiKey => "API key: " + draft.MaskedKey(selected),
                ProviderRowKind.ModelsHeader => $"Models ({draft.Models.Count})",
                ProviderRowKind.Model => ModelText(draft.Models[row.ModelIndex]),
                ProviderRowKind.AddModel => "+ Add model (a)",
                _ => string.Empty
            };
            lines.Add(marker + text);
            if (row.Kind == ProviderRowKind.BaseUrl
                && draft.AddressMode == SettingsAddressMode.Base)
            {
                var (chat, anthropic, responses) =
                    SettingsUrls.ExpandBase(draft.BaseUrl.Value);
                lines.Add("    openai-chat -> " + chat);
                lines.Add("    anthropic -> " + anthropic);
                lines.Add("    openai-responses -> " + responses);
            }
        }

        lines.Add(string.Empty);
        lines.Add("Ctrl+S saves provider, models and any new API key.");
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderModelEditor(
        SettingsTuiState state)
    {
        var draft = state.ModelDraft;
        if (draft is null)
        {
            return (["No model selected."], -1);
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            lines.Add("[error] " + state.Error);
        }
        else if (!string.IsNullOrWhiteSpace(state.Status))
        {
            lines.Add(state.Status);
        }
        lines.Add("Model: " + (string.IsNullOrWhiteSpace(draft.Id.Value)
            ? "new"
            : draft.Id.Value));
        var selectedLine = -1;
        var rows = new[]
        {
            (ModelRowKind.Id, "Id: " + Field(
                draft.Id,
                state.Pane == SettingsPane.Content && state.ModelEditorRowIndex == 0)),
            (ModelRowKind.Protocol, "Protocol: [" + draft.Protocol + "]"),
            (ModelRowKind.ContextWindow,
                "Context window: " + Field(
                    draft.ContextWindow,
                    state.Pane == SettingsPane.Content && state.ModelEditorRowIndex == 2)),
            (ModelRowKind.CompactThreshold,
                "Compact threshold: " + Field(
                    draft.CompactThreshold,
                    state.Pane == SettingsPane.Content && state.ModelEditorRowIndex == 3)),
            (ModelRowKind.MaxOutputTokens,
                "Max output tokens: " + Field(
                    draft.MaxOutputTokens,
                    state.Pane == SettingsPane.Content && state.ModelEditorRowIndex == 4))
        };
        for (var index = 0; index < rows.Length; index++)
        {
            var selected = state.Pane == SettingsPane.Content
                && state.ModelEditorRowIndex == index;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            lines.Add((selected ? "> " : "  ") + rows[index].Item2);
        }

        lines.Add(string.Empty);
        lines.Add("Enter cycles protocol. Ctrl+S confirms this model.");
        return (lines, selectedLine);
    }

    private static string ModelText(SettingsModelDraft draft) =>
        string.IsNullOrWhiteSpace(draft.Id.Value)
            ? "(new model)"
            : $"{draft.Id.Value} [{draft.Protocol}]";

    private static string Field(SettingsText field, bool focused)
    {
        if (!focused)
        {
            return field.Value;
        }
        var cursor = Math.Clamp(field.Cursor, 0, field.Value.Length);
        return field.Value.Insert(cursor, "|");
    }

    private static List<string> Window(
        List<string> lines,
        int selectedLine,
        int height,
        SettingsTuiState state)
    {
        if (height <= 0 || lines.Count <= height)
        {
            state.RightScroll = 0;
            return lines;
        }
        var maximum = Math.Max(0, lines.Count - height);
        var start = Math.Clamp(state.RightScroll, 0, maximum);
        if (selectedLine >= 0)
        {
            if (selectedLine < start)
            {
                start = selectedLine;
            }
            else if (selectedLine >= start + height)
            {
                start = selectedLine - height + 1;
            }
        }
        state.RightScroll = Math.Clamp(start, 0, maximum);
        return lines.Skip(state.RightScroll).Take(height).ToList();
    }

    private static string Fit(string text, int width)
    {
        text ??= string.Empty;
        if (text.Length > width)
        {
            return text[..width];
        }
        return text.PadRight(width);
    }
}

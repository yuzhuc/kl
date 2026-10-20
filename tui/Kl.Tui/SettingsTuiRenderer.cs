using System.Globalization;

namespace Kl.Tui;

public static class SettingsTuiRenderer
{
    public const string Title = "KL Settings";
    public const string FooterHints =
        "↑/↓ move  Tab/Shift+Tab pane  Enter edit/confirm  Esc back  Ctrl+S save  F2 save & exit";
    public const string PluginFooterHints =
        "↑/↓ move  Tab/Shift+Tab pane  e enable  d disable  r reload  Enter confirm  Esc back  F2 save & exit";

    public static string Render(int width, int height, SettingsTuiState state)
    {
        width = Math.Max(20, width);
        height = Math.Max(8, height);
        var bodyHeight = Math.Max(1, height - 3);
        var leftWidth = Math.Clamp(width / 4, 12, 22);
        var rightWidth = Math.Max(1, width - leftWidth - 3);
        var left = RenderGroups(state);
        var (right, selectedLine) = RenderContent(state);
        var visible = Window(right, selectedLine, bodyHeight, rightWidth, state);

        var lines = new List<string>
        {
            SettingsTexts.Title(state.Language),
            new string('-', width)
        };
        for (var row = 0; row < bodyHeight; row++)
        {
            var leftText = row < left.Count ? left[row] : string.Empty;
            var rightText = row < visible.Count ? visible[row] : string.Empty;
            lines.Add(Fit(leftText, leftWidth) + " | " + Fit(rightText, rightWidth));
        }
        lines.Add(Footer(state));
        return string.Join('\n', lines.Take(height).Select(line => Fit(line, width)));
    }

    private static string Footer(SettingsTuiState state) => state.Group switch
    {
        SettingsGroup.Plugins => SettingsTexts.PluginFooter(state.Language),
        SettingsGroup.Config => SettingsTexts.ConfigFooter(state.Language),
        SettingsGroup.Agent => state.View == SettingsView.AgentMenu
            ? SettingsTexts.AgentMenuFooter(state.Language)
            : SettingsTexts.AgentEditFooter(state.Language),
        SettingsGroup.Model when state.View == SettingsView.ModelPicker =>
            SettingsTexts.ModelPickerFooter(state.Language),
        _ => SettingsTexts.Footer(state.Language)
    };

    private static List<string> RenderGroups(SettingsTuiState state)
    {
        var lines = new List<string>
        {
            SettingsTexts.Groups(state.Language),
            string.Empty
        };
        for (var index = 0; index < state.Groups.Count; index++)
        {
            var marker = state.Pane == SettingsPane.Groups && index == state.GroupIndex
                ? "> "
                : index == state.GroupIndex
                    ? "* "
                    : "  ";
            lines.Add(marker + SettingsTexts.Group(state.Groups[index], state.Language));
        }
        return lines;
    }

    private static (List<string> Lines, int SelectedLine) RenderContent(
        SettingsTuiState state)
    {
        if (state.Group == SettingsGroup.Plugins)
        {
            return RenderPluginList(state);
        }
        if (state.Group == SettingsGroup.Config)
        {
            return RenderConfig(state);
        }
        if (state.Group == SettingsGroup.Agent)
        {
            return RenderAgent(state);
        }
        if (state.Group == SettingsGroup.Skills)
        {
            return RenderSkills(state);
        }
        if (state.Group != SettingsGroup.Model)
        {
            return ([SettingsTexts.Group(state.Group, state.Language)], -1);
        }

        return state.View switch
        {
            SettingsView.ProviderEditor => RenderProviderEditor(state),
            SettingsView.ModelEditor => RenderModelEditor(state),
            SettingsView.ModelPicker => RenderModelPicker(state),
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

        lines.Add(SettingsTexts.T(state.Language, "Model", "模型"));
        lines.Add(SettingsTexts.T(state.Language, "Providers", "供应商"));
        var selectedLine = -1;
        if (state.Snapshot.Providers.Count == 0)
        {
            lines.Add(SettingsTexts.T(
                state.Language,
                "  No providers. Press n to add one.",
                "  暂无供应商。按 n 新增。"));
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
                $"{(provider.Enabled
                    ? SettingsTexts.T(state.Language, "enabled", "已启用")
                    : SettingsTexts.T(state.Language, "disabled", "已禁用"))}  " +
                $"key={key}  " +
                $"{provider.ModelCount} " +
                $"{SettingsTexts.T(state.Language, "models", "模型")}  " +
                $"[{provider.ProtocolSummary}]");
        }

        lines.Add(SettingsTexts.T(state.Language, "Models", "模型"));
        if (state.Snapshot.Models.Count == 0)
        {
            lines.Add(SettingsTexts.T(
                state.Language,
                "  No visible models. Enable a provider or add one.",
                "  暂无可见模型。启用或新增供应商。"));
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
        lines.Add(SettingsTexts.T(
            state.Language,
            "Enter edit / set default  q quick select  n new  e edit  a add model  Space enable/disable",
            "Enter 编辑/设为默认  q 快速选择  n 新增  e 编辑  a 添加模型  空格 启用/禁用"));
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderConfig(
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

        var language = SettingsTexts.T(state.Language, "Language", "语言");
        lines.Add(SettingsTexts.T(state.Language, "Config", "配置"));
        lines.Add(language);
        var selectedLine = state.Pane == SettingsPane.Content ? lines.Count : -1;
        lines.Add(
            $"{(state.Pane == SettingsPane.Content ? "> " : "  ")}" +
            $"{language}: [English] [简体中文]");
        lines.Add(string.Empty);
        var readOnly = SettingsTexts.ReadOnly(state.Language);
        lines.Add($"{SettingsTexts.ConfigPath(state.Language)} ({readOnly}): " +
            $"{(state.Snapshot.Paths.ConfigPath.Length == 0 ? "-" : state.Snapshot.Paths.ConfigPath)}");
        lines.Add($"{SettingsTexts.UiConfigPath(state.Language)} ({readOnly}): " +
            $"{(state.Snapshot.Paths.UiPath.Length == 0 ? "-" : state.Snapshot.Paths.UiPath)}");
        lines.Add($"{SettingsTexts.KlHome(state.Language)} ({readOnly}): " +
            $"{(state.Snapshot.Paths.HomePath.Length == 0 ? "-" : state.Snapshot.Paths.HomePath)}");
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderAgent(
        SettingsTuiState state) => state.View switch
        {
            SettingsView.Permissions => RenderPermissions(state),
            SettingsView.Reasoning => RenderReasoning(state),
            _ => RenderAgentMenu(state)
        };

    private static (List<string> Lines, int SelectedLine) RenderAgentMenu(
        SettingsTuiState state)
    {
        var lines = new List<string>();
        AddStatus(lines, state);
        lines.Add(SettingsTexts.AgentMenu(state.Language));
        lines.Add(SettingsTexts.AgentMenuIntro(state.Language));
        lines.Add(string.Empty);
        var selectedLine = -1;
        var pages = new[]
        {
            (SettingsTexts.Permissions(state.Language),
                SettingsTexts.T(state.Language, "Approval mode and sandbox", "批准模式与沙箱")),
            (SettingsTexts.Reasoning(state.Language),
                SettingsTexts.T(state.Language, "Reasoning effort support", "推理强度支持"))
        };
        for (var index = 0; index < pages.Length; index++)
        {
            var selected = state.Pane == SettingsPane.Content && state.ListRow == index;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            lines.Add(
                $"{(selected ? "> " : "  ")}{pages[index].Item1}  -  {pages[index].Item2}");
        }
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderPermissions(
        SettingsTuiState state)
    {
        var lines = new List<string>();
        AddStatus(lines, state);
        AddAgentReadError(lines, state);
        lines.Add(SettingsTexts.Permissions(state.Language));
        lines.Add(SettingsTexts.PermissionsIntro(state.Language));
        lines.Add(SettingsTexts.ApprovalMode(state.Language));

        var selectedLine = -1;
        for (var index = 0; index < SettingsAgentValues.ApprovalModes.Count; index++)
        {
            var mode = SettingsAgentValues.ApprovalModes[index];
            var selected = state.Pane == SettingsPane.Content && state.ListRow == index;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            var current = mode == state.CurrentApprovalMode ? "*" : " ";
            lines.Add(
                $"{(selected ? "> " : "  ")}{current} {ApprovalModeText(mode, state.Language)}");
        }

        lines.Add(string.Empty);
        lines.Add($"{SettingsTexts.SandboxBackend(state.Language)}: " +
            $"{(state.Snapshot.AgentConfig.SandboxBackend.Length == 0
                ? SettingsTexts.Unknown(state.Language)
                : state.Snapshot.AgentConfig.SandboxBackend)}");
        lines.Add($"{SettingsTexts.SandboxStrict(state.Language)}: " +
            $"{YesNoUnknown(state.Snapshot.AgentConfig.SandboxStrict, state.Language)}");
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderReasoning(
        SettingsTuiState state)
    {
        var lines = new List<string>();
        AddStatus(lines, state);
        AddAgentReadError(lines, state);
        lines.Add(SettingsTexts.Reasoning(state.Language));
        lines.Add(SettingsTexts.ReasoningIntro(state.Language));
        var provider = state.Snapshot.AgentConfig.Provider.Length == 0
            ? state.Snapshot.ActiveProvider
            : state.Snapshot.AgentConfig.Provider;
        var model = state.Snapshot.AgentConfig.Model.Length == 0
            ? state.Snapshot.ActiveModel
            : state.Snapshot.AgentConfig.Model;
        lines.Add($"{SettingsTexts.T(state.Language, "Provider/model", "供应商/模型")}: " +
            $"{(provider.Length == 0 ? "-" : provider)}/" +
            $"{(model.Length == 0 ? "-" : model)}");
        lines.Add($"{SettingsTexts.ReasoningSupport(state.Language)}: " +
            $"{SupportText(state.Snapshot.AgentConfig.ReasoningSupported, state.Language)}");
        lines.Add(SettingsTexts.ReasoningEffort(state.Language));

        var selectedLine = -1;
        for (var index = 0; index < SettingsAgentValues.ReasoningEfforts.Count; index++)
        {
            var effort = SettingsAgentValues.ReasoningEfforts[index];
            var selected = state.Pane == SettingsPane.Content && state.ListRow == index;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            var current = effort == state.CurrentReasoningEffort ? "*" : " ";
            lines.Add(
                $"{(selected ? "> " : "  ")}{current} {EffortText(effort, state.Language)}");
        }
        return (lines, selectedLine);
    }

    private static void AddStatus(List<string> lines, SettingsTuiState state)
    {
        if (state.Notice == SettingsNotice.PermissionsSaved)
        {
            lines.Add(SettingsTexts.T(state.Language,
                "permissions saved",
                "权限已保存"));
        }
        else if (state.Notice == SettingsNotice.ReasoningSaved)
        {
            lines.Add(SettingsTexts.T(state.Language,
                "reasoning saved",
                "推理设置已保存"));
        }
        else if (!string.IsNullOrWhiteSpace(state.Error))
        {
            lines.Add("[error] " + state.Error);
        }
        else if (!string.IsNullOrWhiteSpace(state.Status))
        {
            lines.Add(state.Status);
        }
    }

    private static void AddAgentReadError(List<string> lines, SettingsTuiState state)
    {
        if (state.Notice == SettingsNotice.None
            && string.IsNullOrWhiteSpace(state.Error)
            && !string.IsNullOrWhiteSpace(state.Snapshot.AgentConfig.ReadError))
        {
            lines.Add("[error] " + state.Snapshot.AgentConfig.ReadError);
        }
    }

    private static string ApprovalModeText(
        SettingsApprovalMode mode,
        UiLanguage language) => mode switch
        {
            SettingsApprovalMode.Ask => SettingsTexts.T(language,
                "ask - approve before changes",
                "ask - 修改前询问"),
            SettingsApprovalMode.Auto => SettingsTexts.T(language,
                "auto - approve automatically",
                "auto - 自动批准"),
            SettingsApprovalMode.ReadOnly => SettingsTexts.T(language,
                "read-only - no changes",
                "read-only - 只读"),
            _ => mode.ToString()
        };

    private static string EffortText(
        SettingsReasoningEffort effort,
        UiLanguage language) => effort switch
        {
            SettingsReasoningEffort.Default => SettingsTexts.T(language,
                "default - provider decides",
                "default - 供应商决定"),
            SettingsReasoningEffort.Low => SettingsTexts.T(language,
                "low - fastest",
                "low - 最快"),
            SettingsReasoningEffort.Medium => SettingsTexts.T(language,
                "medium - balanced",
                "medium - 均衡"),
            SettingsReasoningEffort.High => SettingsTexts.T(language,
                "high - deepest",
                "high - 最深"),
            _ => effort.ToString()
        };

    private static string SupportText(bool? supported, UiLanguage language) =>
        supported switch
        {
            true => SettingsTexts.Supported(language),
            false => SettingsTexts.Unsupported(language),
            _ => SettingsTexts.Unknown(language)
        };

    private static string YesNoUnknown(bool? value, UiLanguage language) =>
        value switch
        {
            true => SettingsTexts.T(language, "yes", "是"),
            false => SettingsTexts.T(language, "no", "否"),
            _ => SettingsTexts.Unknown(language)
        };

    private static (List<string> Lines, int SelectedLine) RenderModelPicker(
        SettingsTuiState state)
    {
        var lines = new List<string>();
        AddStatus(lines, state);
        lines.Add(SettingsTexts.QuickSelect(state.Language));
        lines.Add(SettingsTexts.QuickSelectIntro(state.Language));
        var models = state.QuickModels;
        var selectedLine = -1;
        if (models.Count == 0)
        {
            lines.Add(SettingsTexts.T(state.Language,
                "  No enabled-provider models available.",
                "  暂无已启用供应商的模型。"));
            return (lines, selectedLine);
        }

        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            var selected = state.Pane == SettingsPane.Content && state.ListRow == index;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            var active = model.Provider == state.Snapshot.ActiveProvider
                && model.Id == state.Snapshot.ActiveModel
                ? "*"
                : " ";
            lines.Add(
                $"{(selected ? "> " : "  ")}{active} {model.Provider}/{model.Id} " +
                $"[{model.Protocol}]");
            lines.Add($"    context_window={model.ContextWindow}");
            lines.Add(
                $"    compact_threshold={model.CompactThreshold.ToString("0.###", CultureInfo.InvariantCulture)}");
            lines.Add($"    max_output_tokens={model.MaxOutputTokens}");
        }
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderSkills(
        SettingsTuiState state)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Snapshot.SkillsError))
        {
            lines.Add(SettingsTexts.SkillsError(
                state.Language,
                state.Snapshot.SkillsError));
        }
        lines.Add(SettingsTexts.SkillsTitle(state.Language));
        if (state.Snapshot.Skills.Count == 0)
        {
            lines.Add(SettingsTexts.SkillsEmpty(state.Language));
            return (lines, -1);
        }
        foreach (var skill in state.Snapshot.Skills)
        {
            lines.Add("  " + skill.Name + "  " + skill.Description);
        }
        return (lines, -1);
    }

    private static (List<string> Lines, int SelectedLine) RenderPluginList(
        SettingsTuiState state)
    {
        var lines = new List<string>();
        if (state.IsLoading)
        {
            lines.Add(SettingsTexts.T(state.Language, "Loading plugins...", "正在加载插件..."));
            return (lines, -1);
        }
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            lines.Add("[error] " + state.Error);
        }
        else if (!string.IsNullOrWhiteSpace(state.Status))
        {
            lines.Add(state.Status);
        }

        lines.Add(SettingsTexts.T(state.Language, "Plugins", "插件"));
        if (state.Snapshot.Plugins.Count == 0)
        {
            lines.Add(SettingsTexts.T(
                state.Language,
                "  No plugins installed. Use `kl plugin install <path>`.",
                "  未安装插件。使用 `kl plugin install <path>`。"));
        }

        var selectedLine = -1;
        for (var index = 0; index < state.Snapshot.Plugins.Count; index++)
        {
            var plugin = state.Snapshot.Plugins[index];
            var selected = state.Pane == SettingsPane.Content
                && state.ListRow == index;
            if (selected)
            {
                selectedLine = lines.Count;
            }
            var detail = OneLine(plugin.Detail);
            lines.Add(
                $"{(selected ? ">" : " ")} {plugin.Id}  v{plugin.Version}  " +
                $"{plugin.Kind}  " +
                $"{(plugin.Enabled
                    ? SettingsTexts.T(state.Language, "enabled", "已启用")
                    : SettingsTexts.T(state.Language, "disabled", "已禁用"))}  " +
                $"{(plugin.Healthy
                    ? SettingsTexts.T(state.Language, "healthy", "健康")
                    : SettingsTexts.T(state.Language, "unhealthy", "异常"))}  " +
                $"{(detail.Length == 0 ? "-" : detail)}");
        }

        if (state.SelectedPlugin is { } selectedPlugin)
        {
            var detail = OneLine(selectedPlugin.Detail);
            lines.Add(string.Empty);
            lines.Add(SettingsTexts.T(state.Language, "Selected plugin", "已选插件"));
            lines.Add("  " + SettingsTexts.T(state.Language, "Id", "ID") + ": " + selectedPlugin.Id);
            lines.Add("  " + SettingsTexts.T(state.Language, "Version", "版本") + ": " + selectedPlugin.Version);
            lines.Add("  " + SettingsTexts.T(state.Language, "Kind", "类型") + ": " + selectedPlugin.Kind);
            lines.Add("  " + SettingsTexts.T(state.Language, "Enabled", "启用") + ": " + (selectedPlugin.Enabled ? "yes" : "no"));
            lines.Add("  " + SettingsTexts.T(state.Language, "Healthy", "健康") + ": " + (selectedPlugin.Healthy ? "yes" : "no"));
            lines.Add("  " + SettingsTexts.T(state.Language, "Tool", "工具") + ": " + (selectedPlugin.ToolName ?? "-"));
            lines.Add(
                "  " + SettingsTexts.T(state.Language, "Models", "模型") + ": " + (selectedPlugin.Models.Count == 0
                    ? "-"
                    : string.Join(", ", selectedPlugin.Models)));
            lines.Add("  " + SettingsTexts.T(state.Language, "Detail", "详情") + ": " + (detail.Length == 0 ? "-" : detail));
            lines.Add(
                "  " + SettingsTexts.T(state.Language, "Recent error", "最近错误") + ": " + (selectedPlugin.Healthy
                    ? "none"
                    : detail.Length == 0
                        ? "plugin reported unhealthy"
                        : detail));
        }

        lines.Add(string.Empty);
        lines.Add(SettingsTexts.T(
            state.Language,
            "e enable  d disable  r reload  Space toggle  Enter confirm",
            "e 启用  d 禁用  r 重载  空格 切换  Enter 确认"));
        lines.Add(SettingsTexts.T(
            state.Language,
            "Install remains: kl plugin install <path>",
            "安装仍使用：kl plugin install <path>"));
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderProviderEditor(
        SettingsTuiState state)
    {
        var draft = state.Draft;
        if (draft is null)
        {
            return ([SettingsTexts.T(state.Language, "No provider selected.", "未选择供应商。")], -1);
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
        lines.Add(draft.IsNew
            ? SettingsTexts.T(state.Language, "Provider: new", "供应商：新建")
            : $"{SettingsTexts.T(state.Language, "Provider", "供应商")}: {draft.OriginalId} ({SettingsTexts.T(state.Language, "editing", "编辑中")})");

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
                    ? SettingsTexts.T(state.Language, "Id", "ID") + ": " + Field(draft.Id, selected)
                    : SettingsTexts.T(state.Language, "Id", "ID") + ": " + draft.OriginalId + " " + SettingsTexts.T(state.Language, "(fixed)", "（固定）"),
                ProviderRowKind.Enabled =>
                    SettingsTexts.T(state.Language, "Enabled", "启用") + ": [" +
                    (draft.Enabled
                        ? SettingsTexts.T(state.Language, "yes", "是")
                        : SettingsTexts.T(state.Language, "no", "否")) + "]",
                ProviderRowKind.Mode =>
                    SettingsTexts.T(state.Language, "Address mode", "地址模式") + ": [" +
                    (draft.AddressMode == SettingsAddressMode.Base
                        ? SettingsTexts.T(state.Language, "base", "基础地址")
                        : SettingsTexts.T(state.Language, "full", "完整地址")) + "]",
                ProviderRowKind.BaseUrl =>
                    SettingsTexts.T(state.Language, "Base URL", "基础地址") + ": " +
                    Field(draft.BaseUrl, selected),
                ProviderRowKind.ChatUrl => "openai-chat URL: " + Field(draft.ChatUrl, selected),
                ProviderRowKind.AnthropicUrl => "anthropic URL: " + Field(draft.AnthropicUrl, selected),
                ProviderRowKind.ResponsesUrl => "openai-responses URL: " + Field(draft.ResponsesUrl, selected),
                ProviderRowKind.ApiKey =>
                    SettingsTexts.T(state.Language, "API key", "API Key") + ": " +
                    draft.MaskedKey(selected),
                ProviderRowKind.ModelsHeader =>
                    SettingsTexts.T(state.Language, "Models", "模型") + $" ({draft.Models.Count})",
                ProviderRowKind.Model => ModelText(draft.Models[row.ModelIndex], state.Language),
                ProviderRowKind.AddModel =>
                    SettingsTexts.T(state.Language, "+ Add model (a)", "+ 添加模型 (a)"),
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
        lines.Add(SettingsTexts.T(
            state.Language,
            "Ctrl+S saves provider, models and any new API key. F2 saves and exits.",
            "Ctrl+S 保存供应商、模型和新的 API Key。F2 保存并退出。"));
        return (lines, selectedLine);
    }

    private static (List<string> Lines, int SelectedLine) RenderModelEditor(
        SettingsTuiState state)
    {
        var draft = state.ModelDraft;
        if (draft is null)
        {
            return ([SettingsTexts.T(state.Language, "No model selected.", "未选择模型。")], -1);
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
        lines.Add(SettingsTexts.T(state.Language, "Model", "模型") + ": " +
            (string.IsNullOrWhiteSpace(draft.Id.Value)
                ? SettingsTexts.T(state.Language, "new", "新建")
                : draft.Id.Value));
        var selectedLine = -1;
        var rows = new[]
        {
            (ModelRowKind.Id, SettingsTexts.T(state.Language, "Id", "ID") + ": " + Field(
                draft.Id,
                state.Pane == SettingsPane.Content && state.ModelEditorRowIndex == 0)),
            (ModelRowKind.Protocol,
                SettingsTexts.T(state.Language, "Protocol", "协议") + ": [" + draft.Protocol + "]"),
            (ModelRowKind.ContextWindow,
                SettingsTexts.T(state.Language, "Context window", "上下文窗口") + ": " + Field(
                    draft.ContextWindow,
                    state.Pane == SettingsPane.Content && state.ModelEditorRowIndex == 2)),
            (ModelRowKind.CompactThreshold,
                SettingsTexts.T(state.Language, "Compact threshold", "压缩阈值") + ": " + Field(
                    draft.CompactThreshold,
                    state.Pane == SettingsPane.Content && state.ModelEditorRowIndex == 3)),
            (ModelRowKind.MaxOutputTokens,
                SettingsTexts.T(state.Language, "Max output tokens", "最大输出 token") + ": " + Field(
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
        lines.Add(SettingsTexts.T(
            state.Language,
            "Enter cycles protocol. Ctrl+S saves this model and provider. F2 saves and exits.",
            "Enter 切换协议。Ctrl+S 保存模型和供应商。F2 保存并退出。"));
        return (lines, selectedLine);
    }

    private static string ModelText(
        SettingsModelDraft draft,
        UiLanguage language) =>
        string.IsNullOrWhiteSpace(draft.Id.Value)
            ? SettingsTexts.T(language, "(new model)", "(新模型)")
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
        int width,
        SettingsTuiState state)
    {
        var fitted = lines
            .Select(line => DisplayText.TruncateToWidth(line, width))
            .ToList();
        if (height <= 0 || fitted.Count <= height)
        {
            state.RightScroll = 0;
            return fitted;
        }
        var maximum = Math.Max(0, fitted.Count - height);
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
        return fitted.Skip(state.RightScroll).Take(height).ToList();
    }

    private static string OneLine(string value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));

    private static string Fit(string text, int width) =>
        DisplayText.PadToWidth(text, width);
}

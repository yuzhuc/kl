using System.Text.Json;
using Xunit;

namespace Kl.Cli.Tests;

public sealed class SettingsTuiTests
{
    [Fact]
    public void SettingsPageShowsTitleGroupsAndShortcuts()
    {
        var chat = new ChatTuiState { Screen = ChatScreen.Settings };
        chat.Settings.Load(SettingsSnapshot.Empty);

        var render = ChatTuiRenderer.Render(100, 24, chat);
        var lines = render.Split('\n');

        Assert.Equal(24, lines.Length);
        Assert.Equal(SettingsTuiRenderer.Title, lines[0].TrimEnd());
        Assert.Contains("Model", render);
        Assert.Contains("Agent", render);
        Assert.Contains("Plugins", render);
        Assert.Contains("Skills", render);
        Assert.Contains("Tab/Shift+Tab pane", render);
        Assert.Contains("Ctrl+S save", render);
    }

    [Fact]
    public void TabAndShiftTabSwitchColumns()
    {
        var state = new SettingsTuiState();
        state.Load(SettingsSnapshot.Empty);

        Assert.Equal(SettingsPane.Groups, state.Pane);
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        Assert.Equal(SettingsPane.Content, state.Pane);
        state.HandleKey(Key(ConsoleKey.Tab, '\t', shift: true));
        Assert.Equal(SettingsPane.Groups, state.Pane);
        state.HandleKey(Key(ConsoleKey.Tab, '\t', shift: true));
        Assert.Equal(SettingsPane.Content, state.Pane);
    }

    [Fact]
    public void AgentGroupShowsPermissionsAndReasoningPages()
    {
        var state = new SettingsTuiState();
        state.Load(SettingsSnapshot.Empty);

        state.HandleKey(Key(ConsoleKey.DownArrow));
        Assert.Equal(SettingsGroup.Agent, state.Group);
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        var render = SettingsTuiRenderer.Render(100, 24, state);

        Assert.Contains("Agent", render);
        Assert.Contains("Permissions", render);
        Assert.Contains("Reasoning", render);
        Assert.DoesNotContain("Coming soon.", render);
    }

    [Fact]
    public void AgentPermissionsShowsConfirmsAndSavesEveryMode()
    {
        var cases = new[]
        {
            (Mode: SettingsApprovalMode.Ask, Wire: "ask", Current: "auto"),
            (Mode: SettingsApprovalMode.Auto, Wire: "auto", Current: "ask"),
            (Mode: SettingsApprovalMode.ReadOnly, Wire: "read-only", Current: "ask")
        };

        foreach (var item in cases)
        {
            var state = new SettingsTuiState();
            state.Load(AgentSnapshot(item.Current));
            state.HandleKey(Key(ConsoleKey.DownArrow));
            state.HandleKey(Key(ConsoleKey.Tab, '\t'));
            state.HandleKey(Key(ConsoleKey.Enter));

            Assert.Equal(SettingsView.Permissions, state.View);
            var render = SettingsTuiRenderer.Render(100, 30, state);
            Assert.Contains("ask - approve before changes", render);
            Assert.Contains("auto - approve automatically", render);
            Assert.Contains("read-only - no changes", render);
            Assert.Contains("Sandbox backend: appcontainer", render);
            Assert.Contains("Strict: yes", render);

            var target = SettingsAgentValues.ApprovalModes
                .ToList()
                .IndexOf(item.Mode);
            while (state.ListRow < target)
            {
                state.HandleKey(Key(ConsoleKey.DownArrow));
            }
            while (state.ListRow > target)
            {
                state.HandleKey(Key(ConsoleKey.UpArrow));
            }

            var pending = state.HandleKey(Key(ConsoleKey.Enter));
            Assert.Equal(SettingsCommandKind.None, pending.Kind);
            Assert.Equal(item.Mode, state.PendingApprovalMode);
            Assert.Contains("Press Enter to confirm", state.Status);

            var save = state.HandleKey(Key(ConsoleKey.Enter));
            Assert.Equal(SettingsCommandKind.SavePermissions, save.Kind);
            Assert.Equal(SettingsAgentRoutes.Permissions, save.Provider);
            Assert.Equal(item.Wire, save.ApprovalMode);
            Assert.Null(state.PendingApprovalMode);
        }
    }

    [Fact]
    public void AgentPermissionConfirmationCanBeCancelled()
    {
        var state = new SettingsTuiState();
        state.Load(AgentSnapshot("ask"));
        state.HandleKey(Key(ConsoleKey.DownArrow));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.Enter));
        state.HandleKey(Key(ConsoleKey.DownArrow));
        state.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(SettingsApprovalMode.Auto, state.PendingApprovalMode);

        var cancel = state.HandleKey(Key(ConsoleKey.Escape));

        Assert.Equal(SettingsCommandKind.None, cancel.Kind);
        Assert.Null(state.PendingApprovalMode);
        Assert.Equal(SettingsView.Permissions, state.View);
        Assert.Null(state.Status);
    }

    [Fact]
    public void AgentReasoningShowsConfirmsAndSavesEveryEffort()
    {
        var cases = new[]
        {
            (Effort: SettingsReasoningEffort.Default, Wire: "default", Current: "high"),
            (Effort: SettingsReasoningEffort.Low, Wire: "low", Current: "default"),
            (Effort: SettingsReasoningEffort.Medium, Wire: "medium", Current: "default"),
            (Effort: SettingsReasoningEffort.High, Wire: "high", Current: "default")
        };

        foreach (var item in cases)
        {
            var state = new SettingsTuiState();
            state.Load(AgentSnapshot(effort: item.Current));
            state.HandleKey(Key(ConsoleKey.DownArrow));
            state.HandleKey(Key(ConsoleKey.Tab, '\t'));
            state.HandleKey(Key(ConsoleKey.DownArrow));
            state.HandleKey(Key(ConsoleKey.Enter));

            Assert.Equal(SettingsView.Reasoning, state.View);
            var render = SettingsTuiRenderer.Render(100, 30, state);
            Assert.Contains("default - provider decides", render);
            Assert.Contains("low - fastest", render);
            Assert.Contains("medium - balanced", render);
            Assert.Contains("high - deepest", render);
            Assert.Contains("Reasoning support: supported", render);

            var target = SettingsAgentValues.ReasoningEfforts
                .ToList()
                .IndexOf(item.Effort);
            while (state.ListRow < target)
            {
                state.HandleKey(Key(ConsoleKey.DownArrow));
            }
            while (state.ListRow > target)
            {
                state.HandleKey(Key(ConsoleKey.UpArrow));
            }

            var pending = state.HandleKey(Key(ConsoleKey.Enter));
            Assert.Equal(SettingsCommandKind.None, pending.Kind);
            Assert.Equal(item.Effort, state.PendingReasoningEffort);

            var save = state.HandleKey(Key(ConsoleKey.Enter));
            Assert.Equal(SettingsCommandKind.SaveReasoning, save.Kind);
            Assert.Equal(SettingsAgentRoutes.Reasoning, save.Provider);
            Assert.Equal(item.Wire, save.Effort);
        }
    }

    [Fact]
    public void AgentReadFailureIsShownClearly()
    {
        var state = new SettingsTuiState();
        state.Load(AgentSnapshot(
            readError: "agent.config.get failed: permission denied"));
        state.HandleKey(Key(ConsoleKey.DownArrow));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.Enter));

        var render = SettingsTuiRenderer.Render(100, 30, state);

        Assert.Contains("[error] agent.config.get failed: permission denied", render);
    }

    [Fact]
    public void QuickSelectShowsOnlyEnabledProviderModelsAndConfirmsDefault()
    {
        var state = new SettingsTuiState();
        state.Load(ParseSnapshot(
            EnabledAndDisabledModels(),
            ConfigWithEnabledAndDisabledProviders()));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.Q, 'q'));

        Assert.Equal(SettingsView.ModelPicker, state.View);
        Assert.Equal(["m1", "m2"], state.QuickModels.Select(model => model.Id));
        var render = SettingsTuiRenderer.Render(120, 30, state);
        Assert.Contains("p/m1", render);
        Assert.Contains("p/m2", render);
        Assert.DoesNotContain("hidden", render);
        Assert.Contains("context_window=1000", render);
        Assert.Contains("compact_threshold=0.8", render);
        Assert.Contains("max_output_tokens=100", render);
        Assert.DoesNotContain("key=", render);

        state.HandleKey(Key(ConsoleKey.DownArrow));
        var pending = state.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(SettingsCommandKind.None, pending.Kind);
        Assert.Equal("m2", state.PendingDefaultModel?.Id);
        var save = state.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(SettingsCommandKind.SetDefaultModel, save.Kind);
        Assert.Equal("p", save.Provider);
        Assert.Equal("m2", save.Model);
    }

    [Fact]
    public void NewPagesSwitchBetweenEnglishAndChinese()
    {
        var agent = new SettingsTuiState();
        agent.Load(AgentSnapshot());
        agent.HandleKey(Key(ConsoleKey.DownArrow));
        agent.HandleKey(Key(ConsoleKey.Tab, '\t'));
        Assert.Contains("Approval mode and sandbox", SettingsTuiRenderer.Render(100, 24, agent));
        agent.SetLanguage(UiLanguage.ChineseSimplified);
        var agentChinese = SettingsTuiRenderer.Render(100, 24, agent);
        Assert.Contains("批准模式与沙箱", agentChinese);
        Assert.Contains("推理强度支持", agentChinese);
        Assert.Contains("F2 保存并退出", agentChinese);

        agent.HandleKey(Key(ConsoleKey.Enter));
        var permissionsChinese = SettingsTuiRenderer.Render(100, 24, agent);
        Assert.Contains("权限", permissionsChinese);
        Assert.Contains("批准模式", permissionsChinese);
        Assert.Contains("沙箱后端", permissionsChinese);
        Assert.Contains("严格模式", permissionsChinese);
        Assert.Contains("修改前询问", permissionsChinese);
        Assert.Contains("Esc 取消/返回", permissionsChinese);

        var reasoning = new SettingsTuiState();
        reasoning.Load(AgentSnapshot());
        reasoning.SetLanguage(UiLanguage.ChineseSimplified);
        reasoning.HandleKey(Key(ConsoleKey.DownArrow));
        reasoning.HandleKey(Key(ConsoleKey.Tab, '\t'));
        reasoning.HandleKey(Key(ConsoleKey.DownArrow));
        reasoning.HandleKey(Key(ConsoleKey.Enter));
        var reasoningChinese = SettingsTuiRenderer.Render(100, 24, reasoning);
        Assert.Contains("推理强度", reasoningChinese);
        Assert.Contains("供应商/模型", reasoningChinese);
        Assert.Contains("推理支持", reasoningChinese);
        Assert.Contains("供应商决定", reasoningChinese);

        var picker = new SettingsTuiState();
        picker.Load(ParseSnapshot(
            EnabledAndDisabledModels(),
            ConfigWithEnabledAndDisabledProviders()));
        picker.SetLanguage(UiLanguage.ChineseSimplified);
        picker.HandleKey(Key(ConsoleKey.Tab, '\t'));
        picker.HandleKey(Key(ConsoleKey.Q, 'q'));
        var pickerChinese = SettingsTuiRenderer.Render(100, 24, picker);
        Assert.Contains("快速选择模型", pickerChinese);
        Assert.Contains("仅显示已启用供应商的模型", pickerChinese);
        Assert.Contains("Esc 取消/返回", pickerChinese);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    public void NewPagesDoNotExceedTerminalWidth(int width)
    {
        var permissions = new SettingsTuiState();
        permissions.Load(AgentSnapshot());
        permissions.SetLanguage(UiLanguage.ChineseSimplified);
        permissions.HandleKey(Key(ConsoleKey.DownArrow));
        permissions.HandleKey(Key(ConsoleKey.Tab, '\t'));
        permissions.HandleKey(Key(ConsoleKey.Enter));
        AssertRenderFits(width, permissions);

        var reasoning = new SettingsTuiState();
        reasoning.Load(AgentSnapshot());
        reasoning.SetLanguage(UiLanguage.ChineseSimplified);
        reasoning.HandleKey(Key(ConsoleKey.DownArrow));
        reasoning.HandleKey(Key(ConsoleKey.Tab, '\t'));
        reasoning.HandleKey(Key(ConsoleKey.DownArrow));
        reasoning.HandleKey(Key(ConsoleKey.Enter));
        AssertRenderFits(width, reasoning);

        var picker = new SettingsTuiState();
        picker.Load(ParseSnapshot(
            EnabledAndDisabledModels(),
            ConfigWithEnabledAndDisabledProviders()));
        picker.SetLanguage(UiLanguage.ChineseSimplified);
        picker.HandleKey(Key(ConsoleKey.Tab, '\t'));
        picker.HandleKey(Key(ConsoleKey.Q, 'q'));
        AssertRenderFits(width, picker);

        var config = new SettingsTuiState();
        config.Load(AgentSnapshot());
        config.SetLanguage(UiLanguage.ChineseSimplified);
        for (var index = 0; index < 4; index++)
        {
            config.HandleKey(Key(ConsoleKey.DownArrow));
        }
        config.HandleKey(Key(ConsoleKey.Tab, '\t'));
        AssertRenderFits(width, config);
    }

    [Fact]
    public void PluginListParsesSafeFieldsAndSortsById()
    {
        var snapshot = ParseSnapshot(
            """
            { "models": [] }
            """,
            """{ "config": { "providers": {} } }""",
            PluginList());

        Assert.Equal(
            ["alpha-tool", "zeta-provider"],
            snapshot.Plugins.Select(plugin => plugin.Id));

        var tool = snapshot.Plugins[0];
        Assert.Equal("1.2.3", tool.Version);
        Assert.Equal("tool", tool.Kind);
        Assert.True(tool.Enabled);
        Assert.True(tool.Healthy);
        Assert.Equal("wasm component", tool.Detail);
        Assert.Equal("run_alpha", tool.ToolName);
        Assert.Empty(tool.Models);

        var provider = snapshot.Plugins[1];
        Assert.False(provider.Enabled);
        Assert.False(provider.Healthy);
        Assert.Equal(["zeta-1"], provider.Models);
    }

    [Fact]
    public void PluginGroupShowsListDetailsAndRecentErrorWithoutRawParameters()
    {
        var state = new SettingsTuiState();
        state.LoadPlugins(
            ParseSnapshot(
                """
                { "models": [] }
                """,
                """{ "config": { "providers": {} } }""",
                PluginList()),
            "zeta-provider");

        var render = SettingsTuiRenderer.Render(140, 40, state);

        Assert.Contains("Plugins", render);
        Assert.Contains("alpha-tool", render);
        Assert.Contains("zeta-provider", render);
        Assert.Contains("v1.2.3", render);
        Assert.Contains("provider", render);
        Assert.Contains("disabled", render);
        Assert.Contains("unhealthy", render);
        Assert.Contains("startup failed", render);
        Assert.Contains("Recent error: startup failed", render);
        Assert.Contains("Models: zeta-1", render);
        Assert.Contains(SettingsTuiRenderer.PluginFooterHints, render);
        Assert.DoesNotContain("super-secret", render);
        Assert.DoesNotContain("hidden-token", render);
    }

    [Fact]
    public void PluginActionsRequireConfirmationAndEmitCommands()
    {
        var snapshot = ParseSnapshot(
            """
            { "models": [] }
            """,
            """{ "config": { "providers": {} } }""",
            PluginList());

        var state = new SettingsTuiState();
        state.LoadPlugins(snapshot, "alpha-tool");
        var pendingDisable = state.HandleKey(Key(ConsoleKey.D, 'd'));
        Assert.Equal(SettingsCommandKind.None, pendingDisable.Kind);
        Assert.Equal(PluginAction.Disable, state.PendingPluginAction);
        Assert.Contains("Press d again or Enter to confirm", state.Status);
        var disable = state.HandleKey(Key(ConsoleKey.D, 'd'));
        Assert.Equal(SettingsCommandKind.SetPluginEnabled, disable.Kind);
        Assert.Equal("alpha-tool", disable.Plugin);
        Assert.False(disable.Enabled);

        state.LoadPlugins(snapshot, "zeta-provider");
        var pendingEnable = state.HandleKey(Key(ConsoleKey.E, 'e'));
        Assert.Equal(SettingsCommandKind.None, pendingEnable.Kind);
        var enable = state.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(SettingsCommandKind.SetPluginEnabled, enable.Kind);
        Assert.Equal("zeta-provider", enable.Plugin);
        Assert.True(enable.Enabled);

        state.LoadPlugins(snapshot, "alpha-tool");
        var pendingReload = state.HandleKey(Key(ConsoleKey.R, 'r'));
        Assert.Equal(SettingsCommandKind.None, pendingReload.Kind);
        Assert.Equal(PluginAction.Reload, state.PendingPluginAction);
        var reload = state.HandleKey(Key(ConsoleKey.R, 'r'));
        Assert.Equal(SettingsCommandKind.ReloadPlugin, reload.Kind);
        Assert.Equal("alpha-tool", reload.Plugin);
    }

    [Fact]
    public void PluginGroupShowsLoadingEmptyAndErrorStates()
    {
        var state = new SettingsTuiState();
        state.LoadPlugins(SettingsSnapshot.Empty);
        var empty = SettingsTuiRenderer.Render(100, 24, state);
        Assert.Contains("No plugins installed", empty);

        state.LoadPlugins(SettingsSnapshot.Empty);
        state.BeginLoad();
        var loading = SettingsTuiRenderer.Render(100, 24, state);
        Assert.Contains("Loading plugins...", loading);

        state.FailLoad("plugin.list failed");
        var failed = SettingsTuiRenderer.Render(100, 24, state);
        Assert.Contains("[error] plugin.list failed", failed);

        state.LoadPlugins(SettingsSnapshot.Empty);
        state.Error = "plugin action failed: permission denied";
        var actionFailed = SettingsTuiRenderer.Render(100, 24, state);
        Assert.Contains("[error] plugin action failed: permission denied", actionFailed);
    }

    [Fact]
    public void PluginGroupHandlesResize()
    {
        var snapshot = ParseSnapshot(
            """
            { "models": [] }
            """,
            """{ "config": { "providers": {} } }""",
            PluginList());

        var small = new SettingsTuiState();
        small.LoadPlugins(snapshot, "alpha-tool");
        var large = new SettingsTuiState();
        large.LoadPlugins(snapshot, "alpha-tool");

        var smallFrame = SettingsTuiRenderer.Render(70, 16, small);
        var largeFrame = SettingsTuiRenderer.Render(120, 30, large);

        Assert.Equal(16, smallFrame.Split('\n').Length);
        Assert.Equal(30, largeFrame.Split('\n').Length);
        Assert.Contains("alpha-tool", smallFrame);
        Assert.Contains("Selected plugin", largeFrame);
        Assert.NotEqual(smallFrame, largeFrame);
    }

    [Fact]
    public void ResizeRequestsRedrawAndChangesFrameSize()
    {
        Assert.True(ChatTuiRenderer.NeedsRedraw(false, false, 80, 24, 100, 24));
        Assert.True(ChatTuiRenderer.NeedsRedraw(false, false, 80, 24, 80, 30));
        Assert.False(ChatTuiRenderer.NeedsRedraw(false, false, 80, 24, 80, 24));
        Assert.True(ChatTuiRenderer.NeedsRedraw(true, false, 80, 24, 80, 24));

        var small = new ChatTuiState { Screen = ChatScreen.Settings };
        small.Settings.Load(SettingsSnapshot.Empty);
        var large = new ChatTuiState { Screen = ChatScreen.Settings };
        large.Settings.Load(SettingsSnapshot.Empty);

        var smallFrame = ChatTuiRenderer.Render(70, 16, small);
        var largeFrame = ChatTuiRenderer.Render(120, 30, large);
        Assert.Equal(16, smallFrame.Split('\n').Length);
        Assert.Equal(30, largeFrame.Split('\n').Length);
        Assert.NotEqual(smallFrame, largeFrame);
    }

    [Fact]
    public void BaseModeExpandsUrlsAndTrimsTrailingSlashes()
    {
        var urls = SettingsUrls.ExpandBase("https://example.test/v1///");

        Assert.Equal("https://example.test/v1/chat/completions", urls.Chat);
        Assert.Equal("https://example.test/v1/messages", urls.Anthropic);
        Assert.Equal("https://example.test/v1/responses", urls.Responses);

        var protocols = SettingsUrls.Build(
            SettingsAddressMode.Base,
            "https://example.test/v1/",
            string.Empty,
            string.Empty,
            string.Empty);
        Assert.Equal("https://example.test/v1/chat/completions", protocols["openai-chat"]);
        Assert.Equal("https://example.test/v1/messages", protocols["anthropic"]);
        Assert.Equal("https://example.test/v1/responses", protocols["openai-responses"]);
    }

    [Fact]
    public void FullModePreservesUserInputExactly()
    {
        var protocols = SettingsUrls.Build(
            SettingsAddressMode.Full,
            "ignored",
            " https://example.test/custom/chat/ ",
            "https://example.test/custom/messages?tenant=kl",
            "https://example.test/custom/responses");

        Assert.Equal(
            " https://example.test/custom/chat/ ",
            protocols["openai-chat"]);
        Assert.Equal(
            "https://example.test/custom/messages?tenant=kl",
            protocols["anthropic"]);
        Assert.Equal(
            "https://example.test/custom/responses",
            protocols["openai-responses"]);
    }

    [Fact]
    public void ProviderDraftWritesExpandedUrlsAndNoKeyIntoUpsert()
    {
        var draft = ProviderDraft.New();
        draft.Id.Set("local");
        draft.BaseUrl.Set("https://api.example/v1/");
        var model = new SettingsModelDraft();
        model.Id.Set("model-1");
        draft.Models.Add(model);

        var parameters = draft.ToUpsert();
        var protocols = Assert.IsType<Dictionary<string, string>>(parameters["protocols"]);

        Assert.Equal("local", parameters["id"]);
        Assert.Equal("https://api.example/v1/chat/completions", protocols["openai-chat"]);
        Assert.Equal("https://api.example/v1/messages", protocols["anthropic"]);
        Assert.Equal("https://api.example/v1/responses", protocols["openai-responses"]);
        Assert.False(parameters.ContainsKey("api_key"));
        Assert.False(parameters.ContainsKey("key"));
    }

    [Fact]
    public void SwitchingToFullPrefillsExpandedUrls()
    {
        var state = new SettingsTuiState();
        state.Load(ParseSnapshot(TwoModelProviderList(), ConfigWithProvider(enabled: true)));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.Enter));
        state.HandleKey(Key(ConsoleKey.DownArrow));
        state.HandleKey(Key(ConsoleKey.DownArrow));

        state.HandleKey(Key(ConsoleKey.Enter));

        Assert.Equal(SettingsAddressMode.Full, state.Draft!.AddressMode);
        Assert.Equal(
            "https://example.test/v1/chat/completions",
            state.Draft.ChatUrl.Value);
        Assert.Equal(
            "https://example.test/v1/messages",
            state.Draft.AnthropicUrl.Value);
        Assert.Equal(
            "https://example.test/v1/responses",
            state.Draft.ResponsesUrl.Value);
    }

    [Fact]
    public void EnterOnModelRequestsDefaultSelection()
    {
        var state = new SettingsTuiState();
        state.Load(ParseSnapshot(TwoModelProviderList(), ConfigWithProvider(enabled: true)));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.DownArrow));
        state.HandleKey(Key(ConsoleKey.DownArrow));

        var command = state.HandleKey(Key(ConsoleKey.Enter));

        Assert.Equal(SettingsCommandKind.SetDefaultModel, command.Kind);
        Assert.Equal("p", command.Provider);
        Assert.Equal("m2", command.Model);
    }

    [Fact]
    public void DisabledProviderModelsAreHidden()
    {
        var snapshot = ParseSnapshot(
            """
            {
              "active_provider": "p",
              "active_model": "m1",
              "models": [
                {
                  "provider": "p",
                  "model": "m1",
                  "protocol": "openai-chat",
                  "url": "https://example.test/v1/chat/completions",
                  "context_window": 1000,
                  "compact_threshold": 0.8,
                  "max_output_tokens": 100,
                  "enabled": true,
                  "key_configured": true
                },
                {
                  "provider": "d",
                  "model": "hidden",
                  "protocol": "openai-chat",
                  "url": "https://example.test/v1/chat/completions",
                  "context_window": 1000,
                  "compact_threshold": 0.8,
                  "max_output_tokens": 100,
                  "enabled": true,
                  "key_configured": true
                }
              ]
            }
            """,
            """
            {
              "config": {
                "providers": {
                  "p": { "enabled": true, "path": "providers/p.toml" },
                  "d": { "enabled": false, "path": "providers/d.toml" }
                }
              }
            }
            """);

        Assert.DoesNotContain(snapshot.Models, model => model.Provider == "d");
        var disabled = Assert.Single(snapshot.Providers, provider => provider.Id == "d");
        Assert.False(disabled.Enabled);
        Assert.Equal(0, disabled.ModelCount);
        Assert.Empty(disabled.Models);
    }

    [Fact]
    public void DisabledProviderIsReadOnlyUntilEnabled()
    {
        var state = new SettingsTuiState();
        state.Load(ParseSnapshot(TwoModelProviderList(), ConfigWithProvider(enabled: false)));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));

        state.HandleKey(Key(ConsoleKey.Enter));
        Assert.Null(state.Draft);
        Assert.Contains("enable this provider first", state.Error);

        state.HandleKey(Key(ConsoleKey.A, 'a'));
        Assert.Null(state.Draft);

        var command = state.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        Assert.Equal(SettingsCommandKind.ToggleProvider, command.Kind);
        Assert.Equal("p", command.Provider);
        Assert.True(command.Enabled);
    }

    [Fact]
    public void ApiKeyIsMaskedAndNeverRendered()
    {
        var state = new SettingsTuiState();
        state.Load(ParseSnapshot(TwoModelProviderList(), ConfigWithProvider(enabled: true)));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.Enter));
        state.Draft!.ApiKey.Set("super-secret");

        var render = SettingsTuiRenderer.Render(120, 30, state);

        Assert.DoesNotContain("super-secret", render);
        Assert.Contains("************", render);
        Assert.DoesNotContain(
            "super-secret",
            ProviderDraft.Redact("failed for super-secret", "super-secret"));
    }

    [Fact]
    public void NonNativeProtocolsAreNotOfferedForEditing()
    {
        var snapshot = ParseSnapshot(
            """
            {
              "active_provider": "scripted",
              "active_model": "scripted",
              "models": [
                {
                  "provider": "scripted",
                  "model": "scripted",
                  "protocol": "scripted",
                  "url": "offline://scripted",
                  "context_window": 8192,
                  "compact_threshold": 0.8,
                  "max_output_tokens": 1024,
                  "enabled": true,
                  "key_configured": true
                }
              ]
            }
            """,
            """{ "config": { "providers": {} } }""");

        Assert.Empty(snapshot.Providers);
        Assert.Empty(snapshot.Models);
    }

    [Fact]
    public void SettingsCommandRecognizesBothAliases()
    {
        Assert.True(ChatCommand.IsSettings("/settings"));
        Assert.True(ChatCommand.IsSettings(" /设置 "));
        Assert.False(ChatCommand.IsSettings("/settings now"));
        Assert.False(ChatCommand.IsSettings("/other"));
    }

    [Fact]
    public void CtrlSOnModelEditorSavesTheProvider()
    {
        var state = new SettingsTuiState();
        state.Load(ParseSnapshot(TwoModelProviderList(), ConfigWithProvider(enabled: true)));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.Enter));
        state.EditModel(0);

        var command = state.HandleKey(Key(ConsoleKey.S, '\u0013', control: true));

        Assert.Equal(SettingsCommandKind.SaveProvider, command.Kind);
    }

    [Fact]
    public void CtrlEnterOnModelEditorSavesProviderAndClosesSettings()
    {
        var state = new SettingsTuiState();
        state.Load(ParseSnapshot(TwoModelProviderList(), ConfigWithProvider(enabled: true)));
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        state.HandleKey(Key(ConsoleKey.Enter));
        state.EditModel(0);

        var command = state.HandleKey(Key(ConsoleKey.F2));

        Assert.Equal(SettingsCommandKind.SaveProviderAndClose, command.Kind);
    }

    [Fact]
    public void ConfigGroupTogglesLanguageAndRendersChinese()
    {
        var state = new SettingsTuiState();
        state.Load(SettingsSnapshot.Empty);
        for (var index = 0; index < 4; index++)
        {
            state.HandleKey(Key(ConsoleKey.DownArrow));
        }
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));

        var command = state.HandleKey(Key(ConsoleKey.Enter));

        Assert.Equal(SettingsCommandKind.SetLanguage, command.Kind);
        Assert.Equal(UiLanguage.ChineseSimplified, command.Language);
        Assert.Equal(UiLanguage.ChineseSimplified, state.Language);
        var render = SettingsTuiRenderer.Render(100, 24, state);
        Assert.Contains("KL 设置", render);
        Assert.Contains("配置", render);
        Assert.Contains("语言", render);
        Assert.Contains("F2 保存并退出", render);
    }
    private static SettingsSnapshot AgentSnapshot(
        string approvalMode = "ask",
        string effort = "default",
        bool? supported = true,
        string? readError = null) =>
        ParseSnapshot(TwoModelProviderList(), ConfigWithProvider(enabled: true)) with
        {
            AgentConfig = new SettingsAgentConfig(
                approvalMode,
                effort,
                supported,
                "appcontainer",
                true,
                "p",
                "m1",
                readError),
            Paths = new SettingsPaths(
                "/tmp/kl.toml",
                "/tmp/home/ui.json",
                "/tmp/home")
        };

    private static void AssertRenderFits(int width, SettingsTuiState state)
    {
        const int height = 24;
        var render = SettingsTuiRenderer.Render(width, height, state);
        var lines = render.Split('\n');

        Assert.Equal(height, lines.Length);
        foreach (var line in lines)
        {
            Assert.True(
                DisplayText.Width(line) <= width,
                $"width {width}: line is {DisplayText.Width(line)} columns: [{line}]");
        }
    }

    private static SettingsSnapshot ParseSnapshot(
        string providerList,
        string config,
        string? pluginList = null)
    {
        using var providers = JsonDocument.Parse(providerList);
        using var configDocument = JsonDocument.Parse(config);
        using var plugins = JsonDocument.Parse(
            pluginList ?? """{ "plugins": [] }""");
        return SettingsSnapshot.FromJson(
            providers.RootElement,
            configDocument.RootElement,
            plugins.RootElement);
    }

    private static string PluginList() => """
        {
          "plugins": [
            {
              "id": "zeta-provider",
              "version": "2.0.0",
              "kind": "provider",
              "enabled": false,
              "healthy": false,
              "models": ["zeta-1"],
              "detail": "startup failed",
              "parameters": {
                "api_key": "super-secret",
                "token": "hidden-token"
              }
            },
            {
              "id": "alpha-tool",
              "version": "1.2.3",
              "kind": "tool",
              "enabled": true,
              "healthy": true,
              "tool_name": "run_alpha",
              "models": [],
              "detail": "wasm component"
            }
          ]
        }
        """;

    private static string TwoModelProviderList() => """
        {
          "active_provider": "p",
          "active_model": "m1",
          "models": [
            {
              "provider": "p",
              "model": "m1",
              "protocol": "openai-chat",
              "url": "https://example.test/v1/chat/completions",
              "context_window": 1000,
              "compact_threshold": 0.8,
              "max_output_tokens": 100,
              "enabled": true,
              "key_configured": true
            },
            {
              "provider": "p",
              "model": "m2",
              "protocol": "openai-responses",
              "url": "https://example.test/v1/responses",
              "context_window": 2000,
              "compact_threshold": 0.9,
              "max_output_tokens": 200,
              "enabled": true,
              "key_configured": true
            }
          ]
        }
        """;

    private static string ConfigWithProvider(bool enabled) => $$"""
        {
          "config": {
            "providers": {
              "p": { "enabled": {{enabled.ToString().ToLowerInvariant()}}, "path": "providers/p.toml" }
            }
          }
        }
        """;

    private static string EnabledAndDisabledModels() => """
        {
          "active_provider": "p",
          "active_model": "m1",
          "models": [
            {
              "provider": "p",
              "model": "m1",
              "protocol": "openai-chat",
              "url": "https://example.test/v1/chat/completions",
              "context_window": 1000,
              "compact_threshold": 0.8,
              "max_output_tokens": 100,
              "enabled": true,
              "key_configured": true
            },
            {
              "provider": "p",
              "model": "m2",
              "protocol": "openai-responses",
              "url": "https://example.test/v1/responses",
              "context_window": 2000,
              "compact_threshold": 0.9,
              "max_output_tokens": 200,
              "enabled": true,
              "key_configured": true
            },
            {
              "provider": "d",
              "model": "hidden",
              "protocol": "openai-chat",
              "url": "https://example.test/v1/chat/completions",
              "context_window": 3000,
              "compact_threshold": 0.7,
              "max_output_tokens": 300,
              "enabled": true,
              "key_configured": true
            }
          ]
        }
        """;

    private static string ConfigWithEnabledAndDisabledProviders() => """
        {
          "config": {
            "providers": {
              "p": { "enabled": true, "path": "providers/p.toml" },
              "d": { "enabled": false, "path": "providers/d.toml" }
            }
          }
        }
        """;

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char character = '\0',
        bool shift = false,
        bool control = false) =>
        new(character, key, shift, alt: false, control);
}

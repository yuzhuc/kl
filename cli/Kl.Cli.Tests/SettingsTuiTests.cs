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
    public void GroupMovementShowsPlaceholder()
    {
        var state = new SettingsTuiState();
        state.Load(SettingsSnapshot.Empty);

        state.HandleKey(Key(ConsoleKey.DownArrow));
        Assert.Equal(SettingsGroup.Agent, state.Group);
        state.HandleKey(Key(ConsoleKey.Tab, '\t'));
        var render = SettingsTuiRenderer.Render(100, 24, state);

        Assert.Contains("Agent", render);
        Assert.Contains("Coming soon.", render);
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

    private static SettingsSnapshot ParseSnapshot(string providerList, string config)
    {
        using var providers = JsonDocument.Parse(providerList);
        using var configDocument = JsonDocument.Parse(config);
        return SettingsSnapshot.FromJson(providers.RootElement, configDocument.RootElement);
    }

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

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char character = '\0',
        bool shift = false,
        bool control = false) =>
        new(character, key, shift, alt: false, control);
}

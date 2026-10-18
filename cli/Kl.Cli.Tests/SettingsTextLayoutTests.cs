using Xunit;

namespace Kl.Cli.Tests;

public sealed class SettingsTextLayoutTests
{
    [Fact]
    public void CjkAndFullWidthTextUseTwoColumns()
    {
        Assert.Equal(4, DisplayText.Width("中文"));
        Assert.Equal(7, DisplayText.Width("KL 设置"));
        Assert.Equal(16, DisplayText.Width("，。！？：；（）"));
        Assert.Equal(1, DisplayText.Width("e\u0301"));
        Assert.Equal(0, DisplayText.Width(string.Empty));
    }

    [Fact]
    public void PadAndTruncateRespectDisplayWidth()
    {
        var padded = DisplayText.PadToWidth("中文", 6);

        Assert.Equal("中文  ", padded);
        Assert.Equal(6, DisplayText.Width(padded));

        var truncated = DisplayText.TruncateToWidth("中文abc", 3);

        Assert.Equal("中", truncated);
        Assert.True(DisplayText.Width(truncated) <= 3);

        var paddedTruncated = DisplayText.PadToWidth("中文abc", 3);

        Assert.Equal("中 ", paddedTruncated);
        Assert.Equal(3, DisplayText.Width(paddedTruncated));

        Assert.Equal(string.Empty, DisplayText.TruncateToWidth("中文", 1));
    }

    [Fact]
    public void AsciiPaddingMatchesLegacyFit()
    {
        var text = "local/gpt-test [openai-chat] ctx=128000";

        Assert.Equal(text.Length, DisplayText.Width(text));
        Assert.Equal(text.PadRight(60), DisplayText.PadToWidth(text, 60));
        Assert.Equal(text[..30], DisplayText.PadToWidth(text, 30));
    }

    [Fact]
    public void WrapBreaksOnDisplayWidth()
    {
        Assert.Equal(
            new[] { "中文", "中文" },
            DisplayText.WrapToWidth("中文中文", 4));
        Assert.Equal(
            new[] { "ab中", "文cd" },
            DisplayText.WrapToWidth("ab中文cd", 4));
        Assert.Equal(
            new[] { "hello", "world" },
            DisplayText.WrapToWidth("hello world", 7));

        foreach (var line in DisplayText.WrapToWidth("你好，世界！KL 设置", 5))
        {
            Assert.True(DisplayText.Width(line) <= 5);
        }
    }

    [Fact]
    public void SurrogatePairsAndCombiningMarksAreNotSplit()
    {
        var truncated = DisplayText.TruncateToWidth("😀😀", 3);

        Assert.Equal("😀", truncated);
        Assert.Equal(2, truncated.Length);
        Assert.True(char.IsSurrogatePair(truncated, 0));

        Assert.Equal("a", DisplayText.TruncateToWidth("a😀b", 2));
        Assert.Equal("a😀", DisplayText.PadToWidth("a😀", 3));
        Assert.Equal("e\u0301", DisplayText.TruncateToWidth("e\u0301x", 1));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    public void ChineseSettingsLinesNeverExceedTerminalWidth(int width)
    {
        for (var groupIndex = 0; groupIndex < 5; groupIndex++)
        {
            var state = new SettingsTuiState();
            state.Load(Snapshot());
            state.SetLanguage(UiLanguage.ChineseSimplified);
            for (var index = 0; index < groupIndex; index++)
            {
                state.HandleKey(Key(ConsoleKey.DownArrow));
            }
            state.HandleKey(Key(ConsoleKey.Tab, '\t'));

            AssertRenderFits(width, state);
        }

        var providerEditor = new SettingsTuiState();
        providerEditor.Load(Snapshot());
        providerEditor.SetLanguage(UiLanguage.ChineseSimplified);
        providerEditor.HandleKey(Key(ConsoleKey.Tab, '\t'));
        providerEditor.HandleKey(Key(ConsoleKey.N, 'n'));
        Assert.Equal(SettingsView.ProviderEditor, providerEditor.View);
        AssertRenderFits(width, providerEditor);

        var modelEditor = new SettingsTuiState();
        modelEditor.Load(Snapshot());
        modelEditor.SetLanguage(UiLanguage.ChineseSimplified);
        modelEditor.HandleKey(Key(ConsoleKey.Tab, '\t'));
        modelEditor.HandleKey(Key(ConsoleKey.DownArrow));
        modelEditor.HandleKey(Key(ConsoleKey.E, 'e'));
        modelEditor.HandleKey(Key(ConsoleKey.E, 'e'));
        Assert.Equal(SettingsView.ModelEditor, modelEditor.View);
        AssertRenderFits(width, modelEditor);
    }

    [Fact]
    public async Task RepeatedLanguageToggleKeepsStateAndDoesNotStartCore()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "kl-settings-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new CliOptions
            {
                CorePath = Path.Combine(root, "missing-core"),
                HomePath = Path.Combine(root, "home")
            };
            var state = new SettingsTuiState();
            state.Load(Snapshot());
            for (var index = 0; index < 4; index++)
            {
                state.HandleKey(Key(ConsoleKey.DownArrow));
            }
            state.HandleKey(Key(ConsoleKey.Tab, '\t'));

            Assert.Equal(SettingsGroup.Config, state.Group);
            var snapshot = state.Snapshot;
            var groupIndex = state.GroupIndex;
            var pane = state.Pane;
            var listRow = state.ListRow;

            for (var index = 0; index < 3; index++)
            {
                var command = state.HandleKey(Key(ConsoleKey.Enter));

                Assert.Equal(SettingsCommandKind.SetLanguage, command.Kind);
                Assert.Equal<UiLanguage?>(state.Language, command.Language);
                await SettingsTuiRunner.SaveLanguageAsync(options, state.Language);

                Assert.Same(snapshot, state.Snapshot);
                Assert.Equal(groupIndex, state.GroupIndex);
                Assert.Equal(pane, state.Pane);
                Assert.Equal(listRow, state.ListRow);
                AssertRenderFits(60, state);
            }

            Assert.Equal(UiLanguage.ChineseSimplified, state.Language);
            Assert.False(File.Exists(options.CorePath));
            var json = await File.ReadAllTextAsync(
                Path.Combine(root, "home", "ui.json"));
            Assert.Contains("\"language\": \"zh-CN\"", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

    private static SettingsSnapshot Snapshot() => new(
        "provider-one",
        "model-one",
        [
            new SettingsProvider(
                "provider-one-with-a-long-identifier",
                Enabled: true,
                KeyConfigured: true,
                ModelCount: 1,
                ProtocolSummary: "openai-chat,openai-responses",
                AddressMode: SettingsAddressMode.Base,
                BaseUrl: "https://gateway.example.test/v1/",
                ChatUrl: "https://gateway.example.test/v1/chat/completions",
                AnthropicUrl: "https://gateway.example.test/v1/messages",
                ResponsesUrl: "https://gateway.example.test/v1/responses",
                Models:
                [
                    new SettingsModel(
                        "provider-one-with-a-long-identifier",
                        "model-one-with-a-long-identifier",
                        "openai-chat",
                        128000,
                        0.85,
                        8192,
                        "https://gateway.example.test/v1/chat/completions",
                        KeyConfigured: true)
                ])
        ],
        [
            new SettingsModel(
                "provider-one-with-a-long-identifier",
                "model-one-with-a-long-identifier",
                "openai-chat",
                128000,
                0.85,
                8192,
                "https://gateway.example.test/v1/chat/completions",
                KeyConfigured: true)
        ],
        [
            new SettingsPlugin(
                "plugin-alpha-with-a-long-identifier",
                "1.2.3",
                "provider",
                Enabled: true,
                Healthy: true,
                Detail: "startup detail long enough to need truncation in narrow terminals",
                ToolName: "run_alpha",
                Models: ["model-one-with-a-long-identifier"])
        ]);

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char character = '\0',
        bool shift = false,
        bool control = false) =>
        new(character, key, shift, alt: false, control);
}
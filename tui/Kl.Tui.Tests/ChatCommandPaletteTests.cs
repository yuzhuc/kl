using Xunit;

namespace Kl.Tui.Tests;

public sealed class ChatCommandPaletteTests
{
    [Fact]
    public void SlashShowsFirstEightRegisteredCommands()
    {
        var state = new ChatTuiState();
        state.AddInfo("seed");
        state.Insert('/');

        Assert.True(ChatCommandPalette.IsOpen(state));
        Assert.Equal(20, ChatCommandPalette.Matches(state.Input).Count);
        Assert.Contains(ChatCommandPalette.Matches(state.Input), match => match.Name == "/fork");
        Assert.Contains(ChatCommandPalette.Matches(state.Input), match => match.Name == "/cost");
        Assert.Contains(ChatCommandPalette.Matches(state.Input), match => match.Name == "/context");
        Assert.Contains(ChatCommandPalette.Matches(state.Input), match => match.Name == "/diff");
        Assert.Contains(ChatCommandPalette.Matches(state.Input), match => match.Name == "/export");
        Assert.Equal("/init", ChatCommandPalette.Selected(state)?.Name);

        var lines = ChatTuiRenderer.CommandPaletteLines(state, 100);
        Assert.Equal(8, lines.Count);
        Assert.Contains(lines, line => line.Contains("/init"));
        Assert.Contains(lines, line => line.Contains("/settings"));
        Assert.DoesNotContain(lines, line => line.Contains("/resume"));
    }

    [Fact]
    public void CostCommandAccumulatesUsageAndPricing()
    {
        var command = ChatCommandPalette.FindExact("/cost");
        Assert.NotNull(command);
        Assert.Equal(ChatCommandKind.Cost, command!.Kind);

        var state = new ChatTuiState();
        state.Apply(new SessionEvent(
            SessionEventKind.TurnCompleted,
            Status: "completed",
            InputTokens: 120,
            OutputTokens: 30,
            CostUsd: 0.001));
        state.Apply(new SessionEvent(
            SessionEventKind.TurnCompleted,
            Status: "completed",
            InputTokens: 80,
            OutputTokens: 20,
            CostUsd: 0.002));

        Assert.Equal(80, state.LastTurnInputTokens);
        Assert.Equal(20, state.LastTurnOutputTokens);
        Assert.Equal(0.002, state.LastTurnCostUsd);
        Assert.Equal(200, state.TotalInputTokens);
        Assert.Equal(50, state.TotalOutputTokens);
        Assert.Equal(0.003, state.TotalCostUsd, 6);
    }

    [Fact]
    public void SelectionScrollsToNinthItem()
    {
        var state = new ChatTuiState();
        state.AddInfo("seed");
        state.Input = "/";
        state.Cursor = 1;

        for (var index = 0; index < 8; index++)
        {
            ChatCommandPalette.MoveSelection(state, 1);
        }

        Assert.Equal("/resume", ChatCommandPalette.Selected(state)?.Name);
        var lines = ChatTuiRenderer.CommandPaletteLines(state, 100);
        Assert.Contains(lines, line => line.Contains("/resume"));
        Assert.DoesNotContain(lines, line => line.Contains("/init"));
    }

    [Fact]
    public void PrefixSeOnlyShowsSettings()
    {
        var matches = ChatCommandPalette.Matches("/se");
        Assert.Single(matches);
        Assert.Equal("/settings", matches[0].Name);
    }

    [Fact]
    public void ChineseQueryDoesNotOpenPalette()
    {
        var state = new ChatTuiState();
        state.Insert('/');
        state.Insert('设');

        Assert.Equal("/设", state.Input);
        Assert.False(ChatCommandPalette.IsQuery(state.Input));
        Assert.Empty(ChatCommandPalette.Matches(state.Input));
        Assert.False(ChatCommandPalette.IsOpen(state));
        Assert.Null(ChatCommandPalette.FindExact("/设置"));
    }

    [Fact]
    public void DescriptionsFollowLanguageAndNamesStayEnglish()
    {
        var state = new ChatTuiState();
        state.AddInfo("seed");
        state.Input = "/st";
        state.Cursor = 3;
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);

        var chinese = ChatTuiRenderer.Render(100, 24, state);
        Assert.Contains("/status", chinese);
        Assert.Contains("查看当前会话配置", chinese);
        Assert.DoesNotContain("show current session configuration", chinese);

        state.Settings.SetLanguage(UiLanguage.English);
        var english = ChatTuiRenderer.Render(100, 24, state);
        Assert.Contains("/status", english);
        Assert.Contains("show current session configuration", english);
        Assert.DoesNotContain("查看当前会话配置", english);
    }

    [Fact]
    public void AgentCommandSharesPresetDefinitionsAndValidation()
    {
        var command = ChatCommandPalette.FindExact("/agent");
        Assert.NotNull(command);
        Assert.Equal(ChatCommandKind.Agent, command!.Kind);
        Assert.Contains("agent preset", command.EnglishDescription);
        Assert.Contains("Agent preset", command.ChineseDescription);
        Assert.Equal(
            new[] { "build", "plan", "explore", "review", "verify", "deep" },
            ChatCommandPalette.AgentPresets);
        Assert.True(ChatCommandPalette.IsValidAgentPreset("deep"));
        Assert.False(ChatCommandPalette.IsValidAgentPreset("nope"));
        Assert.False(ChatCommandPalette.IsValidAgentPreset(null));
    }

    [Fact]
    public void SkillsAndMcpCommandsAreRegistered()
    {
        var skills = ChatCommandPalette.FindExact("/skills");
        var mcp = ChatCommandPalette.FindExact("/mcp");

        Assert.NotNull(skills);
        Assert.Equal(ChatCommandKind.Skills, skills!.Kind);
        Assert.Equal("list available skills", skills.EnglishDescription);
        Assert.NotNull(mcp);
        Assert.Equal(ChatCommandKind.Mcp, mcp!.Kind);
        Assert.Equal("list MCP tools", mcp.EnglishDescription);
    }

    [Theory]
    [InlineData("/agent", true, null)]
    [InlineData("/agent plan", true, "plan")]
    [InlineData(" /agent   deep ", true, "deep")]
    [InlineData("/agent nope", true, "nope")]
    [InlineData("/agents plan", false, null)]
    public void AgentCommandParserAcceptsOptionalPreset(
        string input,
        bool expectedMatch,
        string? expectedPreset)
    {
        Assert.Equal(
            expectedMatch,
            ChatTuiRunner.TryParseAgentCommand(input, out var preset));
        Assert.Equal(expectedPreset, preset);
    }

    [Fact]
    public void OverlayDoesNotChangeTranscriptOrScroll()
    {
        var state = new ChatTuiState();
        for (var index = 0; index < 30; index++)
        {
            state.AddInfo($"line-{index:00}");
        }
        state.TranscriptScroll = 5;

        var beforeTranscript = ChatTuiRenderer.TranscriptLines(state, 80);
        var beforeScroll = state.TranscriptScroll;
        var beforeMaximum = ChatTuiRenderer.MaxTranscriptScroll(state, 80, 24);
        var beforeBody = ChatTuiRenderer.Render(80, 24, state)
            .Split('\n')
            .Skip(2)
            .Take(8)
            .ToArray();

        state.Input = "/";
        state.Cursor = 1;

        var afterTranscript = ChatTuiRenderer.TranscriptLines(state, 80);
        var afterBody = ChatTuiRenderer.Render(80, 24, state)
            .Split('\n')
            .Skip(2)
            .Take(8)
            .ToArray();

        Assert.Equal(beforeTranscript, afterTranscript);
        Assert.Equal(beforeScroll, state.TranscriptScroll);
        Assert.Equal(
            beforeMaximum,
            ChatTuiRenderer.MaxTranscriptScroll(state, 80, 24));
        Assert.Equal(beforeBody, afterBody);
    }

    [Fact]
    public void PaletteColumnsAlignAndNeverOverflow()
    {
        foreach (var width in new[] { 40, 60, 80, 100 })
        {
            var state = new ChatTuiState();
            state.AddInfo("seed");
            state.Input = "/";
            state.Cursor = 1;

            var matches = ChatCommandPalette.Matches(state.Input);
            var nameWidth = matches.Max(command => DisplayText.Width(command.Name));
            var descriptionColumn = 2 + nameWidth + 2;
            Assert.True(
                width - descriptionColumn >= (width * 30 + 99) / 100,
                $"description column too narrow at width {width}");

            var lines = ChatTuiRenderer.CommandPaletteLines(state, width);
            Assert.Equal(8, lines.Count);
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                Assert.True(DiffRender.Ansi.VisibleWidth(line) <= width);
                var plain = DiffRender.Ansi.Strip(line);
                Assert.Equal(width, plain.Length);
                Assert.Equal(2, plain.IndexOf(matches[index].Name));
                Assert.Equal(' ', plain[descriptionColumn - 1]);
                var description = ChatCommandPalette.Description(
                    matches[index],
                    UiLanguage.English);
                Assert.StartsWith(
                    new string(description.Take(1).ToArray()),
                    plain[descriptionColumn..].TrimEnd());
            }
        }
    }

    [Fact]
    public void SelectedRowUsesArrowAndInverseAndNonSelectedUsesTwoSpaces()
    {
        var state = new ChatTuiState();
        state.AddInfo("seed");
        state.Input = "/";
        state.Cursor = 1;

        var lines = ChatTuiRenderer.CommandPaletteLines(state, 100);
        Assert.StartsWith(DiffRender.Ansi.Inverse + "› ", lines[0]);
        Assert.StartsWith("  /status", DiffRender.Ansi.Strip(lines[1]));
        Assert.Contains(DiffRender.Ansi.Bold + "/status", lines[1]);
        Assert.Contains(
            DiffRender.Ansi.Secondary + "show current session configuration",
            lines[1]);

        state.DismissCommandPalette();
        Assert.False(ChatCommandPalette.IsOpen(state));
        Assert.DoesNotContain(
            "› /init",
            ChatTuiRenderer.Render(100, 24, state));
    }

    [Fact]
    public void MovingUpFromLastMovesHighlightBeforeScrollingWindow()
    {
        var state = new ChatTuiState { Input = "/", Cursor = 1 };
        var matches = ChatCommandPalette.Matches("/");
        state.CommandPaletteSelection = matches.Count - 1;

        var bottomLines = ChatTuiRenderer.CommandPaletteLines(state, 100);
        var bottomRow = bottomLines.FindIndex(line =>
            line.StartsWith(DiffRender.Ansi.Inverse + "› ", StringComparison.Ordinal));
        Assert.Equal(ChatCommandPalette.MaxVisible - 1, bottomRow);

        state.CommandPaletteSelection--;
        var upwardLines = ChatTuiRenderer.CommandPaletteLines(state, 100);
        var upwardRow = upwardLines.FindIndex(line =>
            line.StartsWith(DiffRender.Ansi.Inverse + "› ", StringComparison.Ordinal));
        Assert.Equal(bottomRow - 1, upwardRow);
    }
    [Fact]
    public void MovingDownScrollsAtSecondToLastVisibleRow()
    {
        var state = new ChatTuiState { Input = "/", Cursor = 1 };
        var lines = ChatTuiRenderer.CommandPaletteLines(state, 100);
        for (var step = 0; step < ChatCommandPalette.MaxVisible; step++)
        {
            ChatCommandPalette.MoveSelection(state, 1);
            lines = ChatTuiRenderer.CommandPaletteLines(state, 100);
        }

        var selectedRow = lines.FindIndex(line =>
            line.StartsWith(DiffRender.Ansi.Inverse + "› ", StringComparison.Ordinal));
        Assert.Equal(ChatCommandPalette.MaxVisible - 2, selectedRow);
        Assert.Equal(2, state.CommandPaletteScroll);
    }
    [Fact]
    public void ClickingACommandPaletteItemFillsTheComposer()
    {
        var state = new ChatTuiState { Input = "/", Cursor = 1 };
        var lines = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(80, 24, state))
            .Split('\n');
        var row = Enumerable.Range(0, lines.Length)
            .Last(index => lines[index].Contains("/model", StringComparison.Ordinal));

        Assert.True(ChatTuiRenderer.TryClickPopupItemAt(state, 80, 24, row));
        Assert.Equal("/model ", state.Input);
        Assert.Equal(state.Input.Length, state.Cursor);
        Assert.False(ChatCommandPalette.IsOpen(state));
    }
    [Fact]
    public void UpDownControlPAndControlNCycleSelection()
    {
        var state = new ChatTuiState { Input = "/", Cursor = 1 };

        Assert.Equal(
            ChatKeyAction.CommandPaletteDown,
            ChatKeyMapper.Map(Key(ConsoleKey.DownArrow), state));
        Assert.Equal(
            ChatKeyAction.CommandPaletteDown,
            ChatKeyMapper.Map(Key(ConsoleKey.N, '\u000e', control: true), state));
        Assert.Equal(
            ChatKeyAction.CommandPaletteUp,
            ChatKeyMapper.Map(Key(ConsoleKey.P, '\u0010', control: true), state));

        ChatCommandPalette.MoveSelection(state, -1);
        Assert.Equal(
            "/fork",
            ChatCommandPalette.Selected(state)?.Name);

        ChatCommandPalette.MoveSelection(state, 1);
        Assert.Equal(
            "/init",
            ChatCommandPalette.Selected(state)?.Name);
    }

    [Fact]
    public void EscapeKeepsInputAndOnlyTokenChangesReopen()
    {
        var state = new ChatTuiState { Input = "/", Cursor = 1 };

        Assert.Equal(
            ChatKeyAction.CommandPaletteClose,
            ChatKeyMapper.Map(Key(ConsoleKey.Escape), state));

        state.DismissCommandPalette();
        Assert.False(ChatCommandPalette.IsOpen(state));
        Assert.Equal("/", state.Input);

        state.MoveCursorLeft();
        state.MoveCursorRight();
        Assert.False(ChatCommandPalette.IsOpen(state));

        state.Insert('s');
        Assert.True(ChatCommandPalette.IsOpen(state));
    }

    [Fact]
    public void ExactMatchWinsAndHiddenAliasesResolve()
    {
        Assert.Equal(
            "/resume",
            ChatCommandPalette.Matches("/history")[0].Name);
        Assert.Equal(
            ChatCommandKind.Resume,
            ChatCommandPalette.FindExact("/history")?.Kind);
        Assert.Equal(
            ChatCommandKind.Copy,
            ChatCommandPalette.FindExact("/copy")?.Kind);
        Assert.Null(ChatCommandPalette.FindExact("/settings now"));
        Assert.DoesNotContain(
            ChatCommandPalette.Matches("/"),
            command => command.Name == "/copy");
    }

    [Fact]
    public void EnterAcceptsSelectedCommandInsteadOfSending()
    {
        var state = new ChatTuiState { Input = "/hi", Cursor = 3 };

        var action = ChatKeyMapper.Map(Key(ConsoleKey.Enter), state);

        Assert.Equal(ChatKeyAction.CommandPaletteAccept, action);
        Assert.Equal("/resume", ChatCommandPalette.Selected(state)?.Name);
    }

    [Fact]
    public void UnknownSlashInputIsNeverAPrompt()
    {
        Assert.True(ChatTuiRunner.IsPrompt("fix it"));
        Assert.False(ChatTuiRunner.IsPrompt("/nope"));
        Assert.Null(ChatCommandPalette.FindExact("/nope"));
        Assert.False(ChatCommandPalette.IsOpen(new ChatTuiState
        {
            Input = "/nope",
            Cursor = 5
        }));
    }

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char character = '\0',
        bool control = false) =>
        new(character, key, shift: false, alt: false, control);
}

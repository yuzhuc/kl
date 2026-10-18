using Xunit;

namespace Kl.Cli.Tests;

public sealed class ChatCommandPaletteTests
{
    [Fact]
    public void TypingSlashOpensPaletteWithCommands()
    {
        var state = new ChatTuiState();

        state.Insert('/');

        Assert.True(ChatCommandPalette.IsOpen(state));
        Assert.Equal(6, ChatCommandPalette.Matches(state.Input).Count);
        Assert.Equal("/settings", ChatCommandPalette.Selected(state)?.Name);

        var render = ChatTuiRenderer.Render(100, 24, state);
        Assert.Contains("> /settings  Open settings", render);
        Assert.Contains(" /设置  打开设置", render);
        Assert.Contains(" /history  Open conversation history", render);
        Assert.Contains(" /new  Start a new session", render);
        Assert.Contains(" /help  Show keyboard shortcuts", render);
        Assert.Contains(" /exit  Exit KL", render);

        var lines = render.Split('\n');
        Assert.Equal(24, lines.Length);
        var exitIndex = Array.FindIndex(lines, line => line.Contains("/exit"));
        var inputIndex = Array.FindIndex(lines, line => line.Contains("> /|"));
        Assert.True(exitIndex >= 0);
        Assert.True(inputIndex >= 0);
        Assert.True(exitIndex < inputIndex);
    }

    [Fact]
    public void PrefixSeOnlyShowsSettingsAliases()
    {
        var state = new ChatTuiState { Input = "/se", Cursor = 3 };

        var names = ChatCommandPalette.Matches(state.Input)
            .Select(command => command.Name);

        Assert.Equal(new[] { "/settings", "/设置" }, names);
        Assert.Equal("/settings", ChatCommandPalette.Selected(state)?.Name);
    }

    [Fact]
    public void UpAndDownChangePaletteSelection()
    {
        var state = new ChatTuiState { Input = "/", Cursor = 1 };

        Assert.Equal(
            ChatKeyAction.CommandPaletteDown,
            ChatKeyMapper.Map(Key(ConsoleKey.DownArrow), state));
        ChatCommandPalette.MoveSelection(state, 1);
        Assert.Equal("/设置", ChatCommandPalette.Selected(state)?.Name);

        Assert.Equal(
            ChatKeyAction.CommandPaletteUp,
            ChatKeyMapper.Map(Key(ConsoleKey.UpArrow), state));
        ChatCommandPalette.MoveSelection(state, -1);
        Assert.Equal("/settings", ChatCommandPalette.Selected(state)?.Name);
    }

    [Fact]
    public void EnterAcceptsSelectedCommandInsteadOfSending()
    {
        var state = new ChatTuiState { Input = "/hi", Cursor = 3 };

        var action = ChatKeyMapper.Map(Key(ConsoleKey.Enter), state);

        Assert.Equal(ChatKeyAction.CommandPaletteAccept, action);
        Assert.NotEqual(ChatKeyAction.Send, action);
        Assert.Equal("/history", ChatCommandPalette.Selected(state)?.Name);
    }

    [Fact]
    public void EscapeClosesPalette()
    {
        var state = new ChatTuiState { Input = "/", Cursor = 1 };

        Assert.Equal(
            ChatKeyAction.CommandPaletteClose,
            ChatKeyMapper.Map(Key(ConsoleKey.Escape), state));

        state.DismissCommandPalette();

        Assert.False(ChatCommandPalette.IsOpen(state));
        Assert.DoesNotContain(
            "Open settings",
            ChatTuiRenderer.Render(100, 24, state));
    }

    [Fact]
    public void ExactSettingsAliasesRemainDirectlyExecutable()
    {
        Assert.Equal(
            ChatCommandKind.Settings,
            ChatCommandPalette.FindExact("/settings")?.Kind);
        Assert.Equal(
            ChatCommandKind.Settings,
            ChatCommandPalette.FindExact(" /设置 ")?.Kind);
        Assert.Null(ChatCommandPalette.FindExact("/settings now"));
    }

    [Fact]
    public void ExactAliasSelectsItsOwnRow()
    {
        var state = new ChatTuiState { Input = "/设置", Cursor = 3 };

        Assert.Equal("/设置", ChatCommandPalette.Selected(state)?.Name);
    }

    [Fact]
    public void PaletteStillOpensWithoutProviderOrSession()
    {
        var state = new ChatTuiState();

        state.Insert('/');

        Assert.Equal(string.Empty, state.Provider);
        Assert.Equal(string.Empty, state.SessionId);
        Assert.True(ChatCommandPalette.IsOpen(state));
        Assert.NotNull(ChatCommandPalette.Selected(state));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);
}

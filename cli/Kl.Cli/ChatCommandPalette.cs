namespace Kl.Cli;

public enum ChatCommandKind
{
    Settings,
    History,
    New,
    Help,
    Exit
}

public sealed record ChatCommandDefinition(
    string Name,
    ChatCommandKind Kind,
    string Description,
    IReadOnlyList<string> Aliases);

public static class ChatCommandPalette
{
    public const int MaxVisible = 6;

    private static readonly IReadOnlyList<ChatCommandDefinition> All =
    [
        new(
            "/settings",
            ChatCommandKind.Settings,
            "Open settings",
            ["/settings", "/设置"]),
        new(
            "/设置",
            ChatCommandKind.Settings,
            "打开设置",
            ["/设置", "/settings"]),
        new(
            "/history",
            ChatCommandKind.History,
            "Open conversation history",
            ["/history"]),
        new(
            "/new",
            ChatCommandKind.New,
            "Start a new session",
            ["/new"]),
        new(
            "/help",
            ChatCommandKind.Help,
            "Show keyboard shortcuts",
            ["/help"]),
        new(
            "/exit",
            ChatCommandKind.Exit,
            "Exit KL",
            ["/exit"])
    ];

    public static bool IsQuery(string input) =>
        input.StartsWith('/') && !input.Any(char.IsWhiteSpace);

    public static bool IsOpen(ChatTuiState state) =>
        state.Screen == ChatScreen.Chat
        && !state.CommandPaletteDismissed
        && IsQuery(state.Input);

    public static IReadOnlyList<ChatCommandDefinition> Matches(string input)
    {
        if (!IsQuery(input))
        {
            return [];
        }

        var exact = All
            .Where(command => command.Name.Equals(
                input,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        exact.AddRange(All
            .Where(command => !exact.Contains(command))
            .Where(command => command.Aliases.Any(alias => alias.StartsWith(
                input,
                StringComparison.OrdinalIgnoreCase))));
        return exact.Take(MaxVisible).ToList();
    }

    public static ChatCommandDefinition? Selected(ChatTuiState state)
    {
        if (!IsOpen(state))
        {
            return null;
        }

        var matches = Matches(state.Input);
        return matches.Count == 0
            ? null
            : matches[Math.Clamp(state.CommandPaletteSelection, 0, matches.Count - 1)];
    }

    public static void MoveSelection(ChatTuiState state, int delta)
    {
        if (!IsOpen(state))
        {
            return;
        }

        var count = Matches(state.Input).Count;
        state.CommandPaletteSelection = count == 0
            ? 0
            : Math.Clamp(state.CommandPaletteSelection + delta, 0, count - 1);
    }

    public static ChatCommandDefinition? FindExact(string input)
    {
        var command = input.Trim();
        return All.FirstOrDefault(definition => definition.Aliases.Any(alias =>
            alias.Equals(command, StringComparison.OrdinalIgnoreCase)));
    }
}

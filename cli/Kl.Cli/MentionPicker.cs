namespace Kl.Cli;

public enum MentionFilter
{
    AllResults,
    FilesystemOnly
}
public sealed class MentionPickerState
{
    public bool Open { get; set; }
    public bool Dismissed { get; set; }
    public string DismissedToken { get; set; } = string.Empty;
    public int Start { get; set; }
    public string Query { get; set; } = string.Empty;
    public int Selection { get; set; }
    public int Scroll { get; set; }
    public MentionFilter Filter { get; set; } = MentionFilter.AllResults;
    public bool Loaded { get; set; }
    public List<string> Files { get; set; } = [];
    public List<string> Matches { get; set; } = [];
}

public static class MentionPicker
{
    public const int MaxVisible = 8;
    public const int MaxFiles = 5000;

    private static readonly string[] IgnoredDirectories =
    [
        ".git",
        "target",
        "node_modules",
        "bin",
        "obj",
        "dist",
        ".kl"
    ];

    public static bool IsOpen(ChatTuiState state) =>
        state.Screen == ChatScreen.Chat && state.MentionPicker.Open;

    public static void Update(ChatTuiState state)
    {
        var picker = state.MentionPicker;
        if (state.Screen != ChatScreen.Chat)
        {
            Close(state);
            return;
        }

        var cursor = Math.Clamp(state.Cursor, 0, state.Input.Length);
        var start = cursor;
        while (start > 0 && !char.IsWhiteSpace(state.Input[start - 1]))
        {
            start--;
        }

        var token = state.Input[start..cursor];
        if (!token.StartsWith('@'))
        {
            Close(state);
            return;
        }

        if (picker.Dismissed && picker.DismissedToken == token)
        {
            return;
        }

        picker.Dismissed = false;
        picker.DismissedToken = string.Empty;
        picker.Open = true;
        picker.Start = start;
        var previousQuery = picker.Query;
        picker.Query = token[1..];
        if (!previousQuery.Equals(picker.Query, StringComparison.Ordinal))
        {
            picker.Selection = 0;
            picker.Scroll = 0;
        }

        if (!picker.Loaded)
        {
            picker.Files = LoadFiles(state.Workspace);
            picker.Loaded = true;
        }

        var query = picker.Query;
        picker.Matches = picker.Files
            .Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        picker.Selection = picker.Matches.Count == 0
            ? 0
            : Math.Clamp(picker.Selection, 0, picker.Matches.Count - 1);
    }

    public static void Dismiss(ChatTuiState state)
    {
        var picker = state.MentionPicker;
        picker.Dismissed = true;
        picker.DismissedToken = CurrentToken(state);
        picker.Open = false;
    }

    public static void MoveSelection(ChatTuiState state, int delta)
    {
        var picker = state.MentionPicker;
        var count = picker.Matches.Count;
        if (!IsOpen(state) || count == 0)
        {
            return;
        }

        picker.Selection = (picker.Selection + delta + count) % count;
    }

    public static void MoveFilter(ChatTuiState state, int delta)
    {
        if (!IsOpen(state))
        {
            return;
        }

        var values = Enum.GetValues<MentionFilter>();
        var index = Array.IndexOf(values, state.MentionPicker.Filter);
        state.MentionPicker.Filter = values[(index + delta + values.Length) % values.Length];
        state.MentionPicker.Selection = 0;
        state.MentionPicker.Scroll = 0;
    }
    public static string? Selected(ChatTuiState state)
    {
        var picker = state.MentionPicker;
        return IsOpen(state) && picker.Matches.Count > 0
            ? picker.Matches[Math.Clamp(picker.Selection, 0, picker.Matches.Count - 1)]
            : null;
    }

    public static bool Accept(ChatTuiState state)
    {
        if (Selected(state) is not { } path)
        {
            return false;
        }

        var picker = state.MentionPicker;
        var cursor = Math.Clamp(state.Cursor, 0, state.Input.Length);
        var end = cursor;
        while (end < state.Input.Length && !char.IsWhiteSpace(state.Input[end]))
        {
            end++;
        }

        var replacement = "@" + path + " ";
        state.Input = state.Input[..picker.Start] + replacement + state.Input[end..];
        state.Cursor = picker.Start + replacement.Length;
        Close(state);
        return true;
    }

    public static List<string> LoadFiles(string workspace)
    {
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return files;
        }

        string root;
        try
        {
            root = Path.GetFullPath(workspace);
        }
        catch (Exception error) when (error is IOException or ArgumentException
            or NotSupportedException)
        {
            return files;
        }
        if (!Directory.Exists(root))
        {
            return files;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                    if (files.Count >= MaxFiles)
                    {
                        break;
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    pending.Push(child);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Keep the picker usable when a directory disappears or is protected.
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static string CurrentToken(ChatTuiState state)
    {
        var cursor = Math.Clamp(state.Cursor, 0, state.Input.Length);
        var start = cursor;
        while (start > 0 && !char.IsWhiteSpace(state.Input[start - 1]))
        {
            start--;
        }
        return state.Input[start..cursor];
    }

    private static void Close(ChatTuiState state)
    {
        state.MentionPicker.Open = false;
        state.MentionPicker.Dismissed = false;
        state.MentionPicker.DismissedToken = string.Empty;
        state.MentionPicker.Scroll = 0;
    }
}

namespace Kl.Gui.Services;

/// <summary>One active "/" or "@" token in the composer, from <see cref="Start"/> to the caret.</summary>
public readonly record struct ComposerToken(char Trigger, string Query, int Start);

/// <summary>
/// Pure rules behind the composer suggestion menus, kept separate from the window
/// so they can be tested without an Avalonia host.
/// </summary>
public static class ComposerSuggestionRules
{
    /// <summary>
    /// Finds a "/" or "@" token that ends at the caret. The trigger must start a
    /// token (beginning of the text or after whitespace), which keeps mail-like
    /// "a@b" and paths from opening the file menu.
    /// </summary>
    public static bool TryGetToken(string? text, int caret, out ComposerToken token)
    {
        token = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        caret = Math.Clamp(caret, 0, text.Length);
        if (caret == 0)
        {
            return false;
        }

        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        if (start >= caret)
        {
            return false;
        }

        var trigger = text[start];
        if (trigger != '/' && trigger != '@')
        {
            return false;
        }

        token = new ComposerToken(trigger, text[(start + 1)..caret], start);
        return true;
    }

    /// <summary>Filters suggestion candidates: prefix matches first, then contains.</summary>
    public static IReadOnlyList<string> Filter(
        IEnumerable<string> candidates,
        string? query,
        int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<string>();
        }

        var needle = query?.Trim() ?? string.Empty;
        return candidates
            .Where(candidate => needle.Length == 0
                || candidate.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => needle.Length > 0
                && candidate.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.Length)
            .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Replaces the token with <paramref name="replacement"/> and returns the new
    /// text plus where the caret should land.
    /// </summary>
    public static (string Text, int Caret) ReplaceToken(
        string text,
        int caret,
        ComposerToken token,
        string replacement)
    {
        var head = text[..token.Start];
        // Do not double the space when the insertion already ends with one and the
        // caret sits right before an existing space.
        var tailStart = caret;
        if (replacement.EndsWith(' ') && tailStart < text.Length && text[tailStart] == ' ')
        {
            tailStart++;
        }

        var tail = text[tailStart..];
        return (head + replacement + tail, head.Length + replacement.Length);
    }

    /// <summary>Directory names that never make sense to browse in the file menu.</summary>
    public static readonly IReadOnlySet<string> SkippedDirectories = new HashSet<string>(
        new[]
        {
            ".git", ".vs", ".idea", ".venv", "venv", "__pycache__",
            "node_modules", "target", "bin", "obj", "dist", "build", ".next", ".turbo"
        },
        StringComparer.OrdinalIgnoreCase);
}

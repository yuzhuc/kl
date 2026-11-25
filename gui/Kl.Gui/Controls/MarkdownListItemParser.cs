using System.Text.RegularExpressions;

namespace Kl.Gui.Controls;

public readonly record struct MarkdownListItem(int Level, string Marker, string Text, bool Numbered);

public static class MarkdownListItemParser
{
    private static readonly Regex OrderedPattern = new(@"^(\d{1,3})\.\s+(.*)$", RegexOptions.Compiled);

    public static bool TryParse(string line, out MarkdownListItem item)
    {
        item = default;
        var indent = line.Length - line.TrimStart(' ', '\t').Length;
        var body = line[indent..];
        var level = Math.Min(indent / 2, 2);
        if (body.StartsWith("- ", StringComparison.Ordinal)
            || body.StartsWith("* ", StringComparison.Ordinal))
        {
            item = new MarkdownListItem(level, level == 0 ? "•" : "◦", body[2..], false);
            return true;
        }
        var match = OrderedPattern.Match(body);
        if (!match.Success)
        {
            return false;
        }
        item = new MarkdownListItem(level, match.Groups[1].Value + ".", match.Groups[2].Value, true);
        return true;
    }
}

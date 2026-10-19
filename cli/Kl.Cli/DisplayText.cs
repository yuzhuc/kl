using System.Globalization;
using System.Text;

namespace Kl.Cli;

/// <summary>
/// Terminal display-width helpers for text that mixes ASCII with wide
/// (CJK / full-width) characters.
/// </summary>
public static class DisplayText
{
    public static int Width(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }
        return width;
    }

    public static string TruncateToWidth(string? text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var used = 0;
        foreach (var element in Elements(text))
        {
            var width = ElementWidth(element);
            if (used + width > maxWidth)
            {
                break;
            }
            builder.Append(element);
            used += width;
        }
        return builder.ToString();
    }

    public static string TailToWidth(string? text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }

        var elements = Elements(text);
        var start = elements.Count;
        var used = 0;
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            var width = ElementWidth(elements[index]);
            if (used + width > maxWidth)
            {
                break;
            }
            used += width;
            start = index;
        }
        return string.Concat(elements[start..]);
    }

    public static string Ellipsize(string? text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }
        if (Width(text) <= maxWidth)
        {
            return text;
        }
        if (maxWidth == 1)
        {
            return "…";
        }
        return TruncateToWidth(text, maxWidth - 1) + "…";
    }

    public static string SliceToWidth(string? text, int start, int end)
    {
        if (string.IsNullOrEmpty(text) || end <= start)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var column = 0;
        foreach (var element in Elements(text))
        {
            var width = ElementWidth(element);
            if (column >= start && column < end)
            {
                builder.Append(element);
            }
            column += width;
            if (column >= end)
            {
                break;
            }
        }
        return builder.ToString();
    }

    public static string PadToWidth(string? text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var value = TruncateToWidth(text, width);
        var padding = width - Width(value);
        return padding <= 0 ? value : value + new string(' ', padding);
    }

    public static IReadOnlyList<string> WrapToWidth(string? text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (var paragraph in normalized.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var elements = Elements(paragraph);
            var start = 0;
            while (start < elements.Count)
            {
                var used = 0;
                var end = start;
                var lastSpace = -1;
                while (end < elements.Count)
                {
                    var widthOfElement = ElementWidth(elements[end]);
                    if (used + widthOfElement > width)
                    {
                        break;
                    }
                    if (elements[end] == " ")
                    {
                        lastSpace = end;
                    }
                    used += widthOfElement;
                    end++;
                }

                if (end == start)
                {
                    // A single grapheme is wider than the target width; keep it
                    // intact rather than splitting a surrogate pair or cluster.
                    end++;
                }
                else if (end < elements.Count && lastSpace > start)
                {
                    end = lastSpace;
                }

                lines.Add(string.Concat(elements[start..end]).TrimEnd());
                start = end;
                while (start < elements.Count && elements[start] == " ")
                {
                    start++;
                }
            }
        }
        return lines;
    }

    private static List<string> Elements(string text)
    {
        var elements = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add((string)enumerator.Current);
        }
        return elements;
    }

    private static int ElementWidth(string element)
    {
        var width = 0;
        foreach (var rune in element.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }
        return width;
    }

    private static int RuneWidth(Rune rune)
    {
        if (rune.Value is < 0x20 or 0x7F)
        {
            return 0;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark)
        {
            return 0;
        }

        return IsWide(rune.Value) ? 2 : 1;
    }

    private static bool IsWide(int value) =>
        value is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F1E6 and <= 0x1F1FF
            or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x1FA70 and <= 0x1FAFF
            or >= 0x20000 and <= 0x2FFFD
            or >= 0x30000 and <= 0x3FFFD;
}
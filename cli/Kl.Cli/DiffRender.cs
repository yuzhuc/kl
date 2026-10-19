using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kl.Cli;

/// <summary>ANSI-safe width helpers and diff / tool-output rendering.</summary>
public static class DiffRender
{
    public const int ToolOutputMaxLines = 20;

    private static readonly Regex SecretValue = new(
        @"(?i)\b(api[_-]?key|token|password|secret|authorization)\b\s*[:=]\s*([^\s,;]+)",
        RegexOptions.Compiled);

    private static readonly Regex BearerToken = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled);

    private static readonly Regex SkToken = new(
        @"\bsk-[A-Za-z0-9_-]{6,}\b",
        RegexOptions.Compiled);

    public static string RedactSensitive(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = SecretValue.Replace(text, "$1=***");
        redacted = BearerToken.Replace(redacted, "Bearer ***");
        return SkToken.Replace(redacted, "sk-***");
    }

    public static IReadOnlyList<string> TruncateToolOutput(
        string? text,
        int maxLines,
        UiLanguage language)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count <= maxLines)
        {
            return lines;
        }

        var omitted = lines.Count - maxLines;
        var visible = lines.Take(maxLines).ToList();
        visible.Add(ChatTexts.OmittedLines(language, omitted));
        return visible;
    }

    public static string ColorizeDiffLine(string line)
    {
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return Ansi.BoldCyan + line + Ansi.Reset;
        }
        if (line.StartsWith('+'))
        {
            return Ansi.Green + line + Ansi.Reset;
        }
        if (line.StartsWith('-'))
        {
            return Ansi.Red + line + Ansi.Reset;
        }
        return line;
    }

    /// <summary>ANSI helpers. Width always ignores escape sequences.</summary>
    public static class Ansi
    {
        public const string Reset = "\u001b[0m";
        // Codex secondary text: 60% foreground/background blend instead of terminal dim.
        public const string Secondary = "\u001b[38;5;244m";
        public const string Dim = Secondary;
        public const string Green = "\u001b[32m";
        public const string Red = "\u001b[31m";
        public const string Yellow = "\u001b[33m";
        public const string BoldGreen = "\u001b[1;32m";
        public const string BoldRed = "\u001b[1;31m";
        public const string BoldCyan = "\u001b[1;36m";
        public const string Cyan = "\u001b[36m";
        public const string Inverse = "\u001b[7m";
        public const string Bold = "\u001b[1m";
        // Codex TUI palette.
        public const string Accent = "\u001b[38;2;99;168;248m";
        public const string BoldAccent = "\u001b[1;38;2;99;168;248m";
        public const string ActiveTab = "\u001b[1;100m\u001b[97m";
        public const string Selection = "\u001b[1;48;2;99;168;248m\u001b[38;2;0;0;46m";
        public const string UserMessage = "\u001b[48;5;236m";
        public const string Composer = "\u001b[48;5;236m\u001b[38;5;252m";
        public const string Warning = "\u001b[38;5;179m";

        public static int VisibleWidth(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return DisplayText.Width(Strip(text));
        }

        public static string Strip(string? text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\u001b') < 0)
            {
                return text ?? string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            for (var index = 0; index < text.Length;)
            {
                if (text[index] == '\u001b'
                    && index + 1 < text.Length
                    && text[index + 1] == '[')
                {
                    index += 2;
                    while (index < text.Length
                        && (text[index] < '@' || text[index] > '~'))
                    {
                        index++;
                    }
                    if (index < text.Length)
                    {
                        index++;
                    }
                    continue;
                }

                builder.Append(text[index]);
                index++;
            }
            return builder.ToString();
        }

        public static string TruncateVisible(string? text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                return string.Empty;
            }

            if (VisibleWidth(text) <= maxWidth)
            {
                return text;
            }

            var limited = TruncateExact(text, Math.Max(0, maxWidth - 1));
            return limited + "…";
        }

        public static string Fit(string? text, int width)
        {
            if (width <= 0)
            {
                return string.Empty;
            }

            var value = TruncateVisible(text, width);
            var padding = width - VisibleWidth(value);
            return padding <= 0 ? value : value + new string(' ', padding);
        }

        public static string Colorize(string? text, string color)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(color))
            {
                return text ?? string.Empty;
            }
            return color + text + Reset;
        }

        public static string ApplySelection(string text, int start, int end)
        {
            if (string.IsNullOrEmpty(text) || end <= start)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length + 32);
            var activeStyle = new StringBuilder();
            var column = 0;
            var inSelection = false;
            for (var index = 0; index < text.Length;)
            {
                if (text[index] == '\u001b'
                    && index + 1 < text.Length
                    && text[index + 1] == '[')
                {
                    var sequenceEnd = index + 2;
                    while (sequenceEnd < text.Length
                        && (text[sequenceEnd] < '@' || text[sequenceEnd] > '~'))
                    {
                        sequenceEnd++;
                    }
                    if (sequenceEnd < text.Length)
                    {
                        sequenceEnd++;
                        var sequence = text.Substring(index, sequenceEnd - index);
                        if (sequence is Reset or "\u001b[m")
                        {
                            activeStyle.Clear();
                        }
                        else if (sequence[^1] == 'm')
                        {
                            activeStyle.Append(sequence);
                        }
                        index = sequenceEnd;
                        continue;
                    }
                }

                var element = StringInfo.GetNextTextElement(text, index);
                var width = DisplayText.Width(element);
                var selected = width == 0
                    ? column >= start && column < end
                    : column < end && column + width > start;
                if (selected && !inSelection)
                {
                    builder.Append(Reset).Append(Selection);
                    inSelection = true;
                }
                else if (!selected && inSelection)
                {
                    builder.Append(Reset);
                    inSelection = false;
                }
                if (!selected)
                {
                    builder.Append(activeStyle);
                }
                builder.Append(element);
                column += width;
                index += element.Length;
            }
            return builder.Append(Reset).ToString();
        }

        private static string TruncateExact(string text, int maxWidth)
        {
            if (maxWidth <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            var used = 0;
            var hasAnsi = false;
            for (var index = 0; index < text.Length;)
            {
                if (text[index] == '\u001b'
                    && index + 1 < text.Length
                    && text[index + 1] == '[')
                {
                    var end = index + 2;
                    while (end < text.Length
                        && (text[end] < '@' || text[end] > '~'))
                    {
                        end++;
                    }
                    if (end < text.Length)
                    {
                        end++;
                    }
                    builder.Append(text, index, end - index);
                    hasAnsi = true;
                    index = end;
                    continue;
                }

                var element = StringInfo.GetNextTextElement(text, index);
                var width = DisplayText.Width(element);
                if (used + width > maxWidth)
                {
                    break;
                }
                builder.Append(element);
                used += width;
                index += element.Length;
            }

            if (hasAnsi)
            {
                builder.Append(Reset);
            }
            return builder.ToString();
        }
    }
}

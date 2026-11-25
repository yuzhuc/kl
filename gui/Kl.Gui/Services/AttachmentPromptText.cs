namespace Kl.Gui.Services;

/// <summary>
/// Chat history stores the prompt exactly as the model saw it, including inline
/// attachment blocks. The transcript shows the compact "附件：…" form instead.
/// </summary>
public static class AttachmentPromptText
{
    private const string Open = "<attachment ";
    private const string Close = "</attachment>";

    public static string Collapse(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(Open, StringComparison.Ordinal))
        {
            return text;
        }

        var names = new List<string>();
        var output = new System.Text.StringBuilder(text.Length);
        var position = 0;
        while (true)
        {
            var start = text.IndexOf(Open, position, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Append(text, position, text.Length - position);
                break;
            }

            var end = text.IndexOf(Close, start, StringComparison.Ordinal);
            if (end < 0)
            {
                output.Append(text, position, text.Length - position);
                break;
            }

            output.Append(text, position, start - position);
            var name = ReadAttribute(text[start..end], "name");
            if (name.Length > 0)
            {
                names.Add(name);
            }
            position = end + Close.Length;
        }

        var collapsed = output.ToString().TrimEnd();
        if (names.Count == 0)
        {
            return collapsed;
        }

        var suffix = $"附件：{string.Join("、", names)}"; // i18n-keep
        return collapsed.Length == 0 ? suffix : $"{collapsed}\n\n{suffix}";
    }

    private static string ReadAttribute(string header, string attribute)
    {
        var marker = attribute + "=\"";
        var start = header.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = header.IndexOf('"', start);
        return end < 0 ? string.Empty : header[start..end];
    }
}

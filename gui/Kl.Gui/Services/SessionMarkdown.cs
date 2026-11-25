using System.Text;
using Kl.Gui.Models;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.Services;

/// <summary>Renders a saved session as a portable Markdown transcript.</summary>
public static class SessionMarkdown
{
    public static string Build(string title, string workspace, IEnumerable<StoredMessage> messages)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(string.IsNullOrWhiteSpace(title) ? Loc.T("KL 会话") : title.Trim());
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            builder.Append(Loc.T("- 工作区：`")).Append(workspace.Trim()).AppendLine("`");
        }
        builder.Append(Loc.T("- 导出时间：")).AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"));
        builder.AppendLine();

        foreach (var message in messages)
        {
            var text = message.Text?.Trim() ?? string.Empty;
            switch (message.Kind)
            {
                case "user":
                    builder.AppendLine(Loc.T("## 用户")).AppendLine();
                    builder.AppendLine(AttachmentPromptText.Collapse(text));
                    break;
                case "assistant" when text.Length > 0:
                    builder.AppendLine("## KL").AppendLine();
                    builder.AppendLine(text);
                    break;
                case "error" when text.Length > 0:
                    builder.AppendLine(Loc.T("> 错误：")).AppendLine("> " + text.Replace("\n", "\n> "));
                    break;
                case "tool" when text.Length > 0:
                    builder.AppendLine("<details>");
                    builder.Append(Loc.T("<summary>工具：")).Append(message.Header ?? string.Empty).AppendLine("</summary>");
                    builder.AppendLine();
                    builder.AppendLine("```");
                    builder.AppendLine(text);
                    builder.AppendLine("```");
                    builder.AppendLine("</details>");
                    break;
                default:
                    continue;
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + "\n";
    }
}

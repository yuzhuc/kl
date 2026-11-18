using System.Text;
using Kl.Gui.Models;

namespace Kl.Gui.Services;

/// <summary>Renders a saved session as a portable Markdown transcript.</summary>
public static class SessionMarkdown
{
    public static string Build(string title, string workspace, IEnumerable<StoredMessage> messages)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(string.IsNullOrWhiteSpace(title) ? "KL 会话" : title.Trim());
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            builder.Append("- 工作区：`").Append(workspace.Trim()).AppendLine("`");
        }
        builder.Append("- 导出时间：").AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"));
        builder.AppendLine();

        foreach (var message in messages)
        {
            var text = message.Text?.Trim() ?? string.Empty;
            switch (message.Kind)
            {
                case "user":
                    builder.AppendLine("## 用户").AppendLine();
                    builder.AppendLine(AttachmentPromptText.Collapse(text));
                    break;
                case "assistant" when text.Length > 0:
                    builder.AppendLine("## KL").AppendLine();
                    builder.AppendLine(text);
                    break;
                case "error" when text.Length > 0:
                    builder.AppendLine("> 错误：").AppendLine("> " + text.Replace("\n", "\n> "));
                    break;
                case "tool" when text.Length > 0:
                    builder.AppendLine("<details>");
                    builder.Append("<summary>工具：").Append(message.Header ?? string.Empty).AppendLine("</summary>");
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

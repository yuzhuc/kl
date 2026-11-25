using Kl.Gui.Models;
using Kl.Gui.Services;

namespace Kl.Gui.Tests;

public sealed class SessionMarkdownTests
{
    [Fact]
    public void BuildRendersTheConversationAndToolDetails()
    {
        var markdown = SessionMarkdown.Build(
            "修复登录",
            @"D:\work\app",
            [
                new StoredMessage { Kind = "user", Text = "看一下\n<attachment name=\"log.txt\" path=\"x\">boom</attachment>" },
                new StoredMessage { Kind = "reasoning", Text = "内部思考" },
                new StoredMessage { Kind = "tool", Header = "读取文件", Text = "ok" },
                new StoredMessage { Kind = "assistant", Text = "已修复。" },
                new StoredMessage { Kind = "error", Text = "一行\n两行" }
            ]);

        Assert.Contains("# 修复登录", markdown);
        Assert.Contains(@"- 工作区：`D:\work\app`", markdown);
        Assert.Contains("## 用户", markdown);
        Assert.Contains("附件：log.txt", markdown);
        Assert.DoesNotContain("boom", markdown);
        Assert.DoesNotContain("内部思考", markdown);
        Assert.Contains("<summary>工具：读取文件</summary>", markdown);
        Assert.Contains("## KL", markdown);
        Assert.Contains("已修复。", markdown);
        Assert.Contains("> 错误：", markdown);
        Assert.Contains("> 两行", markdown);
    }
}

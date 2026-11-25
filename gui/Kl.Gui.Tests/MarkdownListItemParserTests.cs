using Kl.Gui.Controls;

namespace Kl.Gui.Tests;

public sealed class MarkdownListItemParserTests
{
    [Fact]
    public void ParsesBulletsByLevel()
    {
        Assert.True(MarkdownListItemParser.TryParse("- 顶层", out var top));
        Assert.Equal(("•", 0, false), (top.Marker, top.Level, top.Numbered));

        Assert.True(MarkdownListItemParser.TryParse("    - 嵌套", out var nested));
        Assert.Equal(("◦", 2, false), (nested.Marker, nested.Level, nested.Numbered));
    }

    [Fact]
    public void ParsesOrderedItemsKeepingTheNumber()
    {
        Assert.True(MarkdownListItemParser.TryParse("12. GUI 设置页只走协议", out var item));
        Assert.Equal(("12.", "GUI 设置页只走协议", true), (item.Marker, item.Text, item.Numbered));
    }

    [Fact]
    public void RejectsPlainLinesAndBareDashes()
    {
        Assert.False(MarkdownListItemParser.TryParse("普通段落", out _));
        Assert.False(MarkdownListItemParser.TryParse("-", out _));
        Assert.False(MarkdownListItemParser.TryParse("1.没有空格", out _));
    }
}

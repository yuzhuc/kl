using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Kl.Gui.Controls;

namespace Kl.Gui.Tests;

public sealed class MarkdownTextBlockTests
{
    [Fact]
    public void RendersOrderedNestedAndBulletRows()
    {
        var block = new MarkdownTextBlock { Text = "12. 顶层\n  - 嵌套\n- 普通" };
        typeof(MarkdownTextBlock)
            .GetMethod("Rebuild", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(block, null);

        var list = Assert.IsType<StackPanel>(Assert.Single(((StackPanel)block.Content!).Children));
        var rows = list.Children.Cast<Grid>().ToList();

        Assert.Equal(["12.", "◦", "•"], rows.Select(Marker).ToList());
        Assert.True(Gutter(rows[1]) > Gutter(rows[0]));
    }

    [Fact]
    public void JoinsListContinuationLinesIntoTheItem()
    {
        var block = new MarkdownTextBlock { Text = "1. 第一行\n第二行" };
        typeof(MarkdownTextBlock)
            .GetMethod("Rebuild", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(block, null);

        var list = Assert.IsType<StackPanel>(Assert.Single(((StackPanel)block.Content!).Children));
        var row = Assert.IsType<Grid>(Assert.Single(list.Children));
        var text = Assert.IsType<SelectableTextBlock>(row.Children[1]);
        Assert.Equal("第一行\n第二行", string.Concat(text.Inlines!.Cast<Run>().Select(run => run.Text)));
    }

    private static string Marker(Grid row) =>
        Assert.IsType<TextBlock>(row.Children[0]).Text!;

    private static double Gutter(Grid row) =>
        row.ColumnDefinitions[0].Width.Value;
}

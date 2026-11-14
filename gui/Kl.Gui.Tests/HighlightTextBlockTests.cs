using Avalonia.Controls.Documents;
using Kl.Gui.Controls;

namespace Kl.Gui.Tests;

public sealed class HighlightTextBlockTests
{
    [Fact]
    public void MarksOnlyCaseInsensitiveMatches()
    {
        var block = new HighlightTextBlock
        {
            Text = "alpha class beta CLASS",
            HighlightQuery = "class"
        };

        var runs = block.Inlines!
            .Cast<Run>()
            .Select(run => (run.Text, Hit: run.Background is not null))
            .ToList();

        Assert.Equal(
            [("alpha ", false), ("class", true), (" beta ", false), ("CLASS", true)],
            runs);
        Assert.Equal("alpha class beta CLASS", block.Text);
    }

    [Fact]
    public void KeepsPlainTextWhenNothingMatches()
    {
        var block = new HighlightTextBlock { Text = "no hits", HighlightQuery = "zzz" };

        Assert.Equal("no hits", Assert.Single(block.Inlines!.Cast<Run>()).Text);
    }
}

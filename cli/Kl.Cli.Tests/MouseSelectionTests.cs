using Xunit;

namespace Kl.Cli.Tests;

public sealed class MouseSelectionTests
{
    [Fact]
    public void SingleLineSelectionUsesInclusiveCellBounds()
    {
        var selection = Select(0, 0, 0, 4);

        Assert.Equal("hello", selection.Text(["hello world"]));
    }

    [Fact]
    public void MultiLineSelectionPreservesNewlines()
    {
        var selection = Select(0, 1, 2, 2);

        Assert.Equal("ne\ntwo\nthr", selection.Text(["one", "two", "three"]));
    }

    [Fact]
    public void ReverseDragSelectsTheSameRange()
    {
        var selection = Select(1, 2, 0, 0);

        Assert.Equal("abc\ndef", selection.Text(["abc", "def"]));
    }

    [Fact]
    public void EmptySelectionDoesNotProduceText()
    {
        var selection = new MouseSelection();
        selection.Begin(0, 2);
        selection.End(0, 2);

        Assert.False(selection.HasSelection);
        Assert.Equal(string.Empty, selection.Text(["hello"]));
    }

    [Fact]
    public void SelectionClipsRowsAndColumnsToAvailableText()
    {
        var selection = Select(0, 1, 9, 9);

        Assert.Equal("bc\nde", selection.Text(["abc", "de"]));
    }

    [Fact]
    public void CopiedTextExcludesAnsiSequences()
    {
        var selection = Select(0, 0, 0, 2);

        var text = selection.Text(["\u001b[31mred\u001b[0m blue"]);

        Assert.Equal("red", text);
        Assert.False(text.Contains('\u001b'));
    }

    [Fact]
    public void HighlightPreservesPlainTextAndUsesSelectionBackground()
    {
        var line = "\u001b[31mred\u001b[0m blue";
        var highlighted = DiffRender.Ansi.ApplySelection(line, 0, 3);

        Assert.Equal("red blue", DiffRender.Ansi.Strip(highlighted));
        Assert.Contains(DiffRender.Ansi.Selection, highlighted);
    }

    [Fact]
    public void SelectionDoesNotLeakColorPastAnsiReset()
    {
        var line = "plain \u001b[34mblue\u001b[0m tail";
        var highlighted = DiffRender.Ansi.ApplySelection(line, 0, 5);

        Assert.Contains("e\u001b[0m tail", highlighted);
        Assert.DoesNotContain("\u001b[34m tail", highlighted);
        Assert.EndsWith(DiffRender.Ansi.Reset, highlighted);
    }
    [Fact]
    public void ViewportMapsOnlyTranscriptRowsAndClampsDrag()
    {
        var viewport = new TranscriptViewport(3, 10, ["one", "two"]);
        var point = default(SelectionPoint);

        Assert.False(viewport.TryMap(0, 2, out _));
        Assert.True(viewport.TryMap(9, 3, out point));
        Assert.Equal(new SelectionPoint(10, 3), point);
        Assert.True(viewport.TryMapClamped(99, 99, out point));
        Assert.Equal(new SelectionPoint(11, 3), point);
    }

    private static MouseSelection Select(
        int startRow,
        int startColumn,
        int endRow,
        int endColumn)
    {
        var selection = new MouseSelection();
        selection.Begin(startRow, startColumn);
        selection.Extend(endRow, endColumn);
        selection.End(endRow, endColumn);
        return selection;
    }
}

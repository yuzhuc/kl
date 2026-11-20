using Kl.Gui.Controls;
using Xunit;

namespace Kl.Gui.Tests;

public sealed class TerminalBufferTests
{
    [Fact]
    public void PrintsAndWrapsAtColumnLimit()
    {
        var buffer = new TerminalBuffer(5, 2);
        buffer.Feed("abcdef");

        Assert.Equal("abcde", TextOf(buffer.Lines[0]));
        Assert.StartsWith("f", TextOf(buffer.Lines[1]));
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void CarriageReturnOverwritesTheLine()
    {
        var buffer = new TerminalBuffer(8, 2);
        buffer.Feed("abc\rX");

        Assert.Equal("Xbc", TextOf(buffer.Lines[0]).TrimEnd());
    }

    [Fact]
    public void SgrSetsAndResetsForeground()
    {
        var buffer = new TerminalBuffer(8, 2);
        buffer.Feed("\x1b[31mR\x1b[0mN");

        Assert.Equal(TerminalColorKind.Indexed, buffer.Lines[0].Cells[0].Foreground.Kind);
        Assert.Equal(1, buffer.Lines[0].Cells[0].Foreground.Index);
        Assert.Equal(TerminalColorKind.Default, buffer.Lines[0].Cells[1].Foreground.Kind);
    }

    [Fact]
    public void CursorPositionAndEraseLine()
    {
        var buffer = new TerminalBuffer(8, 2);
        buffer.Feed("abcdef\x1b[1;3H\x1b[K");

        Assert.Equal("ab", TextOf(buffer.Lines[0]).TrimEnd());
    }

    [Fact]
    public void WideCharactersOccupyTwoCells()
    {
        var buffer = new TerminalBuffer(8, 2);
        buffer.Feed("中A");

        Assert.Equal("中", buffer.Lines[0].Cells[0].Text);
        Assert.Equal(2, buffer.Lines[0].Cells[0].Width);
        Assert.Equal(0, buffer.Lines[0].Cells[1].Width);
        Assert.Equal("A", buffer.Lines[0].Cells[2].Text);
    }

    [Fact]
    public void SelectionReturnsLineText()
    {
        var buffer = new TerminalBuffer(8, 2);
        buffer.Feed("hello\r\nworld");
        buffer.SetSelection(0, 0, 1, 4);

        Assert.Equal("hello\nworld", buffer.GetSelectedText());
    }

    [Fact]
    public void AlternateScreenRestoresMainContent()
    {
        var buffer = new TerminalBuffer(8, 2);
        buffer.Feed("main");
        buffer.Feed("\x1b[?1049h");
        buffer.Feed("alt");
        buffer.Feed("\x1b[?1049l");

        Assert.Contains("main", buffer.GetAllText());
    }

    [Fact]
    public void ResizeKeepsExistingText()
    {
        var buffer = new TerminalBuffer(8, 2);
        buffer.Feed("hello");
        buffer.Resize(12, 3);

        Assert.Equal(12, buffer.Columns);
        Assert.Equal(3, buffer.Rows);
        Assert.Equal("hello", TextOf(buffer.Lines[0]).TrimEnd());
    }

    [Fact]
    public void ShrinkKeepsCursorVisible()
    {
        var buffer = new TerminalBuffer(8, 24);
        buffer.Feed("PS> ");

        buffer.Resize(8, 13);

        Assert.Equal(13, buffer.Lines.Count);
        Assert.Equal(0, buffer.CursorScreenRow);
        Assert.Contains("PS>", buffer.GetAllText());
    }

    [Fact]
    public void CursorHomeTargetsScreenTopAfterScrollback()
    {
        var buffer = new TerminalBuffer(8, 3);
        buffer.Feed("a\r\nb\r\nc\r\nd\r\ne");

        buffer.Feed("\x1b[H");

        Assert.Equal(buffer.ViewportTop, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorScreenRow);
    }

    [Fact]
    public void ScrollStaysWithinScrollback()
    {
        var buffer = new TerminalBuffer(8, 3);
        buffer.Feed("a\r\nb\r\nc\r\nd\r\ne");

        buffer.Scroll(2);
        Assert.Equal(2, buffer.ScrollOffset);
        buffer.Scroll(10);
        Assert.Equal(2, buffer.ScrollOffset);
        buffer.Scroll(-10);
        Assert.Equal(0, buffer.ScrollOffset);
    }

    private static string TextOf(TerminalLine line) =>
        string.Concat(line.Cells.Select(cell => cell.Width == 0 ? string.Empty : cell.Text));
}

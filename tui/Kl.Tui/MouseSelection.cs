namespace Kl.Tui;

public readonly record struct SelectionPoint(int Row, int Column);

/// <summary>Mouse selection over rendered transcript lines, in inclusive cell coordinates.</summary>
public sealed class MouseSelection
{
    private SelectionPoint? _anchor;
    private SelectionPoint? _cursor;
    private bool _moved;

    public SelectionPoint? Anchor => _anchor;

    public SelectionPoint? Cursor => _cursor;

    public bool IsDragging { get; private set; }

    public bool HasSelection =>
        _moved
        && _anchor is { } anchor
        && _cursor is { } cursor
        && anchor != cursor;

    public void Begin(int row, int column)
    {
        _anchor = _cursor = new SelectionPoint(Math.Max(0, row), Math.Max(0, column));
        _moved = false;
        IsDragging = true;
    }

    public void Extend(int row, int column)
    {
        if (_anchor is null)
        {
            return;
        }

        var next = new SelectionPoint(Math.Max(0, row), Math.Max(0, column));
        _moved = true;
        _cursor = next;
    }

    public void End(int row, int column)
    {
        if (!IsDragging)
        {
            return;
        }

        Extend(row, column);
        IsDragging = false;
        if (!HasSelection)
        {
            Clear();
        }
    }

    public void Clear()
    {
        _anchor = null;
        _cursor = null;
        _moved = false;
        IsDragging = false;
    }

    /// <summary>Returns plain selected text, preserving row breaks and excluding ANSI sequences.</summary>
    public string Text(IReadOnlyList<string> lines)
    {
        if (!HasSelection || lines.Count == 0)
        {
            return string.Empty;
        }

        var (start, end) = OrderedPoints();
        var startRow = Math.Clamp(start.Row, 0, lines.Count - 1);
        var endRow = Math.Clamp(end.Row, 0, lines.Count - 1);
        var selected = new List<string>();
        for (var row = startRow; row <= endRow; row++)
        {
            var plain = DiffRender.Ansi.Strip(lines[row]);
            var width = DisplayText.Width(plain);
            var from = row == startRow ? Math.Clamp(start.Column, 0, width) : 0;
            var to = row == endRow ? Math.Clamp(end.Column + 1, 0, width) : width;
            selected.Add(to > from ? DisplayText.SliceToWidth(plain, from, to).TrimEnd() : string.Empty);
        }
        return string.Join('\n', selected);
    }

    public bool TryGetColumns(int row, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (!HasSelection)
        {
            return false;
        }

        var (first, last) = OrderedPoints();
        if (row < first.Row || row > last.Row)
        {
            return false;
        }

        start = row == first.Row ? first.Column : 0;
        end = row == last.Row ? last.Column + 1 : int.MaxValue;
        return end > start;
    }

    private (SelectionPoint Start, SelectionPoint End) OrderedPoints()
    {
        var anchor = _anchor!.Value;
        var cursor = _cursor!.Value;
        return anchor.Row < cursor.Row || (anchor.Row == cursor.Row && anchor.Column <= cursor.Column)
            ? (anchor, cursor)
            : (cursor, anchor);
    }
}

/// <summary>Visible transcript rows and their absolute indices in the rendered transcript.</summary>
public sealed class TranscriptViewport
{
    public TranscriptViewport(
        int top,
        int startLine,
        IReadOnlyList<string> lines,
        int? headerLine = null)
    {
        Top = top;
        StartLine = startLine;
        Lines = lines;
        HeaderLine = headerLine;
    }

    public int Top { get; }

    public int StartLine { get; }

    public IReadOnlyList<string> Lines { get; }

    public int? HeaderLine { get; }

    public bool TryGetHeaderTarget(int screenRow, out int line)
    {
        if (HeaderLine is { } target && screenRow == Top - 1)
        {
            line = target;
            return true;
        }
        line = 0;
        return false;
    }

    public bool TryMap(int screenColumn, int screenRow, out SelectionPoint point)
    {
        var localRow = screenRow - Top;
        if (localRow < 0 || localRow >= Lines.Count)
        {
            point = default;
            return false;
        }

        point = MapRow(localRow, screenColumn);
        return true;
    }

    public bool TryMapClamped(int screenColumn, int screenRow, out SelectionPoint point)
    {
        if (Lines.Count == 0)
        {
            point = default;
            return false;
        }

        var localRow = Math.Clamp(screenRow - Top, 0, Lines.Count - 1);
        point = MapRow(localRow, screenColumn);
        return true;
    }

    private SelectionPoint MapRow(int localRow, int screenColumn)
    {
        var lineWidth = DisplayText.Width(DiffRender.Ansi.Strip(Lines[localRow]));
        return new SelectionPoint(
            StartLine + localRow,
            Math.Clamp(screenColumn, 0, lineWidth));
    }
}

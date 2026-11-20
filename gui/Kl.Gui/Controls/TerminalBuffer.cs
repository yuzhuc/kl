using System.Text;

namespace Kl.Gui.Controls;

public enum TerminalColorKind
{
    Default,
    Indexed,
    Rgb
}

public readonly record struct TerminalColor(TerminalColorKind Kind, int Index, uint Rgb)
{
    public static readonly TerminalColor Default = new(TerminalColorKind.Default, 0, 0);

    public static TerminalColor Indexed(int index) => new(TerminalColorKind.Indexed, index, 0);

    public static TerminalColor FromRgb(uint rgb) => new(TerminalColorKind.Rgb, 0, rgb);
}

public struct TerminalCell
{
    public string Text;
    public int Width;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public bool Bold;
    public bool Underline;
    public bool Inverse;

    public static TerminalCell Blank => new()
    {
        Text = " ",
        Width = 1,
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default
    };
}

public sealed class TerminalLine
{
    public TerminalLine(int columns)
    {
        Cells = new TerminalCell[Math.Max(1, columns)];
        for (var i = 0; i < Cells.Length; i++)
        {
            Cells[i] = TerminalCell.Blank;
        }
    }

    public TerminalCell[] Cells { get; set; }
}

/// <summary>Cell buffer and ANSI/VT parser. The view only renders this data.</summary>
public sealed class TerminalBuffer
{
    private readonly List<TerminalLine> _lines = [];
    private readonly StringBuilder _csi = new();
    private readonly StringBuilder _osc = new();
    private ParserState _state;
    private bool _oscEscape;
    private int _savedCursorRow;
    private int _savedCursorColumn;
    private List<TerminalLine>? _alternateLines;
    private int _alternateCursorRow;

    private TerminalColor _foreground = TerminalColor.Default;
    private TerminalColor _background = TerminalColor.Default;
    private bool _bold;
    private bool _underline;
    private bool _inverse;

    public TerminalBuffer(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        Clear();
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorColumn { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public int ScrollOffset { get; private set; }
    public int ScrollbackLimit { get; set; } = 5000;
    public (int Row, int Column)? SelectionStart { get; private set; }
    public (int Row, int Column)? SelectionEnd { get; private set; }

    public IReadOnlyList<TerminalLine> Lines => _lines;

    public int ViewportTop => Math.Max(0, _lines.Count - Rows - ScrollOffset);

    public int CursorScreenRow => CursorRow - ViewportTop;

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            switch (_state)
            {
                case ParserState.Ground:
                    HandleGround(text, ref index, ch);
                    break;
                case ParserState.Escape:
                    HandleEscape(ch);
                    break;
                case ParserState.Csi:
                    if (ch is >= (char)0x40 and <= (char)0x7E)
                    {
                        DispatchCsi(ch, _csi.ToString());
                        _csi.Clear();
                        _state = ParserState.Ground;
                    }
                    else
                    {
                        _csi.Append(ch);
                    }

                    break;
                case ParserState.Osc:
                    if (ch == (char)0x07)
                    {
                        _state = ParserState.Ground;
                        _osc.Clear();
                    }
                    else if (ch == (char)0x1B)
                    {
                        _oscEscape = true;
                    }
                    else if (_oscEscape && ch == '\\')
                    {
                        _oscEscape = false;
                        _state = ParserState.Ground;
                        _osc.Clear();
                    }
                    else
                    {
                        _oscEscape = false;
                        _osc.Append(ch);
                    }

                    break;
            }
        }
    }

    private void HandleGround(string text, ref int index, char ch)
    {
        if (ch == (char)0x1B)
        {
            _state = ParserState.Escape;
            return;
        }

        switch (ch)
        {
            case '\r':
                CursorColumn = 0;
                return;
            case '\n':
                NewLine();
                return;
            case '\b':
                CursorColumn = Math.Max(0, CursorColumn - 1);
                return;
            case '\t':
                CursorColumn = Math.Min(Columns - 1, ((CursorColumn / 8) + 1) * 8);
                return;
            case (char)0x07:
                return;
        }

        if (ch < 0x20)
        {
            return;
        }

        var rune = Rune.GetRuneAt(text, index);
        index += rune.Utf16SequenceLength - 1;
        Print(rune);
    }

    private void HandleEscape(char ch)
    {
        _state = ParserState.Ground;
        switch (ch)
        {
            case '[':
                _csi.Clear();
                _state = ParserState.Csi;
                break;
            case ']':
                _osc.Clear();
                _oscEscape = false;
                _state = ParserState.Osc;
                break;
            case '7':
                _savedCursorRow = CursorRow;
                _savedCursorColumn = CursorColumn;
                break;
            case '8':
                CursorRow = _savedCursorRow;
                CursorColumn = _savedCursorColumn;
                ClampCursor();
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void DispatchCsi(char final, string raw)
    {
        var privateMode = raw.StartsWith('?');
        if (privateMode)
        {
            raw = raw[1..];
        }

        var parameters = raw.Length == 0
            ? []
            : raw.Replace(':', ';')
                .Split(';', StringSplitOptions.None)
                .Select(part => int.TryParse(part, out var value) ? value : 0)
                .ToArray();

        int Param(int index, int fallback) =>
            index < parameters.Length && parameters[index] > 0 ? parameters[index] : fallback;

        switch (final)
        {
            case 'm':
                ApplySgr(parameters);
                break;
            case 'H':
            case 'f':
                MoveCursor(ScreenTop() + Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'A':
                MoveCursor(CursorRow - Param(0, 1), CursorColumn);
                break;
            case 'B':
                MoveCursor(CursorRow + Param(0, 1), CursorColumn);
                break;
            case 'C':
                MoveCursor(CursorRow, CursorColumn + Param(0, 1));
                break;
            case 'D':
                MoveCursor(CursorRow, CursorColumn - Param(0, 1));
                break;
            case 'G':
                MoveCursor(CursorRow, Param(0, 1) - 1);
                break;
            case 'd':
                MoveCursor(ScreenTop() + Param(0, 1) - 1, CursorColumn);
                break;
            case 's':
                _savedCursorRow = CursorRow;
                _savedCursorColumn = CursorColumn;
                break;
            case 'u':
                CursorRow = _savedCursorRow;
                CursorColumn = _savedCursorColumn;
                ClampCursor();
                break;
            case 'J':
                EraseDisplay(Param(0, 0));
                break;
            case 'K':
                EraseLine(Param(0, 0));
                break;
            case 'h' when privateMode:
                ApplyPrivateMode(parameters, true);
                break;
            case 'l' when privateMode:
                ApplyPrivateMode(parameters, false);
                break;
        }
    }

    private void ApplyPrivateMode(int[] parameters, bool enabled)
    {
        foreach (var parameter in parameters)
        {
            switch (parameter)
            {
                case 25:
                    CursorVisible = enabled;
                    break;
                case 1049:
                    if (enabled)
                    {
                        EnterAlternateScreen();
                    }
                    else
                    {
                        LeaveAlternateScreen();
                    }

                    break;
            }
        }
    }

    private void ApplySgr(int[] parameters)
    {
        if (parameters.Length == 0)
        {
            ResetAttributes();
            return;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            switch (parameter)
            {
                case 0:
                    ResetAttributes();
                    break;
                case 1:
                    _bold = true;
                    break;
                case 2:
                    break;
                case 4:
                    _underline = true;
                    break;
                case 7:
                    _inverse = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case 24:
                    _underline = false;
                    break;
                case 27:
                    _inverse = false;
                    break;
                case >= 30 and <= 37:
                    _foreground = TerminalColor.Indexed(parameter - 30);
                    break;
                case 39:
                    _foreground = TerminalColor.Default;
                    break;
                case >= 40 and <= 47:
                    _background = TerminalColor.Indexed(parameter - 40);
                    break;
                case 49:
                    _background = TerminalColor.Default;
                    break;
                case >= 90 and <= 97:
                    _foreground = TerminalColor.Indexed(parameter - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _background = TerminalColor.Indexed(parameter - 100 + 8);
                    break;
                case 38 or 48 when index + 1 < parameters.Length:
                    var target = parameter == 38;
                    var mode = parameters[index + 1];
                    if (mode == 5 && index + 2 < parameters.Length)
                    {
                        var color = TerminalColor.Indexed(parameters[index + 2]);
                        if (target)
                        {
                            _foreground = color;
                        }
                        else
                        {
                            _background = color;
                        }

                        index += 2;
                    }
                    else if (mode == 2 && index + 4 < parameters.Length)
                    {
                        var rgb = (uint)((parameters[index + 2] & 0xFF) << 16
                            | (parameters[index + 3] & 0xFF) << 8
                            | (parameters[index + 4] & 0xFF));
                        var color = TerminalColor.FromRgb(rgb);
                        if (target)
                        {
                            _foreground = color;
                        }
                        else
                        {
                            _background = color;
                        }

                        index += 4;
                    }

                    break;
            }
        }
    }

    private void ResetAttributes()
    {
        _foreground = TerminalColor.Default;
        _background = TerminalColor.Default;
        _bold = false;
        _underline = false;
        _inverse = false;
    }

    private void Print(Rune rune)
    {
        var width = RuneWidth(rune);
        if (width <= 0)
        {
            return;
        }

        if (CursorColumn + width > Columns)
        {
            CursorColumn = 0;
            NewLine();
        }

        var line = EnsureLine(CursorRow);
        if (CursorColumn >= line.Cells.Length)
        {
            return;
        }

        line.Cells[CursorColumn] = new TerminalCell
        {
            Text = rune.ToString(),
            Width = width,
            Foreground = _foreground,
            Background = _background,
            Bold = _bold,
            Underline = _underline,
            Inverse = _inverse
        };

        if (width == 2 && CursorColumn + 1 < line.Cells.Length)
        {
            line.Cells[CursorColumn + 1] = new TerminalCell
            {
                Text = string.Empty,
                Width = 0,
                Foreground = _foreground,
                Background = _background,
                Bold = _bold,
                Underline = _underline,
                Inverse = _inverse
            };
        }

        CursorColumn += width;
    }

    private void NewLine()
    {
        CursorRow++;
        CursorColumn = 0;
        EnsureLine(CursorRow);
        TrimScrollback();
        if (ScrollOffset == 0)
        {
            // Keep following the output.
        }
    }

    private TerminalLine EnsureLine(int row)
    {
        while (_lines.Count <= row)
        {
            _lines.Add(new TerminalLine(Columns));
        }

        return _lines[row];
    }

    private void TrimScrollback()
    {
        var excess = _lines.Count - (Rows + ScrollbackLimit);
        if (excess <= 0)
        {
            return;
        }

        _lines.RemoveRange(0, excess);
        CursorRow = Math.Max(0, CursorRow - excess);
        if (SelectionStart is { } start)
        {
            SelectionStart = (Math.Max(0, start.Row - excess), start.Column);
        }

        if (SelectionEnd is { } end)
        {
            SelectionEnd = (Math.Max(0, end.Row - excess), end.Column);
        }
    }

    private int ScreenTop() => Math.Max(0, _lines.Count - Rows);

    private void MoveCursor(int row, int column)
    {
        CursorRow = Math.Max(0, row);
        CursorColumn = Math.Max(0, Math.Min(Columns - 1, column));
        if (CursorRow < _lines.Count)
        {
            EnsureLine(CursorRow);
        }
    }

    private void ClampCursor()
    {
        CursorRow = Math.Max(0, CursorRow);
        CursorColumn = Math.Max(0, Math.Min(Columns - 1, CursorColumn));
        EnsureLine(CursorRow);
    }

    private void EraseLine(int mode)
    {
        var line = EnsureLine(CursorRow);
        for (var column = 0; column < line.Cells.Length; column++)
        {
            if (mode == 0 && column < CursorColumn
                || mode == 1 && column > CursorColumn)
            {
                continue;
            }

            line.Cells[column] = TerminalCell.Blank;
        }
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 2)
        {
            foreach (var line in _lines)
            {
                for (var column = 0; column < line.Cells.Length; column++)
                {
                    line.Cells[column] = TerminalCell.Blank;
                }
            }

            CursorRow = Math.Max(0, _lines.Count - Rows);
            CursorColumn = 0;
            return;
        }

        EraseLine(mode == 0 ? 0 : 1);
        if (mode == 0)
        {
            for (var row = CursorRow + 1; row < _lines.Count; row++)
            {
                ClearLine(_lines[row]);
            }
        }
        else if (mode == 1)
        {
            for (var row = 0; row < CursorRow; row++)
            {
                ClearLine(_lines[row]);
            }
        }
    }

    private static void ClearLine(TerminalLine line)
    {
        for (var column = 0; column < line.Cells.Length; column++)
        {
            line.Cells[column] = TerminalCell.Blank;
        }
    }

    private void EnterAlternateScreen()
    {
        if (_alternateLines is not null)
        {
            return;
        }

        _alternateLines = [.. _lines];
        _alternateCursorRow = CursorRow;
        _lines.Clear();
        for (var row = 0; row < Rows; row++)
        {
            _lines.Add(new TerminalLine(Columns));
        }

        CursorRow = 0;
        CursorColumn = 0;
        ScrollOffset = 0;
    }

    private void LeaveAlternateScreen()
    {
        if (_alternateLines is null)
        {
            return;
        }

        _lines.Clear();
        _lines.AddRange(_alternateLines);
        _alternateLines = null;
        CursorRow = Math.Min(_alternateCursorRow, _lines.Count - 1);
        CursorColumn = Math.Min(CursorColumn, Columns - 1);
        ScrollOffset = 0;
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        var next = new List<TerminalLine>(_lines.Count + rows);
        foreach (var line in _lines)
        {
            var replacement = new TerminalLine(columns);
            Array.Copy(line.Cells, replacement.Cells, Math.Min(line.Cells.Length, columns));
            next.Add(replacement);
        }

        Columns = columns;
        Rows = rows;
        // Shrinking clips the bottom, never content above the cursor.
        while (next.Count - Rows > CursorRow)
        {
            next.RemoveAt(next.Count - 1);
        }

        while (next.Count < Rows)
        {
            next.Add(new TerminalLine(columns));
        }

        _lines.Clear();
        _lines.AddRange(next);
        CursorColumn = Math.Min(CursorColumn, columns - 1);
        CursorRow = Math.Min(CursorRow, _lines.Count - 1);
        ScrollOffset = Math.Min(ScrollOffset, Math.Max(0, _lines.Count - Rows));
    }

    public void Clear()
    {
        _lines.Clear();
        for (var row = 0; row < Rows; row++)
        {
            _lines.Add(new TerminalLine(Columns));
        }

        CursorRow = 0;
        CursorColumn = 0;
        ScrollOffset = 0;
        SelectionStart = null;
        SelectionEnd = null;
        _alternateLines = null;
        ResetAttributes();
        _state = ParserState.Ground;
        _csi.Clear();
        _osc.Clear();
    }

    public void Reset()
    {
        Clear();
        CursorVisible = true;
    }

    public void Scroll(int lines)
    {
        var maximum = Math.Max(0, _lines.Count - Rows);
        ScrollOffset = Math.Clamp(ScrollOffset + lines, 0, maximum);
    }

    public void SetSelection(int startRow, int startColumn, int endRow, int endColumn)
    {
        SelectionStart = (Math.Max(0, startRow), Math.Max(0, startColumn));
        SelectionEnd = (Math.Max(0, endRow), Math.Max(0, endColumn));
    }

    public void ClearSelection()
    {
        SelectionStart = null;
        SelectionEnd = null;
    }

    public string GetSelectedText()
    {
        if (SelectionStart is not { } start || SelectionEnd is not { } end)
        {
            return string.Empty;
        }

        var ordered = start.Row < end.Row || start.Row == end.Row && start.Column <= end.Column;
        var from = ordered ? start : end;
        var to = ordered ? end : start;
        var builder = new StringBuilder();
        for (var row = from.Row; row <= to.Row && row < _lines.Count; row++)
        {
            var line = _lines[row];
            var first = row == from.Row ? from.Column : 0;
            var last = row == to.Row ? to.Column : line.Cells.Length - 1;
            var lineBuilder = new StringBuilder();
            for (var column = first; column <= last && column < line.Cells.Length; column++)
            {
                var cell = line.Cells[column];
                if (cell.Width == 0)
                {
                    continue;
                }

                lineBuilder.Append(cell.Text);
            }

            builder.Append(lineBuilder.ToString().TrimEnd());
            if (row != to.Row)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    public string GetAllText()
    {
        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            var lineBuilder = new StringBuilder();
            foreach (var cell in line.Cells)
            {
                if (cell.Width != 0)
                {
                    lineBuilder.Append(cell.Text);
                }
            }

            builder.Append(lineBuilder.ToString().TrimEnd()).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static int RuneWidth(Rune rune)
    {
        var value = rune.Value;
        if (value == 0 || Rune.IsControl(rune))
        {
            return 0;
        }

        return value is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x20000 and <= 0x3FFFD
            ? 2
            : 1;
    }

    private enum ParserState
    {
        Ground,
        Escape,
        Csi,
        Osc
    }
}

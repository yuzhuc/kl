using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Kl.Gui.Services;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.Controls;

/// <summary>Self-drawn ConPTY terminal with ANSI colors, selection and scrollback.</summary>
public sealed class TerminalView : Control
{
    private const double Padding = 8;
    private const double TerminalFontSize = 13;
    private static readonly string[] AnsiKeys =
    [
        "TerminalBlack", "TerminalRed", "TerminalGreen", "TerminalYellow",
        "TerminalBlue", "TerminalMagenta", "TerminalCyan", "TerminalWhite",
        "TerminalBrightBlack", "TerminalBrightRed", "TerminalBrightGreen", "TerminalBrightYellow",
        "TerminalBrightBlue", "TerminalBrightMagenta", "TerminalBrightCyan", "TerminalBrightWhite"
    ];

    private readonly Dictionary<TerminalColor, IBrush> _brushes = [];
    private readonly DispatcherTimer _redrawTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private TerminalBuffer _buffer = new(80, 24);
    private TerminalSession? _session;
    private string? _pendingShell;
    private string? _pendingWorkingDirectory;
    private FontFamily _fontFamily = new("Cascadia Mono, Consolas");
    private FontWeight _fontWeight = FontWeight.Normal;
    private double _cellWidth = 8;
    private double _cellHeight = 17;
    private bool _sizeInitialized;
    private bool _selecting;
    private int _selectionAnchorRow;
    private int _selectionAnchorColumn;

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _redrawTimer.Tick += (_, _) =>
        {
            _redrawTimer.Stop();
            InvalidateVisual();
        };
        SizeChanged += (_, _) =>
        {
            UpdateTerminalSize();
            StartPendingSession();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (Application.Current is { } application)
            {
                application.ActualThemeVariantChanged += OnThemeVariantChanged;
            }

            UpdateFont();
            UpdateTerminalSize();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (Application.Current is { } application)
            {
                application.ActualThemeVariantChanged -= OnThemeVariantChanged;
            }
        };
        UpdateFont();
    }

    public bool IsRunning => _session?.IsRunning == true;

    public void Start(string shell, string workingDirectory)
    {
        Stop();
        _buffer = new TerminalBuffer(80, 24);
        _sizeInitialized = false;
        _pendingShell = shell;
        _pendingWorkingDirectory = workingDirectory;
        UpdateFont();
        UpdateTerminalSize();
        StartPendingSession();
    }

    // The panel is opened before layout runs, so the real size is only known after the first
    // pass. Starting ConPTY earlier makes the follow-up resize repaint the screen blank.
    private void StartPendingSession()
    {
        if (_pendingShell is null || _session is not null)
        {
            return;
        }

        if (Bounds.Width < Padding * 2 + _cellWidth || Bounds.Height < Padding * 2 + _cellHeight)
        {
            return;
        }

        var shell = _pendingShell;
        var workingDirectory = _pendingWorkingDirectory ?? string.Empty;
        _pendingShell = null;
        _pendingWorkingDirectory = null;
        var session = new TerminalSession();
        session.Output += OnOutput;
        session.Exited += OnExited;
        try
        {
            session.Start(shell, workingDirectory, _buffer.Columns, _buffer.Rows);
            _sizeInitialized = true;
            _session = session;
            Focus();
        }
        catch (Exception error)
        {
            session.Output -= OnOutput;
            session.Exited -= OnExited;
            session.Dispose();
            _buffer.Feed(Loc.F("\r\n[31m无法启动终端：{0}[0m\r\n", (error.Message)));
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _buffer.Clear();
        InvalidateVisual();
    }

    public void CopyAll()
    {
        if (!string.IsNullOrEmpty(_buffer.GetAllText()))
        {
            _ = CopyToClipboardAsync(_buffer.GetAllText());
        }
    }

    public void CopySelection()
    {
        var text = _buffer.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            _ = CopyToClipboardAsync(text);
        }
    }

    public void CopySelectionOrAll()
    {
        if (string.IsNullOrEmpty(_buffer.GetSelectedText()))
        {
            CopyAll();
            return;
        }

        CopySelection();
    }

    public void Dispose()
    {
        Stop();
    }

    private void Stop()
    {
        if (_session is not null)
        {
            _session.Output -= OnOutput;
            _session.Exited -= OnExited;
            _session.Dispose();
            _session = null;
        }
    }

    private void OnOutput(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            _buffer.Feed(text);
            ScheduleRedraw();
        });

    private void OnExited() =>
        Dispatcher.UIThread.Post(() =>
        {
            _buffer.Feed(Loc.T("\r\n[90m[终端已退出][0m\r\n"));
            InvalidateVisual();
        });

    private void ScheduleRedraw()
    {
        if (!_redrawTimer.IsEnabled)
        {
            _redrawTimer.Start();
        }
    }

    private void UpdateFont()
    {
        var family = ThemeFonts.CodeFont;
        var size = TerminalFontSize;
        var typeface = new Typeface(family, FontStyle.Normal, _fontWeight);
        var probe = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.White);
        _cellWidth = Math.Max(6, probe.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Max(12, Math.Ceiling(probe.Height));
        _fontFamily = family;
    }

    private void UpdateTerminalSize()
    {
        if (Bounds.Width < Padding * 2 + _cellWidth || Bounds.Height < Padding * 2 + _cellHeight)
        {
            return;
        }

        var columns = Math.Max(20, (int)((Bounds.Width - Padding * 2) / _cellWidth));
        var rows = Math.Max(5, (int)((Bounds.Height - Padding * 2) / _cellHeight));
        if (!_sizeInitialized && _session is null)
        {
            _buffer.Resize(columns, rows);
            return;
        }

        if (columns == _buffer.Columns && rows == _buffer.Rows)
        {
            return;
        }

        _buffer.Resize(columns, rows);
        _session?.Resize(columns, rows);
        InvalidateVisual();
    }

    private void OnThemeVariantChanged(object? sender, EventArgs eventArgs)
    {
        _brushes.Clear();
        UpdateFont();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var background = ThemeBrushes.Get("TerminalBackground", "#0F0F11");
        context.FillRectangle(background, new Rect(Bounds.Size));
        var typeface = new Typeface(_fontFamily, FontStyle.Normal, _fontWeight);
        var visibleRows = Math.Max(1, (int)((Bounds.Height - Padding * 2) / _cellHeight));
        var top = _buffer.ViewportTop;

        for (var screenRow = 0; screenRow < visibleRows; screenRow++)
        {
            var lineIndex = top + screenRow;
            if (lineIndex >= _buffer.Lines.Count)
            {
                break;
            }

            var line = _buffer.Lines[lineIndex];
            var y = Padding + screenRow * _cellHeight;

            // Cell backgrounds first, then text runs on top.
            for (var column = 0; column < _buffer.Columns && column < line.Cells.Length; column++)
            {
                var cell = line.Cells[column];
                if (cell.Width == 0)
                {
                    continue;
                }

                var (foreground, backgroundColor) = ResolveColors(cell, bold: cell.Bold);
                if (backgroundColor is not null)
                {
                    context.FillRectangle(
                        backgroundColor,
                        new Rect(Padding + column * _cellWidth, y,
                            _cellWidth * Math.Max(1, cell.Width), _cellHeight));
                }
            }

            DrawLine(context, line, y, typeface, background);
        }

        DrawSelection(context, top, visibleRows);

        if (_buffer.CursorVisible && _buffer.ScrollOffset == 0)
        {
            var cursorRow = _buffer.CursorScreenRow;
            if (cursorRow >= 0 && cursorRow < visibleRows && _buffer.CursorColumn < _buffer.Columns)
            {
                context.FillRectangle(
                    ThemeBrushes.Get("TerminalCursor", "#4D9FFF"),
                    new Rect(Padding + _buffer.CursorColumn * _cellWidth,
                        Padding + cursorRow * _cellHeight,
                        _cellWidth, _cellHeight));
            }
        }
    }

    private void DrawLine(DrawingContext context, TerminalLine line, double y, Typeface typeface, IBrush background)
    {
        var column = 0;
        while (column < _buffer.Columns && column < line.Cells.Length)
        {
            var cell = line.Cells[column];
            if (cell.Width == 0)
            {
                column++;
                continue;
            }

            var text = new StringBuilder();
            var start = column;
            var style = cell;
            while (column < _buffer.Columns && column < line.Cells.Length)
            {
                var current = line.Cells[column];
                if (current.Width == 0 || !SameStyle(current, style))
                {
                    break;
                }

                text.Append(current.Text);
                column += Math.Max(1, current.Width);
            }

            var (foreground, backgroundColor) = ResolveColors(style, style.Bold);
            var brush = foreground ?? ThemeBrushes.Get("TerminalForeground", "#E3E4E6");
            var typefaceForRun = style.Bold
                ? new Typeface(_fontFamily, FontStyle.Normal, FontWeight.Bold)
                : typeface;
            var formatted = new FormattedText(text.ToString(), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typefaceForRun, TerminalFontSize, brush);
            context.DrawText(formatted, new Point(Padding + start * _cellWidth, y));
            if (style.Underline)
            {
                context.DrawLine(
                    new Pen(brush, 1),
                    new Point(Padding + start * _cellWidth, y + _cellHeight - 2),
                    new Point(Padding + column * _cellWidth, y + _cellHeight - 2));
            }
        }
    }

    private void DrawSelection(DrawingContext context, int top, int visibleRows)
    {
        if (_buffer.SelectionStart is not { } start || _buffer.SelectionEnd is not { } end)
        {
            return;
        }

        var ordered = start.Row < end.Row || start.Row == end.Row && start.Column <= end.Column;
        var from = ordered ? start : end;
        var to = ordered ? end : start;
        var selection = ThemeBrushes.Get("TerminalSelection", "#554D9FFF");
        for (var row = from.Row; row <= to.Row; row++)
        {
            var screenRow = row - top;
            if (screenRow < 0 || screenRow >= visibleRows)
            {
                continue;
            }

            var first = row == from.Row ? from.Column : 0;
            var last = row == to.Row ? to.Column : _buffer.Columns - 1;
            first = Math.Clamp(first, 0, _buffer.Columns - 1);
            last = Math.Clamp(last, 0, _buffer.Columns - 1);
            if (last < first)
            {
                continue;
            }

            context.FillRectangle(selection, new Rect(
                Padding + first * _cellWidth,
                Padding + screenRow * _cellHeight,
                (last - first + 1) * _cellWidth,
                _cellHeight));
        }
    }

    private (IBrush? Foreground, IBrush? Background) ResolveColors(TerminalCell cell, bool bold)
    {
        var foregroundColor = cell.Foreground;
        var backgroundColor = cell.Background;
        if (bold && foregroundColor.Kind == TerminalColorKind.Indexed
            && foregroundColor.Index is >= 0 and < 8)
        {
            foregroundColor = TerminalColor.Indexed(foregroundColor.Index + 8);
        }

        var foreground = BrushFor(foregroundColor, foreground: true);
        var background = backgroundColor.Kind == TerminalColorKind.Default
            ? null
            : BrushFor(backgroundColor, foreground: false);
        if (cell.Inverse)
        {
            (foreground, background) = (background ?? ThemeBrushes.Get("TerminalBackground", "#0F0F11"),
                foreground ?? ThemeBrushes.Get("TerminalForeground", "#E3E4E6"));
        }

        return (foreground, background);
    }

    private IBrush BrushFor(TerminalColor color, bool foreground)
    {
        if (color.Kind == TerminalColorKind.Default)
        {
            return foreground
                ? ThemeBrushes.Get("TerminalForeground", "#E3E4E6")
                : ThemeBrushes.Get("TerminalBackground", "#0F0F11");
        }

        if (_brushes.TryGetValue(color, out var cached))
        {
            return cached;
        }

        var brush = color.Kind switch
        {
            TerminalColorKind.Indexed when color.Index is >= 0 and < 16 =>
                ThemeBrushes.Get(AnsiKeys[color.Index], "#E3E4E6"),
            TerminalColorKind.Indexed => new SolidColorBrush(XtermColor(color.Index)),
            _ => new SolidColorBrush(Color.FromRgb(
                (byte)(color.Rgb >> 16), (byte)(color.Rgb >> 8), (byte)color.Rgb))
        };
        _brushes[color] = brush;
        return brush;
    }

    private static Color XtermColor(int index)
    {
        if (index is >= 0 and < 16)
        {
            return Color.FromRgb(0x80, 0x80, 0x80);
        }

        if (index is >= 16 and < 232)
        {
            var value = index - 16;
            var red = value / 36;
            var green = value / 6 % 6;
            var blue = value % 6;
            return Color.FromRgb(
                (byte)(red == 0 ? 0 : 55 + red * 40),
                (byte)(green == 0 ? 0 : 55 + green * 40),
                (byte)(blue == 0 ? 0 : 55 + blue * 40));
        }

        var gray = (byte)(8 + (index - 232) * 10);
        return Color.FromRgb(gray, gray, gray);
    }

    private static bool SameStyle(TerminalCell left, TerminalCell right) =>
        left.Foreground == right.Foreground
        && left.Background == right.Background
        && left.Bold == right.Bold
        && left.Underline == right.Underline
        && left.Inverse == right.Inverse;

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (_session is null)
        {
            return;
        }

        var modifiers = eventArgs.KeyModifiers;
        var control = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        if (control && shift && eventArgs.Key == Key.C)
        {
            CopySelection();
            eventArgs.Handled = true;
            return;
        }

        if (control && eventArgs.Key == Key.V)
        {
            _ = PasteAsync();
            eventArgs.Handled = true;
            return;
        }

        if (control && !shift && eventArgs.Key == Key.C)
        {
            var selected = _buffer.GetSelectedText();
            if (selected.Length > 0)
            {
                _ = CopyToClipboardAsync(selected);
            }
            else
            {
                _session.Write("\x03");
            }

            eventArgs.Handled = true;
            return;
        }

        var sequence = eventArgs.Key switch
        {
            Key.Enter => "\r",
            Key.Back => "\x7f",
            Key.Tab => "\t",
            Key.Escape => "\x1b",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            _ => null
        };

        if (sequence is not null)
        {
            _session.Write(sequence);
            eventArgs.Handled = true;
            return;
        }

        if (control && eventArgs.Key is >= Key.A and <= Key.Z)
        {
            _session.Write(((char)(1 + eventArgs.Key - Key.A)).ToString());
            eventArgs.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs eventArgs)
    {
        if (_session is not null && !string.IsNullOrEmpty(eventArgs.Text))
        {
            _session.Write(eventArgs.Text);
            eventArgs.Handled = true;
            return;
        }

        base.OnTextInput(eventArgs);
    }

    private async Task PasteAsync()
    {
        if (_session is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _session.Write(text.Replace("\r\n", "\r").Replace('\n', '\r'));
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        Focus();
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var (row, column) = CellAt(eventArgs.GetPosition(this));
        _selecting = true;
        _selectionAnchorRow = row;
        _selectionAnchorColumn = column;
        _buffer.SetSelection(row, column, row, column);
        eventArgs.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (!_selecting || !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var (row, column) = CellAt(eventArgs.GetPosition(this));
        _buffer.SetSelection(_selectionAnchorRow, _selectionAnchorColumn, row, column);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        _selecting = false;
        eventArgs.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        _buffer.Scroll(eventArgs.Delta.Y > 0 ? 3 : -3);
        InvalidateVisual();
        eventArgs.Handled = true;
    }

    private (int Row, int Column) CellAt(Point point)
    {
        var row = _buffer.ViewportTop
            + Math.Clamp((int)((point.Y - Padding) / _cellHeight), 0, _buffer.Rows - 1);
        var column = Math.Clamp((int)((point.X - Padding) / _cellWidth), 0, _buffer.Columns - 1);
        return (Math.Max(0, row), Math.Max(0, column));
    }
}

internal static class ThemeFonts
{
    public static FontFamily CodeFont
    {
        get
        {
            if (Application.Current is { } application
                && application.TryFindResource("CodeFont", out var value)
                && value is FontFamily family)
            {
                return family;
            }

            return new FontFamily("Cascadia Mono, Consolas");
        }
    }
}

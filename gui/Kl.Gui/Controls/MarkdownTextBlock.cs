using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace Kl.Gui.Controls;

public sealed class MarkdownTextBlock : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string>(nameof(Text), string.Empty);
    public static readonly StyledProperty<double> ContentFontSizeProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, double>(nameof(ContentFontSize), 14);
    public static readonly StyledProperty<double> ContentLineHeightProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, double>(nameof(ContentLineHeight), 24);

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
    private static readonly IBrush PrimaryBrush = new SolidColorBrush(Color.Parse("#E3E4E6"));
    private static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#D7D9DD"));
    private static readonly IBrush InlineCodeBrush = new SolidColorBrush(Color.Parse("#D7D9DD"));
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#1B1B1C"));
    private readonly StackPanel _root;
    private readonly DispatcherTimer _rebuildTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    static MarkdownTextBlock()
    {
        TextProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.ScheduleRebuild());
        ContentFontSizeProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
        ContentLineHeightProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
    }

    public MarkdownTextBlock()
    {
        _root = new StackPanel { Spacing = 8 };
        Content = _root;
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            Rebuild();
        };
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double ContentFontSize
    {
        get => GetValue(ContentFontSizeProperty);
        set => SetValue(ContentFontSizeProperty, value);
    }

    public double ContentLineHeight
    {
        get => GetValue(ContentLineHeightProperty);
        set => SetValue(ContentLineHeightProperty, value);
    }

    private void ScheduleRebuild()
    {
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var lines = Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var fence = new string((char)96, 3);
        var paragraph = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                var code = new List<string>();
                index++;
                while (index < lines.Length
                    && !lines[index].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }
                AddCodeBlock(string.Join("\n", code));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(paragraph);
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                AddHeading(line[2..], 4);
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                AddHeading(line[3..], 2);
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                AddListItem(line[2..]);
                continue;
            }

            paragraph.Add(line);
        }
        FlushParagraph(paragraph);
    }

    private void FlushParagraph(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }
        var text = string.Join("\n", lines);
        lines.Clear();
        var block = new SelectableTextBlock
        {
            FontSize = ContentFontSize,
            LineHeight = ContentLineHeight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = PrimaryBrush
        };
        AddInline(block.Inlines!, text);
        _root.Children.Add(block);
    }

    private void AddHeading(string text, double sizeDelta)
    {
        var block = new SelectableTextBlock
        {
            FontSize = ContentFontSize + sizeDelta,
            LineHeight = ContentLineHeight + sizeDelta,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = PrimaryBrush
        };
        AddInline(block.Inlines!, text);
        _root.Children.Add(block);
    }

    private void AddListItem(string text)
    {
        var block = new SelectableTextBlock
        {
            FontSize = ContentFontSize,
            LineHeight = ContentLineHeight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = PrimaryBrush
        };
        block.Inlines!.Add(new Run("• "));
        AddInline(block.Inlines, text);
        _root.Children.Add(block);
    }

    private void AddCodeBlock(string code)
    {
        var codeText = new SelectableTextBlock
        {
            Text = code,
            FontFamily = CodeFont,
            FontSize = Math.Max(12, ContentFontSize - 1),
            LineHeight = Math.Max(18, ContentLineHeight - 3),
            Foreground = CodeBrush,
            TextWrapping = TextWrapping.NoWrap
        };
        var scroll = new ScrollViewer
        {
            Content = codeText,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 360
        };
        _root.Children.Add(new Border
        {
            Child = scroll,
            Background = CodeBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10)
        });
    }

    private static void AddInline(InlineCollection target, string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var boldStart = text.IndexOf("**", position, StringComparison.Ordinal);
            var codeStart = text.IndexOf((char)96, position);
            var next = MinPositive(boldStart, codeStart);
            if (next < 0)
            {
                target.Add(new Run(text[position..]));
                break;
            }

            if (next > position)
            {
                target.Add(new Run(text[position..next]));
            }

            if (next == codeStart)
            {
                var codeEnd = text.IndexOf((char)96, codeStart + 1);
                if (codeEnd < 0)
                {
                    target.Add(new Run(text[codeStart..]));
                    break;
                }
                target.Add(new Run(text[(codeStart + 1)..codeEnd])
                {
                    FontFamily = CodeFont,
                    Foreground = InlineCodeBrush
                });
                position = codeEnd + 1;
                continue;
            }

            var boldEnd = text.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
            if (boldEnd < 0)
            {
                target.Add(new Run(text[boldStart..]));
                break;
            }
            var bold = new Bold();
            AddInline(bold.Inlines!, text[(boldStart + 2)..boldEnd]);
            target.Add(bold);
            position = boldEnd + 2;
        }
    }

    private static int MinPositive(int left, int right)
    {
        if (left < 0)
        {
            return right;
        }
        if (right < 0)
        {
            return left;
        }
        return Math.Min(left, right);
    }
}

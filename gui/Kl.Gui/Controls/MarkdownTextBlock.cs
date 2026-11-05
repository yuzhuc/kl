using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
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
    private static readonly IBrush BodyBrush = new SolidColorBrush(Color.Parse("#C3C4C6"));
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
        var listItems = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                FlushListBlock(listItems);
                var language = line.TrimStart()[fence.Length..].Trim();
                var code = new List<string>();
                index++;
                while (index < lines.Length
                    && !lines[index].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }
                AddCodeBlock(string.Join("\n", code), language);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(paragraph);
                FlushListBlock(listItems);
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                FlushListBlock(listItems);
                AddHeading(line[2..], 4);
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                FlushListBlock(listItems);
                AddHeading(line[3..], 2);
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                listItems.Add(line[2..]);
                continue;
            }

            FlushListBlock(listItems);
            paragraph.Add(line);
        }
        FlushParagraph(paragraph);
        FlushListBlock(listItems);
    }

    private void FlushListBlock(List<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        AddListBlock(items);
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
            Foreground = BodyBrush
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

    private void AddListBlock(List<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var block = new SelectableTextBlock
        {
            FontSize = ContentFontSize,
            LineHeight = ContentLineHeight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BodyBrush
        };
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                block.Inlines!.Add(new LineBreak());
            }

            block.Inlines!.Add(new Run("• ") { Foreground = BodyBrush });
            AddInline(block.Inlines!, items[index]);
        }

        items.Clear();
        _root.Children.Add(block);
    }

    private void AddCodeBlock(string code, string language)
    {
        var headerIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M9 6L3 12L9 18 M15 6L21 12L15 18"),
            Stroke = new SolidColorBrush(Color.Parse("#7E7F82")),
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "纯文本" : language,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#A0A1A3")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var headerStack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        headerStack.Children.Add(headerIcon);
        headerStack.Children.Add(label);

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        actions.Children.Add(CreateCodeActionButton("M4 6H20 M4 12H15L18 15 M4 18H20 M18 15H14", null));
        actions.Children.Add(CreateCodeActionButton("M8 8H20V20H8Z M4 16V4H16", () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                _ = clipboard.SetTextAsync(code);
            }
        }));

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Height = 36,
            Margin = new Thickness(12, 0, 8, 0)
        };
        header.Children.Add(headerStack);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var codeText = new SelectableTextBlock
        {
            Text = code,
            FontFamily = CodeFont,
            FontSize = 12,
            LineHeight = 19,
            Foreground = new SolidColorBrush(Color.Parse("#C3C4C6")),
            TextWrapping = TextWrapping.NoWrap
        };
        var scroll = new ScrollViewer
        {
            Content = codeText,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 360
        };
        var codeArea = new Border
        {
            Child = scroll,
            Background = new SolidColorBrush(Color.Parse("#1E1F21")),
            Padding = new Thickness(12, 10)
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        layout.Children.Add(header);
        Grid.SetRow(codeArea, 1);
        layout.Children.Add(codeArea);
        _root.Children.Add(new Border
        {
            Child = layout,
            Background = new SolidColorBrush(Color.Parse("#1C1C1E")),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true
        });
    }

    private static Button CreateCodeActionButton(string geometry, Action? onClick)
    {
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(geometry),
                Stroke = new SolidColorBrush(Color.Parse("#7E7F82")),
                StrokeThickness = 1.3,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
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
                var codeText = text[(codeStart + 1)..codeEnd];
                if (codeText.Length > 80)
                {
                    target.Add(new Run(codeText)
                    {
                        FontFamily = CodeFont,
                        Foreground = InlineCodeBrush
                    });
                    position = codeEnd + 1;
                    continue;
                }

                var pill = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#242427")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 0),
                    Margin = new Thickness(1, 0),
                    Height = 17,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = codeText,
                        FontFamily = CodeFont,
                        FontSize = 12,
                        Foreground = PrimaryBrush,
                        TextWrapping = TextWrapping.NoWrap,
                        MaxWidth = 560,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }
                };
                target.Add(new InlineUIContainer(pill));
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

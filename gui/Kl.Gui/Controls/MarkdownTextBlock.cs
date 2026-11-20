using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    public static readonly StyledProperty<double> CodeFontSizeProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, double>(nameof(CodeFontSize), 12);
    public static readonly StyledProperty<FontFamily> ContentFontFamilyProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, FontFamily>(nameof(ContentFontFamily), FontFamily.Default);
    public static readonly StyledProperty<FontWeight> ContentFontWeightProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, FontWeight>(nameof(ContentFontWeight), FontWeight.Normal);
    public static readonly StyledProperty<FontFamily> CodeFontFamilyProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, FontFamily>(nameof(CodeFontFamily), FontFamily.Default);
    public static readonly StyledProperty<FontWeight> CodeFontWeightProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, FontWeight>(nameof(CodeFontWeight), FontWeight.Normal);

    private static IBrush PrimaryBrush => ThemeBrushes.Get("TextPrimary", "#E3E4E6");
    private readonly StackPanel _root;
    private readonly DispatcherTimer _rebuildTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    static MarkdownTextBlock()
    {
        TextProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.ScheduleRebuild());
        ContentFontSizeProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
        ContentLineHeightProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
        CodeFontSizeProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
        ContentFontFamilyProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
        ContentFontWeightProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
        CodeFontFamilyProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
        CodeFontWeightProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (control, _) => control.ScheduleRebuild());
    }

    public MarkdownTextBlock()
    {
        _root = new StackPanel { Spacing = 14 };
        Content = _root;
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            Rebuild();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (Application.Current is { } application)
            {
                application.ActualThemeVariantChanged += OnApplicationThemeChanged;
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (Application.Current is { } application)
            {
                application.ActualThemeVariantChanged -= OnApplicationThemeChanged;
            }
        };
    }

    private void OnApplicationThemeChanged(object? sender, EventArgs eventArgs) => ScheduleRebuild();

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

    public double CodeFontSize
    {
        get => GetValue(CodeFontSizeProperty);
        set => SetValue(CodeFontSizeProperty, value);
    }

    public FontFamily ContentFontFamily
    {
        get => GetValue(ContentFontFamilyProperty);
        set => SetValue(ContentFontFamilyProperty, value);
    }

    public FontWeight ContentFontWeight
    {
        get => GetValue(ContentFontWeightProperty);
        set => SetValue(ContentFontWeightProperty, value);
    }

    public FontFamily CodeFontFamily
    {
        get => GetValue(CodeFontFamilyProperty);
        set => SetValue(CodeFontFamilyProperty, value);
    }

    public FontWeight CodeFontWeight
    {
        get => GetValue(CodeFontWeightProperty);
        set => SetValue(CodeFontWeightProperty, value);
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
        var listItems = new List<MarkdownListItem>();
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

            var hashCount = line.TakeWhile(character => character == '#').Count();
            if (hashCount is >= 1 and <= 3 && line.Length > hashCount && line[hashCount] == ' ')
            {
                FlushParagraph(paragraph);
                FlushListBlock(listItems);
                AddHeading(line[(hashCount + 1)..], hashCount == 1 ? 4 : 2);
                continue;
            }
            if (MarkdownListItemParser.TryParse(line, out var listItem))
            {
                FlushParagraph(paragraph);
                listItems.Add(listItem);
                continue;
            }

            // Codex hangs wrapped list text off the item's text column.
            if (listItems.Count > 0)
            {
                var continuation = listItems[^1].Text;
                listItems[^1] = listItems[^1] with
                {
                    Text = continuation + "\n" + line.Trim(),
                };
                continue;
            }

            paragraph.Add(line);
        }
        FlushParagraph(paragraph);
        FlushListBlock(listItems);
    }

    private void FlushListBlock(List<MarkdownListItem> items)
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
            FontFamily = ContentFontFamily,
            FontWeight = ContentFontWeight,
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
            FontFamily = ContentFontFamily,
            FontSize = ContentFontSize + sizeDelta,
            LineHeight = ContentLineHeight + sizeDelta,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = PrimaryBrush
        };
        AddInline(block.Inlines!, text);
        _root.Children.Add(block);
    }

    private void AddListBlock(List<MarkdownListItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Codex hangs list text off a gutter: the marker sits left of the body column
        // and nested levels step in, ordered numbers right-aligned against the text.
        var block = new StackPanel { Spacing = 3 };
        foreach (var item in items)
        {
            var gutter = 26 + item.Level * 35;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions($"{gutter},*") };
            row.Children.Add(new TextBlock
            {
                Text = item.Marker,
                FontFamily = ContentFontFamily,
                FontWeight = ContentFontWeight,
                FontSize = ContentFontSize,
                LineHeight = ContentLineHeight,
                Foreground = PrimaryBrush,
                Margin = new Thickness(item.Numbered ? 0 : (item.Level == 0 ? 4 : 0), 0, item.Numbered ? 6 : 0, 0),
                HorizontalAlignment = item.Numbered
                    ? Avalonia.Layout.HorizontalAlignment.Right
                    : Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            });
            var text = new SelectableTextBlock
            {
                FontFamily = ContentFontFamily,
                FontWeight = ContentFontWeight,
                FontSize = ContentFontSize,
                LineHeight = ContentLineHeight,
                TextWrapping = TextWrapping.Wrap,
                Foreground = PrimaryBrush
            };
            AddInline(text.Inlines!, item.Text);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            block.Children.Add(row);
        }

        items.Clear();
        _root.Children.Add(block);
    }

    private void AddCodeBlock(string code, string language)
    {
        var headerIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M10 4V20 M4 12H20 M16 8L20 12L16 16"),
            Stroke = ThemeBrushes.Get("CodeIcon", "#7E7F82"),
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
            Foreground = ThemeBrushes.Get("CodeText", "#D0D1D3"),
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
        var wrapButton = CreateCodeActionButton("M4 6H20 M4 12H15L18 15 M4 18H20 M18 15H14", null);
        ToolTip.SetTip(wrapButton, "自动换行");
        actions.Children.Add(wrapButton);
        var downloadButton = CreateCodeActionButton(
            "M12 4V15 M7.5 10.5L12 15L16.5 10.5 M5 19.5H19",
            null);
        downloadButton.Click += async (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            var (fileName, extension) = CodeFileHint(language);
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存代码",
                SuggestedFileName = fileName,
                DefaultExtension = extension,
                ShowOverwritePrompt = true
            });
            if (file is null)
            {
                return;
            }

            try
            {
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(code);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A failed save keeps the code on screen; nothing to roll back.
            }
        };
        ToolTip.SetTip(downloadButton, "下载代码");
        actions.Children.Add(downloadButton);
        var copyButton = CreateCodeActionButton("M8 8H20V20H8Z M4 16V4H16", null);
        var copyIcon = (Avalonia.Controls.Shapes.Path)copyButton.Content!;
        var copyGeometry = copyIcon.Data;
        var copyStroke = copyIcon.Stroke;
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(code);
            // Show a check mark briefly, as the app does after copying code.
            copyIcon.Data = Geometry.Parse("M5 12L10 17L19 7");
            copyIcon.Stroke = ThemeBrushes.Get("SuccessGreen", "#7FBF8F");
            await Task.Delay(1200);
            copyIcon.Data = copyGeometry;
            copyIcon.Stroke = copyStroke;
        };
        actions.Children.Add(copyButton);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Height = 36,
            Margin = new Thickness(22, 0, 10, 0)
        };
        header.Children.Add(headerStack);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var codeText = new SelectableTextBlock
        {
            Text = code,
            FontFamily = CodeFontFamily,
            FontWeight = CodeFontWeight,
            FontSize = CodeFontSize,
            LineHeight = CodeFontSize + 8,
            Foreground = PrimaryBrush,
            TextWrapping = TextWrapping.NoWrap
        };
        var scroll = new ScrollViewer
        {
            Content = codeText,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 360
        };
        var wrapIcon = (Avalonia.Controls.Shapes.Path)wrapButton.Content!;
        var wrapOffStroke = wrapIcon.Stroke;
        var wrapOnStroke = ThemeBrushes.Get("AccentSoft", "#8FA8FF");
        wrapButton.Click += (_, _) =>
        {
            var wrap = codeText.TextWrapping != TextWrapping.Wrap;
            codeText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            wrapIcon.Stroke = wrap ? wrapOnStroke : wrapOffStroke;
            scroll.HorizontalScrollBarVisibility = wrap
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        };
        ToolTip.SetTip(copyButton, "复制代码");
        var codeArea = new Border
        {
            Child = scroll,
            Background = Brushes.Transparent,
            Padding = new Thickness(20, 16, 20, 14)
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        layout.Children.Add(header);
        Grid.SetRow(codeArea, 1);
        layout.Children.Add(codeArea);
        _root.Children.Add(new Border
        {
            Child = layout,
            Background = ThemeBrushes.Get("CodeBlockFill", "#1E1E20"),
            BorderBrush = ThemeBrushes.Get("CodeActionBorder", "#262628"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true
        });
    }

    private static (string Name, string Extension) CodeFileHint(string language)
    {
        var extension = language.Trim().ToLowerInvariant() switch
        {
            "csharp" or "cs" or "c#" => "cs",
            "rust" or "rs" => "rs",
            "python" or "py" => "py",
            "javascript" or "js" => "js",
            "typescript" or "ts" => "ts",
            "json" => "json",
            "markdown" or "md" => "md",
            "toml" => "toml",
            "bash" or "sh" or "shell" => "sh",
            "html" => "html",
            "css" => "css",
            "xml" => "xml",
            "yaml" or "yml" => "yaml",
            _ => "txt"
        };
        return ($"snippet.{extension}", extension);
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
                Stroke = ThemeBrushes.Get("CodeIcon", "#7E7F82"),
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

    private void AddInline(InlineCollection target, string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var boldStart = text.IndexOf("**", position, StringComparison.Ordinal);
            var codeStart = text.IndexOf((char)96, position);
            var italicStart = IndexOfSingleAsterisk(text, position);
            var next = MinPositive(boldStart, codeStart, italicStart);
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
                        FontFamily = CodeFontFamily,
                        FontWeight = CodeFontWeight,
                        Foreground = PrimaryBrush
                    });
                    position = codeEnd + 1;
                    continue;
                }

                var pill = new Border
                {
                    Background = ThemeBrushes.Get("InlineCodeFill", "#242427"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 0),
                    Margin = new Thickness(1, 0),
                    Height = 17,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    RenderTransform = new TranslateTransform(0, 4),
                    Child = new TextBlock
                    {
                        Text = codeText,
                        FontFamily = CodeFontFamily,
                        FontWeight = CodeFontWeight,
                        FontSize = CodeFontSize - 1,
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

            if (next == italicStart)
            {
                var italicEnd = IndexOfSingleAsterisk(text, italicStart + 1);
                if (italicEnd < 0)
                {
                    target.Add(new Run(text[italicStart..]));
                    break;
                }
                var italic = new Italic();
                AddInline(italic.Inlines!, text[(italicStart + 1)..italicEnd]);
                target.Add(italic);
                position = italicEnd + 1;
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

    private static int IndexOfSingleAsterisk(string text, int start)
    {
        // shortcut: any unpaired single "*" pairs up, so a stray asterisk can italicize nearby text.
        for (var index = text.IndexOf('*', start); index >= 0; index = text.IndexOf('*', index + 1))
        {
            if ((index == 0 || text[index - 1] != '*')
                && (index == text.Length - 1 || text[index + 1] != '*'))
            {
                return index;
            }
        }
        return -1;
    }

    private static int MinPositive(params int[] values)
    {
        var min = -1;
        foreach (var value in values)
        {
            if (value >= 0 && (min < 0 || value < min))
            {
                min = value;
            }
        }
        return min;
    }
}

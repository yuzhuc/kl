using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Kl.Gui.Controls;

internal static class ThemeBrushes
{
    public static IBrush Get(string key, string fallback)
    {
        if (Application.Current is { } application
            && application.TryFindResource(key, application.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }
}

public enum DiffLineKind
{
    Normal,
    Add,
    Remove,
    Hunk,
    Meta
}

public readonly record struct DiffLine(string Text, DiffLineKind Kind);

public static class DiffTextFormatter
{
    public static IReadOnlyList<DiffLine> Format(string? text, bool useMarkers)
    {
        var lines = new List<DiffLine>();
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in normalized.Split('\n'))
        {
            var kind = useMarkers ? DiffLineKind.Normal : Classify(raw);
            var output = raw;
            if (!useMarkers && kind is DiffLineKind.Add or DiffLineKind.Remove && raw.Length > 0)
            {
                output = raw.Length > 1 && raw[1] == ' ' ? raw[2..] : raw[1..];
            }

            lines.Add(new DiffLine(output, kind));
        }

        return lines;
    }

    private static DiffLineKind Classify(string line)
    {
        if (line.StartsWith("diff --git", StringComparison.Ordinal)
            || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("--- ", StringComparison.Ordinal)
            || line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            return DiffLineKind.Meta;
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return DiffLineKind.Hunk;
        }

        if (line.StartsWith('+'))
        {
            return DiffLineKind.Add;
        }

        return line.StartsWith('-') ? DiffLineKind.Remove : DiffLineKind.Normal;
    }
}

/// <summary>Selectable tool detail that tints unified-diff lines or shows +/- markers.</summary>
public sealed class DiffTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<DiffTextBlock, string>(nameof(SourceText), string.Empty);
    public static readonly StyledProperty<bool> UseMarkersProperty =
        AvaloniaProperty.Register<DiffTextBlock, bool>(nameof(UseMarkers));

    static DiffTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<DiffTextBlock>((control, _) => control.Rebuild());
        UseMarkersProperty.Changed.AddClassHandler<DiffTextBlock>((control, _) => control.Rebuild());
    }

    public DiffTextBlock()
    {
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

    private void OnApplicationThemeChanged(object? sender, EventArgs eventArgs) => Rebuild();

    public string SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public bool UseMarkers
    {
        get => GetValue(UseMarkersProperty);
        set => SetValue(UseMarkersProperty, value);
    }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        foreach (var line in DiffTextFormatter.Format(SourceText, UseMarkers))
        {
            inlines.Add(new Run
            {
                Text = line.Text + "\n",
                Foreground = BrushFor(line.Kind)
            });
        }

        Inlines = inlines;
    }

    private static IBrush BrushFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Add => ThemeBrushes.Get("DiffAddText", "#7FBF8F"),
        DiffLineKind.Remove => ThemeBrushes.Get("DiffRemoveText", "#E5837C"),
        DiffLineKind.Hunk => ThemeBrushes.Get("DiffHunkText", "#7EA7E8"),
        DiffLineKind.Meta => ThemeBrushes.Get("DiffMetaText", "#8E8F91"),
        _ => ThemeBrushes.Get("TextDim", "#A8A9AB")
    };
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Kl.Gui.Controls;

/// <summary>
/// TextBlock that tints every case-insensitive occurrence of <see cref="HighlightQuery"/>.
/// Used by palette search results; Text is left untouched when the query is empty.
/// </summary>
public sealed class HighlightTextBlock : TextBlock
{
    private static readonly IBrush HitBackground = new SolidColorBrush(Color.Parse("#453719"));
    private static readonly IBrush HitForeground = new SolidColorBrush(Color.Parse("#F0D9A8"));

    public static readonly StyledProperty<string?> HighlightQueryProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(HighlightQuery));

    public string? HighlightQuery
    {
        get => GetValue(HighlightQueryProperty);
        set => SetValue(HighlightQueryProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HighlightQueryProperty || change.Property == TextProperty)
        {
            RebuildInlines();
        }
    }

    private void RebuildInlines()
    {
        var text = Text;
        var query = HighlightQuery;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            Inlines = null;
            return;
        }

        // Assign a fresh collection: mutating the auto-created one leaves the
        // plain-text run in place, so every hit would render twice.
        var inlines = new InlineCollection();
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                inlines.Add(new Run(text[start..]));
                break;
            }
            if (index > start)
            {
                inlines.Add(new Run(text[start..index]));
            }
            inlines.Add(new Run(text.Substring(index, query.Length))
            {
                Background = HitBackground,
                Foreground = HitForeground
            });
            start = index + query.Length;
        }
        Inlines = inlines;
    }
}

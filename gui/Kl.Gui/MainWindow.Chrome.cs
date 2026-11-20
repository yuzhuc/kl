using System.Collections.Specialized;
using Avalonia;
using System.Diagnostics;
using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Kl.Client;
using Kl.Gui.Services;
using Kl.Gui.ViewModels;

namespace Kl.Gui;

public partial class MainWindow : Window
{
    internal async void OnAppearanceFontSizeCommitted(object? sender, RoutedEventArgs eventArgs) =>
        await CommitAppearanceFontSizeAsync();

    internal async void OnAppearanceFontSizeKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
        {
            return;
        }

        eventArgs.Handled = true;
        await CommitAppearanceFontSizeAsync();
    }

    internal void OnContrastCommitted(object? sender, PointerReleasedEventArgs eventArgs) =>
        _viewModel.SaveAppearanceSettings();

    internal void OnContrastKeyUp(object? sender, KeyEventArgs eventArgs) =>
        _viewModel.SaveAppearanceSettings();

    internal void OnContrastLostFocus(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveAppearanceSettings();

    private async Task CommitAppearanceFontSizeAsync()
    {
        await _viewModel.SaveSettingsAsync();
        ApplyContentFontResources();
    }

    private void ApplyPointerCursor() =>
        Classes.Set("pointer-cursor", _viewModel.UsePointerCursor);

    /// <summary>Re-colours the few accent-keyed resources the app reads at runtime.</summary>
    private void ApplyAccentResources()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        var color = Color.Parse(_viewModel.AccentColorHex);
        var accent = new SolidColorBrush(color);
        application.Resources["AccentBlue"] = accent;
        application.Resources["SliderTrackFill"] = accent;
        application.Resources["SliderTrackFillPointerOver"] = accent;
        application.Resources["SliderTrackFillPressed"] = accent;
        var dim = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B));
        application.Resources["SliderTrackValueFill"] = dim;
        application.Resources["SliderTrackValueFillPointerOver"] = dim;
        application.Resources["SliderTrackValueFillPressed"] = dim;
    }

    private void ApplyContentFontResources()
    {
        if (Application.Current is not { } application)
        {
            return;
        }
        var size = (double)_viewModel.ContentFontSize;
        application.Resources["ChatContentFontSize"] = size;
        application.Resources["ChatCodeFontSize"] = (double)_viewModel.CodeFontSize;
        // Codex keeps the conversation at 14/23; the line-height divisor is fixed.
        application.Resources["ChatContentLineHeight"] = size + 9;
        application.Resources["ChatSecondaryFontSize"] = size - 1;
        application.Resources["ChatSecondaryLineHeight"] = size + 6;
    }

    private void ApplyThemeVariant()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = _viewModel.ThemeMode switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SyncEffectiveTheme();
            ApplyAppearanceResources();
            ApplyAccentResources();
            ApplyDiffMarkerResources();
        }, DispatcherPriority.Background);
    }

    private void SyncEffectiveTheme()
    {
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;
        _viewModel.SetEffectiveTheme(variant == ThemeVariant.Light);
    }

    private void ApplyDiffMarkerResources()
    {
        if (Application.Current is { } application)
        {
            application.Resources["DiffMarkers"] = _viewModel.IsDiffMarkers;
        }
    }

    private void ApplyAppearanceResources()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        ApplyFontResources(application);
        ApplyContrastResources(application);
        ApplySidebarTranslucency(application);
    }

    private void ApplyFontResources(Application application)
    {
        var ui = UiFonts[Math.Clamp(_viewModel.UiFontIndex, 0, UiFonts.Length - 1)];
        var content = _viewModel.ContentFontIndex switch
        {
            1 => UiFonts[0],
            2 => UiFonts[1],
            _ => ui
        };
        var code = CodeFonts[Math.Clamp(_viewModel.CodeFontIndex, 0, CodeFonts.Length - 1)];
        application.Resources["UiFont"] = new FontFamily(ui);
        application.Resources["ContentFont"] = new FontFamily(content);
        application.Resources["CodeFont"] = new FontFamily(code);
        application.Resources["UiFontWeight"] = FontWeights[Math.Clamp(_viewModel.UiFontWeightIndex, 0, 2)];
        application.Resources["ContentFontWeight"] = FontWeights[Math.Clamp(_viewModel.ContentFontWeightIndex, 0, 2)];
        application.Resources["CodeFontWeight"] = FontWeights[Math.Clamp(_viewModel.CodeFontWeightIndex, 0, 2)];
    }

    private void ApplyContrastResources(Application application)
    {
        var contrast = Math.Clamp(_viewModel.Contrast, 0, 100);
        var background = BaseBrushColor(application, "BgBase");
        var highTarget = ActualThemeVariant == ThemeVariant.Light ? Colors.Black : Colors.White;
        foreach (var key in ContrastTextKeys)
        {
            var baseColor = BaseBrushColor(application, key);
            Resources[key] = new SolidColorBrush(contrast switch
            {
                < 50 => Blend(baseColor, background, (50 - contrast) / 50.0 * 0.45),
                > 50 => Blend(baseColor, highTarget, (contrast - 50) / 50.0 * 0.25),
                _ => baseColor
            });
        }
    }

    private void ApplySidebarTranslucency(Application application)
    {
        var background = BaseBrushColor(application, "BgBase");
        var sidebar = BaseBrushColor(application, "SidebarFill");
        var rail = BaseBrushColor(application, "SidebarRailFill");
        var enabled = _viewModel.TranslucentSidebar;
        Resources["BgBase"] = new SolidColorBrush(enabled ? WithAlpha(background, 0) : background);
        Resources["SidebarFill"] = new SolidColorBrush(enabled ? WithAlpha(sidebar, 0xB4) : sidebar);
        Resources["SidebarRailFill"] = new SolidColorBrush(enabled ? WithAlpha(rail, 0xB4) : rail);
        TransparencyLevelHint = enabled
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.None];
    }

    private Color BaseBrushColor(Application application, string key)
    {
        var variant = ActualThemeVariant;
        var cacheKey = (variant, key);
        if (_baseBrushColors.TryGetValue(cacheKey, out var color))
        {
            return color;
        }

        color = application.TryFindResource(key, variant, out var value)
            && value is SolidColorBrush brush
            ? brush.Color
            : Colors.White;
        _baseBrushColors[cacheKey] = color;
        return color;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(from.A, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}

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
    private void OnJumpToBottomClicked(object? sender, RoutedEventArgs eventArgs) =>
        TranscriptScroller.ScrollToEnd();

    // ---- Codex-style interactive ruler: content-generated ticks, hover previews, click jumps ----

    private void OnRulerPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        var position = eventArgs.GetPosition(RulerTicksPanel);
        if (_rulerTickYs.Count == 0 || !TryFindHoveredTick(position.Y, out var index))
        {
            HideRulerPreview();
            return;
        }

        ApplyRulerWave(index);
        UpdateRulerPreview(position.Y, index);
    }

    private void OnRulerPointerExited(object? sender, PointerEventArgs eventArgs) =>
        ScheduleRulerPreviewClose();

    private void OnRulerPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!_viewModel.HasConversation || _rulerTickTargets.Count == 0)
        {
            return;
        }

        if (!TryFindHoveredTick(eventArgs.GetPosition(RulerTicksPanel).Y, out var index))
        {
            return;
        }

        var extent = TranscriptScroller.Extent.Height;
        var viewport = TranscriptScroller.Viewport.Height;
        var target = Math.Clamp(_rulerTickTargets[index].Top - 12, 0, Math.Max(0, extent - viewport));
        TranscriptScroller.Offset = new Vector(0, target);
        HideRulerPreview();
    }

    private void UpdateRulerPreview(double pointerY, int index)
    {
        if (!_viewModel.HasConversation || index < 0 || index >= _rulerTickTargets.Count)
        {
            HideRulerPreview();
            return;
        }

        var extent = TranscriptScroller.Extent.Height;
        var viewport = TranscriptScroller.Viewport.Height;
        if (extent <= viewport + 1)
        {
            RulerPreviewCard.IsVisible = false;
            return;
        }

        _rulerPreviewIndex = index;
        UpdateRulerPreviewIconState();
        PopulateRulerPreview(_rulerTickTargets[index].Item);
        var cardHeight = Math.Max(96, RulerPreviewCard.Bounds.Height);
        var top = Math.Clamp(pointerY - (cardHeight / 2), 8, Math.Max(8, viewport - cardHeight - 8));

        // Position changes are applied instantly: when the pointer stops somewhere new the card
        // must appear there directly, not glide down from the previous position.
        RulerPreviewCard.RenderTransform = TransformOperations.Parse($"translate(0px,{top:0.#}px)");
        if (!RulerPreviewCard.IsVisible)
        {
            RulerPreviewCard.Opacity = 0;
            RulerPreviewCard.IsVisible = true;
            Dispatcher.UIThread.Post(() => RulerPreviewCard.Opacity = 1, DispatcherPriority.Render);
        }
        else
        {
            RulerPreviewCard.Opacity = 1;
        }
    }

    private void ScheduleRulerPreviewClose()
    {
        _rulerPreviewCloseTimer.Stop();
        _rulerPreviewCloseTimer.Start();
    }

    private void OnRulerPreviewPointerEntered(object? sender, PointerEventArgs eventArgs) =>
        _rulerPreviewCloseTimer.Stop();

    private void OnRulerPreviewPointerExited(object? sender, PointerEventArgs eventArgs) =>
        ScheduleRulerPreviewClose();

    private void OnRulerPreviewJumpClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_rulerPreviewIndex < 0 || _rulerPreviewIndex >= _rulerTickTargets.Count)
        {
            return;
        }

        _selectedRulerTickIndex = _selectedRulerTickIndex == _rulerPreviewIndex ? -1 : _rulerPreviewIndex;
        ApplyRulerTickVisuals(_rulerWaveIndex);
        UpdateRulerPreviewIconState();
    }

    private void UpdateRulerPreviewIconState()
    {
        var active = _rulerPreviewIndex >= 0 && _rulerPreviewIndex == _selectedRulerTickIndex;
        RulerPreviewIconButton.Classes.Set("active", active);
    }

    private void HideRulerPreview()
    {
        _rulerPreviewCloseTimer.Stop();
        _rulerPreviewIndex = -1;
        RulerPreviewCard.IsVisible = false;
        RulerPreviewCard.Opacity = 0;
        ResetRulerWave();
    }

    /// <summary>
    /// One ruler tick per user turn (two turns render two ticks, three render three), packed at the
    /// same tight 10px pitch and vertically centred in the ruler column.
    /// </summary>
    private void BuildRulerTicks()
    {
        var height = RulerStrip.Bounds.Height;
        _lastRulerExtent = TranscriptScroller.Extent.Height;
        RulerTicksPanel.Children.Clear();
        _rulerTicks.Clear();
        _rulerTickYs.Clear();
        _rulerTickTargets.Clear();
        _rulerWaveIndex = -1;
        _selectedRulerTickIndex = -1;
        _currentRulerTickIndex = -1;
        _rulerSelectionMarker = null;
        if (height <= 44 || _viewModel.Messages.Count == 0)
        {
            return;
        }

        var turns = new List<(ChatItemViewModel Item, double Top)>();
        for (var index = 0; index < _viewModel.Messages.Count; index++)
        {
            var item = _viewModel.Messages[index];
            if (!item.IsUser)
            {
                continue;
            }

            var container = TranscriptItems.ContainerFromIndex(index);
            if (container is null)
            {
                continue;
            }

            var point = container.TranslatePoint(new Point(0, 0), TranscriptScroller);
            if (point is null)
            {
                continue;
            }

            turns.Add((item, TranscriptScroller.Offset.Y + point.Value.Y));
        }

        if (turns.Count == 0)
        {
            return;
        }

        var usable = height - 36;
        const double preferredPitch = 10;
        var pitch = turns.Count == 1 ? 0 : Math.Min(preferredPitch, usable / (turns.Count - 1));
        var total = pitch * (turns.Count - 1);
        var startY = 18 + Math.Max(0, (usable - total) / 2);
        for (var index = 0; index < turns.Count; index++)
        {
            var y = startY + (index * pitch);
            var tick = CreateRulerTick();
            Canvas.SetTop(tick, y - 1);
            Canvas.SetLeft(tick, 0);
            RulerTicksPanel.Children.Add(tick);
            _rulerTicks.Add(tick);
            _rulerTickYs.Add(y);
            _rulerTickTargets.Add(turns[index]);
        }

        _rulerSelectionMarker = CreateRulerMarker();
        RulerTicksPanel.Children.Add(_rulerSelectionMarker);
        UpdateCurrentRulerTick();
    }

    private static Border CreateRulerTick()
    {
        var tick = new Border
        {
            Width = RulerTickBaseWidth,
            Height = 2,
            Background = RulerTickBrush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative)
        };
        tick.Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(110),
                Easing = new CubicEaseOut()
            },
            new BrushTransition
            {
                Property = BackgroundProperty,
                Duration = TimeSpan.FromMilliseconds(120)
            }
        };
        return tick;
    }

    private bool TryFindHoveredTick(double pointerY, out int index)
    {
        index = -1;
        if (_rulerTickYs.Count == 0)
        {
            return false;
        }

        var nearest = FindNearestTickIndex(pointerY);
        if (Math.Abs(_rulerTickYs[nearest] - pointerY) > RulerHoverRadius)
        {
            return false;
        }

        index = nearest;
        return true;
    }

    private static Border CreateRulerMarker() => new()
    {
        Width = 2,
        Height = 2,
        Background = RulerSelectionMarkerBrush,
        IsVisible = false,
        IsHitTestVisible = false
    };

    private int FindNearestTickIndex(double y)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < _rulerTickYs.Count; index++)
        {
            var distance = Math.Abs(_rulerTickYs[index] - y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        return best;
    }

    private void ApplyRulerWave(int index)
    {
        if (_rulerTicks.Count == 0 || index < 0 || index >= _rulerTicks.Count)
        {
            return;
        }

        _rulerWaveIndex = index;
        ApplyRulerTickVisuals(index);
    }

    private void ApplyRulerTickVisuals(int waveIndex)
    {
        for (var tickIndex = 0; tickIndex < _rulerTicks.Count; tickIndex++)
        {
            var width = RulerTickWidth(tickIndex, waveIndex);
            var tick = _rulerTicks[tickIndex];
            tick.RenderTransform = TransformOperations.Parse($"scaleX({width / RulerTickBaseWidth:0.###})");
            tick.Background = tickIndex == waveIndex
                ? RulerTickActiveBrush
                : tickIndex == _selectedRulerTickIndex
                    ? RulerTickSelectedBrush
                    : tickIndex == _currentRulerTickIndex
                        ? RulerTickCurrentBrush
                        : RulerTickBrush;
        }

        PositionRulerMarker(_rulerSelectionMarker, _selectedRulerTickIndex, waveIndex);
    }

    private double RulerTickWidth(int tickIndex, int waveIndex)
    {
        var distance = waveIndex >= 0 ? Math.Abs(tickIndex - waveIndex) : int.MaxValue;
        return distance switch
        {
            0 => 26,
            1 => 20,
            2 => 14,
            3 => 10,
            _ => tickIndex == _selectedRulerTickIndex ? 8 : (int)RulerTickBaseWidth
        };
    }

    private void PositionRulerMarker(Border? marker, int tickIndex, int waveIndex)
    {
        if (marker is null)
        {
            return;
        }

        var visible = tickIndex >= 0 && tickIndex < _rulerTickYs.Count && tickIndex < _rulerTicks.Count;
        marker.IsVisible = visible;
        if (!visible)
        {
            return;
        }

        Canvas.SetLeft(marker, RulerTickWidth(tickIndex, waveIndex) + 2);
        Canvas.SetTop(marker, _rulerTickYs[tickIndex] - 1);
        marker.Background = waveIndex == tickIndex ? RulerSelectionMarkerHoverBrush : RulerSelectionMarkerBrush;
    }

    /// <summary>Moves the subtle current-position highlight along with the transcript scroll.</summary>
    private void UpdateCurrentRulerTick()
    {
        if (_rulerTicks.Count == 0 || _rulerTickTargets.Count == 0)
        {
            return;
        }

        var offset = TranscriptScroller.Offset.Y;
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < _rulerTickTargets.Count; index++)
        {
            var distance = Math.Abs(_rulerTickTargets[index].Top - offset);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        if (best == _currentRulerTickIndex)
        {
            return;
        }

        _currentRulerTickIndex = best;
        ApplyRulerTickVisuals(_rulerWaveIndex);
    }

    private void ResetRulerWave()
    {
        _rulerWaveIndex = -1;
        ApplyRulerTickVisuals(-1);
    }

    private void PopulateRulerPreview(ChatItemViewModel item)
    {
        var user = item;
        var index = _viewModel.Messages.IndexOf(item);
        for (var cursor = index; cursor >= 0; cursor--)
        {
            if (_viewModel.Messages[cursor].IsUser)
            {
                user = _viewModel.Messages[cursor];
                break;
            }
        }

        if (!user.IsUser)
        {
            user = _viewModel.Messages.FirstOrDefault(message => message.IsUser) ?? item;
        }

        RulerPreviewTitle.Text = FirstPreviewLine(user.Text, 60);
        var userIndex = _viewModel.Messages.IndexOf(user);
        var reply = new System.Text.StringBuilder();
        for (var next = userIndex + 1; next < _viewModel.Messages.Count; next++)
        {
            var candidate = _viewModel.Messages[next];
            if (candidate.IsUser)
            {
                break;
            }

            if (candidate.IsAssistant && !string.IsNullOrWhiteSpace(candidate.Text))
            {
                reply.AppendLine(candidate.Text);
            }
        }

        var (body, bullet) = BuildPreviewText(reply.ToString());
        RulerPreviewBody.Text = body;
        RulerPreviewBody.IsVisible = !string.IsNullOrWhiteSpace(body);
        RulerPreviewBulletRow.IsVisible = !string.IsNullOrWhiteSpace(bullet);
        RulerPreviewBullet.Text = bullet;
    }    private static string FirstPreviewLine(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        line = line.Replace("**", string.Empty).Replace("`", string.Empty);
        return line.Length <= maxLength ? line : line[..maxLength].TrimEnd() + "…";
    }

    private static (string Body, string Bullet) BuildPreviewText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("**", string.Empty)
            .Replace("`", string.Empty)
            .Split('\n')
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToList();

        var body = new System.Text.StringBuilder();
        string? bullet = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                bullet ??= line[2..].Trim();
                continue;
            }

            if (bullet is not null)
            {
                continue;
            }

            if (body.Length > 0)
            {
                body.Append(' ');
            }

            body.Append(line);
            if (body.Length > 150)
            {
                break;
            }
        }

        return (TruncatePreview(body.ToString(), 140), TruncatePreview(bullet ?? string.Empty, 44));
    }
    private static string TruncatePreview(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
}

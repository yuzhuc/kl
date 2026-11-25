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
    private void OnLanguageChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshLocalizedUi();
            return;
        }
        Dispatcher.UIThread.Post(RefreshLocalizedUi);
    }

    private void RefreshLocalizedUi()
    {
        _viewModel.RefreshLocalization();
        ApplySettingsSearch();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.UiFontIndex)
            or nameof(MainWindowViewModel.ContentFontIndex)
            or nameof(MainWindowViewModel.CodeFontIndex)
            or nameof(MainWindowViewModel.UiFontWeightIndex)
            or nameof(MainWindowViewModel.ContentFontWeightIndex)
            or nameof(MainWindowViewModel.CodeFontWeightIndex)
            or nameof(MainWindowViewModel.Contrast)
            or nameof(MainWindowViewModel.TranslucentSidebar))
        {
            ApplyAppearanceResources();
            return;
        }

        if (eventArgs.PropertyName is nameof(MainWindowViewModel.IsChatPage)
            or nameof(MainWindowViewModel.IsProjectsPage)
            or nameof(MainWindowViewModel.IsPluginsPage)
            or nameof(MainWindowViewModel.IsAutomationPage))
        {
            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsTerminalPanelOpen))
        {
            ApplyTerminalPosition();
            if (_viewModel.IsTerminalPanelOpen)
            {
                StartTerminal();
                Dispatcher.UIThread.Post(() => TerminalView.Focus(), DispatcherPriority.Background);
            }

            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.TerminalShellIndex))
        {
            UpdateTerminalShellLabel();
            if (_viewModel.IsTerminalPanelOpen)
            {
                StartTerminal(forceRestart: true);
            }

            return;
        }

        if (eventArgs.PropertyName is nameof(MainWindowViewModel.IsTerminalBottom)
            or nameof(MainWindowViewModel.IsTerminalRight))
        {
            ApplyTerminalPosition();
            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
        {
            if (_viewModel.IsSettingsOpen)
            {
                SettingsNavPanelBorder.Width = Math.Clamp(
                    _viewModel.SidebarWidth, SidebarMinWidth, SidebarMaxWidth);
                SettingsSearchBox.Text = string.Empty;
                ApplySettingsSearch();
                Dispatcher.UIThread.Post(
                    () => SettingsNavScroller.GetLogicalDescendants().OfType<Button>()
                        .FirstOrDefault(button => button.Classes.Contains("active"))
                        ?.BringIntoView(),
                    DispatcherPriority.Background);
            }

            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.HasConversation))
        {
            UpdateJumpButton();
            // The scroll extent is only known after the transcript layout pass.
            Dispatcher.UIThread.Post(() => UpdateJumpButton(), DispatcherPriority.Background);
            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSidebarVisible))
        {
            if (!_viewModel.IsSidebarVisible)
            {
                ProductMenuPopup.IsOpen = false;
                WorkspaceMenuPopup.IsOpen = false;
                CloseProjectSectionMenus();
            }

            AnimateSidebar(_viewModel.IsSidebarVisible);
            return;
        }

        if (eventArgs.PropertyName is nameof(MainWindowViewModel.IsAddProjectPinned)
            or nameof(MainWindowViewModel.IsCodeReviewPinned))
        {
            UpdatePinGlyphs();
            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.ThemeMode))
        {
            ApplyThemeVariant();
            UpdateThemeModeCards();
            return;
        }

        if (eventArgs.PropertyName is nameof(MainWindowViewModel.DiffMarkerMode)
            or nameof(MainWindowViewModel.IsDiffMarkers))
        {
            ApplyDiffMarkerResources();
            return;
        }

        if (eventArgs.PropertyName is nameof(MainWindowViewModel.AccentColorHex)
            or nameof(MainWindowViewModel.AccentColorBrush))
        {
            ApplyAccentResources();
            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.UsePointerCursor))
        {
            ApplyPointerCursor();
            return;
        }

        if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsApprovalOpen))
        {
            return;
        }

        if (!_viewModel.IsApprovalOpen)
        {
            return;
        }

        CloseMoreMenu();
        Dispatcher.UIThread.Post(() =>
        {
            ApprovalAllowButton.Focus();
        }, DispatcherPriority.Background);
    }

    private const double SidebarMinWidth = 238;
    private const double SidebarMaxWidth = 468;

    // Width of the left rail; the sidebar's left edge sits right after it.
    private const double RailWidth = 52;

    // Dragging the splitter below half of the minimum width collapses the sidebar;
    // pulling the collapsed sidebar past the same offset expands it again.
    private const double SidebarCollapseThreshold = SidebarMinWidth * 0.5;

    private bool _sidebarDragging;
    private bool _settingsNavDragging;
    private bool _transcriptPinnedToBottom = true;
    private readonly List<Border> _rulerTicks = new();
    private readonly List<double> _rulerTickYs = new();
    private readonly List<(ChatItemViewModel Item, double Top)> _rulerTickTargets = new();
    private int _rulerWaveIndex = -1;
    private double _lastRulerExtent = -1;
    private int _rulerPreviewIndex = -1;
    private int _selectedRulerTickIndex = -1;
    private int _currentRulerTickIndex = -1;
    private Border? _rulerSelectionMarker;
    private ChatItemViewModel? _jumpHighlightedItem;
    private readonly DispatcherTimer _jumpHighlightTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private readonly DispatcherTimer _rulerPreviewCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private const double RulerTickBaseWidth = 6;
    private const double RulerHoverRadius = 6;
    private static readonly IBrush RulerTickBrush = new SolidColorBrush(Color.Parse("#373739"));
    private static readonly IBrush RulerTickActiveBrush = new SolidColorBrush(Color.Parse("#E2E3E5"));
    private static readonly IBrush RulerTickSelectedBrush = new SolidColorBrush(Color.Parse("#737375"));
    private static readonly IBrush RulerTickCurrentBrush = new SolidColorBrush(Color.Parse("#8E8F91"));
    private static readonly IBrush RulerSelectionMarkerBrush = new SolidColorBrush(Color.Parse("#5E5E61"));
    private static readonly IBrush RulerSelectionMarkerHoverBrush = new SolidColorBrush(Color.Parse("#B6B7B9"));
    private readonly Dictionary<Control, CancellationTokenSource> _sectionAnimations = new();
    private CancellationTokenSource? _sidebarAnimationCts;
    private double _sidebarAnimationTarget;
    private bool _sidebarAnimating;

    /// <summary>Codex-style slide: the sidebar column animates instead of jumping.</summary>
    private void AnimateSidebar(bool visible)
    {
        var expandedWidth = double.IsNaN(_viewModel.SidebarWidth)
            ? SidebarMinWidth
            : Math.Clamp(_viewModel.SidebarWidth, SidebarMinWidth, SidebarMaxWidth);
        if (_viewModel.ReduceMotion)
        {
            CancelSidebarAnimation();
            SidebarPanelBorder.Width = visible ? expandedWidth : 0;
            return;
        }

        AnimateSidebarTo(visible ? expandedWidth : 0, visible);
    }

    /// <summary>
    /// Animates the sidebar width with a cancellable timer loop, so a new
    /// show/hide request interrupts the running animation and continues from
    /// whatever width is visible right now instead of restarting from an end.
    /// </summary>
    private async void AnimateSidebarTo(double target, bool visible)
    {
        CancelSidebarAnimation();

        var current = SidebarPanelBorder.Width;
        var start = double.IsNaN(current) ? (visible ? 0 : SidebarMinWidth) : current;
        _sidebarAnimationTarget = target;

        // Keep the divider line painted while the column slides, so it reads as a
        // moving edge instead of the panel popping in or out.
        SidebarPanelBorder.BorderThickness = new Thickness(0, 0, 1, 0);

        if (Math.Abs(start - target) < 0.5)
        {
            SidebarPanelBorder.Width = target;
            return;
        }

        var cts = new CancellationTokenSource();
        _sidebarAnimationCts = cts;
        _sidebarAnimating = true;

        var stopwatch = Stopwatch.StartNew();
        const double durationMilliseconds = 200;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / durationMilliseconds);
                var eased = 1 - Math.Pow(1 - progress, 3);
                SidebarPanelBorder.Width = start + ((target - start) * eased);
                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(10, cts.Token);
            }

            if (!cts.IsCancellationRequested)
            {
                SidebarPanelBorder.Width = target;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer animation; it continues from the current width.
        }
        finally
        {
            if (ReferenceEquals(_sidebarAnimationCts, cts))
            {
                _sidebarAnimationCts = null;
                _sidebarAnimating = false;
            }

            cts.Dispose();
        }
    }

    private void CancelSidebarAnimation(bool snapToTarget = false)
    {
        if (_sidebarAnimationCts is not { } cts)
        {
            return;
        }

        _sidebarAnimationCts = null;
        _sidebarAnimating = false;
        cts.Cancel();

        if (snapToTarget)
        {
            SidebarPanelBorder.Width = _sidebarAnimationTarget;
        }
    }

    private void OnSidebarResizePressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is Control handle)
        {
            eventArgs.Pointer.Capture(handle);
        }

        // Grabbing the splitter mid-animation stops it where it is.
        CancelSidebarAnimation(snapToTarget: false);
        _sidebarDragging = true;
        eventArgs.Handled = true;
    }

    private void OnSidebarResizeMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_sidebarDragging)
        {
            return;
        }

        // Drive the width straight from the pointer position so the splitter always
        // stays under the cursor: no cumulative drift, no runaway growth.
        var pointerWidth = eventArgs.GetPosition(this).X - RailWidth;

        if (!_viewModel.IsSidebarVisible)
        {
            if (pointerWidth >= SidebarCollapseThreshold)
            {
                // Slide open to the minimum width (not the last saved width): the
                // pressed splitter should land where the drag is, not restore wide.
                _viewModel.SetSidebarVisible(true);
                AnimateSidebarTo(SidebarMinWidth, true);
            }

            eventArgs.Handled = true;
            return;
        }

        if (pointerWidth < SidebarCollapseThreshold)
        {
            // Past half of the minimum width: slide shut to the rail.
            _viewModel.SetSidebarVisible(false);
            eventArgs.Handled = true;
            return;
        }

        // While the open animation is still playing, let it finish rather than
        // fighting the pointer; it is cancelled if the pointer crosses back.
        if (_sidebarAnimating)
        {
            eventArgs.Handled = true;
            return;
        }

        // Hang at the minimum width instead of shrinking below it until the pointer
        // passes the collapse point; above the minimum the edge tracks the cursor 1:1.
        SidebarPanelBorder.Width = Math.Clamp(pointerWidth, SidebarMinWidth, SidebarMaxWidth);
        eventArgs.Handled = true;
    }

    private void OnSidebarResizeReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_sidebarDragging)
        {
            return;
        }

        _sidebarDragging = false;
        if (sender is Control handle)
        {
            eventArgs.Pointer.Capture(null);
        }

        // A show/hide animation that was still running finishes immediately on release.
        CancelSidebarAnimation(snapToTarget: true);

        if (_viewModel.IsSidebarVisible)
        {
            var width = SidebarPanelBorder.Width;
            if (double.IsNaN(width))
            {
                width = _viewModel.SidebarWidth;
            }

            var target = Math.Clamp(width, SidebarMinWidth, SidebarMaxWidth);
            SidebarPanelBorder.Width = target;
            _viewModel.SetSidebarWidth(target);
            AnimateSidebar(true);
        }

        eventArgs.Handled = true;
    }

    // Chromium's bundled column-resize cursor (32x32, hotspot 10,8): a 1px
    // double vertical line with a left/right arrow. SizeWestEast is the plain
    // system double arrow and looks different.
    private static readonly string[] ColumnResizeCursorGlyph =
    {
        ".........#.#....................",
        ".........#.#....................",
        ".........#.#....................",
        ".........#.#....................",
        ".........#.#....................",
        "...#.....#.#.....#..............",
        "..##.....#.#.....##.............",
        ".##......#.#......##............",
        "#######..#.#..#######...........",
        ".##......#.#......##............",
        "..##.....#.#.....##.............",
        "...#.....#.#.....#..............",
        ".........#.#....................",
        ".........#.#....................",
        ".........#.#....................",
        ".........#.#....................",
        ".........#.#....................",
    };

    private static Cursor? CreateColumnResizeCursor()
    {
        try
        {
            var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                for (var y = 0; y < ColumnResizeCursorGlyph.Length; y++)
                {
                    var row = ColumnResizeCursorGlyph[y];
                    for (var x = 0; x < row.Length; x++)
                    {
                        if (row[x] == '#')
                        {
                            context.FillRectangle(Brushes.White, new Rect(x, y, 1, 1));
                        }
                    }
                }
            }

            return new Cursor(bitmap, new PixelPoint(10, 8));
        }
        catch
        {
            // Platform cursor factories may not support bitmap cursors; the XAML
            // SizeWestEast fallback still works there.
            return null;
        }
    }

    private void OnSettingsNavResizePressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is Control handle)
        {
            eventArgs.Pointer.Capture(handle);
        }

        _settingsNavDragging = true;
        eventArgs.Handled = true;
    }

    private void OnSettingsNavResizeMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_settingsNavDragging)
        {
            return;
        }

        SettingsNavPanelBorder.Width = Math.Clamp(
            eventArgs.GetPosition(SettingsPanelBorder).X, SidebarMinWidth, SidebarMaxWidth);
        eventArgs.Handled = true;
    }

    private void OnSettingsNavResizeReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_settingsNavDragging)
        {
            return;
        }

        _settingsNavDragging = false;
        if (sender is Control handle)
        {
            eventArgs.Pointer.Capture(null);
        }

        var width = Math.Clamp(SettingsNavPanelBorder.Width, SidebarMinWidth, SidebarMaxWidth);
        SettingsNavPanelBorder.Width = width;
        _viewModel.SetSidebarWidth(width);
        // Both columns share one width; keep the hidden sidebar in step without
        // expanding it while it is collapsed.
        if (_viewModel.IsSidebarVisible)
        {
            SidebarPanelBorder.Width = width;
        }

        eventArgs.Handled = true;
    }

    /// <summary>Filled pushpin when pinned, outline when not.</summary>
    private void UpdatePinGlyphs()
    {
        var pinnedBrush = new SolidColorBrush(Color.Parse("#C6C7C9"));
        var idleBrush = new SolidColorBrush(Color.Parse("#737374"));

        Apply(PinProjectGlyph, _viewModel.IsAddProjectPinned);
        Apply(PinReviewGlyph, _viewModel.IsCodeReviewPinned);

        void Apply(Avalonia.Controls.Shapes.Path glyph, bool pinned)
        {
            glyph.Fill = pinned ? pinnedBrush : Brushes.Transparent;
            glyph.Stroke = pinned ? pinnedBrush : idleBrush;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.NewItems is not null)
        {
            foreach (ChatItemViewModel item in eventArgs.NewItems)
            {
                item.PropertyChanged += OnMessagePropertyChanged;
            }
        }
        if (eventArgs.OldItems is not null)
        {
            foreach (ChatItemViewModel item in eventArgs.OldItems)
            {
                item.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        if (eventArgs.Action is NotifyCollectionChangedAction.Reset)
        {
            _transcriptPinnedToBottom = true;
        }

        Dispatcher.UIThread.Post(BuildRulerTicks, DispatcherPriority.Background);
        UpdateJumpButton();
        ScrollTranscript(force: eventArgs.Action is NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Reset);
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        // Only follow streaming output. Expanding/collapsing activity rows must grow
        // downward in place: re-pinning to the bottom here made long panels push the
        // whole transcript up (the reported "展开就跳到底部" bug).
        if (eventArgs.PropertyName is nameof(ChatItemViewModel.Text)
            or nameof(ChatItemViewModel.Detail))
        {
            ScrollTranscript();
        }
    }

    private void ScrollTranscript(bool force = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.IsChatPage && _viewModel.HasConversation && (force || _transcriptPinnedToBottom))
            {
                TranscriptScroller.ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        if (TranscriptScroller.Viewport.Height <= 0)
        {
            return;
        }

        var distanceFromBottom = TranscriptScroller.Extent.Height
            - TranscriptScroller.Viewport.Height
            - TranscriptScroller.Offset.Y;
        _transcriptPinnedToBottom = distanceFromBottom <= 32;
        if (Math.Abs(TranscriptScroller.Extent.Height - _lastRulerExtent) > 0.5)
        {
            Dispatcher.UIThread.Post(BuildRulerTicks, DispatcherPriority.Background);
        }
        else
        {
            UpdateCurrentRulerTick();
        }


        UpdateJumpButton(distanceFromBottom);
    }

    private void UpdateJumpButton(double? distanceFromBottom = null)
    {
        if (!_viewModel.HasConversation)
        {
            JumpToBottomButton.IsVisible = false;
            JumpToBottomButton.IsHitTestVisible = false;
            return;
        }

        var distance = distanceFromBottom
            ?? TranscriptScroller.Extent.Height
               - TranscriptScroller.Viewport.Height
               - TranscriptScroller.Offset.Y;
        var showJump = distance > 32;
        JumpToBottomButton.IsVisible = true;
        JumpToBottomButton.IsHitTestVisible = showJump;
        JumpToBottomButton.Opacity = showJump ? 1 : 0;
        JumpToBottomButton.RenderTransform = TransformOperations.Parse(
            showJump ? "translate(0px,0px)" : "translate(0px,6px)");
    }
}

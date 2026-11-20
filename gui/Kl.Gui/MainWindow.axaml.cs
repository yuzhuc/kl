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
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _automationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _providerRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _moreMenuOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _moreMenuCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _previewCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _previewShowTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _sectionSubmenuCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private static readonly string[] UiFonts =
    [
        "Inter, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI",
        "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"
    ];
    private static readonly string[] CodeFonts =
    [
        "Geist Mono, Cascadia Mono, Consolas",
        "JetBrains Mono, Cascadia Mono, Consolas",
        "Cascadia Mono, Consolas"
    ];
    private static readonly FontWeight[] FontWeights =
        [FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold];
    private static readonly string[] ContrastTextKeys =
    [
        "TextPrimary", "TextSecondary", "TextTertiary", "TextCaption",
        "SidebarText", "SidebarTextHover", "SidebarLabel"
    ];
    private readonly Dictionary<(ThemeVariant Variant, string Key), Color> _baseBrushColors = new();
    private SessionItemViewModel? _pinnedDragSession;
    private SessionItemViewModel? _nestedDragSession;
    private WorkspaceItemViewModel? _workspaceDragTarget;
    private Point _pinnedDragOrigin;
    private bool _pinnedDragging;
    private bool _pinnedDragSkipClick;
    private Control? _previewPendingRow;
    private SessionItemViewModel? _previewPendingSession;
    private WorkspaceItemViewModel? _previewPendingWorkspace;
    private bool _previewPendingIsWorkspace;
    private int _dragMode;
    private long _lastRailPointerNavigation;
    private bool _moreMenuPinned;
    private bool _moreMenuHovered;
    private bool _windowActive = true;
    private bool _forceClose;
    private bool _closeRequestedByShortcut;

    public MainWindow()
    {
        var stateStore = new StateStore();
        var state = stateStore.Load();
        var remoteEndpoint = string.IsNullOrWhiteSpace(state.RemoteCoreEndpoint)
            ? null
            : state.RemoteCoreEndpoint.Trim();
        _viewModel = new MainWindowViewModel(
            stateStore,
            coreSessionService: new CoreSessionService(new KlClientOptions
            {
                Endpoint = remoteEndpoint,
                Token = ReadRemoteToken(KlDataPaths.Default.RemoteTokenFilePath),
                TlsFingerprint = string.IsNullOrWhiteSpace(state.RemoteCoreTlsFingerprint)
                    ? null
                    : state.RemoteCoreTlsFingerprint.Trim()
            }),
            dispatcher: action => Dispatcher.UIThread.Post(action));
        ApplyThemeVariant();
        InitializeComponent();
        ApplyStartupWindowSize();
        // Grayscale antialiasing matches the flat Codex look; subpixel AA fringes
        // badly on near-black surfaces.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        DataContext = _viewModel;
        if (Application.Current is { } application)
        {
            application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        }

        SyncEffectiveTheme();
        _viewModel.AttentionRequested += OnAttentionRequested;
        Activated += OnWindowActivated;
        Opened += (_, _) =>
        {
            WireSettingsChevrons();
            ApplyAppearanceResources();
        };
        Closed += (_, _) => TerminalView.Dispose();
        WireComboBoxMenus();
        SettingsNavScroller.SizeChanged += (_, _) => FitSettingsNavWidth();
        _viewModel.PropertyChanged += OnSettingsPaneChanged;
        _providerRefreshTimer.Tick += OnProviderRefreshTimerTick;
        // Popup contents live in their own visual tree; give the More menu the
        // view-model explicitly so the pin bindings (Classes.pinned) resolve.
        MoreMenuCard.DataContext = _viewModel;
        ProjectSectionMenuCard.DataContext = _viewModel;
        ProjectSectionSubmenuCard.DataContext = _viewModel;
        PinnedSectionMenuCard.DataContext = _viewModel;
        NotificationMenuPopup.DataContext = _viewModel;
        ApplyContentFontResources();
        ApplyAppearanceResources();
        ApplyAccentResources();
        ApplyDiffMarkerResources();
        ApplyPointerCursor();
        ApplyTerminalPosition();
        UpdateTerminalShellLabel();
        if (_viewModel.IsTerminalPanelOpen)
        {
            StartTerminal();
            Dispatcher.UIThread.Post(() => TerminalView.Focus(), DispatcherPriority.Background);
        }
        UpdatePinGlyphs();
        SidebarPanelBorder.Width = _viewModel.IsSidebarVisible
            ? Math.Clamp(_viewModel.SidebarWidth, SidebarMinWidth, SidebarMaxWidth)
            : 0;
        if (CreateColumnResizeCursor() is { } columnResizeCursor)
        {
            SidebarResizeHandle.Cursor = columnResizeCursor;
            SettingsNavResizeHandle.Cursor = columnResizeCursor;
        }
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        RulerStrip.SizeChanged += (_, _) => BuildRulerTicks();
        _rulerPreviewCloseTimer.Tick += (_, _) =>
        {
            _rulerPreviewCloseTimer.Stop();
            HideRulerPreview();
        };
        _jumpHighlightTimer.Tick += (_, _) =>
        {
            _jumpHighlightTimer.Stop();
            _jumpHighlightedItem?.SetJumpHighlighted(false);
            _jumpHighlightedItem = null;
        };
        TranscriptScroller.ScrollChanged += OnTranscriptScrollChanged;
        foreach (var item in _viewModel.Messages)
        {
            item.PropertyChanged += OnMessagePropertyChanged;
        }
        _automationTimer.Tick += OnAutomationTimerTick;
        _automationTimer.Start();
        foreach (var (popup, card) in new (Popup Popup, Border Card)[]
        {
            (ProductMenuPopup, ProductMenuCard),
            (NotificationMenuPopup, NotificationMenuCard),
            (WorkspaceMenuPopup, WorkspaceMenuCard),
            (PinnedSectionMenuPopup, PinnedSectionMenuCard)
        })
        {
            popup.Opened += (_, _) => card.Opacity = 1;
            popup.Closed += (_, _) => card.Opacity = 0;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        MorePopup.PlacementTarget = MoreButton;
        ComposerMenuPopup.PlacementTarget = ComposerCard;
        // Codex keeps the settings column at a fixed, centred width instead of
        // letting it stretch edge to edge on wide windows.
        SettingsScroller.SizeChanged += (_, _) => SettingsColumn.Width = Math.Min(
            760,
            Math.Max(320, SettingsScroller.Bounds.Width - SettingsScroller.Padding.Left
                - SettingsScroller.Padding.Right));
        DragDrop.SetAllowDrop(ComposerCard, true);
        ComposerCard.AddHandler(DragDrop.DragOverEvent, OnComposerDragOver,
            RoutingStrategies.Bubble, handledEventsToo: true);
        ComposerCard.AddHandler(DragDrop.DropEvent, OnComposerDrop,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _moreMenuOpenTimer.Tick += OnMoreMenuOpenTimerTick;
        _moreMenuCloseTimer.Tick += OnMoreMenuCloseTimerTick;
        _previewCloseTimer.Tick += OnPreviewCloseTimerTick;
        _previewShowTimer.Tick += OnPreviewShowTimerTick;
        _sectionSubmenuCloseTimer.Tick += (_, _) =>
        {
            _sectionSubmenuCloseTimer.Stop();
            ProjectSectionSubmenuCard.IsVisible = false;
        };
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPinnedDragPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPinnedDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPinnedDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // App shortcuts win over focused controls (the terminal consumes every Ctrl+letter).
        AddHandler(KeyDownEvent, OnWindowShortcutTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        ConfigureRailPointerNavigation();

        // Test hook: open the command palette with a query (IME-safe verification).
        var e2ePalette = Environment.GetEnvironmentVariable("KL_GUI_E2E_PALETTE");
        if (!string.IsNullOrEmpty(e2ePalette))
        {
            DispatcherTimer.RunOnce(() =>
            {
                Activate();
                _viewModel.OpenSearchPalette();
                _viewModel.SearchText = e2ePalette;
                PaletteSearchBox.CaretIndex = e2ePalette.Length;
                PaletteSearchBox.Focus();
            }, TimeSpan.FromMilliseconds(1200));
        }

        // Test hook: preload composer text (and the caret) for the "/"/"@" menus.
        var e2eComposer = Environment.GetEnvironmentVariable("KL_GUI_E2E_COMPOSER");
        if (!string.IsNullOrEmpty(e2eComposer))
        {
            DispatcherTimer.RunOnce(() =>
            {
                Activate();
                var box = _viewModel.HasConversation ? ChatComposerBox : HeroComposerBox;
                box.Text = e2eComposer;
                box.CaretIndex = e2eComposer.Length;
                box.Focus();
                RefreshComposerSuggestions(box);
            }, TimeSpan.FromMilliseconds(900));
        }
    }

    /// <summary>
    /// Same rule as the Codex desktop window: 85% × 80% of the work area,
    /// capped at 1280 × 820, and never below this layout's minimum.
    /// </summary>
    private void ApplyStartupWindowSize()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        Width = Math.Max(MinWidth, Math.Min(1280, Math.Round(0.85 * screen.WorkingArea.Width / scale)));
        Height = Math.Max(MinHeight, Math.Min(820, Math.Round(0.8 * screen.WorkingArea.Height / scale)));
    }

    private void FocusComposer()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_viewModel.IsChatPage)
            {
                return;
            }
            if (_viewModel.IsEmpty)
            {
                HeroComposerBox.Focus();
            }
            else
            {
                ChatComposerBox.Focus();
            }
        }, DispatcherPriority.Background);
    }

    // ---- Codex-style frameless title bar ---------------------------------

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        try
        {
            BeginMoveDrag(eventArgs);
        }
        catch (InvalidOperationException)
        {
            // The window manager rejected the drag (e.g. during a state change).
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs eventArgs)
    {
        ToggleWindowState();
        eventArgs.Handled = true;
    }

    private void OnAttentionRequested(AttentionKind kind, bool sound)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (kind == AttentionKind.Approval)
            {
                if (!_viewModel.ApprovalNotifications || _windowActive)
                {
                    return;
                }
            }
            else if (kind == AttentionKind.Question)
            {
                if (!_viewModel.QuestionNotifications || _windowActive)
                {
                    return;
                }
            }
            else if (kind == AttentionKind.Automation)
            {
                if (!_viewModel.AutomationNotifications || _windowActive)
                {
                    return;
                }
            }
            else
            {
                var mode = _viewModel.TurnNotificationMode;
                if (mode == "never" || (mode == "unfocused" && _windowActive))
                {
                    return;
                }
            }

            NotificationEffects.Attention(
                TryGetPlatformHandle()?.Handle ?? 0,
                sound && _viewModel.NotificationSound);
        });
    }

    private void OnConfirmCloseClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _forceClose = true;
        _viewModel.CloseCloseConfirm();
        Close();
    }

    private void OnCancelCloseClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseCloseConfirm();

    private void OnMinimizeClicked(object? sender, RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object? sender, RoutedEventArgs eventArgs) =>
        ToggleWindowState();

    private void OnCloseWindowClicked(object? sender, RoutedEventArgs eventArgs) => Close();

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        if (this.FindControl<Avalonia.Controls.Shapes.Path>("MaximizeGlyph") is not { } glyph)
        {
            return;
        }

        glyph.Data = StreamGeometry.Parse(WindowState == WindowState.Maximized
            ? "M8 4H20V16 M4 8H16V20H4Z"
            : "M4 4H20V20H4Z");
    }

    private void OnToggleSidebarClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetSidebarVisible(!_viewModel.IsSidebarVisible);

    private void OnProductMenuClicked(object? sender, RoutedEventArgs eventArgs) =>
        ProductMenuPopup.IsOpen = !ProductMenuPopup.IsOpen;

    private void OnChatModeMenuClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetInteractionMode(workMode: false);
        ProductMenuPopup.IsOpen = false;
    }

    private void OnWorkModeMenuClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetInteractionMode(workMode: true);
        ProductMenuPopup.IsOpen = false;
    }

    private void OnToggleSearchClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.ToggleSearch();
        if (_viewModel.IsSearchVisible)
        {
            Dispatcher.UIThread.Post(() =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }, DispatcherPriority.Background);
        }
    }

    private void OnToggleRecentClicked(object? sender, RoutedEventArgs eventArgs) =>
        AnimateSection(RecentItemsControl, _viewModel.IsRecentExpanded, _viewModel.ToggleRecent);

    private void OnTogglePinnedSectionClicked(object? sender, RoutedEventArgs eventArgs) =>
        AnimateSection(PinnedItemsControl, _viewModel.IsPinnedExpanded, _viewModel.TogglePinned);

    /// <summary>
    /// Collapses or expands a sidebar list with a 160ms height ease. A collapse
    /// animates while the list is still visible, then commits the toggle; an
    /// expansion measures the list, then grows it from zero.
    /// </summary>
    private async void AnimateSection(Control? list, bool expanded, Action toggle)
    {
        if (list is null || _viewModel.ReduceMotion)
        {
            toggle();
            return;
        }

        if (_sectionAnimations.Remove(list, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _sectionAnimations[list] = cancellation;
        try
        {
            if (expanded)
            {
                var start = list.Bounds.Height;
                if (start > 0)
                {
                    list.ClipToBounds = true;
                    await AnimateHeightAsync(list, start, 0, cancellation.Token);
                }

                toggle();
            }
            else
            {
                toggle();
                list.Measure(new Size(
                    SidebarPanelBorder.Width > 0 ? SidebarPanelBorder.Width : 240,
                    double.PositiveInfinity));
                var target = list.DesiredSize.Height;
                if (target > 0)
                {
                    list.Height = 0;
                    list.ClipToBounds = true;
                    await AnimateHeightAsync(list, 0, target, cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer toggle.
        }
        finally
        {
            list.Height = double.NaN;
            if (_sectionAnimations.TryGetValue(list, out var current) && ReferenceEquals(current, cancellation))
            {
                _sectionAnimations.Remove(list);
                cancellation.Dispose();
            }
        }
    }

    private static async Task AnimateHeightAsync(
        Control list,
        double from,
        double to,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        const double durationMilliseconds = 160;
        while (true)
        {
            var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / durationMilliseconds);
            var eased = 1 - Math.Pow(1 - progress, 3);
            list.Height = from + ((to - from) * eased);
            if (progress >= 1)
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}

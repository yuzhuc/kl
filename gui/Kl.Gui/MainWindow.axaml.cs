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
    private readonly DispatcherTimer _railHintOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
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
    private Control? _railHintTarget;
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
        _railHintOpenTimer.Tick += OnRailHintOpenTimerTick;
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

    private void OnWindowPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs) =>
        HidePreviewCards();

    private void ConfigureRailPointerNavigation()
    {
        ChatRailButton.AddHandler(
            PointerPressedEvent,
            (_, eventArgs) => ActivateRailSectionFromPointer(eventArgs, AppSection.Chat),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PluginsRailButton.AddHandler(
            PointerPressedEvent,
            (_, eventArgs) => ActivateRailSectionFromPointer(eventArgs, AppSection.Plugins),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AutomationRailButton.AddHandler(
            PointerPressedEvent,
            (_, eventArgs) => ActivateRailSectionFromPointer(eventArgs, AppSection.Automation),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }


    private void OnRailHintPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is not Control { Tag: string label } target)
        {
            return;
        }

        _railHintOpenTimer.Stop();
        _railHintTarget = target;
        RailHintText.Text = label;
        _railHintOpenTimer.Start();
    }

    private void OnRailHintPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (ReferenceEquals(sender, _railHintTarget))
        {
            HideRailHint();
        }
    }

    private void OnRailHintOpenTimerTick(object? sender, EventArgs eventArgs)
    {
        _railHintOpenTimer.Stop();
        if (_railHintTarget?.IsPointerOver != true)
        {
            return;
        }

        RailHintCard.Opacity = 0;
        RailHintCard.IsVisible = true;
        RailHintCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var position = _railHintTarget.TranslatePoint(
            new Point(_railHintTarget.Bounds.Width + 8, 0),
            this);
        if (position is null)
        {
            HideRailHint();
            return;
        }

        var top = position.Value.Y + ((_railHintTarget.Bounds.Height - RailHintCard.DesiredSize.Height) / 2);
        Canvas.SetLeft(RailHintCard, position.Value.X);
        Canvas.SetTop(RailHintCard, top);
        RailHintCard.Opacity = 1;
    }

    private void HideRailHint()
    {
        _railHintOpenTimer.Stop();
        _railHintTarget = null;
        RailHintCard.Opacity = 0;
        RailHintCard.IsVisible = false;
    }

    private void OnMoreButtonPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        HideRailHint();
        _moreMenuHovered = true;
        _moreMenuCloseTimer.Stop();
        if (!_moreMenuPinned && !MorePopup.IsOpen)
        {
            _moreMenuOpenTimer.Start();
        }
    }

    private void OnMoreButtonPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        _moreMenuHovered = false;
        _moreMenuOpenTimer.Stop();
        ScheduleMoreMenuClose();
    }

    private void OnMoreMenuPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        _moreMenuHovered = true;
        _moreMenuCloseTimer.Stop();
    }

    private void OnMoreMenuPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        _moreMenuHovered = false;
        ScheduleMoreMenuClose();
    }

    private void OnMoreMenuOpenTimerTick(object? sender, EventArgs eventArgs)
    {
        _moreMenuOpenTimer.Stop();
        if (!_moreMenuPinned && _moreMenuHovered)
        {
            OpenMoreMenu();
        }
    }

    private void OnMoreMenuCloseTimerTick(object? sender, EventArgs eventArgs)
    {
        _moreMenuCloseTimer.Stop();
        if (!_moreMenuPinned && !_moreMenuHovered)
        {
            MorePopup.IsOpen = false;
        }
    }

    private void OnMoreButtonClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideRailHint();
        _moreMenuOpenTimer.Stop();
        _moreMenuCloseTimer.Stop();
        if (_moreMenuPinned)
        {
            _moreMenuPinned = false;
            MorePopup.IsOpen = false;
            return;
        }

        _moreMenuPinned = true;
        OpenMoreMenu();
    }

    private void OnMorePopupOpened(object? sender, EventArgs eventArgs)
    {
        MoreButton.Classes.Set("open", true);
        Dispatcher.UIThread.Post(() =>
        {
            MoreMenuCard.Opacity = 1;
            SetMoreMenuSlide(0);
        });
    }

    private void OnMorePopupClosed(object? sender, EventArgs eventArgs)
    {
        MoreButton.Classes.Set("open", false);
        MoreMenuCard.Opacity = 0;
        SetMoreMenuSlide(-3);
        _moreMenuOpenTimer.Stop();
        _moreMenuCloseTimer.Stop();
        _moreMenuPinned = false;
        _moreMenuHovered = MoreButton.IsPointerOver;
    }

    private void OnMoreMenuRowPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        // Kept for the pre-Codex popup markup; the new popup rows do not use pins.
    }

    private void OnMoreMenuRowPointerExited(object? sender, PointerEventArgs eventArgs)
    {
    }

    private void OnToggleAddProjectPinnedClicked(object? sender, RoutedEventArgs eventArgs)
    {
        // Pin buttons sit inside a clickable row: stop the row navigation.
        eventArgs.Handled = true;
        _viewModel.ToggleAddProjectPinned();
    }

    private void OnToggleCodeReviewPinnedClicked(object? sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        _viewModel.ToggleCodeReviewPinned();
    }

    private void OnPinnedRailItemClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: PinnedRailItemViewModel item })
        {
            return;
        }

        if (item.IsProject)
        {
            ActivateRailSection(AppSection.Projects);
            return;
        }

        // Already on the code-review draft: don't wipe the composer again.
        if (item.IsActive)
        {
            FocusComposer();
            return;
        }

        CloseMoreMenu();
        _viewModel.StartCodeReviewDraft();
        FocusComposer();
    }

    private void OnMoreProjectClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMoreMenu();
        ActivateRailSection(AppSection.Projects);
    }

    private void OnMoreCodeReviewClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMoreMenu();
        if (!_viewModel.IsCodeReviewDraftActive)
        {
            _viewModel.StartCodeReviewDraft();
        }
        FocusComposer();
    }

    private void ScheduleMoreMenuClose()
    {
        if (!_moreMenuPinned)
        {
            _moreMenuCloseTimer.Stop();
            _moreMenuCloseTimer.Start();
        }
    }

    private void OpenMoreMenu()
    {
        if (MorePopup.IsOpen)
        {
            return;
        }

        MoreMenuCard.Opacity = 0;
        SetMoreMenuSlide(-3);
        MorePopup.IsOpen = true;
    }

    private void SetMoreMenuSlide(double x)
    {
        if (MoreMenuCard.RenderTransform is TranslateTransform transform)
        {
            transform.X = x;
        }
    }

    private void CloseMoreMenu()
    {
        _moreMenuOpenTimer.Stop();
        _moreMenuCloseTimer.Stop();
        _moreMenuPinned = false;
        _moreMenuHovered = false;
        MorePopup.IsOpen = false;
    }

    private void HideRailHintAndMoreMenu()
    {
        HideRailHint();
        CloseMoreMenu();
    }

    private async void OnAttachFilesClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择文本或代码附件",
            AllowMultiple = true
        });
        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>();
        _viewModel.AddAttachments(paths);
        FocusComposer();
    }

    private void OnComposerDragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = eventArgs.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnComposerDrop(object? sender, DragEventArgs eventArgs)
    {
        var paths = (eventArgs.DataTransfer.TryGetFiles() ?? [])
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }
        _viewModel.AddAttachments(paths);
        FocusComposer();
        eventArgs.Handled = true;
    }

    private void OnRemoveAttachmentClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: AttachmentItemViewModel attachment })
        {
            _viewModel.RemoveAttachment(attachment);
        }
    }

    private async void OnSendClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_viewModel.CanSend)
        {
            return;
        }
        await _viewModel.SendAsync();
        FocusComposer();
        ScrollTranscript();
    }

    private async void OnCancelClicked(object? sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.CancelAsync();
    }

    private async void OnNewSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        // Clicking new chat while a turn is streaming used to be ignored, which
        // felt like the old conversation jumping back once it finished. Stop the
        // current turn first so the new chat actually opens.
        if (_viewModel.IsBusy)
        {
            await _viewModel.CancelAsync();
        }

        _viewModel.Navigate(AppSection.Chat);
        await _viewModel.NewSessionAsync();
        FocusComposer();
    }

    private async void OnSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_pinnedDragSkipClick)
        {
            _pinnedDragSkipClick = false;
            return;
        }

        if (sender is Button { DataContext: SessionItemViewModel session })
        {
            // Clicking the already-open session must not reload it: reopening
            // clears the composer draft and resets the message scroll.
            if (session.IsSelected)
            {
                FocusComposer();
                return;
            }

            _viewModel.CloseSettings();
            _viewModel.Navigate(AppSection.Chat);
            await _viewModel.OpenSessionAsync(session);
            FocusComposer();
        }
    }

    private async void OnWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_pinnedDragSkipClick)
        {
            _pinnedDragSkipClick = false;
            return;
        }

        if (sender is Button { DataContext: WorkspaceItemViewModel workspace })
        {
            _viewModel.Navigate(AppSection.Chat);
            await _viewModel.SelectWorkspaceAsync(workspace);
            FocusComposer();
        }
    }

    private async void OnNewWorkspaceSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: WorkspaceItemViewModel workspace })
        {
            _viewModel.Navigate(AppSection.Chat);
            await _viewModel.SelectWorkspaceAsync(workspace);
            await _viewModel.NewSessionAsync();
            FocusComposer();
        }
    }

    private void OnWorkspaceMoreClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HidePreviewCards();
        if (GetWorkspace(sender) is not { } workspace || sender is not Control anchor)
        {
            return;
        }

        WorkspaceMenuPopup.DataContext = workspace;
        WorkspaceMenuPopup.PlacementTarget = anchor;
        WorkspaceMenuPopup.IsOpen = !WorkspaceMenuPopup.IsOpen;
        eventArgs.Handled = true;
    }

    private void OnWorkspaceEditClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HidePreviewCards();
        if (GetWorkspace(sender) is not { } workspace)
        {
            return;
        }

        WorkspaceMenuPopup.IsOpen = false;
        _viewModel.BeginRenameWorkspace(workspace);
        Dispatcher.UIThread.Post(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void OnToggleWorkspacePinnedClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (WorkspaceMenuPopup.DataContext is WorkspaceItemViewModel workspace)
        {
            _viewModel.ToggleWorkspacePinned(workspace);
        }

        WorkspaceMenuPopup.IsOpen = false;
    }

    private void OnRenameWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (WorkspaceMenuPopup.DataContext is not WorkspaceItemViewModel workspace)
        {
            return;
        }

        WorkspaceMenuPopup.IsOpen = false;
        _viewModel.BeginRenameWorkspace(workspace);
        Dispatcher.UIThread.Post(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void OnWorkspaceAgentsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        WorkspaceMenuPopup.IsOpen = false;
        if (WorkspaceMenuPopup.DataContext is WorkspaceItemViewModel workspace)
        {
            _viewModel.OpenProjectAgentsEditor(workspace);
        }
    }

    private void OnArchiveWorkspaceChatsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (WorkspaceMenuPopup.DataContext is WorkspaceItemViewModel workspace)
        {
            _viewModel.ArchiveWorkspaceChats(workspace);
        }

        WorkspaceMenuPopup.IsOpen = false;
    }

    private void OnWorkspacePinMenuClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if ((sender as Control)?.DataContext is WorkspaceItemViewModel workspace)
        {
            _viewModel.ToggleWorkspacePinned(workspace);
        }
    }

    private void OnWorkspaceArchiveMenuClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if ((sender as Control)?.DataContext is WorkspaceItemViewModel workspace)
        {
            _viewModel.ArchiveWorkspaceChats(workspace);
        }
    }

    private void OnWorkspaceRemoveMenuClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if ((sender as Control)?.DataContext is WorkspaceItemViewModel workspace)
        {
            _viewModel.RemoveWorkspace(workspace);
        }
    }

    private void OnRemoveWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (WorkspaceMenuPopup.DataContext is WorkspaceItemViewModel workspace)
        {
            _viewModel.RemoveWorkspace(workspace);
        }

        WorkspaceMenuPopup.IsOpen = false;
    }

    private void OnProjectSectionMoreClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HidePreviewCards();
        if (sender is not Control anchor)
        {
            return;
        }

        if (SectionMenuOverlay.IsVisible)
        {
            CloseProjectSectionMenus();
            eventArgs.Handled = true;
            return;
        }

        var origin = anchor.TranslatePoint(new Point(0, 0), SectionMenuOverlay) ?? new Point(0, 0);
        Canvas.SetLeft(SectionMenuStack, origin.X);
        Canvas.SetTop(SectionMenuStack, origin.Y + anchor.Bounds.Height + 4);
        ProjectSectionSubmenuCard.IsVisible = false;
        SectionMenuOverlay.Opacity = 1;
        SectionMenuOverlay.IsVisible = true;
        eventArgs.Handled = true;
    }

    private void OnPinnedSectionMoreClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HidePreviewCards();
        if (sender is not Control anchor)
        {
            return;
        }

        PinnedSectionMenuPopup.PlacementTarget = anchor;
        PinnedSectionMenuPopup.IsOpen = !PinnedSectionMenuPopup.IsOpen;
        eventArgs.Handled = true;
    }

    private void OnPinnedSortRecentClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetPinnedSortMode("recent");
        PinnedSectionMenuPopup.IsOpen = false;
    }

    private void OnPinnedSortManualClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetPinnedSortMode("manual");
        PinnedSectionMenuPopup.IsOpen = false;
    }

    private void OnSessionRowPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is not Control row || row.DataContext is not SessionItemViewModel session)
        {
            return;
        }

        _previewCloseTimer.Stop();
        _previewShowTimer.Stop();
        if (SessionPreviewCard.IsVisible || WorkspacePreviewCard.IsVisible)
        {
            // A bubble is already up: switch rows instantly so fast downward
            // sweeps never feel one row behind the pointer.
            ShowSessionPreview(row, session);
            return;
        }

        _previewPendingRow = row;
        _previewPendingSession = session;
        _previewPendingWorkspace = null;
        _previewPendingIsWorkspace = false;
        _previewShowTimer.Start();
    }

    private void OnSessionRowPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        _previewShowTimer.Stop();
        StartPreviewClose();
    }

    private void OnPinnedDragPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        _pinnedDragSession = null;
        _nestedDragSession = null;
        _workspaceDragTarget = null;
        _dragMode = 0;

        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        switch (FindDragContext(eventArgs.Source as Visual))
        {
            case SessionItemViewModel { IsPinned: true } pinned when _viewModel.IsPinnedSortByManual:
                _pinnedDragSession = pinned;
                _dragMode = 1;
                break;
            case SessionItemViewModel nested:
                _nestedDragSession = nested;
                _dragMode = 2;
                break;
            case WorkspaceItemViewModel workspace:
                _workspaceDragTarget = workspace;
                _dragMode = 3;
                break;
            default:
                return;
        }

        _pinnedDragOrigin = eventArgs.GetPosition(this);
        _pinnedDragging = false;
        HideDropIndicator();
    }

    private void OnPinnedDragMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_dragMode == 0)
        {
            return;
        }

        var position = eventArgs.GetPosition(this);
        if (!_pinnedDragging)
        {
            if (Math.Abs(position.Y - _pinnedDragOrigin.Y) <= 6)
            {
                return;
            }

            _pinnedDragging = true;
            HidePreviewCards();
        }

        UpdateDropIndicator(position.Y);
    }

    private void OnPinnedDragReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_pinnedDragging || _dragMode == 0)
        {
            HideDropIndicator();
            _dragMode = 0;
            _pinnedDragSession = null;
            _nestedDragSession = null;
            _workspaceDragTarget = null;
            return;
        }

        var y = eventArgs.GetPosition(this).Y;
        switch (_dragMode)
        {
            case 1 when _pinnedDragSession is { } pinned:
                _viewModel.MovePinnedSession(pinned, DropIndexInPinned(y));
                break;
            case 2 when _nestedDragSession is { } nested:
                _viewModel.MoveSessionWithinWorkspace(nested, DropIndexAmongSessionRows(nested.Workspace, y));
                break;
            case 3 when _workspaceDragTarget is { } workspace:
                _viewModel.MoveWorkspace(workspace, DropIndexAmongWorkspaceRows(y));
                break;
        }

        _pinnedDragSkipClick = true;
        HideDropIndicator();
        _dragMode = 0;
        _pinnedDragSession = null;
        _nestedDragSession = null;
        _workspaceDragTarget = null;
        _pinnedDragging = false;
    }

    private void HideDropIndicator() => DropIndicator.IsVisible = false;

    private void UpdateDropIndicator(double pointerY)
    {
        var rows = GetDragRowBounds();
        if (rows.Count == 0)
        {
            DropIndicator.IsVisible = false;
            return;
        }

        var index = rows.Count(row => (row.Top + row.Bottom) / 2 < pointerY);
        var target = rows[Math.Clamp(index, 0, rows.Count - 1)];
        var lineY = index >= rows.Count ? rows[^1].Bottom - 1 : target.Top - 1;
        Canvas.SetLeft(DropIndicator, target.Left);
        Canvas.SetTop(DropIndicator, lineY);
        DropIndicator.Width = Math.Max(40, target.Right - target.Left);
        DropIndicator.IsVisible = true;
    }

    private List<(double Top, double Bottom, double Left, double Right)> GetDragRowBounds()
    {
        var buttons = this.GetVisualDescendants().OfType<Button>();
        IEnumerable<Button> filtered = _dragMode switch
        {
            1 => buttons.Where(button => button.DataContext is SessionItemViewModel session && session.IsPinned),
            2 when _nestedDragSession is { } nested => buttons.Where(button =>
                button.Classes.Contains("session-row")
                && button.Classes.Contains("nested")
                && button.DataContext is SessionItemViewModel session
                && ReferenceEquals(session.Workspace, nested.Workspace)),
            3 => buttons.Where(button => button.Classes.Contains("row")
                                          && button.DataContext is WorkspaceItemViewModel),
            _ => []
        };

        return filtered
            .Select(button =>
            {
                var point = button.TranslatePoint(new Point(0, 0), this);
                return point is null
                    ? (Top: 0.0, Bottom: 0.0, Left: 0.0, Right: 0.0)
                    : (Top: point.Value.Y, Bottom: point.Value.Y + button.Bounds.Height,
                        Left: point.Value.X, Right: point.Value.X + button.Bounds.Width);
            })
            .Where(row => row.Bottom > row.Top)
            .OrderBy(row => row.Top)
            .ToList();
    }

    private int DropIndexInPinned(double pointerY)
    {
        var relative = pointerY - PinnedItemsControl.TranslatePoint(new Point(0, 0), this)!.Value.Y;
        return (int)Math.Floor(Math.Max(0, relative) / 31.0);
    }

    private int DropIndexAmongSessionRows(WorkspaceItemViewModel workspace, double pointerY)
    {
        var rows = this.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("session-row")
                             && button.Classes.Contains("nested")
                             && button.DataContext is SessionItemViewModel session
                             && ReferenceEquals(session.Workspace, workspace)
                             && !session.IsPinned)
            .Select(button => RowCenterY(button))
            .OrderBy(center => center)
            .ToList();
        return rows.Count(center => center < pointerY);
    }

    private int DropIndexAmongWorkspaceRows(double pointerY)
    {
        var rows = this.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("row")
                             && button.DataContext is WorkspaceItemViewModel)
            .Select(button => RowCenterY(button))
            .OrderBy(center => center)
            .ToList();
        return rows.Count(center => center < pointerY);
    }

    private double RowCenterY(Control control) =>
        control.TranslatePoint(new Point(0, control.Bounds.Height / 2), this)?.Y ?? 0;

    private static object? FindDragContext(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control control)
            {
                if (control.DataContext is SessionItemViewModel session)
                {
                    return session;
                }

                if (control.DataContext is WorkspaceItemViewModel workspace)
                {
                    return workspace;
                }
            }
        }

        return null;
    }

    private void OnWorkspaceRowPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is not Control row || row.DataContext is not WorkspaceItemViewModel workspace)
        {
            return;
        }

        _previewCloseTimer.Stop();
        _previewShowTimer.Stop();
        if (SessionPreviewCard.IsVisible || WorkspacePreviewCard.IsVisible)
        {
            ShowWorkspacePreview(row, workspace);
            return;
        }

        _previewPendingRow = row;
        _previewPendingWorkspace = workspace;
        _previewPendingSession = null;
        _previewPendingIsWorkspace = true;
        _previewShowTimer.Start();
    }

    private void OnWorkspaceRowPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        _previewShowTimer.Stop();
        StartPreviewClose();
    }

    private void OnPreviewCardPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        _previewShowTimer.Stop();
        _previewCloseTimer.Stop();
    }

    private void OnPreviewCardPointerExited(object? sender, PointerEventArgs eventArgs) =>
        StartPreviewClose();

    private void StartPreviewClose()
    {
        _previewCloseTimer.Stop();
        _previewCloseTimer.Start();
    }

    private void OnPreviewShowTimerTick(object? sender, EventArgs eventArgs)
    {
        _previewShowTimer.Stop();
        if (_previewPendingRow is not { } row)
        {
            return;
        }

        if (_previewPendingIsWorkspace && _previewPendingWorkspace is { } workspace)
        {
            ShowWorkspacePreview(row, workspace);
        }
        else if (_previewPendingSession is { } session)
        {
            ShowSessionPreview(row, session);
        }

        _previewPendingRow = null;
        _previewPendingSession = null;
        _previewPendingWorkspace = null;
    }

    private void OnPreviewCloseTimerTick(object? sender, EventArgs eventArgs)
    {
        _previewCloseTimer.Stop();
        if (SessionPreviewCard.IsPointerOver || WorkspacePreviewCard.IsPointerOver)
        {
            return;
        }

        HidePreviewCards();
    }

    private void HidePreviewCards()
    {
        _previewCloseTimer.Stop();
        _previewShowTimer.Stop();
        _previewPendingRow = null;
        _previewPendingSession = null;
        _previewPendingWorkspace = null;
        SessionPreviewCard.IsVisible = false;
        WorkspacePreviewCard.IsVisible = false;
    }

    private bool IsSidebarMenuOpen =>
        WorkspaceMenuPopup.IsOpen || SectionMenuOverlay.IsVisible
        || PinnedSectionMenuPopup.IsOpen || NotificationMenuPopup.IsOpen;

    private void ShowSessionPreview(Control row, SessionItemViewModel session)
    {
        if (IsSidebarMenuOpen)
        {
            HidePreviewCards();
            return;
        }

        _previewCloseTimer.Stop();
        WorkspacePreviewCard.IsVisible = false;
        SessionPreviewCard.DataContext = session;
        ShowPreviewCard(SessionPreviewCard);
        PlacePreviewCard(row, SessionPreviewCard);
    }

    private void ShowWorkspacePreview(Control row, WorkspaceItemViewModel workspace)
    {
        if (IsSidebarMenuOpen)
        {
            HidePreviewCards();
            return;
        }

        _previewCloseTimer.Stop();
        SessionPreviewCard.IsVisible = false;
        WorkspacePreviewCard.DataContext = workspace;
        ShowPreviewCard(WorkspacePreviewCard);
        PlacePreviewCard(row, WorkspacePreviewCard);
    }

    private void ShowPreviewCard(Control card)
    {
        if (card.IsVisible && card.Opacity >= 0.99)
        {
            return;
        }

        card.Opacity = 0;
        card.IsVisible = true;
        Dispatcher.UIThread.Post(() => card.Opacity = 1, DispatcherPriority.Render);
    }

    private void PlacePreviewCard(Control row, Control card)
    {
        if (row.TranslatePoint(new Point(0, 0), PreviewCanvas) is not { } origin)
        {
            return;
        }

        var left = origin.X + row.Bounds.Width + 4;
        Canvas.SetLeft(card, Math.Round(left));
        Canvas.SetTop(card, Math.Round(origin.Y));
        card.Measure(Size.Infinity);

        Dispatcher.UIThread.Post(() =>
        {
            if (!card.IsVisible)
            {
                return;
            }

            var top = Canvas.GetTop(card);
            var maxTop = Bounds.Height - card.Bounds.Height - 16;
            if (maxTop > 0 && top > maxTop)
            {
                Canvas.SetTop(card, Math.Round(maxTop));
            }
        }, DispatcherPriority.Background);
    }

    private void OnOrganizeSidebarRowEntered(object? sender, PointerEventArgs eventArgs)
    {
        CancelSectionSubmenuClose();
        ShowProjectSectionSubmenu(sender as Control, organize: true);
    }

    private void OnSessionSortRowEntered(object? sender, PointerEventArgs eventArgs)
    {
        CancelSectionSubmenuClose();
        ShowProjectSectionSubmenu(sender as Control, organize: false);
    }

    private void OnOrganizeSidebarRowClicked(object? sender, RoutedEventArgs eventArgs) =>
        ShowProjectSectionSubmenu(sender as Control, organize: true);

    private void OnSessionSortRowClicked(object? sender, RoutedEventArgs eventArgs) =>
        ShowProjectSectionSubmenu(sender as Control, organize: false);

    private void OnProjectSectionMenuRowExited(object? sender, PointerEventArgs eventArgs)
    {
        // PointerExited can be delivered after the next row's PointerEntered while
        // moving between rows; only schedule a close when the pointer left both
        // the menu and the submenu, otherwise the submenu flickers shut.
        if (!ProjectSectionMenuCard.IsPointerOver && !ProjectSectionSubmenuCard.IsPointerOver)
        {
            StartSectionSubmenuClose();
        }
    }

    private void OnProjectSectionMenuPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        // Row PointerEntered can be unreliable while a popup window owns the
        // pointer, so drive the submenu from the card's pointer position too.
        var y = eventArgs.GetPosition(ProjectSectionMenuCard).Y;
        if (y < 6 || y > 68)
        {
            return;
        }

        var organize = y < 37;
        if (ProjectSectionSubmenuCard.IsVisible && organize == _sectionSubmenuShowsOrganize)
        {
            return;
        }

        ShowProjectSectionSubmenu(null, organize);
    }

    private void OnProjectSectionMenuCardPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (!ProjectSectionSubmenuCard.IsPointerOver && !ProjectSectionSubmenuCard.IsVisible)
        {
            StartSectionSubmenuClose();
        }
        else if (!ProjectSectionSubmenuCard.IsPointerOver)
        {
            StartSectionSubmenuClose();
        }
    }

    private void OnProjectSectionSubmenuPointerEntered(object? sender, PointerEventArgs eventArgs) =>
        CancelSectionSubmenuClose();

    private void OnProjectSectionSubmenuPointerExited(object? sender, PointerEventArgs eventArgs) =>
        StartSectionSubmenuClose();

    private void CancelSectionSubmenuClose() => _sectionSubmenuCloseTimer.Stop();

    private void StartSectionSubmenuClose()
    {
        _sectionSubmenuCloseTimer.Stop();
        _sectionSubmenuCloseTimer.Start();
    }

    private bool _sectionSubmenuShowsOrganize;

    private void ShowProjectSectionSubmenu(Control? anchor, bool organize)
    {
        CancelSectionSubmenuClose();
        // The submenu card lives inside the same popup window as the parent
        // menu, so showing/switching it cannot close or reopen any window.
        if (ProjectSectionSubmenuCard.IsVisible && organize == _sectionSubmenuShowsOrganize)
        {
            return;
        }

        _sectionSubmenuShowsOrganize = organize;
        OrganizeSidebarOptions.IsVisible = organize;
        SessionSortOptions.IsVisible = !organize;
        // Align the card with the hovered row: 6px card padding, 30px rows, 1px gap.
        ProjectSectionSubmenuCard.Margin = new Thickness(0, organize ? 6 : 37, 0, 0);
        ProjectSectionSubmenuCard.IsVisible = true;
    }

    private void CloseProjectSectionMenus()
    {
        _sectionSubmenuCloseTimer.Stop();
        ProjectSectionSubmenuCard.IsVisible = false;
        if (!SectionMenuOverlay.IsVisible)
        {
            return;
        }

        SectionMenuOverlay.IsVisible = false;
    }

    private void OnSectionMenuDismissPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        CloseProjectSectionMenus();
        eventArgs.Handled = true;
    }

    private void OnGroupByProjectClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetSidebarGrouped(true);
        CloseProjectSectionMenus();
    }

    private void OnMergeDisplayClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetSidebarGrouped(false);
        CloseProjectSectionMenus();
    }

    private void OnSortByUpdatedClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetSessionSort("updated");
        CloseProjectSectionMenus();
    }

    private void OnSortByCreatedClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.SetSessionSort("created");
        CloseProjectSectionMenus();
    }

    private void OnRenameSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (GetSession(sender) is { } session)
        {
            _viewModel.BeginRename(session);
            Dispatcher.UIThread.Post(() =>
            {
                RenameBox.Focus();
                RenameBox.SelectAll();
            }, DispatcherPriority.Background);
        }
    }

    private void OnDeleteSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (GetSession(sender) is { } session)
        {
            _viewModel.BeginDelete(session);
        }
    }

    private void OnMarkSessionUnreadClicked(object? sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (GetSession(sender) is { } session)
        {
            _viewModel.MarkSessionUnread(session);
        }
    }

    private void OnPinSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (GetSession(sender) is { } session)
        {
            _viewModel.ToggleSessionPinned(session);
        }
    }

    private void OnArchiveSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (GetSession(sender) is { } session)
        {
            _viewModel.ArchiveSession(session);
        }
    }

    private void OnUnarchiveSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (GetSession(sender) is { } session)
        {
            _viewModel.UnarchiveSession(session);
        }
    }

    private void OnToggleProjectSectionClicked(object? sender, RoutedEventArgs eventArgs) =>
        AnimateSection(WorkspaceItemsControl, _viewModel.IsProjectSectionExpanded,
            _viewModel.ToggleProjectSection);

    /// <summary>Codex's header share action: copy the conversation as plain text.</summary>
    private void OnShareConversationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var message in _viewModel.Messages)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }
            builder.AppendLine(message.IsUser ? "用户：" : "KL：");
            builder.AppendLine(message.Text);
            builder.AppendLine();
        }
        if (builder.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        _ = clipboard.SetTextAsync(builder.ToString());
        var originalData = ShareHeaderIcon.Data;
        ShareHeaderIcon.Data = (Geometry)Application.Current!.FindResource("CheckIcon")!;
        ShareHeaderIcon.Stroke = new SolidColorBrush(Color.Parse("#7FBF8F"));
        DispatcherTimer.RunOnce(() =>
        {
            ShareHeaderIcon.Data = originalData;
            ShareHeaderIcon.ClearValue(Avalonia.Controls.Shapes.Shape.StrokeProperty);
        }, TimeSpan.FromMilliseconds(1200));
    }

    private void OnRenameActiveSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.ActiveSessionItem is { } session)
        {
            _viewModel.BeginRename(session);
            Dispatcher.UIThread.Post(() =>
            {
                RenameBox.Focus();
                RenameBox.SelectAll();
            }, DispatcherPriority.Background);
        }
    }

    private void OnDeleteActiveSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.ActiveSessionItem is { } session)
        {
            _viewModel.BeginDelete(session);
        }
    }

    private void OnChatModeClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetInteractionMode(workMode: false);

    private void OnWorkModeClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetInteractionMode(workMode: true);

    private void OnNavChatClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (SkipPointerGeneratedRailClick())
        {
            return;
        }
        ActivateRailSection(AppSection.Chat);
    }

    private void OnNavLibraryClicked(object? sender, RoutedEventArgs eventArgs) =>
        ActivateRailSection(AppSection.Library);

    private void OnNavProjectsClicked(object? sender, RoutedEventArgs eventArgs) =>
        ActivateRailSection(AppSection.Projects);

    private void OnNavPluginsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (SkipPointerGeneratedRailClick())
        {
            return;
        }
        ActivateRailSection(AppSection.Plugins);
    }

    private void OnPluginsTabClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ShowPluginsList();

    private void OnSkillsTabClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ShowSkillsList();

    private void OnRefreshSkillsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshSkillsAsync();

    private void OnNavAutomationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (SkipPointerGeneratedRailClick())
        {
            return;
        }
        ActivateRailSection(AppSection.Automation);
    }

    private void ActivateRailSectionFromPointer(PointerPressedEventArgs eventArgs, AppSection section)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.PointerUpdateKind
            != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        _lastRailPointerNavigation = Environment.TickCount64;
        ActivateRailSection(section);
    }

    private bool SkipPointerGeneratedRailClick() =>
        Environment.TickCount64 - _lastRailPointerNavigation < 750;

    private void ActivateRailSection(AppSection section)
    {
        CloseMoreMenu();
        CloseProjectSectionMenus();
        HidePreviewCards();
        WorkspaceMenuPopup.IsOpen = false;
        ProductMenuPopup.IsOpen = false;
        _viewModel.CloseSearchPalette();
        if (_viewModel.IsNotificationsVisible)
        {
            _viewModel.ToggleNotifications();
        }

        SetRailSection(section);

        // Clicking the rail item of the section already on screen is a no-op;
        // re-navigating used to refresh lists and reset the page state.
        var alreadyHere = section switch
        {
            AppSection.Chat => _viewModel.IsChatPage,
            AppSection.Library => _viewModel.IsLibraryPage,
            AppSection.Projects => _viewModel.IsProjectsPage,
            AppSection.Plugins => _viewModel.IsPluginsPage,
            AppSection.Automation => _viewModel.IsAutomationPage,
            _ => false
        };
        if (alreadyHere && !_viewModel.IsSettingsOpen)
        {
            return;
        }

        _viewModel.Navigate(section);
        if (section == AppSection.Chat)
        {
            FocusComposer();
        }
    }

    private void SetRailSection(AppSection section)
    {
        ChatRailButton.Classes.Set("active", section == AppSection.Chat);
        PluginsRailButton.Classes.Set("active", section == AppSection.Plugins);
        AutomationRailButton.Classes.Set("active", section == AppSection.Automation);
    }

    private void OnAutomationFilterAllClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetAutomationFilter(AutomationFilter.All);

    private void OnAutomationFilterActiveClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetAutomationFilter(AutomationFilter.Active);

    private void OnAutomationFilterEndedClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetAutomationFilter(AutomationFilter.Ended);

    private void OnNewAutomationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.BeginCreateAutomation();
        Dispatcher.UIThread.Post(() => AutomationNameBox.Focus(), DispatcherPriority.Background);
    }

    private void OnEditAutomationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (GetAutomation(sender) is { } automation)
        {
            _viewModel.BeginEditAutomation(automation);
            Dispatcher.UIThread.Post(() => AutomationNameBox.Focus(), DispatcherPriority.Background);
        }
    }

    private void OnToggleAutomationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (GetAutomation(sender) is { } automation)
        {
            _viewModel.ToggleAutomation(automation);
        }
    }

    private async void OnRunAutomationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (GetAutomation(sender) is { } automation)
        {
            await _viewModel.RunAutomationAsync(automation);
        }
    }

    private void OnDeleteAutomationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (GetAutomation(sender) is { } automation)
        {
            _viewModel.BeginDeleteAutomation(automation);
        }
    }

    private void OnSaveAutomationClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveAutomation();

    private void OnConfirmDeleteAutomationClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ConfirmDeleteAutomation();

    private async void OnAutomationTimerTick(object? sender, EventArgs eventArgs)
    {
        await _viewModel.RunDueAutomationsAsync();
    }

    private void OnNavSettingsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMoreMenu();
        // Re-clicking the settings icon while settings is open would reload the
        // draft fields from saved state and drop in-progress edits.
        if (_viewModel.IsSettingsOpen)
        {
            return;
        }

        _viewModel.OpenSettings();
    }

    private void OnOpenModelSettingsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideOwnerFlyout(sender);
        _viewModel.OpenSettings(SettingsPane.Models);
    }

    private void OnWorkspacePickerClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control anchor)
        {
            ShowWorkspacePicker(anchor);
        }
    }

    private void OnHomeWorkspaceClicked(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is Control anchor)
        {
            ShowWorkspacePicker(anchor);
        }
    }

    private void ShowWorkspacePicker(Control anchor)
    {
        var flyout = new MenuFlyout();
        foreach (var workspace in _viewModel.Workspaces)
        {
            var item = new MenuItem { Header = workspace.Name };
            item.Click += async (_, _) =>
            {
                await _viewModel.SelectWorkspaceAsync(workspace);
                FocusComposer();
            };
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new Separator());
        var add = new MenuItem { Header = "添加工作区…" };
        add.Click += async (_, _) => await PickWorkspaceAsync();
        flyout.Items.Add(add);
        flyout.ShowAt(anchor);
    }

    private void OnMemoryScopeWorkspaceClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetMemoryScope("workspace");

    private void OnMemoryScopeUserClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetMemoryScope("user");

    private void OnRefreshMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshMemoryAsync();

    private void OnNewMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.StartNewMemory();

    private async void OnSaveMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveMemoryAsync();

    private async void OnDeleteMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.DeleteMemoryAsync();

    private async void OnMemoryEntryClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string name })
        {
            await _viewModel.LoadMemoryAsync(name);
        }
    }

    private void OnRefreshConfigClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshConfigAsync();

    private void OnRefreshHooksClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.RefreshHooks();

    private async void OnRefreshGitClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RefreshGitAsync();

    private async void OnRefreshWorktreesClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RefreshWorktreesAsync();

    private async void OnRunReviewClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RunReviewAsync();

    private async void OnCreateWorktreeClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.CreateWorktreeAsync();

    private void OnDeleteWorktreeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: WorktreeRow row })
        {
            _viewModel.RequestDeleteWorktree(row);
        }
    }

    private void OnCancelWorktreeDeleteClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CancelDeleteWorktree();

    private async void OnConfirmWorktreeDeleteClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.ConfirmDeleteWorktreeAsync();

    private void OnOpenWorktreeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: WorktreeRow row } && Directory.Exists(row.Path))
        {
            OpenInFileTarget(row.Path);
        }
    }

    private void OnOpenHooksClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var path = _viewModel.HooksFilePath;
        if (File.Exists(path))
        {
            OpenInFileTarget(path);
            return;
        }

        Directory.CreateDirectory(_viewModel.KlHomeDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_viewModel.KlHomeDirectory}\"",
            UseShellExecute = true
        });
    }

    private void OnOpenMemorySettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        SwitchSettingsPane(SettingsPane.Memory);

    private void OnOpenWorktreesSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        SwitchSettingsPane(SettingsPane.Worktrees);

    private void OnOpenModelsSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        SwitchSettingsPane(SettingsPane.Models);

    private void OnAddProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenProviderForm();

    private void OnCancelProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseProviderForm();

    private async void OnSaveProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderAsync();

    private void OnEditProviderClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ProviderCardRow provider })
        {
            _viewModel.OpenProviderDetail(provider);
        }
    }

    private void OnCloseProviderDetailClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseProviderDetail();

    private void OnSaveProviderProfileClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveProviderProfile();

    private async void OnSaveProviderConnectionClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderConnectionAsync();

    private void OnDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenProviderDeleteConfirm();

    private void OnCancelDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CancelProviderDeleteConfirm();

    private async void OnConfirmDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.ConfirmDeleteProviderAsync();

    private void OnAddProviderModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenAddProviderModel();

    private async void OnFetchProviderModelsClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.FetchProviderModelsAsync();

    private void OnEditProviderModelClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ProviderModelRow model })
        {
            _viewModel.OpenEditProviderModel(model);
        }
    }

    private async void OnDeleteProviderModelClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ProviderModelRow model })
        {
            await _viewModel.DeleteProviderModelAsync(model);
        }
    }

    private void OnCancelProviderModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseProviderModelForm();

    private async void OnSaveProviderModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderModelAsync();

    private void OnToggleProviderApiKeyVisibilityClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.IsProviderApiKeyVisible = !_viewModel.IsProviderApiKeyVisible;

    private async void OnSaveProviderApiKeyClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderDetailApiKeyAsync();

    private async void OnProviderEnabledChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is ToggleSwitch { DataContext: ProviderCardRow provider } toggle)
        {
            await _viewModel.SetProviderEnabledAsync(provider, toggle.IsChecked == true);
        }
    }

    private async void OnProviderModelEnabledChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is ToggleSwitch { DataContext: ProviderModelRow model } toggle)
        {
            await _viewModel.SetProviderModelEnabledAsync(model, toggle.IsChecked == true);
        }
    }

    private void OnOpenProviderWebsiteClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string url }
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception error)
            {
                _viewModel.ReportError($"无法打开官网链接：{error.Message}");
            }
        }
    }

    private void OnFollowUpModeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string mode })
        {
            _viewModel.SetFollowUpMode(mode);
        }
    }

    private void OnMotionModeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string mode })
        {
            _viewModel.SetReduceMotionMode(mode);
        }
    }

    private void OnThemeModeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string mode })
        {
            _viewModel.SetThemeMode(mode);
        }
    }

    private void OnDiffMarkerModeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string mode })
        {
            _viewModel.SetDiffMarkerMode(mode);
        }
    }

    private void OnToggleTerminalClicked(object? sender, RoutedEventArgs eventArgs) =>
        ToggleTerminalFromAnyPage();

    private void ToggleTerminalFromAnyPage()
    {
        if (!_viewModel.IsChatPage || _viewModel.IsSettingsOpen)
        {
            _viewModel.CloseSettings();
            _viewModel.Navigate(AppSection.Chat);
            if (!_viewModel.IsTerminalPanelOpen)
            {
                _viewModel.ToggleTerminalPanel();
            }

            return;
        }

        _viewModel.ToggleTerminalPanel();
    }

    private void OnTerminalPositionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string position })
        {
            _viewModel.SetTerminalPosition(position);
        }
    }

    private void OnTerminalPanelCloseClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.IsTerminalPanelOpen = false;

    private void OnTerminalClearClicked(object? sender, RoutedEventArgs eventArgs) =>
        TerminalView.Clear();

    private void OnTerminalCopyClicked(object? sender, RoutedEventArgs eventArgs) =>
        TerminalView.CopySelectionOrAll();

    private void OnTerminalRestartClicked(object? sender, RoutedEventArgs eventArgs) =>
        StartTerminal(forceRestart: true);

    private void StartTerminal(bool forceRestart = false)
    {
        if (!_viewModel.IsTerminalPanelOpen)
        {
            return;
        }

        if (TerminalView.IsRunning && !forceRestart)
        {
            return;
        }

        var cwd = Directory.Exists(_viewModel.WorkspacePath)
            ? _viewModel.WorkspacePath
            : Environment.CurrentDirectory;
        TerminalView.Start(_viewModel.TerminalShell, cwd);
        UpdateTerminalShellLabel();
    }

    private void UpdateTerminalShellLabel() =>
        TerminalShellLabel.Text = _viewModel.TerminalShell switch
        {
            "cmd" => "命令提示符",
            "git-bash" => "Git Bash",
            _ => "PowerShell"
        };

    private void ApplyTerminalPosition()
    {
        if (TerminalPanelBorder is null || TerminalSplitter is null)
        {
            return;
        }

        var size = _viewModel.TerminalPanelSize;
        if (_viewModel.IsTerminalRight)
        {
            Grid.SetRow(TerminalSplitter, 0);
            Grid.SetColumn(TerminalSplitter, 1);
            TerminalSplitter.Width = 6;
            TerminalSplitter.Height = double.NaN;
            TerminalSplitter.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            TerminalSplitterLine.Width = 1;
            TerminalSplitterLine.Height = double.NaN;
            TerminalSplitterLine.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            TerminalSplitterLine.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

            Grid.SetRow(TerminalPanelBorder, 0);
            Grid.SetColumn(TerminalPanelBorder, 2);
            TerminalPanelBorder.Width = size;
            TerminalPanelBorder.Height = double.NaN;
            TerminalPanelBorder.BorderThickness = new Thickness(1, 0, 0, 0);
            return;
        }

        Grid.SetRow(TerminalSplitter, 1);
        Grid.SetColumn(TerminalSplitter, 0);
        TerminalSplitter.Width = double.NaN;
        TerminalSplitter.Height = 6;
        TerminalSplitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        TerminalSplitterLine.Width = double.NaN;
        TerminalSplitterLine.Height = 1;
        TerminalSplitterLine.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        TerminalSplitterLine.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        Grid.SetRow(TerminalPanelBorder, 2);
        Grid.SetColumn(TerminalPanelBorder, 0);
        TerminalPanelBorder.Width = double.NaN;
        TerminalPanelBorder.Height = size;
        TerminalPanelBorder.BorderThickness = new Thickness(0, 1, 0, 0);
    }

    private bool _terminalSplitterDragging;

    private void OnTerminalSplitterPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _terminalSplitterDragging = true;
        eventArgs.Pointer.Capture(TerminalSplitter);
        eventArgs.Handled = true;
    }

    private void OnTerminalSplitterMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_terminalSplitterDragging
            || !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || TerminalSplitter.Parent is not Visual parent)
        {
            return;
        }

        // Drive the size straight from the pointer so the splitter stays under the cursor.
        var point = eventArgs.GetPosition(parent);
        var size = _viewModel.IsTerminalRight
            ? parent.Bounds.Width - point.X
            : parent.Bounds.Height - point.Y;
        _viewModel.SetTerminalPanelSize(size);
        ApplyTerminalPosition();
        eventArgs.Handled = true;
    }

    private void OnTerminalSplitterReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        _terminalSplitterDragging = false;
        eventArgs.Pointer.Capture(null);
    }

    private void OnRefreshCredentialsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.RefreshCredentials();

    private void OnOpenCredentialsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var directory = _viewModel.CredentialsDirectory;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true
        });
    }

    private async void OnDeleteAllMemoriesClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.DeleteAllMemoriesAsync();

    private async void OnScanImportsClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.DiscoverImportsAsync();

    private async void OnImportSelectedClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.ImportSelectedAsync();

    private void OnOpenImportSourceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var path = Directory.Exists(_viewModel.ImportCodexHome)
            ? _viewModel.ImportCodexHome
            : _viewModel.ImportClaudeHome;
        if (!Directory.Exists(path))
        {
            _viewModel.ReportError("没有找到 Codex 或 Claude 配置目录。");
            return;
        }

        OpenDirectory(path, "导入来源");
    }

    private void OnRefreshMcpToolsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshMcpToolsAsync();

    private void OnShortcutEditClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ShortcutRowViewModel row })
        {
            _viewModel.BeginShortcutCapture(row);
            SettingsScroller.Offset = new Vector(0, 0);
        }
    }

    private void OnShortcutResetClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ShortcutRowViewModel row })
        {
            _viewModel.ResetShortcut(row);
        }
    }

    private void OnCancelShortcutCaptureClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CancelShortcutCapture();

    private void OnSettingsNavClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string name }
            && Enum.TryParse<SettingsPane>(name, out var pane))
        {
            SwitchSettingsPane(pane);
        }
    }

    private void SwitchSettingsPane(SettingsPane pane)
    {
        // Clicking the already-selected item is a no-op: re-running the switch
        // would reset the pane scroll under the user.
        if (pane == _viewModel.ActiveSettingsPane)
        {
            return;
        }

        _viewModel.SelectSettingsPane(pane);
        ApplySettingsSearch();
        SettingsScroller.Offset = new Vector(0, 0);
    }

    /// <summary>Clicking blank space drops the text focus, as Codex does.</summary>
    private void OnOpenRemoteCoreSettingsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        SwitchSettingsPane(SettingsPane.Connections);
        var tab = SettingsPanesPanel.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag is "connect:other");
        if (tab is not null && !tab.Classes.Contains("active"))
        {
            OnSettingsTabClicked(tab, new RoutedEventArgs());
        }

        this.FindControl<TextBox>("RemoteEndpointBox")?.Focus();
    }

    private void OnSettingsScrollerPointerPressed(object? sender, PointerPressedEventArgs eventArgs) =>
        FocusManager?.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None);

    /// <summary>Codex keeps the appearance pane's advanced card collapsed until asked for.</summary>
    private void OnAppearanceAdvancedClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (this.FindControl<StackPanel>("AppearanceAdvancedPanel") is not { } panel
            || this.FindControl<Avalonia.Controls.Shapes.Path>("AppearanceAdvancedChevronRight") is not { } right
            || this.FindControl<Avalonia.Controls.Shapes.Path>("AppearanceAdvancedChevronDown") is not { } down)
        {
            return;
        }

        panel.IsVisible = !panel.IsVisible;
        right.IsVisible = !panel.IsVisible;
        down.IsVisible = panel.IsVisible;
    }

    /// <summary>Marks the selected appearance preview and routes clicks to the theme mode.</summary>
    private void WireSettingsChevrons()
    {
        var modeCards = SettingsPanesPanel.GetLogicalDescendants().OfType<Border>()
            .Where(card => card.Classes.Contains("settings-mode-card"))
            .ToList();
        foreach (var card in modeCards)
        {
            if (card.Tag is not string mode)
            {
                continue;
            }

            card.Cursor = new Cursor(StandardCursorType.Hand);
            card.PointerPressed += (_, _) => _viewModel.SetThemeMode(mode);
        }

        UpdateThemeModeCards();
    }

    private void UpdateThemeModeCards()
    {
        foreach (var card in SettingsPanesPanel.GetLogicalDescendants().OfType<Border>()
                     .Where(card => card.Classes.Contains("settings-mode-card")))
        {
            var active = card.Tag is string mode && mode == _viewModel.ThemeMode;
            card.BorderBrush = new SolidColorBrush(Color.Parse(active ? "#606ACC" : "#2E2E31"));
            card.BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    private void OnSettingsTabClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string tag } current)
        {
            return;
        }

        // Re-clicking the selected tab would replay the page fade; keep it still.
        if (current.Classes.Contains("active"))
        {
            return;
        }

        var group = tag.Split(':')[0];
        if (current.Parent is Panel panel)
        {
            foreach (var sibling in panel.Children.OfType<Button>())
            {
                if (sibling.Tag is string siblingTag && siblingTag.StartsWith(group + ":", StringComparison.Ordinal))
                {
                    sibling.Classes.Set("active", sibling == current);
                }
            }
        }

        foreach (var page in SettingsPanesPanel.GetLogicalDescendants().OfType<Control>())
        {
            if (!page.Classes.Contains("settings-tab-page") || page.Tag is not string pageTag)
            {
                continue;
            }

            if (!pageTag.StartsWith(group + ":", StringComparison.Ordinal))
            {
                continue;
            }

            page.IsVisible = pageTag == tag;
        }
    }

    private void OnEditAgentsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenAgentsEditor();

    private void OnCloseAgentsEditorClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseAgentsEditor();

    private void OnSaveAgentsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveAgents();

    /// <summary>Chevron rows without a destination become clickable and open the detail sheet.</summary>
    private void OnSettingsPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StorageBarColumns))
        {
            StorageBar.ColumnDefinitions = _viewModel.StorageBarColumns;
            return;
        }
        if (e.PropertyName is null || !e.PropertyName.StartsWith("IsSettings", StringComparison.Ordinal))
            return;
        if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsProviders)
            or nameof(MainWindowViewModel.IsSettingsOpen))
        {
            UpdateProviderAutoRefresh();
        }
        Dispatcher.UIThread.Post(ScrollActiveSettingsNavIntoView, DispatcherPriority.Loaded);
    }

    private void UpdateProviderAutoRefresh()
    {
        if (_viewModel.IsSettingsOpen && _viewModel.IsSettingsProviders)
        {
            if (!_providerRefreshTimer.IsEnabled)
            {
                _providerRefreshTimer.Start();
            }
            return;
        }

        _providerRefreshTimer.Stop();
    }

    private void OnProviderRefreshTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!_viewModel.IsSettingsOpen
            || !_viewModel.IsSettingsProviders
            || _viewModel.IsProviderDetailOpen
            || _viewModel.IsProviderFormOpen)
        {
            if (!_viewModel.IsSettingsOpen || !_viewModel.IsSettingsProviders)
            {
                _providerRefreshTimer.Stop();
            }
            return;
        }

        _ = _viewModel.RefreshProvidersAsync(force: true, announce: false);
    }

    // Keep the selected row visible, as Codex does when a pane deep in the list is opened.
    private void ScrollActiveSettingsNavIntoView()
    {
        foreach (var child in SettingsNavPanel.Children)
        {
            if (child is Button { Classes: var classes } button && classes.Contains("active"))
            {
                button.BringIntoView();
                break;
            }
        }
    }

    // The ScrollViewer measures its content at content width, so stretch the nav rows ourselves.
    private void FitSettingsNavWidth()
    {
        if (SettingsNavScroller.Bounds.Width > 0)
            SettingsNavPanel.Width = SettingsNavScroller.Bounds.Width - 16;
    }

    /// <summary>Codex hangs a menu off the right edge of its trigger instead of centring it.</summary>
    private void WireComboBoxMenus()
    {
        foreach (var combo in this.GetLogicalDescendants().OfType<ComboBox>())
        {
            combo.DropDownOpened += (_, _) =>
            {
                if (combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault() is { } popup)
                {
                    popup.Placement = PlacementMode.BottomEdgeAlignedRight;
                }
            };
        }
    }

    private void OnSettingsSearchChanged(object? sender, TextChangedEventArgs eventArgs) =>
        ApplySettingsSearch();

    /// <summary>
    /// Settings panes are static XAML, so the search matches each card's own text
    /// instead of duplicating a keyword list in the view model.
    /// </summary>
    private void ApplySettingsSearch()
    {
        var query = SettingsSearchBox.Text?.Trim() ?? string.Empty;
        var paneHasMatch = false;
        foreach (var pane in SettingsPanesPanel.Children.OfType<StackPanel>())
        {
            if (!pane.IsVisible)
            {
                continue;
            }
            foreach (var card in pane.Children.OfType<Control>())
            {
                var match = query.Length == 0
                    || card.GetLogicalDescendants().OfType<TextBlock>().Any(
                        block => block.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
                paneHasMatch |= match;
                // Tab pages own their visibility; the search must not reveal every tab at once.
                if (!card.Classes.Contains("settings-tab-page"))
                {
                    card.IsVisible = match;
                }
            }
        }
        SettingsNoMatch.IsVisible = query.Length > 0 && !paneHasMatch;
    }

    private void OnAddWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.OpenCreateProjectDialog();
        Dispatcher.UIThread.Post(() => ProjectNameBox.Focus(), DispatcherPriority.Background);
    }

    private async void OnCreateProjectBrowseClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择源文件夹",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.SetProjectSourcePath(path);
        }
    }

    private void OnCreateProjectCancelClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseCreateProjectDialog();

    private void OnCreateProjectConfirmClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CreateProjectFromDraft();

    private async Task PickWorkspaceAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作区",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.AddWorkspace(path);
        }
    }

    /// <summary>Codex settings apply on change; only write when the draft
    /// actually differs from the saved state so binding on open is a no-op.</summary>
    private async void OnGeneralSettingsSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is not ComboBox { SelectedItem: { } selected })
        {
            return;
        }
        if (selected is int fontSize)
        {
            _viewModel.SettingsContentFontSize = fontSize;
        }
        else
        {
            _viewModel.SettingsPermissionMode = selected.ToString() ?? string.Empty;
        }
        if (_viewModel.SettingsPermissionMode == _viewModel.PermissionMode
            && _viewModel.SettingsContentFontSize == _viewModel.ContentFontSize)
        {
            return;
        }
        await _viewModel.SaveSettingsAsync();
        ApplyContentFontResources();
    }

    private async void OnTestConnectionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.TestConnectionAsync();
    }

    private void OnSaveLibraryItemClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveLibraryItem();

    private void OnLibraryItemClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: LibraryItemViewModel item })
        {
            _viewModel.InsertLibraryItem(item);
        }
    }

    private void OnInsertLibraryItemClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: LibraryItemViewModel item })
        {
            _viewModel.InsertLibraryItem(item);
        }
    }

    private void OnDeleteLibraryItemClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: LibraryItemViewModel item })
        {
            _viewModel.DeleteLibraryItem(item);
        }
    }

    private void OnPermissionAutoClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideOwnerFlyout(sender);
        _viewModel.SetPermissionMode("帮我批准");
    }

    private void OnPermissionFullClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideOwnerFlyout(sender);
        _viewModel.SetPermissionMode("完全访问");
    }

    private void OnPermissionAskClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideOwnerFlyout(sender);
        _viewModel.SetPermissionMode("请求批准");
    }

    private void OnDismissFullAccessWarningClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.DismissFullAccessWarning(forever: false);

    private void OnHideFullAccessWarningClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.DismissFullAccessWarning(forever: true);

    private void OnPermissionLearnMoreClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideOwnerFlyout(sender);
        _viewModel.OpenSettings(SettingsPane.General);
    }

    /// <summary>Closes the Flyout that hosts a clicked row.</summary>
    private static void HideOwnerFlyout(object? sender)
    {
        if (sender is Control control)
        {
            control.FindAncestorOfType<Button>()?.Flyout?.Hide();
        }
    }

    private void OnReasoningFlyoutOpened(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(UpdateReasoningThumb, DispatcherPriority.Render);

    private void OnReasoningTrackSizeChanged(object? sender, SizeChangedEventArgs eventArgs) =>
        UpdateReasoningThumb();

    private bool _reasoningDragging;
    private double _reasoningDragFraction;

    private void OnReasoningTrackPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(ReasoningTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _reasoningDragging = true;
        eventArgs.Pointer.Capture(ReasoningTrack);
        DragReasoningThumb(eventArgs.GetPosition(ReasoningTrack).X);
        eventArgs.Handled = true;
    }

    private void OnReasoningTrackMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_reasoningDragging)
        {
            return;
        }

        DragReasoningThumb(eventArgs.GetPosition(ReasoningTrack).X);
        eventArgs.Handled = true;
    }

    private async void OnReasoningTrackReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_reasoningDragging)
        {
            return;
        }

        _reasoningDragging = false;
        eventArgs.Pointer.Capture(null);
        var effort = EffortForFraction(_reasoningDragFraction);
        try
        {
            if (!string.Equals(effort, _viewModel.ReasoningEffort, StringComparison.Ordinal))
            {
                await _viewModel.SetReasoningEffortAsync(effort);
            }
        }
        finally
        {
            // Keep the preview while the Core write is in flight so the label does
            // not snap back for a frame.
            _viewModel.ClearReasoningEffortPreview();
            UpdateReasoningThumb();
        }
    }

    /// <summary>
    /// Moves the knob while dragging; the Core write happens on release. The knob
    /// snaps onto a tier dot once the pointer is within a few pixels of it, which
    /// makes the four stops feel magnetic instead of loose.
    /// </summary>
    private void DragReasoningThumb(double x)
    {
        var width = ReasoningTrack.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        const double snapPixels = 16;
        var raw = Math.Clamp(x / width, 0, 1);
        var tier = (int)Math.Round(raw * 3);
        var tierFraction = tier / 3.0;
        var fraction = Math.Abs(raw - tierFraction) * width <= snapPixels ? tierFraction : raw;

        _reasoningDragFraction = fraction;
        _viewModel.PreviewReasoningEffort(EffortForFraction(fraction));
        PositionReasoningThumb(fraction);
    }

    private void UpdateReasoningThumb()
    {
        if (_reasoningDragging)
        {
            return;
        }

        var index = _viewModel.IsReasoningHigh ? 3
            : _viewModel.IsReasoningMedium ? 2
            : _viewModel.IsReasoningLow ? 1
            : 0;
        _reasoningDragFraction = index / 3.0;
        PositionReasoningThumb(_reasoningDragFraction);
    }

    private void PositionReasoningThumb(double fraction)
    {
        var width = ReasoningTrack.Bounds.Width;
        var thumbWidth = ReasoningThumb.Bounds.Width;
        var travel = Math.Max(0, width - thumbWidth);
        ReasoningThumb.Margin = new Thickness(travel * fraction, 0, 0, 0);

        // The coloured part runs under the thumb centre at every tier.
        ReasoningFill.Width = width;
        ReasoningFillClip.Width = Math.Clamp(travel * fraction + (thumbWidth / 2), 0, width);

        // One dot per tier, centred on where the thumb stops.
        var offset = (thumbWidth / 2) - 2;
        var ticks = new[] { ReasoningTick0, ReasoningTick1, ReasoningTick2, ReasoningTick3 };
        for (var index = 0; index < ticks.Length; index++)
        {
            Canvas.SetLeft(ticks[index], travel * index / 3.0 + offset);
            Canvas.SetTop(ticks[index], 2);
        }
    }

    private static string EffortForFraction(double fraction) =>
        (int)Math.Round(Math.Clamp(fraction, 0, 1) * 3) switch
        {
            0 => "default",
            1 => "low",
            2 => "medium",
            _ => "high"
        };

    private async void OnSaveModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveModelAsync();

    private async void OnSaveApiKeyClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveApiKeyAsync();

    private async void OnSaveCustomInstructionsClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveCustomInstructionsAsync();

    private async void OnExportConversationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var markdown = _viewModel.BuildSessionMarkdown();
        if (markdown.Length == 0)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出会话",
            SuggestedFileName = _viewModel.SessionExportFileName,
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] }
            ]
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
            await writer.WriteAsync(markdown);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _viewModel.ReportError($"导出会话失败：{error.Message}");
        }
    }

    private async void OnForkSessionClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.ForkActiveSessionAsync();

    private async void OnCompactSessionClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.CompactActiveSessionAsync();

    private async void OnRunDoctorClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RunDoctorAsync();

    private async void OnRetryCoreClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.IsCoreReadOnly)
        {
            await _viewModel.TakeOverWriteAsync();
            return;
        }

        await _viewModel.RetryCoreAsync();
    }

    private async void OnApprovalAllowClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RespondApprovalAsync("allow");

    private async void OnApprovalAllowSessionClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RespondApprovalAsync("allow_session");

    private async void OnApprovalDenyClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RespondApprovalAsync("deny");

    private void OnResetSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ResetSettingsDefaults();

    private void OnRefreshStorageClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshStorageAsync();

    private void OnOpenStateDirectoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        OpenDirectory(_viewModel.StateDirectory, "数据目录");

    private void OnOpenKlHomeClicked(object? sender, RoutedEventArgs eventArgs) =>
        OpenDirectory(_viewModel.KlHomeDirectory, "KL Home");

    private void OnOpenCoreConfigClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var path = _viewModel.ConfigFilePath;
        if (File.Exists(path))
        {
            OpenInFileTarget(path);
            return;
        }
        OpenDirectory(_viewModel.KlHomeDirectory, "KL Home");
    }

    private void OpenInFileTarget(string path)
    {
        if (_viewModel.FileOpenTargetIndex == 1 && TryOpenInVsCode(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static bool TryOpenInVsCode(string path)
    {
        try
        {
            var start = new ProcessStartInfo("code") { UseShellExecute = true };
            start.ArgumentList.Add("--reuse-window");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception error) when (error is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private async void OnExportStateBackupClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出设置备份",
            SuggestedFileName = "kl-gui-state.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.ExportStateBackup(path);
        }
    }

    private async void OnImportStateBackupClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入设置备份",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.ImportStateBackup(path);
        }
    }

    private void OnOpenPluginDirectoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        OpenDirectory(_viewModel.PluginDirectoryPath, "插件目录");

    private async void OnRefreshPluginsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.RefreshPlugins();
        await _viewModel.RefreshPluginsAsync();
    }

    private async void OnPluginToggled(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is ToggleSwitch { DataContext: PluginItemViewModel plugin } toggle)
        {
            await _viewModel.SetPluginEnabledAsync(plugin, toggle.IsChecked == true);
        }
    }

    private async void OnImportPluginClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含 plugin.json 的插件目录",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_viewModel.ImportPlugin(path) && _viewModel.LastImportedPluginPath is { } target)
        {
            await _viewModel.InstallPluginAsync(target);
        }
    }

    private void OpenDirectory(string path, string label)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = '"' + path + '"',
                UseShellExecute = true
            });
        }
        catch (Exception error)
        {
            _viewModel.ReportError($"无法打开{label}：{error.Message}");
        }
    }

    private async void OnCopyStatePathClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }
        try
        {
            await clipboard.SetTextAsync(_viewModel.StateFilePath);
        }
        catch (Exception error)
        {
            _viewModel.ReportError($"无法复制状态文件路径：{error.Message}");
        }
    }

    private async void OnCopyMessageClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Control { DataContext: ChatItemViewModel item } control)
        {
            return;
        }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }
        try
        {
            await clipboard.SetTextAsync(item.Text);
            if (control is not Button button)
            {
                return;
            }
            ToolTip.SetTip(button, "已复制");
            button.Opacity = 1;
            await Task.Delay(1200);
            ToolTip.SetTip(button, "复制回复");
            button.Opacity = 0.72;
        }
        catch (Exception error)
        {
            _viewModel.ReportError($"无法复制回复：{error.Message}");
        }
    }

    private void OnDismissErrorClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.DismissError();

    private static string? ReadRemoteToken(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnSaveRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveRemoteCoreSettings();

    private void OnClearRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ClearRemoteCoreSettings();

    private async void OnTestRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.TestRemoteCoreConnectionAsync();

    private async void OnReconnectCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RetryCoreAsync();

    private void OnOpenCleanupClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenCleanupDialog();

    private void OnCloseCleanupDialogClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseCleanupDialog();

    private void OnCleanupDeleteClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: SessionItemViewModel session })
        {
            _viewModel.BeginDelete(session);
        }
    }

    private void OnCloseDialogClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseDialogs();

    private async void OnEditProjectAddFolderClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.SetWorkspaceSourcePath(path);
        }
    }

    private void OnRemoveWorkspaceFromDialogClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.RemoveWorkspaceFromDialog();

    private void OnConfirmRenameClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ConfirmRename();

    private void OnConfirmDeleteClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ConfirmDelete();

    private void OnActivityClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ChatItemViewModel item } button)
        {
            return;
        }

        item.Toggle();
        if (!item.ShowExpanded)
        {
            return;
        }

        // The panel is laid out after this event; scroll it into view once it exists.
        var panel = (button.Parent as Panel)?.Children
            .OfType<Control>()
            .LastOrDefault(child => child.IsVisible && !ReferenceEquals(child, button));
        if (panel is not null)
        {
            Dispatcher.UIThread.Post(() => panel.BringIntoView(), DispatcherPriority.Background);
        }
    }

    private static bool TryNumberSlot(Key key, out int slot)
    {
        slot = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1
        };
        return slot >= 0;
    }

    private async void OnRenameKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            eventArgs.Handled = true;
            _viewModel.ConfirmRename();
        }
        else if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            _viewModel.CloseDialogs();
        }
        await Task.CompletedTask;
    }

    // ---- composer "/" commands and "@" file mentions ----

    private enum ComposerSuggestionMode
    {
        None,
        Slash,
        At
    }

    private sealed record ComposerSuggestionItem(
        string Label,
        string Detail,
        char Trigger,
        Action? Run = null,
        string Insert = "");

    private ComposerSuggestionMode _composerSuggestionMode;
    private ComposerToken _composerToken;
    private TextBox? _composerSuggestionBox;
    private readonly List<ComposerSuggestionItem> _composerSuggestionItems = [];
    private int _composerSuggestionIndex = -1;

    private IReadOnlyList<ComposerSuggestionItem> SlashCommands =>
    [
        new("new", "新建会话", '/', () => _ = _viewModel.NewSessionAsync()),
        new("compact", "压缩当前会话的上下文", '/', () => _ = _viewModel.CompactActiveSessionAsync()),
        new("fork", "把当前会话复制为分支", '/', () => _ = _viewModel.ForkActiveSessionAsync()),
        new("settings", "打开设置", '/', () => _viewModel.OpenSettings()),
        new("plugins", "打开插件页", '/', () => _viewModel.Navigate(AppSection.Plugins)),
        new("library", "打开资料库", '/', () => _viewModel.Navigate(AppSection.Library)),
        new("doctor", "运行 KL Core 诊断（设置 → 模型与连接查看）", '/', () => _ = _viewModel.RunDoctorAsync()),
        new("review", "运行代码审查", '/', () =>
        {
            _viewModel.OpenSettings(SettingsPane.CodeReview);
            _ = _viewModel.RunReviewAsync();
        })
    ];

    private void OnComposerTextChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        // The hidden composer twin shares the same bound text; reacting to it would
        // close the menu immediately (its caret is 0). Only the focused one counts.
        if (!box.IsVisible || !box.IsFocused)
        {
            return;
        }

        RefreshComposerSuggestions(box);
    }

    private void RefreshComposerSuggestions(TextBox box)
    {
        if (!ComposerSuggestionRules.TryGetToken(box.Text, box.CaretIndex, out var token))
        {
            CloseComposerSuggestions();
            return;
        }

        _composerToken = token;
        _composerSuggestionBox = box;
        _composerSuggestionMode = token.Trigger == '/'
            ? ComposerSuggestionMode.Slash
            : ComposerSuggestionMode.At;

        if (token.Trigger == '/')
        {
            var query = token.Query.Trim();
            var commands = SlashCommands
                .Where(item => query.Length == 0
                    || item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            ShowComposerSuggestions(commands, "命令");
            return;
        }

        var matches = ComposerSuggestionRules.Filter(GetComposerFiles(), token.Query, 40)
            .Select(path =>
            {
                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                return new ComposerSuggestionItem(
                    Path.GetFileName(path),
                    string.IsNullOrEmpty(folder) ? "根目录" : folder,
                    '@',
                    Insert: "@" + path + " ");
            })
            .ToList();
        ShowComposerSuggestions(matches, "文件");
    }

    private void ShowComposerSuggestions(IReadOnlyList<ComposerSuggestionItem> items, string header)
    {
        ComposerMenuHeader.Text = header;
        ComposerMenuCard.Width = _composerSuggestionMode == ComposerSuggestionMode.Slash ? 340 : 400;
        ComposerMenuItems.Children.Clear();
        _composerSuggestionItems.Clear();
        _composerSuggestionItems.AddRange(items);
        _composerSuggestionIndex = items.Count > 0 ? 0 : -1;

        if (items.Count == 0)
        {
            ComposerMenuItems.Children.Add(new TextBlock
            {
                Text = "没有匹配项",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#727275")),
                Margin = new Thickness(10, 6, 10, 8)
            });
        }
        else
        {
            for (var index = 0; index < items.Count; index++)
            {
                ComposerMenuItems.Children.Add(BuildComposerSuggestionRow(items[index], index));
            }
        }

        ApplyComposerSuggestionSelection();
        if (!ComposerMenuPopup.IsOpen)
        {
            ComposerMenuCard.Opacity = 0;
            ComposerMenuPopup.IsOpen = true;
            Dispatcher.UIThread.Post(() => ComposerMenuCard.Opacity = 1, DispatcherPriority.Render);
        }
    }

    private Button BuildComposerSuggestionRow(ComposerSuggestionItem item, int index)
    {
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#232326")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = item.Trigger.ToString(),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#A0A1A3")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };

        var title = new TextBlock
        {
            Text = item.Label,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#E3E4E6")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var detail = new TextBlock
        {
            Text = item.Detail,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#727275")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var layout = new Grid
        {
            // Fixed title column so every description starts on the same x.
            ColumnDefinitions = new ColumnDefinitions("Auto,88,*"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        layout.Children.Add(badge);
        Grid.SetColumn(title, 1);
        layout.Children.Add(title);
        Grid.SetColumn(detail, 2);
        layout.Children.Add(detail);

        var button = new Button { Content = layout };
        button.Classes.Add("composer-menu-row");
        button.Click += (_, _) => CommitComposerSuggestion(item);
        button.PointerEntered += (_, _) =>
        {
            _composerSuggestionIndex = index;
            ApplyComposerSuggestionSelection();
        };
        return button;
    }

    private void ApplyComposerSuggestionSelection()
    {
        for (var index = 0; index < ComposerMenuItems.Children.Count; index++)
        {
            if (ComposerMenuItems.Children[index] is Button row)
            {
                row.Classes.Set("selected", index == _composerSuggestionIndex);
            }
        }

        if (_composerSuggestionIndex >= 0
            && _composerSuggestionIndex < ComposerMenuItems.Children.Count
            && ComposerMenuItems.Children[_composerSuggestionIndex] is Control selected)
        {
            selected.BringIntoView();
        }
    }

    private void MoveComposerSuggestion(int delta)
    {
        if (_composerSuggestionItems.Count == 0)
        {
            return;
        }

        _composerSuggestionIndex = (_composerSuggestionIndex + delta + _composerSuggestionItems.Count)
            % _composerSuggestionItems.Count;
        ApplyComposerSuggestionSelection();
    }

    private void CommitComposerSuggestion(ComposerSuggestionItem item)
    {
        var box = _composerSuggestionBox;
        if (box is null)
        {
            return;
        }

        var replacement = item.Trigger == '@' ? item.Insert : string.Empty;
        var (text, caret) = ComposerSuggestionRules.ReplaceToken(
            box.Text ?? string.Empty,
            box.CaretIndex,
            _composerToken,
            replacement);
        CloseComposerSuggestions();
        box.Text = text;
        box.CaretIndex = Math.Clamp(caret, 0, text.Length);
        box.Focus();
        item.Run?.Invoke();
    }

    private void CloseComposerSuggestions()
    {
        _composerSuggestionMode = ComposerSuggestionMode.None;
        _composerSuggestionItems.Clear();
        _composerSuggestionIndex = -1;
        ComposerMenuItems.Children.Clear();
        ComposerMenuCard.Opacity = 0;
        if (ComposerMenuPopup.IsOpen)
        {
            ComposerMenuPopup.IsOpen = false;
        }
    }

    private List<string> GetComposerFiles()
    {
        var workspace = _viewModel.WorkspacePath;
        // WorkspaceFileIndex caches per workspace, so no second cache is needed.
        return WorkspaceFileIndex.Files(workspace).ToList();
    }

    private async void OnComposerKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (ComposerMenuPopup.IsOpen)
        {
            switch (eventArgs.Key)
            {
                case Key.Down when _composerSuggestionItems.Count > 0:
                    MoveComposerSuggestion(1);
                    eventArgs.Handled = true;
                    return;
                case Key.Up when _composerSuggestionItems.Count > 0:
                    MoveComposerSuggestion(-1);
                    eventArgs.Handled = true;
                    return;
                case Key.Enter or Key.Tab when _composerSuggestionItems.Count > 0:
                    CommitComposerSuggestion(_composerSuggestionItems[_composerSuggestionIndex]);
                    eventArgs.Handled = true;
                    return;
                case Key.Escape:
                    CloseComposerSuggestions();
                    eventArgs.Handled = true;
                    return;
            }
        }

        if (eventArgs.Key != Key.Enter)
        {
            return;
        }

        var modifiers = eventArgs.KeyModifiers;
        var send = modifiers.HasFlag(KeyModifiers.Control)
            || (_viewModel.SendsOnEnter
                && !modifiers.HasFlag(KeyModifiers.Shift)
                && !modifiers.HasFlag(KeyModifiers.Alt));
        if (!send)
        {
            return;
        }

        eventArgs.Handled = true;
        CloseComposerSuggestions();
        await _viewModel.SendAsync();
        FocusComposer();
        ScrollTranscript();
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        HideRailHint();
        HidePreviewCards();
        HideDropIndicator();
        if (ComposerMenuPopup.IsOpen
            && !ComposerCard.IsPointerOver
            && !ComposerMenuCard.IsPointerOver)
        {
            CloseComposerSuggestions();
        }

        if (!MorePopup.IsOpen || MoreButton.IsPointerOver || MoreMenuCard.IsPointerOver)
        {
            return;
        }

        CloseMoreMenu();
    }

    private void OnWindowActivated(object? sender, EventArgs eventArgs) => _windowActive = true;

    private void OnWindowDeactivated(object? sender, EventArgs eventArgs)
    {
        _windowActive = false;
        HideDropIndicator();
        HideRailHintAndMoreMenu();
        CloseComposerSuggestions();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (_viewModel.IsShortcutCapturing)
        {
            eventArgs.Handled = true;
            if (eventArgs.Key == Key.Escape)
            {
                _viewModel.CancelShortcutCapture();
                return;
            }

            if (FormatShortcutGesture(eventArgs.Key, eventArgs.KeyModifiers) is { } captured)
            {
                _viewModel.ApplyShortcutGesture(captured);
            }

            return;
        }

        if (_viewModel.NumberShortcuts && TryNumberSlot(eventArgs.Key, out var slot))
        {
            if (eventArgs.KeyModifiers == KeyModifiers.Control && slot < 5)
            {
                eventArgs.Handled = true;
                _viewModel.CloseSettings();
                _viewModel.Navigate(slot switch
                {
                    0 => AppSection.Chat,
                    1 => AppSection.Library,
                    2 => AppSection.Projects,
                    3 => AppSection.Plugins,
                    _ => AppSection.Automation
                });
                return;
            }

            if (eventArgs.KeyModifiers == KeyModifiers.Alt)
            {
                eventArgs.Handled = true;
                _ = _viewModel.OpenSessionShortcutAsync(slot);
                return;
            }
        }

        if (eventArgs.Key == Key.Escape && _viewModel.IsApprovalOpen)
        {
            eventArgs.Handled = true;
            _ = _viewModel.RespondApprovalAsync("deny");
            return;
        }

        if (eventArgs.Key == Key.Escape && _viewModel.IsProviderModelFormOpen)
        {
            eventArgs.Handled = true;
            _viewModel.CloseProviderModelForm();
            return;
        }

        if (eventArgs.Key == Key.Escape && _viewModel.IsProviderFormOpen)
        {
            eventArgs.Handled = true;
            _viewModel.CloseProviderForm();
            return;
        }

        // Esc never navigates back or closes settings; it only clears the search.
        if (eventArgs.Key == Key.Escape && _viewModel.IsSettingsOpen)
        {
            eventArgs.Handled = true;
            if (!string.IsNullOrEmpty(SettingsSearchBox.Text))
            {
                SettingsSearchBox.Text = string.Empty;
                ApplySettingsSearch();
            }

            return;
        }

        if (eventArgs.Key == Key.Escape && _viewModel.IsCreateProjectDialogOpen)
        {
            eventArgs.Handled = true;
            _viewModel.CloseCreateProjectDialog();
            return;
        }

        if (eventArgs.Key == Key.Escape && _viewModel.IsCloseConfirmOpen)
        {
            eventArgs.Handled = true;
            _viewModel.CloseCloseConfirm();
            return;
        }

        if (eventArgs.Key == Key.Escape
            && (_viewModel.IsRenameDialogOpen
                || _viewModel.IsDeleteDialogOpen
                || _viewModel.IsAutomationDialogOpen
                || _viewModel.IsAutomationDeleteDialogOpen))
        {
            eventArgs.Handled = true;
            _viewModel.CloseDialogs();
            return;
        }
    }

    /// <summary>Runs before focused controls so Ctrl+letter shortcuts still work in the terminal.</summary>
    private void OnWindowShortcutTunnel(object? sender, KeyEventArgs eventArgs)
    {
        if (_viewModel.IsShortcutCapturing)
        {
            return;
        }

        if (FormatShortcutGesture(eventArgs.Key, eventArgs.KeyModifiers) is { } gesture
            && _viewModel.FindShortcut(gesture) is { } shortcut)
        {
            eventArgs.Handled = true;
            ExecuteShortcut(shortcut.ActionId);
        }
    }

    private void ExecuteShortcut(string actionId)
    {
        switch (actionId)
        {
            case "newChat":
                OnNewSessionClicked(this, new RoutedEventArgs());
                break;
            case "archiveChat":
                _viewModel.ArchiveActiveSession();
                break;
            case "copyLastCodeBlock":
                _ = CopyLastCodeBlockAsync();
                break;
            case "markUnread":
                _viewModel.ToggleActiveSessionUnread();
                break;
            case "togglePin":
                _viewModel.ToggleActiveSessionPinned();
                break;
            case "searchSessions":
                if (_viewModel.IsSettingsOpen)
                {
                    SettingsSearchBox.Focus();
                }
                else
                {
                    SearchBox.Focus();
                }

                break;
            case "openSettings":
                _viewModel.OpenSettings();
                break;
            case "toggleTerminal":
                ToggleTerminalFromAnyPage();
                break;
            case "closeWindow":
                _closeRequestedByShortcut = true;
                Close();
                break;
        }
    }

    private static string? FormatShortcutGesture(Key key, KeyModifiers modifiers)
    {
        if (IsModifierKey(key))
        {
            return null;
        }

        var hasModifier = (modifiers & (KeyModifiers.Control | KeyModifiers.Shift
            | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        var isFunctionKey = key is >= Key.F1 and <= Key.F12;
        if (!hasModifier && !isFunctionKey)
        {
            return null;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Win");
        }

        if (FormatShortcutKey(key) is not { } label)
        {
            return null;
        }

        parts.Add(label);
        return string.Join("+", parts);
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;

    private static string? FormatShortcutKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemSemicolon => ";",
        Key.OemQuestion => "/",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        Key.OemQuotes => "'",
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        _ => key.ToString()
    };

    private async Task CopyLastCodeBlockAsync()
    {
        if (_viewModel.LastAssistantCodeBlock() is not { } code)
        {
            _viewModel.ReportError("当前聊天没有可复制的代码块。");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(code);
        }
        catch (Exception error)
        {
            _viewModel.ReportError($"无法复制代码块：{error.Message}");
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        var action = _viewModel.CloseWindowAction;
        var confirm = action == "always"
            || (action == "shortcut" && _closeRequestedByShortcut);
        if (!_forceClose && confirm)
        {
            eventArgs.Cancel = true;
            _closeRequestedByShortcut = false;
            _viewModel.OpenCloseConfirm();
            return;
        }

        _automationTimer.Stop();
        _providerRefreshTimer.Stop();
        if (_viewModel.IsBusy)
        {
            _ = _viewModel.CancelAsync();
        }
        _ = _viewModel.ShutdownAsync();
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
            eventArgs.GetPosition(SettingsHost).X, SidebarMinWidth, SidebarMaxWidth);
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

    private async void OnAppearanceFontSizeCommitted(object? sender, RoutedEventArgs eventArgs) =>
        await CommitAppearanceFontSizeAsync();

    private async void OnAppearanceFontSizeKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
        {
            return;
        }

        eventArgs.Handled = true;
        await CommitAppearanceFontSizeAsync();
    }

    private void OnContrastCommitted(object? sender, PointerReleasedEventArgs eventArgs) =>
        _viewModel.SaveAppearanceSettings();

    private void OnContrastKeyUp(object? sender, KeyEventArgs eventArgs) =>
        _viewModel.SaveAppearanceSettings();

    private void OnContrastLostFocus(object? sender, RoutedEventArgs eventArgs) =>
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

    private void OnNotificationsButtonClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.CloseSearchPalette();
        _viewModel.ToggleNotifications();
    }

    private void OnSearchButtonClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.OpenSearchPalette();
        Dispatcher.UIThread.Post(() =>
        {
            PaletteSearchBox.Focus();
            PaletteSearchBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void OnNotificationMenuClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HidePreviewCards();
        if (sender is not Control anchor)
        {
            return;
        }

        NotificationMenuPopup.PlacementTarget = anchor;
        NotificationMenuPopup.IsOpen = !NotificationMenuPopup.IsOpen;
        eventArgs.Handled = true;
    }

    private void OnNotificationFilterClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string filter })
        {
            _viewModel.SetNotificationFilter(filter);
        }

        NotificationMenuPopup.IsOpen = false;
    }

    private void OnMarkAllNotificationsReadClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.MarkAllNotificationsRead();
        NotificationMenuPopup.IsOpen = false;
    }

    private void OnArchiveNotificationsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.ArchiveNotificationSessions();
        NotificationMenuPopup.IsOpen = false;
    }

    private async void OnNotificationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: NotificationItemViewModel notification })
        {
            _viewModel.ToggleNotifications();
            await OpenPaletteSessionAsync(notification.Session);
        }
    }

    private void OnPaletteCloseClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseSearchPalette();

    private void OnPaletteFilterClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string filter })
        {
            _viewModel.SetPaletteFilter(filter);
        }
    }

    private async void OnPaletteNewChatClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.CloseSearchPalette();
        _viewModel.Navigate(AppSection.Chat);
        await _viewModel.NewSessionAsync();
        FocusComposer();
    }

    private async void OnPaletteOpenFolderClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.CloseSearchPalette();
        await PickWorkspaceAsync();
    }

    private void OnPaletteFileClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: PaletteFileGroupViewModel file })
        {
            InsertPaletteMention(file.RelativePath);
        }
    }

    private void OnPaletteTextLineClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: PaletteLineViewModel line })
        {
            InsertPaletteMention(line.RelativePath);
        }
    }

    /// <summary>Palette file results become @-mentions so the next prompt can cite them.</summary>
    private void InsertPaletteMention(string relativePath)
    {
        var mention = "@" + relativePath.Replace('\\', '/') + " ";
        var composer = _viewModel.Composer;
        _viewModel.Composer = string.IsNullOrWhiteSpace(composer)
            ? mention
            : composer.TrimEnd() + " " + mention;
        _viewModel.CloseSearchPalette();
        _viewModel.Navigate(AppSection.Chat);
        Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Background);
    }

    private async void OnPaletteSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (GetSession(sender) is { } session)
        {
            _viewModel.CloseSearchPalette();
            await OpenPaletteSessionAsync(session);
        }
    }

    private async Task OpenPaletteSessionAsync(SessionItemViewModel session)
    {
        _viewModel.Navigate(AppSection.Chat);
        await _viewModel.OpenSessionAsync(session);
        FocusComposer();
    }

    private void OnPaletteSearchKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            _viewModel.CloseSearchPalette();
            eventArgs.Handled = true;
        }
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (ReferenceEquals(eventArgs.Source, sender))
        {
            _viewModel.CloseSearchPalette();
        }
    }

    private void OnExitClicked(object? sender, RoutedEventArgs eventArgs) => Close();

    private async void OnRegenerateMessageClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ChatItemViewModel item })
        {
            await _viewModel.RegenerateAsync(item);
        }
    }

    private async void OnResendUserMessageClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ChatItemViewModel item })
        {
            await _viewModel.ResendUserAsync(item);
        }
    }

    private void OnCancelEditMessageClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CancelEditUserMessage();

    private void OnEditUserMessageClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: ChatItemViewModel item })
        {
            _viewModel.BeginEditUserMessage(item);
            FocusComposer();
        }
    }

    private static SessionItemViewModel? GetSession(object? sender) =>
        sender is Control control
            ? control.Tag as SessionItemViewModel ?? control.DataContext as SessionItemViewModel
            : null;

    private static WorkspaceItemViewModel? GetWorkspace(object? sender) =>
        (sender as Control)?.DataContext as WorkspaceItemViewModel;

    private static AutomationItemViewModel? GetAutomation(object? sender) =>
        (sender as Control)?.DataContext as AutomationItemViewModel;
}


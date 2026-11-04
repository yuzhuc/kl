using System.Collections.Specialized;
using Avalonia;
using System.Diagnostics;
using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Kl.Gui.Services;
using Kl.Gui.ViewModels;

namespace Kl.Gui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _automationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _railHintOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _moreMenuOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _moreMenuCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private Control? _railHintTarget;
    private long _lastRailPointerNavigation;
    private bool _moreMenuPinned;
    private bool _moreMenuHovered;

    public MainWindow()
    {
        _viewModel = new MainWindowViewModel(
            coreSessionService: new CoreSessionService(),
            dispatcher: action => Dispatcher.UIThread.Post(action));
        InitializeComponent();
        DataContext = _viewModel;
        // Popup contents live in their own visual tree; give the More menu the
        // view-model explicitly so the pin bindings (Classes.pinned) resolve.
        MoreMenuCard.DataContext = _viewModel;
        ApplyContentFontResources();
        UpdatePinGlyphs();
        SidebarPanelBorder.Width = _viewModel.IsSidebarVisible ? SidebarExpandedWidth : 0;
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var item in _viewModel.Messages)
        {
            item.PropertyChanged += OnMessagePropertyChanged;
        }
        _automationTimer.Tick += OnAutomationTimerTick;
        _automationTimer.Start();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        MorePopup.PlacementTarget = MoreButton;
        _railHintOpenTimer.Tick += OnRailHintOpenTimerTick;
        _moreMenuOpenTimer.Tick += OnMoreMenuOpenTimerTick;
        _moreMenuCloseTimer.Tick += OnMoreMenuCloseTimerTick;
        ConfigureRailPointerNavigation();
    }

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

    private void OnToggleMapPinnedClicked(object? sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        _viewModel.ToggleMapPinned();
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

        if (item.IsMap)
        {
            ActivateRailSection(AppSection.Map);
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

    private void OnMoreMapClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMoreMenu();
        ActivateRailSection(AppSection.Map);
    }

    private void OnMoreCodeReviewClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMoreMenu();
        _viewModel.StartCodeReviewDraft();
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
        _viewModel.Navigate(AppSection.Chat);
        await _viewModel.NewSessionAsync();
        FocusComposer();
    }

    private async void OnSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: SessionItemViewModel session })
        {
            _viewModel.Navigate(AppSection.Chat);
            await _viewModel.OpenSessionAsync(session);
            FocusComposer();
        }
    }

    private async void OnWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
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

    private void OnNavImageClicked(object? sender, RoutedEventArgs eventArgs) =>
        ActivateRailSection(AppSection.Image);

    private void OnNavLibraryClicked(object? sender, RoutedEventArgs eventArgs) =>
        ActivateRailSection(AppSection.Library);

    private void OnNavPluginsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (SkipPointerGeneratedRailClick())
        {
            return;
        }
        ActivateRailSection(AppSection.Plugins);
    }

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
        SetRailSection(section);
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
        _viewModel.OpenSettings();
    }

    private void OnOpenModelSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenSettings(SettingsPane.Models);

    private void OnWorkspacePickerClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button button)
        {
            return;
        }
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
        flyout.ShowAt(button);
    }

    private void OnSettingsGeneralClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SelectSettingsPane(SettingsPane.General);

    private void OnSettingsModelsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SelectSettingsPane(SettingsPane.Models);

    private void OnSettingsDataClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SelectSettingsPane(SettingsPane.Data);

    private void OnSettingsAboutClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SelectSettingsPane(SettingsPane.About);

    private async void OnAddWorkspaceClicked(object? sender, RoutedEventArgs eventArgs) =>
        await PickWorkspaceAsync();

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

    private async void OnSaveSettingsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.SaveSettingsAsync();
        ApplyContentFontResources();
    }

    private async void OnTestConnectionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.TestConnectionAsync();
    }

    private async void OnRetryCoreClicked(object? sender, RoutedEventArgs eventArgs)
    {
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

    private void OnOpenStateDirectoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        OpenDirectory(_viewModel.StateDirectory, "数据目录");

    private void OnOpenPluginDirectoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        OpenDirectory(_viewModel.PluginDirectoryPath, "插件目录");

    private void OnRefreshPluginsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.RefreshPlugins();

    private async void OnImportPluginClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含 plugin.json 的插件目录",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.ImportPlugin(path);
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
        if (sender is not Button { DataContext: ChatItemViewModel item } button)
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

    private void OnCloseDialogClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseDialogs();

    private void OnConfirmRenameClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ConfirmRename();

    private void OnConfirmDeleteClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ConfirmDelete();

    private void OnActivityClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: ChatItemViewModel item })
        {
            item.Toggle();
        }
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

    private async void OnComposerKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter
            || !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        eventArgs.Handled = true;
        await _viewModel.SendAsync();
        FocusComposer();
        ScrollTranscript();
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        HideRailHint();
        if (!MorePopup.IsOpen || MoreButton.IsPointerOver || MoreMenuCard.IsPointerOver)
        {
            return;
        }

        CloseMoreMenu();
    }

    private void OnWindowDeactivated(object? sender, EventArgs eventArgs) =>
        HideRailHintAndMoreMenu();

    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape && _viewModel.IsApprovalOpen)
        {
            eventArgs.Handled = true;
            _ = _viewModel.RespondApprovalAsync("deny");
            return;
        }

        if (eventArgs.Key == Key.Escape && _viewModel.IsSettingsOpen)
        {
            eventArgs.Handled = true;
            _viewModel.CloseSettings();
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

        if (eventArgs.Key == Key.OemComma && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            eventArgs.Handled = true;
            _viewModel.OpenSettings();
            return;
        }

        if (eventArgs.Key == Key.N && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            eventArgs.Handled = true;
            OnNewSessionClicked(sender, new RoutedEventArgs());
            return;
        }

        if (eventArgs.Key == Key.F && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            eventArgs.Handled = true;
            SearchBox.Focus();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _automationTimer.Stop();
        if (_viewModel.IsBusy)
        {
            _ = _viewModel.CancelAsync();
        }
        _ = _viewModel.ShutdownAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSidebarVisible))
        {
            AnimateSidebar(_viewModel.IsSidebarVisible);
            return;
        }

        if (eventArgs.PropertyName is nameof(MainWindowViewModel.IsAddProjectPinned)
            or nameof(MainWindowViewModel.IsMapPinned)
            or nameof(MainWindowViewModel.IsCodeReviewPinned))
        {
            UpdatePinGlyphs();
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

    private const double SidebarExpandedWidth = 284;

    /// <summary>Codex-style slide: the sidebar column animates instead of jumping.</summary>
    private void AnimateSidebar(bool visible)
    {
        var target = visible ? SidebarExpandedWidth : 0;
        var current = SidebarPanelBorder.Width;
        var start = double.IsNaN(current) ? (visible ? 0 : SidebarExpandedWidth) : current;

        if (Math.Abs(start - target) < 0.5)
        {
            SidebarPanelBorder.Width = target;
            SidebarPanelBorder.BorderThickness = visible ? new Thickness(0, 0, 1, 0) : default;
            return;
        }

        SidebarPanelBorder.BorderThickness = visible ? new Thickness(0, 0, 1, 0) : default;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(Border.WidthProperty, start) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(Border.WidthProperty, target) },
                },
            },
        };

        _ = RunSidebarAnimationAsync(animation, target, visible);
    }

    private async Task RunSidebarAnimationAsync(Animation animation, double target, bool visible)
    {
        await animation.RunAsync(SidebarPanelBorder);
        SidebarPanelBorder.Width = target;
        SidebarPanelBorder.BorderThickness = visible ? new Thickness(0, 0, 1, 0) : default;
    }

    /// <summary>Filled pushpin when pinned, outline when not.</summary>
    private void UpdatePinGlyphs()
    {
        var pinnedBrush = new SolidColorBrush(Color.Parse("#C6C7C9"));
        var idleBrush = new SolidColorBrush(Color.Parse("#737374"));

        Apply(PinProjectGlyph, _viewModel.IsAddProjectPinned);
        Apply(PinMapGlyph, _viewModel.IsMapPinned);
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
        ScrollTranscript();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ChatItemViewModel.Text)
            or nameof(ChatItemViewModel.Detail)
            or nameof(ChatItemViewModel.IsExpanded))
        {
            ScrollTranscript();
        }
    }

    private void ScrollTranscript()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.IsChatPage && _viewModel.HasConversation)
            {
                TranscriptScroller.ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }

    private void ApplyContentFontResources()
    {
        if (Application.Current is not { } application)
        {
            return;
        }
        var size = (double)_viewModel.ContentFontSize;
        application.Resources["ChatContentFontSize"] = size;
        application.Resources["ChatContentLineHeight"] = size + 10;
        application.Resources["ChatSecondaryFontSize"] = size - 1;
        application.Resources["ChatSecondaryLineHeight"] = size + 6;
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
        _viewModel.ToggleSidebar();

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
        _viewModel.ToggleRecent();

    private void OnShowAboutClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenSettings(SettingsPane.About);

    private void OnExitClicked(object? sender, RoutedEventArgs eventArgs) => Close();

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

    private static AutomationItemViewModel? GetAutomation(object? sender) =>
        (sender as Control)?.DataContext as AutomationItemViewModel;
}


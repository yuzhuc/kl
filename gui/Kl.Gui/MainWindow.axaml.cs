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
        SidebarPanelBorder.Width = _viewModel.IsSidebarVisible
            ? Math.Clamp(_viewModel.SidebarWidth, SidebarMinWidth, SidebarMaxWidth)
            : 0;
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
        _viewModel.ToggleProjectSection();

    private void OnToggleArchivedClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ToggleArchived();

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

    private const double SidebarMinWidth = 238;
    private const double SidebarMaxWidth = 468;

    // Width of the left rail; the sidebar's left edge sits right after it.
    private const double RailWidth = 52;

    // Dragging the splitter below half of the minimum width collapses the sidebar;
    // pulling the collapsed sidebar past the same offset expands it again.
    private const double SidebarCollapseThreshold = SidebarMinWidth * 0.5;

    private bool _sidebarDragging;
    private CancellationTokenSource? _sidebarAnimationCts;
    private double _sidebarAnimationTarget;
    private bool _sidebarAnimating;

    /// <summary>Codex-style slide: the sidebar column animates instead of jumping.</summary>
    private void AnimateSidebar(bool visible)
    {
        var expandedWidth = double.IsNaN(_viewModel.SidebarWidth)
            ? SidebarMinWidth
            : Math.Clamp(_viewModel.SidebarWidth, SidebarMinWidth, SidebarMaxWidth);
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
        _viewModel.SetSidebarVisible(!_viewModel.IsSidebarVisible);

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


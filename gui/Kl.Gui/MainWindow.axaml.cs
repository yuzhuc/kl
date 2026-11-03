using System.Collections.Specialized;
using Avalonia;
using System.Diagnostics;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kl.Gui.ViewModels;

namespace Kl.Gui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly DispatcherTimer _automationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _railHintOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _moreMenuOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _moreMenuCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private Control? _railHintTarget;
    private bool _moreMenuPinned;
    private bool _moreMenuHovered;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ApplyContentFontResources();
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var item in _viewModel.Messages)
        {
            item.PropertyChanged += OnMessagePropertyChanged;
        }
        _automationTimer.Tick += OnAutomationTimerTick;
        _automationTimer.Start();
        MorePopup.PlacementTarget = MoreButton;
        _railHintOpenTimer.Tick += OnRailHintOpenTimerTick;
        _moreMenuOpenTimer.Tick += OnMoreMenuOpenTimerTick;
        _moreMenuCloseTimer.Tick += OnMoreMenuCloseTimerTick;
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
    }

    private void HideRailHint()
    {
        _railHintOpenTimer.Stop();
        _railHintTarget = null;
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
            MorePopup.IsOpen = true;
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
        MorePopup.IsOpen = true;
    }

    private void OnMorePopupClosed(object? sender, EventArgs eventArgs)
    {
        _moreMenuOpenTimer.Stop();
        _moreMenuCloseTimer.Stop();
        _moreMenuPinned = false;
        _moreMenuHovered = MoreButton.IsPointerOver;
    }

    private async void OnMoreAddWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMoreMenu();
        await PickWorkspaceAsync();
    }

    private void OnToggleAddProjectPinnedClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ToggleAddProjectPinned();

    private void OnToggleCodeReviewPinnedClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ToggleCodeReviewPinned();

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

    private void OnNewSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.Navigate(AppSection.Chat);
        _viewModel.NewSession();
        FocusComposer();
    }

    private void OnSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: SessionItemViewModel session })
        {
            _viewModel.Navigate(AppSection.Chat);
            _viewModel.OpenSession(session);
            FocusComposer();
        }
    }

    private void OnWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: WorkspaceItemViewModel workspace })
        {
            _viewModel.Navigate(AppSection.Chat);
            _viewModel.SelectWorkspace(workspace);
            FocusComposer();
        }
    }

    private void OnNewWorkspaceSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: WorkspaceItemViewModel workspace })
        {
            _viewModel.Navigate(AppSection.Chat);
            _viewModel.SelectWorkspace(workspace);
            _viewModel.NewSession();
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
        _viewModel.Navigate(AppSection.Chat);
        FocusComposer();
    }

    private void OnNavPluginsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.Navigate(AppSection.Plugins);

    private void OnNavAutomationClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.Navigate(AppSection.Automation);

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

    private void OnNavSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenSettings();

    private void OnCloseSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseSettings();

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
            item.Click += (_, _) =>
            {
                _viewModel.SelectWorkspace(workspace);
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
        _viewModel.SaveSettings();
        ApplyContentFontResources();
        await Task.CompletedTask;
    }

    private async void OnTestConnectionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.TestConnectionAsync();
    }

    private void OnResetSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ResetSettingsDefaults();

    private void OnClearApiKeyClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ClearStoredApiKey();

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

    private static SessionItemViewModel? GetSession(object? sender) =>
        sender is Control control
            ? control.Tag as SessionItemViewModel ?? control.DataContext as SessionItemViewModel
            : null;

    private static AutomationItemViewModel? GetAutomation(object? sender) =>
        (sender as Control)?.DataContext as AutomationItemViewModel;
}


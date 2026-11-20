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


    private void OnMoreButtonPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
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

    private void HideMoreMenu()
    {
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
            WorkspaceMenuPopup.IsOpen = false;
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
}

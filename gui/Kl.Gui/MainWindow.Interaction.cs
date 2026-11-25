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
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui;

public partial class MainWindow : Window
{
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

    internal void OnUnarchiveSessionClicked(object? sender, RoutedEventArgs eventArgs)
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
            builder.AppendLine(message.IsUser ? Loc.T("用户：") : "KL：");
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
}

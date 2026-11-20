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

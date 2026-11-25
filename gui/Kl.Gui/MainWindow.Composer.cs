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
        new("new", Loc.T("新建会话"), '/', () => _ = _viewModel.NewSessionAsync()),
        new("compact", Loc.T("压缩当前会话的上下文"), '/', () => _ = _viewModel.CompactActiveSessionAsync()),
        new("fork", Loc.T("把当前会话复制为分支"), '/', () => _ = _viewModel.ForkActiveSessionAsync()),
        new("settings", Loc.T("打开设置"), '/', () => _viewModel.OpenSettings()),
        new("plugins", Loc.T("打开插件页"), '/', () => _viewModel.Navigate(AppSection.Plugins)),
        new("library", Loc.T("打开资料库"), '/', () => _viewModel.Navigate(AppSection.Library)),
        new("doctor", Loc.T("运行 KL Core 诊断（设置 → 模型与连接查看）"), '/', () => _ = _viewModel.RunDoctorAsync()),
        new("review", Loc.T("运行代码审查"), '/', () =>
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
            ShowComposerSuggestions(commands, Loc.T("命令"));
            return;
        }

        var matches = ComposerSuggestionRules.Filter(GetComposerFiles(), token.Query, 40)
            .Select(path =>
            {
                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                return new ComposerSuggestionItem(
                    Path.GetFileName(path),
                    string.IsNullOrEmpty(folder) ? Loc.T("根目录") : folder,
                    '@',
                    Insert: "@" + path + " ");
            })
            .ToList();
        ShowComposerSuggestions(matches, Loc.T("文件"));
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
                Text = Loc.T("没有匹配项"),
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
        HideMoreMenu();
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

        // Esc clears the settings search first; a second Esc leaves settings,
        // matching Codex's settings page.
        if (eventArgs.Key == Key.Escape && _viewModel.IsSettingsOpen)
        {
            eventArgs.Handled = true;
            if (!string.IsNullOrEmpty(SettingsSearchBox.Text))
            {
                SettingsSearchBox.Text = string.Empty;
                ApplySettingsSearch();
            }
            else
            {
                _viewModel.CloseSettings();
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
            _viewModel.ReportError(Loc.T("当前聊天没有可复制的代码块。"));
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
            _viewModel.ReportError(Loc.F("无法复制代码块：{0}", (error.Message)));
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
}

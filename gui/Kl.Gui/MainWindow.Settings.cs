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

    private void OnCloseSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseSettings();

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
        var add = new MenuItem { Header = Loc.T("添加工作区…") };
        add.Click += async (_, _) => await PickWorkspaceAsync();
        flyout.Items.Add(add);
        flyout.ShowAt(anchor);
    }

    internal void OnMemoryScopeWorkspaceClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetMemoryScope("workspace");

    internal void OnMemoryScopeUserClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SetMemoryScope("user");

    internal void OnRefreshMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshMemoryAsync();

    internal void OnNewMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.StartNewMemory();

    internal async void OnSaveMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveMemoryAsync();

    internal async void OnDeleteMemoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.DeleteMemoryAsync();

    internal async void OnMemoryEntryClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string name })
        {
            await _viewModel.LoadMemoryAsync(name);
        }
    }

    internal void OnRefreshConfigClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshConfigAsync();

    internal void OnRefreshHooksClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.RefreshHooks();

    internal async void OnRefreshGitClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RefreshGitAsync();

    internal async void OnRefreshWorktreesClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RefreshWorktreesAsync();

    internal async void OnRunReviewClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RunReviewAsync();

    internal async void OnCreateWorktreeClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.CreateWorktreeAsync();

    internal void OnDeleteWorktreeClicked(object? sender, RoutedEventArgs eventArgs)
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

    internal void OnOpenWorktreeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: WorktreeRow row } && Directory.Exists(row.Path))
        {
            OpenInFileTarget(row.Path);
        }
    }

    internal void OnOpenHooksClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (BlockRemoteLocalPath(Loc.T("打开 hooks.toml")))
        {
            return;
        }
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

    internal void OnOpenMemorySettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        SwitchSettingsPane(SettingsPane.Memory);

    internal void OnOpenWorktreesSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        SwitchSettingsPane(SettingsPane.Worktrees);

    internal void OnOpenModelsSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        SwitchSettingsPane(SettingsPane.Models);

    internal void OnAddProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenProviderForm();

    internal void OnCancelProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseProviderForm();

    internal async void OnSaveProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderAsync();

    internal void OnEditProviderClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ProviderCardRow provider })
        {
            _viewModel.OpenProviderDetail(provider);
        }
    }

    internal void OnCloseProviderDetailClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseProviderDetail();

    internal void OnSaveProviderProfileClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveProviderProfile();

    internal async void OnSaveProviderConnectionClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderConnectionAsync();

    internal void OnDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenProviderDeleteConfirm();

    internal void OnCancelDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CancelProviderDeleteConfirm();

    internal async void OnConfirmDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.ConfirmDeleteProviderAsync();

    internal void OnAddProviderModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenAddProviderModel();

    internal async void OnFetchProviderModelsClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.FetchProviderModelsAsync();

    internal void OnEditProviderModelClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ProviderModelRow model })
        {
            _viewModel.OpenEditProviderModel(model);
        }
    }

    internal async void OnDeleteProviderModelClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ProviderModelRow model })
        {
            await _viewModel.DeleteProviderModelAsync(model);
        }
    }

    internal void OnCancelProviderModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseProviderModelForm();

    internal async void OnSaveProviderModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderModelAsync();

    internal async void OnSaveProviderApiKeyClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveProviderDetailApiKeyAsync();

    internal async void OnProviderEnabledChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is ToggleSwitch { DataContext: ProviderCardRow provider } toggle)
        {
            await _viewModel.SetProviderEnabledAsync(provider, toggle.IsChecked == true);
        }
    }

    internal async void OnProviderModelEnabledChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is ToggleSwitch { DataContext: ProviderModelRow model } toggle)
        {
            await _viewModel.SetProviderModelEnabledAsync(model, toggle.IsChecked == true);
        }
    }

    internal void OnOpenProviderWebsiteClicked(object? sender, RoutedEventArgs eventArgs)
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
                _viewModel.ReportError(Loc.F("无法打开官网链接：{0}", (error.Message)));
            }
        }
    }

    internal void OnFollowUpModeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string mode })
        {
            _viewModel.SetFollowUpMode(mode);
        }
    }

    internal void OnMotionModeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { Tag: string mode })
        {
            _viewModel.SetReduceMotionMode(mode);
        }
    }

    internal void OnDiffMarkerModeClicked(object? sender, RoutedEventArgs eventArgs)
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

    internal void OnTerminalPositionClicked(object? sender, RoutedEventArgs eventArgs)
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
            "cmd" => Loc.T("命令提示符"),
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

    internal void OnRefreshCredentialsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.RefreshCredentials();

    internal void OnOpenCredentialsClicked(object? sender, RoutedEventArgs eventArgs)
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

    internal async void OnDeleteAllMemoriesClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.DeleteAllMemoriesAsync();

    internal async void OnScanImportsClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.DiscoverImportsAsync();

    internal async void OnImportSelectedClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.ImportSelectedAsync();

    internal void OnOpenImportSourceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (BlockRemoteLocalPath(Loc.T("打开导入源目录")))
        {
            return;
        }
        var path = Directory.Exists(_viewModel.ImportCodexHome)
            ? _viewModel.ImportCodexHome
            : _viewModel.ImportClaudeHome;
        if (!Directory.Exists(path))
        {
            _viewModel.ReportError(Loc.T("没有找到 Codex 或 Claude 配置目录。"));
            return;
        }

        OpenDirectory(path, Loc.T("导入来源"));
    }

    internal void OnRefreshMcpToolsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshMcpToolsAsync();

    internal void OnShortcutEditClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ShortcutRowViewModel row })
        {
            _viewModel.BeginShortcutCapture(row);
            SettingsScroller.Offset = new Vector(0, 0);
        }
    }

    internal void OnShortcutResetClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ShortcutRowViewModel row })
        {
            _viewModel.ResetShortcut(row);
        }
    }

    internal void OnCancelShortcutCaptureClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CancelShortcutCapture();

    internal void OnSettingsNavClicked(object? sender, RoutedEventArgs eventArgs)
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
    internal void OnOpenRemoteCoreSettingsClicked(object? sender, RoutedEventArgs eventArgs)
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
    internal void OnAppearanceAdvancedClicked(object? sender, RoutedEventArgs eventArgs)
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

    internal void OnSettingsTabClicked(object? sender, RoutedEventArgs eventArgs)
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

    internal void OnEditAgentsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.OpenAgentsEditor();

    private void OnCloseAgentsEditorClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseAgentsEditor();

    private void OnSaveAgentsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveAgents();

    /// <summary>Chevron rows without a destination become clickable and open the detail sheet.</summary>
    private void OnSettingsPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
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
        foreach (var pane in SettingsPanesPanel.GetLogicalDescendants().OfType<StackPanel>()
                     .Where(pane => pane.Classes.Contains("settings-pane")))
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

    internal void OnAddWorkspaceClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.OpenCreateProjectDialog();
        Dispatcher.UIThread.Post(() => ProjectNameBox.Focus(), DispatcherPriority.Background);
    }

    private async void OnCreateProjectBrowseClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("选择源文件夹"),
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
            Title = Loc.T("选择工作区"),
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.AddWorkspace(path);
        }
    }

    internal async void OnTestConnectionClicked(object? sender, RoutedEventArgs eventArgs)
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
        _viewModel.SetPermissionMode("auto");
    }

    private void OnPermissionFullClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideOwnerFlyout(sender);
        _viewModel.SetPermissionMode("full");
    }

    private void OnPermissionAskClicked(object? sender, RoutedEventArgs eventArgs)
    {
        HideOwnerFlyout(sender);
        _viewModel.SetPermissionMode("ask");
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

    private void OnReasoningFlyoutOpened(object? sender, EventArgs eventArgs)
    {
        _viewModel.IsComposerEffortFlyoutOpen = true;
        ResetComposerPanels();
        _viewModel.RefreshComposerReasoningEfforts();
        _viewModel.RefreshComposerModelSelection();
        // Open at the current tier instantly; slides are only for presses and releases.
        StopReasoningSlide();
        UpdateReasoningThumb();
        Dispatcher.UIThread.Post(UpdateReasoningThumb, DispatcherPriority.Render);
    }

    private void OnReasoningFlyoutClosed(object? sender, EventArgs eventArgs)
    {
        _viewModel.IsComposerEffortFlyoutOpen = false;
        ResetComposerPanels();
    }

    /// <summary>
    /// Swaps to the model list like Codex: the card height tweens while the effort
    /// view slides up and out and the list slides up from below.
    /// </summary>
    private void OnComposerModelPickerClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (ComposerModelPanel.IsVisible)
        {
            return;
        }

        _viewModel.RefreshComposerModelSelection();

        // Park the list below the card so it can enter from +10px.
        ComposerModelPanel.Opacity = 0;
        ComposerModelPanelTransform.Y = 10;
        ComposerModelPanel.IsVisible = true;

        // Measure at natural width so the auto-sized card can follow the longest model name.
        ComposerModelPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var targetHeight = ComposerModelPanel.DesiredSize.Height + 10;
        // Hold the popup window at the taller card while the height tweens: the OS
        // coalesces per-frame window resizes, which turns the animation into steps.
        ReasoningFlyoutHost.Height = Math.Max(ReasoningFlyoutCard.Bounds.Height, targetHeight) + 4;
        AnimateComposerCardHeight(targetHeight);

        Dispatcher.UIThread.Post(() =>
        {
            ComposerModelPanel.Opacity = 1;
            ComposerModelPanelTransform.Y = 0;
            ComposerEffortTopRow.Opacity = 0;
            ComposerEffortTopRowTransform.Y = -10;
            ComposerEffortSliderMotion.Opacity = 0;
            ComposerEffortSliderMotionTransform.Y = -10;
        }, DispatcherPriority.Render);

        _composerPanelSwitchTimer?.Stop();
        var timer = new DispatcherTimer { Interval = ComposerPanelSwitchDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_composerPanelSwitchTimer, timer))
            {
                _composerPanelSwitchTimer = null;
                ComposerEffortPanel.IsVisible = false;
                ReasoningFlyoutCard.Height = double.NaN;
                ReasoningFlyoutHost.Height = targetHeight + 4;
            }
        };
        _composerPanelSwitchTimer = timer;
        timer.Start();
    }

    /// <summary>Restores the effort page and clears the height tween for the next open.</summary>
    private void ResetComposerPanels()
    {
        _composerPanelSwitchTimer?.Stop();
        _composerPanelSwitchTimer = null;
        StopComposerCardHeightAnimation();
        ReasoningFlyoutCard.Height = double.NaN;
        ReasoningFlyoutHost.Height = double.NaN;
        ComposerEffortPanel.IsVisible = true;
        ComposerEffortTopRow.Opacity = 1;
        ComposerEffortTopRowTransform.Y = 0;
        ComposerEffortSliderMotion.Opacity = 1;
        ComposerEffortSliderMotionTransform.Y = 0;
        ComposerModelPanel.Opacity = 0;
        ComposerModelPanelTransform.Y = 10;
        ComposerModelPanel.IsVisible = false;
    }

    private void OnComposerModelClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: ComposerModelRow row })
        {
            _viewModel.SelectComposerModel(row);
            ReasoningChipButton.Flyout?.Hide();
        }
    }

    private void OnReasoningTrackSizeChanged(object? sender, SizeChangedEventArgs eventArgs) =>
        UpdateReasoningThumb();

    private bool _reasoningDragging;
    private bool _reasoningPressTracked;
    private double _reasoningPressX;
    private double _reasoningPressScreenX;
    private double _reasoningDragFraction;
    private double _reasoningShownFraction;
    private DispatcherTimer? _reasoningSlideTimer;
    private double? _reasoningSlideTarget;
    private DispatcherTimer? _composerPanelSwitchTimer;
    private DispatcherTimer? _composerCardHeightTimer;
    private static readonly TimeSpan ReasoningSlideDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ComposerPanelSwitchDuration = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan ComposerCardHeightDuration = TimeSpan.FromMilliseconds(320);
    private static readonly Easing ComposerEnterEase =
        new SplineEasing { X1 = 0.19, Y1 = 1, X2 = 0.22, Y2 = 1 };
    private const double ReasoningThumbPressedScale = 32.0 / 28.0;

    /// <summary>The XAML translate transform that slides the knob without a layout pass.</summary>
    private TranslateTransform ReasoningThumbTransform =>
        (TranslateTransform)((TransformGroup)ReasoningThumb.RenderTransform!).Children[1];

    /// <summary>The XAML scale transform that grows the knob around its centre while held.</summary>
    private ScaleTransform ReasoningThumbScaleTransform =>
        (ScaleTransform)((TransformGroup)ReasoningThumb.RenderTransform!).Children[0];

    private TranslateTransform ComposerEffortTopRowTransform =>
        (TranslateTransform)ComposerEffortTopRow.RenderTransform!;

    private TranslateTransform ComposerEffortSliderMotionTransform =>
        (TranslateTransform)ComposerEffortSliderMotion.RenderTransform!;

    private TranslateTransform ComposerModelPanelTransform =>
        (TranslateTransform)ComposerModelPanel.RenderTransform!;

    private void SetReasoningThumbPressed(bool pressed)
    {
        var scale = pressed ? ReasoningThumbPressedScale : 1.0;
        ReasoningThumbScaleTransform.ScaleX = scale;
        ReasoningThumbScaleTransform.ScaleY = scale;
    }

    /// <summary>
    /// Layout height transitions do not tick inside the flyout, so the card height
    /// is tweened here with Codex's --cubic-enter curve.
    /// </summary>
    private void AnimateComposerCardHeight(double target)
    {
        StopComposerCardHeightAnimation();
        var from = ReasoningFlyoutCard.Bounds.Height;
        if (Math.Abs(target - from) < 0.5)
        {
            ReasoningFlyoutCard.Height = target;
            return;
        }

        var started = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp(
                (Environment.TickCount64 - started) / ComposerCardHeightDuration.TotalMilliseconds, 0, 1);
            if (t >= 1)
            {
                StopComposerCardHeightAnimation();
                ReasoningFlyoutCard.Height = target;
                return;
            }

            ReasoningFlyoutCard.Height = from + (target - from) * ComposerEnterEase.Ease(t);
        };
        _composerCardHeightTimer = timer;
        timer.Start();
    }

    private void StopComposerCardHeightAnimation()
    {
        _composerCardHeightTimer?.Stop();
        _composerCardHeightTimer = null;
    }

    /// <summary>
    /// Slides the knob to a fraction over <see cref="ReasoningSlideDuration"/> so
    /// clicks glide instead of snapping; the tween is driven here to keep the
    /// 260ms cubic-out curve used for presses and releases.
    /// </summary>
    private void SlideReasoningThumb(double target)
    {
        StopReasoningSlide();
        var from = _reasoningShownFraction;
        if (Math.Abs(target - from) < 0.005)
        {
            PositionReasoningThumb(target);
            return;
        }

        _reasoningSlideTarget = target;
        var started = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp(
                (Environment.TickCount64 - started) / ReasoningSlideDuration.TotalMilliseconds,
                0,
                1);
            if (t >= 1)
            {
                StopReasoningSlide();
                PositionReasoningThumb(target);
                return;
            }

            var eased = 1 - Math.Pow(1 - t, 3); // CubicEaseOut
            PositionReasoningThumb(from + (target - from) * eased);
        };
        _reasoningSlideTimer = timer;
        timer.Start();
    }

    private void StopReasoningSlide()
    {
        _reasoningSlideTimer?.Stop();
        _reasoningSlideTimer = null;
        _reasoningSlideTarget = null;
    }

    private void OnReasoningTrackPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(ReasoningTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _reasoningDragging = true;
        _reasoningPressTracked = false;
        SetReasoningThumbPressed(true);
        _reasoningPressX = eventArgs.GetPosition(ReasoningTrack).X;
        // Screen X, not track X: changing the preview label resizes the chip, which
        // moves this popup (and the track) under a stationary pointer.
        _reasoningPressScreenX = ReasoningTrack.PointToScreen(eventArgs.GetPosition(ReasoningTrack)).X;
        eventArgs.Pointer.Capture(ReasoningTrack);
        // Slide to the clicked position; the first real movement takes over as a drag.
        if (FractionForX(_reasoningPressX) is { } pressed)
        {
            _reasoningDragFraction = pressed;
            _viewModel.PreviewReasoningEffort(EffortForFraction(pressed));
            SlideReasoningThumb(pressed);
        }

        eventArgs.Handled = true;
    }

    private void OnReasoningTrackMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_reasoningDragging)
        {
            return;
        }

        var position = eventArgs.GetPosition(ReasoningTrack);
        var x = position.X;
        var screenX = ReasoningTrack.PointToScreen(position).X;
        if (!_reasoningPressTracked)
        {
            // Ignore click jitter so the press animation is not cut short.
            if (Math.Abs(screenX - _reasoningPressScreenX) < 8)
            {
                eventArgs.Handled = true;
                return;
            }

            _reasoningPressTracked = true;
            StopReasoningSlide();
        }

        DragReasoningThumb(x);
        eventArgs.Handled = true;
    }

    private void OnReasoningTrackReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_reasoningDragging)
        {
            return;
        }

        _reasoningDragging = false;
        SetReasoningThumbPressed(false);
        eventArgs.Pointer.Capture(null);
        var count = _viewModel.ComposerReasoningEffortOptions.Count;
        var tierFraction = count > 1
            ? Math.Round(_reasoningDragFraction * (count - 1)) / (count - 1)
            : 0;
        _viewModel.SetComposerReasoningEffort(EffortForFraction(_reasoningDragFraction));
        _viewModel.ClearReasoningEffortPreview();
        SlideReasoningThumb(tierFraction);
    }

    /// <summary>Drops the drag if capture is lost (for example on Alt+Tab).</summary>
    private void OnReasoningTrackCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        if (!_reasoningDragging)
        {
            return;
        }

        _reasoningDragging = false;
        SetReasoningThumbPressed(false);
        StopReasoningSlide();
        _viewModel.ClearReasoningEffortPreview();
        UpdateReasoningThumb();
    }

    /// <summary>
    /// Moves the knob continuously under the pointer; the label previews the
    /// nearest tier and the knob snaps onto that tier on release.
    /// </summary>
    private void DragReasoningThumb(double x)
    {
        if (FractionForX(x) is not { } fraction)
        {
            return;
        }

        _reasoningDragFraction = fraction;
        _viewModel.PreviewReasoningEffort(EffortForFraction(fraction));
        PositionReasoningThumb(fraction);
    }

    /// <summary>Maps a pointer X to a knob fraction; null before layout.</summary>
    private double? FractionForX(double x)
    {
        var width = ReasoningTrack.Bounds.Width;
        var thumbWidth = ReasoningThumb.Bounds.Width;
        var travel = width - thumbWidth;
        if (travel <= 0)
        {
            return null;
        }

        // Map the pointer to the thumb centre so the knob stays under the cursor.
        return Math.Clamp((x - (thumbWidth / 2)) / travel, 0, 1);
    }

    private void UpdateReasoningThumb()
    {
        if (_reasoningDragging)
        {
            return;
        }

        var count = _viewModel.ComposerReasoningEffortOptions.Count;
        var index = Math.Clamp(_viewModel.ComposerReasoningEffortIndex, 0, Math.Max(0, count - 1));
        var fraction = count > 1 ? index / (count - 1.0) : 0;

        // A live slide already lands on this fraction; a fast write, a same-tier
        // click or a re-post after layout must not cut it short.
        if (_reasoningSlideTimer is not null
            && _reasoningSlideTarget is { } target
            && Math.Abs(target - fraction) < 0.005)
        {
            return;
        }

        StopReasoningSlide();
        _reasoningDragFraction = fraction;
        PositionReasoningThumb(fraction);
    }

    private void PositionReasoningThumb(double fraction)
    {
        var width = ReasoningTrack.Bounds.Width;
        var thumbWidth = ReasoningThumb.Bounds.Width;
        var travel = Math.Max(0, width - thumbWidth);
        ReasoningThumbTransform.X = travel * fraction;

        // The fill runs from the track start to the thumb centre.
        ReasoningFill.Width = Math.Clamp(travel * fraction + (thumbWidth / 2), 0, width);

        // One dot per tier, centred on where the thumb stops.
        var count = Math.Max(1, _viewModel.ComposerReasoningEffortOptions.Count);
        var offset = (thumbWidth / 2) - 2;
        var ticks = new[]
        {
            ReasoningTick0, ReasoningTick1, ReasoningTick2, ReasoningTick3,
            ReasoningTick4, ReasoningTick5, ReasoningTick6, ReasoningTick7
        };
        for (var index = 0; index < ticks.Length; index++)
        {
            var visible = index < count;
            ticks[index].IsVisible = visible;
            if (!visible)
            {
                continue;
            }
            Canvas.SetLeft(ticks[index], count > 1 ? travel * index / (count - 1.0) + offset : offset);
            Canvas.SetTop(ticks[index], 2);
        }

        _reasoningShownFraction = fraction;
        UpdateReasoningFillColor(fraction);
    }

    /// <summary>
    /// The whole fill turns from the accent blue towards purple as the knob
    /// moves right; the tier, not the position inside the bar, picks the colour.
    /// A faint left-to-right ramp keeps the bar from looking flat.
    /// </summary>
    private void UpdateReasoningFillColor(double fraction)
    {
        var purple = _viewModel.ReasoningPurple;
        var blue = Color.Parse(_viewModel.AccentColorHex);
        var t = Math.Pow(Math.Clamp(fraction, 0, 1), 0.8);
        ReasoningFill.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                // The left end keeps a visible blue lean; the right end carries the
                // full tier purple so the bar keeps a left-to-right gradient.
                new GradientStop(LerpColor(blue, purple, t * (0.3 + 0.3 * t)), 0),
                new GradientStop(LerpColor(blue, purple, t), 1)
            }
        };
    }

    private static Color LerpColor(Color from, Color to, double t) => Color.FromArgb(
        Lerp(from.A, to.A, t),
        Lerp(from.R, to.R, t),
        Lerp(from.G, to.G, t),
        Lerp(from.B, to.B, t));

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)Math.Round(from + (to - from) * t);

    private string EffortForFraction(double fraction)
    {
        var options = _viewModel.ComposerReasoningEffortOptions;
        if (options.Count == 0)
        {
            return "default";
        }
        var index = (int)Math.Round(Math.Clamp(fraction, 0, 1) * (options.Count - 1));
        return options[Math.Clamp(index, 0, options.Count - 1)].Id;
    }

    internal async void OnSaveModelClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveModelAsync();

    internal async void OnSaveApiKeyClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.SaveApiKeyAsync();

    internal async void OnSaveCustomInstructionsClicked(object? sender, RoutedEventArgs eventArgs) =>
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
            Title = Loc.T("导出会话"),
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
            _viewModel.ReportError(Loc.F("导出会话失败：{0}", (error.Message)));
        }
    }

    private async void OnForkSessionClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.ForkActiveSessionAsync();

    private async void OnCompactSessionClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.CompactActiveSessionAsync();

    internal async void OnRunDoctorClicked(object? sender, RoutedEventArgs eventArgs) =>
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

    internal void OnResetSettingsClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ResetSettingsDefaults();

    internal void OnRefreshStorageClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshStorageAsync();

    internal void OnRefreshAuditClicked(object? sender, RoutedEventArgs eventArgs) =>
        _ = _viewModel.RefreshAuditAsync();

    private void OnSkillRowClicked(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: SkillRow row })
        {
            _ = _viewModel.OpenSkillAsync(row.Name);
        }
    }

    private void OnCloseSkillDetailClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.CloseSkillDetail();

    internal void OnOpenStateDirectoryClicked(object? sender, RoutedEventArgs eventArgs) =>
        OpenDirectory(_viewModel.StateDirectory, Loc.T("数据目录"));

    internal void OnOpenKlHomeClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (BlockRemoteLocalPath(Loc.T("打开 KL Home")))
        {
            return;
        }
        OpenDirectory(_viewModel.KlHomeDirectory, "KL Home");
    }

    internal void OnOpenCoreConfigClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (BlockRemoteLocalPath(Loc.T("打开 config.toml")))
        {
            return;
        }
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

    internal async void OnExportStateBackupClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("导出设置备份"),
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

    internal async void OnImportStateBackupClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("导入设置备份"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.ImportStateBackup(path);
        }
    }

    private void OnOpenPluginDirectoryClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (BlockRemoteLocalPath(Loc.T("打开插件目录")))
        {
            return;
        }
        OpenDirectory(_viewModel.PluginDirectoryPath, Loc.T("插件目录"));
    }

    private bool BlockRemoteLocalPath(string label)
    {
        if (!_viewModel.IsRemoteCore)
        {
            return false;
        }
        _viewModel.ReportError(MainWindowViewModel.RemoteDisabled(label));
        return true;
    }

    private async void OnRefreshPluginsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        _viewModel.RefreshPlugins();
        await _viewModel.RefreshPluginsAsync();
    }

    internal async void OnReloadPluginClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: PluginItemViewModel plugin })
        {
            await _viewModel.ReloadPluginAsync(plugin);
        }
    }

    internal async void OnPluginToggled(object? sender, RoutedEventArgs eventArgs)
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
            Title = Loc.T("选择包含 plugin.json 的插件目录"),
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
            _viewModel.ReportError(Loc.F("无法打开{0}：{1}", (label), (error.Message)));
        }
    }

    internal async void OnCopyStatePathClicked(object? sender, RoutedEventArgs eventArgs)
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
            _viewModel.ReportError(Loc.F("无法复制状态文件路径：{0}", (error.Message)));
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
            ToolTip.SetTip(button, Loc.T("已复制"));
            button.Opacity = 1;
            await Task.Delay(1200);
            ToolTip.SetTip(button, Loc.T("复制回复"));
            button.Opacity = 0.72;
        }
        catch (Exception error)
        {
            _viewModel.ReportError(Loc.F("无法复制回复：{0}", (error.Message)));
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

    internal void OnSaveRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.SaveRemoteCoreSettings();

    internal void OnClearRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        _viewModel.ClearRemoteCoreSettings();

    internal async void OnTestRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.TestRemoteCoreConnectionAsync();

    internal async void OnReconnectCoreClicked(object? sender, RoutedEventArgs eventArgs) =>
        await _viewModel.RetryCoreAsync();

    internal void OnOpenCleanupClicked(object? sender, RoutedEventArgs eventArgs) =>
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
            Title = Loc.T("选择文件夹"),
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
}

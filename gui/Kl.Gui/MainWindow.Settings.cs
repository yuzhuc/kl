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
}

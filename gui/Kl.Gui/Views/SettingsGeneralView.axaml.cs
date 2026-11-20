using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kl.Client;
using Kl.Gui.Services;
using Kl.Gui.ViewModels;

namespace Kl.Gui.Views;

public partial class SettingsGeneralView : UserControl
{
    public SettingsGeneralView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private MainWindowViewModel? _viewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StorageBarColumns)
            && _viewModel is not null)
        {
            StorageBar.ColumnDefinitions = _viewModel.StorageBarColumns;
        }
    }

    private void OnAppearanceAdvancedClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnAppearanceAdvancedClicked(sender, eventArgs);
    private void OnAppearanceFontSizeCommitted(object? sender, RoutedEventArgs eventArgs) => Host!.OnAppearanceFontSizeCommitted(sender, eventArgs);
    private void OnAppearanceFontSizeKeyDown(object? sender, KeyEventArgs eventArgs) => Host!.OnAppearanceFontSizeKeyDown(sender, eventArgs);
    private void OnCancelShortcutCaptureClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnCancelShortcutCaptureClicked(sender, eventArgs);
    private void OnContrastCommitted(object? sender, PointerReleasedEventArgs eventArgs) => Host!.OnContrastCommitted(sender, eventArgs);
    private void OnContrastLostFocus(object? sender, RoutedEventArgs eventArgs) => Host!.OnContrastLostFocus(sender, eventArgs);
    private void OnCopyStatePathClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnCopyStatePathClicked(sender, eventArgs);
    private void OnDeleteAllMemoriesClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnDeleteAllMemoriesClicked(sender, eventArgs);
    private void OnDeleteMemoryClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnDeleteMemoryClicked(sender, eventArgs);
    private void OnDiffMarkerModeClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnDiffMarkerModeClicked(sender, eventArgs);
    private void OnEditAgentsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnEditAgentsClicked(sender, eventArgs);
    private void OnExportStateBackupClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnExportStateBackupClicked(sender, eventArgs);
    private void OnFollowUpModeClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnFollowUpModeClicked(sender, eventArgs);
    private void OnImportSelectedClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnImportSelectedClicked(sender, eventArgs);
    private void OnImportStateBackupClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnImportStateBackupClicked(sender, eventArgs);
    private void OnMemoryEntryClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnMemoryEntryClicked(sender, eventArgs);
    private void OnMemoryScopeUserClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnMemoryScopeUserClicked(sender, eventArgs);
    private void OnMemoryScopeWorkspaceClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnMemoryScopeWorkspaceClicked(sender, eventArgs);
    private void OnMotionModeClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnMotionModeClicked(sender, eventArgs);
    private void OnNewMemoryClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnNewMemoryClicked(sender, eventArgs);
    private void OnOpenCleanupClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenCleanupClicked(sender, eventArgs);
    private void OnOpenCoreConfigClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenCoreConfigClicked(sender, eventArgs);
    private void OnOpenImportSourceClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenImportSourceClicked(sender, eventArgs);
    private void OnOpenKlHomeClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenKlHomeClicked(sender, eventArgs);
    private void OnOpenMemorySettingsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenMemorySettingsClicked(sender, eventArgs);
    private void OnOpenModelsSettingsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenModelsSettingsClicked(sender, eventArgs);
    private void OnOpenStateDirectoryClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenStateDirectoryClicked(sender, eventArgs);
    private void OnOpenWorktreesSettingsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenWorktreesSettingsClicked(sender, eventArgs);
    private void OnRefreshConfigClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshConfigClicked(sender, eventArgs);
    private void OnRefreshMemoryClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshMemoryClicked(sender, eventArgs);
    private void OnRefreshStorageClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshStorageClicked(sender, eventArgs);
    private void OnResetSettingsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnResetSettingsClicked(sender, eventArgs);
    private void OnRunDoctorClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRunDoctorClicked(sender, eventArgs);
    private void OnSaveCustomInstructionsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveCustomInstructionsClicked(sender, eventArgs);
    private void OnSaveMemoryClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveMemoryClicked(sender, eventArgs);
    private void OnScanImportsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnScanImportsClicked(sender, eventArgs);
    private void OnSessionClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSessionClicked(sender, eventArgs);
    private void OnSettingsNavClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSettingsNavClicked(sender, eventArgs);
    private void OnShortcutEditClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnShortcutEditClicked(sender, eventArgs);
    private void OnShortcutResetClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnShortcutResetClicked(sender, eventArgs);
    private void OnTerminalPositionClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnTerminalPositionClicked(sender, eventArgs);
    private void OnUnarchiveSessionClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnUnarchiveSessionClicked(sender, eventArgs);
    private void OnContrastKeyUp(object? sender, KeyEventArgs eventArgs) => Host!.OnContrastKeyUp(sender, eventArgs);
}

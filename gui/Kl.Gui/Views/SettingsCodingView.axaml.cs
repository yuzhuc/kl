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

public partial class SettingsCodingView : UserControl
{
    public SettingsCodingView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnAddWorkspaceClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnAddWorkspaceClicked(sender, eventArgs);
    private void OnClearRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnClearRemoteCoreClicked(sender, eventArgs);
    private void OnCreateWorktreeClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnCreateWorktreeClicked(sender, eventArgs);
    private void OnDeleteWorktreeClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnDeleteWorktreeClicked(sender, eventArgs);
    private void OnOpenHooksClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenHooksClicked(sender, eventArgs);
    private void OnOpenRemoteCoreSettingsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenRemoteCoreSettingsClicked(sender, eventArgs);
    private void OnOpenWorktreeClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenWorktreeClicked(sender, eventArgs);
    private void OnRefreshAuditClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshAuditClicked(sender, eventArgs);
    private void OnRefreshGitClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshGitClicked(sender, eventArgs);
    private void OnRefreshHooksClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshHooksClicked(sender, eventArgs);
    private void OnRefreshWorktreesClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshWorktreesClicked(sender, eventArgs);
    private void OnRunReviewClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRunReviewClicked(sender, eventArgs);
    private void OnSaveRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveRemoteCoreClicked(sender, eventArgs);
    private void OnSettingsTabClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSettingsTabClicked(sender, eventArgs);
    private void OnTestRemoteCoreClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnTestRemoteCoreClicked(sender, eventArgs);
    private void OnWorkspaceEditClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnWorkspaceEditClicked(sender, eventArgs);
}

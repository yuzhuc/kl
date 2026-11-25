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

public partial class SettingsIntegrationView : UserControl
{
    public SettingsIntegrationView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnOpenCredentialsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenCredentialsClicked(sender, eventArgs);
    private void OnOpenModelsSettingsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenModelsSettingsClicked(sender, eventArgs);
    private void OnOpenRemoteCoreSettingsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenRemoteCoreSettingsClicked(sender, eventArgs);
    private void OnPluginToggled(object? sender, RoutedEventArgs eventArgs) => Host!.OnPluginToggled(sender, eventArgs);
    private void OnReconnectCoreClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnReconnectCoreClicked(sender, eventArgs);
    private void OnRefreshCredentialsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshCredentialsClicked(sender, eventArgs);
    private void OnRefreshMcpToolsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRefreshMcpToolsClicked(sender, eventArgs);
    private void OnSettingsTabClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSettingsTabClicked(sender, eventArgs);
    private void OnReloadPluginClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnReloadPluginClicked(sender, eventArgs);
}

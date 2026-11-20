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

public partial class SettingsModelsView : UserControl
{
    public SettingsModelsView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private void OnAddProviderClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnAddProviderClicked(sender, eventArgs);
    private void OnAddProviderModelClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnAddProviderModelClicked(sender, eventArgs);
    private void OnCancelDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnCancelDeleteProviderClicked(sender, eventArgs);
    private void OnCancelProviderClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnCancelProviderClicked(sender, eventArgs);
    private void OnCancelProviderModelClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnCancelProviderModelClicked(sender, eventArgs);
    private void OnCloseProviderDetailClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnCloseProviderDetailClicked(sender, eventArgs);
    private void OnConfirmDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnConfirmDeleteProviderClicked(sender, eventArgs);
    private void OnDeleteProviderClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnDeleteProviderClicked(sender, eventArgs);
    private void OnDeleteProviderModelClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnDeleteProviderModelClicked(sender, eventArgs);
    private void OnEditProviderClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnEditProviderClicked(sender, eventArgs);
    private void OnEditProviderModelClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnEditProviderModelClicked(sender, eventArgs);
    private void OnFetchProviderModelsClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnFetchProviderModelsClicked(sender, eventArgs);
    private void OnOpenCoreConfigClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenCoreConfigClicked(sender, eventArgs);
    private void OnOpenProviderWebsiteClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnOpenProviderWebsiteClicked(sender, eventArgs);
    private void OnProviderEnabledChanged(object? sender, RoutedEventArgs eventArgs) => Host!.OnProviderEnabledChanged(sender, eventArgs);
    private void OnProviderModelEnabledChanged(object? sender, RoutedEventArgs eventArgs) => Host!.OnProviderModelEnabledChanged(sender, eventArgs);
    private void OnRunDoctorClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnRunDoctorClicked(sender, eventArgs);
    private void OnSaveApiKeyClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveApiKeyClicked(sender, eventArgs);
    private void OnSaveModelClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveModelClicked(sender, eventArgs);
    private void OnSaveProviderApiKeyClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveProviderApiKeyClicked(sender, eventArgs);
    private void OnSaveProviderClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveProviderClicked(sender, eventArgs);
    private void OnSaveProviderConnectionClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveProviderConnectionClicked(sender, eventArgs);
    private void OnSaveProviderModelClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveProviderModelClicked(sender, eventArgs);
    private void OnSaveProviderProfileClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnSaveProviderProfileClicked(sender, eventArgs);
    private void OnTestConnectionClicked(object? sender, RoutedEventArgs eventArgs) => Host!.OnTestConnectionClicked(sender, eventArgs);
}

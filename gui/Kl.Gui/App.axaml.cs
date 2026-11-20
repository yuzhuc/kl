using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Kl.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["ContentFont"] = new FontFamily("Inter, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        Resources["CodeFont"] = new FontFamily("Cascadia Mono, Consolas");
        Resources["UiFontWeight"] = FontWeight.Normal;
        Resources["ContentFontWeight"] = FontWeight.Normal;
        Resources["CodeFontWeight"] = FontWeight.Normal;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
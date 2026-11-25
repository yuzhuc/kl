using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Kl.Gui;

namespace Kl.Gui.HeadlessTests;

/// <summary>
/// Runs one headless Avalonia application on a dedicated dispatcher thread so
/// UI tests never depend on which thread xunit happens to use.
/// </summary>
public sealed class AvaloniaFixture : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new();

    public AvaloniaFixture()
    {
        _uiThread = new Thread(() =>
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            _ready.Set();
            Dispatcher.UIThread.MainLoop(_stop.Token);
        })
        {
            IsBackground = true,
            Name = "Kl.Gui.Headless"
        };
        _uiThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>Runs a UI action on the dispatcher thread and rethrows its failure.</summary>
    public void Run(Action action) => Dispatcher.UIThread.Invoke(action);

    public void Dispose()
    {
        _stop.Cancel();
        _uiThread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
        _stop.Dispose();
    }
}

[CollectionDefinition("avalonia", DisableParallelization = true)]
public sealed class AvaloniaCollection : ICollectionFixture<AvaloniaFixture>
{
}

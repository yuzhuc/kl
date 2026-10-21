using System.Text;
using System.Text.Json;
using Kl.Client;
using Terminal.Gui.App;

namespace Kl.Tui;

public static class TerminalGuiRunner
{
    public static bool CanUseTui =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static async Task<int> RunAsync(
        TuiOptions options,
        CancellationToken cancellationToken = default)
    {
        var presenter = new TuiSessionPresenter(options.AutoApprove);
        await using var session = await KlSessionClient.StartAsync(
            options.ToClientOptions(),
            options.ToSessionOptions(),
            presenter,
            cancellationToken).ConfigureAwait(false);

        var history = await session.HistoryAsync(20, cancellationToken).ConfigureAwait(false);
        var initialText = FormatHistory(history);

        using IApplication app = Application.Create();
        app.Init();
        var window = new TerminalGuiMainWindow(app, session, presenter, options.AutoApprove, initialText);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            window.FocusInput();
            return false;
        });
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            app.Invoke(window.FocusInput);
        });
        app.Run(window);
        window.Stop();
        return 0;
    }

    private static string FormatHistory(JsonElement history)
    {
        var builder = new StringBuilder();
        foreach (var message in ChatTuiState.ParseHistory(history))
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }
            var label = message.Role switch
            {
                "user" => "You",
                "assistant" => "KL",
                "tool" => $"[tool] {message.ToolDisplay()}",
                _ => message.Role
            };
            builder.AppendLine(message.Role == "tool"
                ? $"[tool] {message.ToolDisplay()}"
                : $"{label}: {message.Text}");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }
}
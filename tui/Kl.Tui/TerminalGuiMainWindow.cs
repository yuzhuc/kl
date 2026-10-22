// Terminal.Gui 2.5 marks TextView obsolete in favor of the separate Editor package.
#pragma warning disable CS0618
using Kl.Client;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Kl.Tui;

public sealed class TerminalGuiMainWindow : Runnable
{
    private readonly IApplication _app;
    private readonly KlSessionClient _session;
    private readonly TuiSessionPresenter _presenter;
    private readonly bool _autoApprove;
    private readonly TextView _output;
    private readonly TextField _input;
    private readonly Button _send;
    private readonly StatusBar _status;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private double _totalCostUsd;

    public TerminalGuiMainWindow(
        IApplication app,
        KlSessionClient session,
        TuiSessionPresenter presenter,
        bool autoApprove,
        string initialText)
    {
        _app = app;
        _session = session;
        _presenter = presenter;
        _autoApprove = autoApprove;
        Title = "KL";

        var menu = new MenuBar
        {
            CanFocus = false,
            Menus =
            [
                new MenuBarItem("File", [
                    new MenuItem("New", "", () => ShowInfo("use the CLI `kl chat` to create a new session")),
                    new MenuItem("Quit", "", Quit)
                ]),
                new MenuBarItem("Help", [
                    new MenuItem("About", "", ShowAbout)
                ])
            ]
        };

        _status = new StatusBar { CanFocus = false };
        _status.Add(new Shortcut(Key.F1, "About", ShowAbout));
        _status.Add(new Shortcut(Application.GetDefaultKey(Command.Quit), "Quit", Quit));

        _output = new TextView
        {
            X = 0,
            Y = Pos.Bottom(menu),
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            CanFocus = false,
            Text = initialText
        };
        _input = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(9)
        };
        _send = new Button
        {
            Text = "Send",
            X = Pos.AnchorEnd(8),
            Y = Pos.AnchorEnd(2),
            Width = 8,
            CanFocus = false
        };
        _send.Accepted += (_, _) => _ = SendAsync();
        _input.Accepted += (_, _) => _ = SendAsync();

        Add(menu, _output, _input, _send, _status);
        Initialized += (_, _) => _input.SetFocus();
        _ = PumpEventsAsync();
        SetStatus();
    }

    public void Stop() => _lifetime.Cancel();

    public void FocusInput()
    {
        _input.SetFocus();
        _input.HasFocus = true;
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var sessionEvent in _presenter.Events.ReadAllAsync(_lifetime.Token))
            {
                _app.Invoke(() => Apply(sessionEvent));
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
    }

    private void Apply(SessionEvent sessionEvent)
    {
        switch (sessionEvent.Kind)
        {
            case SessionEventKind.TurnStarted:
                _busy = true;
                SetStatus("working...");
                break;
            case SessionEventKind.AssistantDelta:
                Append(sessionEvent.Text);
                break;
            case SessionEventKind.ReasoningDelta:
                Append("[reasoning] " + sessionEvent.Text);
                break;
            case SessionEventKind.ToolStarted:
                Append($"\n[tool] {sessionEvent.Name} {sessionEvent.Args}");
                break;
            case SessionEventKind.ToolOutput:
                Append(sessionEvent.Text);
                break;
            case SessionEventKind.ToolCompleted:
                Append($"\n{(sessionEvent.Ok ? "[ok]" : "[fail]")} {sessionEvent.Name}: {sessionEvent.Summary}");
                if (!string.IsNullOrWhiteSpace(sessionEvent.Diff))
                {
                    Append("\n" + sessionEvent.Diff);
                }
                break;
            case SessionEventKind.ApprovalRequested:
                ResolveApproval(sessionEvent);
                break;
            case SessionEventKind.ApprovalResolved:
                Append($"\n[approval] {sessionEvent.Decision}");
                break;
            case SessionEventKind.TurnCompleted:
                _totalInputTokens += sessionEvent.InputTokens;
                _totalOutputTokens += sessionEvent.OutputTokens;
                _totalCostUsd += sessionEvent.CostUsd ?? 0;
                _busy = false;
                Append($"\n[done] {sessionEvent.Status}");
                SetStatus();
                break;
            case SessionEventKind.TurnFailed:
                _busy = false;
                Append($"\n[failed] {sessionEvent.Text}");
                SetStatus();
                break;
            case SessionEventKind.Error:
                Append($"\n[error] {sessionEvent.Text}");
                break;
            case SessionEventKind.AuditRecorded:
                Append($"\n[audit] {sessionEvent.Text}");
                break;
        }
        _input.Enabled = !_busy;
        _send.Enabled = !_busy;
    }

    private void ResolveApproval(SessionEvent request)
    {
        if (_autoApprove)
        {
            return;
        }
        if (request.ApprovalId is not { } approvalId)
        {
            return;
        }
        var message = $"{request.Name} ({request.Action})\n{request.Resource}\n\n{request.Reason}\n\n{request.Args}";
        var choice = MessageBox.Query(_app, "Approval required", message, "Deny", "Allow Session", "Allow");
        var decision = choice switch
        {
            1 => "allow_session",
            2 => "allow",
            _ => "deny"
        };
        _presenter.ResolveApprovalDecision(approvalId, decision);
    }

    private async Task SendAsync()
    {
        if (_busy)
        {
            return;
        }
        var prompt = _input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }
        if (prompt.Equals("/exit", StringComparison.OrdinalIgnoreCase)
            || prompt.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            Quit();
            return;
        }
        if (prompt.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            _input.Text = string.Empty;
            Append("\nCommands: /exit, /help, /cost, /status");
            return;
        }
        if (prompt.Equals("/cost", StringComparison.OrdinalIgnoreCase))
        {
            _input.Text = string.Empty;
            Append($"\nTokens: input {_totalInputTokens}, output {_totalOutputTokens}; cost ${_totalCostUsd:0.000000}");
            return;
        }
        if (prompt.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            _input.Text = string.Empty;
            Append($"\nSession {_session.SessionId} | {_session.Provider}/{_session.Model}\nWorkspace: {_session.Workspace}");
            return;
        }
        if (MemoryCommandParser.TryParse(prompt, out var memory))
        {
            _input.Text = string.Empty;
            _ = RunMemoryAsync(memory);
            return;
        }
        if (prompt.StartsWith('/'))
        {
            _input.Text = string.Empty;
            Append("\ncommand is not available in the Terminal.Gui runner yet; use the CLI command");
            return;
        }

        _input.Text = string.Empty;
        _busy = true;
        _input.Enabled = false;
        _send.Enabled = false;
        Append($"\nYou: {prompt}\n");
        try
        {
            var outcome = await _session.RunTurnAsync(prompt, _lifetime.Token).ConfigureAwait(false);
            if (outcome.Status == "failed")
            {
                _app.Invoke(() => Append($"\n[failed] {outcome.Text}"));
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            _app.Invoke(() => Append($"\n[error] {error.Message}"));
        }
        finally
        {
            _app.Invoke(() =>
            {
                _busy = false;
                _input.Enabled = true;
                _send.Enabled = true;
                _input.SetFocus();
                SetStatus();
            });
        }
    }

    private void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _output.Text = string.IsNullOrEmpty(_output.Text)
            ? text
            : _output.Text + text;
        _output.MoveEnd();
        SetNeedsDraw();
    }

    private async Task RunMemoryAsync(MemoryCommand command)
    {
        try
        {
            switch (command.Action)
            {
                case MemoryCommandAction.List:
                {
                    var result = await _session.ListMemoryAsync(command.Scope, _lifetime.Token)
                        .ConfigureAwait(false);
                    var entries = new List<string>();
                    if (result.TryGetProperty("entries", out var list)
                        && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var entry in list.EnumerateArray())
                        {
                            entries.Add($"{entry.GetProperty("name").GetString()} ({entry.GetProperty("scope").GetString()})");
                        }
                    }
                    _app.Invoke(() => Append(entries.Count == 0
                        ? "\nno memory entries"
                        : "\n" + string.Join(Environment.NewLine, entries)));
                    break;
                }
                case MemoryCommandAction.Read:
                {
                    if (string.IsNullOrWhiteSpace(command.Name))
                    {
                        _app.Invoke(() => Append("\nusage: /memory read <name>"));
                        break;
                    }
                    var result = await _session
                        .ReadMemoryAsync(command.Name, command.Scope, _lifetime.Token)
                        .ConfigureAwait(false);
                    _app.Invoke(() => Append(
                        "\n" + result.GetProperty("content").GetString()));
                    break;
                }
                case MemoryCommandAction.Write:
                {
                    if (string.IsNullOrWhiteSpace(command.Name))
                    {
                        _app.Invoke(() => Append("\nusage: /memory write <name> <content>"));
                        break;
                    }
                    var result = await _session
                        .WriteMemoryAsync(command.Name, command.Content, command.Scope, _lifetime.Token)
                        .ConfigureAwait(false);
                    _app.Invoke(() => Append(
                        $"\nwrote {result.GetProperty("name").GetString()}"));
                    break;
                }
                case MemoryCommandAction.Delete:
                {
                    if (string.IsNullOrWhiteSpace(command.Name))
                    {
                        _app.Invoke(() => Append("\nusage: /memory delete <name>"));
                        break;
                    }
                    var result = await _session
                        .DeleteMemoryAsync(command.Name, command.Scope, _lifetime.Token)
                        .ConfigureAwait(false);
                    _app.Invoke(() => Append(
                        $"\ndeleted {result.GetProperty("name").GetString()}"));
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            _app.Invoke(() => Append($"\n[error] {error.Message}"));
        }
    }

    private void SetStatus(string? state = null)
    {
        var suffix = string.IsNullOrWhiteSpace(state) ? string.Empty : $" | {state}";
        _status.Title = $"{_session.Provider}/{_session.Model} | {_session.Workspace}{suffix}";
        SetNeedsDraw();
    }

    private void ShowInfo(string message) =>
        MessageBox.Query(_app, "KL", message, "OK");

    private void ShowAbout() =>
        MessageBox.Query(_app, "About", "KL Terminal.Gui client\nBuilt with Terminal.Gui v2.", "OK");

    private void Quit()
    {
        Stop();
        _app.RequestStop(this);
    }
}
#pragma warning restore CS0618

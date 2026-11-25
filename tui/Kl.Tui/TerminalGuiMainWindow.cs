// Terminal.Gui 2.5 marks TextView obsolete in favor of the separate Editor package.
#pragma warning disable CS0618
using System.Text;
using System.Text.Json;
using Kl.Client;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Kl.Tui;

/// <summary>
/// Codex-CLI-style single-screen chat: transcript on top, composer and status
/// at the bottom. Pages are reachable through slash commands instead of a rail.
/// </summary>
public sealed class TerminalGuiMainWindow : Runnable
{
    private const string Hint = "  Enter send   Esc interrupt/clear   Ctrl+L clear   / commands";
    private readonly IApplication _app;
    private readonly KlSessionClient _session;
    private readonly TuiSessionPresenter _presenter;
    private readonly bool _autoApprove;
    private readonly TextView _transcript;
    private readonly TextField _composer;
    private readonly Label _footer;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private string? _queued;
    private bool _reasoningOpen;
    private int _runGeneration;
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

        _transcript = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            ReadOnly = true,
            Text = BuildBanner() + (string.IsNullOrWhiteSpace(initialText)
                ? string.Empty
                : Environment.NewLine + initialText)
        };
        var prompt = new Label
        {
            Text = "›",
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = 2
        };
        _composer = new TextField
        {
            X = 2,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(1)
        };
        _composer.Accepted += (_, _) => _ = SubmitAsync();
        var hint = new Label
        {
            Text = Hint,
            X = 2,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(1)
        };
        _footer = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill()
        };

        Add(_transcript, prompt, _composer, hint, _footer);
        Initialized += (_, _) => _composer.SetFocus();
        KeyDown += OnKeyDown;
        _ = PumpEventsAsync();
        SetFooter();
    }

    public void Stop() => _lifetime.Cancel();

    public void FocusInput()
    {
        _composer.SetFocus();
        _composer.HasFocus = true;
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key == Key.Esc)
        {
            key.Handled = true;
            if (_busy)
            {
                _ = CancelTurnAsync();
            }
            else
            {
                _composer.Text = string.Empty;
            }
            return;
        }
        if (key == Key.C.WithCtrl)
        {
            key.Handled = true;
            if (_busy)
            {
                _ = CancelTurnAsync();
            }
            else
            {
                Quit();
            }
            return;
        }
        if (key == Key.Q.WithCtrl)
        {
            key.Handled = true;
            Quit();
            return;
        }
        if (key == Key.L.WithCtrl)
        {
            key.Handled = true;
            ClearTranscript();
        }
    }

    private string BuildBanner()
    {
        var lines = new[]
        {
            $">_ KL CLI  ({Protocol.ClientVersion})",
            string.Empty,
            $"model: {_session.Provider}/{_session.Model}",
            $"cwd:   {_session.Workspace}",
            "Type /help for commands."
        };
        var width = Math.Clamp(lines.Max(line => line.Length) + 4, 40, 72);
        var builder = new StringBuilder();
        builder.Append('╭').Append('─', width).AppendLine("╮");
        foreach (var line in lines)
        {
            var text = line.Length <= width - 4 ? line : line[..(width - 5)] + "…";
            builder.Append("│  ").Append(text.PadRight(width - 2)).AppendLine("│");
        }
        builder.Append('╰').Append('─', width).AppendLine("╯");
        builder.AppendLine();
        return builder.ToString();
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
                SetFooter("working… (esc to interrupt)");
                break;
            case SessionEventKind.AssistantDelta:
                _reasoningOpen = false;
                Append(sessionEvent.Text);
                break;
            case SessionEventKind.ReasoningDelta:
                AppendReasoning(sessionEvent.Text);
                break;
            case SessionEventKind.ToolStarted:
                _reasoningOpen = false;
                Append($"\n  • {sessionEvent.Name} {Shorten(sessionEvent.Args, 120)}");
                break;
            case SessionEventKind.ToolOutput:
                Append(sessionEvent.Text);
                break;
            case SessionEventKind.ToolCompleted:
                Append($"\n  {(sessionEvent.Ok ? "✓" : "✗")} {sessionEvent.Name}" +
                       (string.IsNullOrWhiteSpace(sessionEvent.Summary) ? string.Empty : $": {sessionEvent.Summary}"));
                if (!string.IsNullOrWhiteSpace(sessionEvent.Diff))
                {
                    Append("\n" + sessionEvent.Diff);
                }
                break;
            case SessionEventKind.ApprovalRequested:
                ResolveApproval(sessionEvent);
                break;
            case SessionEventKind.ApprovalResolved:
                Append($"\n  approval: {sessionEvent.Decision}");
                break;
            case SessionEventKind.TurnCompleted:
                _totalInputTokens += sessionEvent.InputTokens;
                _totalOutputTokens += sessionEvent.OutputTokens;
                _totalCostUsd += sessionEvent.CostUsd ?? 0;
                _busy = false;
                _reasoningOpen = false;
                Append(Environment.NewLine);
                SetFooter();
                SendQueued();
                break;
            case SessionEventKind.TurnFailed:
                _busy = false;
                _reasoningOpen = false;
                Append($"\n  ✗ {sessionEvent.Text}\n");
                SetFooter();
                SendQueued();
                break;
            case SessionEventKind.Error:
                Append($"\n  ✗ {sessionEvent.Text}");
                break;
            case SessionEventKind.AuditRecorded:
                Append($"\n  audit: {sessionEvent.Text}");
                break;
        }
    }

    private void SendQueued()
    {
        if (_queued is not { } prompt)
        {
            return;
        }
        _queued = null;
        _ = SendPromptAsync(prompt);
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

    private async Task SubmitAsync()
    {
        var prompt = _composer.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }
        _composer.Text = string.Empty;

        if (prompt.StartsWith('/'))
        {
            await RunCommandAsync(prompt).ConfigureAwait(true);
            return;
        }
        if (_busy)
        {
            if (_queued is null)
            {
                _queued = prompt;
                Append($"\n  queued: {prompt}\n");
            }
            else
            {
                Append("\n  one message is already queued; wait or press Esc.\n");
            }
            return;
        }
        await SendPromptAsync(prompt).ConfigureAwait(true);
    }

    private async Task SendPromptAsync(string prompt)
    {
        if (_busy)
        {
            _queued = prompt;
            return;
        }
        var generation = ++_runGeneration;
        _busy = true;
        SetFooter("working… (esc to interrupt)");
        Append($"\n› {prompt}\n\n");
        try
        {
            var outcome = await _session.RunTurnAsync(prompt, _lifetime.Token).ConfigureAwait(true);
            if (outcome.Status == "failed")
            {
                Append($"\n  ✗ {outcome.Text}\n");
            }
        }
        catch (OperationCanceledException)
        {
            Append("\n  cancelled\n");
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            Append($"\n  ✗ {error.Message}\n");
        }
        finally
        {
            if (generation == _runGeneration)
            {
                _busy = false;
                SetFooter();
            }
            _composer.SetFocus();
        }
    }

    private async Task CancelTurnAsync()
    {
        try
        {
            await _session.CancelAsync(_lifetime.Token).ConfigureAwait(true);
            Append("\n  cancelled\n");
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            Append($"\n  ✗ {error.Message}\n");
        }
        finally
        {
            _queued = null;
        }
    }

    private async Task RunCommandAsync(string prompt)
    {
        var parts = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? string.Join(' ', parts[1..]).Trim() : null;
        switch (command)
        {
            case "/help":
                Append(HelpText());
                break;
            case "/exit" or "/quit":
                Quit();
                return;
            case "/clear":
                ClearTranscript();
                break;
            case "/cost":
                Append($"\n  tokens: input {_totalInputTokens}, output {_totalOutputTokens}; " +
                       $"cost ${_totalCostUsd:0.000000}\n");
                break;
            case "/status":
                await ShowStatusAsync().ConfigureAwait(true);
                break;
            case "/model":
                Append($"\n  model: {_session.Provider}/{_session.Model}\n" +
                       "  change the default in the GUI or with `kl model select`.\n");
                break;
            case "/permissions":
                await PermissionsAsync(argument).ConfigureAwait(true);
                break;
            case "/sessions":
                await ListSessionsAsync().ConfigureAwait(true);
                break;
            case "/memory":
                if (MemoryCommandParser.TryParse(prompt, out var memory))
                {
                    await RunMemoryAsync(memory).ConfigureAwait(true);
                }
                else
                {
                    Append("\n  usage: /memory list|read|write|delete ...\n");
                }
                break;
            case "/new":
                Append("\n  start a new session from the shell with `kl chat` or `kl`.\n");
                break;
            case "/about":
                Append("\n  KL Terminal client. Core owns sessions, tools, approvals and audit.\n");
                break;
            default:
                Append($"\n  unknown command: {command}. Type /help.\n");
                break;
        }
    }

    private static string HelpText() =>
        "\n  Commands\n" +
        "    /help            This help\n" +
        "    /status          Session, model and approval mode\n" +
        "    /cost            Token usage for this session\n" +
        "    /clear           Clear the transcript\n" +
        "    /model           Show the active model\n" +
        "    /permissions     Show or set approval mode (ask|auto|full)\n" +
        "    /memory          list | read <name> | write <name> <text> | delete <name>\n" +
        "    /sessions        Recent Core sessions\n" +
        "    /new             How to start a new session\n" +
        "    /about           About KL\n" +
        "    /exit            Quit\n" +
        "\n  Keys: Enter send · Esc interrupt/clear · Ctrl+C cancel/quit · Ctrl+L clear\n";

    private async Task ShowStatusAsync()
    {
        var approval = await ReadApprovalModeAsync().ConfigureAwait(true);
        Append("\n" +
               $"  session:     {_session.SessionId}\n" +
               $"  model:       {_session.Provider}/{_session.Model}\n" +
               $"  workspace:   {_session.Workspace}\n" +
               $"  approval:    {DescribeApproval(approval)}\n" +
               $"  tokens:      input {_totalInputTokens}, output {_totalOutputTokens}\n" +
               $"  cost:        ${_totalCostUsd:0.000000}\n");
    }

    private async Task PermissionsAsync(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var mode = await ReadApprovalModeAsync().ConfigureAwait(true);
            Append($"\n  approval: {DescribeApproval(mode)} " +
                   "(/permissions ask|auto|full)\n");
            return;
        }
        var code = argument.Trim().ToLowerInvariant() switch
        {
            "ask" => "ask",
            "auto" => "auto",
            "full" or "dontask" => "dontAsk",
            _ => null
        };
        if (code is null)
        {
            Append("\n  usage: /permissions ask|auto|full\n");
            return;
        }
        try
        {
            await _session.RequestAsync(
                Protocol.PermissionsSet,
                new { approval_mode = code },
                _lifetime.Token).ConfigureAwait(true);
            Append($"\n  approval: {DescribeApproval(code)}\n");
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            Append($"\n  ✗ {error.Message}\n");
        }
    }

    private async Task<string> ReadApprovalModeAsync()
    {
        try
        {
            var result = await _session
                .RequestAsync(Protocol.PermissionsGet, null, _lifetime.Token)
                .ConfigureAwait(true);
            return result.TryGetProperty("approval_mode", out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            return error.Message;
        }
    }

    private static string DescribeApproval(string mode) => mode switch
    {
        "dontAsk" => "full access",
        "auto" or "acceptEdits" => "approve for me",
        "ask" or "plan" or "read-only" => "ask every time",
        _ => string.IsNullOrEmpty(mode) ? "unknown" : mode
    };

    private async Task ListSessionsAsync()
    {
        try
        {
            var result = await _session.ListSessionsAsync(_lifetime.Token).ConfigureAwait(true);
            if (!result.TryGetProperty("sessions", out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                Append("\n  no sessions\n");
                return;
            }
            var builder = new StringBuilder("\n  Recent sessions\n");
            foreach (var value in values.EnumerateArray().Take(15))
            {
                var id = GetString(value, "session_id");
                var updated = GetString(value, "updated_at");
                var title = GetString(value, "task");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = GetString(value, "last_message");
                }
                builder.AppendLine(
                    $"    {id}  {(updated.Length >= 16 ? updated[..16] : updated)}  " +
                    $"{Shorten(title, 60)}");
            }
            Append(builder.ToString());
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            Append($"\n  ✗ {error.Message}\n");
        }
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
                        .ConfigureAwait(true);
                    var entries = new List<string>();
                    if (result.TryGetProperty("entries", out var list)
                        && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in list.EnumerateArray())
                        {
                            entries.Add($"{GetString(entry, "name")} ({GetString(entry, "scope")})");
                        }
                    }
                    Append(entries.Count == 0
                        ? "\n  no memory entries\n"
                        : "\n  memory\n    " + string.Join("\n    ", entries) + "\n");
                    break;
                }
                case MemoryCommandAction.Read:
                    if (string.IsNullOrWhiteSpace(command.Name))
                    {
                        Append("\n  usage: /memory read <name>\n");
                        break;
                    }
                    var read = await _session
                        .ReadMemoryAsync(command.Name, command.Scope, _lifetime.Token)
                        .ConfigureAwait(true);
                    Append("\n" + GetString(read, "content") + "\n");
                    break;
                case MemoryCommandAction.Write:
                    if (string.IsNullOrWhiteSpace(command.Name))
                    {
                        Append("\n  usage: /memory write <name> <content>\n");
                        break;
                    }
                    await _session
                        .WriteMemoryAsync(command.Name, command.Content, command.Scope, _lifetime.Token)
                        .ConfigureAwait(true);
                    Append($"\n  wrote {command.Name}\n");
                    break;
                case MemoryCommandAction.Delete:
                    if (string.IsNullOrWhiteSpace(command.Name))
                    {
                        Append("\n  usage: /memory delete <name>\n");
                        break;
                    }
                    await _session
                        .DeleteMemoryAsync(command.Name, command.Scope, _lifetime.Token)
                        .ConfigureAwait(true);
                    Append($"\n  deleted {command.Name}\n");
                    break;
            }
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            Append($"\n  ✗ {error.Message}\n");
        }
    }

    private void ClearTranscript()
    {
        _transcript.Text = BuildBanner();
        _transcript.MoveEnd();
    }

    private void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _transcript.Text = string.IsNullOrEmpty(_transcript.Text)
            ? text
            : _transcript.Text + text;
        _transcript.MoveEnd();
        SetNeedsDraw();
    }

    private void AppendReasoning(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (!_reasoningOpen)
        {
            Append("\n  [thinking] ");
            _reasoningOpen = true;
        }
        Append(text);
    }

    private void SetFooter(string? state = null)
    {
        var status = string.IsNullOrWhiteSpace(state) ? "ready" : state;
        _footer.Text = $"  {status}  ·  {_session.Provider}/{_session.Model}  ·  " +
                       $"{_session.Workspace}  ·  tokens {_totalInputTokens}/{_totalOutputTokens}";
        SetNeedsDraw();
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Shorten(string? text, int limit)
    {
        var value = text?.ReplaceLineEndings(" ").Trim() ?? string.Empty;
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private void Quit()
    {
        Stop();
        _app.RequestStop(this);
    }
}
#pragma warning restore CS0618

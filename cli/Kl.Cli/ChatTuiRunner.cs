using System.Threading.Channels;

namespace Kl.Cli;

public static class ChatTuiRunner
{
    public static bool CanUseTui =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static async Task<int> RunAsync(
        CliOptions options,
        CancellationToken cancellationToken = default)
    {
        using var terminal = new TerminalSession();
        var cancelRequested = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref cancelRequested, 1);
        };
        Console.CancelKeyPress += cancelHandler;
        Connection? connection = null;
        try
        {
            ChatTuiState state;
            try
            {
                connection = await OpenSessionAsync(
                    options,
                    options.Session,
                    cancellationToken).ConfigureAwait(false);
                state = NewState(connection.Session, options.Verbose);
                await LoadTranscriptAsync(connection.Session, state, cancellationToken)
                    .ConfigureAwait(false);
                state.History = await LoadHistoryAsync(connection.Session, state)
                    .ConfigureAwait(false);
            }
            catch (JsonRpcException error) when (error.Code == -32005)
            {
                state = NewStateWithoutSession(options);
                state.AddError(
                    "No provider configured. Type /settings to configure a provider and model.");
            }

            state.Settings.SetLanguage(SettingsTuiRunner.LoadLanguage(options));

            var keys = Channel.CreateUnbounded<ConsoleKeyInfo>(
                new UnboundedChannelOptions { SingleReader = true });
            _ = Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        keys.Writer.TryWrite(Console.ReadKey(intercept: true));
                    }
                    catch (Exception error)
                        when (error is IOException or InvalidOperationException)
                    {
                        break;
                    }
                }
            });

            var exit = false;
            var dirty = true;
            var lastWidth = 0;
            var lastHeight = 0;
            while (!exit)
            {
                var changed = false;
                if (connection is not null)
                {
                    while (connection.Presenter.Events.TryRead(out var sessionEvent))
                    {
                        state.Apply(sessionEvent);
                        changed = true;
                    }
                }

                var width = Math.Max(20, Console.WindowWidth);
                var height = Math.Max(8, Console.WindowHeight);
                if (ChatTuiRenderer.NeedsRedraw(
                        changed,
                        dirty,
                        width,
                        height,
                        lastWidth,
                        lastHeight))
                {
                    RenderFrame(width, height, state);
                    dirty = false;
                    lastWidth = width;
                    lastHeight = height;
                }

                while (keys.Reader.TryRead(out var key))
                {
                    if (state.Screen == ChatScreen.Settings)
                    {
                        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
                        if (key.KeyChar == '\u0003'
                            || (key.Key == ConsoleKey.C && control))
                        {
                            if (connection is not null
                                && (state.IsTurnActive || state.HasPendingApproval))
                            {
                                await connection.Session.CancelAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                state.PendingApprovalId = null;
                                state.StatusMessage = "cancelling";
                            }
                            else
                            {
                                exit = true;
                            }
                            dirty = true;
                            continue;
                        }

                        var command = state.Settings.HandleKey(key);
                        switch (command.Kind)
                        {
                            case SettingsCommandKind.Close:
                                state.Screen = ChatScreen.Chat;
                                break;
                            case SettingsCommandKind.SaveProvider:
                                await SaveSettingsProviderAsync(
                                    options,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                connection = await TryOpenSessionAfterSettingsAsync(
                                    options,
                                    state,
                                    connection,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            case SettingsCommandKind.SaveProviderAndClose:
                                await SaveSettingsProviderAsync(
                                    options,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                if (state.Settings.Error is null)
                                {
                                    state.Screen = ChatScreen.Chat;
                                    connection = await TryOpenSessionAfterSettingsAsync(
                                        options,
                                        state,
                                        connection,
                                        cancellationToken).ConfigureAwait(false);
                                }
                                break;
                            case SettingsCommandKind.SetLanguage:
                                state.Settings.SetLanguage(
                                    command.Language ?? UiLanguage.English);
                                await SettingsTuiRunner.SaveLanguageAsync(
                                    options,
                                    state.Settings.Language,
                                    cancellationToken).ConfigureAwait(false);
                                state.Settings.Status = "language saved";
                                break;
                            case SettingsCommandKind.SetDefaultModel:
                                await SetSettingsDefaultAsync(
                                    options,
                                    state,
                                    command.Provider ?? string.Empty,
                                    command.Model ?? string.Empty,
                                    cancellationToken).ConfigureAwait(false);
                                connection = await TryOpenSessionAfterSettingsAsync(
                                    options,
                                    state,
                                    connection,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            case SettingsCommandKind.ToggleProvider:
                                await ToggleSettingsProviderAsync(
                                    options,
                                    state,
                                    command.Provider ?? string.Empty,
                                    command.Enabled == true,
                                    cancellationToken).ConfigureAwait(false);
                                connection = await TryOpenSessionAfterSettingsAsync(
                                    options,
                                    state,
                                    connection,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            case SettingsCommandKind.SetPluginEnabled:
                                await SetSettingsPluginEnabledAsync(
                                    options,
                                    state,
                                    command.Plugin ?? string.Empty,
                                    command.Enabled == true,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            case SettingsCommandKind.ReloadPlugin:
                                await ReloadSettingsPluginAsync(
                                    options,
                                    state,
                                    command.Plugin ?? string.Empty,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            case SettingsCommandKind.Reload:
                                await LoadSettingsAsync(
                                    options,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                        }
                        dirty = true;
                        continue;
                    }

                    var action = ChatKeyMapper.Map(key, state);
                    switch (action)
                    {
                        case ChatKeyAction.Insert:
                            state.Insert(key.KeyChar);
                            break;
                        case ChatKeyAction.Backspace:
                            state.Backspace();
                            break;
                        case ChatKeyAction.Delete:
                            state.Delete();
                            break;
                        case ChatKeyAction.CursorLeft:
                            state.MoveCursorLeft();
                            break;
                        case ChatKeyAction.CursorRight:
                            state.MoveCursorRight();
                            break;
                        case ChatKeyAction.CursorHome:
                            state.MoveCursorHome();
                            break;
                        case ChatKeyAction.CursorEnd:
                            state.MoveCursorEnd();
                            break;
                        case ChatKeyAction.Send:
                            var prompt = state.Input.Trim();
                            if (ChatCommand.IsSettings(prompt))
                            {
                                state.Input = string.Empty;
                                state.Cursor = 0;
                                state.Screen = ChatScreen.Settings;
                                state.Settings.BeginLoad();
                                RenderFrame(width, height, state);
                                await LoadSettingsAsync(
                                    options,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            }
                            if (connection is null)
                            {
                                state.AddError(
                                    "No provider configured. Type /settings to configure a provider and model.");
                                state.Input = string.Empty;
                                state.Cursor = 0;
                                break;
                            }
                            state.AddUserMessage(prompt);
                            _ = TrackTurnAsync(connection.Session, connection.Presenter, prompt);
                            break;
                        case ChatKeyAction.CancelOrExit:
                            if (connection is not null
                                && (state.IsTurnActive || state.HasPendingApproval))
                            {
                                await connection.Session.CancelAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                state.PendingApprovalId = null;
                                state.StatusMessage = "cancelling";
                            }
                            else
                            {
                                exit = true;
                            }
                            break;
                        case ChatKeyAction.OpenHistory:
                            if (connection is null)
                            {
                                state.StatusMessage = "configure provider first";
                                break;
                            }
                            state.History = await LoadHistoryAsync(
                                connection.Session,
                                state).ConfigureAwait(false);
                            state.HistorySelection = 0;
                            state.Screen = ChatScreen.History;
                            break;
                        case ChatKeyAction.HistoryUp:
                            if (state.History.Count > 0)
                            {
                                state.HistorySelection = Math.Max(
                                    0,
                                    state.HistorySelection - 1);
                            }
                            break;
                        case ChatKeyAction.HistoryDown:
                            if (state.History.Count > 0)
                            {
                                state.HistorySelection = Math.Min(
                                    state.History.Count - 1,
                                    state.HistorySelection + 1);
                            }
                            break;
                        case ChatKeyAction.HistoryOpen:
                            if (connection is null)
                            {
                                break;
                            }
                            if (!state.IsTurnActive && state.History.Count > 0)
                            {
                                var selected = state.History[
                                    Math.Clamp(
                                        state.HistorySelection,
                                        0,
                                        state.History.Count - 1)];
                                await connection.Session.DisposeAsync().ConfigureAwait(false);
                                connection = await OpenSessionAsync(
                                    options,
                                    selected.SessionId,
                                    cancellationToken).ConfigureAwait(false);
                                state = NewState(connection.Session, options.Verbose);
                                await LoadTranscriptAsync(
                                    connection.Session,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                state.History = await LoadHistoryAsync(
                                    connection.Session,
                                    state).ConfigureAwait(false);
                                if (connection.Session.MessageCount > 0)
                                {
                                    state.AddInfo(
                                        $"Resumed session {connection.Session.SessionId} " +
                                        $"({connection.Session.MessageCount} messages)");
                                }
                            }
                            break;
                        case ChatKeyAction.HistoryNew:
                            if (connection is null)
                            {
                                break;
                            }
                            if (!state.IsTurnActive)
                            {
                                await connection.Session.DisposeAsync().ConfigureAwait(false);
                                connection = await OpenSessionAsync(
                                    options,
                                    null,
                                    cancellationToken).ConfigureAwait(false);
                                state = NewState(connection.Session, options.Verbose);
                                await LoadTranscriptAsync(
                                    connection.Session,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                state.History = await LoadHistoryAsync(
                                    connection.Session,
                                    state).ConfigureAwait(false);
                                state.AddInfo($"New session {connection.Session.SessionId}");
                            }
                            break;
                        case ChatKeyAction.HistoryBack:
                            state.Screen = ChatScreen.Chat;
                            break;
                        case ChatKeyAction.ToggleShortcuts:
                            if (state.Screen == ChatScreen.Shortcuts)
                            {
                                state.Screen = state.ShortcutReturnScreen;
                            }
                            else
                            {
                                state.ShortcutReturnScreen = state.Screen;
                                state.Screen = ChatScreen.Shortcuts;
                            }
                            break;
                        case ChatKeyAction.ScrollUp:
                            state.ScrollTranscript(1, width, height);
                            break;
                        case ChatKeyAction.ScrollDown:
                            state.ScrollTranscript(-1, width, height);
                            break;
                        case ChatKeyAction.ApprovalAllow:
                            if (connection is not null)
                            {
                                ResolveApproval(connection.Presenter, state, allow: true);
                            }
                            break;
                        case ChatKeyAction.ApprovalDeny:
                            if (connection is not null)
                            {
                                ResolveApproval(connection.Presenter, state, allow: false);
                            }
                            break;
                    }
                    dirty = true;
                }

                if (Interlocked.Exchange(ref cancelRequested, 0) != 0)
                {
                    if (connection is not null
                        && (state.IsTurnActive || state.HasPendingApproval))
                    {
                        await connection.Session.CancelAsync(cancellationToken)
                            .ConfigureAwait(false);
                        state.PendingApprovalId = null;
                        state.StatusMessage = "cancelling";
                    }
                    else
                    {
                        exit = true;
                    }
                    dirty = true;
                }

                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (connection is not null)
            {
                await connection.Session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static ChatTuiState NewStateWithoutSession(CliOptions options) =>
        new(
            Path.GetFullPath(options.Workspace ?? Directory.GetCurrentDirectory()),
            string.Empty,
            string.Empty,
            string.Empty,
            options.Verbose);

    private static async Task<Connection?> TryOpenSessionAfterSettingsAsync(
        CliOptions options,
        ChatTuiState state,
        Connection? current,
        CancellationToken cancellationToken)
    {
        if (current is not null)
        {
            return current;
        }

        try
        {
            var connection = await OpenSessionAsync(options, null, cancellationToken)
                .ConfigureAwait(false);
            state.SessionId = connection.Session.SessionId;
            state.Provider = connection.Session.Provider;
            state.Model = connection.Session.Model;
            state.AddInfo($"Connected to {state.Provider}/{state.Model}");
            return connection;
        }
        catch (JsonRpcException error)
        {
            state.StatusMessage = "provider not ready: " + error.Message;
            return null;
        }
    }
    private static async Task LoadSettingsAsync(
        CliOptions options,
        ChatTuiState state,
        CancellationToken cancellationToken)
    {
        state.Settings.BeginLoad();
        try
        {
            var snapshot = await SettingsTuiRunner.LoadAsync(options, cancellationToken)
                .ConfigureAwait(false);
            state.Settings.Load(snapshot);
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.FailLoad("settings unavailable: " + error.Message);
        }
    }

    private static async Task SaveSettingsProviderAsync(
        CliOptions options,
        ChatTuiState state,
        CancellationToken cancellationToken)
    {
        if (state.Settings.Draft is not { } draft)
        {
            return;
        }
        var validation = draft.Validate();
        if (validation is not null)
        {
            state.Settings.Error = validation;
            return;
        }

        state.Settings.Error = null;
        state.Settings.Status = "saving provider...";
        try
        {
            var provider = draft.EffectiveId;
            await SettingsTuiRunner.SaveProviderAsync(options, draft, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await SettingsTuiRunner.LoadAsync(options, cancellationToken)
                .ConfigureAwait(false);
            state.Settings.Load(snapshot, provider);
            state.Settings.Status = "provider saved";
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = ProviderDraft.Redact(error.Message, draft.ApiKey.Value);
        }
    }

    private static async Task ToggleSettingsProviderAsync(
        CliOptions options,
        ChatTuiState state,
        string provider,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return;
        }
        state.Settings.Error = null;
        state.Settings.Status = enabled ? "enabling provider..." : "disabling provider...";
        try
        {
            await SettingsTuiRunner.SetProviderEnabledAsync(
                    options,
                    provider,
                    enabled,
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await SettingsTuiRunner.LoadAsync(options, cancellationToken)
                .ConfigureAwait(false);
            state.Settings.Load(snapshot, provider);
            state.Settings.Status = enabled
                ? "provider enabled"
                : "provider disabled";
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = error.Message;
        }
    }

    private static async Task SetSettingsPluginEnabledAsync(
        CliOptions options,
        ChatTuiState state,
        string plugin,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plugin))
        {
            return;
        }
        state.Settings.Error = null;
        state.Settings.Status = enabled ? "enabling plugin..." : "disabling plugin...";
        try
        {
            await SettingsTuiRunner.SetPluginEnabledAsync(
                    options,
                    plugin,
                    enabled,
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await SettingsTuiRunner.LoadAsync(options, cancellationToken)
                .ConfigureAwait(false);
            state.Settings.LoadPlugins(snapshot, plugin);
            state.Settings.Status = enabled ? "plugin enabled" : "plugin disabled";
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = "plugin action failed: " + error.Message;
        }
    }

    private static async Task ReloadSettingsPluginAsync(
        CliOptions options,
        ChatTuiState state,
        string plugin,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plugin))
        {
            return;
        }
        state.Settings.Error = null;
        state.Settings.Status = "reloading plugin...";
        try
        {
            await SettingsTuiRunner.ReloadPluginAsync(
                    options,
                    plugin,
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await SettingsTuiRunner.LoadAsync(options, cancellationToken)
                .ConfigureAwait(false);
            state.Settings.LoadPlugins(snapshot, plugin);
            state.Settings.Status = "plugin reloaded";
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = "plugin reload failed: " + error.Message;
        }
    }

    private static async Task SetSettingsDefaultAsync(
        CliOptions options,
        ChatTuiState state,
        string provider,
        string model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return;
        }
        state.Settings.Error = null;
        state.Settings.Status = "saving default...";
        try
        {
            await SettingsTuiRunner.SetDefaultModelAsync(
                    options,
                    provider,
                    model,
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await SettingsTuiRunner.LoadAsync(options, cancellationToken)
                .ConfigureAwait(false);
            state.Settings.Load(snapshot, provider);
            state.Settings.Status = $"default: {provider}/{model}";
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = error.Message;
        }
    }

    private static bool IsSettingsError(Exception error) =>
        error is JsonRpcException or IOException or InvalidOperationException;

    private static async Task<Connection> OpenSessionAsync(
        CliOptions options,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var sessionOptions = CloneWithSession(options, sessionId);
        var presenter = new TuiSessionPresenter(options.AutoApprove);
        var session = await KlSessionClient.StartAsync(
            sessionOptions,
            presenter,
            cancellationToken).ConfigureAwait(false);
        return new Connection(session, presenter);
    }

    private static ChatTuiState NewState(KlSessionClient session, bool verbose) =>
        new(
            session.Workspace,
            session.Provider,
            session.Model,
            session.SessionId,
            verbose);

    private static async Task<List<HistorySession>> LoadHistoryAsync(
        KlSessionClient session,
        ChatTuiState state)
    {
        try
        {
            var result = await session.ListSessionsAsync().ConfigureAwait(false);
            return ChatTuiState.ParseSessions(result);
        }
        catch (Exception error)
            when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            state.AddError($"history unavailable: {error.Message}");
            return [];
        }
    }

    private static async Task LoadTranscriptAsync(
        KlSessionClient session,
        ChatTuiState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await session.HistoryAsync(20, cancellationToken)
                .ConfigureAwait(false);
            state.AddHistory(ChatTuiState.ParseHistory(result));
        }
        catch (Exception error)
            when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            state.AddError($"history unavailable: {error.Message}");
        }
    }

    private static async Task TrackTurnAsync(
        KlSessionClient session,
        TuiSessionPresenter presenter,
        string prompt)
    {
        try
        {
            await session.RunTurnAsync(prompt).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            presenter.Publish(new SessionEvent(
                SessionEventKind.Error,
                Text: error.Message));
            presenter.Publish(new SessionEvent(
                SessionEventKind.TurnFailed,
                Text: error.Message));
        }
    }

    private static void ResolveApproval(
        TuiSessionPresenter presenter,
        ChatTuiState state,
        bool allow)
    {
        if (state.PendingApprovalId is not { } approvalId)
        {
            return;
        }

        presenter.ResolveApproval(approvalId, allow);
        state.PendingApprovalId = null;
        state.StatusMessage = allow ? "approval sent: allow" : "approval sent: deny";
    }

    private static void RenderFrame(int width, int height, ChatTuiState state)
    {
        var frame = ChatTuiRenderer.Render(width, height, state)
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        Console.Out.Write("\u001b[H" + frame + "\u001b[0J");
        Console.Out.Flush();
    }

    private static CliOptions CloneWithSession(CliOptions source, string? sessionId)
    {
        var copy = new CliOptions
        {
            Command = source.Command,
            CorePath = source.CorePath,
            ConfigPath = source.ConfigPath,
            HomePath = source.HomePath,
            Workspace = source.Workspace,
            Provider = source.Provider,
            Model = source.Model,
            Session = sessionId,
            AutoApprove = source.AutoApprove,
            Verbose = source.Verbose,
            Help = source.Help,
            Version = source.Version
        };
        foreach (var positional in source.Positionals)
        {
            copy.Positionals.Add(positional);
        }
        return copy;
    }

    private sealed record Connection(
        KlSessionClient Session,
        TuiSessionPresenter Presenter);

    private sealed class TerminalSession : IDisposable
    {
        private readonly bool _previousTreatControlCAsInput;

        public TerminalSession()
        {
            try
            {
                _previousTreatControlCAsInput = Console.TreatControlCAsInput;
                Console.TreatControlCAsInput = true;
            }
            catch (Exception error)
                when (error is IOException or InvalidOperationException)
            {
                _previousTreatControlCAsInput = false;
            }

            Console.Out.Write("\u001b[?1049h\u001b[?25l\u001b[2J\u001b[H");
            Console.Out.Flush();
        }

        public void Dispose()
        {
            try
            {
                Console.Out.Write("\u001b[?25h\u001b[?1049l");
                Console.Out.Flush();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                // The terminal may already be gone.
            }

            try
            {
                Console.TreatControlCAsInput = _previousTreatControlCAsInput;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                // Best effort during shutdown.
            }

            Console.ResetColor();
        }
    }
}

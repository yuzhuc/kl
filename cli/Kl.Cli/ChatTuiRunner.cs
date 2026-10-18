using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kl.Cli;

public enum CtrlCAction
{
    Cancel,
    CopySelection,
    Exit
}

public static class ChatTuiRunner
{
    public static bool CanUseTui =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>Slash input is always a command, never a prompt.</summary>
    public static bool IsPrompt(string? input) =>
        !string.IsNullOrWhiteSpace(input)
        && !input.TrimStart().StartsWith('/');

    public static CtrlCAction ResolveCtrlC(ChatTuiState state, bool hasConnection) =>
        hasConnection && (state.IsTurnActive || state.HasPendingApproval)
            ? CtrlCAction.Cancel
            : state.HasSelection
                ? CtrlCAction.CopySelection
                : CtrlCAction.Exit;

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
            await SplashScreen.ShowAsync(CanUseTui, cancellationToken).ConfigureAwait(false);
            var language = SettingsTuiRunner.LoadLanguage(options);
            ChatTuiState state;
            try
            {
                connection = await OpenSessionAsync(
                    options,
                    options.Session,
                    cancellationToken).ConfigureAwait(false);
                state = NewState(connection.Session, options.Verbose, language);
                await LoadTranscriptAsync(connection.Session, state, cancellationToken)
                    .ConfigureAwait(false);
                state.History = await LoadHistoryAsync(connection.Session, state)
                    .ConfigureAwait(false);
            }
            catch (JsonRpcException error) when (error.Code == -32005)
            {
                state = NewStateWithoutSession(options, language);
            }

            state.Settings.SetLanguage(language);

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
            var forceClear = false;
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
                if (forceClear)
                {
                    Console.Out.Write("\u001b[0m\u001b[2J\u001b[H");
                    Console.Out.Flush();
                    forceClear = false;
                }
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
                        if (control && key.Key == ConsoleKey.D)
                        {
                            exit = true;
                            dirty = true;
                            continue;
                        }
                        if (key.KeyChar == '\u0003'
                            || (key.Key == ConsoleKey.C && control))
                        {
                            if (connection is not null
                                && (state.IsTurnActive || state.HasPendingApproval))
                            {
                                await connection.Session.CancelAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                state.PendingApprovalId = null;
                                state.StatusMessage = ChatTexts.Cancelling(state.Language);
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
                                state.Settings.Status = ChatTexts.T(
                                    state.Language,
                                    "language saved",
                                    "语言已保存");
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
                        case ChatKeyAction.CommandPaletteUp:
                            ChatCommandPalette.MoveSelection(state, -1);
                            break;
                        case ChatKeyAction.CommandPaletteDown:
                            ChatCommandPalette.MoveSelection(state, 1);
                            break;
                        case ChatKeyAction.CommandPaletteClose:
                            state.DismissCommandPalette();
                            break;
                        case ChatKeyAction.CommandPaletteAccept:
                            var selectedCommand = ChatCommandPalette.Selected(state);
                            if (selectedCommand is null)
                            {
                                state.StatusMessage = ChatTexts.NoMatchingCommand(
                                    state.Language);
                                break;
                            }
                            var paletteResult = await ExecuteCommandAsync(
                                options,
                                state,
                                connection,
                                selectedCommand.Kind,
                                cancellationToken).ConfigureAwait(false);
                            connection = paletteResult.Connection;
                            state = paletteResult.State;
                            exit = paletteResult.Exit;
                            break;
                        case ChatKeyAction.Send:
                            var prompt = state.Input.Trim();
                            if (!IsPrompt(prompt))
                            {
                                var command = ChatCommandPalette.FindExact(prompt);
                                if (command is null)
                                {
                                    state.AddError(ChatTexts.UnknownCommand(
                                        state.Language,
                                        prompt));
                                    state.ClearInput();
                                    break;
                                }
                                var commandResult = await ExecuteCommandAsync(
                                    options,
                                    state,
                                    connection,
                                    command.Kind,
                                    cancellationToken).ConfigureAwait(false);
                                connection = commandResult.Connection;
                                state = commandResult.State;
                                exit = commandResult.Exit;
                                break;
                            }
                            if (connection is null)
                            {
                                state.StatusMessage = ChatTexts.NoProviderStatus(
                                    state.Language);
                                state.ClearInput();
                                break;
                            }
                            state.AddUserMessage(prompt);
                            _ = TrackTurnAsync(
                                connection.Session,
                                connection.Presenter,
                                prompt);
                            break;
                        case ChatKeyAction.CancelOrExit:
                            switch (ResolveCtrlC(state, connection is not null))
                            {
                                case CtrlCAction.Cancel:
                                    await connection!.Session.CancelAsync(cancellationToken)
                                        .ConfigureAwait(false);
                                    state.PendingApprovalId = null;
                                    state.StatusMessage = ChatTexts.Cancelling(
                                        state.Language);
                                    break;
                                case CtrlCAction.CopySelection:
                                    CopySelection(state, width);
                                    break;
                                case CtrlCAction.Exit:
                                    exit = true;
                                    break;
                            }
                            break;
                        case ChatKeyAction.OpenHistory:
                            if (connection is null)
                            {
                                state.StatusMessage = ChatTexts.NoProviderStatus(
                                    state.Language);
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
                                var currentLanguage = state.Language;
                                await connection.Session.DisposeAsync()
                                    .ConfigureAwait(false);
                                connection = await OpenSessionAsync(
                                    options,
                                    selected.SessionId,
                                    cancellationToken).ConfigureAwait(false);
                                state = NewState(
                                    connection.Session,
                                    options.Verbose,
                                    currentLanguage);
                                await LoadTranscriptAsync(
                                    connection.Session,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                state.History = await LoadHistoryAsync(
                                    connection.Session,
                                    state).ConfigureAwait(false);
                                if (connection.Session.MessageCount > 0)
                                {
                                    state.AddInfo(ChatTexts.ResumedSession(
                                        state.Language,
                                        connection.Session.SessionId,
                                        connection.Session.MessageCount));
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
                                var newSessionLanguage = state.Language;
                                await connection.Session.DisposeAsync()
                                    .ConfigureAwait(false);
                                connection = await OpenSessionAsync(
                                    options,
                                    null,
                                    cancellationToken).ConfigureAwait(false);
                                state = NewState(
                                    connection.Session,
                                    options.Verbose,
                                    newSessionLanguage);
                                await LoadTranscriptAsync(
                                    connection.Session,
                                    state,
                                    cancellationToken).ConfigureAwait(false);
                                state.History = await LoadHistoryAsync(
                                    connection.Session,
                                    state).ConfigureAwait(false);
                                state.AddInfo(ChatTexts.NewSession(
                                    state.Language,
                                    connection.Session.SessionId));
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
                            ResolveApproval(
                                connection?.Presenter,
                                state,
                                allow: true);
                            break;
                        case ChatKeyAction.ApprovalDeny:
                            ResolveApproval(
                                connection?.Presenter,
                                state,
                                allow: false);
                            break;
                        case ChatKeyAction.SelectUp:
                            state.ExtendSelection(
                                -1,
                                ChatTuiRenderer.TranscriptLines(state, width).Count);
                            break;
                        case ChatKeyAction.SelectDown:
                            state.ExtendSelection(
                                1,
                                ChatTuiRenderer.TranscriptLines(state, width).Count);
                            break;
                        case ChatKeyAction.CopyLastAssistant:
                            CopyLastAssistant(state);
                            break;
                        case ChatKeyAction.ClearScreen:
                            forceClear = true;
                            dirty = true;
                            break;
                        case ChatKeyAction.Exit:
                            exit = true;
                            break;
                        case ChatKeyAction.ToggleReasoning:
                            state.ToggleReasoning();
                            break;
                        case ChatKeyAction.ModelPickerUp:
                            state.ModelPicker.Move(-1);
                            break;
                        case ChatKeyAction.ModelPickerDown:
                            state.ModelPicker.Move(1);
                            break;
                        case ChatKeyAction.ModelPickerAccept:
                            await SelectModelAsync(
                                options,
                                state,
                                cancellationToken).ConfigureAwait(false);
                            break;
                        case ChatKeyAction.ModelPickerClose:
                            state.ModelPicker.Close();
                            break;
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

    private static async Task<CommandResult> ExecuteCommandAsync(
        CliOptions options,
        ChatTuiState state,
        Connection? connection,
        ChatCommandKind command,
        CancellationToken cancellationToken)
    {
        var language = state.Language;
        state.ClearInput();
        switch (command)
        {
            case ChatCommandKind.Init:
                await RunInitAsync(state, connection, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ChatCommandKind.Status:
                await RunStatusAsync(state, connection, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ChatCommandKind.Permissions:
                state.Screen = ChatScreen.Settings;
                await LoadSettingsAsync(options, state, cancellationToken)
                    .ConfigureAwait(false);
                _ = state.Settings.HandleKey(new ConsoleKeyInfo(
                    '\0',
                    ConsoleKey.DownArrow,
                    shift: false,
                    alt: false,
                    control: false));
                state.Settings.Status = ChatTexts.PermissionsHint(language);
                break;
            case ChatCommandKind.Model:
                await OpenModelPickerAsync(
                    options,
                    state,
                    connection,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ChatCommandKind.Review:
                await RunReviewAsync(
                    options,
                    state,
                    connection,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ChatCommandKind.Compact:
                await RunCompactAsync(state, connection, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ChatCommandKind.Clear:
                (connection, state) = await ClearAsync(
                    options,
                    state,
                    connection,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ChatCommandKind.Settings:
                state.Screen = ChatScreen.Settings;
                await LoadSettingsAsync(options, state, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ChatCommandKind.Resume:
                if (connection is null)
                {
                    state.StatusMessage = ChatTexts.HistoryUnavailable(
                        language,
                        ChatTexts.NoProviderStatus(language));
                    break;
                }
                state.History = await LoadHistoryAsync(connection.Session, state)
                    .ConfigureAwait(false);
                state.HistorySelection = 0;
                state.Screen = ChatScreen.History;
                break;
            case ChatCommandKind.New:
                (connection, state) = await NewSessionAsync(
                    options,
                    state,
                    connection,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ChatCommandKind.Help:
                state.ShortcutReturnScreen = ChatScreen.Chat;
                state.Screen = ChatScreen.Shortcuts;
                break;
            case ChatCommandKind.Copy:
                CopyLastAssistant(state);
                break;
            case ChatCommandKind.Exit:
                return new CommandResult(connection, state, Exit: true);
        }

        return new CommandResult(connection, state, Exit: false);
    }

    private static async Task RunInitAsync(
        ChatTuiState state,
        Connection? connection,
        CancellationToken cancellationToken)
    {
        var language = state.Language;
        if (connection is null)
        {
            state.AddError(ChatTexts.InitUnavailable(language, "no active session"));
            return;
        }

        try
        {
            var result = await RequestCoreAsync(
                    connection.Session,
                    "workspace.init",
                    new Dictionary<string, object?> { ["workspace"] = state.Workspace },
                    cancellationToken)
                .ConfigureAwait(false);
            var path = GetString(result, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(state.Workspace, "AGENTS.md");
            }
            state.AddInfo(ChatTexts.InitCreated(language, path));
        }
        catch (Exception error) when (IsCommandError(error))
        {
            state.AddError(ChatTexts.InitUnavailable(language, error.Message));
        }
    }

    private static async Task RunStatusAsync(
        ChatTuiState state,
        Connection? connection,
        CancellationToken cancellationToken)
    {
        var language = state.Language;
        string? rpcError = null;
        if (connection is not null)
        {
            try
            {
                var result = await RequestCoreAsync(
                        connection.Session,
                        "session.status",
                        new Dictionary<string, object?>
                        {
                            ["session_id"] = state.SessionId
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                AddStatusLines(state, result);
                return;
            }
            catch (Exception error) when (IsCommandError(error))
            {
                rpcError = error.Message;
            }
        }

        state.AddInfo(ChatTexts.StatusFallback(
            language,
            rpcError ?? "no active session"));
        state.AddInfo(
            ChatTexts.ProviderLabel(language) + ": " + state.Provider);
        state.AddInfo(ChatTexts.ModelLabel(language) + ": " + state.Model);
        state.AddInfo(ChatTexts.SessionLabel(language) + ": " + state.SessionId);
        state.AddInfo(ChatTexts.WorkspaceLabel(language) + ": " + state.Workspace);
    }

    private static void AddStatusLines(ChatTuiState state, JsonElement result)
    {
        var language = state.Language;
        var provider = GetString(result, "provider");
        var model = GetString(result, "model");
        var session = GetString(result, "session_id");
        var workspace = GetString(result, "workspace");
        state.AddInfo(ChatTexts.ProviderLabel(language) + ": " +
            (string.IsNullOrWhiteSpace(provider) ? state.Provider : provider));
        state.AddInfo(ChatTexts.ModelLabel(language) + ": " +
            (string.IsNullOrWhiteSpace(model) ? state.Model : model));
        state.AddInfo(ChatTexts.SessionLabel(language) + ": " +
            (string.IsNullOrWhiteSpace(session) ? state.SessionId : session));
        state.AddInfo(ChatTexts.WorkspaceLabel(language) + ": " +
            (string.IsNullOrWhiteSpace(workspace) ? state.Workspace : workspace));
        if (result.TryGetProperty("message_count", out var count)
            && count.TryGetInt32(out var messageCount))
        {
            state.AddInfo(ChatTexts.Messages(language, messageCount));
        }
    }

    private static async Task OpenModelPickerAsync(
        CliOptions options,
        ChatTuiState state,
        Connection? connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = connection is not null
                ? await RequestCoreAsync(
                    connection.Session,
                    Protocol.ProviderList,
                    null,
                    cancellationToken).ConfigureAwait(false)
                : await RequestStandaloneAsync(
                    options,
                    Protocol.ProviderList,
                    null,
                    cancellationToken).ConfigureAwait(false);
            var models = ChatModelPicker.Parse(result);
            if (models.Count == 0)
            {
                state.AddInfo(ChatTexts.NoModels(state.Language));
                return;
            }
            state.ModelPicker.Open(models, state.Provider, state.Model);
        }
        catch (Exception error) when (IsCommandError(error))
        {
            state.AddError(ChatTexts.T(
                state.Language,
                "provider.list unavailable: " + error.Message,
                "provider.list 不可用：" + error.Message));
        }
    }

    private static async Task SelectModelAsync(
        CliOptions options,
        ChatTuiState state,
        CancellationToken cancellationToken)
    {
        var selected = state.ModelPicker.Selected;
        if (selected is null)
        {
            state.ModelPicker.Close();
            return;
        }

        state.ModelPicker.Close();
        try
        {
            await using var client = KlCoreClient.Start(options);
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            await client.SetDefaultModelAsync(
                    selected.Provider,
                    selected.Model,
                    cancellationToken)
                .ConfigureAwait(false);
            state.Provider = selected.Provider;
            state.Model = selected.Model;
            options.Provider = selected.Provider;
            options.Model = selected.Model;
            state.StatusMessage = ChatTexts.ModelDefaultSet(
                state.Language,
                selected.Provider,
                selected.Model);
        }
        catch (Exception error) when (IsCommandError(error))
        {
            state.StatusMessage = ChatTexts.T(
                state.Language,
                "set default model failed: " + error.Message,
                "设置默认模型失败：" + error.Message);
        }
    }

    private static async Task RunReviewAsync(
        CliOptions options,
        ChatTuiState state,
        Connection? connection,
        CancellationToken cancellationToken)
    {
        var language = state.Language;
        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["workspace"] = state.Workspace,
                ["strict"] = false
            };
            var result = connection is not null
                ? await RequestCoreAsync(
                    connection.Session,
                    Protocol.ReviewRun,
                    parameters,
                    cancellationToken).ConfigureAwait(false)
                : await RequestStandaloneAsync(
                    options,
                    Protocol.ReviewRun,
                    parameters,
                    cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("findings", out var findings)
                || findings.ValueKind != JsonValueKind.Array)
            {
                state.AddInfo(ChatTexts.ReviewFindings(language, 0));
                return;
            }

            var count = findings.GetArrayLength();
            state.AddInfo(ChatTexts.ReviewFindings(language, count));
            var shown = 0;
            foreach (var finding in findings.EnumerateArray())
            {
                if (shown >= 3)
                {
                    break;
                }
                var severity = GetString(finding, "severity");
                var file = GetString(finding, "file");
                var line = finding.TryGetProperty("line", out var lineValue)
                    && lineValue.TryGetInt32(out var parsedLine)
                    ? parsedLine
                    : 0;
                var rule = GetString(finding, "rule");
                var location = string.IsNullOrWhiteSpace(file)
                    ? "(workspace)"
                    : (line > 0 ? $"{file}:{line}" : file);
                state.AddInfo(ChatTexts.ReviewFindingLine(
                    language,
                    string.IsNullOrWhiteSpace(severity) ? "info" : severity,
                    location,
                    rule));
                shown++;
            }
            if (count > shown)
            {
                state.AddInfo(ChatTexts.ReviewMore(language, count - shown));
            }
        }
        catch (Exception error) when (IsCommandError(error))
        {
            state.AddError(ChatTexts.T(
                language,
                "review unavailable: " + error.Message,
                "审查不可用：" + error.Message));
        }
    }

    private static async Task RunCompactAsync(
        ChatTuiState state,
        Connection? connection,
        CancellationToken cancellationToken)
    {
        var language = state.Language;
        if (connection is null)
        {
            state.AddError(ChatTexts.CompactUnavailable(language, "no active session"));
            return;
        }

        try
        {
            var result = await RequestCoreAsync(
                    connection.Session,
                    "session.compact",
                    new Dictionary<string, object?>
                    {
                        ["session_id"] = state.SessionId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var omitted = GetInt(result, "omitted_messages")
                ?? GetInt(result, "omitted")
                ?? 0;
            long? tokens = GetLong(result, "estimated_tokens")
                ?? GetLong(result, "tokens");
            state.AddInfo(ChatTexts.CompactResult(language, omitted, tokens));
        }
        catch (Exception error) when (IsCommandError(error))
        {
            state.AddError(ChatTexts.CompactUnavailable(language, error.Message));
        }
    }

    private static async Task<(Connection?, ChatTuiState)> ClearAsync(
        CliOptions options,
        ChatTuiState state,
        Connection? connection,
        CancellationToken cancellationToken)
    {
        var language = state.Language;
        if (connection is not null)
        {
            try
            {
                if (state.IsTurnActive)
                {
                    await connection.Session.CancelAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                await connection.Session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (IsCommandError(error))
            {
                // The clear still proceeds.
                _ = error;
            }
            connection = null;
        }

        state.ClearTranscript();
        state.SessionId = string.Empty;
        state.StatusMessage = null;

        try
        {
            connection = await OpenSessionAsync(
                options,
                null,
                cancellationToken).ConfigureAwait(false);
            state = NewState(connection.Session, options.Verbose, language);
            await LoadTranscriptAsync(
                connection.Session,
                state,
                cancellationToken).ConfigureAwait(false);
            state.History = await LoadHistoryAsync(connection.Session, state)
                .ConfigureAwait(false);
            state.AddInfo(ChatTexts.NewSession(
                state.Language,
                connection.Session.SessionId));
        }
        catch (Exception error) when (IsCommandError(error))
        {
            state.AddError(ChatTexts.NewSessionUnavailable(
                language,
                error.Message));
        }
        return (connection, state);
    }

    private static async Task<(Connection?, ChatTuiState)> NewSessionAsync(
        CliOptions options,
        ChatTuiState state,
        Connection? connection,
        CancellationToken cancellationToken)
    {
        var language = state.Language;
        if (connection is null)
        {
            state.StatusMessage = ChatTexts.NewSessionUnavailable(
                language,
                ChatTexts.NoProviderStatus(language));
            return (connection, state);
        }
        if (state.IsTurnActive)
        {
            state.StatusMessage = ChatTexts.T(
                language,
                "cannot start a new session while a turn is active",
                "turn 进行中，无法新建会话");
            return (connection, state);
        }

        await connection.Session.DisposeAsync().ConfigureAwait(false);
        connection = null;
        try
        {
            connection = await OpenSessionAsync(
                options,
                null,
                cancellationToken).ConfigureAwait(false);
            state = NewState(connection.Session, options.Verbose, language);
            await LoadTranscriptAsync(
                connection.Session,
                state,
                cancellationToken).ConfigureAwait(false);
            state.History = await LoadHistoryAsync(connection.Session, state)
                .ConfigureAwait(false);
            state.AddInfo(ChatTexts.NewSession(
                state.Language,
                connection.Session.SessionId));
        }
        catch (Exception error) when (IsCommandError(error))
        {
            state = NewStateWithoutSession(options, language);
            state.AddError(ChatTexts.NewSessionUnavailable(
                language,
                error.Message));
            state.StatusMessage = ChatTexts.T(
                language,
                "new session unavailable",
                "无法新建会话");
        }
        return (connection, state);
    }

    private static void CopyLastAssistant(ChatTuiState state)
    {
        var text = state.LastAssistantText();
        if (string.IsNullOrWhiteSpace(text))
        {
            state.StatusMessage = ChatTexts.NoAssistantToCopy(state.Language);
            return;
        }
        CopyText(state, text);
    }

    private static void CopySelection(ChatTuiState state, int width)
    {
        var text = state.SelectedText(width);
        if (string.IsNullOrWhiteSpace(text))
        {
            state.StatusMessage = ChatTexts.NoSelectionToCopy(state.Language);
        }
        else
        {
            CopyText(state, text);
        }
        state.ClearSelection();
    }

    private static void CopyText(ChatTuiState state, string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Length;
        state.StatusMessage = ClipboardService.TryCopy(text, out var error)
            ? ChatTexts.Copied(state.Language, lines)
            : ChatTexts.CopyFailed(state.Language, error);
    }

    private static ChatTuiState NewState(
        KlSessionClient session,
        bool verbose,
        UiLanguage language)
    {
        var state = new ChatTuiState(
            session.Workspace,
            session.Provider,
            session.Model,
            session.SessionId,
            verbose);
        state.Settings.SetLanguage(language);
        return state;
    }

    private static ChatTuiState NewStateWithoutSession(
        CliOptions options,
        UiLanguage language)
    {
        var state = new ChatTuiState(
            Path.GetFullPath(options.Workspace ?? Directory.GetCurrentDirectory()),
            string.Empty,
            string.Empty,
            string.Empty,
            options.Verbose);
        state.Settings.SetLanguage(language);
        return state;
    }

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
            state.AddInfo(ChatTexts.T(
                state.Language,
                $"Connected to {state.Provider}/{state.Model}",
                $"已连接 {state.Provider}/{state.Model}"));
            return connection;
        }
        catch (JsonRpcException error)
        {
            state.StatusMessage = ChatTexts.T(
                state.Language,
                "provider not ready: " + error.Message,
                "Provider 未就绪：" + error.Message);
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
            state.Settings.FailLoad(ChatTexts.T(
                state.Language,
                "settings unavailable: " + error.Message,
                "设置不可用：" + error.Message));
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
        state.Settings.Status = ChatTexts.T(
            state.Language,
            "saving provider...",
            "正在保存 Provider…");
        try
        {
            var provider = draft.EffectiveId;
            await SettingsTuiRunner.SaveProviderAsync(options, draft, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await SettingsTuiRunner.LoadAsync(options, cancellationToken)
                .ConfigureAwait(false);
            state.Settings.Load(snapshot, provider);
            state.Settings.Status = ChatTexts.T(
                state.Language,
                "provider saved",
                "Provider 已保存");
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
        state.Settings.Status = enabled
            ? ChatTexts.T(state.Language, "enabling provider...", "正在启用 Provider…")
            : ChatTexts.T(state.Language, "disabling provider...", "正在禁用 Provider…");
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
                ? ChatTexts.T(state.Language, "provider enabled", "Provider 已启用")
                : ChatTexts.T(state.Language, "provider disabled", "Provider 已禁用");
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
        state.Settings.Status = enabled
            ? ChatTexts.T(state.Language, "enabling plugin...", "正在启用插件…")
            : ChatTexts.T(state.Language, "disabling plugin...", "正在禁用插件…");
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
            state.Settings.Status = enabled
                ? ChatTexts.T(state.Language, "plugin enabled", "插件已启用")
                : ChatTexts.T(state.Language, "plugin disabled", "插件已禁用");
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = ChatTexts.T(
                state.Language,
                "plugin action failed: " + error.Message,
                "插件操作失败：" + error.Message);
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
        state.Settings.Status = ChatTexts.T(
            state.Language,
            "reloading plugin...",
            "正在重载插件…");
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
            state.Settings.Status = ChatTexts.T(
                state.Language,
                "plugin reloaded",
                "插件已重载");
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = ChatTexts.T(
                state.Language,
                "plugin reload failed: " + error.Message,
                "插件重载失败：" + error.Message);
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
        state.Settings.Status = ChatTexts.T(
            state.Language,
            "saving default...",
            "正在保存默认模型…");
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
            state.Settings.Status = ChatTexts.ModelDefaultSet(
                state.Language,
                provider,
                model);
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            state.Settings.Error = error.Message;
        }
    }

    private static bool IsSettingsError(Exception error) =>
        error is JsonRpcException or IOException or InvalidOperationException;

    private static bool IsCommandError(Exception error) =>
        error is JsonRpcException or IOException or InvalidOperationException
            or TimeoutException;

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
            state.AddError(ChatTexts.HistoryUnavailable(
                state.Language,
                error.Message));
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
            state.AddError(ChatTexts.HistoryUnavailable(
                state.Language,
                error.Message));
        }
    }

    private static Task<JsonElement> RequestCoreAsync(
        KlSessionClient session,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var field = typeof(KlSessionClient).GetField(
            "_client",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(session) is not KlCoreClient client)
        {
            throw new InvalidOperationException("core client unavailable");
        }
        return client.RequestAsync(method, parameters, cancellationToken);
    }

    private static async Task<JsonElement> RequestStandaloneAsync(
        CliOptions options,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await using var client = KlCoreClient.Start(options);
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        return await client.RequestAsync(method, parameters, cancellationToken)
            .ConfigureAwait(false);
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
        TuiSessionPresenter? presenter,
        ChatTuiState state,
        bool allow)
    {
        if (state.PendingApprovalId is not { } approvalId)
        {
            return;
        }

        presenter?.ResolveApproval(approvalId, allow);
        state.PendingApprovalId = null;
        state.StatusMessage = ChatTexts.T(
            state.Language,
            allow ? "approval sent: allow" : "approval sent: deny",
            allow ? "已发送审批：允许" : "已发送审批：拒绝");
    }

    private static void RenderFrame(int width, int height, ChatTuiState state)
    {
        var frame = ChatTuiRenderer.Render(width, height, state)
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        Console.Out.Write("\u001b[H" + frame + "\u001b[0J");
        Console.Out.Flush();
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value)
            && value.TryGetInt64(out var parsed))
        {
            return parsed;
        }
        return null;
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

    private sealed record CommandResult(
        Connection? Connection,
        ChatTuiState State,
        bool Exit);

    private sealed class TerminalSession : IDisposable
    {
        private readonly bool _previousTreatControlCAsInput;

        public TerminalSession()
        {
            try
            {
                Console.InputEncoding = new UTF8Encoding(false);
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception error)
                when (error is IOException or InvalidOperationException)
            {
                // Keep the console defaults if the encoding cannot be changed.
            }

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

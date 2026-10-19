using System.Diagnostics;
using System.Text.Json;

namespace Kl.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CommandLine.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"kl: {error.Message}");
            return 64;
        }

        try
        {
            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (JsonRpcException error)
        {
            Console.Error.WriteLine($"kl: core error {error.Code}: {error.Message}");
            return 1;
        }
        catch (FileNotFoundException error)
        {
            Console.Error.WriteLine($"kl: {error.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("kl: cancelled");
            return 130;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"kl: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(CliOptions options)
    {
        var ui = new ConsoleUi();
        if (options.Command == "resume"
            && string.IsNullOrWhiteSpace(options.Session)
            && options.Positionals.Count > 0)
        {
            options.Session = options.Positionals[0];
        }

        switch (options.Command)
        {
            case "version":
                Console.WriteLine($"kl {Protocol.ClientVersion} (protocol {Protocol.Version})");
                return 0;
            case "help":
                PrintHelp();
                return 0;
            case "doctor":
                return await DoctorAsync(options, ui).ConfigureAwait(false);
            case "ping":
                return await PingAsync(options, ui).ConfigureAwait(false);
            case "run":
                return await RunTaskAsync(options, ui).ConfigureAwait(false);
            case "chat":
                return await ChatAsync(options, ui).ConfigureAwait(false);
            case "resume":
                return await ResumeAsync(options, ui).ConfigureAwait(false);
            case "provider":
                return await ProviderCli.ProviderAsync(options, ui).ConfigureAwait(false);
            case "auth":
                return await ProviderCli.AuthAsync(options, ui).ConfigureAwait(false);
            case "model":
                return await ProviderCli.ModelAsync(options, ui).ConfigureAwait(false);
            case "plugin":
                return await PluginAsync(options, ui).ConfigureAwait(false);
            case "review":
                return await ReviewAsync(options, ui).ConfigureAwait(false);
            case "audit":
                return await AuditAsync(options, ui).ConfigureAwait(false);
            case "config":
                return await ConfigAsync(options, ui).ConfigureAwait(false);
            default:
                ui.Error($"unknown command: {options.Command}");
                PrintHelp();
                return 64;
        }
    }

    private static async Task<int> DoctorAsync(CliOptions options, ConsoleUi ui)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        AttachDiagnostics(client, options, ui);
        var handshake = await client.HandshakeAsync().ConfigureAwait(false);
        var ping = await client.PingAsync("doctor").ConfigureAwait(false);
        var doctor = await client.DoctorAsync().ConfigureAwait(false);

        ui.Header("KL doctor");
        ui.Check("core", true, $"{handshake.ServerName} {handshake.ServerVersion}");
        ui.Check("protocol", true, $"v{handshake.ProtocolVersion}");
        ui.Check(
            "ping",
            ping.GetProperty("pong").GetBoolean(),
            $"{ping.GetProperty("server_time_unix_ms").GetInt64()}");

        foreach (var check in doctor.GetProperty("checks").EnumerateArray())
        {
            ui.Check(
                check.GetProperty("name").GetString() ?? "?",
                check.GetProperty("ok").GetBoolean(),
                check.GetProperty("detail").GetString() ?? string.Empty);
        }

        return doctor.GetProperty("ok").GetBoolean() ? 0 : 2;
    }

    private static async Task<int> PingAsync(CliOptions options, ConsoleUi ui)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        AttachDiagnostics(client, options, ui);
        var handshake = await client.HandshakeAsync().ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var ping = await client.PingAsync("ping").ConfigureAwait(false);
        stopwatch.Stop();

        if (!ping.GetProperty("pong").GetBoolean())
        {
            ui.Error("core did not pong");
            return 1;
        }

        ui.Line(
            $"pong from {handshake.ServerName} {handshake.ServerVersion} " +
            $"in {stopwatch.ElapsedMilliseconds} ms (protocol {handshake.ProtocolVersion})");
        return 0;
    }

    private static async Task<int> RunTaskAsync(CliOptions options, ConsoleUi ui)
    {
        if (string.IsNullOrWhiteSpace(options.TaskText))
        {
            ui.Error("run requires a task, e.g. kl run \"fix the failing test\"");
            return 64;
        }

        await using var session = await KlSessionClient.StartAsync(
                options.ToClientOptions(),
                options.ToSessionOptions(),
                new PlainTextSessionPresenter(options, ui))
            .ConfigureAwait(false);
        ui.Line($"session {session.SessionId} | {session.Provider}/{session.Model}");
        var outcome = await session.RunTurnAsync(options.TaskText).ConfigureAwait(false);
        if (outcome.Status == "failed")
        {
            ui.Error(outcome.Text);
            return 1;
        }
        ui.Line($"turn {outcome.Status}");
        return outcome.Status == "cancelled" ? 130 : 0;
    }

    private static async Task<int> ChatAsync(CliOptions options, ConsoleUi ui)
    {
        if (ChatTuiRunner.CanUseTui)
        {
            return await ChatTuiRunner.RunAsync(options).ConfigureAwait(false);
        }
        return await PlainChatAsync(options, ui).ConfigureAwait(false);
    }

    private static async Task<int> PlainChatAsync(CliOptions options, ConsoleUi ui)
    {
        await using var session = await KlSessionClient.StartAsync(
                options.ToClientOptions(),
                options.ToSessionOptions(),
                new PlainTextSessionPresenter(options, ui))
            .ConfigureAwait(false);
        ui.Header($"KL session {session.SessionId}");
        ui.Line($"workspace: {session.Workspace}");
        ui.Line($"provider: {session.Provider}/{session.Model} ({session.MessageCount} messages)");
        if (!string.IsNullOrWhiteSpace(options.Session))
        {
            await PrintHistoryAsync(session, ui).ConfigureAwait(false);
        }
        ui.Line("Type /exit to quit, Ctrl+C to cancel the current turn.");

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _ = session.CancelAsync();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            while (true)
            {
                Console.Write("\nkl> ");
                var line = Console.ReadLine();
                if (line is null
                    || line.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)
                    || line.Trim().Equals("/quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (ChatCommand.IsSettings(line))
                {
                    ui.Line(
                        "settings require an interactive terminal; " +
                        "run kl in a terminal and enter /settings");
                    continue;
                }

                var outcome = await session.RunTurnAsync(line).ConfigureAwait(false);
                if (outcome.Status == "failed")
                {
                    ui.Error(outcome.Text);
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
        return 0;
    }

    private static async Task PrintHistoryAsync(KlSessionClient session, ConsoleUi ui)
    {
        try
        {
            var result = await session.HistoryAsync(20).ConfigureAwait(false);
            foreach (var message in ChatTuiState.ParseHistory(result))
            {
                switch (message.Role)
                {
                    case "user" when !string.IsNullOrWhiteSpace(message.Text):
                        ui.Line($"You: {message.Text}");
                        break;
                    case "assistant" when !string.IsNullOrWhiteSpace(message.Text):
                        ui.Line($"KL: {message.Text}");
                        break;
                    case "tool":
                        var tool = message.ToolDisplay();
                        if (!string.IsNullOrWhiteSpace(tool))
                        {
                            ui.Line($"[tool] {tool}");
                        }
                        break;
                }
            }
        }
        catch (Exception error)
            when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ui.Error($"history unavailable: {error.Message}");
        }
    }

    private static async Task<int> ResumeAsync(CliOptions options, ConsoleUi ui)
    {
        if (!string.IsNullOrWhiteSpace(options.Session))
        {
            return await ChatAsync(options, ui).ConfigureAwait(false);
        }

        await using var client = KlCoreClient.Start(options.ToClientOptions());
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.ListSessionsAsync().ConfigureAwait(false);
        var sessions = result.GetProperty("sessions");
        if (sessions.GetArrayLength() == 0)
        {
            ui.Line("no sessions yet");
            return 0;
        }

        ui.Header("Sessions");
        foreach (var session in sessions.EnumerateArray())
        {
            ui.Line(
                $"{session.GetProperty("session_id").GetString()}  " +
                $"{session.GetProperty("updated_at").GetString()}  " +
                $"{session.GetProperty("provider").GetString()}/" +
                $"{session.GetProperty("model").GetString()}  " +
                $"{session.GetProperty("message_count").GetInt32()} msgs  " +
                $"{session.GetProperty("workspace").GetString()}");
        }
        ui.Line("\nuse: kl resume <session-id>");
        return 0;
    }

    private static async Task<int> PluginAsync(CliOptions options, ConsoleUi ui)
    {
        var subcommand = (options.Positionals.FirstOrDefault() ?? "list").ToLowerInvariant();
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);

        if (subcommand == "list")
        {
            var result = await client.PluginListAsync().ConfigureAwait(false);
            var plugins = result.GetProperty("plugins");
            ui.Header("Plugins");
            if (plugins.GetArrayLength() == 0)
            {
                ui.Line("no plugins installed");
                return 0;
            }
            foreach (var plugin in plugins.EnumerateArray())
            {
                ui.Line(
                    $"{plugin.GetProperty("id").GetString()}  " +
                    $"v{plugin.GetProperty("version").GetString()}  " +
                    $"{plugin.GetProperty("kind").GetString()}  " +
                    $"{(plugin.GetProperty("enabled").GetBoolean() ? "enabled" : "disabled")}  " +
                    $"{(plugin.GetProperty("healthy").GetBoolean() ? "healthy" : "unhealthy")}  " +
                    $"{plugin.GetProperty("detail").GetString()}");
            }
            return 0;
        }

        JsonElement response;
        switch (subcommand)
        {
            case "install":
                if (options.Positionals.Count < 2)
                {
                    ui.Error("plugin install requires a path");
                    return 64;
                }
                response = await client.PluginInstallAsync(options.Positionals[1])
                    .ConfigureAwait(false);
                break;
            case "reload":
            case "enable":
            case "disable":
                if (options.Positionals.Count < 2)
                {
                    ui.Error($"plugin {subcommand} requires a plugin id");
                    return 64;
                }
                response = subcommand switch
                {
                    "reload" => await client.PluginReloadAsync(options.Positionals[1])
                        .ConfigureAwait(false),
                    "enable" => await client.PluginEnableAsync(options.Positionals[1])
                        .ConfigureAwait(false),
                    _ => await client.PluginDisableAsync(options.Positionals[1])
                        .ConfigureAwait(false)
                };
                break;
            default:
                ui.Error($"unknown plugin command: {subcommand}");
                return 64;
        }

        ui.Line(JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> ReviewAsync(CliOptions options, ConsoleUi ui)
    {
        var workspace = Path.GetFullPath(options.Workspace ?? Directory.GetCurrentDirectory());
        var mode = options.Strict ? "strict" : options.ReviewMode ?? "standard";
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.ReviewAsync(
                workspace,
                options.BaseRef,
                mode,
                options.Strict)
            .ConfigureAwait(false);
        ui.Line(ReviewOutput.Render(result));
        return ReviewOutput.ExitCode(result);
    }

    private static async Task<int> AuditAsync(CliOptions options, ConsoleUi ui)
    {
        var limit = 20;
        if (options.Positionals.FirstOrDefault() is { } raw
            && int.TryParse(raw, out var parsed))
        {
            limit = Math.Clamp(parsed, 1, 1000);
        }

        await using var client = KlCoreClient.Start(options.ToClientOptions());
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.AuditTailAsync(limit).ConfigureAwait(false);
        var records = result.GetProperty("records");
        ui.Header($"Audit (last {limit})");
        foreach (var record in records.EnumerateArray())
        {
            var ok = record.GetProperty("ok").GetBoolean();
            ui.Line(
                $"{record.GetProperty("timestamp").GetString()}  " +
                $"{(ok ? "ok  " : "fail")}  " +
                $"{record.GetProperty("decision").GetString(),-5}  " +
                $"{record.GetProperty("tool").GetString(),-10}  " +
                $"{record.GetProperty("resource").GetString()}");
        }
        return 0;
    }

    private static async Task<int> ConfigAsync(CliOptions options, ConsoleUi ui)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.GetConfigAsync().ConfigureAwait(false);
        ui.Line(JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void AttachDiagnostics(KlCoreClient client, CliOptions options, ConsoleUi ui)
    {
        if (!options.Verbose)
        {
            return;
        }

        client.Diagnostic += line => ui.Error($"[core] {line}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            kl - terminal client for the KL coding agent

            Usage:
              kl                         interactive chat (alias: kl chat)
              kl chat                    start or continue a session
              kl run "<task>"            run one task and exit
              kl resume [session]        resume a session
              kl doctor                  check the core and environment
              kl ping                    verify the core handshake
              kl provider list           list configured providers
              kl provider add            add a provider interactively
              kl provider enable <id>    enable a provider
              kl provider disable <id>   disable a provider
              kl auth set <provider>     save a masked API key
              kl model list              list configured models
              kl model select            choose the default provider/model
              kl plugin list             list installed sidecar plugins
              kl plugin install <path>   install a local plugin directory
              kl plugin reload <id>      reload a plugin without restarting core
              kl plugin enable <id>      enable a plugin
              kl plugin disable <id>     drain and disable a plugin
              kl review [--base <ref>]   review the current diff
              kl audit [limit]           show recent audit records
              kl config                  show the redacted configuration
              kl version                 print versions

            Options:
              --core <path>              path to kl-core
              --config <path>            KL config file (TOML)
              --home <path>              session/audit home (default ~/.kl)
              --workspace <path>, -C     workspace root
              --provider <id>            override provider
              --model <id>               override model
              --session <id>             resume a specific session
              --base <ref>               review changes since a git ref
              --strict                   show low-confidence review findings
              --mode <mode>              review mode: quick|standard|strict
              --quality <mode>           quality mode: fast|balanced|deep
              --agent <preset>           agent: build|plan|explore|review|verify|deep
              --yes, -y                  auto-approve tool requests
              --verbose, -v              show core diagnostics
            """);
    }
}

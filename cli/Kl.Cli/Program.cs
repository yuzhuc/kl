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
                return await ProviderAsync(options, ui).ConfigureAwait(false);
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
        await using var client = KlCoreClient.Start(options);
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
        await using var client = KlCoreClient.Start(options);
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

        await using var session = await KlSessionClient.StartAsync(options, ui)
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
        await using var session = await KlSessionClient.StartAsync(options, ui)
            .ConfigureAwait(false);
        ui.Header($"KL session {session.SessionId}");
        ui.Line($"workspace: {session.Workspace}");
        ui.Line($"provider: {session.Provider}/{session.Model} ({session.MessageCount} messages)");
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

    private static async Task<int> ResumeAsync(CliOptions options, ConsoleUi ui)
    {
        if (!string.IsNullOrWhiteSpace(options.Session))
        {
            return await ChatAsync(options, ui).ConfigureAwait(false);
        }

        await using var client = KlCoreClient.Start(options);
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

    private static async Task<int> ProviderAsync(CliOptions options, ConsoleUi ui)
    {
        var subcommand = options.Positionals.FirstOrDefault() ?? "list";
        if (!subcommand.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            ui.Error($"unknown provider command: {subcommand} (only `list` is supported)");
            return 64;
        }

        await using var client = KlCoreClient.Start(options);
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.ListProvidersAsync().ConfigureAwait(false);
        ui.Header("Providers");
        foreach (var provider in result.GetProperty("providers").EnumerateArray())
        {
            var active = provider.GetProperty("active").GetBoolean();
            var available = provider.GetProperty("available").GetBoolean();
            ui.Line(
                $"{(active ? "*" : " ")} {provider.GetProperty("id").GetString(),-12} " +
                $"{provider.GetProperty("kind").GetString(),-14} " +
                $"{(available ? "available" : "unavailable"),-11} " +
                $"{provider.GetProperty("detail").GetString()}");
        }
        return 0;
    }

    private static async Task<int> AuditAsync(CliOptions options, ConsoleUi ui)
    {
        var limit = 20;
        if (options.Positionals.FirstOrDefault() is { } raw
            && int.TryParse(raw, out var parsed))
        {
            limit = Math.Clamp(parsed, 1, 1000);
        }

        await using var client = KlCoreClient.Start(options);
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
        await using var client = KlCoreClient.Start(options);
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
              --yes, -y                  auto-approve tool requests
              --verbose, -v              show core diagnostics
            """);
    }
}

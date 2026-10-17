using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kl.Cli;

public static class ProviderCli
{
    public static async Task<int> ProviderAsync(CliOptions options, ConsoleUi ui)
    {
        var subcommand = (options.Positionals.FirstOrDefault() ?? "list").ToLowerInvariant();
        return subcommand switch
        {
            "list" => await ListAsync(options, ui).ConfigureAwait(false),
            "add" => await AddAsync(options, ui).ConfigureAwait(false),
            "enable" => await SetEnabledAsync(options, ui, enabled: true).ConfigureAwait(false),
            "disable" => await SetEnabledAsync(options, ui, enabled: false).ConfigureAwait(false),
            _ => Unknown(ui, "provider", subcommand)
        };
    }

    public static async Task<int> AuthAsync(CliOptions options, ConsoleUi ui)
    {
        var subcommand = (options.Positionals.FirstOrDefault() ?? "set").ToLowerInvariant();
        if (subcommand != "set")
        {
            return Unknown(ui, "auth", subcommand);
        }
        if (options.Positionals.Count < 2)
        {
            ui.Error("auth set requires a provider id");
            return 64;
        }

        var provider = options.Positionals[1].Trim();
        var key = ReadSecret("API key: ");
        if (string.IsNullOrWhiteSpace(key))
        {
            ui.Error("API key must not be empty");
            return 64;
        }

        await using var client = KlCoreClient.Start(options);
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.SetProviderApiKeyAsync(provider, key).ConfigureAwait(false);
        ui.Line(
            $"key saved for {result.GetProperty("provider").GetString()} " +
            $"(key_configured={result.GetProperty("key_configured").GetBoolean().ToString().ToLowerInvariant()})");
        return 0;
    }

    public static async Task<int> ModelAsync(CliOptions options, ConsoleUi ui)
    {
        var subcommand = (options.Positionals.FirstOrDefault() ?? "list").ToLowerInvariant();
        if (subcommand == "list")
        {
            return await ListAsync(options, ui).ConfigureAwait(false);
        }
        if (subcommand != "select")
        {
            return Unknown(ui, "model", subcommand);
        }

        await using var client = KlCoreClient.Start(options);
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);

        string? provider = options.Provider;
        string? model = options.Model;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            if (options.Positionals.Count >= 3)
            {
                provider = options.Positionals[1];
                model = options.Positionals[2];
            }
            else
            {
                var selected = await SelectModelAsync(client, ui).ConfigureAwait(false);
                if (selected is null)
                {
                    return 64;
                }
                provider = selected.Value.Provider;
                model = selected.Value.Model;
            }
        }

        var result = await client.SetDefaultModelAsync(provider, model)
            .ConfigureAwait(false);
        ui.Line(
            $"default model: {result.GetProperty("provider").GetString()}/" +
            $"{result.GetProperty("model").GetString()}");
        return 0;
    }

    private static async Task<int> ListAsync(CliOptions options, ConsoleUi ui)
    {
        await using var client = KlCoreClient.Start(options);
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.ListProvidersAsync().ConfigureAwait(false);
        var activeProvider = result.GetProperty("active_provider").GetString();
        var activeModel = result.GetProperty("active_model").GetString();
        var models = result.GetProperty("models");
        if (models.GetArrayLength() == 0)
        {
            ui.Header("Providers");
            ui.Line("no enabled providers/models configured");
            return 0;
        }

        ui.Header("Providers");
        foreach (var model in models.EnumerateArray())
        {
            var provider = model.GetProperty("provider").GetString();
            var modelId = model.GetProperty("model").GetString();
            var protocol = model.GetProperty("protocol").GetString();
            var url = model.GetProperty("url").GetString();
            var enabled = model.GetProperty("enabled").GetBoolean();
            var keyConfigured = model.GetProperty("key_configured").GetBoolean();
            var available = model.GetProperty("available").GetBoolean();
            var active = provider == activeProvider && modelId == activeModel;
            ui.Line(
                $"{(active ? "*" : " ")} {provider}/{modelId} [{protocol}] " +
                $"enabled={enabled.ToString().ToLowerInvariant()} " +
                $"key_configured={keyConfigured.ToString().ToLowerInvariant()} " +
                $"available={available.ToString().ToLowerInvariant()}");
            ui.Line($"    url: {url}");
            ui.Line(
                $"    context_window: {model.GetProperty("context_window").GetInt32()}  " +
                $"compact_threshold: {model.GetProperty("compact_threshold").GetDouble():0.###}  " +
                $"max_output_tokens: {model.GetProperty("max_output_tokens").GetInt32()}");
            ui.Line($"    {model.GetProperty("detail").GetString()}");
        }
        return 0;
    }

    private static async Task<int> AddAsync(CliOptions options, ConsoleUi ui)
    {
        ui.Header("Add provider");
        var id = Prompt("Provider id: ");
        if (string.IsNullOrWhiteSpace(id))
        {
            ui.Error("provider id is required");
            return 64;
        }

        var openaiChat = Prompt("openai-chat URL (optional): ") ?? string.Empty;
        var anthropic = Prompt("anthropic URL (optional): ") ?? string.Empty;
        var openaiResponses = Prompt("openai-responses URL (optional): ") ?? string.Empty;
        var key = ReadSecret("API key: ");
        if (string.IsNullOrWhiteSpace(key))
        {
            ui.Error("API key is required");
            return 64;
        }

        var models = new List<Dictionary<string, object?>>();
        while (true)
        {
            var modelId = Prompt("Model id (empty to finish): ");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                break;
            }

            var protocol = Prompt("Protocol (openai-chat|anthropic|openai-responses): ")
                ?? string.Empty;
            var contextWindow = PromptPositiveInt("context_window: ", ui);
            if (contextWindow is null)
            {
                return 64;
            }
            var threshold = PromptThreshold("compact_threshold (0-1): ", ui);
            if (threshold is null)
            {
                return 64;
            }
            var maxOutput = PromptPositiveInt("max_output_tokens: ", ui);
            if (maxOutput is null)
            {
                return 64;
            }
            var modelUrl = Prompt("Model URL override (optional): ");

            var model = new Dictionary<string, object?>
            {
                ["id"] = modelId.Trim(),
                ["protocol"] = protocol.Trim(),
                ["context_window"] = contextWindow.Value,
                ["compact_threshold"] = threshold.Value,
                ["max_output_tokens"] = maxOutput.Value
            };
            if (!string.IsNullOrWhiteSpace(modelUrl))
            {
                model["url"] = modelUrl.Trim();
            }
            models.Add(model);
        }

        if (models.Count == 0)
        {
            ui.Error("at least one model is required");
            return 64;
        }

        var enabled = AskYesNo("Enable provider now? [Y/n] ", defaultYes: true);
        var setDefault = AskYesNo("Set first model as default? [y/N] ", defaultYes: false);
        if (!enabled && setDefault)
        {
            ui.Error("a disabled provider cannot be the default");
            return 64;
        }

        var provider = id.Trim();

        await using var client = KlCoreClient.Start(options);
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var protocols = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(openaiChat))
        {
            protocols["openai-chat"] = openaiChat.Trim();
        }
        if (!string.IsNullOrWhiteSpace(anthropic))
        {
            protocols["anthropic"] = anthropic.Trim();
        }
        if (!string.IsNullOrWhiteSpace(openaiResponses))
        {
            protocols["openai-responses"] = openaiResponses.Trim();
        }

        var upsert = await client.UpsertProviderAsync(new Dictionary<string, object?>
        {
            ["id"] = provider,
            ["protocols"] = protocols,
            ["models"] = models,
            ["enabled"] = enabled
        }).ConfigureAwait(false);
        await client.SetProviderApiKeyAsync(provider, key).ConfigureAwait(false);

        var firstModel = models[0]["id"]?.ToString() ?? string.Empty;
        if (setDefault)
        {
            await client.SetDefaultModelAsync(provider, firstModel).ConfigureAwait(false);
        }

        ui.Line($"provider saved: {upsert.GetProperty("provider").GetString()}");
        ui.Line($"path: {upsert.GetProperty("path").GetString()}");
        ui.Line($"models: {upsert.GetProperty("models").GetInt32()}");
        ui.Line("key: configured");
        if (setDefault)
        {
            ui.Line($"default: {provider}/{firstModel}");
        }
        return 0;
    }

    private static async Task<int> SetEnabledAsync(
        CliOptions options,
        ConsoleUi ui,
        bool enabled)
    {
        if (options.Positionals.Count < 2)
        {
            ui.Error($"provider {(enabled ? "enable" : "disable")} requires a provider id");
            return 64;
        }
        var provider = options.Positionals[1].Trim();
        await using var client = KlCoreClient.Start(options);
        AttachDiagnostics(client, options, ui);
        await client.HandshakeAsync().ConfigureAwait(false);
        var result = await client.SetProviderEnabledAsync(provider, enabled)
            .ConfigureAwait(false);
        ui.Line(
            $"provider {result.GetProperty("provider").GetString()}: " +
            $"enabled={result.GetProperty("enabled").GetBoolean().ToString().ToLowerInvariant()}");
        return 0;
    }

    private static async Task<(string Provider, string Model)?> SelectModelAsync(
        KlCoreClient client,
        ConsoleUi ui)
    {
        var result = await client.ListProvidersAsync().ConfigureAwait(false);
        var models = result.GetProperty("models");
        if (models.GetArrayLength() == 0)
        {
            ui.Error("no models configured; run `kl provider add` first");
            return null;
        }
        if (Console.IsInputRedirected)
        {
            ui.Error("model select requires --provider/--model or an interactive terminal");
            return null;
        }

        ui.Header("Models");
        var rows = new List<(string Provider, string Model)>();
        var index = 1;
        foreach (var model in models.EnumerateArray())
        {
            var provider = model.GetProperty("provider").GetString() ?? string.Empty;
            var modelId = model.GetProperty("model").GetString() ?? string.Empty;
            var protocol = model.GetProperty("protocol").GetString() ?? string.Empty;
            var available = model.GetProperty("available").GetBoolean();
            ui.Line(
                $"{index}. {provider}/{modelId} [{protocol}] " +
                $"{(available ? "available" : "unavailable")}");
            rows.Add((provider, modelId));
            index++;
        }

        var raw = Prompt("Select model: ");
        if (!int.TryParse(raw, out var selected)
            || selected < 1
            || selected > rows.Count)
        {
            ui.Error("invalid model selection");
            return null;
        }
        return rows[selected - 1];
    }

    private static int? PromptPositiveInt(string prompt, ConsoleUi ui)
    {
        var raw = Prompt(prompt);
        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            ui.Error("expected a positive integer");
            return null;
        }
        return value;
    }

    private static double? PromptThreshold(string prompt, ConsoleUi ui)
    {
        var raw = Prompt(prompt);
        if (!double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || value < 0
            || value > 1)
        {
            ui.Error("expected a number between 0 and 1");
            return null;
        }
        return value;
    }

    private static bool AskYesNo(string prompt, bool defaultYes)
    {
        var raw = Prompt(prompt);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultYes;
        }
        return raw.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static string? Prompt(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (key.Key == ConsoleKey.C
                && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                throw new OperationCanceledException();
            }
            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    private static int Unknown(ConsoleUi ui, string command, string subcommand)
    {
        ui.Error($"unknown {command} command: {subcommand}");
        return 64;
    }

    private static void AttachDiagnostics(
        KlCoreClient client,
        CliOptions options,
        ConsoleUi ui)
    {
        if (options.Verbose)
        {
            client.Diagnostic += line => ui.Error($"[core] {line}");
        }
    }
}

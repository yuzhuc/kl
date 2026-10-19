using System.Text.Json;

namespace Kl.Cli;

public static class SettingsTuiRunner
{
    public const string PermissionsSetMethod = "permissions.set";
    public const string AgentConfigGetMethod = "agent.config.get";
    public const string AgentConfigSetMethod = "agent.config.set";

    public static async Task<SettingsSnapshot> LoadAsync(
        CliOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        var providers = await client.ListProvidersAsync(cancellationToken)
            .ConfigureAwait(false);
        var config = await client.GetConfigAsync(cancellationToken)
            .ConfigureAwait(false);
        var plugins = await client.PluginListAsync(cancellationToken)
            .ConfigureAwait(false);
        var doctor = default(JsonElement);
        var agentConfig = default(JsonElement);
        string? readError = null;
        try
        {
            doctor = await client.DoctorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            readError = "sandbox status unavailable: " + error.Message;
        }
        try
        {
            agentConfig = await client.RequestAsync(
                    AgentConfigGetMethod,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            var message = "agent.config.get failed: " + error.Message;
            readError = readError is null ? message : readError + "; " + message;
        }

        var snapshot = SettingsSnapshot.FromJson(
            providers,
            config,
            plugins,
            doctor,
            agentConfig);
        snapshot = snapshot with
        {
            AgentConfig = snapshot.AgentConfig with { ReadError = readError },
            Paths = new SettingsPaths(
                snapshot.Paths.ConfigPath,
                UiSettingsPath(options),
                HomePath(options))
        };
        return WithCredentialState(snapshot, options);
    }

    public static async Task SaveProviderAsync(
        CliOptions options,
        ProviderDraft draft,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        await client.UpsertProviderAsync(
                draft.ToUpsert(),
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(draft.ApiKey.Value))
        {
            await client.SetProviderApiKeyAsync(
                    draft.EffectiveId,
                    draft.ApiKey.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static UiLanguage LoadLanguage(CliOptions options)
    {
        try
        {
            var path = UiSettingsPath(options);
            if (!File.Exists(path))
            {
                return UiLanguage.English;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("language", out var value)
                && value.GetString() == "zh-CN"
                ? UiLanguage.ChineseSimplified
                : UiLanguage.English;
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException
                or JsonException)
        {
            return UiLanguage.English;
        }
    }

    public static async Task SaveLanguageAsync(
        CliOptions options,
        UiLanguage language,
        CancellationToken cancellationToken = default)
    {
        var path = UiSettingsPath(options);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var json = JsonSerializer.Serialize(
            new { language = language == UiLanguage.ChineseSimplified ? "zh-CN" : "en" },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string HomePath(CliOptions options)
    {
        var home = options.HomePath;
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("KL_HOME");
        }
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kl");
        }
        return Path.GetFullPath(home);
    }

    private static string UiSettingsPath(CliOptions options) =>
        Path.Combine(HomePath(options), "ui.json");

    private static SettingsSnapshot WithCredentialState(
        SettingsSnapshot snapshot,
        CliOptions options)
    {
        var home = HomePath(options);

        var providers = snapshot.Providers
            .Select(provider =>
            {
                if (provider.KeyConfigured is not null)
                {
                    return provider;
                }
                bool configured;
                try
                {
                    configured = File.Exists(Path.Combine(
                        home,
                        "credentials",
                        provider.Id + ".key"));
                }
                catch (Exception error)
                    when (error is IOException or ArgumentException
                        or NotSupportedException)
                {
                    configured = false;
                }
                return provider with
                {
                    KeyConfigured = configured ? true : null
                };
            })
            .ToList();
        return snapshot with { Providers = providers };
    }

    public static async Task SetProviderEnabledAsync(
        CliOptions options,
        string provider,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        await client.SetProviderEnabledAsync(provider, enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task SetDefaultModelAsync(
        CliOptions options,
        string provider,
        string model,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        if (provider == SettingsAgentRoutes.Permissions)
        {
            await client.RequestAsync(
                    PermissionsSetMethod,
                    PermissionsSetParameters(model),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (provider == SettingsAgentRoutes.Reasoning)
        {
            await client.RequestAsync(
                    AgentConfigSetMethod,
                    AgentConfigSetParameters(model),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        await client.SetDefaultModelAsync(provider, model, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Dictionary<string, object?> PermissionsSetParameters(string mode) =>
        new()
        {
            ["mode"] = SettingsAgentValues.Wire(
                SettingsAgentValues.ParseApprovalMode(mode))
        };

    public static Dictionary<string, object?> AgentConfigSetParameters(string effort) =>
        new()
        {
            ["effort"] = SettingsAgentValues.Wire(
                SettingsAgentValues.ParseReasoningEffort(effort))
        };

    public static async Task SetPluginEnabledAsync(
        CliOptions options,
        string plugin,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        if (enabled)
        {
            await client.PluginEnableAsync(plugin, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await client.PluginDisableAsync(plugin, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static async Task ReloadPluginAsync(
        CliOptions options,
        string plugin,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options.ToClientOptions());
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        await client.PluginReloadAsync(plugin, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsSettingsError(Exception error) =>
        error is JsonRpcException or IOException or InvalidOperationException;
}

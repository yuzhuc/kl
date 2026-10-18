namespace Kl.Cli;

public static class SettingsTuiRunner
{
    public static async Task<SettingsSnapshot> LoadAsync(
        CliOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options);
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        var providers = await client.ListProvidersAsync(cancellationToken)
            .ConfigureAwait(false);
        var config = await client.GetConfigAsync(cancellationToken)
            .ConfigureAwait(false);
        return WithCredentialState(
            SettingsSnapshot.FromJson(providers, config),
            options);
    }

    public static async Task SaveProviderAsync(
        CliOptions options,
        ProviderDraft draft,
        CancellationToken cancellationToken = default)
    {
        await using var client = KlCoreClient.Start(options);
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

    private static SettingsSnapshot WithCredentialState(
        SettingsSnapshot snapshot,
        CliOptions options)
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
        await using var client = KlCoreClient.Start(options);
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
        await using var client = KlCoreClient.Start(options);
        await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        await client.SetDefaultModelAsync(provider, model, cancellationToken)
            .ConfigureAwait(false);
    }
}

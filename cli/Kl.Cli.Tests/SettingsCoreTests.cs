using Xunit;

namespace Kl.Cli.Tests;

public sealed class SettingsCoreTests
{
    [Fact]
    public async Task BaseModeWritesExpandedUrlsAndKeepsKeyOutOfToml()
    {
        var root = TempRoot();
        try
        {
            var options = Options(root);
            var draft = Draft(enabled: true, SettingsAddressMode.Base);
            draft.ApiKey.Set("super-secret");

            await SettingsTuiRunner.SaveProviderAsync(options, draft);

            var providerToml = await File.ReadAllTextAsync(
                Path.Combine(root, "providers", "local.toml"));
            Assert.Contains(
                "openai-chat = \"https://gateway.example/v1/chat/completions\"",
                providerToml);
            Assert.Contains(
                "anthropic = \"https://gateway.example/v1/messages\"",
                providerToml);
            Assert.Contains(
                "openai-responses = \"https://gateway.example/v1/responses\"",
                providerToml);
            Assert.DoesNotContain("super-secret", providerToml);
            Assert.Equal(
                "super-secret",
                await File.ReadAllTextAsync(
                    Path.Combine(root, "home", "credentials", "local.key")));

            var snapshot = await SettingsTuiRunner.LoadAsync(options);
            var provider = Assert.Single(snapshot.Providers);
            Assert.True(provider.Enabled);
            Assert.True(provider.KeyConfigured);
            Assert.Equal(1, provider.ModelCount);
            Assert.Equal("openai-chat", provider.ProtocolSummary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FullModePreservesUrlsAndDisablingHidesModels()
    {
        var root = TempRoot();
        try
        {
            var options = Options(root);
            var draft = Draft(enabled: true, SettingsAddressMode.Full);
            draft.ChatUrl.Set("https://gateway.example/custom/chat?tenant=kl");
            draft.AnthropicUrl.Set("https://gateway.example/custom/messages?tenant=kl");
            draft.ResponsesUrl.Set("https://gateway.example/custom/responses?tenant=kl");
            draft.ApiKey.Set("super-secret");

            await SettingsTuiRunner.SaveProviderAsync(options, draft);

            var providerToml = await File.ReadAllTextAsync(
                Path.Combine(root, "providers", "local.toml"));
            Assert.Contains(
                "https://gateway.example/custom/chat?tenant=kl",
                providerToml);
            Assert.Contains(
                "https://gateway.example/custom/messages?tenant=kl",
                providerToml);
            Assert.Contains(
                "https://gateway.example/custom/responses?tenant=kl",
                providerToml);

            draft.Enabled = false;
            await SettingsTuiRunner.SaveProviderAsync(options, draft);
            var snapshot = await SettingsTuiRunner.LoadAsync(options);

            Assert.DoesNotContain(snapshot.Models, model => model.Provider == "local");
            var provider = Assert.Single(snapshot.Providers);
            Assert.False(provider.Enabled);
            Assert.Equal(0, provider.ModelCount);
            Assert.True(provider.KeyConfigured);

            await SettingsTuiRunner.SetProviderEnabledAsync(options, "local", true);
            snapshot = await SettingsTuiRunner.LoadAsync(options);
            provider = Assert.Single(snapshot.Providers);
            Assert.True(provider.Enabled);
            Assert.Equal(1, provider.ModelCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginsLoadToggleReloadAndListFields()
    {
        var root = TempRoot();
        try
        {
            var options = Options(root);
            var source = Path.Combine(root, "plugin-source");
            Directory.CreateDirectory(source);
            File.Copy(
                RepoFile(
                    "crates",
                    "kl-plugin-host",
                    "tests",
                    "fixtures",
                    "wasm-tool",
                    "valid.wat"),
                Path.Combine(source, "tool.wat"));
            await File.WriteAllTextAsync(
                Path.Combine(source, "manifest.json"),
                """
                {
                  "id": "settings-test",
                  "version": "1",
                  "api": 1,
                  "kind": "tool",
                  "entrypoint": "tool.wat",
                  "runtime": "wasm",
                  "capabilities": ["tool"],
                  "permissions": {
                    "network": [],
                    "env": [],
                    "fs_read": [],
                    "fs_write": []
                  },
                  "tool_name": "settings_test",
                  "tool_parameters": { "type": "object" },
                  "health_timeout_ms": 1000,
                  "drain_timeout_ms": 500,
                  "call_timeout_ms": 1000,
                  "memory_limit_mb": 64
                }
                """);

            await using (var client = KlCoreClient.Start(options))
            {
                await client.HandshakeAsync();
                await client.PluginInstallAsync(source);
            }

            var snapshot = await SettingsTuiRunner.LoadAsync(options);
            var plugin = Assert.Single(snapshot.Plugins);
            Assert.Equal("settings-test", plugin.Id);
            Assert.Equal("1", plugin.Version);
            Assert.Equal("tool", plugin.Kind);
            Assert.True(plugin.Enabled);
            Assert.True(plugin.Healthy);
            Assert.Equal("settings_test", plugin.ToolName);

            await SettingsTuiRunner.SetPluginEnabledAsync(
                options,
                plugin.Id,
                enabled: false);
            snapshot = await SettingsTuiRunner.LoadAsync(options);
            plugin = Assert.Single(snapshot.Plugins);
            Assert.False(plugin.Enabled);
            Assert.False(plugin.Healthy);
            Assert.Contains("disabled", plugin.Detail);

            await SettingsTuiRunner.SetPluginEnabledAsync(
                options,
                plugin.Id,
                enabled: true);
            snapshot = await SettingsTuiRunner.LoadAsync(options);
            plugin = Assert.Single(snapshot.Plugins);
            Assert.True(plugin.Enabled);
            Assert.True(plugin.Healthy);

            await SettingsTuiRunner.ReloadPluginAsync(options, plugin.Id);
            snapshot = await SettingsTuiRunner.LoadAsync(options);
            plugin = Assert.Single(snapshot.Plugins);
            Assert.True(plugin.Enabled);
            Assert.True(plugin.Healthy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultModelSelectionPersists()
    {
        var root = TempRoot();
        try
        {
            var options = Options(root);
            await SettingsTuiRunner.SaveProviderAsync(
                options,
                Draft(enabled: true, SettingsAddressMode.Base));
            await SettingsTuiRunner.SetDefaultModelAsync(options, "local", "model-1");

            var snapshot = await SettingsTuiRunner.LoadAsync(options);

            Assert.Equal("local", snapshot.ActiveProvider);
            Assert.Equal("model-1", snapshot.ActiveModel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProviderDraft Draft(bool enabled, SettingsAddressMode mode)
    {
        var draft = ProviderDraft.New();
        draft.Id.Set("local");
        draft.Enabled = enabled;
        draft.AddressMode = mode;
        draft.BaseUrl.Set("https://gateway.example/v1/");
        var model = new SettingsModelDraft();
        model.Id.Set("model-1");
        draft.Models.Add(model);
        return draft;
    }

    private static CliOptions Options(string root) => new()
    {
        ConfigPath = Path.Combine(root, "kl.toml"),
        HomePath = Path.Combine(root, "home")
    };

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            "repository file not found: " + Path.Combine(parts));
    }

    private static string TempRoot()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

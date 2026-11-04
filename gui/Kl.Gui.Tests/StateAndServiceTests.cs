using System.Text.Json;
using Kl.Gui.Models;
using Kl.Gui.Services;
using Kl.Gui.ViewModels;

namespace Kl.Gui.Tests;

/// <summary>
/// State-store, data-path and plugin catalog tests that do not involve kl-core.
/// Core integration is covered by <see cref="MainWindowViewModelCoreTests"/>.
/// </summary>
public sealed class StateAndServiceTests
{
    [Fact]
    public void StateStoreRoundTripsWorkspaceAndMessages()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        var workspaceId = Guid.NewGuid().ToString("N");
        var sessionId = Guid.NewGuid().ToString("N");
        var state = new AppState
        {
            Endpoint = "http://localhost:9999",
            Model = "test-model",
            ApiKeyEnvironmentVariable = "TEST_KEY",
            PermissionMode = "按需确认",
            InteractionMode = "chat",
            IsAddProjectPinned = true,
            IsCodeReviewPinned = true,
            ActiveWorkspaceId = workspaceId,
            ActiveSessionId = sessionId,
            Workspaces =
            [
                new WorkspaceState
                {
                    Id = workspaceId,
                    Name = "demo",
                    Path = temp.Path,
                    Sessions =
                    [
                        new SessionState
                        {
                            Id = sessionId,
                            Title = "持久化会话",
                            Messages =
                            [
                                new StoredMessage { Kind = "user", Text = "你好" },
                                new StoredMessage { Kind = "assistant", Text = "你好，世界" }
                            ]
                        }
                    ]
                }
            ]
        };

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal("http://localhost:9999", loaded.Endpoint);
        Assert.Equal("chat", loaded.InteractionMode);
        Assert.Equal("按需确认", loaded.PermissionMode);
        Assert.True(loaded.IsAddProjectPinned);
        Assert.True(loaded.IsCodeReviewPinned);
        Assert.Equal("持久化会话", Assert.Single(loaded.Workspaces).Sessions[0].Title);
        Assert.Equal("你好，世界", loaded.Workspaces[0].Sessions[0].Messages[1].Text);
    }

    [Fact]
    public void StateStoreKeepsApiKeyOutsideStateJson()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "gui", "state.json");
        var credentialPath = Path.Combine(temp.Path, "credentials", "deepseek.key");
        var store = new StateStore(statePath, credentialPath: credentialPath);
        var state = new AppState { ApiKey = "secret-key" };

        store.Save(state);

        Assert.True(File.Exists(credentialPath));
        Assert.Equal("secret-key", File.ReadAllText(credentialPath));
        Assert.DoesNotContain("secret-key", File.ReadAllText(statePath));
        Assert.DoesNotContain("\"ApiKey\"", File.ReadAllText(statePath));
        Assert.Equal("secret-key", store.Load().ApiKey);

        state.ApiKey = string.Empty;
        store.Save(state);
        Assert.False(File.Exists(credentialPath));
    }

    [Fact]
    public void LocalPluginCatalogLoadsManifestsAndSkipsInvalidFiles()
    {
        using var temp = new TempDirectory();
        var pluginRoot = Path.Combine(temp.Path, "plugins");
        var validDirectory = Path.Combine(pluginRoot, "sample");
        var invalidDirectory = Path.Combine(pluginRoot, "broken");
        Directory.CreateDirectory(validDirectory);
        Directory.CreateDirectory(invalidDirectory);
        File.WriteAllText(
            Path.Combine(validDirectory, "plugin.json"),
            """{"name":"Sample Plugin","version":"1.2.0","description":"Demo"}""");
        File.WriteAllText(Path.Combine(invalidDirectory, "plugin.json"), "{not-json");

        var service = new LocalPluginCatalogService(pluginRoot);
        var plugin = Assert.Single(service.Load());

        Assert.Equal("Sample Plugin", plugin.Name);
        Assert.Equal("1.2.0", plugin.Version);
        Assert.Equal("Demo", plugin.Description);
    }

    [Fact]
    public void LocalPluginCatalogLoadsKlManifest()
    {
        using var temp = new TempDirectory();
        var pluginRoot = Path.Combine(temp.Path, "plugins");
        var pluginDirectory = Path.Combine(pluginRoot, "installed", "core-sample", "1.0.0");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            """{"id":"core-sample","version":"1.0.0","kind":"tool"}""");

        var plugin = Assert.Single(new LocalPluginCatalogService(pluginRoot).Load());

        Assert.Equal("core-sample", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void KlDataPathsInitializesGuiDataAndMigratesLegacyAppData()
    {
        using var temp = new TempDirectory();
        var home = Path.Combine(temp.Path, "home", ".kl");
        var localAppData = Path.Combine(temp.Path, "localappdata");
        var legacyHarness = Path.Combine(localAppData, "DeepSeekHarnessGui");
        var legacyPlugins = Path.Combine(legacyHarness, "plugins", "sample");
        var legacyKl = Path.Combine(localAppData, "Kl");
        Directory.CreateDirectory(legacyPlugins);
        Directory.CreateDirectory(legacyKl);
        File.WriteAllText(
            Path.Combine(legacyHarness, "state.json"),
            """{"Endpoint":"http://localhost:1234","Model":"migrated"}""");
        File.WriteAllText(
            Path.Combine(legacyPlugins, "plugin.json"),
            """{"name":"Migrated Plugin","version":"1.0.0","description":"legacy"}""");
        File.WriteAllText(Path.Combine(legacyHarness, "extra.txt"), "keep me");
        File.WriteAllText(Path.Combine(legacyKl, "gui-settings.json"), """{"mode":"chat"}""");

        var paths = new KlDataPaths(home, localAppData);
        paths.EnsureInitialized();

        Assert.Equal(Path.Combine(home, "gui"), paths.GuiDirectory);
        Assert.Equal(Path.Combine(home, "gui", "state.json"), paths.StateFilePath);
        Assert.Equal(Path.Combine(home, "credentials", "deepseek.key"), paths.ApiKeyFilePath);
        Assert.Equal(Path.Combine(home, "plugins"), paths.PluginsDirectoryPath);
        Assert.Equal(paths.StateFilePath, new StateStore(dataPaths: paths).Path);
        Assert.Equal(paths.PluginsDirectoryPath, new LocalPluginCatalogService(dataPaths: paths).DirectoryPath);
        Assert.True(File.Exists(paths.StateFilePath));
        Assert.True(File.Exists(Path.Combine(paths.PluginsDirectoryPath, "sample", "plugin.json")));
        Assert.True(File.Exists(Path.Combine(paths.GuiDirectory, "extra.txt")));
        Assert.True(File.Exists(Path.Combine(paths.GuiDirectory, "gui-settings.json")));
        Assert.False(Directory.Exists(legacyHarness));
        Assert.False(Directory.Exists(legacyKl));
    }
}

public sealed class ChatItemMigrationTests
{
    [Fact]
    public void LegacyToolMessageKeepsArgumentsInDetailWithoutDuplicateSummary()
    {
        var item = ChatItemViewModel.FromStored(new StoredMessage
        {
            Kind = "tool",
            Header = "调用 inspect_workspace",
            Text = """{"path":"D:\\kl"}""",
            Detail = "模型请求调用工具。",
            Summary = "调用 inspect_workspace",
            Status = "完成"
        });

        Assert.Equal("模型请求调用工具。", item.Text);
        Assert.Equal("""{"path":"D:\\kl"}""", item.Detail);
        Assert.Equal("模型请求调用工具。", item.CollapsedText);
    }

    [Fact]
    public void StoredMessagesRoundTripThroughChatItem()
    {
        var original = ChatItemViewModel.Tool("调用 shell", "ls -la", "output", "done");
        original.Start("运行中");
        original.Complete("完成");

        var restored = ChatItemViewModel.FromStored(original.ToStored());

        Assert.Equal(ChatItemKind.Tool, restored.Kind);
        Assert.Equal("调用 shell", restored.Header);
        Assert.Equal("ls -la", restored.Text);
        Assert.Equal("output", restored.Detail);
        Assert.Equal("完成", restored.StatusText);
    }

    [Fact]
    public void TestJsonHelperProducesObjectElements()
    {
        var element = TestData.Json("""{"ok":true}""");

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.True(element.GetProperty("ok").GetBoolean());
    }
}

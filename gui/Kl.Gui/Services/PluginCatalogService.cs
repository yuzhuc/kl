using System.Text.Json;

namespace Kl.Gui.Services;

public sealed record LocalPluginInfo(
    string Name,
    string Version,
    string Description,
    string ManifestPath);

public interface IPluginCatalogService
{
    string DirectoryPath { get; }
    IReadOnlyList<LocalPluginInfo> Load();
}

public sealed class LocalPluginCatalogService : IPluginCatalogService
{
    public LocalPluginCatalogService(string? directoryPath = null, KlDataPaths? dataPaths = null)
    {
        if (directoryPath is not null)
        {
            DirectoryPath = directoryPath;
            return;
        }

        var paths = dataPaths ?? KlDataPaths.Default;
        paths.EnsureInitialized();
        DirectoryPath = Environment.GetEnvironmentVariable("KL_GUI_PLUGINS")
            ?? paths.PluginsDirectoryPath;
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<LocalPluginInfo> Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        var plugins = new List<LocalPluginInfo>();
        var manifests = Directory.EnumerateFiles(DirectoryPath, "plugin.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(DirectoryPath, "manifest.json", SearchOption.AllDirectories));
        foreach (var manifestPath in manifests)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                var fallbackName = new DirectoryInfo(Path.GetDirectoryName(manifestPath)!).Name;
                var name = GetString(root, "name") ?? GetString(root, "id") ?? fallbackName;
                var version = GetString(root, "version") ?? "未标注版本";
                var description = GetString(root, "description") ?? "未提供说明";
                plugins.Add(new LocalPluginInfo(name, version, description, manifestPath));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // Invalid manifests are ignored so one bad plugin cannot break the catalog.
            }
        }
        return plugins.OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
}


using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kl.Gui.Services;

/// <summary>Last-known provider cards shown before Core answers. Never contains credentials.</summary>
public sealed class ProviderCacheFile
{
    public int Version { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public List<ProviderCacheEntry> Providers { get; set; } = [];
}

public sealed class ProviderCacheEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; }
    public bool KeyConfigured { get; set; }
    public List<CoreModelOption> Models { get; set; } = [];
    public Dictionary<string, string> Protocols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderCacheStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;

    public ProviderCacheStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public ProviderCacheFile? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<ProviderCacheFile>(
                File.ReadAllText(_path),
                JsonOptions);
            if (file is null || file.Version != CurrentVersion)
            {
                return null;
            }

            file.Providers ??= [];
            return file;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    public void Save(IReadOnlyList<ProviderCacheEntry> providers)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new ProviderCacheFile
            {
                Version = CurrentVersion,
                SavedAt = DateTimeOffset.Now,
                Providers = providers.ToList()
            };
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cache persistence is best-effort; a refresh must still succeed.
        }
    }
}

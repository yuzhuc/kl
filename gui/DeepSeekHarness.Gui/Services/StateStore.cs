using System.Text.Json;
using DeepSeekHarness.Gui.Models;

namespace DeepSeekHarness.Gui.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;

    public StateStore(string? path = null, KlDataPaths? dataPaths = null)
    {
        if (path is not null)
        {
            _path = path;
            return;
        }

        var paths = dataPaths ?? KlDataPaths.Default;
        paths.EnsureInitialized();
        _path = Environment.GetEnvironmentVariable("DEEPSEEK_HARNESS_GUI_STATE")
            ?? paths.StateFilePath;
    }

    public string Path => _path;

    public AppState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppState();
            }

            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), JsonOptions)
                ?? new AppState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}

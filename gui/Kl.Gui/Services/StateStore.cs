using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kl.Gui.Models;

namespace Kl.Gui.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
        _path = Environment.GetEnvironmentVariable("KL_GUI_STATE")
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

            return JsonSerializer.Deserialize<AppState>(ReadStateJson(_path), JsonOptions)
                ?? new AppState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        var temporary = _path + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        File.WriteAllBytes(temporary, state.CompressHistory ? Compress(json) : json);
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>Reads either a plain JSON state file or a gzip-compressed one.</summary>
    public static string ReadStateJson(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            bytes = output.ToArray();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] Compress(byte[] json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return output.ToArray();
    }
}

namespace Kl.Gui.Services;

/// <summary>Appends crash and startup failures to the Codex-style log folder.</summary>
public static class AppLog
{
    public static string LogFilePath(string homeDirectory) =>
        Path.Combine(homeDirectory, "log", "kl-gui.log");

    public static void Write(string homeDirectory, string message)
    {
        try
        {
            var path = LogFilePath(homeDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}

namespace Kl.Gui.Services;

public sealed class KlDataPaths
{
    private readonly object _gate = new();
    private readonly string _legacyHarnessDirectory;
    private readonly string _legacyKlDirectory;
    private bool _initialized;

    public static KlDataPaths Default { get; } = new(
        ResolveKlHome(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public KlDataPaths(string homeDirectory, string localApplicationDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new ArgumentException("KL home directory is required.", nameof(homeDirectory));
        }
        if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
        {
            throw new ArgumentException("Local application data directory is required.", nameof(localApplicationDataDirectory));
        }

        HomeDirectory = Path.GetFullPath(homeDirectory);
        GuiDirectory = Path.Combine(HomeDirectory, "gui");
        StateFilePath = Path.Combine(GuiDirectory, "state.json");
        PluginsDirectoryPath = Path.Combine(HomeDirectory, "plugins");
        CredentialsDirectoryPath = Path.Combine(HomeDirectory, "credentials");
        ApiKeyFilePath = Path.Combine(CredentialsDirectoryPath, "deepseek.key");
        _legacyHarnessDirectory = Path.Combine(
            Path.GetFullPath(localApplicationDataDirectory),
            "DeepSeekHarnessGui");
        _legacyKlDirectory = Path.Combine(
            Path.GetFullPath(localApplicationDataDirectory),
            "Kl");
    }

    public string HomeDirectory { get; }
    public string GuiDirectory { get; }
    public string StateFilePath { get; }
    public string PluginsDirectoryPath { get; }
    public string CredentialsDirectoryPath { get; }
    public string ApiKeyFilePath { get; }

    public void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(GuiDirectory);
            Directory.CreateDirectory(PluginsDirectoryPath);
            Directory.CreateDirectory(CredentialsDirectoryPath);
            MigrateGuiPluginsDirectory();
            MigrateLegacyHarnessData();
            MigrateLegacyKlData();
            _initialized = true;
        }
    }

    private void MigrateGuiPluginsDirectory()
    {
        var legacyGuiPlugins = Path.Combine(GuiDirectory, "plugins");
        if (!Directory.Exists(legacyGuiPlugins))
        {
            return;
        }

        try
        {
            CopyDirectoryMissing(legacyGuiPlugins, PluginsDirectoryPath);
            Directory.Delete(legacyGuiPlugins, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the source directory if the plugin files are still in use.
        }
    }

    private void MigrateLegacyHarnessData()
    {
        if (!Directory.Exists(_legacyHarnessDirectory))
        {
            return;
        }

        try
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(_legacyHarnessDirectory))
            {
                var name = Path.GetFileName(item);
                if (Directory.Exists(item) && string.Equals(name, "plugins", StringComparison.OrdinalIgnoreCase))
                {
                    CopyDirectoryMissing(item, PluginsDirectoryPath);
                }
                else
                {
                    CopyEntryMissing(item, Path.Combine(GuiDirectory, name));
                }
            }

            Directory.Delete(_legacyHarnessDirectory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the legacy directory if migration cannot finish; the new path remains canonical.
        }
    }

    private void MigrateLegacyKlData()
    {
        if (!Directory.Exists(_legacyKlDirectory))
        {
            return;
        }

        try
        {
            var legacySettings = Path.Combine(_legacyKlDirectory, "gui-settings.json");
            if (File.Exists(legacySettings))
            {
                var targetSettings = Path.Combine(GuiDirectory, "gui-settings.json");
                if (!File.Exists(targetSettings))
                {
                    File.Copy(legacySettings, targetSettings);
                }
            }

            foreach (var item in Directory.EnumerateFileSystemEntries(_legacyKlDirectory))
            {
                if (string.Equals(Path.GetFileName(item), "gui-settings.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CopyEntryMissing(item, Path.Combine(GuiDirectory, "legacy-appdata", "kl", Path.GetFileName(item)));
            }

            Directory.Delete(_legacyKlDirectory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Preserve the source directory if a legacy item is still in use.
        }
    }

    private static void CopyDirectoryMissing(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Copy(file, destination);
            }
        }
    }

    private static void CopyEntryMissing(string source, string target)
    {
        if (Directory.Exists(source))
        {
            CopyDirectoryMissing(source, target);
        }
        else if (File.Exists(source) && !File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    private static string ResolveKlHome()
    {
        var configured = Environment.GetEnvironmentVariable("KL_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kl");
    }
}

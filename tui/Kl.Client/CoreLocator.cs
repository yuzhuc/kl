namespace Kl.Client;

public static class CoreLocator
{
    public static string Find(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Normalize(explicitPath);
        }

        var fromEnvironment = NormalizeOrNull(Environment.GetEnvironmentVariable("KL_CORE_PATH"));
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var local = NormalizeOrNull(Path.Combine(AppContext.BaseDirectory, ExecutableName));
        if (local is not null)
        {
            return local;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            foreach (var profile in new[] { "debug", "release" })
            {
                var candidate = Path.Combine(directory.FullName, "target", profile, ExecutableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new FileNotFoundException(
            $"could not find {ExecutableName}; build it with `cargo build` or pass --core <path>");
    }

    public static string ExecutableName => OperatingSystem.IsWindows() ? "kl-core.exe" : "kl-core";

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            full = Path.Combine(full, ExecutableName);
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"core executable not found: {full}");
        }

        return full;
    }

    private static string? NormalizeOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Normalize(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}

namespace Kl.Tui.Tests;

/// <summary>
/// Platform-appropriate workspace paths for renderer tests. Each path is
/// absolute and outside the user profile, so the renderer keeps it verbatim
/// on every operating system.
/// </summary>
internal static class TestPaths
{
    public static string Workspace { get; } =
        OperatingSystem.IsWindows() ? @"D:\kl" : "/opt/kl";

    public static string Work { get; } =
        OperatingSystem.IsWindows() ? @"D:\work" : "/opt/work";

    public static string LongWorkspace { get; } = OperatingSystem.IsWindows()
        ? @"D:\workspace-with-a-very-long-name"
        : "/opt/workspace-with-a-very-long-name";

    /// <summary>Root of a path long enough to be center-truncated.</summary>
    public static string LongRoot { get; } =
        OperatingSystem.IsWindows() ? @"D:\" : "/opt/";

    /// <summary>Native directory separator.</summary>
    public static string Separator { get; } = Path.DirectorySeparatorChar.ToString();
}

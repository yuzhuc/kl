using System.Diagnostics;

namespace Kl.Gui.Services;

/// <summary>
/// Registers the GUI in the current user's Windows Run key. Uses reg.exe so the
/// app needs no Windows-only package reference; other platforms report off.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KL";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return Run("query", $"\"{RunKey}\" /v {ValueName}").ExitCode == 0;
    }

    /// <summary>Turns the Run entry on or off; throws when Windows rejects the update.</summary>
    public static void SetEnabled(bool enabled, string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("开机启动仅支持 Windows。");
        }

        var executable = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 KL 可执行文件路径。");
        var (exitCode, output) = enabled
            ? Run("add", $"\"{RunKey}\" /v {ValueName} /t REG_SZ /d \"\\\"{executable}\\\"\" /f")
            : Run("delete", $"\"{RunKey}\" /v {ValueName} /f");
        if (exitCode != 0)
        {
            throw new InvalidOperationException(output.Trim().Length == 0
                ? "无法更新开机启动设置。"
                : output.Trim());
        }
    }

    private static (int ExitCode, string Output) Run(string verb, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = $"{verb} {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("无法启动 reg.exe。");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}

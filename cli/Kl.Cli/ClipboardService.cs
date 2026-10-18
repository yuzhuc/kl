using System.Diagnostics;
using System.Text;

namespace Kl.Cli;

/// <summary>Best-effort clipboard integration without a GUI dependency.</summary>
public static class ClipboardService
{
    public static bool TryCopy(string? text, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            error = "empty";
            return false;
        }

        return OperatingSystem.IsWindows()
            ? TryClipExe(text, out error)
            : TryOsc52(text, out error);
    }

    private static bool TryClipExe(string text, out string error)
    {
        error = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo("clip.exe")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "clip.exe not available";
                return false;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killError)
                    when (killError is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Best effort.
                }
                error = "clip.exe timed out";
                return false;
            }

            if (process.ExitCode != 0)
            {
                error = $"clip.exe exited {process.ExitCode}";
                return false;
            }
            return true;
        }
        catch (Exception errorException)
            when (errorException is IOException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            error = errorException.Message;
            return false;
        }
    }

    private static bool TryOsc52(string text, out string error)
    {
        error = string.Empty;
        try
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            Console.Out.Write("\u001b]52;c;" + payload + "\u0007");
            Console.Out.Flush();
            return true;
        }
        catch (Exception errorException)
            when (errorException is IOException or InvalidOperationException)
        {
            error = errorException.Message;
            return false;
        }
    }
}

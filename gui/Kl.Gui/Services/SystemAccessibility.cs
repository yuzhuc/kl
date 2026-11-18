using System.Runtime.InteropServices;

namespace Kl.Gui.Services;

/// <summary>Reads the Windows "show animations" preference for the 系统 motion mode.</summary>
public static class SystemAccessibility
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        out int value,
        uint update);

    public static bool AnimationsDisabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0)
            && enabled == 0;
    }
}

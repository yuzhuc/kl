using System.Runtime.InteropServices;

namespace Kl.Gui.Services;

/// <summary>
/// Taskbar flash + system beep for turn/approval attention. Windows-only pieces
/// are guarded so the rest of the GUI keeps running on other platforms.
/// </summary>
public static class NotificationEffects
{
    private const uint FlashAll = 0x3;
    private const uint FlashTimerNoForeground = 0xC;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    public static void Attention(nint windowHandle, bool sound)
    {
        if (OperatingSystem.IsWindows())
        {
            if (windowHandle != 0)
            {
                var info = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashInfo>(),
                    Window = windowHandle,
                    Flags = FlashAll | FlashTimerNoForeground,
                    Count = 3,
                    Timeout = 0
                };
                FlashWindowEx(ref info);
            }

            if (sound)
            {
                MessageBeep(0xFFFFFFFF);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }
}

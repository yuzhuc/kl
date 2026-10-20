using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Kl.Tui;

public enum MouseInputKind
{
    LeftDown,
    LeftUp,
    Drag,
    RightDown,
    WheelUp,
    WheelDown
}

public readonly record struct MouseInputEvent(MouseInputKind Kind, int Column, int Row);

public readonly record struct ConsoleInputEvent(ConsoleKeyInfo? Key, MouseInputEvent? Mouse)
{
    public static ConsoleInputEvent FromKey(ConsoleKeyInfo key) => new(key, null);

    public static ConsoleInputEvent FromMouse(MouseInputEvent mouse) => new(null, mouse);
}

/// <summary>Windows console mouse/keyboard input via ReadConsoleInput.</summary>
public static class ConsoleMouseInput
{
    private const int StdInputHandle = -10;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint MouseMoved = 0x0001;
    private const uint MouseWheeled = 0x0004;
    private const uint LeftButton = 0x0001;
    private const uint RightButton = 0x0002;
    private const uint RightAltPressed = 0x0001;
    private const uint LeftAltPressed = 0x0002;
    private const uint RightCtrlPressed = 0x0004;
    private const uint LeftCtrlPressed = 0x0008;
    private const uint ShiftPressed = 0x0010;
    private const string Capture = "\u001b[?1000h\u001b[?1002h\u001b[?1006h";
    private const string Release = "\u001b[?1006l\u001b[?1002l\u001b[?1000l";

    private static IntPtr _inputHandle;
    private static uint _previousMode;
    private static bool _enabled;
    private static bool _modeChanged;
    private static bool _leftDown;
    private static bool _rightDown;

    public static void Enable()
    {
        if (!OperatingSystem.IsWindows() || _enabled)
        {
            return;
        }
        _enabled = true;
        _inputHandle = GetStdHandle(StdInputHandle);
        if (_inputHandle != IntPtr.Zero
            && _inputHandle != new IntPtr(-1)
            && GetConsoleMode(_inputHandle, out var mode))
        {
            _previousMode = mode;
            // ReadConsoleInput needs record mode; SGR capture lets ConPTY report pointer events.
            var requested = ((mode & ~EnableVirtualTerminalInput)
                | EnableExtendedFlags
                | EnableMouseInput) & ~EnableQuickEditMode;
            _modeChanged = requested != mode && SetConsoleMode(_inputHandle, requested);
        }
        Write(Capture);
    }

    public static void Disable()
    {
        if (!OperatingSystem.IsWindows() || !_enabled)
        {
            return;
        }
        _enabled = false;
        Write(Release);
        if (_modeChanged
            && _inputHandle != IntPtr.Zero
            && _inputHandle != new IntPtr(-1))
        {
            SetConsoleMode(_inputHandle, _previousMode);
        }
        _modeChanged = false;
        _leftDown = false;
        _rightDown = false;
    }

    public static void ReadLoop(ChannelWriter<ConsoleInputEvent> writer)
    {
        if (!OperatingSystem.IsWindows())
        {
            ReadKeyLoop(writer);
            return;
        }

        var input = GetStdHandle(StdInputHandle);
        if (input == IntPtr.Zero || input == new IntPtr(-1))
        {
            ReadKeyLoop(writer);
            return;
        }

        while (true)
        {
            try
            {
                if (!ReadConsoleInputW(input, out var record, 1, out var count) || count == 0)
                {
                    break;
                }

                if (record.EventType == 0x0001 && record.KeyEvent.KeyDown != 0)
                {
                    writer.TryWrite(ConsoleInputEvent.FromKey(ToKey(record.KeyEvent)));
                }
                else if (record.EventType == 0x0002
                    && TryMouse(record.MouseEvent, out var mouse))
                {
                    writer.TryWrite(ConsoleInputEvent.FromMouse(mouse));
                }
            }
            catch (Exception error)
                when (error is IOException or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
                break;
            }
        }
        writer.TryComplete();
    }

    private static void ReadKeyLoop(ChannelWriter<ConsoleInputEvent> writer)
    {
        while (true)
        {
            try
            {
                writer.TryWrite(ConsoleInputEvent.FromKey(Console.ReadKey(intercept: true)));
            }
            catch (Exception error)
                when (error is IOException or InvalidOperationException)
            {
                break;
            }
        }
        writer.TryComplete();
    }

    private static ConsoleKeyInfo ToKey(KeyEventRecord record)
    {
        var shift = (record.ControlKeyState & ShiftPressed) != 0;
        var alt = (record.ControlKeyState & (LeftAltPressed | RightAltPressed)) != 0;
        var control = (record.ControlKeyState & (LeftCtrlPressed | RightCtrlPressed)) != 0;
        return new ConsoleKeyInfo(
            record.UnicodeChar,
            (ConsoleKey)record.VirtualKeyCode,
            shift,
            alt,
            control);
    }

    private static bool TryMouse(MouseEventRecord record, out MouseInputEvent mouse)
    {
        mouse = default;
        var left = (record.ButtonState & LeftButton) != 0;
        var right = (record.ButtonState & RightButton) != 0;
        if (record.EventFlags == MouseWheeled)
        {
            var delta = (short)(record.ButtonState >> 16);
            mouse = new MouseInputEvent(
                delta > 0 ? MouseInputKind.WheelUp : MouseInputKind.WheelDown,
                record.Position.X,
                record.Position.Y);
            return delta != 0;
        }

        if (record.EventFlags == MouseMoved)
        {
            if (!left)
            {
                return false;
            }
            mouse = new MouseInputEvent(MouseInputKind.Drag, record.Position.X, record.Position.Y);
            return true;
        }

        var kind = left && !_leftDown
            ? MouseInputKind.LeftDown
            : !left && _leftDown
                ? MouseInputKind.LeftUp
                : right && !_rightDown
                    ? MouseInputKind.RightDown
                    : (MouseInputKind?)null;
        _leftDown = left;
        _rightDown = right;
        if (kind is null)
        {
            return false;
        }
        mouse = new MouseInputEvent(kind.Value, record.Position.X, record.Position.Y);
        return true;
    }

    private static void Write(string value)
    {
        try
        {
            Console.Out.Write(value);
            Console.Out.Flush();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // The terminal may already be gone.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        // Win32 BOOL is four bytes; a managed bool shifts every following field.
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEventRecord
    {
        public Coord Position;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;

        [FieldOffset(4)]
        public MouseEventRecord MouseEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleInputW(
        IntPtr handle,
        out InputRecord record,
        uint length,
        out uint count);
}

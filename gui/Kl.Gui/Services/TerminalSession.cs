using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.Services;

/// <summary>Windows ConPTY session. Starts one shell and exposes its UTF-8 byte stream.</summary>
public sealed class TerminalSession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;

    private IntPtr _pseudoConsole;
    private IntPtr _inputWrite;
    private IntPtr _outputRead;
    private IntPtr _process;
    private IntPtr _thread;
    private FileStream? _input;
    private FileStream? _output;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _disposed;

    public event Action<string>? Output;
    public event Action? Exited;

    public bool IsRunning { get; private set; }
    public string ShellLabel { get; private set; } = string.Empty;

    public void Start(string shell, string workingDirectory, int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException(Loc.T("终端已经在运行。"));
        }

        var spec = ResolveShell(shell);
        ShellLabel = spec.Label;
        var size = new COORD((short)Math.Max(1, columns), (short)Math.Max(1, rows));
        var security = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };
        var securityPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(security, securityPointer, false);

        if (!CreatePipe(out var inputRead, out var inputWrite, securityPointer, 0))
        {
            Marshal.FreeHGlobal(securityPointer);
            throw new InvalidOperationException(Loc.T("无法创建终端输入管道。"));
        }

        if (!CreatePipe(out var outputRead, out var outputWrite, securityPointer, 0))
        {
            Marshal.FreeHGlobal(securityPointer);
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            throw new InvalidOperationException(Loc.T("无法创建终端输出管道。"));
        }

        Marshal.FreeHGlobal(securityPointer);

        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _pseudoConsole);
        if (hr != 0)
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            CloseHandle(outputWrite);
            throw new InvalidOperationException(Loc.F("无法创建 ConPTY（0x{0:X8}）。", (hr)));
        }

        _inputWrite = inputWrite;
        _outputRead = outputRead;

        var attributeList = IntPtr.Zero;
        var processInfo = new PROCESS_INFORMATION();
        try
        {
            var attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            attributeList = Marshal.AllocHGlobal(attributeSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
            {
                throw new InvalidOperationException(Loc.T("无法初始化终端进程属性。"));
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new InvalidOperationException(Loc.T("无法绑定终端伪控制台。"));
            }

            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>()
                },
                lpAttributeList = attributeList
            };

            var commandLine = new StringBuilder($"\"{spec.FileName}\"");
            if (!string.IsNullOrWhiteSpace(spec.Arguments))
            {
                commandLine.Append(' ').Append(spec.Arguments);
            }

            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    Directory.Exists(workingDirectory) ? workingDirectory : null,
                    ref startup,
                    out processInfo))
            {
                throw new InvalidOperationException(
                    Loc.F("无法启动 {0}（错误 {1}）。", (spec.Label), (Marshal.GetLastWin32Error())));
            }

            _process = processInfo.hProcess;
            _thread = processInfo.hThread;
        }
        catch
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
            CloseHandle(inputRead);
            CloseHandle(outputWrite);
            CloseHandle(_inputWrite);
            CloseHandle(_outputRead);
            _inputWrite = IntPtr.Zero;
            _outputRead = IntPtr.Zero;
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }

        CloseHandle(inputRead);
        CloseHandle(outputWrite);

        _input = new FileStream(
            new SafeFileHandle(_inputWrite, ownsHandle: true),
            FileAccess.Write,
            4096,
            isAsync: false);
        _output = new FileStream(
            new SafeFileHandle(_outputRead, ownsHandle: true),
            FileAccess.Read,
            4096,
            isAsync: false);
        _inputWrite = IntPtr.Zero;
        _outputRead = IntPtr.Zero;

        IsRunning = true;
        _readCts = new CancellationTokenSource();
        var token = _readCts.Token;
        _readTask = Task.Run(() => ReadLoop(token));
    }

    public void Write(string text)
    {
        if (_input is null || text.Length == 0)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch (IOException)
        {
            // The shell exited between the keystroke and the write.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_pseudoConsole == IntPtr.Zero)
        {
            return;
        }

        ResizePseudoConsole(
            _pseudoConsole,
            new COORD((short)Math.Max(1, columns), (short)Math.Max(1, rows)));
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[4096];
        var decoder = new UTF8Encoding(false).GetDecoder();
        var chars = new char[8192];
        try
        {
            while (!token.IsCancellationRequested && _output is not null)
            {
                var read = await _output.ReadAsync(buffer.AsMemory(0, buffer.Length), token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                if (count > 0)
                {
                    Output?.Invoke(new string(chars, 0, count));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            IsRunning = false;
            Exited?.Invoke();
        }
    }

    private static (string FileName, string Arguments, string Label) ResolveShell(string shell) =>
        shell switch
        {
            "cmd" => (FindOnPath("cmd.exe") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/K chcp 65001 >nul", Loc.T("命令提示符")),
            "git-bash" => ResolveGitBash(),
            _ => ResolvePowerShell()
        };

    private static (string FileName, string Arguments, string Label) ResolvePowerShell()
    {
        const string args =
            "-NoLogo -NoExit -Command \"[Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.Encoding]::UTF8\"";
        var pwsh = FindOnPath("pwsh.exe");
        if (pwsh is not null)
        {
            return (pwsh, args, "PowerShell");
        }

        var windowsPowerShell = FindOnPath("powershell.exe")
            ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(windowsPowerShell))
        {
            throw new InvalidOperationException(Loc.T("未找到 PowerShell。"));
        }

        return (windowsPowerShell, args, "PowerShell");
    }

    private static (string FileName, string Arguments, string Label) ResolveGitBash()
    {
        var git = FindOnPath("git.exe");
        var candidates = new List<string>();
        if (git is not null)
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, ".."));
            candidates.Add(Path.Combine(root, "bin", "bash.exe"));
            candidates.Add(Path.Combine(root, "usr", "bin", "bash.exe"));
        }

        candidates.Add(@"C:\Program Files\Git\bin\bash.exe");
        candidates.Add(@"C:\Program Files\Git\usr\bin\bash.exe");
        var bash = candidates.FirstOrDefault(File.Exists);
        if (bash is null)
        {
            throw new InvalidOperationException(Loc.T("未找到 Git Bash；请安装 Git for Windows 或改用其他 Shell。"));
        }

        return (bash, "--login -i", "Git Bash");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }


    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsRunning = false;
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;
        _input?.Dispose();
        _input = null;
        _output?.Dispose();
        _output = null;

        if (_process != IntPtr.Zero)
        {
            TerminateProcess(_process, 1);
            CloseHandle(_process);
            _process = IntPtr.Zero;
        }

        if (_thread != IntPtr.Zero)
        {
            CloseHandle(_thread);
            _thread = IntPtr.Zero;
        }

        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        if (_inputWrite != IntPtr.Zero)
        {
            CloseHandle(_inputWrite);
            _inputWrite = IntPtr.Zero;
        }

        if (_outputRead != IntPtr.Zero)
        {
            CloseHandle(_outputRead);
            _outputRead = IntPtr.Zero;
        }

        _readTask = null;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD(short x, short y)
    {
        public short X = x;
        public short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        IntPtr hInput,
        IntPtr hOutput,
        uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        IntPtr pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

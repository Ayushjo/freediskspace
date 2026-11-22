using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Native;

/// <summary>
/// Starts a program with the signed-in user's own rights, never this process's administrator
/// token. The app runs elevated (requireAdministrator), so anything it starts the ordinary way
/// (a document's handler, a terminal) would run as administrator too.
///
/// Elevated, the new process gets the desktop shell's process as its parent through
/// PROC_THREAD_ATTRIBUTE_PARENT_PROCESS. Microsoft documents that the child then inherits "the
/// process token" from that parent rather than from the caller, so it runs at the shell's
/// medium integrity, as the person at the keyboard. Unelevated, the program starts directly.
/// It fails closed: with no shell to take the token from, nothing starts.
/// </summary>
public static class UnelevatedLaunch
{
    /// <summary>True when this process holds an administrator token right now.</summary>
    public static bool IsElevated { get; } = ReadIsElevated();

    /// <summary>
    /// Starts <paramref name="exePath"/> (a full path) with <paramref name="arguments"/> as the
    /// signed-in user. False when it could not.
    /// </summary>
    public static bool TryStart(string exePath, string arguments, string? workingDirectory = null)
    {
        if (!IsElevated)
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(exePath, arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = workingDirectory ?? Environment.SystemDirectory,
                });
                return true;
            }
            catch { return false; }
        }
        using var handle = StartAsShellChild(exePath, arguments, workingDirectory, CREATE_NEW_CONSOLE);
        return handle is not null;
    }

    /// <summary>The desktop shell's process id (Explorer), or 0 when no shell runs.</summary>
    internal static uint ShellProcessId()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(shell, out uint pid);
        return pid;
    }

    /// <summary>
    /// The shell-parented start, whatever this process's elevation (tests call it unelevated).
    /// Returns the new process's handle, or null when any step failed.
    /// </summary>
    internal static SafeProcessHandle? StartAsShellChild(string exePath, string arguments, string? workingDirectory, uint creationFlags)
    {
        uint shellPid = ShellProcessId();
        if (shellPid == 0) return null;
        using var parent = OpenProcess(PROCESS_CREATE_PROCESS, false, shellPid);
        if (parent.IsInvalid) return null;

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr list = Marshal.AllocHGlobal(size);
        IntPtr parentValue = Marshal.AllocHGlobal(IntPtr.Size);
        bool initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) return null;
            initialized = true;
            Marshal.WriteIntPtr(parentValue, parent.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    parentValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                return null;

            var startup = new STARTUPINFOEX { lpAttributeList = list };
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            var commandLine = new StringBuilder("\"" + exePath + "\"" + (arguments.Length > 0 ? " " + arguments : ""));
            if (!CreateProcessW(exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | creationFlags, IntPtr.Zero,
                    workingDirectory ?? Environment.SystemDirectory, ref startup, out var info))
                return null;
            CloseHandle(info.hThread);
            return new SafeProcessHandle(info.hProcess, ownsHandle: true);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(parentValue);
            Marshal.FreeHGlobal(list);
        }
    }

    private static bool ReadIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private const uint PROCESS_CREATE_PROCESS = 0x0080;
    private const int PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;
    internal const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace KronosCLI;

internal static class Injector
{
    public static void Inject(IntPtr process, string dllPath)
    {
        byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");
        IntPtr remote = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)path.Length,
            Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
        if (remote == IntPtr.Zero) throw new Win32Exception();

        try
        {
            if (!Native.WriteProcessMemory(process, remote, path, (UIntPtr)path.Length, out _))
                throw new Win32Exception();

            IntPtr loadLibrary = Native.GetProcAddress(Native.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            IntPtr thread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero) throw new Win32Exception();

            Native.WaitForSingleObject(thread, 15000);
            Native.GetExitCodeThread(thread, out uint moduleLow);
            Native.CloseHandle(thread);
            if (moduleLow == 0)
                throw new Exception("LoadLibraryW returned NULL in the target (bad path or missing dependency)");
        }
        finally
        {
            Native.VirtualFreeEx(process, remote, UIntPtr.Zero, Native.MEM_RELEASE);
        }
    }
}

internal static class Native
{
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;
    public const int INTERNET_OPTION_REFRESH = 37, INTERNET_OPTION_SETTINGS_CHANGED = 39, INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
    public const int INTERNET_PER_CONN_FLAGS = 1, INTERNET_PER_CONN_PROXY_SERVER = 2, INTERNET_PER_CONN_PROXY_BYPASS = 3,
        INTERNET_PER_CONN_AUTOCONFIG_URL = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct INTERNET_PER_CONN_OPTION_LIST
    {
        public int dwSize;
        public IntPtr pszConnection;
        public int dwOptionCount, dwOptionError;
        public IntPtr pOptions;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INTERNET_PER_CONN_OPTION
    {
        [FieldOffset(0)] public int dwOption;
        [FieldOffset(8)] public int dwValue;
        [FieldOffset(8)] public IntPtr pszValue;
    }

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    public static extern bool InternetSetOption(IntPtr h, int option, ref INTERNET_PER_CONN_OPTION_LIST list, int size);

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    public static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int size);

    public const uint CREATE_SUSPENDED = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param,
        uint flags, out uint tid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeThread(IntPtr h, out uint code);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateProcess(IntPtr h, uint code);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
}

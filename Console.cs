using System.Runtime.InteropServices;

namespace KronosCLI;

internal static class Console
{
    const int STD_INPUT_HANDLE = -10;
    const int STD_OUTPUT_HANDLE = -11;
    const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    const uint ENABLE_WINDOW_INPUT = 0x0008;
    const uint ENABLE_MOUSE_INPUT = 0x0010;
    const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    public const ushort KeyEvent = 0x0001;
    public const ushort MouseEvent = 0x0002;

    static uint? originalInputMode;

    [StructLayout(LayoutKind.Explicit)]
    public struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyRecord Key;
        [FieldOffset(4)] public MouseRecord Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKey;
        public ushort ScanCode;
        public ushort Char;
        public uint ControlKeys;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseRecord
    {
        public short X;
        public short Y;
        public uint Buttons;
        public uint ControlKeys;
        public uint Flags;
    }

    public static bool TryEnableAnsi()
    {
        try
        {
            IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out uint mode)) return false;
            return SetConsoleMode(handle, mode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch { return false; }
    }

    public static bool TryEnableMouse()
    {
        try
        {
            IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out uint mode)) return false;

            uint wanted = (mode | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT | ENABLE_EXTENDED_FLAGS)
                & ~(ENABLE_QUICK_EDIT_MODE | ENABLE_VIRTUAL_TERMINAL_INPUT);
            if (!SetConsoleMode(handle, wanted)) return false;

            originalInputMode ??= mode;
            return true;
        }
        catch { return false; }
    }

    public static void RestoreInput()
    {
        if (originalInputMode is not { } mode) return;
        try { SetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), mode | ENABLE_EXTENDED_FLAGS); } catch { }
        originalInputMode = null;
    }

    public static int ReadInput(InputRecord[] buffer)
    {
        try
        {
            return ReadConsoleInputW(GetStdHandle(STD_INPUT_HANDLE), buffer, (uint)buffer.Length, out uint read)
                ? (int)read
                : -1;
        }
        catch { return -1; }
    }

    public static int PendingInput()
    {
        try { return GetNumberOfConsoleInputEvents(GetStdHandle(STD_INPUT_HANDLE), out uint count) ? (int)count : 0; }
        catch { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadConsoleInputW(IntPtr handle, [Out] InputRecord[] buffer, uint length, out uint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);
}

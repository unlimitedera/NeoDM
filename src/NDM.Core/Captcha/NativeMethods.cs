// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Runtime.InteropServices;

namespace NDM.Core.Captcha;

// 1.16.2: the few system calls the real press needs. Kept apart so the rest stays plain code and every caller can be faked.

public static class Native
{
    public const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    private const uint InputMouse = 0, MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000;
    private const int AsfwAny = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    public static int Metrics(int index) => OperatingSystem.IsWindows() ? GetSystemMetrics(index) : 0;
    public static IntPtr Foreground() => OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;
    public static bool SetForeground(IntPtr window) => OperatingSystem.IsWindows() && SetForegroundWindow(window);
    public static bool AllowAnyForeground() => OperatingSystem.IsWindows() && AllowSetForegroundWindow(AsfwAny);

    /// <summary>Sends one mouse event pair. False when the system refused (wrong session, no window station).</summary>
    public static bool Send(int dx, int dy, uint flags)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var input = new[] { new Input { Type = InputMouse, Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags } } };
        return SendInput(1, input, Marshal.SizeOf<Input>()) == 1;
    }

    public static uint MoveFlags => MouseMove | MouseAbsolute | MouseVirtualDesk;
    public static uint DownFlags => MouseLeftDown;
    public static uint UpFlags => MouseLeftUp;
}

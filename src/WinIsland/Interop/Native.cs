using System.Runtime.InteropServices;
using System.Text;

namespace WinIsland.Interop;

internal static class Native
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_BORDER = 0x00800000;
    public const long WS_DLGFRAME = 0x00400000;
    public const long WS_THICKFRAME = 0x00040000;

    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint LWA_ALPHA = 0x2;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public const int MDT_EFFECTIVE_DPI = 0;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    /// <summary>SHQueryUserNotificationState: an exclusive-fullscreen Direct3D app (most games) is running.</summary>
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public readonly bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

        public readonly bool Covers(RECT other) =>
            Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int pquns);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static long GetStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();

    public static void SetStyle(IntPtr hwnd, long style) => SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

    public static long GetExStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

    public static void SetExStyle(IntPtr hwnd, long style) => SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));

    public static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    public static bool TryGetMonitorInfo(IntPtr monitor, out MONITORINFO info)
    {
        info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info);
    }

    public static void SetDwmInt(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}

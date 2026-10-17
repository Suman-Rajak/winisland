using System.Runtime.InteropServices;
using Windows.UI;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>
/// The colour for the bar at the top, read off the screen: the page you're on (the app's own background,
/// as a phone's status bar takes on the app below it), or the taskbar's.
/// </summary>
internal static class AppColors
{
    private const int Inset = 8;  // in from the window's edges (rounded corners, borders)
    private const int Step = 2;   // every other pixel along a line is plenty

    // Lines across the app this far down it (of its height below the bar): past a browser's tabs and
    // toolbars, into the page itself.
    private static readonly double[] s_depths = [0.2, 0.35, 0.5];

    /// <summary>
    /// The colour of the page you're on in <paramref name="window"/>: the commonest colour along a few
    /// lines across it, as it shows on screen. That's its background, not the words or pictures on it.
    /// Null if there's nothing to read.
    /// </summary>
    /// <param name="notAbove">The lines go no higher than this (the bar's bottom).</param>
    /// <param name="skip">Left out where a line crosses it: the island, which can hang down over the app.</param>
    public static Color? PageOf(IntPtr window, int notAbove, Native.RECT skip)
    {
        if (window == IntPtr.Zero || !Native.GetVisibleBounds(window, out var bounds)
            || !Native.TryGetMonitorInfo(Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST), out var monitor))
        {
            return null;
        }

        int top = Math.Max(bounds.Top, notAbove), bottom = Math.Min(bounds.Bottom, monitor.rcMonitor.Bottom);
        int left = Math.Max(bounds.Left, monitor.rcMonitor.Left) + Inset;
        int right = Math.Min(bounds.Right, monitor.rcMonitor.Right) - Inset;
        if (bottom - top < 40 || right - left < 16)
        {
            return null;
        }

        var counts = new Dictionary<int, (int Count, int R, int G, int B)>();
        foreach (double depth in s_depths)
        {
            int y = top + (int)((bottom - top) * depth);
            Count(counts, Capture(left, y, right - left), left, y, skip);
        }
        return Commonest(counts);
    }

    /// <summary>
    /// The taskbar's colour as it shows on screen: the commonest colour along a line just inside its top
    /// edge, above its buttons. Null when it's hidden.
    /// </summary>
    public static Color? Taskbar()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !Native.IsWindowVisible(taskbar) || !Native.GetWindowRect(taskbar, out var bounds)
            || !Native.TryGetMonitorInfo(Native.MonitorFromWindow(taskbar, Native.MONITOR_DEFAULTTONEAREST), out var monitor))
        {
            return null;
        }

        int top = Math.Max(bounds.Top, monitor.rcMonitor.Top), bottom = Math.Min(bounds.Bottom, monitor.rcMonitor.Bottom);
        if (bottom - top < 12)
        {
            return null; // tucked away (it hides itself)
        }

        int left = Math.Max(bounds.Left, monitor.rcMonitor.Left), right = Math.Min(bounds.Right, monitor.rcMonitor.Right);
        int y = top + 4;
        var counts = new Dictionary<int, (int Count, int R, int G, int B)>();
        Count(counts, Capture(left, y, right - left), left, y, default);
        return Commonest(counts);
    }

    /// <summary>Counts a line's colours, each rounded to 16 steps a channel.</summary>
    private static void Count(Dictionary<int, (int Count, int R, int G, int B)> counts, byte[]? line, int left, int y, Native.RECT skip)
    {
        if (line is null)
        {
            return;
        }

        bool inSkip = y >= skip.Top && y < skip.Bottom;
        for (int x = 0; x < line.Length / 4; x += Step)
        {
            if (inSkip && left + x >= skip.Left && left + x < skip.Right)
            {
                continue;
            }

            int i = x * 4;
            byte b = line[i], g = line[i + 1], r = line[i + 2];
            int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            var (count, sr, sg, sb) = counts.GetValueOrDefault(key);
            counts[key] = (count + 1, sr + r, sg + g, sb + b);
        }
    }

    /// <summary>The most common colour counted, averaged within its step.</summary>
    private static Color? Commonest(Dictionary<int, (int Count, int R, int G, int B)> counts)
    {
        if (counts.Count == 0)
        {
            return null;
        }

        var (most, r, g, b) = counts.Values.MaxBy(c => c.Count);
        return Color.FromArgb(255, (byte)(r / most), (byte)(g / most), (byte)(b / most));
    }

    /// <summary>How light a colour looks, 0 (black) to 1 (white).</summary>
    public static double Lightness(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    /// <summary>One line of the screen, as BGRA pixels.</summary>
    private static byte[]? Capture(int left, int y, int width)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -1, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero)
            {
                return null;
            }

            var old = SelectObject(memory, bitmap);
            bool copied = BitBlt(memory, 0, 0, width, 1, screen, left, y, SRCCOPY);
            SelectObject(memory, old);
            if (!copied)
            {
                return null;
            }

            var pixels = new byte[width * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint op);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr item);
}

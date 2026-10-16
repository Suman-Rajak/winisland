using System.Runtime.InteropServices;
using Windows.UI;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>
/// The colours of the app you're in, for the bar above it to wear, as a phone's status bar takes on the
/// app below it: the app's own accent, from its icon (Chrome's, VS Code's blue, Claude's orange), or
/// failing that the colour along its top (its title bar or tabs), read off the screen.
/// </summary>
internal static class AppColors
{
    private const int Inset = 6;  // down from the window's top edge, into its title bar
    private const int Step = 2;   // every other pixel along the line is plenty

    private const int IconPixels = 48;
    private const int Hues = 24;  // the colour wheel in slices, to find the icon's main colour

    private static readonly Dictionary<string, Color?> s_accents = [];

    /// <summary>
    /// An app's accent: its icon's main colour (of its colourful parts, the hue covering most of it),
    /// made rich enough for a bar (neither neon nor murky). Null for an icon that's grey, black or white.
    /// Call on the UI thread; remembered per app.
    /// </summary>
    /// <param name="icon">Where the icon comes from (see <see cref="ForegroundApp.Icon"/>).</param>
    public static Color? AccentOf(string? icon)
    {
        if (icon is null)
        {
            return null;
        }
        if (s_accents.TryGetValue(icon, out var known))
        {
            return known;
        }

        Color? accent = ShellIcons.Pixels(icon, IconPixels) is { } pixels ? MainColour(pixels.Bgra) : null;
        if (s_accents.Count > 100)
        {
            s_accents.Clear();
        }
        return s_accents[icon] = accent;
    }

    private static Color? MainColour(byte[] bgra)
    {
        var weight = new double[Hues];
        var r = new double[Hues];
        var g = new double[Hues];
        var b = new double[Hues];
        int solid = 0, colourful = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int alpha = bgra[i + 3];
            if (alpha < 160)
            {
                continue; // the icon's see-through edges and background
            }
            solid++;

            // (the shell's pixels have their alpha multiplied in)
            double pr = Math.Min(1, bgra[i + 2] / (double)alpha), pg = Math.Min(1, bgra[i + 1] / (double)alpha), pb = Math.Min(1, bgra[i] / (double)alpha);
            var (h, s, l) = ColorMath.ToHsl(pr, pg, pb);
            if (s < 0.35 || l < 0.15 || l > 0.85)
            {
                continue; // grey, black or white: outlines, shading, highlights
            }

            colourful++;
            int slice = (int)(h * Hues) % Hues;
            weight[slice] += s;
            r[slice] += pr * s;
            g[slice] += pg * s;
            b[slice] += pb * s;
        }
        if (colourful == 0 || colourful < solid * 0.08)
        {
            return null;
        }

        // Neighbouring slices count together: a gradient of one colour is still one colour.
        int best = Enumerable.Range(0, Hues).MaxBy(i => weight[(i + Hues - 1) % Hues] + weight[i] + weight[(i + 1) % Hues]);
        double total = 0, sr = 0, sg = 0, sb = 0;
        foreach (int i in new[] { (best + Hues - 1) % Hues, best, (best + 1) % Hues })
        {
            total += weight[i];
            sr += r[i];
            sg += g[i];
            sb += b[i];
        }

        var (hue, saturation, lightness) = ColorMath.ToHsl(sr / total, sg / total, sb / total);
        return ColorMath.FromHsl(hue, Math.Clamp(saturation, 0.4, 0.75), Math.Clamp(lightness, 0.36, 0.52));
    }

    /// <summary>
    /// The commonest colour along a line just inside the top of <paramref name="window"/> as it shows on
    /// screen: the title bar's own colour, not its text or icons. Null if there's nothing to read.
    /// </summary>
    /// <param name="notAbove">The line is no higher than this (the bar's bottom), so the bar isn't read instead.</param>
    /// <param name="skip">Left out where the line crosses it: the island, which can hang down over the app.</param>
    public static Color? TopOf(IntPtr window, int notAbove, Native.RECT skip)
    {
        if (window == IntPtr.Zero || !Native.GetVisibleBounds(window, out var bounds)
            || !Native.TryGetMonitorInfo(Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST), out var monitor))
        {
            return null;
        }

        int y = Math.Max(bounds.Top, notAbove) + Inset;
        int left = Math.Max(bounds.Left, monitor.rcMonitor.Left) + Inset; // (clear of rounded corners and borders)
        int right = Math.Min(bounds.Right, monitor.rcMonitor.Right) - Inset;
        if (y >= Math.Min(bounds.Bottom, monitor.rcMonitor.Bottom) || right - left < 16)
        {
            return null;
        }

        var line = Capture(left, y, right - left);
        if (line is null)
        {
            return null;
        }

        // Colours rounded to 16 steps a channel, counted: the most common is the background.
        var counts = new Dictionary<int, (int Count, int R, int G, int B)>();
        bool inSkip = y >= skip.Top && y < skip.Bottom;
        for (int x = 0; x < right - left; x += Step)
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
        if (counts.Count == 0)
        {
            return null;
        }

        var (most, rs, gs, bs) = counts.Values.MaxBy(c => c.Count);
        return Color.FromArgb(255, (byte)(rs / most), (byte)(gs / most), (byte)(bs / most));
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

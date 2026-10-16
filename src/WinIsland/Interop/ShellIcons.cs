using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinIsland.Interop;

/// <summary>An icon as the shell draws it, as BGRA pixels (premultiplied alpha), top row first.</summary>
internal sealed record IconPixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// The icons the shell draws for files and apps: a program or shortcut by its path, or an installed app
/// by its entry in the Applications folder (<c>shell:AppsFolder\…</c>). Call on the UI thread.
/// </summary>
internal static class ShellIcons
{
    /// <summary>The icon, about <paramref name="size"/> pixels square; null if the shell has none.</summary>
    public static IconPixels? Pixels(string source, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
        {
            return null;
        }

        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out bitmap) != 0)
            {
                return null;
            }
            return Read(bitmap);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            Log.Error($"Drawing the icon of {source} failed", ex);
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>The icon as something XAML can show.</summary>
    public static WriteableBitmap? Bitmap(string source, int size)
    {
        if (Pixels(source, size) is not { } icon)
        {
            return null;
        }

        var image = new WriteableBitmap(icon.Width, icon.Height);
        using (var stream = image.PixelBuffer.AsStream())
        {
            stream.Write(icon.Bgra, 0, icon.Bgra.Length);
        }
        image.Invalidate();
        return image;
    }

    private static IconPixels? Read(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0)
        {
            return null;
        }

        int width = info.bmWidth, height = info.bmHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var screen = GetDC(IntPtr.Zero);
        try
        {
            return GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0 ? null : new IconPixels(pixels, width, height);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr bitmap, int size, out BITMAP info);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}

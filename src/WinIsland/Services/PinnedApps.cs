using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinIsland.Services;

/// <summary>An app pinned to the taskbar: its shortcut.</summary>
internal sealed record PinnedApp(string Name, string Shortcut);

/// <summary>
/// The apps pinned to the taskbar, for the menu bar: the shortcuts Windows keeps for them, with the
/// icons the shell draws for them.
/// </summary>
internal static partial class PinnedApps
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    public static List<PinnedApp> Read()
    {
        try
        {
            return Directory.Exists(Folder)
                ? [.. Directory.GetFiles(Folder, "*.lnk")
                    .Select(path => new PinnedApp(NameOf(path), path))
                    .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Reading the taskbar's pinned apps failed", ex);
            return [];
        }
    }

    /// <summary>When the pins last changed, to know when to read them again.</summary>
    public static DateTime ChangedAt() => Directory.Exists(Folder) ? Directory.GetLastWriteTimeUtc(Folder) : default;

    public static void Open(PinnedApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.Shortcut) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Log.Error($"Opening {app.Name} failed", ex);
        }
    }

    /// <summary>"File Explorer (5).lnk" → "File Explorer".</summary>
    private static string NameOf(string path) => CopyNumber().Replace(Path.GetFileNameWithoutExtension(path), string.Empty);

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex CopyNumber();

    // ---------------------------------------------------------------- icons

    /// <summary>The shell's icon for a file, <paramref name="size"/> pixels square. Call on the UI thread.</summary>
    public static WriteableBitmap? Icon(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
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
            return ToWriteableBitmap(bitmap);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            Log.Error($"Drawing the icon of {path} failed", ex);
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

    /// <summary>The shell's bitmap (32-bit, premultiplied alpha) as pixels XAML can show.</summary>
    private static WriteableBitmap? ToWriteableBitmap(IntPtr bitmap)
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
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        var image = new WriteableBitmap(width, height);
        using (var stream = image.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }
        image.Invalidate();
        return image;
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

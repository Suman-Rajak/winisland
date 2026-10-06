using System.Runtime.InteropServices;

namespace WinIsland.Interop;

/// <param name="DeviceName">Stable-ish identifier such as <c>\\.\DISPLAY2</c>; what settings remember.</param>
internal sealed record DisplayInfo(IntPtr Handle, string DeviceName, Native.RECT Bounds, Native.RECT WorkArea, bool IsPrimary);

internal static class Displays
{
    public static List<DisplayInfo> All()
    {
        var displays = new List<DisplayInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                displays.Add(new DisplayInfo(monitor, info.szDevice, info.rcMonitor, info.rcWork,
                    (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);

        // Primary first, then left to right, so "Display 2" means what people expect.
        return [.. displays.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top)];
    }

    /// <summary>The display with this device name, or the primary display when it's null or unplugged.</summary>
    public static DisplayInfo? FindOrPrimary(string? deviceName)
    {
        var all = All();
        return all.FirstOrDefault(d => deviceName is not null && d.DeviceName == deviceName)
            ?? all.FirstOrDefault(d => d.IsPrimary)
            ?? all.FirstOrDefault();
    }
}

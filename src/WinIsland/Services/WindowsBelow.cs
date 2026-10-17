using System.Runtime.InteropServices;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>What's under the island: an app's window (a maximized browser's tabs, say), or just the desktop.</summary>
internal static class WindowsBelow
{
    private static readonly uint s_ownProcess = (uint)Environment.ProcessId;

    /// <summary>
    /// An app's window lies under a point on the screen, below the island's own window. Going down the
    /// windows in front-to-back order, the first one that's really shown there decides: the desktop and
    /// taskbar aren't apps, nor are Glyde's own windows (the bar the notch hangs in and the menu bar on it),
    /// and click-through overlays (other widgets) are looked through.
    /// </summary>
    public static bool AppAt(IntPtr island, Native.POINT point)
    {
        const long ClickThrough = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
        for (var window = Native.GetWindow(island, Native.GW_HWNDNEXT); window != IntPtr.Zero; window = Native.GetWindow(window, Native.GW_HWNDNEXT))
        {
            if (!Native.IsWindowVisible(window) || Native.IsIconic(window) || Native.IsCloaked(window)
                || (Native.GetExStyle(window) & ClickThrough) == ClickThrough
                || !Native.GetVisibleBounds(window, out var bounds) || !bounds.Contains(point))
            {
                continue;
            }

            GetWindowThreadProcessId(window, out uint process);
            return process != s_ownProcess && !FullscreenDetector.ShellClasses.Contains(Native.GetClassName(window));
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}

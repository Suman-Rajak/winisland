using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>What's under the island: an app's window (a maximized browser's tabs, say), or just the desktop.</summary>
internal static class WindowsBelow
{
    /// <summary>
    /// An app's window lies under a point on the screen, below the island's own window. Going down the
    /// windows in front-to-back order, the first one that's really shown there decides: the desktop and
    /// taskbar aren't apps, nor is the island's own black bar (<paramref name="bar"/>), and click-through
    /// overlays (other widgets) are looked through.
    /// </summary>
    public static bool AppAt(IntPtr island, Native.POINT point, IntPtr bar)
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
            return window != bar && !FullscreenDetector.ShellClasses.Contains(Native.GetClassName(window));
        }
        return false;
    }
}

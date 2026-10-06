using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>Detects games, fullscreen video and presentations so the island can get out of the way.</summary>
internal static class FullscreenDetector
{
    private static readonly HashSet<string> s_shellClasses = new(StringComparer.Ordinal)
    {
        "Progman",        // desktop
        "WorkerW",        // desktop wallpaper host
        "Shell_TrayWnd",  // taskbar
        "Shell_SecondaryTrayWnd",
    };

    public static bool IsFullscreenAppOn(IntPtr monitor, IntPtr ownWindow)
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownWindow || foreground == Native.GetShellWindow())
        {
            return false;
        }

        if (Native.SHQueryUserNotificationState(out int state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN)
        {
            return true;
        }

        // A maximized window also covers the monitor when the taskbar auto-hides; that is not fullscreen.
        if (Native.IsZoomed(foreground) || s_shellClasses.Contains(Native.GetClassName(foreground)))
        {
            return false;
        }

        if (Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST) != monitor ||
            !Native.TryGetMonitorInfo(monitor, out var info) ||
            !Native.GetWindowRect(foreground, out var bounds))
        {
            return false;
        }

        return bounds.Covers(info.rcMonitor);
    }
}

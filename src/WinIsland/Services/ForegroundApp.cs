using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>
/// The app you're in, for the menu bar (as a Mac's shows the frontmost app's name): its window and a
/// name for it, followed as the foreground changes. Glyde's own island and bar, the taskbar and the
/// Start menu don't count: the last real app stays. Create it on the UI thread.
/// </summary>
internal sealed class ForegroundApp : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_MINIMIZEBOX = 0x00020000;
    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // The shell's own windows: the Start menu, search, quick settings and such.
    private static readonly HashSet<string> s_shellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "ShellHost", "LockApp",
    };

    private static readonly uint s_ownProcess = (uint)Environment.ProcessId;

    private readonly WinEventProc _onForeground; // kept alive: Windows holds a pointer to it
    private readonly Dictionary<uint, (string Exe, string Name)> _processes = [];
    private IntPtr _hook;

    public ForegroundApp()
    {
        _onForeground = (_, _, window, _, _, _, _) => Follow(window);
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        Follow(Native.GetForegroundWindow());
        if (Window == IntPtr.Zero)
        {
            Follow(FrontApp()); // Glyde itself is in front, just started: the app it came up over
        }
    }

    /// <summary>
    /// Stops following (Glyde is quitting): as its windows close the foreground moves, and the news would
    /// otherwise reach windows and timers already shut down.
    /// </summary>
    public void Dispose()
    {
        Changed = null;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The app window nearest the front, going down the windows: an ordinary one, that can be minimized
    /// (not a notification popping up over everything, say).
    /// </summary>
    private IntPtr FrontApp()
    {
        const uint GW_HWNDNEXT = 2;
        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = Native.GetWindow(window, GW_HWNDNEXT))
        {
            if (Native.IsWindowVisible(window) && !Native.IsIconic(window) && !Native.IsCloaked(window)
                && (Native.GetStyle(window) & WS_MINIMIZEBOX) != 0
                && (Native.GetExStyle(window) & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST)) == 0
                && NameOf(window) is not null)
            {
                return window;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>The app's window (the desktop's, when that's what you're on).</summary>
    public IntPtr Window { get; private set; }

    public string Name { get; private set; } = "Desktop";

    public bool IsDesktop { get; private set; } = true;

    public event Action? Changed;

    /// <summary>Looks again (a Store app's title, which is its name, comes a moment after its window).</summary>
    public void Refresh() => Follow(Native.GetForegroundWindow());

    private void Follow(IntPtr window)
    {
        if (window == IntPtr.Zero || NameOf(window) is not { } name)
        {
            return; // not an app: keep the last one
        }

        bool desktop = Native.GetClassName(window) is "Progman" or "WorkerW";
        if (window == Window && name == Name)
        {
            return;
        }

        Window = window;
        Name = name;
        IsDesktop = desktop;
        Changed?.Invoke();
    }

    private string? NameOf(IntPtr window)
    {
        string type = Native.GetClassName(window);
        GetWindowThreadProcessId(window, out uint process);
        if (process == s_ownProcess)
        {
            // Settings has a title bar; the island, the bar and their menus don't.
            return (Native.GetStyle(window) & WS_CAPTION) == WS_CAPTION ? Product.Name : null;
        }
        if (type is "Progman" or "WorkerW")
        {
            return "Desktop";
        }
        if (FullscreenDetector.ShellClasses.Contains(type))
        {
            return null; // the taskbar
        }
        if (type == "CabinetWClass")
        {
            return "File Explorer";
        }

        var (exe, name) = Describe(process);
        if (s_shellHosts.Contains(exe))
        {
            return null;
        }
        if (exe.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            string title = TitleOf(window); // a Store app's frame: its title is the app's name
            return title.Length > 0 ? title : null;
        }
        return name;
    }

    /// <summary>The process's program name and the name it gives itself ("chrome", "Google Chrome").</summary>
    private (string Exe, string Name) Describe(uint process)
    {
        if (_processes.TryGetValue(process, out var known))
        {
            return known;
        }

        string exe = string.Empty, name = string.Empty;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var path = new StringBuilder(1024);
                int length = path.Capacity;
                if (QueryFullProcessImageName(handle, 0, path, ref length))
                {
                    exe = Path.GetFileNameWithoutExtension(path.ToString());
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(path.ToString());
                        name = (string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription)?.Trim() ?? string.Empty;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // (a Store app's folder can't always be read)
                    }
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        if (name.Length == 0)
        {
            name = exe.Length > 0 ? char.ToUpperInvariant(exe[0]) + exe[1..] : "App";
        }
        if (_processes.Count > 200)
        {
            _processes.Clear(); // (process ids get reused)
        }
        return _processes[process] = (exe, name);
    }

    // ---------------------------------------------------------------- what the app menu does

    public static bool IsMaximized(IntPtr window) => Native.IsZoomed(window);

    public static void Minimize(IntPtr window) => ShowWindowAsync(window, SW_MINIMIZE);

    public static void ToggleMaximize(IntPtr window) => ShowWindowAsync(window, Native.IsZoomed(window) ? SW_RESTORE : SW_MAXIMIZE);

    /// <summary>Asks the window to close, as its close button does (so it can ask to save first).</summary>
    public static void Close(IntPtr window) => PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>"Hide others": minimizes every other app window that can be.</summary>
    public static void MinimizeOthers(IntPtr keep)
    {
        var others = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (window != keep && Native.IsWindowVisible(window) && !Native.IsIconic(window) && !Native.IsCloaked(window)
                && (Native.GetStyle(window) & WS_MINIMIZEBOX) != 0
                && (Native.GetExStyle(window) & Native.WS_EX_TOOLWINDOW) == 0)
            {
                GetWindowThreadProcessId(window, out uint process);
                if (process != s_ownProcess)
                {
                    others.Add(window);
                }
            }
            return true;
        }, IntPtr.Zero);

        foreach (var window in others)
        {
            ShowWindowAsync(window, SW_MINIMIZE);
        }
    }

    private static string TitleOf(IntPtr window)
    {
        var title = new StringBuilder(256);
        return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString().Trim() : string.Empty;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

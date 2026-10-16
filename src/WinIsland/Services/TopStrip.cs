using System.Runtime.InteropServices;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>
/// A frosted bar across the top of the island's display, as tall as the island at rest, with the island
/// in it as a black notch: the Mac's menu bar round its notch. Windows keeps it as it keeps the bottom
/// for the taskbar (it's an "app bar"), taking it out of the work area, so maximized and snapped windows
/// (and new ones) start below it.
/// </summary>
internal sealed class TopStrip
{
    private const uint ABM_NEW = 0;
    private const uint ABM_REMOVE = 1;
    private const uint ABM_QUERYPOS = 2;
    private const uint ABM_SETPOS = 3;
    private const uint ABN_POSCHANGED = 1;
    private const uint ABE_TOP = 1;
    private const uint CallbackMessage = 0x8000 + 0x4C1; // WM_APP + n: how Windows tells app bars to look again

    private const int BLACK_BRUSH = 4;
    private const int SW_HIDE = 0;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint GW_HWNDPREV = 3;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_GRADIENT = 1;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    // The frost's tint over the blurred wallpaper (as 0xAABBGGRR), as the taskbar follows the Windows
    // theme; solid when Windows' transparency effects are off.
    private const uint DarkFrost = 0x88201C1C;
    private const uint LightFrost = 0x99F3F3F3;
    private const uint DarkSolid = 0xFF201C1C;
    private const uint LightSolid = 0xFFF3F3F3;

    private static readonly uint s_taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    // Painted black, which the frost treats as see-through (and if Windows won't frost it, it's simply black).
    private readonly MessageWindow _window = new("TopStrip", topLevel: true, background: GetStockObject(BLACK_BRUSH));
    private bool _registered;
    private bool _loggedFailure;
    private Native.RECT _monitor;
    private int _height;
    private Native.RECT _bar;
    private uint _frost;

    public TopStrip()
    {
        _window.MessageReceived += OnMessage;
        Native.SetDwmInt(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
    }

    /// <summary>The bar's window: under the island, it's the island's, not an app's (see <see cref="WindowsBelow"/>).</summary>
    public IntPtr Handle => _window.Handle;

    /// <summary>
    /// The top of the bar (physical pixels) while it's kept: the top of the display, or below another
    /// bar there (a taskbar docked at the top). Null when it isn't kept.
    /// </summary>
    public int? Top { get; private set; }

    /// <summary>Where the bar is (physical pixels) while it's kept: for the menu bar over it.</summary>
    public Native.RECT? Bounds => _registered ? _bar : null;

    /// <summary>Keeps the top <paramref name="height"/> pixels of this display (its bounds) for the island.</summary>
    public void Keep(Native.RECT monitor, int height)
    {
        if (_registered && monitor.Equals(_monitor) && height == _height)
        {
            return;
        }

        _monitor = monitor;
        _height = height;
        if (!_registered)
        {
            var data = Data();
            _registered = SHAppBarMessage(ABM_NEW, ref data) != 0;
            if (!_registered)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Log.Info("Couldn't keep the top of the screen for the island");
                }
                return;
            }
        }

        Place();
    }

    /// <summary>Gives the top back to the windows. True if it was kept.</summary>
    public bool Release()
    {
        if (!_registered)
        {
            return false;
        }

        ShowWindow(Handle, SW_HIDE);
        var data = Data();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
        Top = null;
        return true;
    }

    /// <summary>
    /// Shows the bar while it's kept, always just under the island (both stay above other windows, as the
    /// taskbar does), or hides it: out of the way of a full-screen app, say.
    /// </summary>
    /// <param name="below">The window just above it: the island's, or the menu bar's over it.</param>
    public void Show(bool show, IntPtr below)
    {
        if (!show || !_registered)
        {
            if (Native.IsWindowVisible(Handle))
            {
                ShowWindow(Handle, SW_HIDE);
            }
            return;
        }

        Frost();
        bool placed = Native.IsWindowVisible(Handle) && (Native.GetExStyle(Handle) & Native.WS_EX_TOPMOST) != 0 && IsBelow(below);
        if (!placed)
        {
            // Put after the (topmost) island, it's topmost too, but never over it.
            Native.SetWindowPos(Handle, below, _bar.Left, _bar.Top, _bar.Width, _bar.Height,
                Native.SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    /// <summary>Frosts the bar to suit the Windows theme, when that changed.</summary>
    private void Frost()
    {
        using var personalize = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = personalize?.GetValue("SystemUsesLightTheme") is int l && l != 0;
        bool clear = personalize?.GetValue("EnableTransparency") is not int t || t != 0;
        uint frost = clear ? (light ? LightFrost : DarkFrost) : (light ? LightSolid : DarkSolid);
        if (frost == _frost)
        {
            return;
        }

        _frost = frost;
        var accent = new AccentPolicy
        {
            AccentState = clear ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_GRADIENT,
            GradientColor = frost,
        };
        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            SizeOfData = Marshal.SizeOf<AccentPolicy>(),
        };
        var pointer = Marshal.AllocHGlobal(data.SizeOfData);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            data.Data = pointer;
            if (!SetWindowCompositionAttribute(Handle, ref data))
            {
                Log.Info("Couldn't frost the bar at the top; it stays black");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private bool IsBelow(IntPtr window)
    {
        for (var above = Native.GetWindow(Handle, GW_HWNDPREV); above != IntPtr.Zero; above = Native.GetWindow(above, GW_HWNDPREV))
        {
            if (above == window)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Asks Windows where the bar can go (below any other bar at the top), then takes it.</summary>
    private void Place()
    {
        if (!_registered)
        {
            return;
        }

        var data = Data();
        data.rc = new Native.RECT { Left = _monitor.Left, Top = _monitor.Top, Right = _monitor.Right, Bottom = _monitor.Top + _height };
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.Bottom = data.rc.Top + _height;
        SHAppBarMessage(ABM_SETPOS, ref data);
        Top = data.rc.Top;
        _bar = data.rc;

        Native.SetWindowPos(Handle, IntPtr.Zero, _bar.Left, _bar.Top, _bar.Width, _bar.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage && (uint)wParam == ABN_POSCHANGED)
        {
            Place(); // another bar, the taskbar or the display changed
        }
        else if (message == s_taskbarCreated && _registered)
        {
            // Explorer restarted and forgot every app bar: ask again.
            _registered = false;
            Keep(_monitor, _height);
        }
    }

    private APPBARDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = Handle,
        uCallbackMessage = CallbackMessage,
        uEdge = ABE_TOP,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    /// <summary>Undocumented, but how the taskbar and many apps get acrylic on a plain Win32 window.</summary>
    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);
}

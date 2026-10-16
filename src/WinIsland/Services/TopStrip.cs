using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.UI;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>
/// A bar across the top of the island's display, as tall as the island at rest, with the island in it as
/// a black notch: the Mac's menu bar round its notch. It wears the colour of the app you're in, a little
/// see-through over the blurred wallpaper. Windows keeps it as it keeps the bottom
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

    // The bar wears the colour of the app you're in (see Tint), only a little see-through: mostly its
    // colour, over the blurred wallpaper. Solid when Windows' transparency effects are off.
    private const byte SeeThrough = 0xD9;

    // Until there's an app colour, or for an app without one: the Windows theme's, as the taskbar's.
    private static readonly Color Dark = Color.FromArgb(255, 0x1C, 0x1C, 0x20);
    private static readonly Color Light = Color.FromArgb(255, 0xF3, 0xF3, 0xF3);

    // Going from one app's colour to the next: a short fade.
    private const int FadeSteps = 8;
    private const int FadeStepMs = 25;

    private static readonly uint s_taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    // Painted black, which the frost treats as see-through (and if Windows won't frost it, it's simply black).
    private readonly MessageWindow _window = new("TopStrip", topLevel: true, background: GetStockObject(BLACK_BRUSH));
    private readonly DispatcherQueueTimer _fade;
    private bool _registered;
    private bool _loggedFailure;
    private bool _loggedColour;
    private Native.RECT _monitor;
    private int _height;
    private Native.RECT _bar;
    private Color? _app;   // the app's colour, if there is one
    private bool? _clear;  // frosted (transparency effects on) or solid, as last applied
    private Color _shown, _from, _to;
    private int _step;

    public TopStrip()
    {
        _window.MessageReceived += OnMessage;
        Native.SetDwmInt(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        _fade = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _fade.Interval = TimeSpan.FromMilliseconds(FadeStepMs);
        _fade.Tick += (_, _) => FadeStep();
    }

    /// <summary>
    /// The bar takes on the colour of the app you're in (see <see cref="AppColors"/>), as a phone's status
    /// bar does; null goes back to the Windows theme's.
    /// </summary>
    public void Tint(Color? app)
    {
        _app = app;
        if (Native.IsWindowVisible(Handle))
        {
            Frost();
        }
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

    /// <summary>Colours the bar: the app's colour, or the Windows theme's; fading over when it changes.</summary>
    private void Frost()
    {
        using var personalize = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = personalize?.GetValue("SystemUsesLightTheme") is int l && l != 0;
        bool clear = personalize?.GetValue("EnableTransparency") is not int t || t != 0;
        var colour = _app ?? (light ? Light : Dark);

        if (clear != _clear)
        {
            _clear = clear; // (the first time, or transparency switched: straight there)
            _fade.Stop();
            _shown = _to = colour;
            Apply(colour);
        }
        else if (colour != _to)
        {
            _from = _shown;
            _to = colour;
            _step = 0;
            if (Motion.Enabled)
            {
                _fade.Start();
            }
            else
            {
                _step = FadeSteps;
                FadeStep();
            }
        }
    }

    private void FadeStep()
    {
        _step = Math.Min(_step + 1, FadeSteps);
        float t = _step / (float)FadeSteps;
        t = 1 - (1 - t) * (1 - t); // (easing out)
        _shown = Color.FromArgb(255,
            (byte)Math.Round(_from.R + (_to.R - _from.R) * t),
            (byte)Math.Round(_from.G + (_to.G - _from.G) * t),
            (byte)Math.Round(_from.B + (_to.B - _from.B) * t));
        Apply(_shown);
        if (_step >= FadeSteps)
        {
            _fade.Stop();
        }
    }

    private void Apply(Color colour)
    {
        uint alpha = _clear == true ? SeeThrough : 0xFFu;
        var accent = new AccentPolicy
        {
            AccentState = _clear == true ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_GRADIENT,
            GradientColor = alpha << 24 | (uint)colour.B << 16 | (uint)colour.G << 8 | colour.R, // (0xAABBGGRR)
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
            if (!SetWindowCompositionAttribute(Handle, ref data) && !_loggedColour)
            {
                _loggedColour = true;
                Log.Info("Couldn't colour the bar at the top; it stays black");
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

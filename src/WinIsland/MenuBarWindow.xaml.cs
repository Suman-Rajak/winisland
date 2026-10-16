using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Networking.Connectivity;
using Windows.System.Power;
using Windows.UI;
using WinIsland.Interop;
using WinIsland.Services;
using WinRT.Interop;
using WinUIEx;

namespace WinIsland;

/// <summary>
/// The menu bar beside the notch, as a Mac has round its notch: a see-through window over the frosted
/// bar (see <see cref="TopStrip"/>), just under the island. It never takes the keyboard from the app
/// you're in, except while one of its menus is open, and gives it back after.
/// </summary>
public sealed partial class MenuBarWindow : Window
{
    private const int MaxApps = 8;
    private const int IconPixels = 32; // drawn larger than shown (16), for sharp icons at any scale

    private static readonly Color Charging = Color.FromArgb(255, 0x34, 0xC7, 0x59);
    private static readonly Color Low = Color.FromArgb(255, 0xFF, 0x45, 0x3A);

    private readonly IntPtr _hwnd;
    private readonly ForegroundApp _app = new();
    private readonly List<FlyoutBase> _flyouts = [];
    private List<PinnedApp> _pins = [];
    private DateTime _pinsChangedAt;
    private bool _shown;
    private bool _everShown;
    private Native.RECT _bar;
    private IntPtr _focusFrom;      // the window that had the keyboard before a menu opened
    private FlyoutBase? _open;
    private Action? _afterClose;    // done once the menu's closed and the keyboard's back where it was
    private IntPtr _menuApp;        // the app the app menu is for
    private bool _showingVolume;    // moving the slider to the volume, not you moving it
    private bool? _light;
    private int _refreshes;

    /// <summary>"Glyde Settings…".</summary>
    public event Action? SettingsRequested;

    /// <summary>The microphone button: talk to Glyde.</summary>
    public event Action? TalkRequested;

    public MenuBarWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        SystemBackdrop = new TransparentTintBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;

        foreach (var button in new[] { GlydeButton, AppButton, GoButton, SoundButton, BatteryButton })
        {
            var flyout = button.Flyout;
            _flyouts.Add(flyout);
            flyout.Opening += (_, _) => TakeFocus(flyout);
            flyout.Closed += (_, _) => GiveFocusBack(flyout);
        }
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _open?.Hide(); // you clicked away
            }
        };

        _app.Changed += ShowApp;
        NetworkInformation.NetworkStatusChanged += _ => DispatcherQueue.TryEnqueue(ShowWifi);
        Root.SizeChanged += (_, _) => FitToSpace();

        ShowApp();
        LoadApps();
        ShowWifi();
        Refresh();
    }

    public IntPtr Handle => _hwnd;

    public bool IsShown => _shown;

    // ---------------------------------------------------------------- the window

    /// <summary>
    /// Shows the bar over <paramref name="bar"/> (physical pixels) just under <paramref name="below"/>
    /// (the island), or hides it (null).
    /// </summary>
    internal void Place(Native.RECT? bar, IntPtr below)
    {
        if (bar is not { } rect)
        {
            if (_shown)
            {
                _open?.Hide();
                AppWindow.Hide();
                _shown = false;
            }
            return;
        }

        if (!_everShown)
        {
            // The first time, the window has to be activated to come up at all: give the keyboard straight back.
            _everShown = true;
            var had = Native.GetForegroundWindow();
            Activate();
            ApplyWin32Styles();
            if (had != IntPtr.Zero && had != _hwnd && Native.IsWindow(had))
            {
                Native.SetForegroundWindow(had);
            }
        }
        else if (!_shown)
        {
            AppWindow.Show(activateWindow: false);
        }
        _shown = true;

        bool placed = rect.Equals(_bar) && (Native.GetExStyle(_hwnd) & Native.WS_EX_TOPMOST) != 0 && IsBelow(below);
        if (!placed)
        {
            _bar = rect;
            Native.SetWindowPos(_hwnd, below, rect.Left, rect.Top, rect.Width, rect.Height, Native.SWP_NOACTIVATE);
        }
    }

    private void ApplyWin32Styles()
    {
        Native.SetStyle(_hwnd, Native.GetStyle(_hwnd) & ~(Native.WS_BORDER | Native.WS_DLGFRAME | Native.WS_THICKFRAME));
        var exStyle = Native.GetExStyle(_hwnd);
        exStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        exStyle &= ~Native.WS_EX_APPWINDOW;
        Native.SetExStyle(_hwnd, exStyle);
        Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LWA_ALPHA);
        Native.SetDwmInt(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        Native.SetDwmInt(_hwnd, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    private bool IsBelow(IntPtr window)
    {
        const uint GW_HWNDPREV = 3;
        for (var above = Native.GetWindow(_hwnd, GW_HWNDPREV); above != IntPtr.Zero; above = Native.GetWindow(above, GW_HWNDPREV))
        {
            if (above == window)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A menu's opening: it gets the keyboard (to use it, and so clicking away closes it).</summary>
    private void TakeFocus(FlyoutBase flyout)
    {
        _open = flyout;
        var foreground = Native.GetForegroundWindow();
        if (foreground != _hwnd)
        {
            _focusFrom = foreground;
        }
        Native.SetExStyle(_hwnd, Native.GetExStyle(_hwnd) & ~Native.WS_EX_NOACTIVATE);
        if (!Native.SetForegroundWindow(_hwnd))
        {
            uint ours = Native.GetCurrentThreadId();
            uint theirs = Native.GetWindowThreadProcessId(foreground, IntPtr.Zero);
            if (theirs != 0 && theirs != ours && Native.AttachThreadInput(ours, theirs, true))
            {
                Native.SetForegroundWindow(_hwnd);
                Native.AttachThreadInput(ours, theirs, false);
            }
        }
    }

    /// <summary>The menu's closed: the keyboard goes back to the app it came from, then whatever was chosen is done.</summary>
    private void GiveFocusBack(FlyoutBase flyout)
    {
        if (_open == flyout)
        {
            _open = null;
        }
        Native.SetExStyle(_hwnd, Native.GetExStyle(_hwnd) | Native.WS_EX_NOACTIVATE);
        if (Native.GetForegroundWindow() == _hwnd && _focusFrom != IntPtr.Zero && Native.IsWindow(_focusFrom))
        {
            Native.SetForegroundWindow(_focusFrom);
        }
        _focusFrom = IntPtr.Zero;

        var after = _afterClose;
        _afterClose = null;
        after?.Invoke();
    }

    /// <summary>
    /// Opening something from a menu: it should come to the front, so the keyboard doesn't go back to the
    /// app you were in.
    /// </summary>
    private void Launch(Action open)
    {
        _focusFrom = IntPtr.Zero;
        open();
    }

    // ---------------------------------------------------------------- keeping it up to date

    /// <summary>Every half second or so, while it shows: the time, sound and battery, the theme; now and then Wi-Fi and the pinned apps.</summary>
    public void Refresh()
    {
        _refreshes++;
        ShowTheme();
        ShowClock();
        ShowSound();
        ShowBattery();
        if (_refreshes % 10 == 0)
        {
            ShowWifi(); // (the signal changes without a network change)
            _app.Refresh();
        }
        if (_refreshes % 60 == 0 && PinnedApps.ChangedAt() != _pinsChangedAt)
        {
            LoadApps();
        }
    }

    /// <summary>Light or dark, as the taskbar is (and the frost under the bar).</summary>
    private void ShowTheme()
    {
        using var personalize = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = personalize?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        if (light != _light)
        {
            _light = light;
            var theme = light ? ElementTheme.Light : ElementTheme.Dark;
            Root.RequestedTheme = theme;
            foreach (var flyout in _flyouts)
            {
                // (menus open in windows of their own, which don't inherit it)
                var themed = new Setter(FrameworkElement.RequestedThemeProperty, theme);
                if (flyout is MenuFlyout menu)
                {
                    menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter)) { Setters = { themed } };
                }
                else if (flyout is Flyout panel)
                {
                    panel.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)) { Setters = { themed } };
                }
            }
        }
    }

    private void ShowClock()
    {
        var now = DateTime.Now;
        string text = $"{now:ddd d MMM}   {now.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}";
        if (ClockText.Text != text)
        {
            ClockText.Text = text;
        }
    }

    private void ShowApp()
    {
        AppName.Text = _app.Name;
        ToolTipService.SetToolTip(AppButton, _app.Name);
    }

    // ---------------------------------------------------------------- left: Glyde's menu

    private void OnAboutClick(object sender, RoutedEventArgs e) => Launch(() => SystemActions.Open("ms-settings:about"));

    private void OnWindowsSettingsClick(object sender, RoutedEventArgs e) => Launch(() => SystemActions.Open("ms-settings:"));

    private void OnGlydeSettingsClick(object sender, RoutedEventArgs e) => Launch(() => SettingsRequested?.Invoke());

    private void OnTaskManagerClick(object sender, RoutedEventArgs e) => Launch(() => SystemActions.Open("taskmgr.exe"));

    private void OnSleepClick(object sender, RoutedEventArgs e) => _afterClose = SystemActions.Sleep;

    private void OnShutDownClick(object sender, RoutedEventArgs e) => Launch(SystemActions.AskToShutDown);

    private void OnLockClick(object sender, RoutedEventArgs e) => _afterClose = () => SystemActions.Lock();

    // ---------------------------------------------------------------- left: the app you're in

    private void OnAppMenuOpening(object? sender, object e)
    {
        _menuApp = _app.Window;
        bool window = !_app.IsDesktop && Native.IsWindow(_menuApp);
        bool maximized = window && ForegroundApp.IsMaximized(_menuApp);
        MinimizeItem.Text = $"Hide {_app.Name}";
        MaximizeItem.Text = maximized ? "Restore" : "Maximize";
        MaximizeIcon.Glyph = maximized ? "" : "";
        CloseItem.Text = $"Close {_app.Name}";
        MinimizeItem.Visibility = MaximizeItem.Visibility = CloseItem.Visibility = CloseSeparator.Visibility =
            window ? Visibility.Visible : Visibility.Collapsed;
    }

    // Done once the menu's closed and the app has the keyboard back, as if you'd used its own buttons.
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => _afterClose = () => ForegroundApp.Minimize(_menuApp);

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => _afterClose = () => ForegroundApp.ToggleMaximize(_menuApp);

    private void OnHideOthersClick(object sender, RoutedEventArgs e) => _afterClose = () => ForegroundApp.MinimizeOthers(_menuApp);

    private void OnCloseClick(object sender, RoutedEventArgs e) => _afterClose = () => ForegroundApp.Close(_menuApp);

    // ---------------------------------------------------------------- left: Go

    private void OnGoClick(object sender, RoutedEventArgs e)
    {
        string? tag = (sender as FrameworkElement)?.Tag as string;
        string? place = tag switch
        {
            "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "pc" => "shell:MyComputerFolder",
            "bin" => "shell:RecycleBinFolder",
            _ => null,
        };
        if (place is not null)
        {
            Launch(() => SystemActions.Open("explorer.exe", $"\"{place}\""));
        }
    }

    // ---------------------------------------------------------------- right: pinned apps

    private void LoadApps()
    {
        _pinsChangedAt = PinnedApps.ChangedAt();
        _pins = PinnedApps.Read();
        Apps.Children.Clear();
        foreach (var pin in _pins.Take(MaxApps))
        {
            var button = new Button { Padding = new Thickness(6, 0, 6, 0) };
            button.Content = PinnedApps.Icon(pin.Shortcut, IconPixels) is { } icon
                ? new Image { Source = icon, Width = 16, Height = 16 }
                : new TextBlock { Text = pin.Name[..1], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            ToolTipService.SetToolTip(button, pin.Name);
            button.Click += (_, _) => PinnedApps.Open(pin);
            Apps.Children.Add(button);
        }
        FitToSpace();
    }

    /// <summary>Everything either side of the notch has to fit beside it: pinned apps make room first, from the end.</summary>
    private void FitToSpace()
    {
        double side = (Root.ActualWidth - Root.Padding.Left - Root.Padding.Right - NotchColumn.ActualWidth) / 2;
        if (side <= 0)
        {
            return;
        }

        foreach (var app in Apps.Children)
        {
            app.Visibility = Visibility.Visible;
        }
        var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        Right.Measure(unbounded);
        for (int i = Apps.Children.Count - 1; i >= 0 && Right.DesiredSize.Width > side; i--)
        {
            Apps.Children[i].Visibility = Visibility.Collapsed;
            Right.Measure(unbounded);
        }

        AppName.MaxWidth = Math.Clamp(side - 110, 40, 260); // (beside Glyde's menu and Go)
    }

    // ---------------------------------------------------------------- right: Glyde, Wi-Fi

    private void OnTalkClick(object sender, RoutedEventArgs e) => TalkRequested?.Invoke();

    private void OnWifiClick(object sender, RoutedEventArgs e) => SystemActions.Open("ms-availablenetworks:");

    private async void ShowWifi()
    {
        var (glyph, tip) = await Task.Run(() =>
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile is null)
                {
                    return ("", "Not connected");
                }

                bool online = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
                string suffix = online ? string.Empty : " (no internet)";
                if (!profile.IsWlanConnectionProfile)
                {
                    return ("", $"Ethernet{suffix}");
                }

                string? network = null;
                try
                {
                    network = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                }
                catch (Exception)
                {
                    // (Windows can keep the network's name to apps allowed your location)
                }
                string glyph = (profile.GetSignalBars() ?? 5) switch
                {
                    <= 1 => "",
                    2 => "",
                    3 => "",
                    _ => "",
                };
                return (glyph, $"Wi-Fi: {(string.IsNullOrEmpty(network) ? profile.ProfileName : network)}{suffix}");
            }
            catch (Exception ex)
            {
                Log.Error("Reading the network failed", ex);
                return ("", "Wi-Fi");
            }
        });

        WifiIcon.Glyph = glyph;
        ToolTipService.SetToolTip(WifiButton, tip);
    }

    // ---------------------------------------------------------------- right: sound

    private static string SoundGlyph(float level, bool muted) => muted || level <= 0 ? ""
        : level < 0.34f ? ""
        : level < 0.67f ? ""
        : "";

    private void ShowSound()
    {
        if (SystemActions.GetVolume() is not { } level)
        {
            SoundButton.Visibility = Visibility.Collapsed; // no speakers
            return;
        }

        bool muted = SystemActions.IsMuted() == true;
        SoundButton.Visibility = Visibility.Visible;
        SoundIcon.Glyph = SoundGlyph(level, muted);
        ToolTipService.SetToolTip(SoundButton, muted ? "Sound: muted" : $"Sound: {level * 100:0}%");

        if (_open == SoundFlyout)
        {
            ShowVolumeInFlyout(level, muted);
        }
    }

    private void OnSoundOpening(object? sender, object e)
    {
        if (SystemActions.GetVolume() is { } level)
        {
            ShowVolumeInFlyout(level, SystemActions.IsMuted() == true);
        }
    }

    private void ShowVolumeInFlyout(float level, bool muted)
    {
        SoundPercent.Text = muted ? "Muted" : $"{level * 100:0}%";
        MuteIcon.Glyph = SoundGlyph(level, muted);
        ToolTipService.SetToolTip(MuteButton, muted ? "Unmute" : "Mute");
        if (Math.Abs(VolumeSlider.Value - level * 100) >= 1)
        {
            _showingVolume = true;
            VolumeSlider.Value = Math.Round(level * 100);
            _showingVolume = false;
        }
    }

    private void OnVolumeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_showingVolume)
        {
            return;
        }
        SystemActions.SetVolume((float)(e.NewValue / 100));
        ShowSound();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        SystemActions.SetMute(SystemActions.IsMuted() != true);
        ShowSound();
        if (SystemActions.GetVolume() is { } level)
        {
            ShowVolumeInFlyout(level, SystemActions.IsMuted() == true);
        }
    }

    /// <summary>Scrolling over the speaker turns the sound up and down, 2% a notch.</summary>
    private void OnSoundWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(SoundButton).Properties.MouseWheelDelta;
        if (delta != 0 && SystemActions.GetVolume() is { } level)
        {
            SystemActions.SetVolume(Math.Clamp(level + Math.Sign(delta) * 0.02f, 0, 1));
            ShowSound();
            e.Handled = true;
        }
    }

    private void OnSoundSettingsClick(object sender, RoutedEventArgs e) => Launch(() =>
    {
        SoundFlyout.Hide();
        SystemActions.Open("ms-settings:sound");
    });

    // ---------------------------------------------------------------- right: battery, the time

    private void ShowBattery()
    {
        if (PowerManager.BatteryStatus == BatteryStatus.NotPresent)
        {
            BatteryButton.Visibility = Visibility.Collapsed; // a desktop PC
            return;
        }

        int percent = PowerManager.RemainingChargePercent;
        bool pluggedIn = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        bool charging = PowerManager.BatteryStatus == BatteryStatus.Charging;
        BatteryButton.Visibility = Visibility.Visible;
        BatteryPercent.Text = $"{percent}%";
        BatteryFill.Width = Math.Max(1.5, 18 * percent / 100.0);
        var fill = charging ? Charging
            : percent <= 20 && !pluggedIn ? Low
            : _light == true ? Color.FromArgb(0xE6, 0, 0, 0) : Color.FromArgb(0xF2, 255, 255, 255); // (as BarForeground)
        if (BatteryFill.Background is not SolidColorBrush { } brush || brush.Color != fill)
        {
            BatteryFill.Background = new SolidColorBrush(fill);
        }
        ChargingBolt.Visibility = pluggedIn ? Visibility.Visible : Visibility.Collapsed;

        string state = charging ? "Charging"
            : pluggedIn ? (percent >= 99 ? "Fully charged" : "Plugged in, not charging")
            : PowerManager.RemainingDischargeTime is { TotalMinutes: > 0 and < 24 * 60 } left
                ? $"On battery · about {(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{left.Minutes} min")} left"
                : "On battery";
        BatteryTitle.Text = $"Battery {percent}%";
        BatteryState.Text = state;
        ToolTipService.SetToolTip(BatteryButton, $"Battery {percent}% · {state}");
    }

    private void OnBatterySettingsClick(object sender, RoutedEventArgs e) => Launch(() =>
    {
        _open?.Hide();
        SystemActions.Open("ms-settings:batterysaver");
    });

    private void OnClockClick(object sender, RoutedEventArgs e) => SystemActions.Open("ms-actioncenter:");
}

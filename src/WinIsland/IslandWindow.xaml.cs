using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinIsland.Activities;
using WinIsland.Interop;
using WinIsland.Services;
using WinRT.Interop;
using WinUIEx;

namespace WinIsland;

/// <summary>
/// The island itself: a transparent, topmost window with an animated pill in it. What the pill shows
/// comes from <see cref="IslandActivity"/> objects; this class decides which one to show and handles
/// the window, hover, animation and glass.
/// </summary>
public sealed partial class IslandWindow : Window
{
    // The window is a fixed-size transparent stage; only the pill inside it is visible and clickable.
    // Resizing the window itself during animations would jitter, so the pill animates inside it instead.
    private const float StageWidth = 480;
    private const float StageHeight = 200;
    private const float TopGap = 8;

    private const int PointerTickMs = 30;
    private const int SlowTickEvery = 16; // ~0.5 s
    private const int ExpandDelayMs = 90;
    private const int CollapseDelayMs = 280;

    private static readonly PillShape IdleShape = new(124, 32, 16);

    private readonly IslandOptions _options;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly IntPtr _hwnd;
    private readonly Compositor _compositor;
    private readonly Visual _stageVisual;
    private readonly CompositionRoundedRectangleGeometry _pill;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<IslandActivity> _activities;
    private readonly Dictionary<FrameworkElement, bool> _layerVisible = [];

    private IslandState _state = IslandState.Idle;
    private IslandActivity? _shown;
    private bool _wantExpanded;
    private bool _visible;
    private bool _userHidden;
    private bool _idleHidden;
    private long _insideSince;
    private long _outsideSince;
    private int _tick;

    private Color _tint = ColorMath.NeutralTint;
    private ImageSource? _glow;
    private int _glowVersion;
    private bool _glowFading;

    private IntPtr _monitor;
    private Native.POINT _origin;
    private int _hitTop;
    private double _scale = 1;

    internal IslandWindow(IslandOptions options)
    {
        _options = options;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureWindow();

        _stageVisual = ElementCompositionPreview.GetElementVisual(Stage);
        _compositor = _stageVisual.Compositor;
        _pill = CreatePillGeometry();
        _stageVisual.Clip = _compositor.CreateGeometricClip(_pill);
        CreateSheen();

        var volume = new VolumeActivity(DispatcherQueue);
        var battery = new BatteryActivity(DispatcherQueue);
        var bluetooth = new BluetoothActivity(DispatcherQueue);
        _activities =
        [
            new ClockActivity(DispatcherQueue),
            new MediaActivity(DispatcherQueue, options.DemoMedia),
            volume,
            new BrightnessActivity(DispatcherQueue),
            battery,
            bluetooth,
        ];
        foreach (var activity in _activities)
        {
            AddLayer(activity.CompactView, activity.CompactShape);
            AddLayer(activity.ExpandedView, activity.ExpandedShape);
            activity.Changed += (_, _) => OnActivityChanged();
        }

        if (options.DemoLevel)
        {
            volume.Pin(0.45);
        }
        if (options.DemoBattery)
        {
            battery.Pin(64);
        }
        if (options.DemoBluetooth)
        {
            bluetooth.Pin("OnePlus Nord Buds 3", 80);
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(PointerTickMs);
        _timer.Tick += OnTick;

        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Set from the tray menu.</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            UpdateVisibility();
        }
    }

    public void Start()
    {
        Activate();
        ApplyWin32Styles();
        UpdatePlacement();
        _visible = true;
        UpdateVisibility();
        EnsureTopmost();
        UpdateState();
        ApplyState();
        _timer.Start();

        foreach (var activity in _activities)
        {
            _ = StartActivityAsync(activity);
        }
    }

    private static async Task StartActivityAsync(IslandActivity activity)
    {
        try
        {
            await activity.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"{activity.Name} failed to start", ex);
        }
    }

    private void OnActivityChanged()
    {
        UpdateState();
        ApplyTheme();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _monitor = IntPtr.Zero; // force UpdatePlacement to re-apply display and distance
        UpdatePlacement();
        UpdateVisibility();
        UpdateState();
        ApplyTheme();
        ApplyIdleFade();
    }

    // ---------------------------------------------------------------- window setup

    private void ConfigureWindow()
    {
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
    }

    /// <summary>Raw Win32 tweaks WinUI has no API for. WinUI rewrites styles on activation, so call this after Activate().</summary>
    private void ApplyWin32Styles()
    {
        // SetBorderAndTitleBar(false, false) still leaves a dialog frame, which draws as a white outline.
        Native.SetStyle(_hwnd, Native.GetStyle(_hwnd) & ~(Native.WS_BORDER | Native.WS_DLGFRAME | Native.WS_THICKFRAME));

        // Layered + transparent lets clicks fall through to whatever is underneath; we flip
        // WS_EX_TRANSPARENT off only while the cursor is over the pill. NOACTIVATE keeps clicks
        // on the island from stealing focus from the app you're typing in.
        var exStyle = Native.GetExStyle(_hwnd);
        exStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
        exStyle &= ~Native.WS_EX_APPWINDOW;
        Native.SetExStyle(_hwnd, exStyle);
        Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LWA_ALPHA);

        // No Windows 11 rounded corners or accent border around the invisible window.
        Native.SetDwmInt(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        Native.SetDwmInt(_hwnd, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE);

        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    private CompositionRoundedRectangleGeometry CreatePillGeometry()
    {
        var pill = _compositor.CreateRoundedRectangleGeometry();
        pill.Size = new Vector2(IdleShape.Width, IdleShape.Height);
        pill.CornerRadius = new Vector2(IdleShape.Radius);

        // Keep the pill horizontally centred while its size springs around.
        var centre = _compositor.CreateExpressionAnimation("Vector2((stageWidth - pill.Size.X) / 2, topGap)");
        centre.SetScalarParameter("stageWidth", StageWidth);
        centre.SetScalarParameter("topGap", TopGap);
        centre.SetReferenceParameter("pill", pill);
        pill.StartAnimation(nameof(pill.Offset), centre);

        return pill;
    }

    /// <summary>
    /// The "glass" part: a highlight fading down from the top edge and a thin rim that is brighter
    /// at the top, like light catching the lip of a glass. Both follow the animated pill geometry.
    /// </summary>
    private void CreateSheen()
    {
        var highlight = _compositor.CreateLinearGradientBrush();
        highlight.StartPoint = new Vector2(0, 0);
        highlight.EndPoint = new Vector2(0, 1);
        highlight.ColorStops.Add(_compositor.CreateColorGradientStop(0f, Color.FromArgb(40, 255, 255, 255)));
        highlight.ColorStops.Add(_compositor.CreateColorGradientStop(0.55f, Color.FromArgb(0, 255, 255, 255)));

        var rim = _compositor.CreateLinearGradientBrush();
        rim.StartPoint = new Vector2(0, 0);
        rim.EndPoint = new Vector2(0, 1);
        rim.ColorStops.Add(_compositor.CreateColorGradientStop(0f, Color.FromArgb(110, 255, 255, 255)));
        rim.ColorStops.Add(_compositor.CreateColorGradientStop(1f, Color.FromArgb(28, 255, 255, 255)));

        var shape = _compositor.CreateSpriteShape(_pill);
        shape.FillBrush = highlight;
        shape.StrokeBrush = rim;
        shape.StrokeThickness = 2f; // half of it falls outside the pill clip, leaving a 1 px rim

        var sheen = _compositor.CreateShapeVisual();
        sheen.Size = new Vector2(StageWidth, StageHeight);
        sheen.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(SheenHost, sheen);
    }

    /// <summary>Adds an activity's view to the stage, sized to its pill and hidden.</summary>
    private void AddLayer(FrameworkElement? view, PillShape shape)
    {
        if (view is null)
        {
            return;
        }

        view.Width = shape.Width;
        view.Height = shape.Height;
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Top;
        view.Margin = new Thickness(0, TopGap, 0, 0);
        view.IsHitTestVisible = false;
        ElementCompositionPreview.GetElementVisual(view).Opacity = 0;
        ContentHost.Children.Add(view);
        _layerVisible[view] = false;
    }

    // ---------------------------------------------------------------- placement & visibility

    private void UpdatePlacement()
    {
        if (Displays.FindOrPrimary(_settings.Display) is not { } display)
        {
            return;
        }

        Native.GetDpiForMonitor(display.Handle, Native.MDT_EFFECTIVE_DPI, out uint dpi, out _);
        double scale = dpi / 96.0;
        int width = (int)Math.Ceiling(StageWidth * scale);
        int height = (int)Math.Ceiling(StageHeight * scale);

        // Centre on the screen, below the taskbar if it's docked at the top. The pill sits TopGap
        // inside the window, so a smaller distance pushes the (invisible) top of the window off-screen.
        var origin = new Native.POINT
        {
            X = display.Bounds.Left + (display.Bounds.Width - width) / 2,
            Y = display.WorkArea.Top + (int)Math.Round((_settings.DistanceFromTop - TopGap) * scale),
        };

        if (display.Handle == _monitor && origin.X == _origin.X && origin.Y == _origin.Y && scale == _scale)
        {
            return;
        }

        _monitor = display.Handle;
        _origin = origin;
        _hitTop = display.WorkArea.Top;
        _scale = scale;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, origin.X, origin.Y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void UpdateVisibility()
    {
        bool visible = !_userHidden && !FullscreenDetector.IsFullscreenAppOn(_monitor, _hwnd);
        if (visible == _visible)
        {
            return;
        }

        _visible = visible;
        if (visible)
        {
            AppWindow.Show(activateWindow: false);
            EnsureTopmost();
        }
        else
        {
            AppWindow.Hide();
        }
    }

    /// <summary>
    /// WinUI's presenter can lose topmost when it reacts to our frame-style change, and it filters out
    /// raw SetWindowPos(HWND_TOPMOST) calls that disagree with its own state. So reclaim topmost
    /// through the presenter, by toggling IsAlwaysOnTop so it re-applies it.
    /// </summary>
    private void EnsureTopmost()
    {
        if (!_visible || (Native.GetExStyle(_hwnd) & Native.WS_EX_TOPMOST) != 0)
        {
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsAlwaysOnTop = true;
        }

        if ((Native.GetExStyle(_hwnd) & Native.WS_EX_TOPMOST) == 0 && !_loggedTopmostFailure)
        {
            _loggedTopmostFailure = true;
            Log.Info("Could not reclaim topmost");
        }
    }

    private bool _loggedTopmostFailure;

    // ---------------------------------------------------------------- pointer & state

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        // Exceptions thrown from a timer tick vanish silently; log the first of each kind.
        try
        {
            Tick();
        }
        catch (Exception ex) when (s_loggedTickErrors.Add(ex.GetType().FullName + ex.StackTrace))
        {
            Log.Error("Island tick failed", ex);
        }
        catch
        {
            // Already logged this one.
        }
    }

    private static readonly HashSet<string> s_loggedTickErrors = [];

    private void Tick()
    {
        if (++_tick % SlowTickEvery == 0)
        {
            UpdatePlacement();
            UpdateVisibility();
            EnsureTopmost();
        }

        if (!_visible)
        {
            return;
        }

        Native.GetCursorPos(out var cursor);
        bool inside = HitRect(CurrentShape()).Contains(cursor);
        SetClickThrough(!inside);

        long now = Environment.TickCount64;
        if (inside)
        {
            _outsideSince = 0;
            if (_insideSince == 0) _insideSince = now;
        }
        else
        {
            _insideSince = 0;
            if (_outsideSince == 0) _outsideSince = now;
        }

        bool wantExpanded = _state == IslandState.Expanded
            ? inside || now - _outsideSince < CollapseDelayMs
            : inside && now - _insideSince >= ExpandDelayMs;

        if (wantExpanded != _wantExpanded)
        {
            _wantExpanded = wantExpanded;
            UpdateState();
        }
    }

    /// <summary>
    /// Picks what to show: on hover, the highest-priority activity with an expanded view; otherwise the
    /// highest-priority one that wants the compact pill; otherwise nothing (the idle pill).
    /// </summary>
    private void UpdateState()
    {
        var compact = _activities.Where(a => a.WantsCompact).MaxBy(a => a.Priority);
        var expanded = _activities.Where(a => a.HasExpanded).MaxBy(a => a.Priority);

        var state = _options.PinnedState
            ?? (_wantExpanded && expanded is not null ? IslandState.Expanded
                : compact is not null ? IslandState.Compact
                : IslandState.Idle);
        var shown = state switch
        {
            IslandState.Expanded => expanded,
            IslandState.Compact => compact,
            _ => null,
        };

        if (state == _state && shown == _shown)
        {
            return;
        }

        _state = state;
        _shown = shown;
        ApplyState();
    }

    private PillShape CurrentShape() => _state switch
    {
        IslandState.Expanded when _shown is not null => _shown.ExpandedShape,
        IslandState.Compact when _shown is not null => _shown.CompactShape,
        _ => IdleShape,
    };

    /// <summary>Screen rectangle (physical pixels) that counts as "on the island".</summary>
    private Native.RECT HitRect(PillShape shape)
    {
        const float pad = 4;
        float left = (StageWidth - shape.Width) / 2 - pad;
        float right = left + shape.Width + pad * 2;
        float bottom = TopGap + shape.Height + pad;

        // The top edge extends to the screen edge so flinging the mouse upward still hits the island.
        return new Native.RECT
        {
            Left = _origin.X + (int)(left * _scale),
            Top = _hitTop,
            Right = _origin.X + (int)(right * _scale),
            Bottom = _origin.Y + (int)(bottom * _scale),
        };
    }

    private void SetClickThrough(bool clickThrough)
    {
        // Read the real style rather than caching it: WinUI occasionally rewrites the extended styles.
        var style = Native.GetExStyle(_hwnd);
        if (((style & Native.WS_EX_TRANSPARENT) != 0) == clickThrough)
        {
            return;
        }

        Native.SetExStyle(_hwnd, clickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT);
    }

    // ---------------------------------------------------------------- animation

    private void ApplyState()
    {
        AnimatePill(CurrentShape(), expanding: _state == IslandState.Expanded);

        foreach (var activity in _activities)
        {
            bool compact = _state == IslandState.Compact && activity == _shown;
            bool expanded = _state == IslandState.Expanded && activity == _shown;
            SetLayerVisible(activity.CompactView, compact);
            SetLayerVisible(activity.ExpandedView, expanded);
            activity.OnShown(compact, expanded);
        }

        ApplyIdleFade();
        ApplyTheme();
    }

    /// <summary>"Hide when idle": fade the whole island out, but keep hover working so it can come back.</summary>
    private void ApplyIdleFade()
    {
        bool hide = _settings.HideWhenIdle && _state == IslandState.Idle && _options.PinnedState is null;
        if (hide == _idleHidden)
        {
            return;
        }

        _idleHidden = hide;
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, hide ? 0f : 1f);
        fade.Duration = Scaled(hide ? 300 : 120);
        _stageVisual.StartAnimation(nameof(_stageVisual.Opacity), fade);
    }

    /// <summary>A duration adjusted for the "animation speed" setting.</summary>
    private TimeSpan Scaled(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds * _settings.AnimationScale);

    private void AnimatePill(PillShape shape, bool expanding)
    {
        // Growing is a little bouncy, like the iPhone's island; shrinking settles faster.
        float damping = expanding ? 0.68f : 0.85f;
        var period = Scaled(expanding ? 55 : 45);

        StartSpring(nameof(_pill.Size), new Vector2(shape.Width, shape.Height), damping, period);
        StartSpring(nameof(_pill.CornerRadius), new Vector2(shape.Radius), damping, period);
    }

    private void StartSpring(string property, Vector2 target, float damping, TimeSpan period)
    {
        var spring = _compositor.CreateSpringVector2Animation();
        spring.FinalValue = target;
        spring.DampingRatio = damping;
        spring.Period = period;
        _pill.StartAnimation(property, spring);
    }

    private void SetLayerVisible(FrameworkElement? layer, bool show)
    {
        // Re-running the fade on a layer that's already showing would restart it and flicker.
        if (layer is null || _layerVisible[layer] == show)
        {
            return;
        }

        _layerVisible[layer] = show;
        layer.IsHitTestVisible = show;
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        visual.CenterPoint = new Vector3((float)layer.Width / 2, 0, 0);

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, show ? 1f : 0f);
        fade.Duration = Scaled(show ? 220 : 110);
        if (show)
        {
            fade.DelayTime = Scaled(60); // let the pill start changing shape first
        }

        var scale = _compositor.CreateSpringVector3Animation();
        scale.FinalValue = show ? Vector3.One : new Vector3(0.92f, 0.92f, 1f);
        scale.DampingRatio = 0.8f;
        scale.Period = Scaled(50);

        visual.StartAnimation(nameof(visual.Opacity), fade);
        visual.StartAnimation(nameof(visual.Scale), scale);
    }

    // ---------------------------------------------------------------- glass

    /// <summary>
    /// Colours the glass from the activity being shown or, failing that, from whichever live activity
    /// has colours (so music keeps its colours while the volume indicator is up). Neutral otherwise,
    /// or when "match the music's colours" is off.
    /// </summary>
    private async void ApplyTheme()
    {
        var theme = !_settings.MatchMusicColors ? null
            : _shown?.Theme
              ?? _activities.Where(a => a.WantsCompact && a.Theme is not null).MaxBy(a => a.Priority)?.Theme;

        Backdrop.Opacity = _settings.GlassOpacity;
        ((SolidColorBrush)Application.Current.Resources["IslandAccentBrush"]).Color =
            theme?.Accent ?? Color.FromArgb(255, 255, 255, 255);

        var tint = theme?.Tint ?? ColorMath.NeutralTint;
        if (tint != _tint)
        {
            _tint = tint;
            Backdrop.Background = new SolidColorBrush(tint); // a new brush, so BackgroundTransition animates it
        }

        var glow = theme?.Glow;
        if (ReferenceEquals(glow, _glow))
        {
            if (!_glowFading)
            {
                GlowLayer.Opacity = glow is null ? 0 : 0.9;
            }
            return;
        }

        // Crossfade: fade the old colour wash out before swapping in the new one.
        _glow = glow;
        int version = ++_glowVersion;
        if (GlowLayer.Opacity > 0)
        {
            _glowFading = true;
            GlowLayer.Opacity = 0;
            await Task.Delay(300);
            if (version != _glowVersion)
            {
                return; // a newer change took over
            }
        }

        _glowFading = false;
        GlowBrush.ImageSource = glow;
        GlowLayer.Opacity = glow is null ? 0 : 0.9;
    }
}

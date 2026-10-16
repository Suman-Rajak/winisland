using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    // Wide enough for the widest pill (440, an answer or draft) with the mic/camera bubble beside it (up to
    // 46 wide, 6 away): 2 × (220 + 6 + 46) plus a little room.
    private const float StageWidth = 560;
    private const float StageHeight = 270; // the tallest pill (clipboard + tabs) plus room for its bounce
    private const float TabBarHeight = 34;
    private const float TopGap = 8;

    private const int PointerTickMs = 30;
    private const int SlowTickEvery = 16; // ~0.5 s
    private const int ExpandDelayMs = 50;
    private const int CollapseDelayMs = 280;

    // Stepping aside for an app underneath (see Tick): the pointer must rest this long on the island to
    // open it, moving no more than this (DIPs). A while into resting, the island starts coming back.
    private const int RestToOpenMs = 400;
    private const int RestShowsAfterMs = 120;
    private const float RestSlop = 9; // (a resting hand still wobbles a little)
    private const float PeekOpacity = 0.15f; // the island while you point past it at what's underneath

    // The island at rest is this tall, idle or compact; as a notch, so is the frosted bar it hangs in
    // across the top of the screen (see TopStrip).
    private const float RestingHeight = 34;

    // With nothing to show: Glyde's face at the left end, the time at the right.
    private static readonly PillShape IdleShape = new(176, RestingHeight, RestingHeight / 2);

    private const float BubbleGap = 6;     // between the pill and the mic/camera bubble
    private const float BubbleHeight = 32;

    private const float TabWidth = 40;     // the tab row's buttons, and the gap between them
    private const float TabSpacing = 4;
    private const float TabSlide = 28;     // switching tabs, the new one slides in from this far to the side

    private readonly IslandOptions _options;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly IntPtr _hwnd;
    private readonly Compositor _compositor;
    private readonly Visual _stageVisual;
    private readonly Visual _islandVisual; // the pill with its shadow and glow, for fading it all out
    private readonly CompositionRoundedRectangleGeometry _pill;
    private readonly Aurora _aurora;
    private readonly GlassLight _light;
    private readonly GlydeFace _idleFace = new() { Width = 20, Height = 20 }; // in the pill when there's nothing to show
    private readonly TextBlock _idleTime = new();
    private readonly Grid _idleLayer = new();
    private readonly HashSet<FrameworkElement> _expandedLayers = []; // (they grow from the top; compact ones from the middle)
    private readonly DispatcherQueueTimer _timer;
    private readonly List<IslandActivity> _activities;
    private readonly PrivacyActivity _privacy;
    private readonly ShelfActivity _shelf;
    private readonly AssistantActivity _assistant;
    private readonly Visual _bubbleVisual;
    private readonly ExpressionAnimation _bubbleFollow;
    private readonly Dictionary<FrameworkElement, bool> _layerVisible = [];
    private readonly TopStrip _strip = new(); // (before the activities: they can change while being set up)
    private readonly ForegroundApp _foreground = new(); // the app you're in, for the bar's colour and the menu bar
    private readonly DispatcherQueueTimer _tintSoon;    // a moment after you switch apps, once the new one's drawn
    private MenuBarWindow? _menuBar;
    private bool _started;
    private ExpressionAnimation _pillPlace = null!; // (set up with the pill)
    private ShapeVisual _sheen = null!;
    private bool? _notch; // shown as a notch, as last applied (see ApplyNotch)

    private IslandState _state = IslandState.Idle;
    private IslandActivity? _shown;
    private PillShape _appliedShape;
    private List<IslandActivity> _tabs = [];
    private int _selectedTab;
    private int _highlightedTab = -1; // where the tab highlight is (-1: not placed since the row was built)
    private float _tabSlide;          // switching tabs: which way the new one comes in (see SetLayerVisible)
    private string _tabBarSignature = string.Empty;
    private bool _dragOpensShelf; // files are being dragged over the island
    private bool _wantExpanded;
    private bool _bounce; // a new notice is arriving: the pill springs out with a little bounce
    private bool _visible;
    private bool _userHidden;
    private bool _idleHidden;
    private bool _bubbleShown;
    private float _bubbleWidth;
    private bool _pointerOnBubble;
    private bool _pointerInside;
    private long _insideSince;
    private long _outsideSince;
    private bool _yielding;         // an app's window is under the pointer: clicks go through, resting opens
    private bool _enteredPressed;   // came in with a button held: a drag (files for the shelf, or a browser tab)
    private bool _pressedOver;      // clicked what's underneath: don't open until the pointer leaves
    private Native.POINT _restAt;   // where the pointer is resting, and since when
    private long _restSince;
    private float _peek = 1;        // the island's opacity while stepping aside
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
        _islandVisual = ElementCompositionPreview.GetElementVisual(Island);
        _compositor = _stageVisual.Compositor;
        _pill = CreatePillGeometry();
        _stageVisual.Clip = _compositor.CreateGeometricClip(_pill);
        (_aurora, _light) = CreateSheenAndLight();
        FollowNotchSide(LeftShoulder, -1);
        FollowNotchSide(RightShoulder, 1);

        // The mic/camera bubble slides along with the pill's right edge as the pill changes size.
        ElementCompositionPreview.SetIsTranslationEnabled(PrivacyBubble, true);
        _bubbleVisual = ElementCompositionPreview.GetElementVisual(PrivacyBubble);
        _bubbleVisual.Opacity = 0;
        _bubbleFollow = _compositor.CreateExpressionAnimation("Vector3(pill.Size.X / 2 + gap + half, 0, 0)");
        _bubbleFollow.SetReferenceParameter("pill", _pill);
        _bubbleFollow.SetScalarParameter("gap", BubbleGap);
        _bubbleFollow.SetScalarParameter("half", 0);

        var volume = new VolumeActivity(DispatcherQueue);
        var battery = new BatteryActivity(DispatcherQueue);
        var bluetooth = new BluetoothActivity(DispatcherQueue);
        _privacy = new PrivacyActivity(DispatcherQueue);
        var clipboard = new ClipboardActivity(DispatcherQueue);
        _shelf = new ShelfActivity();
        var notifications = new NotificationActivity(DispatcherQueue);
        _assistant = new AssistantActivity(DispatcherQueue);
        _assistant.KeyboardWanted += SetTakesKeyboard;
        _assistant.VoiceLevel += _aurora.SetLevel;
        Activated += OnActivated;
        _activities =
        [
            new ClockActivity(DispatcherQueue),
            new MediaActivity(DispatcherQueue, options.DemoMedia),
            volume,
            new BrightnessActivity(DispatcherQueue),
            battery,
            bluetooth,
            _privacy,
            notifications,
            clipboard,
            _shelf,
            new TimerActivity(DispatcherQueue),
            _assistant,
        ];
        foreach (var activity in _activities)
        {
            AddLayer(activity.CompactView, activity.CompactShape);
            AddLayer(activity.ExpandedView, activity.ExpandedShape, expanded: true);
            activity.Changed += (_, _) => OnActivityChanged();
        }

        // With nothing to show, Glyde's face sits at the pill's left end, blinking and glancing about now
        // and then, centred in the rounded end; the time is at the right.
        _idleFace.SetWander(true);
        _idleFace.HorizontalAlignment = HorizontalAlignment.Left;
        _idleFace.VerticalAlignment = VerticalAlignment.Center;
        _idleFace.Margin = new Thickness(RestingHeight / 2 - 10, 0, 0, 0);
        _idleTime.HorizontalAlignment = HorizontalAlignment.Right;
        _idleTime.VerticalAlignment = VerticalAlignment.Center;
        _idleTime.Margin = new Thickness(0, 0, 15, 0);
        _idleTime.FontSize = 13;
        _idleTime.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _idleTime.Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 255, 255, 255));
        ShowIdleTime();
        _idleLayer.Children.Add(_idleFace);
        _idleLayer.Children.Add(_idleTime);
        AddLayer(_idleLayer, IdleShape);

        ElementCompositionPreview.SetIsTranslationEnabled(TabBar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(TabHighlight, true);
        ElementCompositionPreview.GetElementVisual(TabBar).Opacity = 0;
        TabBar.IsHitTestVisible = false;
        _layerVisible[TabBar] = false;
        _expandedLayers.Add(TabBar);

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
        if (options.DemoPrivacy)
        {
            _privacy.Demo();
        }
        if (options.DemoClipboard)
        {
            clipboard.Demo();
        }
        if (options.DemoShelf)
        {
            _ = _shelf.DemoAsync();
        }
        if (options.DemoNotification)
        {
            notifications.Demo();
        }
        if (options.DemoAssistant)
        {
            _assistant.Demo();
        }
        _selectedTab = options.Tab; // only sticks with --pin=expanded; hovering starts on the first tab

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(PointerTickMs);
        _timer.Tick += OnTick;

        _tintSoon = DispatcherQueue.CreateTimer();
        _tintSoon.Interval = TimeSpan.FromMilliseconds(300);
        _tintSoon.IsRepeating = false;
        _tintSoon.Tick += (_, _) => TintBar();
        _foreground.Changed += () =>
        {
            _tintSoon.Stop();
            _tintSoon.Start();
        };

        Closed += (_, _) =>
        {
            _menuBar?.Close();
            _strip.Release(); // the windows get the top of the screen back
        };

        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>"Glyde Settings…" in the menu bar.</summary>
    public event Action? SettingsRequested;

    /// <summary>Set from the tray menu.</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            UpdatePlacement(); // hidden, it gives the top of the screen back
            UpdateVisibility();
            ApplyNotch();
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
        ApplyNotch();
        ApplyState();
        Introduce();
        _timer.Start();
        _started = true;
        UpdateVisibility(); // (the menu bar, now the island's up)

        foreach (var activity in _activities)
        {
            _ = StartActivityAsync(activity);
        }
    }

    /// <summary>
    /// Coming up: the pill starts as a dot (see <see cref="CreatePillGeometry"/>), fades in as it springs
    /// out to its shape, and a glint crosses the glass. (Unless it's hidden when idle: then it's just there to hover.)
    /// </summary>
    private void Introduce()
    {
        if (_idleHidden || !Motion.Enabled)
        {
            return;
        }

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, Motion.Decelerate(_compositor));
        fade.Duration = Scaled(450);
        _islandVisual.StartAnimation(nameof(_islandVisual.Opacity), fade);
        _light.Glint(delay: 250);
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
        UpdateVisibility(); // the assistant shows even over a full-screen app
        UpdateState();
        UpdateBubble();
        ApplyIdleFade();
        ApplyTheme();
        _aurora.SetMode(_assistant.Glow);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _monitor = IntPtr.Zero; // force UpdatePlacement to re-apply display and distance
        UpdatePlacement();
        UpdateVisibility();
        UpdateState();
        ApplyNotch();
        UpdateBubble();
        ApplyTheme();
        ApplyIdleFade();
        _aurora.SetMode(_assistant.Glow);
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
    /// <param name="takesKeyboard">While you type to the assistant, the window can be activated (see <see cref="SetTakesKeyboard"/>).</param>
    private void ApplyWin32Styles(bool takesKeyboard = false)
    {
        // SetBorderAndTitleBar(false, false) still leaves a dialog frame, which draws as a white outline.
        Native.SetStyle(_hwnd, Native.GetStyle(_hwnd) & ~(Native.WS_BORDER | Native.WS_DLGFRAME | Native.WS_THICKFRAME));

        // Layered + transparent lets clicks fall through to whatever is underneath; we flip
        // WS_EX_TRANSPARENT off only while the cursor is over the pill. NOACTIVATE keeps clicks
        // on the island from stealing focus from the app you're typing in.
        var exStyle = Native.GetExStyle(_hwnd);
        exStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
        exStyle &= ~Native.WS_EX_APPWINDOW;
        if (takesKeyboard)
        {
            exStyle &= ~Native.WS_EX_NOACTIVATE;
        }
        Native.SetExStyle(_hwnd, exStyle);
        Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LWA_ALPHA);

        // No Windows 11 rounded corners or accent border around the invisible window.
        Native.SetDwmInt(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        Native.SetDwmInt(_hwnd, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE);

        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    // ---------------------------------------------------------------- the keyboard, for typing to the assistant

    private bool _hasKeyboard;
    private IntPtr _keyboardFrom; // the window that had the keyboard before

    /// <summary>
    /// The island never takes the keyboard (you keep typing where you were), except while you type to
    /// the assistant: then it does, and afterwards gives it back to the window that had it.
    /// </summary>
    private void SetTakesKeyboard(bool take)
    {
        if (take == _hasKeyboard)
        {
            return;
        }

        _hasKeyboard = take;
        if (take)
        {
            _keyboardFrom = Native.GetForegroundWindow();
            ApplyWin32Styles(takesKeyboard: true);
            BringToFront();
            Activate();
            ApplyWin32Styles(takesKeyboard: true);
        }
        else
        {
            ApplyWin32Styles();
            if (Native.GetForegroundWindow() == _hwnd && _keyboardFrom != _hwnd && Native.IsWindow(_keyboardFrom))
            {
                Native.SetForegroundWindow(_keyboardFrom);
            }
            _keyboardFrom = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Windows lets an app come to the front only when you just used it. Pressing its shortcut or clicking
    /// it counts, but not always; sharing input with the app in front makes sure.
    /// </summary>
    private void BringToFront()
    {
        if (Native.SetForegroundWindow(_hwnd))
        {
            return;
        }

        uint ours = Native.GetCurrentThreadId();
        uint theirs = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), IntPtr.Zero);
        if (theirs != 0 && theirs != ours && Native.AttachThreadInput(ours, theirs, true))
        {
            Native.SetForegroundWindow(_hwnd);
            Native.AttachThreadInput(ours, theirs, false);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        // You clicked into another app while typing: put the box away.
        if (_hasKeyboard && e.WindowActivationState == WindowActivationState.Deactivated)
        {
            _assistant.OnKeyboardLost();
        }
    }

    private CompositionRoundedRectangleGeometry CreatePillGeometry()
    {
        // A dot to start with: the first ApplyState springs it out to the idle pill (see Introduce).
        var pill = _compositor.CreateRoundedRectangleGeometry();
        pill.Size = Motion.Enabled ? new Vector2(IdleShape.Height) : new Vector2(IdleShape.Width, IdleShape.Height);
        pill.CornerRadius = new Vector2(IdleShape.Radius);

        // Keep the pill horizontally centred while its size springs around. As a notch, its rounded top
        // is lifted out of sight (see ApplyNotch).
        _pillPlace = _compositor.CreateExpressionAnimation("Vector2((stageWidth - pill.Size.X) / 2, topGap - notch * pill.CornerRadius.Y)");
        _pillPlace.SetScalarParameter("stageWidth", StageWidth);
        _pillPlace.SetScalarParameter("topGap", TopGap);
        _pillPlace.SetScalarParameter("notch", 0);
        _pillPlace.SetReferenceParameter("pill", pill);
        pill.StartAnimation(nameof(pill.Offset), _pillPlace);

        return pill;
    }

    /// <summary>
    /// As a notch (see TopStrip), the island is pure black and hangs from the top edge: square at the top
    /// (the pill is made taller by its corner radius and lifted by as much, above a clip at the edge, so
    /// only its rounded bottom shows) with shoulders curving out into the edge. Otherwise it's the glass
    /// pill floating below the top.
    /// </summary>
    private void ApplyNotch()
    {
        bool notch = IsNotch;
        if (notch == _notch)
        {
            return;
        }

        _notch = notch;
        _pillPlace.SetScalarParameter("notch", notch ? 1 : 0);
        _pill.StartAnimation(nameof(_pill.Offset), _pillPlace);
        _islandVisual.Clip = notch ? _compositor.CreateInsetClip(0, TopGap, 0, 0) : null;
        _sheen.IsVisible = !notch; // (black, not glass: no highlight or rim)
        LeftShoulder.Visibility = RightShoulder.Visibility = notch ? Visibility.Visible : Visibility.Collapsed;

        // The mic/camera bubble hangs from the edge beside it, the same.
        float radius = notch ? NotchRadius(IdleShape) : BubbleHeight / 2;
        PrivacyBubble.Height = notch ? RestingHeight : BubbleHeight;
        PrivacyBubble.CornerRadius = notch ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(radius);
        PrivacyBubble.BorderThickness = new Thickness(notch ? 0 : 1);

        ApplyShape();
        ApplyTheme();
    }

    /// <summary>A notch's bottom corners are tighter than the pill's ends, as a Mac's are.</summary>
    private static float NotchRadius(PillShape shape) => Math.Clamp(shape.Radius * 0.65f, 10, 22);

    /// <summary>Keeps a shoulder against the notch's side as it springs wider and narrower.</summary>
    private void FollowNotchSide(FrameworkElement shoulder, float side)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(shoulder, true);
        var follow = _compositor.CreateExpressionAnimation("Vector3(side * (pill.Size.X / 2 + half), 0, 0)");
        follow.SetReferenceParameter("pill", _pill);
        follow.SetScalarParameter("side", side);
        follow.SetScalarParameter("half", (float)(shoulder.Width / 2 - 1)); // (tucked a pixel under the notch)
        ElementCompositionPreview.GetElementVisual(shoulder).StartAnimation("Translation", follow);
    }

    /// <summary>
    /// The "glass" part: a highlight fading down from the top edge and a thin rim that is brighter
    /// at the top, like light catching the lip of a glass. Both follow the animated pill geometry.
    /// Over them, inside the pill, the assistant's glowing rim; behind the pill, its halo and the
    /// pill's shadow (see <see cref="Aurora"/>). And the light that plays on the glass (<see cref="GlassLight"/>).
    /// </summary>
    private (Aurora, GlassLight) CreateSheenAndLight()
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

        var size = new Vector2(StageWidth, StageHeight);
        _sheen = _compositor.CreateShapeVisual();
        _sheen.Size = size;
        _sheen.Shapes.Add(shape);

        var inside = _compositor.CreateContainerVisual();
        inside.Size = size;
        inside.Children.InsertAtTop(_sheen);
        ElementCompositionPreview.SetElementChildVisual(SheenHost, inside);

        var behind = _compositor.CreateContainerVisual();
        behind.Size = size;
        ElementCompositionPreview.SetElementChildVisual(HaloHost, behind);

        var light = new GlassLight(_compositor, size, inside);
        return (new Aurora(_compositor, _pill, size, behind, inside), light);
    }

    /// <summary>Adds an activity's view to the stage, sized to its pill and hidden.</summary>
    private void AddLayer(FrameworkElement? view, PillShape shape, bool expanded = false)
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
        ElementCompositionPreview.SetIsTranslationEnabled(view, true);
        var visual = ElementCompositionPreview.GetElementVisual(view);
        visual.Opacity = 0;
        visual.Scale = new Vector3(HiddenScale, HiddenScale, 1);
        ContentHost.Children.Add(view);
        _layerVisible[view] = false;
        if (expanded)
        {
            _expandedLayers.Add(view);
        }
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

        // As a notch, the island hangs from the top edge in its bar (see TopStrip), as tall as the bar. So it
        // goes from the bar's top, not the work area's, which now starts below the bar.
        int top = display.WorkArea.Top;
        double distance = _settings.DistanceFromTop;
        if (IsNotch)
        {
            _strip.Keep(display.Bounds, (int)Math.Ceiling(RestingHeight * scale));
            if (_strip.Top is { } barTop)
            {
                top = barTop;
                distance = 0;
            }
        }
        else if (_strip.Release())
        {
            display = Displays.FindOrPrimary(_settings.Display) ?? display; // the work area grew back
            top = display.WorkArea.Top;
        }

        // Centre on the screen, below the taskbar if it's docked at the top. The pill sits TopGap
        // inside the window, so a smaller distance pushes the (invisible) top of the window off-screen.
        var origin = new Native.POINT
        {
            X = display.Bounds.Left + (display.Bounds.Width - width) / 2,
            Y = top + (int)Math.Round((distance - TopGap) * scale),
        };

        if (display.Handle == _monitor && origin.X == _origin.X && origin.Y == _origin.Y && scale == _scale)
        {
            return;
        }

        _monitor = display.Handle;
        _origin = origin;
        _hitTop = top;
        _scale = scale;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, origin.X, origin.Y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>The island is a notch in its bar at the top of the screen (see TopStrip), unless it's hidden from the tray.</summary>
    private bool IsNotch => _settings.Notch && !_userHidden;

    private void UpdateVisibility()
    {
        // Out of the way of full-screen games, video and presentations (and when hidden from the tray),
        // except while the assistant is busy with something you asked it for. The bar at the top always goes.
        bool fullscreen = FullscreenDetector.IsFullscreenAppOn(_monitor, _hwnd);
        bool visible = _assistant.HoldsIsland || (!_userHidden && !fullscreen);
        if (visible != _visible)
        {
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

        // The frosted bar under the notch and, over it and under the island, the menu bar.
        bool bar = visible && !fullscreen;
        bool menu = bar && _started && _settings.MenuBar && _strip.Bounds is not null;
        var menuBar = menu ? MenuBar() : _menuBar;
        menuBar?.Place(menu ? _strip.Bounds : null, below: _hwnd);
        _strip.Show(bar, below: menuBar is { IsShown: true } ? menuBar.Handle : _hwnd);
    }

    /// <summary>The menu bar beside the notch, made the first time it's wanted.</summary>
    private MenuBarWindow MenuBar()
    {
        if (_menuBar is null)
        {
            _menuBar = new MenuBarWindow(_foreground);
            _menuBar.SettingsRequested += () => SettingsRequested?.Invoke();
            _menuBar.TalkRequested += _assistant.Toggle;
            TintBar();
        }
        return _menuBar;
    }

    /// <summary>
    /// The bar at the top wears the colour of the app you're in: its title bar's, read off the screen just
    /// below the bar (see AppColors). Not while one of the menu bar's menus is open over it.
    /// </summary>
    private void TintBar()
    {
        if (_strip.Bounds is not { } bar || !Native.IsWindowVisible(_strip.Handle) || _menuBar is { IsMenuOpen: true })
        {
            return;
        }

        var island = new Native.RECT
        {
            Left = _origin.X,
            Top = _origin.Y,
            Right = _origin.X + (int)Math.Ceiling(StageWidth * _scale),
            Bottom = _origin.Y + (int)Math.Ceiling(StageHeight * _scale),
        };
        var colour = AppColors.TopOf(_foreground.Window, bar.Bottom, island);
        _strip.Tint(colour);
        _menuBar?.ShowBarColour(colour);
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
            ShowIdleTime();
            if (_menuBar is { IsShown: true })
            {
                _menuBar.Refresh();
            }
            if (_tick % (SlowTickEvery * 4) == 0)
            {
                TintBar(); // (apps change colour too: a page loads, the theme switches)
            }
        }

        if (!_visible)
        {
            return;
        }

        Native.GetCursorPos(out var cursor);
        bool inside = HitRect(CurrentShape()).Contains(cursor);
        long now = Environment.TickCount64;

        // Pointing at the mic/camera bubble says again who's using them.
        bool onBubble = _bubbleShown && BubbleRect().Contains(cursor);
        if (onBubble && !_pointerOnBubble)
        {
            _privacy.Remind();
        }
        _pointerOnBubble = onBubble;

        UpdateYielding(inside, cursor, now);
        bool stepAside = _yielding && _state != IslandState.Expanded;
        SetClickThrough(!(inside || onBubble) || stepAside);
        Peek(inside && stepAside ? (now - _restSince >= RestShowsAfterMs && !_pressedOver ? 1 : PeekOpacity) : 1);

        // The glass catches the light where you point.
        _light.Follow(inside && !stepAside ? new Vector2((cursor.X - _origin.X) / (float)_scale, (cursor.Y - _origin.Y) / (float)_scale) : null);

        if (inside != _pointerInside)
        {
            _pointerInside = inside;
            foreach (var activity in _activities)
            {
                activity.SetPointerOver(inside);
            }
        }

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

        bool wantExpanded = _state == IslandState.Expanded ? inside || now - _outsideSince < CollapseDelayMs
            : _yielding ? inside && !_pressedOver && now - _restSince >= RestToOpenMs
            : _enteredPressed ? _dragOpensShelf // (a drag: only files for the shelf open it)
            : inside && now - _insideSince >= ExpandDelayMs;

        if (wantExpanded != _wantExpanded)
        {
            _wantExpanded = wantExpanded;
            UpdateState();
        }
    }

    /// <summary>
    /// Windows wasn't designed round a notch: under the island there's often an app (a maximized
    /// browser's tabs). Then, when "step aside for apps" is on, the island lets it be: clicks go through
    /// to the app, the island fades as you point past it, and it opens only when the pointer rests on it
    /// for a moment (never after a click on what's underneath). Over the desktop it opens on hover as
    /// usual. Not while it's showing something you're in the middle of (the assistant, an alarm ringing),
    /// and not for a drag that comes in (files for the shelf still drop on it).
    /// </summary>
    private void UpdateYielding(bool inside, Native.POINT cursor, long now)
    {
        if (!inside)
        {
            _yielding = _enteredPressed = _pressedOver = false;
            return;
        }

        bool pressed = Native.IsMouseButtonDown();
        if (!_pointerInside)
        {
            // Coming in: what's under the pointer decides.
            _enteredPressed = pressed;
            _yielding = _settings.StepAsideForApps && !pressed && _state != IslandState.Expanded
                && WindowsBelow.AppAt(_hwnd, cursor, _strip.Handle);
            _restAt = cursor;
            _restSince = now;
        }
        if (_shown?.HoldsIsland == true)
        {
            _yielding = false; // (the assistant listening or answering, an alarm ringing: it's for you)
        }
        if (!_yielding)
        {
            return;
        }

        if (pressed && _state != IslandState.Expanded)
        {
            _pressedOver = true; // you clicked the app underneath
        }
        float slop = RestSlop * (float)_scale;
        if (Math.Abs(cursor.X - _restAt.X) > slop || Math.Abs(cursor.Y - _restAt.Y) > slop)
        {
            _restAt = cursor; // still moving: start resting again
            _restSince = now;
        }
    }

    /// <summary>Fades the whole island (and its bubble) while you point past it, and back as it's about to open or the pointer leaves.</summary>
    private void Peek(float opacity)
    {
        if (opacity == _peek)
        {
            return;
        }

        bool returning = opacity > _peek;
        _peek = opacity;
        var root = ElementCompositionPreview.GetElementVisual((UIElement)Content);
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, opacity, Motion.Decelerate(_compositor));
        fade.Duration = Scaled(returning ? RestToOpenMs - RestShowsAfterMs : 140);
        root.StartAnimation(nameof(root.Opacity), fade);
    }

    /// <summary>
    /// Picks what to show. On hover, the expanded island's selected tab: the first tab is the
    /// highest-priority activity with an expanded view (music, else the clock), then one per tab
    /// activity (the clipboard). Otherwise the highest-priority activity that wants the compact pill,
    /// otherwise nothing (the idle pill).
    /// </summary>
    private void UpdateState()
    {
        var compact = _activities.Where(a => a.WantsCompact).MaxBy(a => a.Priority);

        var tabs = new List<IslandActivity>();
        if (_activities.Where(a => a.HasExpanded && !a.IsTab).MaxBy(a => a.Priority) is { } home)
        {
            tabs.Add(home);
        }
        tabs.AddRange(_activities.Where(a => a.IsTab && a.HasExpanded));

        bool held = _activities.Any(a => a.HoldsIsland);
        var state = _options.PinnedState
            ?? (_wantExpanded && tabs.Count > 0 && !held ? IslandState.Expanded
                : compact is not null ? IslandState.Compact
                : IslandState.Idle);

        // Every hover starts on the first tab, except a drag of files, which opens the shelf.
        if (state != IslandState.Expanded)
        {
            _selectedTab = 0;
        }
        else if (_dragOpensShelf && tabs.IndexOf(_shelf) is int shelfTab and >= 0)
        {
            _selectedTab = shelfTab;
        }
        _selectedTab = Math.Clamp(_selectedTab, 0, Math.Max(0, tabs.Count - 1));

        var shown = state switch
        {
            IslandState.Expanded => tabs.ElementAtOrDefault(_selectedTab),
            IslandState.Compact => compact,
            _ => null,
        };

        // Switching tabs, the new one slides in from the side it's on.
        int from = _state == IslandState.Expanded && _shown is not null ? _tabs.IndexOf(_shown) : -1;
        int to = state == IslandState.Expanded && shown is not null ? tabs.IndexOf(shown) : -1;
        _tabSlide = from >= 0 && to >= 0 && from != to && Motion.Enabled ? Math.Sign(to - from) * TabSlide : 0;

        bool tabsChanged = !tabs.SequenceEqual(_tabs);
        _tabs = tabs;
        if (state == _state && shown == _shown && !tabsChanged)
        {
            // Same activity, but it may have changed size (the assistant goes from listening to answering).
            if (CurrentShape() != _appliedShape)
            {
                ApplyShape();
            }
            return;
        }

        // Something new on the island: a glint crosses the glass as it opens, and a notice arriving
        // springs the pill out with a little bounce.
        bool opening = state == IslandState.Expanded && _state != IslandState.Expanded;
        bool arriving = state == IslandState.Compact && shown != _shown && _state != IslandState.Expanded;
        _bounce = arriving;
        if (opening || arriving)
        {
            _light.Glint(delay: 120);
        }

        _state = state;
        _shown = shown;
        ApplyState();
    }

    /// <summary>The tab row also holds the assistant's mic button, so it shows even with a single tab.</summary>
    private bool ShowTabs => _state == IslandState.Expanded && (_tabs.Count > 1 || AssistantActivity.IsEnabled);

    // ---------------------------------------------------------------- dropping files on the shelf

    /// <summary>
    /// Files dragged onto the island: accept them for the shelf, and open the shelf tab (the island
    /// expands as usual while the pointer is over it; <see cref="UpdateState"/> picks the tab).
    /// Accepted as a copy, so the app they came from never deletes or moves anything.
    /// </summary>
    private void OnStageDragOver(object sender, DragEventArgs e)
    {
        if (!_shelf.HasExpanded || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to shelf";

        if (!_dragOpensShelf)
        {
            _dragOpensShelf = true;
            _shelf.SetDragOver(true);
            if (_state == IslandState.Expanded)
            {
                UpdateState(); // already open on another tab: switch
            }
        }
    }

    private void OnStageDragLeave(object sender, DragEventArgs e) => EndDrag();

    private async void OnStageDrop(object sender, DragEventArgs e)
    {
        EndDrag();
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            await _shelf.AddAsync(await e.DataView.GetStorageItemsAsync());
        }
        catch (Exception ex)
        {
            Log.Error("Dropping onto the shelf failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void EndDrag()
    {
        _dragOpensShelf = false;
        _shelf.SetDragOver(false);
    }

    /// <summary>The expanded pill is the tab's view, plus the tab row above it when there's more than one tab.</summary>
    private PillShape CurrentShape() => _state switch
    {
        IslandState.Expanded when _shown is not null =>
            ShowTabs ? _shown.ExpandedShape with { Height = _shown.ExpandedShape.Height + TabBarHeight } : _shown.ExpandedShape,
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

    /// <summary>Screen rectangle (physical pixels) of the mic/camera bubble, beside the pill's target shape.</summary>
    private Native.RECT BubbleRect()
    {
        const float pad = 4;
        float left = (StageWidth + CurrentShape().Width) / 2 + BubbleGap - pad;
        return new Native.RECT
        {
            Left = _origin.X + (int)(left * _scale),
            Top = _hitTop,
            Right = _origin.X + (int)((left + _bubbleWidth + pad * 2) * _scale),
            Bottom = _origin.Y + (int)((TopGap + (float)PrivacyBubble.Height + pad) * _scale),
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
        ApplyShape();

        if (_state == IslandState.Expanded && _shown?.ExpandedView is { } expandedView)
        {
            expandedView.Margin = new Thickness(0, TopGap + (ShowTabs ? TabBarHeight : 0), 0, 0);
        }
        UpdateTabBar();
        SetLayerVisible(TabBar, ShowTabs);
        SetLayerVisible(_idleLayer, _state == IslandState.Idle);

        foreach (var activity in _activities)
        {
            bool compact = _state == IslandState.Compact && activity == _shown;
            bool expanded = _state == IslandState.Expanded && activity == _shown;
            SetLayerVisible(activity.CompactView, compact);
            SetLayerVisible(activity.ExpandedView, expanded, _tabSlide);
            activity.OnShown(compact, expanded);
        }
        _tabSlide = 0;

        UpdateBubble();
        ApplyIdleFade();
        ApplyTheme();
    }

    /// <summary>Springs the pill to the current shape and sizes the shown view to match (most views keep one size; the assistant's changes).</summary>
    private void ApplyShape()
    {
        var shape = CurrentShape();
        _appliedShape = shape;
        AnimatePill(shape, expanding: _state == IslandState.Expanded, bounce: _bounce);
        _bounce = false;

        var (view, viewShape) = _state switch
        {
            IslandState.Expanded => (_shown?.ExpandedView, _shown?.ExpandedShape),
            IslandState.Compact => (_shown?.CompactView, _shown?.CompactShape),
            _ => (null, null),
        };
        if (view is not null && viewShape is { } size)
        {
            view.Width = size.Width;
            view.Height = size.Height;
        }
    }

    /// <summary>One small icon button per tab, then the assistant's. Rebuilt only when the tabs change; see <see cref="UpdateTabHighlight"/> for the selection.</summary>
    private void UpdateTabBar()
    {
        string signature = string.Join(",", _tabs.Select(t => t.Name)) + "|"
            + (AssistantActivity.IsEnabled ? AssistantActivity.Shortcut + AssistantActivity.TypeShortcut : "");
        if (signature != _tabBarSignature)
        {
            _tabBarSignature = signature;
            BuildTabBar();
            _highlightedTab = -1;
        }
        UpdateTabHighlight();
    }

    private void BuildTabBar()
    {
        TabButtons.Children.Clear();
        for (int i = 0; i < _tabs.Count; i++)
        {
            int index = i;
            var button = new Microsoft.UI.Xaml.Controls.Button
            {
                Style = (Style)Application.Current.Resources["IslandButtonStyle"],
                Width = TabWidth,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Content = new Microsoft.UI.Xaml.Controls.FontIcon
                {
                    Glyph = _tabs[i].Glyph,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                    OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(220) },
                },
            };
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, _tabs[i].Name);
            button.Click += (_, _) =>
            {
                _selectedTab = index;
                UpdateState();
            };
            TabButtons.Children.Add(button);
        }

        if (AssistantActivity.IsEnabled)
        {
            // After the tabs, set apart: the assistant's mic.
            TabButtons.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 1,
                Height = 14,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var mic = new Microsoft.UI.Xaml.Controls.Button
            {
                Style = (Style)Application.Current.Resources["IslandButtonStyle"],
                Width = TabWidth,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Content = new Microsoft.UI.Xaml.Controls.FontIcon
                {
                    Glyph = "", // Microphone
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)),
                },
            };
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(mic, $"Talk to {Assistant.Name} ({AssistantActivity.Shortcut})");
            mic.Click += (_, _) => _assistant.Toggle();
            TabButtons.Children.Add(mic);

            var keyboard = new Microsoft.UI.Xaml.Controls.Button
            {
                Style = (Style)Application.Current.Resources["IslandButtonStyle"],
                Width = TabWidth,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Content = new Microsoft.UI.Xaml.Controls.FontIcon
                {
                    Glyph = "", // Keyboard
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)),
                },
            };
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(keyboard, $"Type to {Assistant.Name} ({AssistantActivity.TypeShortcut})");
            keyboard.Click += (_, _) => _assistant.StartTyping();
            TabButtons.Children.Add(keyboard);
        }
    }

    /// <summary>
    /// The selected tab's icon brightens and the highlight springs over to it. While the tab row is
    /// hidden (or was just rebuilt) it's simply put there, so it never slides in from a stale place.
    /// </summary>
    private void UpdateTabHighlight()
    {
        for (int i = 0; i < _tabs.Count && i < TabButtons.Children.Count; i++)
        {
            if (TabButtons.Children[i] is Microsoft.UI.Xaml.Controls.Button { Content: UIElement icon })
            {
                icon.Opacity = i == _selectedTab ? 1 : 0.6;
            }
        }

        if (_selectedTab == _highlightedTab)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(TabHighlight);
        var target = new Vector3(_selectedTab * (TabWidth + TabSpacing), 0, 0);
        if (_highlightedTab < 0 || !_layerVisible[TabBar] || !Motion.Enabled)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
        }
        else
        {
            var slide = _compositor.CreateSpringVector3Animation();
            slide.FinalValue = target;
            slide.DampingRatio = 0.72f;
            slide.Period = Scaled(45);
            visual.StartAnimation("Translation", slide);

            // A little squeeze as it moves, like a drop of liquid.
            var squeeze = _compositor.CreateVector3KeyFrameAnimation();
            squeeze.InsertKeyFrame(0.35f, new Vector3(1.18f, 0.86f, 1), Motion.Decelerate(_compositor));
            squeeze.InsertKeyFrame(1f, Vector3.One, Motion.Decelerate(_compositor));
            squeeze.Duration = Scaled(380);
            visual.CenterPoint = new Vector3(TabWidth / 2, 13, 0);
            visual.StartAnimation(nameof(visual.Scale), squeeze);
        }
        _highlightedTab = _selectedTab;
    }

    /// <summary>
    /// Shows the mic/camera bubble beside the pill while either is in use (hidden while expanded: the
    /// pill is too tall to sit beside). Grows out of the pill's edge.
    /// </summary>
    private void UpdateBubble()
    {
        bool mic = _privacy.MicrophoneInUse, camera = _privacy.CameraInUse;
        bool show = (mic || camera) && _state != IslandState.Expanded;

        if (mic || camera)
        {
            MicrophoneDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
            CameraDot.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;

            // Padding 12 each side, 8 per dot, 6 between dots.
            float width = 24 + (mic ? 8 : 0) + (camera ? 8 : 0) + (mic && camera ? 6 : 0);
            if (width != _bubbleWidth)
            {
                _bubbleWidth = width;
                _bubbleFollow.SetScalarParameter("half", width / 2);
                _bubbleVisual.StartAnimation("Translation", _bubbleFollow);
            }
        }

        if (show == _bubbleShown)
        {
            return;
        }

        _bubbleShown = show;
        _bubbleVisual.CenterPoint = new Vector3(0, BubbleHeight / 2, 0);

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, show ? 1f : 0f);
        fade.Duration = Scaled(show ? 220 : 120);

        var scale = _compositor.CreateSpringVector3Animation();
        scale.FinalValue = show ? Vector3.One : new Vector3(0.5f, 0.5f, 1f);
        scale.DampingRatio = 0.65f;
        scale.Period = Scaled(50);

        _bubbleVisual.StartAnimation(nameof(_bubbleVisual.Opacity), fade);
        _bubbleVisual.StartAnimation(nameof(_bubbleVisual.Scale), scale);
    }

    /// <summary>
    /// "Hide when idle": fade the whole island out, but keep hover working so it can come back.
    /// Not while the mic or camera is in use: then the island stays, with its dot beside it. And never
    /// as a notch (see TopStrip): a notch is always there.
    /// </summary>
    private void ApplyIdleFade()
    {
        bool hide = _settings.HideWhenIdle && !IsNotch && _state == IslandState.Idle && _options.PinnedState is null && !_bubbleShown;
        _idleFace.SetAlive(_state == IslandState.Idle && !hide); // (blinking only while it can be seen)
        if (hide == _idleHidden)
        {
            return;
        }

        _idleHidden = hide;
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, hide ? 0f : 1f, hide ? Motion.Accelerate(_compositor) : Motion.Decelerate(_compositor));
        fade.Duration = Scaled(hide ? 300 : 160);
        _islandVisual.StartAnimation(nameof(_islandVisual.Opacity), fade);
    }

    /// <summary>The time at the idle island's right end, as Windows writes it (12 or 24 hours).</summary>
    private void ShowIdleTime()
    {
        string time = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        if (_idleTime.Text != time)
        {
            _idleTime.Text = time;
        }
    }

    /// <summary>A duration adjusted for the "animation speed" setting.</summary>
    private TimeSpan Scaled(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds * _settings.AnimationScale);

    /// <param name="bounce">A notice arriving: a livelier spring, so the pill pops out to meet it.</param>
    private void AnimatePill(PillShape shape, bool expanding, bool bounce = false)
    {
        // Growing is a little bouncy, like the iPhone's island; shrinking settles faster.
        float damping = expanding ? 0.68f : bounce && Motion.Enabled ? 0.6f : 0.85f;
        var period = Scaled(expanding || bounce ? 52 : 45);

        // As a notch, taller by its (tighter) corner radius, lifted by as much (see ApplyNotch): the same
        // height shows, with only the bottom corners rounded.
        float radius = _notch == true ? NotchRadius(shape) : shape.Radius;
        float lift = _notch == true ? radius : 0;
        StartSpring(nameof(_pill.Size), new Vector2(shape.Width, shape.Height + lift), damping, period);
        StartSpring(nameof(_pill.CornerRadius), new Vector2(radius), damping, period);
    }

    private void StartSpring(string property, Vector2 target, float damping, TimeSpan period)
    {
        var spring = _compositor.CreateSpringVector2Animation();
        spring.FinalValue = target;
        spring.DampingRatio = damping;
        spring.Period = period;
        _pill.StartAnimation(property, spring);
    }

    private const float HiddenScale = 0.94f; // a hidden view, a little smaller than it shows: it grows into place

    /// <summary>
    /// Fades a view (or the tab row) in or out. Coming in it grows into place as the pill springs open
    /// (an expanded view from the top, dropping into place as the pill grows down; a compact one from the
    /// middle), then plays its own entrance (<see cref="IEntrance"/>). Going out it shrinks back and fades
    /// quickly, out of the way of what's next.
    /// </summary>
    /// <param name="slide">Switching tabs: comes in from this far to the side (negative: from the left) and leaves the other way.</param>
    private void SetLayerVisible(FrameworkElement? layer, bool show, float slide = 0)
    {
        // Re-running the fade on a layer that's already showing would restart it and flicker.
        if (layer is null || _layerVisible[layer] == show)
        {
            return;
        }

        _layerVisible[layer] = show;
        layer.IsHitTestVisible = show;
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        bool fromTop = _expandedLayers.Contains(layer);
        double width = double.IsNaN(layer.Width) ? layer.ActualWidth : layer.Width; // the tab row sizes itself
        double height = double.IsNaN(layer.Height) ? layer.ActualHeight : layer.Height;
        visual.CenterPoint = new Vector3((float)width / 2, fromTop ? 0 : (float)height / 2, 0);

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, show ? 1f : 0f, show ? Motion.Decelerate(_compositor) : Motion.Accelerate(_compositor));
        fade.Duration = Scaled(show ? 200 : 110);
        if (show)
        {
            fade.DelayTime = Scaled(slide != 0 ? 15 : 25); // let the pill start changing shape first
        }

        var scale = _compositor.CreateSpringVector3Animation();
        scale.FinalValue = show ? Vector3.One : new Vector3(HiddenScale, HiddenScale, 1f);
        scale.DampingRatio = 0.8f;
        scale.Period = Scaled(50);

        var move = _compositor.CreateVector3KeyFrameAnimation();
        if (show)
        {
            move.InsertKeyFrame(0f, new Vector3(slide, fromTop && slide == 0 && Motion.Enabled ? -8 : 0, 0));
            move.InsertKeyFrame(1f, Vector3.Zero, Motion.Decelerate(_compositor));
            move.Duration = Scaled(420);
        }
        else
        {
            move.InsertKeyFrame(1f, new Vector3(-slide * 0.6f, 0, 0), Motion.Accelerate(_compositor));
            move.Duration = Scaled(160);
        }

        visual.StartAnimation(nameof(visual.Opacity), fade);
        visual.StartAnimation(nameof(visual.Scale), scale);
        visual.StartAnimation("Translation", move);

        if (show && layer is IEntrance entrance)
        {
            entrance.Enter();
        }
        else if (show && layer == TabBar)
        {
            Motion.Cascade(TabButtons.Children, new Vector2(0, -6), delay: 50, step: 28);
            Motion.Enter(TabHighlight, Vector2.Zero, delay: 90, scale: 0.6f);
        }
    }

    // ---------------------------------------------------------------- glass

    private const double WashOpacity = 0.6; // the artwork's colour wash in the glass

    /// <summary>
    /// Colours the glass from the activity being shown or, failing that, from whichever live activity
    /// has colours (so music keeps its colours while the volume indicator is up). Neutral otherwise,
    /// or when "match the music's colours" is off. A notch is always pure black (the music keeps its
    /// accent colour for the controls).
    /// </summary>
    private async void ApplyTheme()
    {
        var theme = !_settings.MatchMusicColors ? null
            : _shown?.Theme
              ?? _activities.Where(a => a.WantsCompact && a.Theme is not null).MaxBy(a => a.Priority)?.Theme;
        bool notch = _notch == true;

        double opacity = notch ? 1 : _settings.GlassOpacity;
        Backdrop.Opacity = opacity;
        ((SolidColorBrush)Application.Current.Resources["IslandAccentBrush"]).Color =
            theme?.Accent ?? Color.FromArgb(255, 255, 255, 255);

        var tint = notch ? Color.FromArgb(255, 0, 0, 0) : theme?.Tint ?? ColorMath.NeutralTint;
        if (tint != _tint)
        {
            _tint = tint;
            Backdrop.Background = new SolidColorBrush(tint); // a new brush, so BackgroundTransition animates it
        }

        // The bubble is the same glass as the pill.
        PrivacyBubble.Background = new SolidColorBrush(
            Color.FromArgb((byte)Math.Round(255 * opacity), tint.R, tint.G, tint.B));

        var glow = notch ? null : theme?.Glow;
        if (ReferenceEquals(glow, _glow))
        {
            if (!_glowFading)
            {
                GlowLayer.Opacity = glow is null ? 0 : WashOpacity;
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
        GlowLayer.Opacity = glow is null ? 0 : WashOpacity;
    }
}

using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Streams;
using WinIsland.Interop;
using WinIsland.Services;
using WinRT.Interop;
using WinUIEx;

namespace WinIsland;

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

    private readonly record struct PillShape(float Width, float Height, float Radius);

    private static readonly PillShape IdleShape = new(124, 32, 16);
    private static readonly PillShape CompactShape = new(250, 34, 17);
    private static readonly PillShape ExpandedEmptyShape = new(300, 92, 30);
    private static readonly PillShape ExpandedMediaShape = new(420, 172, 38);

    private readonly IslandOptions _options;
    private readonly IntPtr _hwnd;
    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _pill;
    private readonly MediaService _media;
    private readonly DispatcherQueueTimer _timer;

    private IslandState _state = IslandState.Idle;
    private bool _shownWithMedia;
    private bool _wantExpanded;
    private bool _visible;
    private bool _userHidden;
    private long _insideSince;
    private long _outsideSince;
    private int _tick;
    private string? _artworkKey;

    private IntPtr _monitor;
    private Native.POINT _origin;
    private double _scale = 1;

    internal IslandWindow(IslandOptions options)
    {
        _options = options;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureWindow();

        var stageVisual = ElementCompositionPreview.GetElementVisual(Stage);
        _compositor = stageVisual.Compositor;
        _pill = CreatePillGeometry();
        stageVisual.Clip = _compositor.CreateGeometricClip(_pill);

        foreach (var layer in new UIElement[] { CompactLayer, ExpandedEmptyLayer, ExpandedMediaLayer })
        {
            var visual = ElementCompositionPreview.GetElementVisual(layer);
            visual.Opacity = 0;
            layer.IsHitTestVisible = false;
        }

        _media = new MediaService(DispatcherQueue, options.DemoMedia);
        _media.Changed += OnMediaChanged;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(PointerTickMs);
        _timer.Tick += OnTick;
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
        ApplyState();
        _timer.Start();
        _ = _media.StartAsync();
    }

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

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
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

    // ---------------------------------------------------------------- placement & visibility

    private void UpdatePlacement()
    {
        var monitor = Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        if (!Native.TryGetMonitorInfo(monitor, out var info))
        {
            return;
        }

        Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out uint dpi, out _);
        double scale = dpi / 96.0;
        int width = (int)Math.Ceiling(StageWidth * scale);
        int height = (int)Math.Ceiling(StageHeight * scale);

        // Centre on the screen, but sit below the taskbar if it's docked at the top.
        var origin = new Native.POINT
        {
            X = info.rcMonitor.Left + (info.rcMonitor.Width - width) / 2,
            Y = info.rcWork.Top,
        };

        if (monitor == _monitor && origin.X == _origin.X && origin.Y == _origin.Y && scale == _scale)
        {
            return;
        }

        _monitor = monitor;
        _origin = origin;
        _scale = scale;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, origin.X, origin.Y, width, height, Native.SWP_NOACTIVATE);
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
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            AppWindow.Hide();
        }
    }

    /// <summary>
    /// WinUI's presenter drops topmost when it reacts to our frame-style change, and some apps
    /// grab topmost themselves, so keep reclaiming it.
    /// </summary>
    private void EnsureTopmost()
    {
        if (_visible && (Native.GetExStyle(_hwnd) & Native.WS_EX_TOPMOST) == 0)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    // ---------------------------------------------------------------- pointer & state

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (++_tick % SlowTickEvery == 0)
        {
            OnSlowTick();
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

        _wantExpanded = _state == IslandState.Expanded
            ? inside || now - _outsideSince < CollapseDelayMs
            : inside && now - _insideSince >= ExpandDelayMs;

        UpdateState();
    }

    private void OnSlowTick()
    {
        UpdatePlacement();
        UpdateVisibility();
        EnsureTopmost();

        if (_state == IslandState.Expanded)
        {
            UpdateClock();
            UpdateProgress();
        }
    }

    private void UpdateState()
    {
        var media = _media.Current;
        var state = _options.PinnedState
            ?? (_wantExpanded ? IslandState.Expanded
                : media?.IsPlaying == true ? IslandState.Compact
                : IslandState.Idle);

        if (state == _state && (media is not null) == _shownWithMedia)
        {
            return;
        }

        _state = state;
        _shownWithMedia = media is not null;
        ApplyState();
    }

    private PillShape CurrentShape() => _state switch
    {
        IslandState.Expanded => _shownWithMedia ? ExpandedMediaShape : ExpandedEmptyShape,
        IslandState.Compact => CompactShape,
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
            Top = _origin.Y,
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
        bool expanded = _state == IslandState.Expanded;
        AnimatePill(CurrentShape(), expanded);

        SetLayerVisible(CompactLayer, _state == IslandState.Compact);
        SetLayerVisible(ExpandedEmptyLayer, expanded && !_shownWithMedia);
        SetLayerVisible(ExpandedMediaLayer, expanded && _shownWithMedia);

        UpdateBars();
        if (expanded)
        {
            UpdateClock();
            UpdateProgress();
        }
    }

    private void AnimatePill(PillShape shape, bool expanding)
    {
        // Growing is a little bouncy, like the iPhone's island; shrinking settles faster.
        float damping = expanding ? 0.68f : 0.85f;
        var period = TimeSpan.FromMilliseconds(expanding ? 55 : 45);

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

    private void SetLayerVisible(FrameworkElement layer, bool show)
    {
        layer.IsHitTestVisible = show;
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        visual.CenterPoint = new Vector3((float)layer.Width / 2, 0, 0);

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, show ? 1f : 0f);
        fade.Duration = TimeSpan.FromMilliseconds(show ? 220 : 110);
        if (show)
        {
            fade.DelayTime = TimeSpan.FromMilliseconds(60); // let the pill start growing first
        }

        var scale = _compositor.CreateSpringVector3Animation();
        scale.FinalValue = show ? Vector3.One : new Vector3(0.92f, 0.92f, 1f);
        scale.DampingRatio = 0.8f;
        scale.Period = TimeSpan.FromMilliseconds(50);

        visual.StartAnimation(nameof(visual.Opacity), fade);
        visual.StartAnimation(nameof(visual.Scale), scale);
    }

    private void UpdateBars()
    {
        bool playing = _media.Current?.IsPlaying == true;
        AnimateBars(CompactBars.Children, playing && _state == IslandState.Compact);
        AnimateBars(ExpandedBars.Children, playing && _state == IslandState.Expanded);
    }

    private void AnimateBars(IList<UIElement> bars, bool running)
    {
        // Each bar loops a different pattern and speed, so together they look like a live level meter.
        // These run on the compositor thread, but they still keep it awake, so only animate visible bars.
        float[][] patterns =
        [
            [0.35f, 1.0f, 0.55f, 0.85f, 0.35f],
            [0.6f, 0.3f, 0.95f, 0.45f, 0.6f],
            [0.4f, 0.8f, 0.35f, 1.0f, 0.4f],
            [0.7f, 0.4f, 0.75f, 0.3f, 0.7f],
        ];

        for (int i = 0; i < bars.Count; i++)
        {
            var bar = (Rectangle)bars[i];
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3((float)bar.Width / 2, (float)bar.Height / 2, 0);

            var animation = _compositor.CreateScalarKeyFrameAnimation();
            if (running)
            {
                var pattern = patterns[i % patterns.Length];
                for (int k = 0; k < pattern.Length; k++)
                {
                    animation.InsertKeyFrame(k / (float)(pattern.Length - 1), pattern[k]);
                }
                animation.Duration = TimeSpan.FromMilliseconds(900 + i * 170);
                animation.IterationBehavior = AnimationIterationBehavior.Forever;
            }
            else
            {
                animation.InsertKeyFrame(1f, 0.3f);
                animation.Duration = TimeSpan.FromMilliseconds(200);
            }

            visual.StartAnimation("Scale.Y", animation);
        }
    }

    // ---------------------------------------------------------------- content

    private async void OnMediaChanged(object? sender, EventArgs e)
    {
        var media = _media.Current;
        if (media is not null)
        {
            TitleText.Text = media.Title;
            ArtistText.Text = media.Artist;
            SourceText.Text = media.AppName;
            PlayPauseIcon.Glyph = media.IsPlaying ? "" : "";
        }

        UpdateState();
        UpdateBars();
        UpdateProgress();

        var key = media?.ArtworkKey;
        if (key != _artworkKey)
        {
            _artworkKey = key;
            var artwork = await LoadArtworkAsync(media?.Thumbnail);
            if (_artworkKey == key)
            {
                CompactArt.ImageSource = artwork;
                ExpandedArt.ImageSource = artwork;
            }
        }
    }

    private static async Task<BitmapImage?> LoadArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 128 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("Loading artwork failed", ex);
            return null;
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("t");
        DateText.Text = now.ToString("dddd, d MMMM");
    }

    private void UpdateProgress()
    {
        var media = _media.Current;
        if (media is null || media.Duration <= TimeSpan.Zero)
        {
            ProgressRow.Visibility = Visibility.Collapsed;
            return;
        }

        var position = media.EstimatePosition(DateTimeOffset.Now);
        ProgressRow.Visibility = Visibility.Visible;
        Progress.Value = position / media.Duration;
        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = "-" + FormatTime(media.Duration - position);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _media.NextAsync();
}

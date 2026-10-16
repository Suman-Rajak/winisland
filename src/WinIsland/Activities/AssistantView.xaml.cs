using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Size = Windows.Foundation.Size;

namespace WinIsland.Activities;

public sealed partial class AssistantView : UserControl, IEntrance
{
    // The waveform: the middle bar is now, and what came before spreads outwards to both sides, the
    // bars tapering towards the ends.
    private const int BarCount = 27;
    private const int Half = BarCount / 2;
    private const float BarHeight = 22;
    private const float QuietBar = 0.12f; // bars never vanish completely

    private const float GlowRest = 1.5f; // the orb's glow, as a multiple of the orb's size, when nobody's talking
    private const float GlowOpacity = 0.22f;

    // Across the bars, the orb's colours.
    private static readonly Color Violet = Color.FromArgb(255, 0x8A, 0x6C, 0xFF);
    private static readonly Color Pink = Color.FromArgb(255, 0xF0, 0x5A, 0xB4);
    private static readonly Color Blue = Color.FromArgb(255, 0x3D, 0xB8, 0xFF);

    private static readonly SolidColorBrush Bright = new(Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush Dim = new(Color.FromArgb(0xA6, 255, 255, 255)); // for a shimmer to brighten

    private enum WaveMotion
    {
        Live,      // following the microphone
        WarmingUp, // the microphone is starting: a wave rolls out from the middle
        Resting,   // working out what you said: a low, slow swell
    }

    private readonly float[] _levels = new float[Half + 1]; // newest first: the middle bar, then outwards
    private readonly List<Visual> _bars = [];
    private readonly Visual _orbGlow;
    private readonly Visual _bead;
    private readonly Visual _rippleA;
    private readonly Visual _rippleB;
    private readonly Visual _progressFill;
    private FrameworkElement _panel;
    private bool _shown;            // the island is showing this view
    private bool _busy;             // working something out: the orb turns quickly and ripples
    private bool _listening = true; // the listen panel is listening (else it's working out what you said)
    private bool _ready = true;     // the microphone is recording
    private bool _statusLoading;    // the status is "Thinking…" or a download: it shimmers
    private bool _followUpHint;     // "Listening for a follow-up…" is up: it shimmers
    private WaveMotion _waveMotion = WaveMotion.Live;
    private double? _spin;          // the orb's colours: ms a turn, null still
    private bool _rippling;
    private bool _breathing;
    private string _liveShown = ""; // the live words as last shown, so only new ones fade in

    public AssistantView()
    {
        InitializeComponent();

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = BarHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(WaveColour(i / (float)(BarCount - 1))),
            };
            Wave.Children.Add(bar);
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3(1.5f, BarHeight / 2, 0);
            visual.Scale = new Vector3(1, QuietBar, 1);
            Motion.Glide(bar, 80); // (readings come ~30 times a second: glide between them)
            _bars.Add(visual);
        }
        Wave.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) };

        var middle = new Vector3(10, 10, 0); // of the orb's parts, all 20 × 20
        _orbGlow = Prepare(OrbGlow, middle, new Vector3(GlowRest, GlowRest, 1), GlowOpacity, glide: 140);
        _bead = Prepare(Bead, middle, Vector3.One, 1, glide: 90);
        _rippleA = Prepare(RippleA, middle, Vector3.One, 0);
        _rippleB = Prepare(RippleB, middle, Vector3.One, 0);
        _progressFill = Prepare(ProgressFill, Vector3.Zero, new Vector3(0, 1, 1), 1, glide: 450);

        foreach (var panel in Panels)
        {
            Motion.CrossfadeVisibility(panel);
        }
        _panel = ListenPanel;
        ShowListening();
    }

    private FrameworkElement[] Panels => [ListenPanel, StatusPanel, AnswerPanel, TypePanel, DraftPanel];

    private static Visual Prepare(UIElement element, Vector3 centre, Vector3 scale, float opacity, double glide = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = centre;
        visual.Scale = scale;
        visual.Opacity = opacity;
        if (glide > 0)
        {
            Motion.Glide(element, glide);
        }
        return visual;
    }

    /// <summary>Along the waveform (0 to 1), the orb's colours: violet, pink, blue.</summary>
    private static Color WaveColour(float t) => t < 0.5f ? Motion.Mix(Violet, Pink, t * 2) : Motion.Mix(Pink, Blue, (t - 0.5f) * 2);

    /// <summary>The view was tapped.</summary>
    internal event Action? Clicked;

    /// <summary>The island brings the assistant in: the orb pops up.</summary>
    public void Enter() => Motion.Pop(OrbHost, delay: 30, from: 0.25f);

    /// <summary>The island is showing this view, or has stopped: the loops only run while it's seen.</summary>
    internal void SetShown(bool shown)
    {
        if (shown != _shown)
        {
            _shown = shown;
            UpdateMotion();
        }
    }

    /// <param name="ready">The microphone is recording; until then a wave rolls along the bars (see <see cref="SetListeningReady"/>).</param>
    internal void ShowListening(bool ready = true)
    {
        Array.Clear(_levels);
        _ready = ready;
        _listening = true;
        _busy = false;
        Wave.Opacity = ready ? 1 : 0.6;
        LiveText.Text = "";
        _liveShown = "";
        LiveText.Foreground = Bright;
        LiveText.Visibility = Visibility.Collapsed;
        Show(ListenPanel);
        Face.SetMood(FaceMood.Listening);
        DrawWave();
    }

    /// <summary>The microphone has started: the bars follow your voice, speak now.</summary>
    internal void SetListeningReady()
    {
        _ready = true;
        Wave.Opacity = 1;
        UpdateMotion();
    }

    /// <summary>The latest loudness of your voice (0 to 1): into the middle of the waveform, the rest moving outwards; the orb swells with it.</summary>
    internal void PushLevel(float level)
    {
        Array.Copy(_levels, 0, _levels, 1, _levels.Length - 1);
        _levels[0] = level;
        DrawWave();
        Swell(level);
    }

    /// <summary>How loud the assistant's own voice is as it answers (0 to 1): the orb swells with it, and the face's mouth moves.</summary>
    internal void PushVoiceLevel(float level)
    {
        Swell(level);
        Face.SetVoice(level);
    }

    /// <summary>It starts or stops saying something: the face talks (else it's pleased with its answer).</summary>
    internal void SetSpeaking(bool speaking) => Face.SetMood(speaking ? FaceMood.Speaking : FaceMood.Happy);

    /// <param name="progress">0 to 1 for a progress bar under the text, else null.</param>
    /// <param name="busy">Working something out ("Thinking…"): the orb turns and ripples.</param>
    /// <param name="problem">Something went wrong ("Didn't catch that"): the message gives a little shake.</param>
    internal void ShowStatus(string text, double? progress = null, bool busy = false, bool problem = false)
    {
        StatusText.Text = text;
        _statusLoading = busy || progress is not null;
        StatusText.Foreground = _statusLoading ? Dim : Bright;
        Progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        SetProgress(progress ?? 0);
        _busy = busy;
        Show(StatusPanel);
        Face.SetMood(problem ? FaceMood.Puzzled : _statusLoading ? FaceMood.Thinking : FaceMood.Calm);
        if (problem)
        {
            Motion.Shake(StatusText, delay: 140);
        }
    }

    /// <summary>Glides the progress bar's fill to a new value (0 to 1).</summary>
    private void SetProgress(double value) => _progressFill.Scale = new Vector3((float)Math.Clamp(value, 0, 1), 1, 1);

    internal void ShowAnswer(string heard, string reply)
    {
        _heard = $"“{heard}”";
        HeardText.Text = _heard;
        _followUpHint = false;
        _busy = false;
        Show(AnswerPanel);
        Face.SetMood(FaceMood.Happy);

        // What you asked settles in first, then the answer flows in a few words at a time.
        Motion.Enter(HeardText, new Vector2(0, 6), delay: 40);
        Motion.Enter(ReplyScroll, new Vector2(0, 10), delay: 100, duration: 520);
        Reveal(ReplyText, reply, delay: 120);
    }

    private string _heard = "";

    /// <summary>Over the answer, in place of what you asked: it's listening for a follow-up question (shimmering), or that's over.</summary>
    internal void ShowFollowUpHint(bool listening)
    {
        HeardText.Text = listening ? "Listening for a follow-up…" : _heard;
        _followUpHint = listening;
        Face.SetMood(listening ? FaceMood.Listening : FaceMood.Happy);
        UpdateMotion();
    }

    /// <summary>A follow-up question as you ask it, over the answer in place of the hint: its latest words, on one line.</summary>
    internal void ShowFollowUpText(string text, double pillWidth)
    {
        if (_followUpHint)
        {
            _followUpHint = false;
            UpdateMotion();
        }
        var line = new Size(double.PositiveInfinity, double.PositiveInfinity);
        ShowTail(HeardText, text, () =>
        {
            HeardText.Measure(line);
            return HeardText.DesiredSize.Width <= pillWidth - SideSpace;
        });
    }

    // Left of the text: the grid's padding (10), the orb (20) and the gap after it (11); right: padding (18).
    private const double SideSpace = 10 + 20 + 11 + 18;

    private const int LiveLines = 2;

    private const double WaveRowHeight = 36; // ListenPanel's first row

    internal bool IsShowingLiveText => ListenPanel.Visibility == Visibility.Visible && LiveText.Visibility == Visibility.Visible;

    /// <summary>
    /// What you're saying, as you say it, under the waveform, new words fading in. On one line while it
    /// fits in <paramref name="maxWidth"/>, then two, the earliest words giving way to "…". Returns the
    /// size of pill it needs, in DIPs.
    /// </summary>
    /// <param name="listening">Still listening; else the bars rest and the words shimmer (it's working out the answer).</param>
    internal Size ShowLiveText(string text, double maxWidth, bool listening)
    {
        _listening = listening;
        Show(ListenPanel);
        Face.SetMood(listening ? FaceMood.Listening : FaceMood.Thinking);
        LiveText.Visibility = Visibility.Visible;
        LiveText.Foreground = listening ? Bright : Dim;
        Wave.Opacity = listening ? 1 : 0.5;
        if (!listening)
        {
            Array.Clear(_levels);
            Swell(0);
        }

        // (A couple of DIPs spare: at 125% and 150% the padding and orb round to whole pixels, which can
        // leave the words a pixel short of their line and push the last one onto a second.)
        LiveText.Text = text;
        LiveText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Ceiling(LiveText.DesiredSize.Width) + SideSpace + 2;
        var wrapped = new Size(maxWidth - SideSpace, double.PositiveInfinity);
        if (width > maxWidth)
        {
            width = maxWidth;
            ShowTail(LiveText, text, () =>
            {
                LiveText.Measure(wrapped);
                return LiveText.DesiredSize.Height - LiveText.Margin.Bottom <= LiveLines * LiveText.LineHeight + 1;
            });
        }

        // The same words, but only the new ones fade in (not while it's working on them: they shimmer then).
        string shown = LiveText.Text;
        int fresh = listening && Motion.Enabled ? NewWordsFrom(_liveShown, shown) : shown.Length;
        _liveShown = shown;
        if (fresh < shown.Length)
        {
            Reveal(LiveText, shown, from: fresh);
        }
        LiveText.Measure(width >= maxWidth ? wrapped : new Size(double.PositiveInfinity, double.PositiveInfinity));
        UpdateMotion();
        return new Size(width, WaveRowHeight + LiveText.DesiredSize.Height); // (DesiredSize includes the margin below)
    }

    /// <summary>Shows as much of the end of <paramref name="text"/> as <paramref name="fits"/>, after "…": the earliest words give way.</summary>
    private static void ShowTail(TextBlock block, string text, Func<bool> fits)
    {
        block.Text = text;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int skip = 1; skip < words.Length && !fits(); skip++)
        {
            block.Text = "…" + string.Join(' ', words[skip..]);
        }
    }

    /// <summary>
    /// Where the new words start in <paramref name="now"/>, compared with what was shown before: after
    /// the words both start with, or (once earlier words have given way to "…") after the ones that
    /// were at the end before and are at the start now.
    /// </summary>
    private static int NewWordsFrom(string before, string now)
    {
        var old = before.TrimStart('…').Split(' ');
        var words = now.TrimStart('…').Split(' ');

        int same = 0;
        while (same < old.Length && same < words.Length && old[same] == words[same])
        {
            same++;
        }

        int moved = 0;
        for (int m = Math.Min(old.Length, words.Length); m > same; m--)
        {
            if (old.AsSpan(old.Length - m).SequenceEqual(words.AsSpan(0, m)))
            {
                moved = m;
                break;
            }
        }

        int kept = Math.Max(same, moved);
        int index = now.StartsWith('…') ? 1 : 0;
        for (int w = 0; w < kept; w++)
        {
            index += words[w].Length + 1;
        }
        return Math.Min(index, now.Length);
    }

    /// <summary>
    /// Puts text in a block with it (from <paramref name="from"/> on) fading in a few words at a time,
    /// left to right, like it's being written. The layout doesn't move: only the colours do.
    /// </summary>
    private static void Reveal(TextBlock block, string text, int from = 0, double delay = 0)
    {
        block.Text = text;
        if (!Motion.Enabled || from >= text.Length)
        {
            return;
        }

        var colour = (block.Foreground as SolidColorBrush)?.Color ?? Color.FromArgb(255, 255, 255, 255);
        var hidden = Color.FromArgb(0, colour.R, colour.G, colour.B);

        // About eight steps whatever the length, so a long answer arrives as quickly as a short one.
        var words = text[from..].Split(' ');
        int perStep = Math.Max(1, (int)Math.Ceiling(words.Length / 8.0));

        block.Inlines.Clear();
        if (from > 0)
        {
            block.Inlines.Add(new Run { Text = text[..from] });
        }

        var story = new Storyboard();
        for (int i = 0, step = 0; i < words.Length; i += perStep, step++)
        {
            string chunk = string.Join(' ', words[i..Math.Min(words.Length, i + perStep)]);
            bool last = i + perStep >= words.Length;
            var ink = new SolidColorBrush(hidden);
            block.Inlines.Add(new Run { Text = last ? chunk : chunk + " ", Foreground = ink });

            var appear = new ColorAnimation
            {
                To = colour,
                Duration = TimeSpan.FromMilliseconds(320),
                BeginTime = Motion.Ms(delay + step * 55),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(appear, ink);
            Storyboard.SetTargetProperty(appear, nameof(SolidColorBrush.Color));
            story.Children.Add(appear);
        }
        story.Begin();
    }

    /// <summary>How tall the pill must be to show the whole answer (heard line, reply and margins) at a width, in DIPs.</summary>
    internal double AnswerHeight(double pillWidth)
    {
        var available = new Windows.Foundation.Size(pillWidth - SideSpace, double.PositiveInfinity);
        HeardText.Measure(available);
        ReplyText.Measure(available);
        return AnswerPanel.Padding.Top + HeardText.DesiredSize.Height + AnswerPanel.RowSpacing + ReplyText.DesiredSize.Height + AnswerPanel.Padding.Bottom;
    }

    /// <summary>How tall the pill must be to show a status message at a width (up to three lines), in DIPs.</summary>
    internal double StatusHeight(double pillWidth)
    {
        StatusText.Measure(new Windows.Foundation.Size(pillWidth - SideSpace, double.PositiveInfinity));
        return StatusText.DesiredSize.Height + 16;
    }

    /// <summary>A draft to confirm, with Cancel and <paramref name="openLabel"/> ("Open in Gmail"). Its lines come in one after another.</summary>
    internal void ShowDraft(string title, string subject, string body, string openLabel)
    {
        DraftTitle.Text = title;
        DraftSubject.Text = subject;
        DraftSubject.Visibility = subject.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftBody.Text = body;
        DraftOpenText.Text = openLabel;
        _busy = false;
        Show(DraftPanel);
        Face.SetMood(FaceMood.Calm);

        Motion.Cascade(
            new UIElement[] { DraftTitle, DraftSubject, DraftBody, DraftButtons }.Where(e => e.Visibility == Visibility.Visible),
            new Vector2(0, 8), delay: 50, step: 50);
    }

    /// <summary>An empty box to type in. <paramref name="hint"/> goes above it (what you're changing), or nothing.</summary>
    internal void ShowTyping(string placeholder, string? hint)
    {
        TypeBox.Text = "";
        TypeBox.PlaceholderText = placeholder;
        TypeHint.Text = hint ?? "";
        TypeHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        _busy = false;
        Show(TypePanel);
        Face.SetMood(FaceMood.Listening);
        if (hint is not null)
        {
            Motion.Enter(TypeHint, new Vector2(0, 4), delay: 60);
        }
    }

    /// <summary>Puts the cursor in the box (once the island has the keyboard).</summary>
    internal void FocusTypeBox() => TypeBox.Focus(FocusState.Programmatic);

    /// <summary>Enter in the box, with what was typed (trimmed; may be empty).</summary>
    internal event Action<string>? Typed;

    /// <summary>Esc in the box.</summary>
    internal event Action? TypingCancelled;

    private void OnTypeBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            Typed?.Invoke(TypeBox.Text.Trim());
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            TypingCancelled?.Invoke();
        }
    }

    /// <summary>Working something out without changing what's shown: the orb turns quickly and ripples.</summary>
    internal void SetBusy(bool busy)
    {
        _busy = busy;
        Face.SetMood(busy ? FaceMood.Thinking : FaceMood.Calm);
        UpdateMotion();
    }

    /// <summary>The draft was confirmed (true) or cancelled (false) with its buttons.</summary>
    internal event Action<bool>? DraftDecided;

    private void OnDraftOpenClick(object sender, RoutedEventArgs e) => DraftDecided?.Invoke(true);

    private void OnDraftCancelClick(object sender, RoutedEventArgs e) => DraftDecided?.Invoke(false);

    private void Show(FrameworkElement part)
    {
        foreach (var candidate in Panels)
        {
            candidate.Visibility = candidate == part ? Visibility.Visible : Visibility.Collapsed;
        }
        _panel = part;
        if (part != ListenPanel && part != DraftPanel)
        {
            Swell(0); // (the orb still pulses while a draft waits for your yes or no)
        }

        // Listening, the orb stays level with the waveform as the pill grows below it for your words.
        OrbHost.VerticalAlignment = part == ListenPanel ? VerticalAlignment.Top : VerticalAlignment.Center;
        OrbHost.Margin = new Thickness(0, part == ListenPanel ? (WaveRowHeight - OrbHost.Height) / 2 : 0, 0, 0);
        UpdateMotion();
    }

    private void DrawWave()
    {
        if (_waveMotion != WaveMotion.Live)
        {
            return; // (the bars are looping; setting them would stop that)
        }
        for (int i = 0; i < BarCount; i++)
        {
            int fromMiddle = Math.Abs(i - Half);
            float taper = 1 - 0.5f * MathF.Pow(fromMiddle / (float)Half, 2);
            _bars[i].Scale = new Vector3(1, Math.Max(QuietBar, _levels[fromMiddle] * taper), 1);
        }
    }

    /// <summary>The orb and its glow swell with whoever's talking (0 to 1), gliding between readings.</summary>
    private void Swell(float level)
    {
        float bead = 1 + level * 0.18f;
        float glow = GlowRest + level * 0.5f;
        _bead.Scale = new Vector3(bead, bead, 1);
        _orbGlow.Scale = new Vector3(glow, glow, 1);
        _orbGlow.Opacity = GlowOpacity + level * 0.3f;
    }

    // ---------------------------------------------------------------- loops

    /// <summary>
    /// Starts and stops the loops to suit what's showing, and only while the island shows the view
    /// (running animations keep the compositor busy): the orb breathing and turning (quickly, with
    /// ripples, while busy), the waveform's rolling and resting, and the shimmers.
    /// </summary>
    private void UpdateMotion()
    {
        bool listen = _shown && _panel == ListenPanel;

        if (_breathing != _shown)
        {
            _breathing = _shown;
            Motion.Breathe(Breath, _shown, peak: 1.07f, period: 2800);
        }
        Face.SetAlive(_shown);
        SetSpin(!_shown ? null : _busy ? 1600 : null);
        SetRipples(_shown && _busy);
        SetWaveMotion(!listen ? WaveMotion.Live : !_ready ? WaveMotion.WarmingUp : !_listening ? WaveMotion.Resting : WaveMotion.Live);

        Motion.Shimmer(StatusText, _shown && _panel == StatusPanel && _statusLoading);
        Motion.Shimmer(LiveText, listen && !_listening && LiveText.Visibility == Visibility.Visible);
        Motion.Shimmer(HeardText, _shown && _panel == AnswerPanel && _followUpHint);
    }

    private void SetSpin(double? period)
    {
        if (period != _spin)
        {
            _spin = period;
            Face.SpinColours(period);
        }
    }

    /// <summary>Two rings rippling out of the orb in turn while it thinks.</summary>
    private void SetRipples(bool on)
    {
        if (on == _rippling)
        {
            return;
        }
        _rippling = on;
        Ripple(_rippleA, on, delay: 0);
        Ripple(_rippleB, on, delay: 800);
    }

    private static void Ripple(Visual ring, bool on, double delay)
    {
        var c = ring.Compositor;
        var fade = c.CreateScalarKeyFrameAnimation();
        if (on && Motion.Enabled)
        {
            var grow = c.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, Vector3.One);
            grow.InsertKeyFrame(1f, new Vector3(2.1f, 2.1f, 1), Motion.Decelerate(c));
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.1f, 0.75f);
            fade.InsertKeyFrame(1f, 0f, Motion.Decelerate(c));
            foreach (var loop in new KeyFrameAnimation[] { grow, fade })
            {
                loop.Duration = TimeSpan.FromMilliseconds(1600);
                loop.IterationBehavior = AnimationIterationBehavior.Forever;
                loop.DelayTime = TimeSpan.FromMilliseconds(delay);
                loop.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            }
            ring.StartAnimation(nameof(ring.Scale), grow);
        }
        else
        {
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = TimeSpan.FromMilliseconds(200);
        }
        ring.StartAnimation(nameof(ring.Opacity), fade);
    }

    /// <summary>
    /// The bars' loops: while the microphone starts, a wave rolling out from the middle; while it works
    /// out what you said, a low, slow swell. Live, they follow the readings (see <see cref="DrawWave"/>).
    /// </summary>
    private void SetWaveMotion(WaveMotion motion)
    {
        if (!Motion.Enabled)
        {
            motion = WaveMotion.Live;
        }
        if (motion == _waveMotion)
        {
            return;
        }

        _waveMotion = motion;
        for (int i = 0; i < BarCount; i++)
        {
            var bar = _bars[i];
            if (motion == WaveMotion.Live)
            {
                bar.StopAnimation("Scale.Y");
                continue;
            }

            var c = bar.Compositor;
            var (low, high, period) = motion == WaveMotion.WarmingUp ? (0.14f, 0.55f, 1000.0) : (0.12f, 0.32f, 1600.0);
            var wave = c.CreateScalarKeyFrameAnimation();
            wave.InsertKeyFrame(0f, low);
            wave.InsertKeyFrame(0.5f, high, Motion.Sine(c));
            wave.InsertKeyFrame(1f, low, Motion.Sine(c));
            wave.Duration = TimeSpan.FromMilliseconds(period);
            wave.IterationBehavior = AnimationIterationBehavior.Forever;
            wave.DelayTime = TimeSpan.FromMilliseconds(Math.Abs(i - Half) * period / 16); // rolling outwards
            wave.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            bar.StartAnimation("Scale.Y", wave);
        }
        DrawWave();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e) => Clicked?.Invoke();
}

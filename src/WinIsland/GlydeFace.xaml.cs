using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace WinIsland;

/// <summary>The looks Glyde's face can take.</summary>
public enum FaceMood
{
    Calm,
    Listening, // wide awake
    Thinking,  // eyes up and to the side, mouth pursed
    Speaking,  // mouth open, moving with the voice (see GlydeFace.SetVoice)
    Happy,     // smiling eyes, a broader smile
    Puzzled,   // squint and a little frown: it didn't catch that
}

/// <summary>
/// Glyde's face (the logo), alive. While it's seen it blinks now and then (sometimes twice) and, if asked
/// to wander, glances about; it takes on a look for what the assistant is doing, moves its mouth with its
/// voice, and can follow the pointer. Each move is a short one-off animation, started by a slow timer, so
/// the compositor rests in between: the face can sit in the island all day.
/// </summary>
public sealed partial class GlydeFace : UserControl
{
    // On the face's 256 × 256 grid (see GlydeFace.xaml and tools/make-logo.py).
    private static readonly Vector3 EyesCentre = new(128, 112, 0);
    private static readonly Vector3 MouthCentre = new(128, 172, 0);
    private const float LookReach = 14; // how far the eyes move to look aside

    private readonly Visual _gaze;
    private readonly Visual _eyes;
    private readonly Visual _blink;
    private readonly Visual _mouth;
    private readonly Visual _smile;
    private readonly Visual _talk;
    private readonly Compositor _compositor;
    private readonly DispatcherQueueTimer _timer;
    private readonly Random _random = new();
    private FaceMood? _mood;
    private bool _alive;
    private bool _wander;
    private bool _following; // the eyes follow the pointer (no wandering meanwhile)
    private long _nextBlink;
    private long _nextGlance;

    public GlydeFace()
    {
        InitializeComponent();

        _gaze = Prepare(Gaze, EyesCentre, translatable: true);
        _eyes = Prepare(Eyes, EyesCentre, translatable: true);
        _blink = Prepare(Blink, EyesCentre);
        _mouth = Prepare(Mouth, MouthCentre);
        _smile = Prepare(Smile, MouthCentre);
        _talk = Prepare(Talk, new Vector3(20, 17, 0)); // (its own middle: it's placed at 108, 156)
        _compositor = _gaze.Compositor;

        _talk.Opacity = 0;
        _talk.Scale = new Vector3(0.75f, 0.15f, 1);
        Motion.Glide(Talk, 70); // (the voice's readings come ~30 times a second)

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => OnTick();
        SetMood(FaceMood.Calm);
        Unloaded += (_, _) => _timer.Stop();
    }

    private static Visual Prepare(UIElement element, Vector3 centre, bool translatable = false)
    {
        if (translatable)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        }
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = centre;
        return visual;
    }

    /// <summary>Blinking (and glancing about) while the face can be seen; stopped when it can't.</summary>
    public void SetAlive(bool alive)
    {
        if (alive == _alive)
        {
            return;
        }
        _alive = alive;
        if (alive && Motion.Enabled)
        {
            long now = Environment.TickCount64;
            _nextBlink = now + _random.Next(1200, 3500);
            _nextGlance = now + _random.Next(4000, 8000);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>Glances about now and then on its own (the face in the idle island).</summary>
    public void SetWander(bool wander) => _wander = wander;

    public void SetMood(FaceMood mood)
    {
        if (mood == _mood)
        {
            return;
        }
        _mood = mood;

        // The eyes' shape and where they sit, the mouth's shape, and smiling or talking.
        var (eyes, eyesAt, mouth) = mood switch
        {
            FaceMood.Listening => (new Vector3(1.06f, 1.12f, 1), new Vector3(0, -3, 0), new Vector3(0.82f, 0.72f, 1)),
            FaceMood.Thinking => (new Vector3(1, 0.86f, 1), new Vector3(-LookReach, -12, 0), new Vector3(0.5f, 0.28f, 1)),
            FaceMood.Happy => (new Vector3(1.04f, 0.5f, 1), new Vector3(0, 5, 0), new Vector3(1.1f, 1.22f, 1)),
            FaceMood.Puzzled => (new Vector3(1, 0.3f, 1), new Vector3(0, 7, 0), new Vector3(0.7f, -0.55f, 1)),
            _ => (Vector3.One, Vector3.Zero, Vector3.One),
        };
        bool talking = mood == FaceMood.Speaking;

        To(_eyes, nameof(Visual.Scale), eyes, mood == FaceMood.Puzzled ? 160 : 260);
        To(_eyes, "Translation", eyesAt, 320);
        To(_mouth, nameof(Visual.Scale), mouth, 280);
        Fade(_smile, talking ? 0 : 1);
        Fade(_talk, talking ? 1 : 0);
        if (!talking)
        {
            _talk.Scale = new Vector3(0.75f, 0.15f, 1);
        }
    }

    /// <summary>How loud its voice is (0 to 1) while it talks: the mouth opens with it.</summary>
    public void SetVoice(float level)
    {
        level = Math.Clamp(level, 0, 1);
        _talk.Scale = new Vector3(0.75f + 0.25f * level, 0.15f + 0.85f * level, 1);
    }

    /// <summary>Looks towards a direction (each axis -1 to 1; zero is straight ahead), following the pointer, say.</summary>
    public void LookAt(Vector2 direction)
    {
        if (direction.Length() > 1)
        {
            direction = Vector2.Normalize(direction);
        }
        _following = direction != Vector2.Zero;
        var at = new Vector3(direction.X * LookReach, direction.Y * LookReach * 0.7f, 0);
        To(_gaze, "Translation", at, 180);
    }

    /// <summary>The colours turn round, once every <paramref name="period"/> ms, while it works something out; null stops them.</summary>
    public void SpinColours(double? period) => Motion.Spin(Body, period);

    // ---------------------------------------------------------------- now and then

    private void OnTick()
    {
        long now = Environment.TickCount64;
        if (now >= _nextBlink)
        {
            bool twice = _random.NextDouble() < 0.2;
            Blinks(twice ? 2 : 1);
            _nextBlink = now + _random.Next(2400, 6000);
        }
        if (_wander && !_following && now >= _nextGlance)
        {
            Glance();
            _nextGlance = now + _random.Next(6000, 12000);
        }
    }

    private void Blinks(int times)
    {
        var shut = _compositor.CreateScalarKeyFrameAnimation();
        shut.InsertKeyFrame(0f, 1f);
        shut.InsertKeyFrame(0.45f, 0.08f, Motion.Accelerate(_compositor));
        shut.InsertKeyFrame(1f, 1f, Motion.Decelerate(_compositor));
        shut.Duration = TimeSpan.FromMilliseconds(170);
        shut.IterationBehavior = AnimationIterationBehavior.Count;
        shut.IterationCount = times;
        _blink.StartAnimation("Scale.Y", shut);
    }

    /// <summary>A look to one side (or up), a moment's pause, and back.</summary>
    private void Glance()
    {
        var to = _random.Next(3) switch
        {
            0 => new Vector3(-LookReach, 0, 0),
            1 => new Vector3(LookReach, 0, 0),
            _ => new Vector3(LookReach * 0.6f, -LookReach * 0.6f, 0),
        };
        var look = _compositor.CreateVector3KeyFrameAnimation();
        look.InsertKeyFrame(0f, Vector3.Zero);
        look.InsertKeyFrame(0.18f, to, Motion.Decelerate(_compositor));
        look.InsertKeyFrame(0.78f, to);
        look.InsertKeyFrame(1f, Vector3.Zero, Motion.Decelerate(_compositor));
        look.Duration = TimeSpan.FromMilliseconds(1700);
        _gaze.StartAnimation("Translation", look);
    }

    private void To(Visual visual, string property, Vector3 value, double milliseconds)
    {
        var move = _compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1f, value, Motion.Decelerate(_compositor));
        move.Duration = TimeSpan.FromMilliseconds(Motion.Enabled ? milliseconds : 1);
        visual.StartAnimation(property, move);
    }

    private void Fade(Visual visual, float opacity)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, opacity, Motion.Decelerate(_compositor));
        fade.Duration = TimeSpan.FromMilliseconds(Motion.Enabled ? 140 : 1);
        visual.StartAnimation(nameof(visual.Opacity), fade);
    }
}

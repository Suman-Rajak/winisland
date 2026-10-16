using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WinIsland;

/// <summary>
/// How things move, in one place so the whole island moves alike: the easing curves, the durations
/// (scaled by the "animation speed" setting), and the handful of moves the views are made of. It all
/// runs on the compositor thread, so nothing waits on the UI thread. With Windows' "Animation effects"
/// off (Settings → Accessibility → Visual effects) things simply appear, and nothing loops.
/// </summary>
internal static class Motion
{
    private static readonly UISettings s_system = new();

    /// <summary>Windows' "Animation effects" setting.</summary>
    public static bool Enabled => s_system.AnimationsEnabled;

    /// <summary>A duration adjusted for the "animation speed" setting.</summary>
    public static TimeSpan Ms(double milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(1, milliseconds * AppSettings.Current.AnimationScale));

    // ---------------------------------------------------------------- curves

    /// <summary>Arriving: quick off the mark, then a long, soft landing.</summary>
    public static CompositionEasingFunction Decelerate(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.7f), new Vector2(0.1f, 1f));

    /// <summary>Leaving: eases away, then gets out of the way.</summary>
    public static CompositionEasingFunction Accelerate(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.8f, 0.15f));

    /// <summary>Back and forth, for breathing and pulses.</summary>
    public static CompositionEasingFunction Sine(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0f), new Vector2(0.63f, 1f));

    // ---------------------------------------------------------------- one-off moves

    /// <summary>
    /// Brings an element into place: from <paramref name="from"/> (DIPs away) and transparent, gliding in
    /// and fading up. A <paramref name="scale"/> below 1 also grows it into place.
    /// </summary>
    public static void Enter(UIElement element, Vector2 from, double delay = 0, float scale = 1, double duration = 420)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, Decelerate(c));
        fade.Duration = Ms(duration * 0.7);
        Delay(fade, delay);
        visual.StartAnimation(nameof(visual.Opacity), fade);

        if (from != Vector2.Zero)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var move = c.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0f, new Vector3(from, 0));
            move.InsertKeyFrame(1f, Vector3.Zero, Decelerate(c));
            move.Duration = Ms(duration);
            Delay(move, delay);
            visual.StartAnimation("Translation", move);
        }

        if (scale != 1)
        {
            visual.CenterPoint = Centre(element);
            var grow = c.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, new Vector3(scale, scale, 1));
            grow.InsertKeyFrame(1f, Vector3.One, Decelerate(c));
            grow.Duration = Ms(duration);
            Delay(grow, delay);
            visual.StartAnimation(nameof(visual.Scale), grow);
        }
    }

    /// <summary>Brings elements in one after another (a row of buttons, a list's rows).</summary>
    public static void Cascade(IEnumerable<UIElement> elements, Vector2 from, double delay = 0, double step = 35, int max = 8)
    {
        int i = 0;
        foreach (var element in elements.Take(max))
        {
            Enter(element, from, delay + i++ * step);
        }
    }

    /// <summary>Pops an element in: grows from <paramref name="from"/> of its size with a little overshoot, fading up.</summary>
    public static void Pop(UIElement element, double delay = 0, float from = 0.4f)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        visual.CenterPoint = Centre(element);

        var grow = c.CreateSpringVector3Animation();
        grow.InitialValue = new Vector3(from, from, 1);
        grow.FinalValue = Vector3.One;
        grow.DampingRatio = 0.52f;
        grow.Period = Ms(42);
        grow.DelayTime = Ms(delay);
        grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(visual.Scale), grow);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, Decelerate(c));
        fade.Duration = Ms(200);
        Delay(fade, delay);
        visual.StartAnimation(nameof(visual.Opacity), fade);
    }

    /// <summary>A quick shake from side to side, like a password field that didn't take it: for "didn't catch that".</summary>
    public static void Shake(UIElement element, double delay = 0)
    {
        if (!Enabled)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var shake = c.CreateScalarKeyFrameAnimation();
        float[] swings = [0, -7, 6, -4, 2.5f, -1, 0];
        for (int i = 0; i < swings.Length; i++)
        {
            shake.InsertKeyFrame(i / (float)(swings.Length - 1), swings[i], Sine(c));
        }
        shake.Duration = TimeSpan.FromMilliseconds(460);
        shake.DelayTime = Ms(delay);
        visual.StartAnimation("Translation.X", shake);
    }

    // ---------------------------------------------------------------- loops
    // Running animations keep the compositor awake, so only loop what someone can see, and stop it after.

    /// <summary>Starts or stops a slow, endless swell between two sizes (breathing).</summary>
    public static void Breathe(UIElement element, bool on, float rest = 1, float peak = 1.06f, double period = 2400)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        visual.CenterPoint = Centre(element);

        var swell = c.CreateVector3KeyFrameAnimation();
        if (on && Enabled)
        {
            swell.InsertKeyFrame(0f, new Vector3(rest, rest, 1));
            swell.InsertKeyFrame(0.5f, new Vector3(peak, peak, 1), Sine(c));
            swell.InsertKeyFrame(1f, new Vector3(rest, rest, 1), Sine(c));
            swell.Duration = TimeSpan.FromMilliseconds(period); // (a mood, not a transition: not scaled)
            swell.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        else
        {
            swell.InsertKeyFrame(1f, new Vector3(rest, rest, 1), Decelerate(c));
            swell.Duration = Ms(250);
        }
        visual.StartAnimation(nameof(visual.Scale), swell);
    }

    /// <summary>Starts or stops a wiggle, like a ringing bell: a few quick swings, then a rest.</summary>
    public static void Wiggle(UIElement element, bool on)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var size = element.ActualSize;
        visual.CenterPoint = new Vector3(size.X / 2, size.Y * 0.2f, 0); // a bell swings from the top

        var swing = c.CreateScalarKeyFrameAnimation();
        if (on && Enabled)
        {
            float[] angles = [0, -18, 15, -11, 7, -3, 0];
            for (int i = 0; i < angles.Length; i++)
            {
                swing.InsertKeyFrame(i * 0.07f, angles[i], Sine(c));
            }
            swing.InsertKeyFrame(1f, 0f);
            swing.Duration = TimeSpan.FromMilliseconds(1400);
            swing.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        else
        {
            swing.InsertKeyFrame(1f, 0f, Decelerate(c));
            swing.Duration = Ms(200);
        }
        visual.StartAnimation(nameof(visual.RotationAngleInDegrees), swing);
    }

    /// <summary>Turns an element round and round, once every <paramref name="period"/> ms, carrying on from where it is; null brings it back upright.</summary>
    public static void Spin(UIElement element, double? period)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        visual.CenterPoint = Centre(element);

        var turn = c.CreateScalarKeyFrameAnimation();
        if (period is { } ms && Enabled)
        {
            turn.InsertExpressionKeyFrame(0f, "this.StartingValue");
            turn.InsertExpressionKeyFrame(1f, "this.StartingValue + 360", c.CreateLinearEasingFunction());
            turn.Duration = TimeSpan.FromMilliseconds(ms);
            turn.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        else
        {
            // Upright again, the short way round from wherever the spin had got to.
            turn.InsertExpressionKeyFrame(0f, "this.StartingValue");
            turn.InsertExpressionKeyFrame(1f, "Round(this.StartingValue / 360) * 360", Decelerate(c));
            turn.Duration = Ms(300);
        }
        visual.StartAnimation(nameof(visual.RotationAngleInDegrees), turn);
    }

    // ---------------------------------------------------------------- smoothing

    /// <summary>
    /// From now on, scale and opacity set on the element's visual glide there over <paramref name="duration"/> ms
    /// instead of jumping: for levels that change many times a second (a waveform, a progress bar).
    /// </summary>
    public static void Glide(UIElement element, double duration = 90)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var glides = c.CreateImplicitAnimationCollection();

        var scale = c.CreateVector3KeyFrameAnimation();
        scale.Target = nameof(visual.Scale);
        scale.InsertExpressionKeyFrame(1f, "this.FinalValue", Decelerate(c));
        scale.Duration = TimeSpan.FromMilliseconds(duration);
        glides[nameof(visual.Scale)] = scale;

        var opacity = c.CreateScalarKeyFrameAnimation();
        opacity.Target = nameof(visual.Opacity);
        opacity.InsertExpressionKeyFrame(1f, "this.FinalValue", Decelerate(c));
        opacity.Duration = TimeSpan.FromMilliseconds(duration * 2);
        glides[nameof(visual.Opacity)] = opacity;

        visual.ImplicitAnimations = glides;
    }

    /// <summary>
    /// For panels that take turns in one place: from now on each fades and rises in as it's made visible,
    /// and fades out as it's collapsed, so switching between them is a crossfade.
    /// </summary>
    public static void CrossfadeVisibility(UIElement element, float rise = 6)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var fadeIn = c.CreateScalarKeyFrameAnimation();
        fadeIn.Target = nameof(visual.Opacity);
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, Decelerate(c));
        fadeIn.Duration = Ms(260);
        Delay(fadeIn, 50); // (the panel it replaces is on its way out)

        var riseIn = c.CreateVector3KeyFrameAnimation();
        riseIn.Target = "Translation";
        riseIn.InsertKeyFrame(0f, new Vector3(0, rise, 0));
        riseIn.InsertKeyFrame(1f, Vector3.Zero, Decelerate(c));
        riseIn.Duration = Ms(380);
        Delay(riseIn, 50);

        var show = c.CreateAnimationGroup();
        show.Add(fadeIn);
        show.Add(riseIn);

        var fadeOut = c.CreateScalarKeyFrameAnimation();
        fadeOut.Target = nameof(visual.Opacity);
        fadeOut.InsertKeyFrame(1f, 0f, Accelerate(c));
        fadeOut.Duration = Ms(120);

        if (Enabled)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(element, show);
            ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
        }
    }

    // ---------------------------------------------------------------- shimmer

    private sealed record Shimmering(SpriteVisual Band, CompositionLinearGradientBrush Light, SizeChangedEventHandler OnResize);

    private static readonly ConditionalWeakTable<TextBlock, Shimmering> s_shimmering = new();

    private const float ShimmerWidth = 60;

    /// <summary>
    /// A band of light sweeping across text, over and over: the "working on it" look ("Thinking…", your
    /// words while it works out the answer, a download). It lights up the text's own glyphs only (drawn
    /// through the text's alpha mask), so give the text a dimmer colour for it to brighten.
    /// </summary>
    public static void Shimmer(TextBlock text, bool on)
    {
        if (s_shimmering.TryGetValue(text, out var running))
        {
            if (on && Enabled)
            {
                return;
            }
            text.SizeChanged -= running.OnResize;
            ElementCompositionPreview.SetElementChildVisual(text, null);
            running.Light.StopAnimation(nameof(running.Light.StartPoint));
            running.Light.StopAnimation(nameof(running.Light.EndPoint));
            s_shimmering.Remove(text);
            return;
        }
        if (!on || !Enabled)
        {
            return;
        }

        var c = ElementCompositionPreview.GetElementVisual(text).Compositor;
        var light = c.CreateLinearGradientBrush();
        light.MappingMode = CompositionMappingMode.Absolute;
        light.ColorStops.Add(c.CreateColorGradientStop(0f, Color.FromArgb(0, 255, 255, 255)));
        light.ColorStops.Add(c.CreateColorGradientStop(0.5f, Color.FromArgb(255, 255, 255, 255)));
        light.ColorStops.Add(c.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));

        var throughText = c.CreateMaskBrush();
        throughText.Source = light;
        throughText.Mask = text.GetAlphaMask();

        var band = c.CreateSpriteVisual();
        band.Brush = throughText;
        ElementCompositionPreview.SetElementChildVisual(text, band);

        // A download's percentage changes the width a little many times a second: only start the sweep
        // over when the text has really changed length.
        float swept = text.ActualSize.X;
        SizeChangedEventHandler onResize = (_, e) =>
        {
            band.Size = text.ActualSize;
            if (Math.Abs(e.NewSize.Width - swept) > 24)
            {
                swept = (float)e.NewSize.Width;
                Sweep(text, band, light);
            }
        };
        text.SizeChanged += onResize;
        s_shimmering.Add(text, new Shimmering(band, light, onResize));
        Sweep(text, band, light);
    }

    /// <summary>Sends the band across the text (again, at its new size), tilted a little, with a pause between sweeps.</summary>
    private static void Sweep(TextBlock text, SpriteVisual band, CompositionLinearGradientBrush light)
    {
        var size = text.ActualSize;
        band.Size = size;
        var c = band.Compositor;

        CompositionAnimation Across(float startX, float y)
        {
            var move = c.CreateVector2KeyFrameAnimation();
            move.InsertKeyFrame(0f, new Vector2(startX, y));
            move.InsertKeyFrame(0.72f, new Vector2(startX + size.X + ShimmerWidth, y), Sine(c));
            move.InsertKeyFrame(1f, new Vector2(startX + size.X + ShimmerWidth, y));
            move.Duration = TimeSpan.FromMilliseconds(1700 + size.X * 2); // about the same speed, whatever the length
            move.IterationBehavior = AnimationIterationBehavior.Forever;
            return move;
        }

        light.StartAnimation(nameof(light.StartPoint), Across(-ShimmerWidth, 0));
        light.StartAnimation(nameof(light.EndPoint), Across(0, size.Y * 0.5f));
    }

    // ---------------------------------------------------------------- helpers

    private static void Delay(KeyFrameAnimation animation, double delay)
    {
        if (delay > 0)
        {
            animation.DelayTime = Ms(delay);
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
    }

    /// <summary>The middle of an element, for scaling and turning it about (from its set size, before it's laid out).</summary>
    public static Vector3 Centre(UIElement element)
    {
        var size = element.ActualSize;
        if (size == Vector2.Zero && element is FrameworkElement { Width: > 0, Height: > 0 } sized)
        {
            size = new Vector2((float)sized.Width, (float)sized.Height);
        }
        return new Vector3(size / 2, 0);
    }

    /// <summary>A colour part way from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}

/// <summary>A view with a little choreography of its own for when the island brings it in.</summary>
internal interface IEntrance
{
    /// <summary>The island has just started showing this view (while fading it in as a whole).</summary>
    void Enter();
}

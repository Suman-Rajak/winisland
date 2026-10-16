using System.Numerics;
using Microsoft.UI.Composition;
using Windows.UI;

namespace WinIsland;

/// <summary>What the assistant is doing, as far as the island's glow is concerned.</summary>
internal enum GlowMode
{
    Off,
    Listening, // brightens a little with your voice
    Thinking,  // breathes, its colours drifting faster
    Speaking,  // brightens a little with the assistant's voice
    Waiting,   // an answer or a draft is up, or the type box: no glow (the edge is for when it's busy)
}

/// <summary>
/// The edge light round the island while the assistant is busy: a fine line of colour just inside the
/// pill's edge with a short, faint halo outside it, in the orb's colours, drifting slowly along and
/// brightening a little with whoever is speaking. Kept quiet on purpose: a cue, not a light show. And,
/// all the time, a faint neutral shadow, so the pill stands off light wallpapers.
///
/// Composition has no blur without Win2D, so the soft edges are rings: thin outlines side by side,
/// following the pill's animated shape, each fainter than the one inside it.
/// </summary>
internal sealed class Aurora
{
    // The orb's colours, a little softened; each stop of the gradient walks through them in turn.
    private static readonly Color[] s_palette =
    [
        Color.FromArgb(255, 0x7B, 0x8C, 0xFF), // periwinkle
        Color.FromArgb(255, 0xA8, 0x7C, 0xFF), // violet
        Color.FromArgb(255, 0xEC, 0x7A, 0xB8), // rose
        Color.FromArgb(255, 0x5E, 0xC4, 0xFF), // sky
    ];

    private static readonly TimeSpan ColourCycle = TimeSpan.FromSeconds(12);

    // Rings as (distance from the pill's edge, width, strength), all in DIPs. Each covers its distance
    // ± half its width, so side by side they leave no gaps. Inside the pill (negative distances) they're
    // drawn over the glass: a fine line with a breath of colour behind it. Outside, a short halo.
    private static readonly (float Distance, float Width, float Strength)[] s_halo =
    [
        (0.75f, 1.5f, 0.30f), (2.5f, 2, 0.13f), (4.5f, 2, 0.05f), (6.5f, 2, 0.015f),
    ];

    private static readonly (float Distance, float Width, float Strength)[] s_rim =
    [
        (-0.75f, 1.5f, 0.85f), (-2.5f, 2, 0.12f),
    ];

    private static readonly (float Distance, float Width, float Strength)[] s_shadow =
    [
        (1, 2, 0.13f), (3, 2, 0.09f), (5, 2, 0.06f), (7, 2, 0.04f), (9, 2, 0.022f), (11, 2, 0.01f),
    ];

    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _pill;
    private readonly Vector2 _size;

    // Intensity: 0 to 1. Spread: how far out the glowing rings sit (about 1; a little more with a voice).
    // Pulse: thinking's slow swell, 0 to about 0.25, on top of both.
    private readonly CompositionPropertySet _glow;
    private readonly CompositionLinearGradientBrush _colours;
    private readonly ContainerVisual _halo;
    private readonly ContainerVisual _rim;
    private GlowMode _mode;
    private bool _drifting;
    private int _version; // the latest change, so an older fade-out finishing doesn't put out a newer glow

    /// <param name="pill">The pill's (animated) shape, which every ring follows.</param>
    /// <param name="behind">Unclipped, behind the pill: the shadow and the halo go here.</param>
    /// <param name="inside">Inside the pill's clip, over the glass: the rim goes here.</param>
    public Aurora(Compositor compositor, CompositionRoundedRectangleGeometry pill, Vector2 size, ContainerVisual behind, ContainerVisual inside)
    {
        _compositor = compositor;
        _pill = pill;
        _size = size;

        _glow = compositor.CreatePropertySet();
        _glow.InsertScalar("Intensity", 0);
        _glow.InsertScalar("Spread", 0.9f);
        _glow.InsertScalar("Pulse", 0);

        _colours = compositor.CreateLinearGradientBrush();
        _colours.StartPoint = new Vector2(0, 0.5f);
        _colours.EndPoint = new Vector2(1, 0.5f);
        for (int i = 0; i < s_palette.Length; i++)
        {
            _colours.ColorStops.Add(compositor.CreateColorGradientStop(i / (float)(s_palette.Length - 1), s_palette[i]));
        }

        // The shadow sits a little low, as if lit from above.
        var shade = compositor.CreateColorBrush(Color.FromArgb(255, 0, 0, 0));
        foreach (var ring in s_shadow)
        {
            behind.Children.InsertAtTop(Ring(ring, shade, glowing: false, drop: 1.5f));
        }

        _halo = Rings(s_halo);
        _rim = Rings(s_rim);
        behind.Children.InsertAtTop(_halo);
        inside.Children.InsertAtTop(_rim);

        var shown = compositor.CreateExpressionAnimation("Clamp(glow.Intensity * (1 + glow.Pulse), 0, 1)");
        shown.SetReferenceParameter("glow", _glow);
        _halo.StartAnimation(nameof(_halo.Opacity), shown);
        _rim.StartAnimation(nameof(_rim.Opacity), shown);
        _halo.IsVisible = _rim.IsVisible = false; // nothing to draw until it glows
    }

    /// <summary>Changes the glow to suit what the assistant is doing.</summary>
    public void SetMode(GlowMode mode)
    {
        if (mode == GlowMode.Waiting)
        {
            mode = GlowMode.Off; // (an answer just sitting there needs no light)
        }
        if (mode == _mode)
        {
            return;
        }

        _mode = mode;
        int version = ++_version;
        if (mode != GlowMode.Off)
        {
            _halo.IsVisible = _rim.IsVisible = true;
        }
        Pulse(mode == GlowMode.Thinking);

        switch (mode)
        {
            case GlowMode.Listening:
                Drift(1f);
                To(0.75f, 1f, 360);
                break;
            case GlowMode.Speaking:
                Drift(1.2f);
                To(0.7f, 1f, 360);
                break;
            case GlowMode.Thinking:
                Drift(2.2f);
                To(0.7f, 1f, 400);
                break;
            default:
                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                To(0, 0.9f, 520);
                batch.End();
                batch.Completed += (_, _) => PutOut(version);
                break;
        }
    }

    /// <summary>How loud whoever's speaking is (0 to 1), many times a second: the edge brightens a little with it.</summary>
    public void SetLevel(float level)
    {
        if (_mode is not (GlowMode.Listening or GlowMode.Speaking) || !Motion.Enabled)
        {
            return;
        }

        level = Math.Clamp(level, 0, 1);
        float floor = _mode == GlowMode.Listening ? 0.72f : 0.66f;
        Animate("Intensity", floor + (1 - floor) * level, 160);
        Animate("Spread", 1 + 0.15f * level, 160);
    }

    // ---------------------------------------------------------------- building

    private ContainerVisual Rings((float Distance, float Width, float Strength)[] rings)
    {
        var container = _compositor.CreateContainerVisual();
        container.Size = _size;
        foreach (var ring in rings)
        {
            container.Children.InsertAtTop(Ring(ring, _colours, glowing: true));
        }
        return container;
    }

    /// <summary>One outline round the pill, a set distance from its edge, following it as it springs about.</summary>
    /// <param name="glowing">Sits a little further out (and wider) as the glow spreads; the shadow's rings stay put.</param>
    /// <param name="drop">Moved down this far.</param>
    private ShapeVisual Ring((float Distance, float Width, float Strength) ring, CompositionBrush brush, bool glowing, float drop = 0)
    {
        const string Spread = "glow.Spread * (1 + glow.Pulse * 0.3)";
        string d = glowing ? $"d * {Spread}" : "d";

        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Follow(geometry, nameof(geometry.Size), $"Vector2(Max(pill.Size.X + 2 * {d}, 0), Max(pill.Size.Y + 2 * {d}, 0))", ring.Distance);
        Follow(geometry, nameof(geometry.Offset), $"pill.Offset + Vector2(-{d}, drop - {d})", ring.Distance, drop);
        Follow(geometry, nameof(geometry.CornerRadius), $"Vector2(Max(pill.CornerRadius.X + {d}, 0), Max(pill.CornerRadius.Y + {d}, 0))", ring.Distance);

        var shape = _compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeThickness = ring.Width;
        if (glowing)
        {
            var width = _compositor.CreateExpressionAnimation($"w * {Spread}");
            width.SetScalarParameter("w", ring.Width);
            width.SetReferenceParameter("glow", _glow);
            shape.StartAnimation(nameof(shape.StrokeThickness), width);
        }

        var visual = _compositor.CreateShapeVisual();
        visual.Size = _size;
        visual.Shapes.Add(shape);
        visual.Opacity = ring.Strength;
        return visual;
    }

    private void Follow(CompositionObject target, string property, string expression, float distance, float drop = 0)
    {
        var follow = _compositor.CreateExpressionAnimation(expression);
        follow.SetReferenceParameter("pill", _pill);
        follow.SetReferenceParameter("glow", _glow);
        follow.SetScalarParameter("d", distance);
        follow.SetScalarParameter("drop", drop);
        target.StartAnimation(property, follow);
    }

    // ---------------------------------------------------------------- animating

    private void To(float intensity, float spread, double milliseconds)
    {
        Animate("Intensity", intensity, milliseconds);
        Animate("Spread", spread, milliseconds);
    }

    private void Animate(string property, float value, double milliseconds)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, value, Motion.Decelerate(_compositor));
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        _glow.StartAnimation(property, animation);
    }

    /// <summary>Thinking: a slow swell and ebb, on top of whatever the glow is doing.</summary>
    private void Pulse(bool on)
    {
        var pulse = _compositor.CreateScalarKeyFrameAnimation();
        if (on && Motion.Enabled)
        {
            pulse.InsertKeyFrame(0f, 0f);
            pulse.InsertKeyFrame(0.5f, 0.25f, Motion.Sine(_compositor));
            pulse.InsertKeyFrame(1f, 0f, Motion.Sine(_compositor));
            pulse.Duration = TimeSpan.FromMilliseconds(1800);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        else
        {
            pulse.InsertKeyFrame(1f, 0f, Motion.Decelerate(_compositor));
            pulse.Duration = TimeSpan.FromMilliseconds(300);
        }
        _glow.StartAnimation("Pulse", pulse);
    }

    /// <summary>The colours flow along the gradient, <paramref name="rate"/> times the usual speed.</summary>
    private void Drift(float rate)
    {
        if (!Motion.Enabled)
        {
            return;
        }

        if (!_drifting)
        {
            _drifting = true;
            for (int i = 0; i < _colours.ColorStops.Count; i++)
            {
                var walk = _compositor.CreateColorKeyFrameAnimation();
                walk.InterpolationColorSpace = CompositionColorSpace.Rgb;
                for (int k = 0; k <= s_palette.Length; k++)
                {
                    walk.InsertKeyFrame(k / (float)s_palette.Length, s_palette[(i + k) % s_palette.Length]);
                }
                walk.Duration = ColourCycle;
                walk.IterationBehavior = AnimationIterationBehavior.Forever;
                _colours.ColorStops[i].StartAnimation(nameof(CompositionColorGradientStop.Color), walk);
            }
        }

        foreach (var stop in _colours.ColorStops)
        {
            if (stop.TryGetAnimationController(nameof(stop.Color)) is { } controller)
            {
                controller.PlaybackRate = rate;
            }
        }
    }

    /// <summary>Once faded out (unless something lit it again meanwhile): stop drifting and drawing.</summary>
    private void PutOut(int version)
    {
        if (version != _version)
        {
            return;
        }

        if (_drifting)
        {
            _drifting = false;
            foreach (var stop in _colours.ColorStops)
            {
                stop.StopAnimation(nameof(stop.Color));
            }
        }
        _halo.IsVisible = _rim.IsVisible = false;
    }
}

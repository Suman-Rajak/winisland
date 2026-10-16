using System.Numerics;
using Microsoft.UI.Composition;
using Windows.UI;

namespace WinIsland;

/// <summary>
/// Light on the island's glass, besides its fixed sheen: a soft spot that follows the pointer while
/// it's on the pill (the glass catching the light where you point), and a glint that sweeps across
/// once when the island opens or something new arrives. Both faint: felt more than seen.
/// </summary>
internal sealed class GlassLight
{
    private const float SpotRadius = 120;
    private const float GlintWidth = 110;

    private readonly Compositor _compositor;
    private readonly Vector2 _size;
    private readonly CompositionRadialGradientBrush _spot;
    private readonly SpriteVisual _spotVisual;
    private readonly CompositionLinearGradientBrush _glint;
    private Vector2? _pointer; // where the spot is, while it's lit

    /// <param name="inside">Inside the pill's clip, over the glass and under what it shows.</param>
    public GlassLight(Compositor compositor, Vector2 size, ContainerVisual inside)
    {
        _compositor = compositor;
        _size = size;

        _spot = compositor.CreateRadialGradientBrush();
        _spot.MappingMode = CompositionMappingMode.Absolute;
        _spot.EllipseRadius = new Vector2(SpotRadius);
        _spot.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(46, 255, 255, 255)));
        _spot.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Color.FromArgb(18, 255, 255, 255)));
        _spot.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));
        _spotVisual = compositor.CreateSpriteVisual();
        _spotVisual.Size = size;
        _spotVisual.Brush = _spot;
        _spotVisual.Opacity = 0;
        inside.Children.InsertAtTop(_spotVisual);

        // A tilted band of light, parked off to the left until it's sent across.
        _glint = compositor.CreateLinearGradientBrush();
        _glint.MappingMode = CompositionMappingMode.Absolute;
        _glint.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(0, 255, 255, 255)));
        _glint.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(34, 255, 255, 255)));
        _glint.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));
        _glint.StartPoint = new Vector2(-2 * GlintWidth, 0);
        _glint.EndPoint = new Vector2(-GlintWidth, GlintWidth * 0.6f);
        var glint = compositor.CreateSpriteVisual();
        glint.Size = size;
        glint.Brush = _glint;
        inside.Children.InsertAtTop(glint);
    }

    /// <summary>The pointer is at this point on the stage (DIPs), or off the island (null). Called often; the spot glides after it.</summary>
    public void Follow(Vector2? point)
    {
        if (!Motion.Enabled)
        {
            return;
        }

        if (point is not { } p)
        {
            if (_pointer is not null)
            {
                _pointer = null;
                Fade(0, 260);
            }
            return;
        }

        if (_pointer is null)
        {
            _spot.StopAnimation(nameof(_spot.EllipseCenter));
            _spot.EllipseCenter = p; // (lights up where the pointer came in)
            Fade(1, 220);
        }
        else if (Vector2.Distance(_pointer.Value, p) > 0.5f)
        {
            var glide = _compositor.CreateVector2KeyFrameAnimation();
            glide.InsertKeyFrame(1f, p, Motion.Decelerate(_compositor));
            glide.Duration = TimeSpan.FromMilliseconds(140);
            _spot.StartAnimation(nameof(_spot.EllipseCenter), glide);
        }
        _pointer = p;
    }

    /// <summary>A band of light sweeps across the glass once, left to right.</summary>
    public void Glint(double delay = 0)
    {
        if (!Motion.Enabled)
        {
            return;
        }

        CompositionAnimation Across(float y)
        {
            var sweep = _compositor.CreateVector2KeyFrameAnimation();
            float from = -2 * GlintWidth, to = _size.X + GlintWidth;
            sweep.InsertKeyFrame(0f, new Vector2(from + (y > 0 ? GlintWidth : 0), y));
            sweep.InsertKeyFrame(1f, new Vector2(to + (y > 0 ? GlintWidth : 0), y), Motion.Decelerate(_compositor));
            sweep.Duration = Motion.Ms(900);
            sweep.DelayTime = Motion.Ms(delay);
            sweep.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            return sweep;
        }

        _glint.StartAnimation(nameof(_glint.StartPoint), Across(0));
        _glint.StartAnimation(nameof(_glint.EndPoint), Across(GlintWidth * 0.6f));
    }

    private void Fade(float to, double milliseconds)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, to, Motion.Decelerate(_compositor));
        fade.Duration = TimeSpan.FromMilliseconds(milliseconds);
        _spotVisual.StartAnimation(nameof(_spotVisual.Opacity), fade);
    }
}

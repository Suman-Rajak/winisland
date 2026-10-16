using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinIsland.Activities;

public sealed partial class LevelView : UserControl, IEntrance
{
    private readonly Storyboard _glide = new();
    private readonly DoubleAnimation _grow = new()
    {
        // (Width rather than a scale, so the bar's rounded ends stay round.)
        EnableDependentAnimation = true,
        FillBehavior = FillBehavior.Stop,
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
    };

    private double _level;

    public LevelView()
    {
        InitializeComponent();
        Storyboard.SetTarget(_grow, Fill);
        Storyboard.SetTargetProperty(_grow, nameof(Width));
        _glide.Children.Add(_grow);
    }

    /// <param name="level">0 to 1.</param>
    /// <param name="dimmed">For muted audio: the bar keeps the level but fades.</param>
    public void Show(double level, string glyph, bool dimmed)
    {
        _level = Math.Clamp(level, 0, 1);
        Icon.Glyph = glyph;
        ValueText.Text = $"{Math.Round(_level * 100)}";
        Fill.Opacity = dimmed ? 0.35 : 1;
        UpdateFill(glide: true);
    }

    /// <summary>The icon pops in and the bar fills up to the level.</summary>
    public void Enter()
    {
        Motion.Pop(Icon, delay: 20, from: 0.4f);
        UpdateFill(glide: true, from: 0);
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFill(glide: false);

    /// <summary>The bar's fill to the level: gliding there from where it is (or <paramref name="from"/>), or straight there.</summary>
    private void UpdateFill(bool glide, double? from = null)
    {
        double target = Track.ActualWidth * _level;
        double start = from ?? Fill.ActualWidth; // (mid-glide, where it's got to)
        _glide.Stop();
        Fill.Width = target;
        if (glide && Motion.Enabled && Math.Abs(start - target) > 0.5)
        {
            _grow.From = start;
            _grow.To = target;
            _grow.Duration = Motion.Ms(from is null ? 180 : 420);
            _glide.Begin();
        }
    }
}

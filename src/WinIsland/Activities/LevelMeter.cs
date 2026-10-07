using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace WinIsland.Activities;

/// <summary>The bouncing "now playing" bars. Animations run on the compositor thread.</summary>
internal static class LevelMeter
{
    // Each bar loops a different pattern and speed, so together they look like a live level meter.
    private static readonly float[][] s_patterns =
    [
        [0.35f, 1.0f, 0.55f, 0.85f, 0.35f],
        [0.6f, 0.3f, 0.95f, 0.45f, 0.6f],
        [0.4f, 0.8f, 0.35f, 1.0f, 0.4f],
        [0.7f, 0.4f, 0.75f, 0.3f, 0.7f],
    ];

    /// <summary>
    /// Starts or settles the bars. Only call when the state actually changes: restarting makes them
    /// jump. Running animations keep the compositor awake, so only animate bars someone can see.
    /// </summary>
    public static void Animate(IList<UIElement> bars, bool running)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            var bar = (FrameworkElement)bars[i];
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3((float)bar.Width / 2, (float)bar.Height / 2, 0);

            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            if (running)
            {
                var pattern = s_patterns[i % s_patterns.Length];
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
}

using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;

namespace WinIsland.Activities;

public sealed partial class AssistantView : UserControl
{
    private const int BarCount = 26;
    private const float BarHeight = 22;
    private const float QuietBar = 0.12f; // bars never vanish completely

    private readonly float[] _levels = new float[BarCount]; // oldest first
    private readonly List<Visual> _bars = [];
    private readonly Visual _orb;
    private bool _spinning;

    public AssistantView()
    {
        InitializeComponent();

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle { Width = 3, Height = BarHeight, RadiusX = 1.5, RadiusY = 1.5, Style = (Style)Application.Current.Resources["LevelBarStyle"] };
            Wave.Children.Add(bar);
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3(1.5f, BarHeight / 2, 0);
            _bars.Add(visual);
        }

        _orb = ElementCompositionPreview.GetElementVisual(Orb);
        _orb.CenterPoint = new Vector3(10, 10, 0);
        ShowListening();
    }

    /// <summary>The view was tapped.</summary>
    internal event Action? Clicked;

    internal void ShowListening()
    {
        Array.Clear(_levels);
        DrawWave();
        Show(Wave);
        Spin(false);
    }

    /// <summary>Adds the latest loudness (0 to 1) to the right of the waveform, scrolling the rest left.</summary>
    internal void PushLevel(float level)
    {
        Array.Copy(_levels, 1, _levels, 0, BarCount - 1);
        _levels[^1] = level;
        DrawWave();
        _orb.Scale = new Vector3(1 + level * 0.3f, 1 + level * 0.3f, 1);
    }

    /// <param name="progress">0 to 1 for a progress bar under the text, else null.</param>
    /// <param name="busy">Spin the orb, for "Thinking…".</param>
    internal void ShowStatus(string text, double? progress = null, bool busy = false)
    {
        StatusText.Text = text;
        Progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        Progress.Value = progress ?? 0;
        Show(StatusPanel);
        Spin(busy);
    }

    internal void ShowAnswer(string heard, string reply)
    {
        HeardText.Text = $"“{heard}”";
        ReplyText.Text = reply;
        Show(AnswerPanel);
        Spin(false);
    }

    private void Show(FrameworkElement part)
    {
        foreach (var candidate in new FrameworkElement[] { Wave, StatusPanel, AnswerPanel })
        {
            candidate.Visibility = candidate == part ? Visibility.Visible : Visibility.Collapsed;
        }
        if (part != Wave)
        {
            _orb.Scale = Vector3.One;
        }
    }

    private void DrawWave()
    {
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i].Scale = new Vector3(1, Math.Max(QuietBar, _levels[i]), 1);
        }
    }

    /// <summary>A slowly turning orb (the gradient goes round) while the assistant works something out.</summary>
    private void Spin(bool spin)
    {
        if (spin == _spinning)
        {
            return;
        }

        _spinning = spin;
        var animation = _orb.Compositor.CreateScalarKeyFrameAnimation();
        if (spin)
        {
            animation.InsertKeyFrame(0f, 0f);
            animation.InsertKeyFrame(1f, 360f, _orb.Compositor.CreateLinearEasingFunction());
            animation.Duration = TimeSpan.FromMilliseconds(1100);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        else
        {
            animation.InsertKeyFrame(1f, 0f);
            animation.Duration = TimeSpan.FromMilliseconds(1);
        }
        _orb.StartAnimation(nameof(_orb.RotationAngleInDegrees), animation);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e) => Clicked?.Invoke();
}

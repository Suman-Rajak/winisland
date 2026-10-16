using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Activities;

public sealed partial class TimerView : UserControl, IEntrance
{
    private bool _ringing;
    private bool _shown;

    public TimerView()
    {
        InitializeComponent();
    }

    internal event Action? StopClicked;

    internal event Action? SnoozeClicked;

    /// <param name="title">"10 minute timer", or "10 minute timer +1" when there are more.</param>
    internal void ShowCountdown(string title, string countdown)
    {
        Icon.Glyph = ""; // Stopwatch
        CountdownTitle.Text = title;
        CountdownText.Text = countdown;
        SetRinging(false);
    }

    internal void ShowRinging(string glyph, string title, string subtitle, bool canSnooze)
    {
        Icon.Glyph = glyph;
        RingTitle.Text = title;
        RingSubtitle.Text = subtitle;
        SnoozeButton.Visibility = canSnooze ? Visibility.Visible : Visibility.Collapsed;
        SetRinging(true);
    }

    private void SetRinging(bool ringing)
    {
        var countdown = ringing ? Visibility.Collapsed : Visibility.Visible;
        var ring = ringing ? Visibility.Visible : Visibility.Collapsed;
        CountdownTitle.Visibility = countdown;
        CountdownText.Visibility = countdown;
        RingText.Visibility = ring;
        RingButtons.Visibility = ring;
        if (ringing != _ringing)
        {
            _ringing = ringing;
            UpdateLoops();
        }
    }

    /// <summary>The island is showing this view, or has stopped: the bell only swings while it's seen.</summary>
    internal void SetShown(bool shown)
    {
        if (shown != _shown)
        {
            _shown = shown;
            UpdateLoops();
        }
    }

    /// <summary>The icon pops in; ringing, the title and buttons slide in after it.</summary>
    public void Enter()
    {
        Motion.Pop(Icon, delay: 20, from: 0.3f);
        if (_ringing)
        {
            Motion.Enter(RingText, new Vector2(-10, 0), delay: 70);
            Motion.Cascade(RingButtons.Children.Where(b => b.Visibility == Visibility.Visible), new Vector2(10, 0), delay: 110, step: 50);
        }
        else
        {
            Motion.Enter(CountdownTitle, new Vector2(-10, 0), delay: 70);
            Motion.Enter(CountdownText, new Vector2(10, 0), delay: 110);
        }
    }

    /// <summary>While it rings: the bell swings, and Stop gently swells, asking to be pressed.</summary>
    private void UpdateLoops()
    {
        bool on = _ringing && _shown;
        Motion.Wiggle(Icon, on);
        Motion.Breathe(StopButton, on, peak: 1.06f, period: 1300);
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopClicked?.Invoke();

    private void OnSnoozeClick(object sender, RoutedEventArgs e) => SnoozeClicked?.Invoke();
}

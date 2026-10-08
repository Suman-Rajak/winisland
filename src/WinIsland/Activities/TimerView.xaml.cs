using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Activities;

public sealed partial class TimerView : UserControl
{
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
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopClicked?.Invoke();

    private void OnSnoozeClick(object sender, RoutedEventArgs e) => SnoozeClicked?.Invoke();
}

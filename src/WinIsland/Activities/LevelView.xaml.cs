using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Activities;

public sealed partial class LevelView : UserControl
{
    private double _level;

    public LevelView()
    {
        InitializeComponent();
    }

    /// <param name="level">0 to 1.</param>
    /// <param name="dimmed">For muted audio: the bar keeps the level but fades.</param>
    public void Show(double level, string glyph, bool dimmed)
    {
        _level = Math.Clamp(level, 0, 1);
        Icon.Glyph = glyph;
        ValueText.Text = $"{Math.Round(_level * 100)}";
        Fill.Opacity = dimmed ? 0.35 : 1;
        UpdateFill();
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFill();

    private void UpdateFill() => Fill.Width = Track.ActualWidth * _level;
}

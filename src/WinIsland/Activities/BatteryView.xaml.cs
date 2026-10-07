using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinIsland.Activities;

internal enum BatteryMoment
{
    Charging,
    Full,
    Low,
    Critical,
}

public sealed partial class BatteryView : UserControl
{
    private const double FillWidth = 24 - 2 * 2.2; // inside the outline

    // iOS-style system colours.
    private static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    private static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);
    private static readonly Color Red = Color.FromArgb(255, 255, 69, 58);

    public BatteryView()
    {
        InitializeComponent();
    }

    internal void Show(BatteryMoment moment, int percent)
    {
        var (label, glyph, color) = moment switch
        {
            BatteryMoment.Charging => ("Charging", "", Green),   // LightningBolt
            BatteryMoment.Full => ("Fully charged", "", Green),
            BatteryMoment.Low => ("Low battery", "", Orange),    // Warning
            _ => ("Battery very low", "", Red),
        };

        var brush = new SolidColorBrush(color);
        Label.Text = label;
        Glyph.Glyph = glyph;
        Glyph.Foreground = brush;
        PercentText.Text = $"{percent}%";
        PercentText.Foreground = brush;
        Fill.Background = brush;
        Fill.Width = Math.Max(1.5, FillWidth * Math.Clamp(percent, 0, 100) / 100.0);
    }
}

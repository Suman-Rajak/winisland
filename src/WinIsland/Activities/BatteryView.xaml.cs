using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Activities;

internal enum BatteryMoment
{
    Charging,
    Full,
    Low,
    Critical,
}

public sealed partial class BatteryView : UserControl, IEntrance
{
    public BatteryView()
    {
        InitializeComponent();
    }

    internal void Show(BatteryMoment moment, int percent)
    {
        var (label, glyph, color) = moment switch
        {
            BatteryMoment.Charging => ("Charging", "", BatteryIcon.Green),   // LightningBolt
            BatteryMoment.Full => ("Fully charged", "", BatteryIcon.Green),
            BatteryMoment.Low => ("Low battery", "", BatteryIcon.Orange),    // Warning
            _ => ("Battery very low", "", BatteryIcon.Red),
        };

        var brush = new SolidColorBrush(color);
        Label.Text = label;
        Glyph.Glyph = glyph;
        Glyph.Foreground = brush;
        PercentText.Text = $"{percent}%";
        PercentText.Foreground = brush;
        Battery.Show(percent, color);
    }

    /// <summary>The bolt (or warning) pops in, the words slide in, and the battery fills up to its charge.</summary>
    public void Enter()
    {
        Motion.Pop(Glyph, delay: 30, from: 0.3f);
        Motion.Enter(Label, new Vector2(-10, 0), delay: 70);
        Motion.Enter(PercentText, new Vector2(10, 0), delay: 120);
        Battery.FillUp(delay: 140);
    }
}

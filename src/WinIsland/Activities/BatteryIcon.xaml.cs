using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinIsland.Activities;

public sealed partial class BatteryIcon : UserControl
{
    private const double FillWidth = 24 - 2 * 2.2; // inside the outline

    // iOS-style system colours, shared by the battery and Bluetooth notices.
    internal static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    internal static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);
    internal static readonly Color Red = Color.FromArgb(255, 255, 69, 58);

    public BatteryIcon()
    {
        InitializeComponent();
    }

    /// <summary>Green, or orange/red when the battery is low.</summary>
    internal static Color ColorFor(int percent) => percent <= 10 ? Red : percent <= 20 ? Orange : Green;

    internal void Show(int percent, Color color)
    {
        Fill.Background = new SolidColorBrush(color);
        Fill.Width = Math.Max(1.5, FillWidth * Math.Clamp(percent, 0, 100) / 100.0);
    }
}

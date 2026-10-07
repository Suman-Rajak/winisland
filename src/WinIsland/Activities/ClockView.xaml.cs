using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Activities;

public sealed partial class ClockView : UserControl
{
    public ClockView()
    {
        InitializeComponent();
    }

    public void ShowTime(DateTime now)
    {
        TimeText.Text = now.ToString("t");
        DateText.Text = now.ToString("dddd, d MMMM");
    }
}

using System.Numerics;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Activities;

public sealed partial class ClockView : UserControl, IEntrance
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

    /// <summary>The time settles into place, the date after it.</summary>
    public void Enter()
    {
        Motion.Enter(TimeText, new Vector2(0, 10), delay: 40, scale: 0.92f, duration: 520);
        Motion.Enter(DateText, new Vector2(0, 8), delay: 120);
    }
}

using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Activities;

public sealed partial class MediaCompactView : UserControl, IEntrance
{
    private bool _barsRunning = true; // so the first call settles them

    public MediaCompactView()
    {
        InitializeComponent();
    }

    /// <summary>A new song's artwork pops in.</summary>
    public ImageSource? Artwork
    {
        set
        {
            Art.ImageSource = value;
            if (value is not null)
            {
                Motion.Pop(ArtHost, from: 0.7f);
            }
        }
    }

    /// <summary>The artwork pops in, the bars slide in after it.</summary>
    public void Enter()
    {
        Motion.Pop(ArtHost, delay: 20, from: 0.5f);
        Motion.Enter(Bars, new Vector2(8, 0), delay: 90);
    }

    public void SetBarsRunning(bool running)
    {
        if (running != _barsRunning)
        {
            _barsRunning = running;
            LevelMeter.Animate(Bars.Children, running);
        }
    }
}

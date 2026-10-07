using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Activities;

public sealed partial class MediaCompactView : UserControl
{
    private bool _barsRunning = true; // so the first call settles them

    public MediaCompactView()
    {
        InitializeComponent();
    }

    public ImageSource? Artwork
    {
        set => Art.ImageSource = value;
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

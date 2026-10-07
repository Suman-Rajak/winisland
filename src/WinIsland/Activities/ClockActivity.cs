using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinIsland.Activities;

/// <summary>What the expanded island shows when nothing else has anything to say.</summary>
internal sealed class ClockActivity : IslandActivity
{
    private readonly ClockView _view = new();
    private readonly DispatcherQueueTimer _timer;

    public ClockActivity(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => _view.ShowTime(DateTime.Now);
    }

    public override string Name => "Clock";

    public override int Priority => 0;

    public override string Glyph => ""; // Clock

    public override bool HasExpanded => true;

    public override PillShape ExpandedShape => new(300, 92, 30);

    public override FrameworkElement ExpandedView => _view;

    public override void OnShown(bool compact, bool expanded)
    {
        if (expanded)
        {
            _view.ShowTime(DateTime.Now);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }
}

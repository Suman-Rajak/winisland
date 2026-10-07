using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinIsland.Activities;

/// <summary>
/// A short-lived indicator, like the volume or brightness: it takes over the compact pill when the
/// level changes and gives it back a moment after the last change.
/// </summary>
internal abstract class LevelActivity : IslandActivity
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(1.6);

    private readonly LevelView _view = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private bool _showing;
    private bool _pinned;

    protected LevelActivity(DispatcherQueue dispatcher)
    {
        _hideTimer = dispatcher.CreateTimer();
        _hideTimer.Interval = ShowFor;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            _showing = false;
            RaiseChanged();
        };
    }

    public override int Priority => 100;

    public override bool WantsCompact => (_showing || _pinned) && AppSettings.Current.ShowLevelIndicators;

    public override PillShape CompactShape => new(250, 34, 17);

    public override FrameworkElement CompactView => _view;

    /// <summary>Keeps the indicator up permanently; for the --demo-level switch.</summary>
    public void Pin(double level)
    {
        _pinned = true;
        Show(level, muted: false);
    }

    /// <param name="level">0 to 1.</param>
    protected void Show(double level, bool muted)
    {
        _view.Show(level, GlyphFor(level, muted), dimmed: muted);
        _showing = true;
        _hideTimer.Stop();
        _hideTimer.Start();
        RaiseChanged();
    }

    protected abstract string GlyphFor(double level, bool muted);
}

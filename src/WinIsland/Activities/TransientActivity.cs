using Microsoft.UI.Dispatching;

namespace WinIsland.Activities;

/// <summary>
/// A short-lived activity, like the volume indicator or "Charging": it takes over the compact pill
/// when something happens and gives it back a moment later.
/// </summary>
internal abstract class TransientActivity : IslandActivity
{
    private readonly DispatcherQueueTimer _hideTimer;
    private bool _showing;
    private bool _pinned;

    protected TransientActivity(DispatcherQueue dispatcher)
    {
        _hideTimer = dispatcher.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            _showing = false;
            RaiseChanged();
        };
    }

    public override bool WantsCompact => (_showing || _pinned) && IsEnabled;

    /// <summary>The user's setting for this kind of activity.</summary>
    protected virtual bool IsEnabled => true;

    /// <summary>Show now, for this long after the latest call.</summary>
    protected void ShowFor(TimeSpan duration)
    {
        _showing = true;
        _hideTimer.Stop();
        _hideTimer.Interval = duration;
        _hideTimer.Start();
        RaiseChanged();
    }

    /// <summary>Stay up permanently; for the --demo-* switches.</summary>
    protected void Pin()
    {
        _pinned = true;
        RaiseChanged();
    }
}

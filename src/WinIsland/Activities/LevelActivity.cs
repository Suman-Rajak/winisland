using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinIsland.Activities;

/// <summary>
/// A level indicator, like the volume or brightness: shows the new level when it changes and gives
/// the pill back a moment after the last change.
/// </summary>
internal abstract class LevelActivity : TransientActivity
{
    private static readonly TimeSpan ShowAfterChange = TimeSpan.FromSeconds(1.6);

    private readonly LevelView _view = new();

    protected LevelActivity(DispatcherQueue dispatcher) : base(dispatcher)
    {
    }

    public override int Priority => 100;

    public override PillShape CompactShape => new(250, 34, 17);

    public override FrameworkElement CompactView => _view;

    protected override bool IsEnabled => AppSettings.Current.ShowLevelIndicators;

    /// <summary>Keeps the indicator up permanently; for the --demo-level switch.</summary>
    public void Pin(double level)
    {
        _view.Show(level, GlyphFor(level, muted: false), dimmed: false);
        Pin();
    }

    /// <param name="level">0 to 1.</param>
    protected void Show(double level, bool muted)
    {
        _view.Show(level, GlyphFor(level, muted), dimmed: muted);
        ShowFor(ShowAfterChange);
    }

    protected abstract string GlyphFor(double level, bool muted);
}

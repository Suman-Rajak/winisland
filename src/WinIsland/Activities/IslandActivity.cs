using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinIsland.Activities;

/// <summary>A pill size, in DIPs.</summary>
internal readonly record struct PillShape(float Width, float Height, float Radius);

/// <summary>Colours for the island's glass while an activity is live, e.g. from album art.</summary>
/// <param name="Glow">A tiny image stretched behind the island as a soft colour wash.</param>
/// <param name="Tint">Dark base colour of the glass.</param>
/// <param name="Accent">Light colour for bars, levels and progress.</param>
internal sealed record GlassTheme(ImageSource Glow, Color Tint, Color Accent);

/// <summary>
/// Something the island can show: music, the clock, a volume change… The island shows the
/// highest-priority activity that wants the compact pill. On hover it expands into tabs: the first
/// shows the highest-priority activity with an expanded view, and activities with <see cref="IsTab"/>
/// get their own. Activities own their views and raise <see cref="Changed"/> (on the UI thread)
/// whenever any of the properties below change.
/// </summary>
internal abstract class IslandActivity
{
    public abstract string Name { get; }

    /// <summary>Higher wins. Short-lived indicators (volume) outrank ongoing ones (music), which outrank the clock.</summary>
    public abstract int Priority { get; }

    /// <summary>Wants to be shown in the compact pill right now.</summary>
    public virtual bool WantsCompact => false;

    /// <summary>Has something to show when the island is expanded.</summary>
    public virtual bool HasExpanded => false;

    public virtual PillShape CompactShape => default;

    public virtual PillShape ExpandedShape => default;

    /// <summary>Sized to <see cref="CompactShape"/> by the island. Created once.</summary>
    public virtual FrameworkElement? CompactView => null;

    /// <summary>Sized to <see cref="ExpandedShape"/> by the island. Created once.</summary>
    public virtual FrameworkElement? ExpandedView => null;

    /// <summary>Glass colours while this activity is live; null leaves the glass neutral.</summary>
    public virtual GlassTheme? Theme => null;

    /// <summary>Icon for this activity's tab in the expanded island (Segoe Fluent Icons glyph).</summary>
    public virtual string Glyph => ""; // Home

    /// <summary>
    /// Has a tab of its own in the expanded island (the clipboard), rather than competing by priority
    /// for the first tab (music, else the clock).
    /// </summary>
    public virtual bool IsTab => false;

    /// <summary>
    /// Keeps the island compact on this activity even while the pointer is over it (the assistant
    /// listening or answering shouldn't turn into tabs because the mouse happens to be there).
    /// </summary>
    public virtual bool HoldsIsland => false;

    public event EventHandler? Changed;

    /// <summary>Begin watching whatever this activity is about. Called once, after the island is up.</summary>
    public virtual Task StartAsync() => Task.CompletedTask;

    /// <summary>
    /// Called when the island starts or stops showing this activity's views, so animations and
    /// timers only run while someone can see them.
    /// </summary>
    public virtual void OnShown(bool compact, bool expanded)
    {
    }

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

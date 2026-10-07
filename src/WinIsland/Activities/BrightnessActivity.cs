using Microsoft.UI.Dispatching;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>Shows the screen brightness when it changes (laptops; see <see cref="BrightnessMonitor"/>).</summary>
internal sealed class BrightnessActivity : LevelActivity
{
    private readonly BrightnessMonitor _monitor;

    public BrightnessActivity(DispatcherQueue dispatcher) : base(dispatcher)
    {
        _monitor = new BrightnessMonitor(dispatcher);
        _monitor.Changed += level => Show(level / 100.0, muted: false);
    }

    public override string Name => "Brightness";

    public override Task StartAsync() => _monitor.StartAsync();

    protected override string GlyphFor(double level, bool muted) => ""; // Brightness
}

using Microsoft.UI.Dispatching;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// Shows the volume when you change it. Switching speakers (headphones connecting) doesn't count:
/// the Bluetooth notice says what happened, and the volume would cover it.
/// </summary>
internal sealed class VolumeActivity : LevelActivity
{
    private readonly VolumeMonitor _monitor;

    public VolumeActivity(DispatcherQueue dispatcher) : base(dispatcher)
    {
        _monitor = new VolumeMonitor(dispatcher);
        _monitor.Changed += (level, muted) => Show(level, muted);
    }

    public override string Name => "Volume";

    public override Task StartAsync()
    {
        try
        {
            _monitor.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Volume changes unavailable", ex);
        }
        return Task.CompletedTask;
    }

    protected override string GlyphFor(double level, bool muted) =>
        muted || level <= 0 ? "" // Mute
        : level < 0.34 ? ""     // Volume1
        : level < 0.67 ? ""     // Volume2
        : "";                   // Volume3
}

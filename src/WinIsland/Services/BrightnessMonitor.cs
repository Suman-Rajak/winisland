using System.Management;
using Microsoft.UI.Dispatching;

namespace WinIsland.Services;

/// <summary>
/// Watches the built-in screen's brightness through WMI. Laptops report it; desktop monitors
/// generally don't (they're adjusted over DDC/CI, which Windows doesn't announce), in which case
/// <see cref="StartAsync"/> returns false and nothing is raised. <see cref="Changed"/> is raised on the UI thread.
/// </summary>
internal sealed class BrightnessMonitor
{
    private readonly DispatcherQueue _dispatcher;
    private ManagementEventWatcher? _watcher;

    public BrightnessMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Brightness, 0 to 100.</summary>
    public event Action<int>? Changed;

    public Task<bool> StartAsync() => Task.Run(() =>
    {
        // Connecting to WMI can take a moment, so it happens off the UI thread.
        try
        {
            _watcher = new ManagementEventWatcher(
                new ManagementScope(@"root\WMI"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                int level = Convert.ToInt32(e.NewEvent["Brightness"]);
                _dispatcher.TryEnqueue(() => Changed?.Invoke(level));
            };
            _watcher.Start();
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Brightness changes aren't reported on this PC ({ex.Message})");
            _watcher?.Dispose();
            _watcher = null;
            return false;
        }
    });
}

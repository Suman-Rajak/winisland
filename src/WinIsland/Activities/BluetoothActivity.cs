using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// "OnePlus Nord Buds 3 · 80%" when a Bluetooth device connects, "… Disconnected" when it goes.
/// Connections drop and come back all the time, so short blips are ignored, and mice/keyboards
/// (which nap when idle and reconnect when touched) only announce reconnecting after a long absence
/// and never announce disconnecting.
/// </summary>
internal sealed class BluetoothActivity : TransientActivity
{
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(2);   // a drop shorter than this isn't news
    private static readonly TimeSpan ReconnectQuiet = TimeSpan.FromSeconds(30);   // nor is coming back this soon
    private static readonly TimeSpan InputReconnectQuiet = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);   // dual-mode devices report twice
    private static readonly TimeSpan[] BatteryReadDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(2)];

    private readonly DispatcherQueue _dispatcher;
    private readonly BluetoothMonitor _monitor;
    private readonly BluetoothView _view = new();
    private readonly Dictionary<string, DateTime> _disconnectedAt = [];
    private readonly Dictionary<string, DispatcherQueueTimer> _pendingDisconnects = [];
    private (string Name, bool Connected, DateTime At)? _lastNotice;
    private string? _showingId;

    public BluetoothActivity(DispatcherQueue dispatcher) : base(dispatcher)
    {
        _dispatcher = dispatcher;
        _monitor = new BluetoothMonitor(dispatcher);
        _monitor.ConnectionChanged += OnConnectionChanged;
    }

    public override string Name => "Bluetooth";

    public override int Priority => 85;

    public override PillShape CompactShape => new(300, 34, 17);

    public override FrameworkElement CompactView => _view;

    protected override bool IsEnabled => AppSettings.Current.ShowBluetooth;

    public override Task StartAsync()
    {
        try
        {
            _monitor.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth watching unavailable", ex);
        }
        return Task.CompletedTask;
    }

    /// <summary>Keeps a connected pair of earbuds up permanently; for the --demo-bluetooth switch.</summary>
    public void Pin(string name, int battery)
    {
        _view.Show(new BluetoothDeviceInfo("demo", name, BluetoothDeviceKind.Headphones, null), connected: true);
        _view.ShowBattery(battery);
        Pin();
    }

    private void OnConnectionChanged(BluetoothDeviceInfo device, bool connected)
    {
        var now = DateTime.UtcNow;
        if (!connected)
        {
            _disconnectedAt[device.Id] = now;
            if (device.IsInputDevice)
            {
                Log.Info($"Bluetooth: {device.Name} ({device.Kind}) disconnected; input devices aren't announced");
                return;
            }

            // Wait a moment: if it comes straight back, nothing happened as far as you're concerned.
            var timer = _dispatcher.CreateTimer();
            timer.Interval = DisconnectGrace;
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                _pendingDisconnects.Remove(device.Id);
                Announce(device, connected: false);
            };
            _pendingDisconnects[device.Id] = timer;
            timer.Start();
            return;
        }

        if (_pendingDisconnects.Remove(device.Id, out var pending))
        {
            pending.Stop(); // a blip; the disconnect was never announced
            Log.Info($"Bluetooth: {device.Name} dropped and came straight back; not announced");
            return;
        }

        var quiet = device.IsInputDevice ? InputReconnectQuiet : ReconnectQuiet;
        if (_disconnectedAt.TryGetValue(device.Id, out var since) && now - since < quiet)
        {
            Log.Info($"Bluetooth: {device.Name} reconnected after {(now - since).TotalSeconds:0} s; not announced (under {quiet.TotalSeconds:0} s)");
            return;
        }

        Announce(device, connected: true);
    }

    private void Announce(BluetoothDeviceInfo device, bool connected)
    {
        var now = DateTime.UtcNow;
        if (_lastNotice is { } last && last.Name == device.Name && last.Connected == connected && now - last.At < DuplicateWindow)
        {
            return;
        }
        _lastNotice = (device.Name, connected, now);
        Log.Info($"Bluetooth: {device.Name} ({device.Kind}) {(connected ? "connected" : "disconnected")}");

        _view.Show(device, connected);
        _showingId = device.Id;
        ShowFor(NoticeFor);

        if (connected)
        {
            _ = ShowBatteryAsync(device);
        }
    }

    /// <summary>Headsets report their battery a moment after connecting, so look twice.</summary>
    private async Task ShowBatteryAsync(BluetoothDeviceInfo device)
    {
        foreach (var delay in BatteryReadDelays)
        {
            await Task.Delay(delay);
            try
            {
                if (await BluetoothMonitor.ReadBatteryAsync(device) is int percent && _showingId == device.Id)
                {
                    _view.ShowBattery(percent);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Reading {device.Name}'s battery failed", ex);
                return;
            }
        }
    }
}

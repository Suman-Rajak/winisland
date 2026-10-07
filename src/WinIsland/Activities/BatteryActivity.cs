using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.System.Power;

namespace WinIsland.Activities;

/// <summary>
/// "Charging" when you plug in, "Fully charged" when it gets there, and a warning at 20% and 10%
/// (once each per discharge). Unplugging is silent. PCs without a battery never show anything.
/// </summary>
internal sealed class BatteryActivity : TransientActivity
{
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WarningFor = TimeSpan.FromSeconds(4);
    private const int LowAt = 20;
    private const int CriticalAt = 10;

    private readonly DispatcherQueue _dispatcher;
    private readonly BatteryView _view = new();
    private bool _pluggedIn;
    private bool _announcedFull;
    private int _warnedAt = int.MaxValue; // the lowest threshold already warned about this discharge

    public BatteryActivity(DispatcherQueue dispatcher) : base(dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public override string Name => "Battery";

    /// <summary>Below volume and brightness (they're answers to something you just did), above music.</summary>
    public override int Priority => 90;

    public override PillShape CompactShape => new(260, 34, 17);

    public override FrameworkElement CompactView => _view;

    protected override bool IsEnabled => AppSettings.Current.ShowBattery;

    private static int Percent => PowerManager.RemainingChargePercent;

    private static bool IsPluggedIn => PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;

    public override Task StartAsync()
    {
        if (PowerManager.BatteryStatus == BatteryStatus.NotPresent)
        {
            Log.Info("No battery; battery notices are off");
            return Task.CompletedTask;
        }

        // Start from the current state, so launching on a low or full battery doesn't announce it.
        _pluggedIn = IsPluggedIn;
        _announcedFull = _pluggedIn && Percent >= 100;
        _warnedAt = !_pluggedIn && Percent <= CriticalAt ? CriticalAt
                  : !_pluggedIn && Percent <= LowAt ? LowAt
                  : int.MaxValue;
        Log.Info($"Battery: {Percent}%, {PowerManager.BatteryStatus}, power supply {PowerManager.PowerSupplyStatus}");

        // These arrive on background threads.
        PowerManager.PowerSupplyStatusChanged += (_, _) => _dispatcher.TryEnqueue(OnPowerChanged);
        PowerManager.RemainingChargePercentChanged += (_, _) => _dispatcher.TryEnqueue(OnPowerChanged);
        PowerManager.BatteryStatusChanged += (_, _) => _dispatcher.TryEnqueue(OnPowerChanged);
        return Task.CompletedTask;
    }

    /// <summary>Keeps "Charging" up permanently; for the --demo-battery switch.</summary>
    public void Pin(int percent)
    {
        _view.Show(BatteryMoment.Charging, percent);
        Pin();
    }

    private void OnPowerChanged()
    {
        bool pluggedIn = IsPluggedIn;
        int percent = Percent;

        if (pluggedIn && !_pluggedIn)
        {
            _warnedAt = int.MaxValue;
            _announcedFull = percent >= 100;
            Show(BatteryMoment.Charging, percent, NoticeFor);
        }
        _pluggedIn = pluggedIn;

        if (pluggedIn)
        {
            if (percent >= 100 && !_announcedFull)
            {
                _announcedFull = true;
                Show(BatteryMoment.Full, percent, NoticeFor);
            }
            return;
        }

        _announcedFull = false;
        if (percent <= CriticalAt && _warnedAt > CriticalAt)
        {
            _warnedAt = CriticalAt;
            Show(BatteryMoment.Critical, percent, WarningFor);
        }
        else if (percent <= LowAt && _warnedAt > LowAt)
        {
            _warnedAt = LowAt;
            Show(BatteryMoment.Low, percent, WarningFor);
        }
    }

    private void Show(BatteryMoment moment, int percent, TimeSpan duration)
    {
        _view.Show(moment, percent);
        ShowFor(duration);
    }
}

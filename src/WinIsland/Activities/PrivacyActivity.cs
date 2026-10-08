using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// The microphone and camera indicator. While an app is using either, the island shows a dot
/// (orange for the mic, green for the camera) in a small bubble beside the pill; that part is drawn
/// by the island from <see cref="MicrophoneInUse"/>/<see cref="CameraInUse"/>. This activity is the
/// notice saying who: shown when an app starts using one, and again when you point at the bubble.
/// </summary>
internal sealed class PrivacyActivity : TransientActivity
{
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(3);

    private readonly PrivacyMonitor _monitor;
    private readonly PrivacyView _view = new();
    private IReadOnlyList<CapabilityUse> _uses = [];

    public PrivacyActivity(DispatcherQueue dispatcher) : base(dispatcher)
    {
        _monitor = new PrivacyMonitor(dispatcher);
        _monitor.Changed += OnUsesChanged;
    }

    public override string Name => "Privacy";

    /// <summary>Above battery and Bluetooth: an app starting to listen or watch matters more.</summary>
    public override int Priority => 95;

    public override PillShape CompactShape => new(300, 34, 17);

    public override FrameworkElement CompactView => _view;

    protected override bool IsEnabled => AppSettings.Current.ShowPrivacy;

    public bool MicrophoneInUse => IsEnabled && _uses.Any(u => u.Capability == Capability.Microphone);

    public bool CameraInUse => IsEnabled && _uses.Any(u => u.Capability == Capability.Camera);

    public override Task StartAsync()
    {
        if (!_demo)
        {
            _monitor.Start(); // the real list would replace the demo one
        }
        return Task.CompletedTask;
    }

    /// <summary>Show who's using the mic/camera again (pointing at the bubble).</summary>
    public void Remind()
    {
        if (_uses.Count > 0)
        {
            _view.Show(_uses);
            ShowFor(NoticeFor);
        }
    }

    /// <summary>Pretend Discord just started using the mic; for the --demo-privacy switch.</summary>
    public void Demo()
    {
        _demo = true;
        OnUsesChanged([new CapabilityUse(Capability.Microphone, "Discord", "demo")]);
    }

    private bool _demo;

    private void OnUsesChanged(IReadOnlyList<CapabilityUse> uses)
    {
        var started = uses
            .Where(u => !PrivacyMonitor.IsThisApp(u) && !_uses.Any(p => p.Capability == u.Capability && p.Key == u.Key))
            .ToList();
        _uses = uses;
        Log.Info($"Mic/camera in use: {(uses.Count == 0 ? "nothing" : string.Join(", ", uses.Select(u => $"{u.App} ({u.Capability})")))}");

        if (started.Count > 0)
        {
            _view.Show(started);
            ShowFor(NoticeFor);
        }
        else
        {
            RaiseChanged(); // the dots changed
        }
    }
}

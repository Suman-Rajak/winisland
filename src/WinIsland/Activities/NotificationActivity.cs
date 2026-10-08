using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// Notifications from other apps: a two-line banner for a few seconds when one arrives. Hovering it
/// while it's up opens the whole message (with Open and Clear) and keeps it open while you read;
/// after that, the island goes back to music or the clock. Needs WinIsland installed as a package
/// (see <see cref="NotificationMonitor"/>); otherwise this stays quiet.
/// </summary>
internal sealed class NotificationActivity : TransientActivity
{
    private static readonly TimeSpan BannerFor = TimeSpan.FromSeconds(5);

    private readonly NotificationMonitor _monitor;
    private readonly NotificationView _banner = new();
    private readonly NotificationExpandedView _expanded = new();
    private IslandNotification? _current;
    private bool _beingRead; // the expanded view is open: don't take it away mid-read

    public NotificationActivity(DispatcherQueue dispatcher) : base(dispatcher)
    {
        _monitor = new NotificationMonitor(dispatcher);
        _monitor.Arrived += notification => _ = ShowAsync(notification);
        _expanded.OpenClicked += Open;
        _expanded.ClearClicked += Clear;
    }

    public override string Name => "Notifications";

    /// <summary>Above battery and Bluetooth, below the volume (an answer to what you just did) and the mic/camera.</summary>
    public override int Priority => 92;

    public override string Glyph => ""; // Ringer

    public override PillShape CompactShape => new(380, 56, 28);

    public override PillShape ExpandedShape => new(420, 190, 34);

    public override FrameworkElement CompactView => _banner;

    public override FrameworkElement ExpandedView => _expanded;

    /// <summary>Not once it's been opened or cleared, even if its few seconds aren't up.</summary>
    public override bool WantsCompact => _current is not null && base.WantsCompact;

    /// <summary>Only while the banner is up (or being read), so the first tab goes back to music afterwards.</summary>
    public override bool HasExpanded => _current is not null && (WantsCompact || _beingRead);

    protected override bool IsEnabled => AppSettings.Current.ShowNotifications;

    public override async Task StartAsync()
    {
        var access = await _monitor.StartAsync();
        Log.Info($"Notifications: {access}");
    }

    public override void OnShown(bool compact, bool expanded)
    {
        if (expanded)
        {
            _beingRead = true;
        }
        else if (_beingRead)
        {
            _beingRead = false;
            RaiseChanged(); // finished reading: let the island move on
        }
    }

    /// <summary>A made-up message; for the --demo-notification switch.</summary>
    public void Demo()
    {
        _ = ShowAsync(new IslandNotification(0, "WhatsApp", "", null, "Rahul",
            "Are you coming tonight? We're meeting at 8 near the station, bring the charger you borrowed."));
        Pin();
    }

    private async Task ShowAsync(IslandNotification notification)
    {
        _current = notification;
        BitmapImage? icon = null;
        try
        {
            if (notification.App?.DisplayInfo?.GetLogo(new Size(64, 64)) is { } logo)
            {
                icon = new BitmapImage();
                await icon.SetSourceAsync(await logo.OpenReadAsync());
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Loading {notification.AppName}'s icon failed", ex);
        }

        if (_current != notification)
        {
            return; // a newer one arrived meanwhile
        }

        _banner.Show(notification, icon);
        _expanded.Show(notification, icon);
        ShowFor(BannerFor);
    }

    private void Open()
    {
        if (_current is { AppUserModelId.Length: > 0 } notification)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{notification.AppUserModelId}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error($"Opening {notification.AppName} failed", ex);
            }
        }
        Dismiss();
    }

    private void Clear()
    {
        if (_current is not null)
        {
            NotificationMonitor.Clear(_current.Id);
        }
        Dismiss();
    }

    private void Dismiss()
    {
        _current = null;
        _beingRead = false;
        RaiseChanged();
    }
}

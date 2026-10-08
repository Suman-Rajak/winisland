using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WinIsland.Services;

internal enum NotificationAccess
{
    Allowed,
    Denied,
    NeedsPackage,
}

/// <summary>A notification, boiled down to what the island shows.</summary>
internal sealed record IslandNotification(uint Id, string AppName, string AppUserModelId, AppInfo? App, string Title, string Body);

/// <summary>
/// New notifications from other apps, through Windows' notification listener. Only packaged apps
/// (MSIX) may use it, and only once you allow it. Desktop apps can't receive its change event, so
/// the notification list is re-read every <see cref="CheckEvery"/> and new ones are raised (on the UI thread).
/// Nothing is shown while Windows' Do Not Disturb is on.
/// </summary>
internal sealed class NotificationMonitor
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<uint> _seen = [];
    private DispatcherQueueTimer? _timer;
    private bool _checking;

    public NotificationMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event Action<IslandNotification>? Arrived;

    /// <summary>True when WinIsland runs as an installed (MSIX) package, which notifications need.</summary>
    public static bool HasPackageIdentity => AppPaths.IsPackaged;

    /// <summary>Asks for access (Windows shows its prompt the first time) and starts watching. Call on the UI thread.</summary>
    public async Task<NotificationAccess> StartAsync()
    {
        if (!HasPackageIdentity)
        {
            return NotificationAccess.NeedsPackage;
        }

        var listener = UserNotificationListener.Current;
        if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
        {
            return NotificationAccess.Denied;
        }

        // Whatever is already in the notification centre isn't news.
        foreach (var existing in await listener.GetNotificationsAsync(NotificationKinds.Toast))
        {
            _seen.Add(existing.Id);
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = CheckEvery;
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        return NotificationAccess.Allowed;
    }

    /// <summary>Removes a notification from Windows' notification centre.</summary>
    public static void Clear(uint id)
    {
        try
        {
            UserNotificationListener.Current.RemoveNotification(id);
        }
        catch (Exception ex)
        {
            Log.Error("Clearing a notification failed", ex);
        }
    }

    private async Task CheckAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        try
        {
            var current = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
            bool quiet = IsDoNotDisturbOn();
            foreach (var notification in current.Where(n => !_seen.Contains(n.Id)).OrderBy(n => n.CreationTime))
            {
                _seen.Add(notification.Id);
                if (!quiet && Describe(notification) is { } described)
                {
                    Arrived?.Invoke(described);
                }
            }

            // Forget ones that are gone, so the set doesn't grow forever.
            _seen.IntersectWith(current.Select(n => n.Id));
        }
        catch (Exception ex)
        {
            Log.Error("Reading notifications failed", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    private static IslandNotification? Describe(UserNotification notification)
    {
        var app = notification.AppInfo;
        string aumid = app?.AppUserModelId ?? string.Empty;
        if (HasPackageIdentity && aumid.StartsWith(Package.Current.Id.FamilyName, StringComparison.OrdinalIgnoreCase))
        {
            return null; // our own
        }

        var texts = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?
            .GetTextElements().Select(t => t.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList() ?? [];
        if (texts.Count == 0)
        {
            return null;
        }

        return new IslandNotification(
            notification.Id,
            app?.DisplayInfo?.DisplayName ?? "Notification",
            aumid,
            app,
            texts[0]!,
            string.Join(" ", texts.Skip(1)));
    }

    /// <summary>Windows' Do Not Disturb (or "priority only") mode.</summary>
    private static bool IsDoNotDisturbOn()
    {
        try
        {
            return ToastNotificationManager.GetDefault().NotificationMode != ToastNotificationMode.Unrestricted;
        }
        catch
        {
            return false;
        }
    }

}

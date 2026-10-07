using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Windows.Devices.Enumeration;

namespace WinIsland.Services;

internal enum BluetoothDeviceKind
{
    Headphones,
    Speaker,
    Phone,
    Mouse,
    Keyboard,
    Gamepad,
    Tablet,
    Computer,
    Other,
}

internal sealed record BluetoothDeviceInfo(string Id, string Name, BluetoothDeviceKind Kind, Guid? ContainerId)
{
    /// <summary>Mice and keyboards nap when idle and reconnect when touched; they're treated more quietly.</summary>
    public bool IsInputDevice => Kind is BluetoothDeviceKind.Mouse or BluetoothDeviceKind.Keyboard or BluetoothDeviceKind.Gamepad;
}

/// <summary>
/// Watches paired Bluetooth devices (classic and Low Energy) connecting and disconnecting, through
/// Windows' device enumeration events. <see cref="ConnectionChanged"/> is raised on the UI thread,
/// and only for changes after startup (devices already connected at launch are not announced).
/// </summary>
internal sealed partial class BluetoothMonitor
{
    private const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string ContainerIdKey = "System.Devices.Aep.ContainerId";
    private const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinorKey = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string AppearanceKey = "System.Devices.Aep.Bluetooth.Le.Appearance";

    /// <summary>DEVPKEY_Bluetooth_Battery: what Windows Settings shows, on the device's PnP nodes.</summary>
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, (BluetoothDeviceInfo Device, bool Connected)> _devices = [];
    private readonly List<DeviceWatcher> _watchers = [];
    private int _enumerationsPending;

    public BluetoothMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>A device and whether it's now connected.</summary>
    public event Action<BluetoothDeviceInfo, bool>? ConnectionChanged;

    /// <summary>
    /// One watcher per protocol, each limited to paired devices, so Windows reads its list of paired
    /// devices instead of scanning the air. (A single query OR-ing the two protocols makes Windows run
    /// an active Bluetooth scan, which is slow, costs battery and can stutter Bluetooth audio.)
    /// </summary>
    public void Start()
    {
        foreach (string protocol in new[] { ClassicProtocol, LowEnergyProtocol })
        {
            string aqs = $"System.Devices.Aep.ProtocolId:=\"{protocol}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            var watcher = DeviceInformation.CreateWatcher(
                aqs,
                [IsConnectedKey, ContainerIdKey, CodMajorKey, CodMinorKey, AppearanceKey],
                DeviceInformationKind.AssociationEndpoint);

            // Watcher events arrive on background threads.
            watcher.Added += (_, info) => _dispatcher.TryEnqueue(() => OnAdded(info));
            watcher.Updated += (_, update) => _dispatcher.TryEnqueue(() => OnUpdated(update));
            watcher.Removed += (_, update) => _dispatcher.TryEnqueue(() => _devices.Remove(update.Id));
            watcher.EnumerationCompleted += (_, _) => _dispatcher.TryEnqueue(OnEnumerationCompleted);
            watcher.Stopped += (sender, _) => Log.Info($"Bluetooth watcher stopped ({sender.Status})");

            _watchers.Add(watcher);
            _enumerationsPending++;
            watcher.Start();
        }
    }

    private bool Enumerated => _enumerationsPending == 0;

    private void OnEnumerationCompleted()
    {
        if (--_enumerationsPending == 0)
        {
            Log.Info($"Bluetooth: {_devices.Count} paired endpoints, {_devices.Values.Count(d => d.Connected)} connected");
        }
    }

    /// <summary>The device's battery as Windows last heard it, if the device reports one.</summary>
    public static async Task<int?> ReadBatteryAsync(BluetoothDeviceInfo device)
    {
        if (device.ContainerId is not { } container)
        {
            return null;
        }

        var nodes = await DeviceInformation.FindAllAsync(
            $"System.Devices.ContainerId:=\"{{{container}}}\"", [BatteryKey], DeviceInformationKind.Device);
        foreach (var node in nodes)
        {
            if (node.Properties.TryGetValue(BatteryKey, out var value) && value is not null)
            {
                return Convert.ToInt32(value);
            }
        }
        return null;
    }

    private void OnAdded(DeviceInformation info)
    {
        var device = new BluetoothDeviceInfo(
            info.Id,
            info.Name,
            KindOf(info.Name, info.Properties),
            info.Properties.TryGetValue(ContainerIdKey, out var container) && container is Guid id ? id : null);
        bool connected = info.Properties.TryGetValue(IsConnectedKey, out var value) && value is true;
        _devices[info.Id] = (device, connected);

        // A device paired (and connected) while we're running.
        if (Enumerated && connected)
        {
            ConnectionChanged?.Invoke(device, true);
        }
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!_devices.TryGetValue(update.Id, out var entry) ||
            !update.Properties.TryGetValue(IsConnectedKey, out var value) || value is not bool connected ||
            connected == entry.Connected)
        {
            return;
        }

        _devices[update.Id] = (entry.Device, connected);
        if (Enumerated)
        {
            ConnectionChanged?.Invoke(entry.Device, connected);
        }
    }

    /// <summary>
    /// From the Bluetooth class of device (classic) or appearance (Low Energy). Many speakers claim to be
    /// headsets (a JBL GO reports "wearable headset"), so audio devices also go by their name.
    /// </summary>
    private static BluetoothDeviceKind KindOf(string name, IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(AppearanceKey, out var appearanceValue) && appearanceValue is not null &&
            Convert.ToInt32(appearanceValue) is int appearance and not 0)
        {
            return (appearance >> 6) switch
            {
                1 => BluetoothDeviceKind.Phone,
                2 => BluetoothDeviceKind.Computer,
                10 => BluetoothDeviceKind.Tablet, // media player (an iPad says this)
                15 => (appearance & 0x3F) switch
                {
                    1 => BluetoothDeviceKind.Keyboard,
                    2 => BluetoothDeviceKind.Mouse,
                    3 or 4 => BluetoothDeviceKind.Gamepad,
                    _ => BluetoothDeviceKind.Other,
                },
                33 => BluetoothDeviceKind.Speaker,     // audio sink
                37 => AudioKind(name, speaker: false), // wearable audio
                _ => BluetoothDeviceKind.Other,
            };
        }

        if (properties.TryGetValue(CodMajorKey, out var majorValue) && majorValue is not null)
        {
            int minor = properties.TryGetValue(CodMinorKey, out var minorValue) && minorValue is not null ? Convert.ToInt32(minorValue) : 0;
            return Convert.ToInt32(majorValue) switch
            {
                1 => BluetoothDeviceKind.Computer,
                2 => BluetoothDeviceKind.Phone,
                4 => AudioKind(name, speaker: minor is 5 or 7 or 8 or 10), // loudspeaker, portable, car, hi-fi
                5 => ((minor >> 4) & 3) switch
                {
                    1 or 3 => BluetoothDeviceKind.Keyboard,
                    2 => BluetoothDeviceKind.Mouse,
                    _ => (minor & 0xF) is 1 or 2 ? BluetoothDeviceKind.Gamepad : BluetoothDeviceKind.Other,
                },
                _ => BluetoothDeviceKind.Other,
            };
        }

        return BluetoothDeviceKind.Other;
    }

    private static BluetoothDeviceKind AudioKind(string name, bool speaker) =>
        speaker || SpeakerName().IsMatch(name) ? BluetoothDeviceKind.Speaker : BluetoothDeviceKind.Headphones;

    [GeneratedRegex(@"speaker|sound ?bar|\bgo\b|stone|boom|flip|charge|soundlink|soundcore|partybox|xtreme|echo|homepod", RegexOptions.IgnoreCase)]
    private static partial Regex SpeakerName();
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Management.Deployment;

namespace WinIsland.Services;

internal enum Capability
{
    Microphone,
    Camera,
}

/// <param name="Key">The app's registry key name; identifies the app.</param>
internal sealed record CapabilityUse(Capability Capability, string App, string Key);

/// <summary>
/// Which apps are using the microphone or camera right now. Windows records every app's use in the
/// capability access "consent store" (what its own privacy indicator reads): an app is using the
/// device while its LastUsedTimeStop is 0. We ask to be told when the store changes, but in testing
/// (Windows 11 23H2) no registry notification ever arrived for these writes, so it's also re-read
/// every <see cref="CheckEvery"/>, which is what actually catches changes (a millisecond or so of
/// registry reads; ~0.07% CPU). The log says which of the two saw each change.
/// <see cref="Changed"/> is raised on the UI thread with the full current list.
/// </summary>
internal sealed class PrivacyMonitor
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly (Capability Capability, string Store)[] Stores =
        [(Capability.Microphone, "microphone"), (Capability.Camera, "webcam")];

    private const uint REG_NOTIFY_CHANGE_NAME = 0x1;
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, string> _appNames = [];
    private List<CapabilityUse> _published = [];

    public PrivacyMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event Action<IReadOnlyList<CapabilityUse>>? Changed;

    public void Start()
    {
        // Registry change notifications belong to the thread that asked for them, so a dedicated one waits.
        new Thread(Watch) { IsBackground = true, Name = "WinIsland privacy watcher" }.Start();
    }

    private void Watch()
    {
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(ConsentStore);
            if (store is null)
            {
                Log.Info("No capability consent store; the mic/camera indicator is off");
                return;
            }

            using var changed = new AutoResetEvent(false);
            bool armed = false;
            Publish(Scan(), how: "startup");
            while (true)
            {
                // Each registration fires once; only re-arm after it has, or they'd pile up.
                if (!armed)
                {
                    int result = RegNotifyChangeKeyValue(store.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                        changed.SafeWaitHandle, true);
                    armed = result == 0;
                }

                bool notified = changed.WaitOne(CheckEvery);
                if (notified)
                {
                    armed = false;
                    Thread.Sleep(150); // apps write start and stop values in bursts
                }
                Publish(Scan(), how: notified ? "notification" : "periodic check");
            }
        }
        catch (Exception ex)
        {
            Log.Error("The mic/camera watcher stopped", ex);
        }
    }

    private List<CapabilityUse> Scan()
    {
        var uses = new List<CapabilityUse>();
        foreach (var (capability, storeName) in Stores)
        {
            using var store = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{storeName}");
            if (store is null)
            {
                continue;
            }

            // Store apps are direct subkeys; everything else is under NonPackaged, keyed by path with \ as #.
            foreach (string name in store.GetSubKeyNames().Where(n => n != "NonPackaged"))
            {
                AddIfInUse(uses, store, name, capability, packaged: true);
            }
            using var nonPackaged = store.OpenSubKey("NonPackaged");
            foreach (string name in nonPackaged?.GetSubKeyNames() ?? [])
            {
                AddIfInUse(uses, nonPackaged!, name, capability, packaged: false);
            }
        }
        return uses;
    }

    private void AddIfInUse(List<CapabilityUse> uses, RegistryKey parent, string name, Capability capability, bool packaged)
    {
        using var app = parent.OpenSubKey(name);
        if (app?.GetValue("LastUsedTimeStart") is long start && start > 0 &&
            app.GetValue("LastUsedTimeStop") is long stop && stop == 0)
        {
            uses.Add(new CapabilityUse(capability, AppName(name, packaged), name));
        }
    }

    private void Publish(List<CapabilityUse> uses, string how)
    {
        if (uses.SequenceEqual(_published))
        {
            return;
        }

        _published = uses;
        Log.Info($"Mic/camera change seen by {how}");
        _dispatcher.TryEnqueue(() => Changed?.Invoke(uses));
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Camera"; "C:#…#Discord.exe" → "Discord".</summary>
    private string AppName(string key, bool packaged)
    {
        if (_appNames.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? name = null;
        try
        {
            if (packaged)
            {
                name = new PackageManager().FindPackagesForUser(string.Empty, key).FirstOrDefault()?.DisplayName;
                name ??= key.Split('_')[0].Split('.')[^1];
            }
            else
            {
                string path = key.Replace('#', '\\');
                if (File.Exists(path))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    name = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Path.GetFileNameWithoutExtension(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Naming '{key}' failed", ex);
        }

        return _appNames[key] = string.IsNullOrWhiteSpace(name) ? key : name.Trim();
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, uint dwNotifyFilter,
        SafeWaitHandle hEvent, bool fAsynchronous);
}

using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WinIsland.Interop;
using static WinIsland.Interop.CoreAudio;

namespace WinIsland.Services;

/// <summary>
/// Watches the default speakers' volume and mute, quietly following the default device when it changes
/// (headphones connected, say). <see cref="Changed"/> is raised on the UI thread.
/// </summary>
internal sealed class VolumeMonitor : IAudioEndpointVolumeCallback, IMMNotificationClient
{
    /// <summary>
    /// Newly connected devices set their own volume for a moment (Bluetooth headsets sync theirs), which
    /// isn't you changing the volume, so changes this soon after a switch aren't reported.
    /// </summary>
    private static readonly TimeSpan SettleAfterSwitch = TimeSpan.FromSeconds(4);

    private readonly DispatcherQueue _dispatcher;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private long _switchedAtTicks; // DateTime.UtcNow ticks; written and read on different threads

    public VolumeMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Level (0 to 1) and mute, when the volume is changed. Not raised for the initial state or device switches.</summary>
    public event Action<float, bool>? Changed;

    /// <summary>Call on the UI thread.</summary>
    public void Start()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(this));
        AttachToDefaultDevice();
    }

    private void AttachToDefaultDevice()
    {
        if (_volume is not null)
        {
            _volume.UnregisterControlChangeNotify(this);
            Marshal.ReleaseComObject(_volume);
            _volume = null;
        }

        if (_enumerator is null || _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0)
        {
            return; // no speakers at all
        }

        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance));
            _volume = (IAudioEndpointVolume)instance;
            Marshal.ThrowExceptionForHR(_volume.RegisterControlChangeNotify(this));
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private bool Settling =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _switchedAtTicks), DateTimeKind.Utc) < SettleAfterSwitch;

    // Both callbacks arrive on Core Audio's own threads.

    int IAudioEndpointVolumeCallback.OnNotify(IntPtr notifyData)
    {
        if (Settling)
        {
            return 0;
        }

        var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
        _dispatcher.TryEnqueue(() => Changed?.Invoke(data.MasterVolume, data.Muted != 0));
        return 0;
    }

    int IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {
        if (flow != EDataFlow.Render)
        {
            return 0;
        }

        Interlocked.Exchange(ref _switchedAtTicks, DateTime.UtcNow.Ticks);
        if (role == ERole.Multimedia)
        {
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    AttachToDefaultDevice();
                }
                catch (Exception ex)
                {
                    Log.Error("Following the new default audio device failed", ex);
                }
            });
        }
        return 0;
    }

    int IMMNotificationClient.OnDeviceStateChanged(string deviceId, int newState) => 0;

    int IMMNotificationClient.OnDeviceAdded(string deviceId) => 0;

    int IMMNotificationClient.OnDeviceRemoved(string deviceId) => 0;

    int IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
}

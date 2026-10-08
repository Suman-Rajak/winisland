using System.Runtime.InteropServices;
using static WinIsland.Interop.CoreAudio;

namespace WinIsland.Services;

/// <param name="Id">The Core Audio endpoint ID ("{0.0.1.00000000}.{…}"); null for Windows' default.</param>
internal sealed record MicrophoneChoice(string? Id, string Name, bool IsBluetooth);

/// <summary>
/// Chooses the microphone the assistant listens with: Windows' default, unless that's a Bluetooth
/// headset's. Opening a Bluetooth headset's microphone switches it into "hands-free" mode, which turns
/// whatever you're listening to into muffled phone-call audio for as long as it stays open, so the
/// built-in (or any other) microphone is used instead when there is one.
/// </summary>
internal static class MicrophonePicker
{
    public static MicrophoneChoice Choose()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            MicrophoneChoice? fallback = null;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out var defaultDevice) == 0)
            {
                var choice = Describe(defaultDevice);
                Marshal.ReleaseComObject(defaultDevice);
                if (!choice.IsBluetooth)
                {
                    return choice with { Id = null }; // the default: let Windows pick it, exactly as it would
                }
                fallback = choice;
            }

            if (enumerator.EnumAudioEndpoints(EDataFlow.Capture, DEVICE_STATE_ACTIVE, out var devices) == 0)
            {
                try
                {
                    devices.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (devices.Item(i, out var device) != 0)
                        {
                            continue;
                        }
                        var choice = Describe(device);
                        Marshal.ReleaseComObject(device);
                        if (!choice.IsBluetooth)
                        {
                            return choice;
                        }
                        fallback ??= choice;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(devices);
                }
            }

            return fallback ?? new MicrophoneChoice(null, "default microphone", IsBluetooth: false);
        }
        catch (Exception ex)
        {
            Log.Error("Listing microphones failed", ex);
            return new MicrophoneChoice(null, "default microphone", IsBluetooth: false);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static MicrophoneChoice Describe(IMMDevice device)
    {
        device.GetId(out string id);
        string? enumeratorName = GetString(device, PKEY_Device_EnumeratorName);
        string name = GetString(device, PKEY_Device_FriendlyName) ?? id;
        return new MicrophoneChoice(id, name, enumeratorName?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true);
    }
}

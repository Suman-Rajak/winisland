using System.Runtime.InteropServices;
using static WinIsland.Interop.CoreAudio;

namespace WinIsland.Services;

/// <summary>
/// Whether the PC is playing sound the microphone could pick up: through the default output, when that's
/// speakers (headphones and headsets don't reach the microphone). Follows the default output as it changes.
/// Use from one thread at a time.
/// </summary>
internal sealed class PlaybackMeter : IDisposable
{
    private const float Audible = 0.02f; // peak level (0 to 1): about -34 dB, quieter than any music or speech
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(3);

    private readonly bool _anyOutput;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter; // null when the output can't be heard (or there isn't one)
    private long _nextCheck; // when to look at which output is the default again (TickCount64)

    /// <param name="anyOutput">Measure headphones too: for how loud the PC is, rather than whether the microphone could hear it.</param>
    public PlaybackMeter(bool anyOutput = false)
    {
        _anyOutput = anyOutput;
    }

    public bool IsPlaying() => Peak() > Audible;

    /// <summary>How loud the default output is right now, 0 to 1 (0 when it can't be measured).</summary>
    public float Peak()
    {
        try
        {
            if (Environment.TickCount64 >= _nextCheck)
            {
                _nextCheck = Environment.TickCount64 + (long)Recheck.TotalMilliseconds;
                Attach();
            }
            return _meter is not null && _meter.GetPeakValue(out float peak) == 0 ? peak : 0;
        }
        catch (COMException)
        {
            Release(); // the device went away: look again next time
            return 0;
        }
    }

    private void Attach()
    {
        Release();
        _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0)
        {
            return;
        }

        try
        {
            if (!_anyOutput && GetUInt(device, PKEY_AudioEndpoint_FormFactor) is FormFactorHeadphones or FormFactorHeadset or FormFactorHandset)
            {
                return;
            }
            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance) == 0)
            {
                _meter = (IAudioMeterInformation)instance;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void Release()
    {
        if (_meter is not null)
        {
            Marshal.ReleaseComObject(_meter);
            _meter = null;
        }
    }

    public void Dispose()
    {
        Release();
        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }
}

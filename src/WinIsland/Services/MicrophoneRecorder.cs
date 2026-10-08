using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Render;
using WinRT;

namespace WinIsland.Services;

/// <summary>
/// Records a microphone through an AudioGraph into a rolling buffer of the last <see cref="Keep"/>,
/// which can be read back as 16 kHz mono, the format Whisper wants. Audio is addressed by position:
/// the number of samples recorded since the start (see <see cref="Position"/>). <see cref="Level"/>
/// reports the loudness about 30 times a second, on an audio thread.
/// </summary>
internal sealed class MicrophoneRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _input;
    private AudioFrameOutputNode? _output;
    private float[] _ring = []; // mono, at the graph's own sample rate
    private long _written;
    private int _sourceRate;
    private int _channels;
    private int _quantum;

    /// <summary>Loudness (RMS, 0 to 1) and the position it was measured at. Raised on an audio thread.</summary>
    public event Action<float, long>? Level;

    /// <summary>The microphone went away (unplugged, disabled). Raised on an arbitrary thread.</summary>
    public event Action? Failed;

    /// <summary>Samples recorded so far.</summary>
    public long Position
    {
        get
        {
            lock (_lock)
            {
                return _written;
            }
        }
    }

    /// <summary>How many samples (at the recording's own rate) last <paramref name="duration"/>.</summary>
    public long ToSamples(TimeSpan duration) => (long)(duration.TotalSeconds * _sourceRate);

    /// <summary>Starts recording from a microphone (null: Windows' default). Returns null, or why it couldn't.</summary>
    public async Task<string?> StartAsync(string? microphoneId = null)
    {
        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
        if (created.Status != AudioGraphCreationStatus.Success)
        {
            return $"Audio isn't available ({created.Status})";
        }
        _graph = created.Graph;

        // The shell knows microphones by a longer ID that contains Core Audio's.
        DeviceInformation? device = null;
        if (microphoneId is not null)
        {
            var microphones = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            device = microphones.FirstOrDefault(d => d.Id.Contains(microphoneId, StringComparison.OrdinalIgnoreCase));
        }

        var input = device is null
            ? await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech)
            : await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, _graph.EncodingProperties, device);
        if (input.Status != AudioDeviceNodeCreationStatus.Success)
        {
            Dispose();
            return input.Status == AudioDeviceNodeCreationStatus.AccessDenied
                ? "Microphone access is turned off in Windows Settings → Privacy → Microphone"
                : $"No microphone found ({input.Status})";
        }

        _input = input.DeviceInputNode;
        _output = _graph.CreateFrameOutputNode();
        _input.AddOutgoingConnection(_output);
        _sourceRate = (int)_graph.EncodingProperties.SampleRate;
        _channels = Math.Max(1, (int)_graph.EncodingProperties.ChannelCount);
        _ring = new float[(int)(_sourceRate * Keep.TotalSeconds)];
        _graph.QuantumStarted += OnQuantumStarted;
        _graph.UnrecoverableErrorOccurred += OnUnrecoverableError;
        _graph.Start();
        return null;
    }

    /// <summary>Audio between two positions, as 16 kHz mono; whatever's older than the buffer is gone.</summary>
    public float[] Read(long from, long to)
    {
        float[] mono;
        lock (_lock)
        {
            if (_ring.Length == 0)
            {
                return []; // never started
            }

            from = Math.Clamp(from, Math.Max(0, _written - _ring.Length), _written);
            to = Math.Clamp(to, from, _written);
            mono = new float[to - from];

            // At most two runs: up to the end of the ring, then from its start.
            int start = (int)(from % _ring.Length);
            int first = Math.Min(mono.Length, _ring.Length - start);
            Array.Copy(_ring, start, mono, 0, first);
            Array.Copy(_ring, 0, mono, first, mono.Length - first);
        }
        return Resample(mono, _sourceRate, SampleRate);
    }

    public void Dispose()
    {
        if (_graph is not null)
        {
            _graph.QuantumStarted -= OnQuantumStarted;
            _graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
            _graph.Stop();
            _graph.Dispose();
        }
        _graph = null;
        _input = null;
        _output = null;
    }

    private void OnUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        Log.Info($"The microphone stopped ({args.Error})");
        Failed?.Invoke();
    }

    private void OnQuantumStarted(AudioGraph sender, object args)
    {
        // Runs on the audio thread, so it can race with Dispose(): work from a copy, and never throw back into the graph.
        if (_output is not { } output)
        {
            return;
        }

        try
        {
            ReadFrame(output);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or COMException)
        {
            // stopped mid-read
        }
    }

    private unsafe void ReadFrame(AudioFrameOutputNode output)
    {
        using var frame = output.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out uint capacity);

        // The graph works in 32-bit float, interleaved channels; mix down to mono.
        int frames = (int)(Math.Min(buffer.Length, capacity) / sizeof(float)) / _channels;
        float* samples = (float*)data;
        double sumSquares = 0;
        long position;
        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++)
                {
                    sum += samples[i * _channels + c];
                }
                float mono = sum / _channels;
                _ring[(int)(_written++ % _ring.Length)] = mono;
                sumSquares += mono * mono;
            }
            position = _written;
        }

        // A quantum is 10 ms; report every third.
        if (frames > 0 && ++_quantum % 3 == 0)
        {
            Level?.Invoke((float)Math.Sqrt(sumSquares / frames), position);
        }
    }

    /// <summary>Averages over each output sample's span: crude, but plenty for speech going down to 16 kHz.</summary>
    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0)
        {
            return input;
        }

        double step = (double)fromRate / toRate;
        var output = new float[(int)(input.Length / step)];
        for (int i = 0; i < output.Length; i++)
        {
            int start = (int)(i * step), end = Math.Min(input.Length, Math.Max(start + 1, (int)((i + 1) * step)));
            float sum = 0;
            for (int j = start; j < end; j++)
            {
                sum += input[j];
            }
            output[i] = sum / (end - start);
        }
        return output;
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}

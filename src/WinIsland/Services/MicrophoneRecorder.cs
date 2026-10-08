using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;
using WinRT;

namespace WinIsland.Services;

/// <summary>
/// Records the default microphone through an AudioGraph and hands back 16 kHz mono samples, the format
/// Whisper wants. <see cref="Level"/> reports the loudness (0 to 1) about 30 times a second, on an audio
/// thread, for the live waveform and for noticing when you've stopped talking.
/// </summary>
internal sealed class MicrophoneRecorder : IDisposable
{
    public const int SampleRate = 16000;

    private readonly List<float> _mono = []; // at the graph's own sample rate
    private readonly object _lock = new();
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _input;
    private AudioFrameOutputNode? _output;
    private int _sourceRate;
    private int _channels;
    private int _quantum;

    public event Action<float>? Level;

    /// <summary>Starts recording. Returns null, or why it couldn't (no microphone, access turned off…).</summary>
    public async Task<string?> StartAsync()
    {
        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
        if (created.Status != AudioGraphCreationStatus.Success)
        {
            return $"Audio isn't available ({created.Status})";
        }
        _graph = created.Graph;

        var input = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech);
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
        _graph.QuantumStarted += OnQuantumStarted;
        _graph.Start();
        return null;
    }

    /// <summary>Stops and returns everything recorded, as 16 kHz mono.</summary>
    public float[] Stop()
    {
        _graph?.Stop();
        float[] mono;
        lock (_lock)
        {
            mono = [.. _mono];
            _mono.Clear();
        }
        Dispose();
        return Resample(mono, _sourceRate, SampleRate);
    }

    public void Dispose()
    {
        if (_graph is not null)
        {
            _graph.QuantumStarted -= OnQuantumStarted;
            _graph.Dispose();
        }
        _graph = null;
        _input = null;
        _output = null;
    }

    private void OnQuantumStarted(AudioGraph sender, object args)
    {
        // Runs on the audio thread, so it can race with Stop(): work from a copy, and never throw back into the graph.
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
                _mono.Add(mono);
                sumSquares += mono * mono;
            }
        }

        // A quantum is 10 ms; report every third.
        if (frames > 0 && ++_quantum % 3 == 0)
        {
            Level?.Invoke((float)Math.Sqrt(sumSquares / frames));
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

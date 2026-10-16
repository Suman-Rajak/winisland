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
/// <remarks>
/// It records the microphone twice over: through Windows' speech processing, which takes out what the
/// PC itself is playing (and the background hum) but turns quiet voices up until they clip, and as it
/// is. Whisper understands the voice as it is far better (in a test, "translate this into Hindi" came
/// out of the processed recording as "Thanks, you're a good person"), so that's what's read back,
/// unless the PC's speakers were playing at the time (and weren't paused or turned down for it): then
/// the processed one, without the music.
/// Loudness comes from the processed one: steadier, so pauses are easier to tell.
/// </remarks>
internal sealed class MicrophoneRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _input;
    private AudioFrameOutputNode? _output;
    private AudioDeviceInputNode? _rawInput;
    private AudioFrameOutputNode? _rawOutput;
    private float[] _ring = [];    // processed, mono, at the graph's own sample rate
    private float[] _rawRing = []; // as recorded, the same positions; empty when there's no such stream
    private long _written;
    private int _sourceRate;
    private int _channels;
    private int _quantum;

    // Whether the PC's speakers are playing, checked off the audio thread; and the last position they were.
    private readonly PlaybackMeter _playback = new();
    private Timer? _playbackTimer;
    private volatile bool _playing;
    private long _playedAt = long.MinValue;

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

        // The same microphone as it is ("media": no speech processing). Not every driver allows a second
        // stream; then the processed one does for everything.
        var raw = device is null
            ? await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media)
            : await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _graph.EncodingProperties, device);
        if (raw.Status == AudioDeviceNodeCreationStatus.Success)
        {
            _rawInput = raw.DeviceInputNode;
            _rawOutput = _graph.CreateFrameOutputNode();
            _rawInput.AddOutgoingConnection(_rawOutput);
        }
        else
        {
            Log.Info($"The microphone gives only one stream ({raw.Status}); using Windows' speech processing");
        }

        _sourceRate = (int)_graph.EncodingProperties.SampleRate;
        _channels = Math.Max(1, (int)_graph.EncodingProperties.ChannelCount);
        _ring = new float[(int)(_sourceRate * Keep.TotalSeconds)];
        _rawRing = _rawOutput is null ? [] : new float[_ring.Length];
        _graph.QuantumStarted += OnQuantumStarted;
        _graph.UnrecoverableErrorOccurred += OnUnrecoverableError;
        _graph.Start();
        if (_rawOutput is not null)
        {
            _playbackTimer = new Timer(_ => _playing = _playback.IsPlaying(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        }
        return null;
    }

    /// <summary>
    /// Audio between two positions, as 16 kHz mono; whatever's older than the buffer is gone. As recorded,
    /// unless the PC's speakers played meanwhile (see the remarks above).
    /// </summary>
    /// <param name="playbackQuieted">The PC's sound was paused or turned down meanwhile, so as recorded regardless.</param>
    public float[] Read(long from, long to, bool playbackQuieted = false)
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
            var ring = _rawRing.Length > 0 && (playbackQuieted || _playedAt < from) ? _rawRing : _ring;

            // At most two runs: up to the end of the ring, then from its start.
            int start = (int)(from % ring.Length);
            int first = Math.Min(mono.Length, ring.Length - start);
            Array.Copy(ring, start, mono, 0, first);
            Array.Copy(ring, 0, mono, first, mono.Length - first);
        }
        return Resample(mono, _sourceRate, SampleRate);
    }

    /// <summary>
    /// Audio as 16 kHz mono from <paramref name="next"/> (a position, fractional: where the last piece
    /// ended) up to just short of <paramref name="to"/>, moving <paramref name="next"/> on: for streaming,
    /// in pieces that join up seamlessly. (Pieces from <see cref="Read"/> wouldn't: its resampling filter
    /// has nothing to look at past either end.) As recorded or not, as Read decides.
    /// </summary>
    public float[] ReadOn(ref double next, long to, bool playbackQuieted = false)
    {
        if (_sourceRate == 0)
        {
            return []; // never started
        }

        // Each 16 kHz sample needs the filter's half-width of audio after it, so the last few wait for the next piece.
        var filter = _sourceRate == SampleRate ? null : Filter(_sourceRate, SampleRate);
        int half = filter?.Half ?? 0;
        double step = (double)_sourceRate / SampleRate;
        double last = to - 1 - half; // the latest position a sample can be worked out at
        int count = last < next ? 0 : (int)Math.Floor((last - next) / step) + 1;
        if (count == 0)
        {
            return [];
        }

        long start = (long)Math.Floor(next) - half;
        var input = new float[to - start];
        lock (_lock)
        {
            var ring = _rawRing.Length > 0 && (playbackQuieted || _playedAt < start) ? _rawRing : _ring;
            long from = Math.Max(start, Math.Max(0, _written - ring.Length)); // (anything older is gone: silence)
            for (long position = from; position < Math.Min(to, _written); position++)
            {
                input[position - start] = ring[(int)(position % ring.Length)];
            }
        }

        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double t = next + i * step - start;
            output[i] = filter?.At(input, t) ?? input[(int)t];
        }
        next += count * step;
        return output;
    }

    /// <summary>Whether the PC's speakers played at or after a position.</summary>
    public bool PlayedSince(long position)
    {
        lock (_lock)
        {
            return _playedAt >= position;
        }
    }

    public void Dispose()
    {
        if (_playbackTimer is not null)
        {
            using var stopped = new ManualResetEvent(false);
            _playbackTimer.Dispose(stopped);
            stopped.WaitOne(TimeSpan.FromSeconds(1)); // a check under way finishes before the meter goes
            _playbackTimer = null;
        }
        _playback.Dispose();
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
        _rawInput = null;
        _rawOutput = null;
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
            ReadFrame(output, _rawOutput);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or COMException)
        {
            // stopped mid-read
        }
    }

    /// <summary>This quantum's audio from both streams, into their rings at the same positions.</summary>
    private void ReadFrame(AudioFrameOutputNode output, AudioFrameOutputNode? rawOutput)
    {
        double sumSquares;
        int frames;
        long position;
        lock (_lock)
        {
            long from = _written;
            (frames, sumSquares) = CopyFrame(output, _ring, from, int.MaxValue);
            if (rawOutput is not null && _rawRing.Length > 0)
            {
                int rawFrames = CopyFrame(rawOutput, _rawRing, from, frames).Frames;
                for (int i = rawFrames; i < frames; i++)
                {
                    _rawRing[(int)((from + i) % _rawRing.Length)] = 0; // keep the two in step
                }
            }
            _written += frames;
            if (_playing)
            {
                _playedAt = _written;
            }
            position = _written;
        }

        // A quantum is 10 ms; report every third.
        if (frames > 0 && ++_quantum % 3 == 0)
        {
            Level?.Invoke((float)Math.Sqrt(sumSquares / frames), position);
        }
    }

    /// <summary>A frame's audio, mixed down to mono, into a ring from a position; at most <paramref name="max"/> samples. Returns how many, and their sum of squares.</summary>
    private unsafe (int Frames, double SumSquares) CopyFrame(AudioFrameOutputNode output, float[] ring, long from, int max)
    {
        using var frame = output.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out uint capacity);

        // The graph works in 32-bit float, interleaved channels; mix down to mono.
        int frames = Math.Min(max, (int)(Math.Min(buffer.Length, capacity) / sizeof(float)) / _channels);
        float* samples = (float*)data;
        double sumSquares = 0;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                sum += samples[i * _channels + c];
            }
            float mono = sum / _channels;
            ring[(int)((from + i) % ring.Length)] = mono;
            sumSquares += mono * mono;
        }
        return (frames, sumSquares);
    }

    /// <summary>
    /// Band-limited resampling: a windowed-sinc low-pass just under the lower rate's Nyquist frequency,
    /// evaluated where each output sample falls. (Averaging each output sample's span instead, as this
    /// once did, lets the 8–24 kHz hiss and sibilants of a 48 kHz microphone fold down into the speech
    /// band, smearing the consonants Whisper tells words apart by.)
    /// </summary>
    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0)
        {
            return input;
        }

        var filter = Filter(fromRate, toRate);
        double step = (double)fromRate / toRate;
        var output = new float[(int)(input.Length / step)];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = filter.At(input, i * step);
        }
        return output;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int From, int To), ResamplingFilter> s_filters = new();

    private static ResamplingFilter Filter(int fromRate, int toRate) =>
        s_filters.GetOrAdd((fromRate, toRate), rates => new ResamplingFilter(rates.From, rates.To));

    /// <summary>The low-pass kernel, worked out ahead for <see cref="Phases"/> fractional positions between input samples.</summary>
    private sealed class ResamplingFilter
    {
        private const int Phases = 64;
        private readonly float[][] _kernels; // per phase, taps for input samples floor(t) - half + 1 … floor(t) + half
        private readonly int _half;

        /// <summary>How many input samples either side of a position the filter looks at.</summary>
        public int Half => _half;

        public ResamplingFilter(int fromRate, int toRate)
        {
            // Pass up to 90% of the lower rate's Nyquist frequency (7.2 kHz for 16 kHz); a Blackman window
            // over ~20 / cutoff input samples makes the fall-off (~2 kHz) and the stopband (-74 dB).
            double cutoff = 0.45 * Math.Min(fromRate, toRate) / fromRate; // in cycles per input sample
            _half = (int)Math.Ceiling(10 / cutoff);
            _kernels = new float[Phases][];
            for (int p = 0; p < Phases; p++)
            {
                double fraction = (double)p / Phases;
                var taps = new float[2 * _half];
                double sum = 0;
                for (int j = 0; j < taps.Length; j++)
                {
                    double u = j - _half + 1 - fraction; // distance from the output sample, in input samples
                    double x = 2 * cutoff * u;
                    double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    double w = Math.Clamp(u / _half, -1, 1);
                    double window = 0.42 + 0.5 * Math.Cos(Math.PI * w) + 0.08 * Math.Cos(2 * Math.PI * w);
                    taps[j] = (float)(sinc * window);
                    sum += taps[j];
                }
                for (int j = 0; j < taps.Length; j++)
                {
                    taps[j] = (float)(taps[j] / sum); // unity gain: speech keeps its level
                }
                _kernels[p] = taps;
            }
        }

        /// <summary>The filtered signal at input position <paramref name="t"/> (fractional).</summary>
        public float At(float[] input, double t)
        {
            int index = (int)Math.Floor(t);
            int phase = (int)Math.Round((t - index) * Phases);
            if (phase == Phases)
            {
                index++;
                phase = 0;
            }

            var taps = _kernels[phase];
            int first = index - _half + 1;
            int from = Math.Max(0, -first), to = Math.Min(taps.Length, input.Length - first);
            float sum = 0;
            for (int j = from; j < to; j++)
            {
                sum += input[first + j] * taps[j];
            }
            return sum;
        }
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}

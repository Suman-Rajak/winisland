using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace WinIsland.Services;

/// <summary>
/// Turns speech into text on this PC with OpenAI's Whisper (the "small" model, through whisper.cpp).
/// It runs on the graphics card through Vulkan when there is one, else on the CPU. The model (465 MB)
/// is downloaded once from Hugging Face into the data folder; nothing you say leaves the PC.
/// Loading the model takes a second or two, so it stays loaded until it's been unused for a while
/// (or for good, while listening for the wake phrase).
/// </summary>
internal sealed class SpeechToText
{
    private const string ModelFile = "ggml-small.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(10);

    // Nudges spelling toward the assistant's name and the commands it knows.
    private static readonly string s_prompt = $"{Assistant.WakePhrase}, pause the music. Next song. Volume 30. Open Chrome.";

    private static readonly string s_modelPath = Path.Combine(AppPaths.DataFolder, "Models", ModelFile);

    /// <summary>The languages requests may be in; the first is the fallback.</summary>
    private static readonly string[] s_languages = ["en", "hi"];

    // The wake phrase at the start of what was said: "hey siro", "ok siro…". The check is told to expect
    // the name, so it spells it right. (Told nothing, it writes real voices as "a0" or even "(laughing)";
    // told to expect rivals too, "Hey Siri, hey hey hey"; both lose more real wake-ups than they save.)
    // Noise doesn't fool it: it's captioned ("static", "music"). Some voices' "Hey Sarah" can.
    private static readonly string s_wakePrompt = $"{Assistant.WakePhrase}.";
    private static readonly Regex s_wakePhrase = new(
        $@"^(hey|hi|high|hay|ok|okay|a|eh) ({string.Join("|", [Assistant.Name.ToLowerInvariant(), .. Assistant.SoundsLike])})(?!\S)");
    private static readonly TimeSpan WakeCheckLength = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _unloadTimer;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private WhisperProcessor? _wakeCheck; // short audio, English, no retries
    private readonly HashSet<WhisperProcessor> _warmed = [];
    private Task? _download;

    static SpeechToText()
    {
        // Graphics card first, then the CPU. (CUDA would be faster on NVIDIA cards but needs a 600 MB+ runtime.)
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        LogProvider.AddLogger((level, message) =>
        {
            if (level == WhisperLogLevel.Error)
            {
                Log.Info($"Whisper: {message?.Trim()}");
            }
        });
    }

    public SpeechToText()
    {
        _unloadTimer = new Timer(_ => _ = UnloadAsync());
    }

    public static bool IsModelDownloaded => File.Exists(s_modelPath);

    /// <summary>Keep the model loaded even when unused (while listening for the wake phrase).</summary>
    public bool KeepLoaded { get; set; }

    /// <summary>
    /// Downloads the model, reporting progress (0 to 1). Calling it again while a download is running
    /// joins that one. Downloads to a .part file first, so a half-finished download is never mistaken for the model.
    /// </summary>
    public Task DownloadModelAsync(IProgress<double> progress) => _download is { IsCompleted: false } running
        ? running
        : _download = DownloadAsync(progress);

    private static async Task DownloadAsync(IProgress<double> progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(s_modelPath)!);
        string partial = s_modelPath + ".part";
        Log.Info($"Downloading the speech model from {ModelUrl}");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland");
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            double reported = 0;
            int read;
            while ((read = await ReadWithTimeoutAsync(source, buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                double fraction = total > 0 ? (double)received / total : 0;
                if (fraction - reported >= 0.005)
                {
                    reported = fraction;
                    progress.Report(fraction);
                }
            }

            if (total > 0 && received != total)
            {
                throw new IOException($"The download stopped early ({received:N0} of {total:N0} bytes)");
            }
        }

        File.Move(partial, s_modelPath, overwrite: true);
        Log.Info("Speech model downloaded");
    }

    /// <summary>A stalled connection would otherwise wait forever (the client has no overall timeout: the file is big).</summary>
    private static async Task<int> ReadWithTimeoutAsync(Stream source, byte[] buffer)
    {
        using var stalled = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await source.ReadAsync(buffer, stalled.Token);
    }

    /// <summary>
    /// Loads the model ahead of time (say, while you're still talking), and gets the graphics card ready
    /// by running a moment of silence through it: the first run sets up the GPU code, which can take
    /// many seconds. <paramref name="wakeCheck"/> readies the wake-phrase check too. Needs the model downloaded.
    /// </summary>
    public Task WarmUpAsync(bool wakeCheck = false) => Task.Run(async () =>
    {
        await _lock.WaitAsync();
        try
        {
            EnsureLoaded();
            var silence = new float[MicrophoneRecorder.SampleRate];
            var watch = Stopwatch.StartNew();
            foreach (var processor in wakeCheck ? new[] { _processor!, _wakeCheck! } : new[] { _processor! })
            {
                if (_warmed.Add(processor))
                {
                    await foreach (var _ in processor.ProcessAsync(silence))
                    {
                    }
                }
            }
            if (watch.ElapsedMilliseconds > 100)
            {
                Log.Info($"Speech recognition warmed up in {watch.ElapsedMilliseconds} ms");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Loading the speech model failed", ex);
        }
        finally
        {
            _lock.Release();
        }
    });

    /// <summary>Transcribes 16 kHz mono audio. Returns the text, trimmed; empty if nothing was said.</summary>
    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation) => Task.Run(async () =>
    {
        await _lock.WaitAsync(cancellation);
        try
        {
            EnsureLoaded();
            var watch = Stopwatch.StartNew();

            // Left to guess from a few seconds, Whisper takes plain English for Spanish, Hungarian, even
            // Sinhala (and then struggles for seconds), so it only chooses between the languages you speak.
            var (language, _) = _processor!.DetectLanguageWithProbability(samples, s_languages);
            _processor.ChangeLanguage(language ?? s_languages[0]);

            var text = new List<string>();
            await foreach (var segment in _processor.ProcessAsync(samples, cancellation))
            {
                text.Add(segment.Text);
            }

            string result = CleanUp(string.Concat(text));
            Log.Info($"Transcribed {samples.Length / (double)MicrophoneRecorder.SampleRate:0.0} s of speech in " +
                     $"{watch.ElapsedMilliseconds} ms ({language}): \"{result}\"");
            return result;
        }
        finally
        {
            _unloadTimer.Change(UnloadAfter, Timeout.InfiniteTimeSpan);
            _lock.Release();
        }
    }, cancellation);

    /// <summary>
    /// Whether 16 kHz mono audio (up to ~2 s) starts with the wake phrase: one quick pass (~0.2 s on a
    /// graphics card); see the pattern above. Returns null if it doesn't; otherwise whether more was said
    /// after it ("hey siro, what time…").
    /// </summary>
    public Task<bool?> CheckWakePhraseAsync(float[] samples) => Task.Run(async () =>
    {
        await _lock.WaitAsync();
        try
        {
            EnsureLoaded();
            var audio = new float[Math.Max(samples.Length, (int)(WakeCheckLength.TotalSeconds * MicrophoneRecorder.SampleRate))];
            samples.CopyTo(audio, 0);

            string heard = await TranscribeForWakeAsync(_wakeCheck!, audio);
            var wake = s_wakePhrase.Match(heard);
            if (!wake.Success)
            {
                return (bool?)null;
            }
            Log.Info("Heard the wake phrase");
            return heard.Length > wake.Length;
        }
        finally
        {
            _lock.Release();
        }
    });

    /// <summary>Lower case, words only: "Hey, Siro!" → "hey siro".</summary>
    private static async Task<string> TranscribeForWakeAsync(WhisperProcessor processor, float[] audio)
    {
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio))
        {
            text.Append(segment.Text).Append(' ');
        }
        return Regex.Replace(text.ToString().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    }

    /// <summary>Call while holding the lock.</summary>
    private void EnsureLoaded()
    {
        if (_processor is not null)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        _factory = WhisperFactory.FromPath(s_modelPath, new WhisperFactoryOptions { UseGpu = true });
        _processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .WithPrompt(s_prompt)
            .WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();

        // The wake check looks at ~2 s of English (the encoder only covers the first 5 s: much faster), and
        // takes Whisper's first answer instead of retrying when it's unsure (which could take seconds).
        _wakeCheck = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(s_wakePrompt)
            .WithNoContext()
            .WithAudioContextSize(256)
            .WithTemperatureInc(0)
            .WithMaxTokensPerSegment(24)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();
        Log.Info($"Speech model loaded in {watch.ElapsedMilliseconds} ms, running on {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}");
        _unloadTimer.Change(UnloadAfter, Timeout.InfiniteTimeSpan);
    }

    private async Task UnloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_processor is null || KeepLoaded)
            {
                return;
            }

            await _processor.DisposeAsync();
            await _wakeCheck!.DisposeAsync();
            _factory?.Dispose();
            _warmed.Clear();
            _processor = null;
            _wakeCheck = null;
            _factory = null;
            Log.Info("Speech model unloaded (unused for a while)");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Whisper marks non-speech with things like "[BLANK_AUDIO]" or "(music)", and on silence it
    /// sometimes invents a line from its training data ("Thank you."). Drop those.
    /// </summary>
    private static string CleanUp(string text)
    {
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Trim('.', ' ') is "" or "Thank you" or "Thanks for watching" or "you" ? "" : text;
    }
}

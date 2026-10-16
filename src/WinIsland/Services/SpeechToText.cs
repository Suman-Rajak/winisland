using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace WinIsland.Services;

/// <summary>A Whisper model file, downloaded once from Hugging Face into Models in the data folder.</summary>
/// <param name="Size">For messages: "574 MB".</param>
internal sealed record SpeechModel(string File, string Size)
{
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{File}";

    public string Path => System.IO.Path.Combine(AppPaths.DataFolder, "Models", File);

    public bool IsDownloaded => System.IO.File.Exists(Path);
}

/// <summary>
/// Turns speech into text on this PC with OpenAI's Whisper (through whisper.cpp), on the graphics card
/// through Vulkan when there is one, else the CPU. Nothing you say leaves the PC. Two models:
/// <list type="bullet">
/// <item>requests: large-v3-turbo (quantised, 574 MB), which copes with accents, laptop mics, noise and
/// Hindi far better than smaller models (the small one wrote "capital of Peru" as "capital of the
/// room"). Until it's downloaded, the small model stands in.</item>
/// <item>the wake phrase: small (465 MB), quick enough to check everything said near the PC. It also
/// takes quick looks at a request while you're still saying it, to show your words as you speak.</item>
/// </list>
/// Loading takes a second or two, so a model stays loaded until it's been unused for a while (the
/// wake-phrase one for good, while listening for it).
/// </summary>
internal sealed class SpeechToText
{
    public static readonly SpeechModel RequestModel = new("ggml-large-v3-turbo-q5_0.bin", "574 MB");
    public static readonly SpeechModel WakeModel = new("ggml-small.bin", "465 MB");

    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(10);

    // Nudges spelling toward the assistant's name and the commands it knows.
    private static readonly string s_prompt = $"{Assistant.WakePhrase}, pause the music. Next song. Volume 30. Open Chrome.";


    // The wake phrase at the start of what was said: "hey glyde", "ok glyde…". The check is told to expect
    // the name, so it spells it right. (Told nothing, it writes real voices as "a0" or even "(laughing)";
    // told to expect rivals too, "Hey Siri, hey hey hey"; both lose more real wake-ups than they save.)
    // Noise doesn't fool it: it's captioned ("static", "music"); nor does "hey guys". "Hey Clyde" and
    // "hey guide" can (rare enough to live with).
    private static readonly string s_wakePrompt = $"{Assistant.WakePhrase}.";
    private static readonly Regex s_wakePhrase = new(
        $@"^(hey|hi|high|hay|ok|okay|a|eh) ({string.Join("|", [Assistant.Name.ToLowerInvariant(), .. Assistant.SoundsLike])})(?!\S)");
    private static readonly TimeSpan WakeCheckLength = TimeSpan.FromSeconds(3);

    // A look at what you're saying covers 768 × 20 ms = 15.4 s of audio, as long as a request can be.
    private const int PreviewAudioContext = 768;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _unloadTimer;
    private readonly HashSet<WhisperProcessor> _warmed = [];
    private readonly Dictionary<SpeechModel, Task> _downloads = [];

    private WhisperFactory? _requestFactory;
    private SpeechModel? _requestFactoryModel;
    private WhisperProcessor? _processor;
    private WhisperProcessor? _languageCheck; // small model: English or Hindi?
    private WhisperFactory? _wakeFactory;
    private WhisperProcessor? _wakeCheck; // short audio, English, no retries
    private WhisperProcessor? _preview;   // small model, quick: what you're saying, while you say it

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
        _unloadTimer = new Timer(_ => _ = UnloadRequestModelAsync());
    }

    /// <summary>Requests can be understood: the request model is downloaded, or the small one to stand in.</summary>
    public static bool CanTranscribe => RequestModel.IsDownloaded || WakeModel.IsDownloaded;

    /// <summary>
    /// Quick looks at a request can be taken while it's said (see <see cref="PreviewAsync"/>): the small
    /// model is downloaded, and there's a graphics card (known once a model has loaded).
    /// </summary>
    public static bool CanPreview => WakeModel.IsDownloaded && RuntimeOptions.LoadedLibrary != RuntimeLibrary.Cpu;

    /// <summary>Keep the wake-phrase model loaded even when unused (while listening for it).</summary>
    public bool KeepLoaded { get; set; }

    /// <summary>
    /// The languages requests may be in (Whisper codes); the first is the fallback. With more than one,
    /// each request is checked for which it is, which isn't always right: it took Indian English for
    /// Hindi, and wrote it out as Hindi nonsense. So it's only checked when you speak both.
    /// </summary>
    public IReadOnlyList<string> Languages { get; set; } = ["en"];

    /// <summary>
    /// Downloads a model, reporting progress (0 to 1). Calling it again while that download is running
    /// joins it. Downloads to a .part file first, so a half-finished download is never mistaken for the model.
    /// </summary>
    public Task DownloadAsync(SpeechModel model, IProgress<double> progress)
    {
        lock (_downloads)
        {
            if (_downloads.TryGetValue(model, out var running) && !running.IsCompleted)
            {
                return running;
            }
            return _downloads[model] = DownloadModelAsync(model, progress);
        }
    }

    private async Task DownloadModelAsync(SpeechModel model, IProgress<double> progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(model.Path)!);
        string partial = model.Path + ".part";
        Log.Info($"Downloading the speech model from {model.Url}");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Product.Name);
        using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead);
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

        File.Move(partial, model.Path, overwrite: true);
        Log.Info($"Speech model {model.File} downloaded");

        // The better request model arrived: use it from the next request (the stand-in is let go).
        if (model == RequestModel)
        {
            await UnloadRequestModelAsync(force: true);
        }
    }

    /// <summary>A stalled connection would otherwise wait forever (the client has no overall timeout: the file is big).</summary>
    private static async Task<int> ReadWithTimeoutAsync(Stream source, byte[] buffer)
    {
        using var stalled = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await source.ReadAsync(buffer, stalled.Token);
    }

    /// <summary>
    /// Loads the request model ahead of time (say, while you're still talking), and gets the graphics card
    /// ready by running a moment of silence through it: the first run sets up the GPU code, which can take
    /// many seconds.
    /// </summary>
    public Task WarmUpAsync() => Task.Run(() => CanTranscribe ? WarmUpAsync(EnsureRequestLoaded, "request model") : Task.CompletedTask);

    /// <summary>
    /// Readies the wake-phrase check. Only that: readying the request model keeps the graphics card busy
    /// for seconds, and a wake phrase said meanwhile would have to wait. It gets ready while you say your
    /// request instead.
    /// </summary>
    public Task WarmUpWakeCheckAsync() => Task.Run(() => WakeModel.IsDownloaded ? WarmUpAsync(EnsureWakeLoaded, "wake-phrase check") : Task.CompletedTask);

    private async Task WarmUpAsync(Func<WhisperProcessor> load, string what)
    {
        await _lock.WaitAsync();
        try
        {
            var processor = load();
            if (!_warmed.Add(processor))
            {
                return;
            }
            var watch = Stopwatch.StartNew();
            await foreach (var _ in processor.ProcessAsync(new float[MicrophoneRecorder.SampleRate]))
            {
            }
            if (watch.ElapsedMilliseconds > 100)
            {
                Log.Info($"Speech recognition ({what}) warmed up in {watch.ElapsedMilliseconds} ms");
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
    }

    /// <summary>Transcribes 16 kHz mono audio. Returns the text, trimmed; empty if nothing was said.</summary>
    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation) => Task.Run(async () =>
    {
        await _lock.WaitAsync(cancellation);
        try
        {
            var processor = EnsureRequestLoaded();
            var watch = Stopwatch.StartNew();
            string language = ChooseLanguage(samples);
            processor.ChangeLanguage(language);

            var text = new List<string>();
            await foreach (var segment in processor.ProcessAsync(samples, cancellation))
            {
                text.Add(segment.Text);
            }

            string raw = string.Concat(text).Trim();
            string result = CleanUp(raw);
            Log.Info($"Transcribed {samples.Length / (double)MicrophoneRecorder.SampleRate:0.0} s of speech in " +
                     $"{watch.ElapsedMilliseconds} ms ({language}, {_requestFactoryModel?.File}): \"{result}\"" +
                     (result.Length == 0 && raw.Length > 0 ? $" (dropped \"{raw}\")" : ""));
            return result;
        }
        finally
        {
            _unloadTimer.Change(UnloadAfter, Timeout.InfiniteTimeSpan);
            _lock.Release();
        }
    }, cancellation);

    /// <summary>
    /// A quick look at what's been said so far, while you're still talking, to show it: the small model,
    /// Whisper's first answer, about half a second on a graphics card. The request itself is understood
    /// afterwards, as before. Returns the text (empty if nothing was said), or null when it can't look
    /// now: busy with something else (the next look comes soon), or see <see cref="CanPreview"/>.
    /// </summary>
    public Task<string?> PreviewAsync(float[] samples) => Task.Run(async () =>
    {
        if (!CanPreview || !await _lock.WaitAsync(0))
        {
            return null;
        }
        try
        {
            var preview = EnsurePreviewLoaded();
            if (!CanPreview)
            {
                return null; // no graphics card after all: on the processor a look takes seconds, and would hold up the request
            }

            var watch = Stopwatch.StartNew();
            preview.ChangeLanguage(ChooseLanguage(samples));
            var text = new StringBuilder();
            await foreach (var segment in preview.ProcessAsync(samples))
            {
                text.Append(segment.Text);
            }
            if (watch.ElapsedMilliseconds > 1500)
            {
                Log.Info($"A look at {samples.Length / (double)MicrophoneRecorder.SampleRate:0.0} s of speech took {watch.ElapsedMilliseconds} ms");
            }
            return CleanUp(text.ToString().Trim());
        }
        finally
        {
            _unloadTimer.Change(UnloadAfter, Timeout.InfiniteTimeSpan);
            _lock.Release();
        }
    });

    /// <summary>
    /// Which language: left to guess from a few seconds, Whisper takes plain English for Spanish or even
    /// Sinhala, so it only chooses between the languages you speak (see Languages). Asking the big model
    /// costs as much as the transcription itself, so the small one decides. Call while holding the lock.
    /// </summary>
    private string ChooseLanguage(float[] samples)
    {
        var languages = Languages.ToArray();
        if (languages.Length > 1 && EnsureLanguageCheckLoaded() is { } check)
        {
            return check.DetectLanguageWithProbability(samples, languages).Item1 ?? languages[0];
        }
        return languages[0];
    }

    /// <summary>
    /// Whether 16 kHz mono audio (up to ~2 s) starts with the wake phrase: one quick pass (~0.2 s on a
    /// graphics card); see the pattern above. Returns null if it doesn't; otherwise whether more was said
    /// after it ("hey glyde, what time…").
    /// </summary>
    public Task<bool?> CheckWakePhraseAsync(float[] samples) => Task.Run(async () =>
    {
        await _lock.WaitAsync();
        try
        {
            var check = EnsureWakeLoaded();
            var audio = new float[Math.Max(samples.Length, (int)(WakeCheckLength.TotalSeconds * MicrophoneRecorder.SampleRate))];
            samples.CopyTo(audio, 0);

            string heard = await TranscribeForWakeAsync(check, audio);
            if (!s_wakePhrase.IsMatch(heard))
            {
                return (bool?)null;
            }
            Log.Info($"Heard the wake phrase: \"{heard}\"");

            // "Hey glyde, hey glyde" (said again when it didn't answer at once) is still just the wake phrase.
            string rest = heard;
            while (s_wakePhrase.Match(rest) is { Success: true } wake)
            {
                rest = rest[wake.Length..].TrimStart();
            }
            return rest.Length > 0;
        }
        finally
        {
            _lock.Release();
        }
    });

    /// <summary>Lower case, words only: "Hey, Glyde!" → "hey glyde".</summary>
    private static async Task<string> TranscribeForWakeAsync(WhisperProcessor processor, float[] audio)
    {
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio))
        {
            text.Append(segment.Text).Append(' ');
        }
        return Regex.Replace(text.ToString().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    }

    /// <summary>The request processor: the request model, or the small one standing in. Call while holding the lock.</summary>
    private WhisperProcessor EnsureRequestLoaded()
    {
        if (_processor is not null)
        {
            return _processor;
        }

        var model = RequestModel.IsDownloaded ? RequestModel : WakeModel;
        var watch = Stopwatch.StartNew();
        _requestFactory = model == WakeModel && _wakeFactory is not null
            ? _wakeFactory
            : WhisperFactory.FromPath(model.Path, new WhisperFactoryOptions { UseGpu = true });
        _requestFactoryModel = model;
        if (model == WakeModel)
        {
            _wakeFactory ??= _requestFactory;
        }

        // The whole 30 s window: telling Whisper a shorter "audio context" would save work, but on
        // Vulkan it made the big model 5-10 times slower, not faster. Beam search weighs five wordings
        // instead of taking the likeliest word each time: in a test with a real voice it got more words
        // right ("set the timer for 5 minutes", not "sit time for…") for ~10% more time.
        _processor = _requestFactory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(s_prompt)
            .WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(5))
            .Build();
        Log.Info($"Speech model {model.File} loaded in {watch.ElapsedMilliseconds} ms, running on {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}");
        _unloadTimer.Change(UnloadAfter, Timeout.InfiniteTimeSpan);
        return _processor;
    }

    /// <summary>
    /// English or Hindi? Asked of the small model, which is quick at it (the big one would take as long as
    /// the transcription). Null when the small model isn't downloaded: then it's English. Call while holding the lock.
    /// </summary>
    private WhisperProcessor? EnsureLanguageCheckLoaded()
    {
        if (_languageCheck is not null || !WakeModel.IsDownloaded)
        {
            return _languageCheck;
        }

        _languageCheck = EnsureWakeFactory().CreateBuilder()
            .WithLanguage("auto")
            .WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();
        return _languageCheck;
    }

    /// <summary>The wake-phrase check (small model). Call while holding the lock.</summary>
    private WhisperProcessor EnsureWakeLoaded()
    {
        if (_wakeCheck is not null)
        {
            return _wakeCheck;
        }

        // It looks at ~2 s of English (the encoder only covers the first 5 s: much faster), and takes
        // Whisper's first answer instead of retrying when it's unsure (which could take seconds).
        _wakeCheck = EnsureWakeFactory().CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(s_wakePrompt)
            .WithNoContext()
            .WithAudioContextSize(256)
            .WithTemperatureInc(0)
            .WithMaxTokensPerSegment(24)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();
        return _wakeCheck;
    }

    /// <summary>The quick look at what you're saying (small model). Call while holding the lock.</summary>
    private WhisperProcessor EnsurePreviewLoaded()
    {
        if (_preview is not null)
        {
            return _preview;
        }

        // Like the wake-phrase check: a shorter audio context (unlike the big model, the small one is
        // quicker for it on Vulkan: ~0.5 s for 3 to 9 s of speech, against ~0.8 s for the whole 30 s
        // window), and Whisper's first answer.
        _preview = EnsureWakeFactory().CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(s_prompt)
            .WithNoContext()
            .WithAudioContextSize(PreviewAudioContext)
            .WithTemperatureInc(0)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();
        return _preview;
    }

    /// <summary>The small model, shared by the wake-phrase check, the language check and the quick looks. Call while holding the lock.</summary>
    private WhisperFactory EnsureWakeFactory() =>
        _wakeFactory ??= _requestFactoryModel == WakeModel && _requestFactory is not null
            ? _requestFactory
            : WhisperFactory.FromPath(WakeModel.Path, new WhisperFactoryOptions { UseGpu = true });

    /// <summary>Lets go of the request model (and the quick looks) after a while unused, or when a better one arrives.</summary>
    private async Task UnloadRequestModelAsync(bool force = false)
    {
        await _lock.WaitAsync();
        try
        {
            if (_processor is null && _languageCheck is null && _preview is null)
            {
                return;
            }

            if (_processor is not null)
            {
                await _processor.DisposeAsync();
                _warmed.Remove(_processor);
                _processor = null;
            }
            if (_languageCheck is not null)
            {
                await _languageCheck.DisposeAsync();
                _languageCheck = null;
            }
            if (_preview is not null)
            {
                await _preview.DisposeAsync();
                _preview = null;
            }
            if (_requestFactory != _wakeFactory)
            {
                _requestFactory?.Dispose();
            }
            _requestFactory = null;
            _requestFactoryModel = null;
            Log.Info(force ? "Speech model let go, for the new one" : "Speech model unloaded (unused for a while)");

            // The small model goes too, unless it's listening for the wake phrase.
            if (!KeepLoaded && _wakeFactory is not null)
            {
                if (_wakeCheck is not null)
                {
                    await _wakeCheck.DisposeAsync();
                    _warmed.Remove(_wakeCheck);
                    _wakeCheck = null;
                }
                _wakeFactory.Dispose();
                _wakeFactory = null;
            }
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

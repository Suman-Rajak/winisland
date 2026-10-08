using System.Diagnostics;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace WinIsland.Services;

/// <summary>
/// Turns speech into text on this PC with OpenAI's Whisper (the "small" model, through whisper.cpp).
/// It runs on the graphics card through Vulkan when there is one, else on the CPU. The model (465 MB)
/// is downloaded once from Hugging Face into the data folder; nothing you say leaves the PC.
/// Loading the model takes a few seconds, so it stays loaded until it's been unused for a while.
/// </summary>
internal sealed class SpeechToText
{
    private const string ModelFile = "ggml-small.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(10);

    // Nudges spelling toward the assistant's name and the commands it knows.
    private static readonly string s_prompt = $"{Assistant.WakePhrase}, pause the music. Next song. Volume 30. Open Chrome.";

    private static readonly string s_modelPath = Path.Combine(AppPaths.DataFolder, "Models", ModelFile);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _unloadTimer;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
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

    /// <summary>Loads the model ahead of time (say, while you're still talking). Needs the model downloaded.</summary>
    public Task WarmUpAsync() => Task.Run(async () =>
    {
        await _lock.WaitAsync();
        try
        {
            EnsureLoaded();
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
            var text = new List<string>();
            string? language = null;
            await foreach (var segment in _processor!.ProcessAsync(samples, cancellation))
            {
                text.Add(segment.Text);
                language ??= segment.Language;
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
        Log.Info($"Speech model loaded in {watch.ElapsedMilliseconds} ms, running on {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}");
        _unloadTimer.Change(UnloadAfter, Timeout.InfiniteTimeSpan);
    }

    private async Task UnloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_processor is null)
            {
                return;
            }

            await _processor.DisposeAsync();
            _factory?.Dispose();
            _processor = null;
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
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Trim('.', ' ') is "" or "Thank you" or "Thanks for watching" or "you" ? "" : text;
    }
}

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WinIsland.Services;

/// <summary>Deepgram couldn't turn the speech into text, with a message fit to show you.</summary>
internal sealed class DeepgramException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One request turned into text by Deepgram (Nova-3) while it's said: the audio streams over a WebSocket
/// as it's recorded, and the words come back as you speak (<see cref="Heard"/>), so they can be shown,
/// and are ready as soon as you stop (<see cref="FinishAsync"/>). The request's audio goes to Deepgram;
/// new accounts get free credit (no card). Its key is in <see cref="SecretStore"/>.
/// </summary>
internal sealed class DeepgramStream : IAsyncDisposable
{
    public const string KeyName = "DeepgramApiKey";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(4); // it hangs up after ~10 s without audio (while an answer's said, say)

    private readonly ClientWebSocket _socket = new();
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _finals = []; // the parts it's finished with
    private string _interim = "";               // its guess at the part being said
    private volatile string? _problem;
    private int _results;    // transcripts it's sent back, for the log
    private long _bytesSent; // audio sent, for the log

    /// <summary>The saved key, or null.</summary>
    public static string? Key => SecretStore.Get(KeyName) is { Length: > 0 } key ? key : null;

    private DeepgramStream()
    {
    }

    /// <summary>What's been said so far (the finished parts and its guess at the rest). Raised on a background thread.</summary>
    public event Action<string>? Heard;

    /// <summary>It stopped working (couldn't connect, or the connection dropped): the request is better heard some other way.</summary>
    public bool Failed => _problem is not null;

    /// <summary>
    /// Connects (in the background: audio sent meanwhile waits) and starts listening.
    /// </summary>
    /// <param name="languages">Whisper codes, as in <see cref="SpeechToText.Languages"/>: English alone, or with Hindi (Nova-3's mixed-language model).</param>
    public static DeepgramStream Start(string key, IReadOnlyList<string> languages)
    {
        var stream = new DeepgramStream();
        _ = Task.Run(() => stream.RunAsync(key, languages));
        return stream;
    }

    /// <summary>Tries a key: half a second of silence, start to finish. Returns null if it works, else why not.</summary>
    public static async Task<string?> TestAsync(string key)
    {
        await using var stream = Start(key, ["en"]);
        stream.Send(new float[MicrophoneRecorder.SampleRate / 2]);
        try
        {
            await stream.FinishAsync(TimeSpan.FromSeconds(10));
            return null;
        }
        catch (DeepgramException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>More of the request: 16 kHz mono.</summary>
    public void Send(float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        // Deepgram takes 16-bit little-endian PCM ("linear16").
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            bytes[2 * i] = (byte)value;
            bytes[2 * i + 1] = (byte)(value >> 8);
        }
        _audio.Writer.TryWrite(bytes);
    }

    /// <summary>
    /// No more audio: what was said, once Deepgram has finished with it (mostly done already, as it
    /// listened). Throws <see cref="DeepgramException"/> if it couldn't, or took longer than <paramref name="timeout"/>.
    /// </summary>
    public async Task<string> FinishAsync(TimeSpan timeout)
    {
        _audio.Writer.TryComplete();
        if (await Task.WhenAny(_closed.Task, Task.Delay(timeout)) != _closed.Task)
        {
            throw new DeepgramException($"Deepgram took longer than {timeout.TotalSeconds:0} s");
        }
        if (_problem is { } problem)
        {
            throw new DeepgramException(problem);
        }

        // Its last guess too, should the end not have come back as finished (it would be lost otherwise).
        lock (_finals)
        {
            return string.Join(' ', _finals.Append(_interim)).Trim();
        }
    }

    /// <summary>For the log: how much went to and fro.</summary>
    public string Summary
    {
        get
        {
            lock (_finals)
            {
                return $"{Interlocked.Read(ref _bytesSent) / 2.0 / MicrophoneRecorder.SampleRate:0.0} s of audio sent, " +
                       $"{_results} transcripts back, {_finals.Count} of them with words{(_interim.Length > 0 ? ", plus a last guess" : "")}";
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _audio.Writer.TryComplete();
        await _cancel.CancelAsync(); // (not disposed: the connection's task may still be looking at it)
        _socket.Abort(); // (closed by Deepgram already, when it finished)
        _socket.Dispose();
    }

    private async Task RunAsync(string key, IReadOnlyList<string> languages)
    {
        try
        {
            // Smart formatting writes numbers as digits ("volume 30"), as the commands expect. The
            // assistant's name is a key term, so it's spelled right (English only: the mixed-language
            // model may not take key terms).
            string language = languages.Count > 1 ? "multi" : "en";
            var uri = new Uri("wss://api.deepgram.com/v1/listen?model=nova-3&encoding=linear16" +
                              $"&sample_rate={MicrophoneRecorder.SampleRate}&channels=1&language={language}" +
                              "&interim_results=true&punctuate=true&smart_format=true" +
                              (language == "en" ? $"&keyterm={Uri.EscapeDataString(Assistant.Name)}" : ""));
            _socket.Options.SetRequestHeader("Authorization", $"Token {key}");
            _socket.Options.CollectHttpResponseDetails = true;
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
            {
                connecting.CancelAfter(ConnectTimeout);
                await _socket.ConnectAsync(uri, connecting.Token);
            }

            var receiving = ReceiveAsync();
            await SendAudioAsync();
            await receiving;
        }
        catch (Exception) when (_cancel.IsCancellationRequested)
        {
            // let go of: the request was dropped, or it's finished
        }
        catch (WebSocketException ex) when (_socket.HttpStatusCode != 0)
        {
            Fail(_socket.HttpStatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Deepgram doesn't accept the API key. Check it in Settings.",
                HttpStatusCode.PaymentRequired => "Deepgram's credit is used up.",
                HttpStatusCode.TooManyRequests => "Deepgram is busy (too many requests). Try again in a moment.",
                _ => $"Deepgram refused to listen ({(int)_socket.HttpStatusCode})",
            }, ex);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            Fail("I can't reach Deepgram. Check the internet connection.", ex);
        }
        catch (Exception ex)
        {
            Fail("Something went wrong with Deepgram.", ex);
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    /// <summary>The audio as it comes, a keep-alive while there's none; then "that's all".</summary>
    private async Task SendAudioAsync()
    {
        while (true)
        {
            bool more;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
            {
                idle.CancelAfter(KeepAliveEvery);
                try
                {
                    more = await _audio.Reader.WaitToReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
                {
                    await SendTextAsync("""{"type":"KeepAlive"}""");
                    continue;
                }
            }
            if (!more)
            {
                break; // finished
            }
            while (_audio.Reader.TryRead(out var chunk))
            {
                await _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, _cancel.Token);
                Interlocked.Add(ref _bytesSent, chunk.Length);
            }
        }

        // It works through what's left, sends the last of the words, and hangs up.
        await SendTextAsync("""{"type":"CloseStream"}""");
    }

    private Task SendTextAsync(string message) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, _cancel.Token);

    private async Task ReceiveAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, _cancel.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (result.CloseStatus is not (WebSocketCloseStatus.NormalClosure or null))
                {
                    Fail($"Deepgram stopped listening ({result.CloseStatusDescription ?? result.CloseStatus.ToString()})");
                }
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                OnMessage(message.ToArray());
                message.SetLength(0);
            }
        }
    }

    /// <summary>
    /// A transcript: "final" ones are parts it's finished with; the rest are its current guess at the part
    /// being said, replaced by the next.
    /// </summary>
    private void OnMessage(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "Error")
        {
            Log.Info($"Deepgram: {root}");
            return;
        }
        if (type != "Results"
            || !root.TryGetProperty("channel", out var channel)
            || channel.GetProperty("alternatives") is not { ValueKind: JsonValueKind.Array } alternatives
            || alternatives.GetArrayLength() == 0)
        {
            return;
        }

        string text = alternatives[0].GetProperty("transcript").GetString()?.Trim() ?? "";
        bool isFinal = root.TryGetProperty("is_final", out var final) && final.GetBoolean();
        string sofar;
        lock (_finals)
        {
            _results++;
            if (isFinal)
            {
                if (text.Length > 0)
                {
                    _finals.Add(text);
                }
                _interim = "";
            }
            else
            {
                _interim = text;
            }
            sofar = string.Join(' ', _finals.Append(_interim)).Trim();
        }
        Heard?.Invoke(sofar);
    }

    private void Fail(string problem, Exception? ex = null)
    {
        if (_problem is null)
        {
            _problem = problem;
            Log.Info($"Deepgram: {problem}{(ex is null ? "" : $" ({ex.GetType().Name}: {ex.Message})")}");
        }
    }
}

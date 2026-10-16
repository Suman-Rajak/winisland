using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace WinIsland.Services;

/// <summary>
/// Says things out loud: in a natural Microsoft voice (Azure, when that's set up in Settings), with the
/// accent you speak with or the one chosen; else, or when Azure can't be reached, with Windows' own voice
/// (one with that accent if Windows has it). Hindi text gets a Hindi voice either way.
/// </summary>
internal sealed class Speaker
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly VoiceInformation _defaultVoice;
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Speech };
    private TaskCompletionSource? _speaking;
    private static string? s_lastProblem; // the last reason Azure couldn't speak, so it's logged once, not every time

    public Speaker()
    {
        // Otherwise Windows lists the assistant's voice as "now playing" media, and the island would show it.
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += (_, _) => _speaking?.TrySetResult();
        _player.MediaFailed += (_, _) => _speaking?.TrySetResult();
        _defaultVoice = _synthesizer.Voice;
    }

    /// <summary>
    /// The accent of the last request heard (set by the assistant, when it listens for it); replies take
    /// it when the reply accent is "match mine". Null until one's been heard.
    /// </summary>
    public static SpokenAccent? HeardAccent { get; set; }

    /// <summary>The accent replies are said in now: the one chosen in Settings, or yours.</summary>
    public static SpokenAccent ReplyAccent => AppSettings.Current.ReplyAccent switch
    {
        AccentChoice.Indian => SpokenAccent.Indian,
        AccentChoice.American => SpokenAccent.American,
        AccentChoice.British => SpokenAccent.British,
        _ => HeardAccent ?? SpokenAccent.Indian, // (before it's heard you: the accent it's mostly spoken to with)
    };

    /// <summary>
    /// Replies can come in more than one accent: Azure's voices are set up, or Windows has voices for more
    /// than one accent of English (an Indian one comes with the English (India) speech pack).
    /// </summary>
    public static bool CanMatchAccent => AzureVoice.IsReady
        || SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Language.ToLowerInvariant()).Distinct().Count() > 1;

    public void Speak(string text) => _ = SpeakAsync(text);

    /// <summary>Saying something right now (including getting the voice ready).</summary>
    public bool IsSpeaking => _speaking is { Task.IsCompleted: false };

    /// <summary>When the voice actually started (TickCount64), or 0 while it's still being got ready (or isn't speaking).</summary>
    public long PlayingSince { get; private set; }

    /// <summary>Says it, finishing when it's been said (or stopped).</summary>
    public async Task SpeakAsync(string text)
    {
        _speaking?.TrySetResult();
        var speaking = _speaking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlayingSince = 0;
        try
        {
            var source = await NaturalVoiceAsync(text) ?? await WindowsVoiceAsync(text);
            if (speaking != _speaking || speaking.Task.IsCompleted)
            {
                return; // stopped (or something else to say) while the voice was got ready
            }
            _player.Source = source;
            _player.Play();
            PlayingSince = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            Log.Error("Speaking failed", ex);
            speaking.TrySetResult();
        }
        // In case the end is never reported: comfortably longer than saying it takes (about 15 characters a second).
        await Task.WhenAny(speaking.Task, Task.Delay(TimeSpan.FromSeconds(Math.Max(30, text.Length / 8.0))));
    }

    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
        PlayingSince = 0;
        _speaking?.TrySetResult();
    }

    /// <summary>The reply in Azure's natural voice, when that's set up; null when it isn't, or Azure can't (then Windows' voice says it).</summary>
    private static async Task<MediaSource?> NaturalVoiceAsync(string text)
    {
        if (!AzureVoice.IsReady || AzureVoice.Key is not { } key)
        {
            return null;
        }

        string voice = AzureVoice.VoiceFor(text, ReplyAccent, AppSettings.Current.MaleVoice);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var wav = await AzureVoice.SynthesizeAsync(text, voice, key, AppSettings.Current.AzureRegion!, CancellationToken.None);
            if (s_lastProblem is not null || watch.ElapsedMilliseconds > 1500)
            {
                Log.Info($"Azure voice {voice} ready in {watch.ElapsedMilliseconds} ms");
            }
            s_lastProblem = null;
            return await AzureVoice.ToMediaSourceAsync(wav);
        }
        catch (AzureVoiceException ex)
        {
            if (ex.Message != s_lastProblem)
            {
                s_lastProblem = ex.Message;
                Log.Info($"Azure voice: {ex.Message} Using Windows' voice meanwhile.");
            }
            return null;
        }
    }

    private async Task<MediaSource> WindowsVoiceAsync(string text)
    {
        _synthesizer.Voice = WindowsVoiceFor(text);
        var stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
        return MediaSource.CreateFromStream(stream, stream.ContentType);
    }

    /// <summary>A Hindi voice for text in Devanagari, else one with the reply's accent, if Windows has them; else the default.</summary>
    private VoiceInformation WindowsVoiceFor(string text)
    {
        string language = AzureVoice.IsHindi(text) ? "hi" : ReplyAccent switch
        {
            SpokenAccent.Indian => "en-IN",
            SpokenAccent.British => "en-GB",
            _ => "en-US",
        };
        bool male = AppSettings.Current.MaleVoice;
        var matching = SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.FirstOrDefault(v => (v.Gender == VoiceGender.Male) == male) is { } voice ? voice
            : matching.FirstOrDefault() ?? _defaultVoice;
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// The voice assistant (<see cref="Assistant.Name"/>). Press the shortcut (or the mic in the expanded
/// island) and speak: the pill shows a live waveform while it listens, stops by itself when you pause
/// (or press again), turns the speech into text on this PC, carries out the command and answers, on the
/// island and out loud. While it's doing any of that it holds the island: hovering doesn't expand it.
/// </summary>
internal sealed class AssistantActivity : IslandActivity
{
    private enum Mode
    {
        Off,
        Listening,
        Thinking,
        Answer,
        Status, // a short message: "Didn't catch that", "Microphone is off"…
    }

    private static readonly TimeSpan MaxListening = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WaitForSpeech = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PauseEndsSpeech = TimeSpan.FromSeconds(1.1);
    private const int SpeechStartsAfter = 3; // loud readings in a row (~90 ms), so a click doesn't count

    private static readonly PillShape ListeningShape = new(250, 36, 18);
    private static readonly PillShape ThinkingShape = new(190, 36, 18);
    private static readonly PillShape StatusShape = new(330, 36, 18);
    private static readonly PillShape ShortAnswerShape = new(380, 56, 24);
    private static readonly PillShape LongAnswerShape = new(380, 74, 26);

    private readonly DispatcherQueue _dispatcher;
    private readonly AssistantView _view = new();
    private readonly SpeechToText _speechToText = new();
    private readonly Speaker _speaker = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private HotkeyListener? _hotkey;
    private string? _listeningFor; // the shortcut registered, null when none
    private MicrophoneRecorder? _recorder;
    private Mode _mode;
    private PillShape _shape = ListeningShape;
    private bool _starting;
    private double? _downloadProgress; // set while the speech model downloads

    // Noticing when you've finished speaking: loudness against the room's background noise.
    private long _listeningSince;
    private long _lastSpeechAt;
    private int _loudInARow;
    private bool _heardSpeech;
    private float _noiseDb;

    public AssistantActivity(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _hideTimer = dispatcher.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            if (_mode is Mode.Answer or Mode.Status)
            {
                SetMode(Mode.Off);
            }
        };
        _view.Clicked += OnClicked;
        AppSettings.Current.Changed += (_, _) =>
        {
            if (!IsEnabled && _mode != Mode.Off)
            {
                Cancel();
            }
            UpdateShortcut();
            RaiseChanged();
        };
    }

    public override string Name => Assistant.Name;

    /// <summary>Above everything: you asked for it.</summary>
    public override int Priority => 110;

    public override bool WantsCompact => IsEnabled && (_mode != Mode.Off || _downloadProgress is not null);

    public override bool HoldsIsland => IsEnabled && _mode != Mode.Off;

    public override PillShape CompactShape => _shape;

    public override FrameworkElement CompactView => _view;

    public static bool IsEnabled => AppSettings.Current.VoiceAssistant;

    public static string Shortcut => AppSettings.Current.AssistantShortcut;

    /// <summary>False while another app owns the chosen shortcut (Settings says so).</summary>
    public static bool ShortcutWorks { get; private set; } = true;

    public override Task StartAsync()
    {
        _hotkey = new HotkeyListener();
        _hotkey.Pressed += Toggle;
        UpdateShortcut();

        if (IsEnabled)
        {
            AppCatalog.Preload();
        }
        return Task.CompletedTask;
    }

    /// <summary>Listens for the chosen shortcut while the assistant is on, and lets go of it while it's off.</summary>
    private void UpdateShortcut()
    {
        string? wanted = IsEnabled ? Shortcut : null;
        if (_hotkey is null || wanted == _listeningFor)
        {
            return;
        }

        _listeningFor = wanted;
        if (wanted is null)
        {
            _hotkey.Unregister();
            ShortcutWorks = true;
            return;
        }

        ShortcutWorks = _hotkey.Register(wanted);
        Log.Info(ShortcutWorks
            ? $"{Assistant.Name} listens for {wanted}"
            : $"{wanted} is taken by another app; {Assistant.Name} can only be started from the island");
    }

    /// <summary>The shortcut or the island's mic button: start listening, or stop and answer.</summary>
    public void Toggle()
    {
        if (!IsEnabled || _starting)
        {
            return;
        }

        switch (_mode)
        {
            case Mode.Listening:
                _ = FinishListeningAsync(stoppedByYou: true);
                break;
            case Mode.Thinking:
                break;
            default:
                _ = SpeechToText.IsModelDownloaded ? StartListeningAsync() : DownloadModelAsync();
                break;
        }
    }

    private void OnClicked()
    {
        if (_mode == Mode.Listening)
        {
            Toggle();
        }
        else if (_mode is Mode.Answer or Mode.Status)
        {
            _speaker.Stop();
            SetMode(Mode.Off);
        }
    }

    // ---------------------------------------------------------------- listening

    private async Task StartListeningAsync()
    {
        _starting = true;
        try
        {
            _speaker.Stop();
            _hideTimer.Stop();
            _view.ShowListening();
            _shape = ListeningShape;
            SetMode(Mode.Listening);
            _ = _speechToText.WarmUpAsync(); // load the model while you talk

            var recorder = new MicrophoneRecorder();
            recorder.Level += level => _dispatcher.TryEnqueue(() => OnLevel(recorder, level));
            if (await recorder.StartAsync() is { } problem)
            {
                Log.Info($"Can't listen: {problem}");
                ShowStatus(problem, TimeSpan.FromSeconds(6));
                return;
            }
            if (_mode != Mode.Listening)
            {
                recorder.Dispose(); // turned off meanwhile
                return;
            }

            _recorder = recorder;
            _listeningSince = Environment.TickCount64;
            _lastSpeechAt = 0;
            _loudInARow = 0;
            _heardSpeech = false;
            _noiseDb = -50;
        }
        catch (Exception ex)
        {
            Log.Error("Starting to listen failed", ex);
            ShowStatus("The microphone isn't available.", TimeSpan.FromSeconds(4));
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>About 30 times a second while listening, with the microphone's loudness (RMS).</summary>
    private void OnLevel(MicrophoneRecorder recorder, float rms)
    {
        if (recorder != _recorder)
        {
            return; // a reading that arrived after we stopped
        }

        float db = 20 * MathF.Log10(rms + 1e-7f);
        _view.PushLevel(Math.Clamp((db + 55) / 40, 0, 1));

        // The background noise level follows quiet moments quickly and loud ones slowly, so speech barely moves it.
        _noiseDb += (db - _noiseDb) * (db < _noiseDb ? 0.3f : 0.01f);
        bool loud = db > Math.Max(-42, _noiseDb + 12);
        _loudInARow = loud ? _loudInARow + 1 : 0;

        long now = Environment.TickCount64;
        if (_loudInARow >= SpeechStartsAfter)
        {
            _heardSpeech = true;
        }
        if (loud && _heardSpeech)
        {
            _lastSpeechAt = now;
        }

        long listening = now - _listeningSince;
        if (listening > MaxListening.TotalMilliseconds
            || (!_heardSpeech && listening > WaitForSpeech.TotalMilliseconds)
            || (_heardSpeech && now - _lastSpeechAt > PauseEndsSpeech.TotalMilliseconds))
        {
            _ = FinishListeningAsync(stoppedByYou: false);
        }
    }

    private async Task FinishListeningAsync(bool stoppedByYou)
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        _recorder = null;
        float[] samples = recorder.Stop();

        // Pressing the shortcut again means "that's all", so try even if it sounded quiet.
        if ((!_heardSpeech && !stoppedByYou) || samples.Length < MicrophoneRecorder.SampleRate / 3)
        {
            ShowStatus("I didn't hear anything.", TimeSpan.FromSeconds(3));
            return;
        }

        _view.ShowStatus("Thinking…", busy: true);
        _shape = ThinkingShape;
        SetMode(Mode.Thinking);

        try
        {
            string heard = await _speechToText.TranscribeAsync(samples, CancellationToken.None);
            if (_mode != Mode.Thinking)
            {
                return; // turned off meanwhile
            }
            if (heard.Length == 0)
            {
                ShowStatus("Sorry, I didn't catch that.", TimeSpan.FromSeconds(3));
                return;
            }

            var result = await VoiceCommands.RunAsync(heard);
            if (_mode == Mode.Thinking)
            {
                ShowAnswer(heard, result);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Understanding speech failed", ex);
            ShowStatus("Something went wrong understanding that.", TimeSpan.FromSeconds(4));
        }
    }

    // ---------------------------------------------------------------- the speech model

    private async Task DownloadModelAsync()
    {
        if (_downloadProgress is not null)
        {
            return; // already on it
        }

        SetDownloadProgress(0);
        try
        {
            await _speechToText.DownloadModelAsync(new Progress<double>(SetDownloadProgress));
            _downloadProgress = null;
            ShowStatus($"{Assistant.Name} is ready: press {Shortcut} and speak.", TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _downloadProgress = null;
            Log.Error("Downloading the speech model failed", ex);
            ShowStatus("Couldn't download the speech model. Try again later.", TimeSpan.FromSeconds(6));
        }
    }

    private void SetDownloadProgress(double progress)
    {
        bool first = _downloadProgress is null;
        _downloadProgress = progress;
        if (_mode == Mode.Off)
        {
            _view.ShowStatus($"Downloading {Assistant.Name}'s speech model · {progress:P0}", progress);
            _shape = StatusShape;
            if (first)
            {
                RaiseChanged();
            }
        }
    }

    // ---------------------------------------------------------------- showing results

    private void ShowAnswer(string heard, CommandResult result)
    {
        _view.ShowAnswer(heard, result.Reply);
        _shape = result.Reply.Length > 40 ? LongAnswerShape : ShortAnswerShape;
        SetMode(Mode.Answer);
        if (result.Speak)
        {
            _speaker.Speak(result.Reply);
        }
        HideAfter(TimeSpan.FromSeconds(Math.Clamp(2.5 + result.Reply.Length * 0.06, 3.5, 9)));
    }

    private void ShowStatus(string message, TimeSpan duration)
    {
        _view.ShowStatus(message);
        _shape = StatusShape;
        SetMode(Mode.Status);
        HideAfter(duration);
    }

    private void HideAfter(TimeSpan duration)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = duration;
        _hideTimer.Start();
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        if (mode == Mode.Off && _downloadProgress is { } progress)
        {
            SetDownloadProgress(progress); // back to showing the download
        }
        RaiseChanged();
    }

    /// <summary>Stops whatever the assistant is doing (it was turned off).</summary>
    private void Cancel()
    {
        _recorder?.Dispose();
        _recorder = null;
        _speaker.Stop();
        _hideTimer.Stop();
        _mode = Mode.Off;
    }
}

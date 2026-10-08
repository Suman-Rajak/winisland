using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Media.Devices;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// The voice assistant (<see cref="Assistant.Name"/>). Press the shortcut (or the mic in the expanded
/// island), or say the wake phrase when that's turned on, and speak: the pill shows a live waveform while
/// it listens, stops by itself when you pause (or press again), turns the speech into text on this PC,
/// carries out the command and answers, on the island and out loud. While it's doing any of that it
/// holds the island: hovering doesn't expand it.
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
    private static readonly TimeSpan MicrophoneTimeout = TimeSpan.FromSeconds(10);

    private static readonly PillShape ListeningShape = new(250, 36, 18);
    private static readonly PillShape ThinkingShape = new(190, 36, 18);
    private static readonly PillShape StatusShape = new(330, 36, 18);
    private static readonly PillShape ShortAnswerShape = new(380, 56, 24);
    private static readonly PillShape LongAnswerShape = new(380, 74, 26);

    private readonly DispatcherQueue _dispatcher;
    private readonly AssistantView _view = new();
    private readonly SpeechToText _speechToText = new();
    private readonly Speaker _speaker = new();
    private readonly SpeechDetector _speech = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private HotkeyListener? _hotkey;
    private string? _listeningFor; // the shortcut registered, null when none
    private Mode _mode;
    private PillShape _shape = ListeningShape;
    private bool _starting;
    private double? _downloadProgress; // set while the speech model downloads

    // The microphone: open while listening to a request, and all the time while listening for the wake phrase.
    private MicrophoneRecorder? _mic;
    private bool _micIsBluetooth;
    private Task<string?>? _micOpening;
    private WakeWordDetector? _wake;

    // The request being listened to: where it starts in the microphone's audio, and whether you've finished.
    private volatile bool _inRequest;
    private long _requestFrom;
    private long _listeningSince;
    private long _lastSpeechAt;
    private bool _heardSpeech;
    private float _loudestDb; // for the log when nothing was heard

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
            UpdateWakeWord();
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

    public static bool WakeWordEnabled => IsEnabled && AppSettings.Current.WakeWord;

    public static string Shortcut => AppSettings.Current.AssistantShortcut;

    /// <summary>False while another app owns the chosen shortcut (Settings says so).</summary>
    public static bool ShortcutWorks { get; private set; } = true;

    /// <summary>
    /// Listening to a request. The island's mic dot counts WinIsland's own microphone use only then, not
    /// while it's just waiting for its name (that would keep the dot up all day).
    /// </summary>
    public static bool IsListeningToRequest { get; private set; }

    /// <summary>Why the wake phrase isn't being listened for although it's turned on; null when it is (or it's off).</summary>
    public static string? WakeWordProblem { get; private set; }

    public override Task StartAsync()
    {
        _hotkey = new HotkeyListener();
        _hotkey.Pressed += Toggle;
        UpdateShortcut();

        // A headset connecting can change which microphone to listen with.
        MediaDevice.DefaultAudioCaptureDeviceChanged += (_, _) => _dispatcher.TryEnqueue(OnMicrophonesChanged);

        if (IsEnabled)
        {
            AppCatalog.Preload();
        }
        UpdateWakeWord();
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
                _ = FinishRequestAsync(stoppedByYou: true);
                break;
            case Mode.Thinking:
                break;
            default:
                _ = SpeechToText.IsModelDownloaded ? StartRequestAsync(from: null, requestStarted: false) : DownloadModelAsync();
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

    // ---------------------------------------------------------------- the microphone

    /// <summary>
    /// Keeps the microphone open (and the model loaded) while listening for the wake phrase, closes it
    /// when nothing needs it, and pauses wake-phrase listening while busy with a request.
    /// </summary>
    private void UpdateWakeWord()
    {
        bool wanted = WakeWordEnabled;
        if (wanted && !SpeechToText.IsModelDownloaded)
        {
            if (_downloadProgress is null && _mode == Mode.Off)
            {
                _ = DownloadModelAsync(); // you turned the wake phrase on; it needs the model
            }
            WakeWordProblem = "Waiting for the speech model to download.";
            wanted = false;
        }
        else if (!wanted)
        {
            WakeWordProblem = null;
        }
        _speechToText.KeepLoaded = wanted;

        // Close the microphone unless it's needed: by a request, or by the wake phrase (but never a
        // Bluetooth headset's, which would sound like a phone call for as long as it's open).
        bool busyWithRequest = _inRequest || _mode == Mode.Listening;
        if (_mic is not null && !busyWithRequest && (!wanted || _micIsBluetooth))
        {
            CloseMicrophone();
        }

        if (wanted && _mic is null && _micOpening is not { IsCompleted: false })
        {
            _ = StartWakeListeningAsync();
        }

        if (_wake is not null)
        {
            _wake.Paused = !(wanted && _mode == Mode.Off);
        }
        if (wanted && _mic is not null && !_micIsBluetooth)
        {
            WakeWordProblem = null;
        }
    }

    private async Task StartWakeListeningAsync()
    {
        try
        {
            await ListenForWakePhraseAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Listening for \"{Assistant.WakePhrase}\" failed", ex);
            WakeWordProblem = "Couldn't start listening. See the log for details.";
        }
    }

    private async Task ListenForWakePhraseAsync()
    {
        if (MicrophonePicker.Choose().IsBluetooth)
        {
            if (WakeWordProblem is null)
            {
                Log.Info($"Not listening for \"{Assistant.WakePhrase}\": the only microphone is a Bluetooth headset's");
            }
            WakeWordProblem = "Not listening: the only microphone is a Bluetooth headset's, and keeping it open would make " +
                              "everything you hear sound like a phone call. Push-to-talk still works.";
            return;
        }

        if (await OpenMicrophoneAsync() is { } problem)
        {
            Log.Info($"Can't listen for \"{Assistant.WakePhrase}\": {problem}");
            WakeWordProblem = problem;
            return;
        }

        WakeWordProblem = null;
        Log.Info($"Listening for \"{Assistant.WakePhrase}\"");
        _ = _speechToText.WarmUpAsync(wakeCheck: true);
        UpdateWakeWord(); // unpause it, unless something changed meanwhile
    }

    /// <summary>Opens the microphone, or joins the opening already under way. Returns null, or why it couldn't.</summary>
    private Task<string?> OpenMicrophoneAsync()
    {
        if (_mic is not null)
        {
            return Task.FromResult<string?>(null);
        }
        return _micOpening is { IsCompleted: false } opening ? opening : _micOpening = OpenAsync();

        async Task<string?> OpenAsync()
        {
            var choice = MicrophonePicker.Choose();
            var mic = new MicrophoneRecorder();
            mic.Level += OnMicrophoneLevel;
            mic.Failed += () => _dispatcher.TryEnqueue(() => OnMicrophoneFailed(mic));

            // When Windows' audio engine hangs (a sound driver's effects, usually), opening a microphone
            // never finishes. Don't wait for ever: say so, and tidy up if it ever does finish.
            var starting = mic.StartAsync(choice.Id);
            if (await Task.WhenAny(starting, Task.Delay(MicrophoneTimeout)) != starting)
            {
                Log.Info($"Opening the microphone ({choice.Name}) didn't finish in {MicrophoneTimeout.TotalSeconds:0} s");
                _ = starting.ContinueWith(_ => mic.Dispose(), TaskScheduler.Default);
                return "The microphone isn't responding. Restarting Windows Audio (or the PC) usually fixes this.";
            }
            if (await starting is { } problem)
            {
                mic.Dispose();
                return problem;
            }

            _mic = mic;
            _micIsBluetooth = choice.IsBluetooth;
            _wake = new WakeWordDetector(mic, _speechToText, _dispatcher) { Paused = true };
            _wake.Heard += OnWakePhrase;
            Log.Info($"Microphone open: {choice.Name}");
            return null;
        }
    }

    private void CloseMicrophone()
    {
        _mic?.Dispose();
        _mic = null;
        _wake = null;
        Log.Info("Microphone closed");
    }

    /// <summary>The microphone's loudness, on the audio thread: to the waveform during a request, else to the wake-phrase detector.</summary>
    private void OnMicrophoneLevel(float rms, long position)
    {
        if (_inRequest)
        {
            _dispatcher.TryEnqueue(() => OnRequestLevel(rms));
        }
        else
        {
            _wake?.OnLevel(rms, position);
        }
    }

    private async void OnMicrophoneFailed(MicrophoneRecorder mic)
    {
        if (mic != _mic)
        {
            return;
        }

        bool wasListening = _inRequest;
        EndRequest();
        CloseMicrophone();
        if (wasListening)
        {
            ShowStatus("The microphone stopped.", TimeSpan.FromSeconds(4));
        }

        await Task.Delay(TimeSpan.FromSeconds(2)); // let Windows settle on another microphone
        UpdateWakeWord();
    }

    /// <summary>The default microphone changed: reopen, so the choice of microphone is made again.</summary>
    private void OnMicrophonesChanged()
    {
        if (_mic is not null && !_inRequest && _mode != Mode.Listening)
        {
            CloseMicrophone();
        }
        UpdateWakeWord();
    }

    // ---------------------------------------------------------------- listening to a request

    private void OnWakePhrase(long from, bool requestStarted)
    {
        if (_mode != Mode.Off || !WakeWordEnabled || _starting)
        {
            return;
        }
        _ = StartRequestAsync(from, requestStarted);
    }

    /// <param name="from">Where the request starts in the microphone's audio (the wake phrase); null for now.</param>
    /// <param name="requestStarted">You already began the request (said it in one breath with the wake phrase).</param>
    private async Task StartRequestAsync(long? from, bool requestStarted)
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

            if (await OpenMicrophoneAsync() is { } problem)
            {
                Log.Info($"Can't listen: {problem}");
                ShowStatus(problem, TimeSpan.FromSeconds(6));
                return;
            }
            if (_mode != Mode.Listening || _mic is null)
            {
                return; // turned off meanwhile
            }

            _requestFrom = from ?? _mic.Position;
            _listeningSince = Environment.TickCount64;
            _heardSpeech = requestStarted;
            _lastSpeechAt = requestStarted ? _listeningSince : 0;
            _speech.Reset();
            _loudestDb = float.NegativeInfinity;
            _inRequest = true;
            IsListeningToRequest = true;
            RaiseChanged(); // the mic dot
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

    /// <summary>About 30 times a second while listening to a request, with the microphone's loudness (RMS).</summary>
    private void OnRequestLevel(float rms)
    {
        if (!_inRequest)
        {
            return; // a reading that arrived after we stopped
        }

        float db = SpeechDetector.ToDecibels(rms);
        _view.PushLevel(Math.Clamp((db + 55) / 40, 0, 1));
        _speech.Update(db);
        _loudestDb = Math.Max(_loudestDb, db);

        long now = Environment.TickCount64;
        if (_speech.IsSpeech)
        {
            _heardSpeech = true;
        }
        if (_speech.IsLoud && _heardSpeech)
        {
            _lastSpeechAt = now;
        }

        long listening = now - _listeningSince;
        if (listening > MaxListening.TotalMilliseconds
            || (!_heardSpeech && listening > WaitForSpeech.TotalMilliseconds)
            || (_heardSpeech && now - _lastSpeechAt > PauseEndsSpeech.TotalMilliseconds))
        {
            _ = FinishRequestAsync(stoppedByYou: false);
        }
    }

    private void EndRequest()
    {
        _inRequest = false;
        IsListeningToRequest = false;
    }

    private async Task FinishRequestAsync(bool stoppedByYou)
    {
        if (!_inRequest || _mic is null)
        {
            return;
        }

        EndRequest();
        float[] samples = _mic.Read(_requestFrom, _mic.Position);

        // Pressing the shortcut again means "that's all", so try even if it sounded quiet.
        if ((!_heardSpeech && !stoppedByYou) || samples.Length < MicrophoneRecorder.SampleRate / 3)
        {
            // Only how loud, never what: around -140 dB is digital silence (something blocking the
            // microphone), -60 to -40 a quiet room; speech is usually above -35.
            Log.Info($"Heard no request in {samples.Length / (double)MicrophoneRecorder.SampleRate:0.0} s (loudest {_loudestDb:0} dB)");
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
            ShowStatus(WakeWordEnabled
                ? $"{Assistant.Name} is ready: say “{Assistant.WakePhrase}” or press {Shortcut}."
                : $"{Assistant.Name} is ready: press {Shortcut} and speak.", TimeSpan.FromSeconds(6));
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
        UpdateWakeWord();
        RaiseChanged();
    }

    /// <summary>Stops whatever the assistant is doing (it was turned off).</summary>
    private void Cancel()
    {
        EndRequest();
        _speaker.Stop();
        _hideTimer.Stop();
        _mode = Mode.Off;
        UpdateWakeWord();
    }
}

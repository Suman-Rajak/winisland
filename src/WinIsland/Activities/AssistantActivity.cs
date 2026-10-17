using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Media.Devices;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// The voice assistant (<see cref="Assistant.Name"/>). Press the shortcut (or the mic in the expanded
/// island), or say the wake phrase when that's turned on, and speak: the pill shows a live waveform while
/// it listens, with your words under it as you say them, stops by itself when you pause (or press
/// again), turns the speech into text on this PC, carries out the command and answers, on the island and
/// out loud. Or press the type shortcut (or the keyboard button) and type the request: the answer is
/// shown, not spoken. While it's doing any of that
/// it holds the island: hovering doesn't expand it.
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
        Draft,  // an email or message for you to confirm, by voice or with its buttons
        Typing, // a box to type a request in (or a change to the draft)
    }

    private static readonly TimeSpan MaxListening = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WaitForSpeech = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FollowUpWaits = TimeSpan.FromSeconds(5); // after an answer, for a follow-up question
    private const int LoudReadingsToInterrupt = 8;                              // ~0.25 s of you talking over the answer
    private const float InterruptAboveEcho = 6;                                 // dB louder than its own voice coming back
    private static readonly TimeSpan EchoMeasuredFor = TimeSpan.FromMilliseconds(800); // the answer's first moments
    private static readonly TimeSpan InterruptPreRoll = TimeSpan.FromMilliseconds(700); // kept from before that was noticed
    private static readonly TimeSpan PauseEndsSpeech = TimeSpan.FromSeconds(0.9);
    private static readonly TimeSpan SpeechPostRoll = TimeSpan.FromMilliseconds(350); // kept after the last loud moment
    private static readonly TimeSpan MicrophoneTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PreviewAfter = TimeSpan.FromSeconds(0.8);      // of audio before the first look at what you're saying
    private static readonly TimeSpan PreviewEvery = TimeSpan.FromSeconds(0.5);      // of new audio before the next

    private static readonly PillShape ListeningShape = new(250, 36, 18);
    private static readonly PillShape ThinkingShape = new(190, 36, 18);
    private static readonly PillShape StatusShape = new(330, 36, 18);
    private const float MaxAnswerHeight = 210; // the tallest the island's stage has room for, with the spring's bounce
    private static readonly TimeSpan AfterSpeaking = TimeSpan.FromSeconds(4); // an answer stays this long once it's been said
    private static readonly PillShape DraftShape = new(440, 140, 30);
    private static readonly PillShape TypingShape = new(400, 44, 22);
    private static readonly PillShape TypingChangeShape = new(400, 58, 26); // with what's being changed above the box
    private const float LiveTextMaxWidth = 420; // what you're saying: one line up to this wide, then two
    private static readonly TimeSpan DraftWaits = TimeSpan.FromMinutes(1);

    private BrainResult? _draft;    // waiting for your yes or no
    private bool _confirming;       // listening for that yes or no (or a change)
    private bool _followingUp;      // listening for a follow-up question, the answer still up
    private int _loudOverAnswer;    // loud readings in a row while the answer's being said
    private float _loudestOverAnswer = float.NegativeInfinity; // for the log
    private float _echoDb;          // how loud its own voice came back at the start of the answer
    private BrainResult? _changing; // typing a change to this draft
    private bool _typed;            // the request being answered was typed: the answer is shown, not spoken
    private int _answerCount;       // which answer is up, so finishing saying an older one doesn't hide it

    private readonly DispatcherQueue _dispatcher;
    private readonly AssistantView _view = new();
    private readonly SpeechToText _speechToText = new();
    private readonly Speaker _speaker = new();
    private readonly SpeechDetector _speech = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _voiceTimer;                    // while it speaks: how loud, for the orb and the glow
    private readonly DispatcherQueueTimer _micRetry;                      // the microphone wouldn't open for the wake phrase: try again
    private static readonly TimeSpan FirstMicRetry = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LastMicRetry = TimeSpan.FromMinutes(5);
    private TimeSpan _micRetryAfter = FirstMicRetry;
    private readonly PlaybackMeter _voiceMeter = new(anyOutput: true);
    private bool _busy;                                                   // working something out with the answer or draft still up
    private HotkeyListener? _hotkey;
    private string? _listeningFor; // the shortcut registered, null when none
    private HotkeyListener? _typeHotkey;
    private string? _typeListeningFor;
    private Mode _mode;
    private PillShape _shape = ListeningShape;
    private bool _starting;
    private double? _downloadProgress; // set while the speech model downloads

    // The microphone: open while listening to a request, and all the time while listening for the wake phrase.
    private MicrophoneRecorder? _mic;
    private bool _micIsBluetooth;
    private Task<string?>? _micOpening;
    private WakeWordDetector? _wake;
    private bool _shutDown; // Glyde is quitting: the microphone stays closed

    // The request being listened to: where it starts in the microphone's audio, and whether you've finished.
    private volatile bool _inRequest;
    private long _requestFrom;
    private long _speechTo; // where you last spoke
    private bool _listenedAgain; // this is the second listen, after hearing nothing to do

    // The PC's sound, paused or turned down while listening (as phones do) so it doesn't drown you out.
    private const float TurnDownTo = 0.3f; // of the volume it was at
    private readonly PlaybackMeter _playback = new();
    private List<Windows.Media.Control.GlobalSystemMediaTransportControlsSession> _pausedMedia = [];
    private float? _volumeBeforeTurnDown;
    private float _turnedDownTo;
    private bool _quietedThisRequest;
    private long _listeningSince;
    private long _lastSpeechAt;
    private bool _heardSpeech;
    private float _loudestDb; // for the log when nothing was heard

    // What you're saying, shown as you say it: from Deepgram when that's on, else every so often a quick
    // look at the audio so far (see SpeechToText.PreviewAsync).
    private Task? _previewing;
    private long _previewedTo;  // where the last look ended
    private int _requestNumber; // which request words are of: any that come after it's moved on are dropped
    private string _liveText = "";

    // The request's audio, streamed to Deepgram as it's recorded (when that's on), and how far it's got.
    private static readonly TimeSpan DeepgramWaits = TimeSpan.FromSeconds(5); // to finish, once you've stopped
    private DeepgramStream? _stream;
    private double _streamedTo;

    public AssistantActivity(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _hideTimer = dispatcher.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            if (_mode is Mode.Answer or Mode.Status && (IsPointerOver || _inRequest))
            {
                // You're reading it (it stays while the pointer's on it), or asking a follow-up.
                HideAfter(TimeSpan.FromSeconds(1.5));
            }
            else if (_mode is Mode.Answer or Mode.Status)
            {
                SetMode(Mode.Off);
            }
            else if (_mode == Mode.Draft)
            {
                if (_inRequest)
                {
                    HideAfter(TimeSpan.FromSeconds(15)); // you're answering: give it time
                }
                else
                {
                    Decide(open: false, quietly: true); // left waiting too long
                }
            }
        };
        _voiceTimer = dispatcher.CreateTimer();
        _voiceTimer.Interval = TimeSpan.FromMilliseconds(33);
        _voiceTimer.Tick += (_, _) => OnVoiceTick();
        _micRetry = dispatcher.CreateTimer();
        _micRetry.IsRepeating = false;
        _micRetry.Tick += (_, _) => UpdateWakeWord();
        _view.Clicked += OnClicked;
        _view.DraftDecided += open => Decide(open);
        _view.Typed += OnTyped;
        _view.TypingCancelled += StopTyping;
        UpdateLanguages();
        AppSettings.Current.Changed += (_, _) =>
        {
            if (!IsEnabled && _mode != Mode.Off)
            {
                Cancel();
            }
            UpdateShortcut();
            UpdateWakeWord();
            UpdateLanguages();
            RaiseChanged();
        };
    }

    private void UpdateLanguages() =>
        _speechToText.Languages = AppSettings.Current.SpeechLanguages == SpeechLanguages.EnglishAndHindi ? ["en", "hi"] : ["en"];

    public override string Name => Assistant.Name;

    /// <summary>Above everything: you asked for it.</summary>
    public override int Priority => 110;

    public override bool WantsCompact => IsEnabled && (_mode != Mode.Off || _downloadProgress is not null);

    public override bool HoldsIsland => IsEnabled && _mode != Mode.Off;

    public override PillShape CompactShape => _shape;

    public override FrameworkElement CompactView => _view;

    /// <summary>The orb's loops run only while the island shows it.</summary>
    public override void OnShown(bool compact, bool expanded) => _view.SetShown(compact);

    /// <summary>How the island's glow should look: listening to you, thinking, talking, or waiting on you.</summary>
    public GlowMode Glow => !IsEnabled ? GlowMode.Off : _mode switch
    {
        Mode.Listening => GlowMode.Listening,
        Mode.Thinking => GlowMode.Thinking,
        Mode.Answer or Mode.Draft when _speaker.IsSpeaking || _demoSpeaking => GlowMode.Speaking,
        Mode.Answer or Mode.Draft when _busy => GlowMode.Thinking,
        Mode.Answer or Mode.Draft when _inRequest => GlowMode.Listening, // a follow-up, or your answer to the draft
        Mode.Answer or Mode.Draft or Mode.Typing => GlowMode.Waiting,
        _ => GlowMode.Off,
    };

    /// <summary>How loud whoever's talking is (0 to 1): you while it listens, it while it answers. About 30 times a second.</summary>
    public event Action<float>? VoiceLevel;

    public static bool IsEnabled => AppSettings.Current.VoiceAssistant;

    public static bool WakeWordEnabled => IsEnabled && AppSettings.Current.WakeWord;

    public static string Shortcut => AppSettings.Current.AssistantShortcut;

    /// <summary>False while another app owns the chosen shortcut (Settings says so).</summary>
    public static bool ShortcutWorks { get; private set; } = true;

    public static string TypeShortcut => AppSettings.Current.TypeShortcut;

    /// <summary>False while another app owns the chosen type shortcut.</summary>
    public static bool TypeShortcutWorks { get; private set; } = true;

    /// <summary>
    /// Typing needs the keyboard, which the island's window never takes otherwise: true to take it, false
    /// to give it back to the app you were in.
    /// </summary>
    public event Action<bool>? KeyboardWanted;

    /// <summary>
    /// Listening to a request. The island's mic dot counts WinIsland's own microphone use only then, not
    /// while it's just waiting for its name (that would keep the dot up all day).
    /// </summary>
    public static bool IsListeningToRequest { get; private set; }

    /// <summary>Why the wake phrase isn't being listened for although it's turned on; null when it is (or it's off).</summary>
    public static string? WakeWordProblem { get; private set; }

    public override Task StartAsync()
    {
        _hotkey = new HotkeyListener("TalkHotkey");
        _hotkey.Pressed += Toggle;
        _typeHotkey = new HotkeyListener("TypeHotkey");
        _typeHotkey.Pressed += StartTyping;
        UpdateShortcut();

        // A headset connecting can change which microphone to listen with.
        MediaDevice.DefaultAudioCaptureDeviceChanged += (_, _) => _dispatcher.TryEnqueue(OnMicrophonesChanged);

        if (IsEnabled)
        {
            AppCatalog.Preload();
        }
        UpdateWakeWord();

        // Upgrade to the better request model (the small one stands in until it's here).
        if (IsEnabled && SpeechToText.CanTranscribe && !SpeechToText.RequestModel.IsDownloaded)
        {
            _ = DownloadModelAsync(SpeechToText.RequestModel);
        }
        return Task.CompletedTask;
    }

    /// <summary>Listens for the chosen shortcuts while the assistant is on, and lets go of them while it's off.</summary>
    private void UpdateShortcut()
    {
        if (_hotkey is null || _typeHotkey is null)
        {
            return;
        }

        string? talk = IsEnabled ? Shortcut : null;
        if (talk != _listeningFor)
        {
            _listeningFor = talk;
            ShortcutWorks = Register(_hotkey, talk, "to talk");
        }

        string? type = IsEnabled ? TypeShortcut : null;
        if (type != _typeListeningFor)
        {
            _typeListeningFor = type;
            TypeShortcutWorks = Register(_typeHotkey, type, "to type");
        }
    }

    /// <summary>Listens for <paramref name="shortcut"/>, or lets go when it's null. False if another app owns it.</summary>
    private static bool Register(HotkeyListener hotkey, string? shortcut, string purpose)
    {
        if (shortcut is null)
        {
            hotkey.Unregister();
            return true;
        }

        bool works = hotkey.Register(shortcut);
        Log.Info(works
            ? $"{Assistant.Name} listens for {shortcut} ({purpose})"
            : $"{shortcut} is taken by another app; use the island's button {purpose} instead");
        return works;
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
            case Mode.Draft:
                // Answering the draft by voice: start listening for it, or stop.
                _ = _inRequest ? FinishRequestAsync(stoppedByYou: true) : StartRequestAsync(from: null, requestStarted: false, keepView: true);
                break;
            case Mode.Typing:
                // Talking instead.
                StopTyping();
                Toggle();
                break;
            case Mode.Answer when _inRequest && _followingUp && _speaker.IsSpeaking:
                // Interrupting the answer: it stops talking and listens to you.
                Interrupt();
                break;
            case Mode.Answer when _inRequest:
                // Asking a follow-up question: pressing again means that's all.
                _ = FinishRequestAsync(stoppedByYou: true);
                break;
            case Mode.Answer:
                // A follow-up question, the answer still up (it stops talking, if it still is).
                _speaker.Stop();
                _ = StartRequestAsync(from: null, requestStarted: false, keepView: true);
                break;
            default:
                _ = SpeechToText.CanTranscribe ? StartRequestAsync(from: null, requestStarted: false) : DownloadModelAsync(SpeechToText.RequestModel);
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
            if (_inRequest)
            {
                EndRequest(); // no follow-up then
                _followingUp = false;
            }
            SetMode(Mode.Off);
        }
    }

    // ---------------------------------------------------------------- typing a request

    /// <summary>
    /// The type shortcut or the island's keyboard button: a box to type a request in, or, while a draft
    /// is up, a change to it ("make it shorter"; "yes" opens it).
    /// </summary>
    public void StartTyping()
    {
        if (!IsEnabled || _starting || _mode is Mode.Thinking or Mode.Typing)
        {
            return;
        }

        if (_inRequest)
        {
            EndRequest(); // was listening: typing instead
            _confirming = false;
            _followingUp = false;
        }
        _requestNumber++; // (a look at what was being said is of no use now)
        _speaker.Stop();
        _hideTimer.Stop();

        _changing = _mode == Mode.Draft ? _draft : null;
        if (_changing is { } draft)
        {
            _view.ShowTyping("Type a change, or yes / no", $"Changing: {Describe(draft).Title}");
            _shape = TypingChangeShape;
        }
        else
        {
            _view.ShowTyping($"Ask {Assistant.Name} anything…", hint: null);
            _shape = TypingShape;
        }
        SetMode(Mode.Typing);
        KeyboardWanted?.Invoke(true);
        _dispatcher.TryEnqueue(_view.FocusTypeBox); // once the island has the keyboard
    }

    /// <summary>Enter: ask (or change the draft). Nothing typed just closes the box.</summary>
    private void OnTyped(string text)
    {
        if (_mode != Mode.Typing)
        {
            return;
        }
        if (text.Length == 0)
        {
            StopTyping();
            return;
        }

        var draft = _changing;
        _changing = null;
        _typed = true;
        _view.ShowStatus("Thinking…", busy: true);
        _shape = ThinkingShape;
        SetMode(Mode.Thinking);
        KeyboardWanted?.Invoke(false); // back to the app you were in while it works

        _ = draft is not null && _draft == draft ? ChangeDraftAsync(draft, text) : AnswerAsync(text);
    }

    /// <summary>Esc (or the island lost the keyboard): close the box, back to the draft it was changing.</summary>
    private void StopTyping()
    {
        if (_mode != Mode.Typing)
        {
            return;
        }

        var draft = _changing;
        _changing = null;
        if (draft is not null && _draft == draft)
        {
            ShowDraftCard(draft);
        }
        else
        {
            SetMode(Mode.Off);
        }
        KeyboardWanted?.Invoke(false);
    }

    /// <summary>You clicked into another app while typing.</summary>
    public void OnKeyboardLost() => StopTyping();

    // ---------------------------------------------------------------- the microphone

    /// <summary>
    /// Keeps the microphone open (and the model loaded) while listening for the wake phrase, closes it
    /// when nothing needs it, and pauses wake-phrase listening while busy with a request.
    /// </summary>
    private void UpdateWakeWord()
    {
        if (_shutDown)
        {
            return;
        }

        bool wanted = WakeWordEnabled;
        if (wanted && !SpeechToText.WakeModel.IsDownloaded)
        {
            if (_downloadProgress is null && _mode == Mode.Off)
            {
                _ = DownloadModelAsync(SpeechToText.WakeModel); // you turned the wake phrase on; it needs its model
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
            // Windows' audio sometimes stops handing out the microphone for a while (a driver's sound
            // effects hanging, a device being switched): try again by itself, less and less often, so
            // the wake phrase comes back once Windows does, without restarting Glyde.
            Log.Info($"Can't listen for \"{Assistant.WakePhrase}\": {problem} Trying again in {_micRetryAfter.TotalSeconds:0} s.");
            WakeWordProblem = problem;
            _micRetry.Interval = _micRetryAfter;
            _micRetry.Start();
            _micRetryAfter = _micRetryAfter * 2 < LastMicRetry ? _micRetryAfter * 2 : LastMicRetry;
            return;
        }

        _micRetryAfter = FirstMicRetry;
        WakeWordProblem = null;
        Log.Info($"Listening for \"{Assistant.WakePhrase}\"");
        _ = _speechToText.WarmUpWakeCheckAsync();
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
            var watch = System.Diagnostics.Stopwatch.StartNew();
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
            if (_shutDown)
            {
                mic.Dispose(); // Glyde quit while it opened
                return "Glyde is quitting.";
            }

            _mic = mic;
            _micIsBluetooth = choice.IsBluetooth;
            _wake = new WakeWordDetector(mic, _speechToText, _dispatcher) { Paused = true };
            _wake.Heard += OnWakePhrase;
            Log.Info($"Microphone open: {choice.Name} (in {watch.ElapsedMilliseconds} ms)");
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

    /// <summary>
    /// Glyde is quitting: stop talking and close the microphone properly, rather than leave it to the
    /// process ending (a capture stream cut off that way has left Windows' audio stuck), and keep it closed.
    /// </summary>
    public void Shutdown()
    {
        _shutDown = true;
        _micRetry.Stop();
        _speaker.Stop();
        if (_mic is not null)
        {
            CloseMicrophone();
        }
    }

    /// <summary>The microphone's loudness, on the audio thread: to the waveform during a request, else to the wake-phrase detector.</summary>
    private void OnMicrophoneLevel(float rms, long position)
    {
        if (_inRequest)
        {
            _dispatcher.TryEnqueue(() => OnRequestLevel(rms, position));
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
            ShowStatus("The microphone stopped.", TimeSpan.FromSeconds(4), problem: true);
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

    /// <param name="from">Where the request starts in the microphone's audio (at or just after the wake phrase); null for now.</param>
    /// <param name="requestStarted">You already began the request (said it in one breath with the wake phrase).</param>
    /// <param name="keepView">
    /// What's shown stays up while listening (the orb pulses as you speak): a draft, for your answer to
    /// it, or an answer, for a follow-up question.
    /// </param>
    /// <param name="again">Listening once more, after hearing nothing to do.</param>
    private async Task StartRequestAsync(long? from, bool requestStarted, bool keepView = false, bool again = false)
    {
        _starting = true;
        _listenedAgain = again;
        _requestNumber++;
        _liveText = "";
        try
        {
            // A follow-up can start while the answer's still being said: you may talk over it.
            if (!(keepView && _mode == Mode.Answer))
            {
                _speaker.Stop();
            }
            var listeningMode = keepView ? _mode : Mode.Listening;
            if (!keepView)
            {
                // Faint bars until the microphone is really recording (it takes a moment to start):
                // words said before that would be lost.
                _hideTimer.Stop();
                _view.ShowListening(ready: _mic is not null);
                _shape = ListeningShape;
                SetMode(Mode.Listening);
            }
            _confirming = keepView && _mode == Mode.Draft;
            _followingUp = keepView && _mode == Mode.Answer;
            _typed = false;

            if (await OpenMicrophoneAsync() is { } problem)
            {
                Log.Info($"Can't listen: {problem}");
                if (!_followingUp)
                {
                    ShowStatus(problem, TimeSpan.FromSeconds(6), problem: true); // (no follow-up then: the answer just stays up)
                }
                _followingUp = false;
                return;
            }
            if (_mode != listeningMode || _mic is null)
            {
                _followingUp = false;
                return; // turned off (or answered with a button) meanwhile
            }
            if (!keepView)
            {
                _view.SetListeningReady();
            }
            else if (_followingUp)
            {
                _view.ShowFollowUpHint(true);
            }

            // Deepgram hears it as you say it, when that's on: connect now, while you start talking.
            DropStream();
            string? deepgramKey = AppSettings.Current.DeepgramSpeech ? DeepgramStream.Key : null;
            if (deepgramKey is not null)
            {
                int request = _requestNumber;
                _stream = DeepgramStream.Start(deepgramKey, _speechToText.Languages);
                _stream.Heard += text => _dispatcher.TryEnqueue(() =>
                {
                    if (request == _requestNumber && text.Length > 0)
                    {
                        ShowLiveText(Assistant.WithoutWakePhrase(text));
                    }
                });
            }

            // Load the PC's speech model while you talk, unless Deepgram or Gemini will understand you
            // (it's only needed then if they can't, and keeps the graphics card busy). Only now: loading
            // it (half a gigabyte, onto the graphics card) slowed the microphone's start by a few hundred ms.
            if (deepgramKey is null && !UsesCloudSpeech)
            {
                _ = _speechToText.WarmUpAsync();
            }

            _requestFrom = from ?? _mic.Position;
            _previewedTo = _requestFrom;
            _streamedTo = _requestFrom;
            _speechTo = _mic.Position;
            _listeningSince = Environment.TickCount64;
            _heardSpeech = requestStarted;
            _lastSpeechAt = requestStarted ? _listeningSince : 0;
            _speech.Reset();
            _loudestDb = float.NegativeInfinity;
            _inRequest = true;
            IsListeningToRequest = true;
            RaiseChanged(); // the mic dot
            _loudOverAnswer = 0;
            _echoDb = float.NegativeInfinity;
            if (_followingUp)
            {
                _quietedThisRequest = false; // what was playing is still paused, and the answer's voice isn't music to turn down
            }
            else
            {
                _ = QuietPlaybackAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Starting to listen failed", ex);
            ShowStatus("The microphone isn't available.", TimeSpan.FromSeconds(4), problem: true);
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// You talked over the answer (or pressed the shortcut): it stops talking and listens to you, from
    /// a moment before you were noticed.
    /// </summary>
    private void Interrupt(long? at = null)
    {
        _speaker.Stop();
        if (_mic is not null)
        {
            _requestFrom = (at ?? _mic.Position) - _mic.ToSamples(InterruptPreRoll);
        }
        _listeningSince = Environment.TickCount64;
        Log.Info($"Interrupted the answer to listen{(at is null ? " (shortcut)" : $" (heard you over it: its own voice up to {_echoDb:0} dB, you {_loudestOverAnswer:0} dB)")}");
        _loudestOverAnswer = float.NegativeInfinity;
    }

    /// <summary>About 30 times a second while listening to a request, with the microphone's loudness (RMS) and position.</summary>
    private void OnRequestLevel(float rms, long position)
    {
        if (!_inRequest || _mic is null)
        {
            return; // a reading that arrived after we stopped
        }

        float db = SpeechDetector.ToDecibels(rms);
        if (!_speaker.IsSpeaking) // (while it talks, the orb and glow follow its own voice: see OnVoiceTick)
        {
            float level = Math.Clamp((db + 55) / 40, 0, 1);
            _view.PushLevel(level);
            VoiceLevel?.Invoke(level);
        }
        _speech.Update(db);
        _loudestDb = Math.Max(_loudestDb, db);

        long now = Environment.TickCount64;
        if (_followingUp && _speaker.IsSpeaking)
        {
            // While the answer's being said, only you talking over it counts (it stops, and listens to
            // you). Its own voice comes back into the microphone too, mostly taken out by Windows, which
            // also turns you down while it talks; how much of each depends on the speakers and the room.
            // So it first hears how loud its own voice comes back (the answer's first moments, before
            // you'd interrupt), and then counts you as talking over it when you're clearly louder than
            // that for most of a third of a second. Until then the follow-up's time hasn't started, nor
            // has its audio. (Timed from when the voice starts playing: a natural voice takes a moment to
            // get ready, and there's no echo to hear until then.)
            _loudestOverAnswer = Math.Max(_loudestOverAnswer, db);
            long playing = _speaker.PlayingSince;
            if (playing == 0 || now - playing < EchoMeasuredFor.TotalMilliseconds)
            {
                if (playing != 0)
                {
                    _echoDb = Math.Max(_echoDb, db);
                }
                _loudOverAnswer = 0;
            }
            else
            {
                bool overIt = _speech.IsLoud && db > Math.Max(_echoDb + InterruptAboveEcho, -45);
                _loudOverAnswer = Math.Max(0, _loudOverAnswer + (overIt ? 1 : -2)); // syllables have gaps; quiet stretches don't count
            }
            if (_loudOverAnswer < LoudReadingsToInterrupt)
            {
                _listeningSince = now;
                _requestFrom = position;
                return;
            }
            Interrupt(position);
        }
        else if (_loudestOverAnswer > float.NegativeInfinity)
        {
            // The answer's been said (you didn't talk over it: that's Interrupt): the follow-up's time
            // starts now. All the microphone heard meanwhile was its own voice coming back, so start
            // afresh; otherwise that echo counts as you having spoken and stopped, and it gives up at once.
            Log.Info($"While answering: its own voice up to {_echoDb:0} dB at first, loudest {_loudestOverAnswer:0} dB");
            _loudestOverAnswer = float.NegativeInfinity;
            _speech.Reset();
            _heardSpeech = false;
            _lastSpeechAt = 0;
            _listeningSince = now;
            _requestFrom = position;
            return;
        }

        if (_speech.IsSpeech)
        {
            _heardSpeech = true;
        }
        if (_speech.IsLoud && _heardSpeech)
        {
            _lastSpeechAt = now;
            _speechTo = position;
        }
        StreamAudio(position);
        PreviewSpeech(position);

        long listening = now - _listeningSince;
        if (listening > MaxListening.TotalMilliseconds
            || (!_heardSpeech && listening > (_followingUp ? FollowUpWaits : WaitForSpeech).TotalMilliseconds)
            || (_heardSpeech && now - _lastSpeechAt > PauseEndsSpeech.TotalMilliseconds))
        {
            _ = FinishRequestAsync(stoppedByYou: false);
        }
    }

    /// <summary>The request's audio up to a position, on to Deepgram (from where the request starts: not the answer still being said).</summary>
    private void StreamAudio(long to)
    {
        if (_stream is not { Failed: false } stream || _mic is null)
        {
            return;
        }
        _streamedTo = Math.Max(_streamedTo, _requestFrom);
        stream.Send(_mic.ReadOn(ref _streamedTo, to, playbackQuieted: _quietedThisRequest));
    }

    /// <summary>Lets go of the request's Deepgram stream, if any.</summary>
    private void DropStream()
    {
        if (_stream is { } stream)
        {
            _stream = null;
            _ = stream.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    /// Without Deepgram, while you speak, a quick look at what you've said so far, once there's a little
    /// and then whenever there's more since the last (one at a time: they take about half a second).
    /// </summary>
    private void PreviewSpeech(long position)
    {
        if (_stream is { Failed: false } || !_heardSpeech || _confirming || _mic is not { } mic || _previewing is { IsCompleted: false }
            || position - _previewedTo < mic.ToSamples(PreviewEvery) || position - _requestFrom < mic.ToSamples(PreviewAfter)
            || !SpeechToText.CanPreview)
        {
            return;
        }
        _previewing = PreviewAsync(mic, _requestFrom, position);
    }

    /// <summary>A look at the audio between two positions, and what was said in it up in the pill (unless the request's over).</summary>
    private async Task PreviewAsync(MicrophoneRecorder mic, long from, long to)
    {
        int request = _requestNumber;
        _previewedTo = to;
        string? text;
        try
        {
            bool quieted = _quietedThisRequest;
            text = await _speechToText.PreviewAsync(await Task.Run(() => mic.Read(from, to, quieted)));
        }
        catch (Exception ex)
        {
            Log.Error("A look at what's being said failed", ex);
            if (request == _requestNumber)
            {
                _previewedTo = long.MaxValue; // no more looks at this request
            }
            return;
        }
        if (request == _requestNumber && text is { Length: > 0 })
        {
            ShowLiveText(Assistant.WithoutWakePhrase(text));
        }
    }

    /// <summary>Once the look under way is done, another, to the end of what you said.</summary>
    private async Task LookAgainAsync(Task? underWay, MicrophoneRecorder mic, long from, long to)
    {
        if (underWay is not null)
        {
            await underWay;
        }
        await PreviewAsync(mic, from, to);
    }

    /// <summary>
    /// What you've said so far: under the waveform (kept up in place of "Thinking…" once you've finished),
    /// or, asking a follow-up, over the answer.
    /// </summary>
    private void ShowLiveText(string text)
    {
        _liveText = char.ToUpper(text[0]) + text[1..]; // ("Hey Glyde, what's…" → "What's…")
        if (_mode == Mode.Answer && _followingUp && _inRequest)
        {
            _view.ShowFollowUpText(_liveText, _shape.Width);
        }
        else if (_mode is Mode.Listening or Mode.Thinking)
        {
            ShowLiveTextPill();
        }
    }

    /// <summary>The pill as wide as what you've said needs: it grows as you talk, and doesn't shrink when a later look words it more briefly.</summary>
    private void ShowLiveTextPill()
    {
        bool growing = _view.IsShowingLiveText;
        var size = _view.ShowLiveText(_liveText, LiveTextMaxWidth, listening: _inRequest);
        float width = (float)Math.Max(size.Width, growing ? _shape.Width : ListeningShape.Width);
        float height = (float)Math.Ceiling(size.Height);
        _shape = new PillShape(width, height, Math.Min(28, height / 2)); // (rounded like an answer)
        RaiseChanged();
    }

    private void EndRequest()
    {
        _inRequest = false;
        IsListeningToRequest = false;
        DropStream(); // (unless it's been taken to hear the request with)
        RestorePlayback();
        RaiseChanged(); // the mic dot, and the glow (no longer listening)
    }

    /// <summary>
    /// Quiets the PC while you speak: music drowns out a voice, and taking it out with Windows' speech
    /// processing garbles the voice too (see <see cref="MicrophoneRecorder"/>). Whatever's playing is
    /// paused, and resumed once you've been heard; sound that can't be paused (an app without media
    /// controls) is turned down instead, and back up when listening ends.
    /// </summary>
    private async Task QuietPlaybackAsync()
    {
        _quietedThisRequest = false;
        try
        {
            var paused = await SystemActions.PausePlayingAsync();
            _pausedMedia.AddRange(paused);
            if (!_inRequest)
            {
                ResumeMedia(); // you'd finished already
                return;
            }
            if (paused.Count > 0)
            {
                Log.Info($"Paused what was playing while listening ({string.Join(", ", paused.Select(s => s.SourceAppUserModelId))})");
                _quietedThisRequest = true;
            }
            else
            {
                TurnDownPlayback();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Quieting the PC to listen failed", ex);
        }
    }

    /// <summary>Plays again what was paused for listening.</summary>
    private void ResumeMedia()
    {
        if (_pausedMedia.Count == 0)
        {
            return;
        }
        var paused = _pausedMedia;
        _pausedMedia = [];
        _ = SystemActions.ResumeAsync(paused);
    }

    /// <summary>The volume part of <see cref="QuietPlaybackAsync"/>: turned down if the speakers are playing.</summary>
    private void TurnDownPlayback()
    {
        try
        {
            if (_volumeBeforeTurnDown is not null || !_playback.IsPlaying() || SystemActions.GetVolume() is not { } volume || volume < 0.05f)
            {
                return;
            }
            _turnedDownTo = volume * TurnDownTo;
            if (SystemActions.SetVolumeQuietly(_turnedDownTo))
            {
                _volumeBeforeTurnDown = volume;
                _quietedThisRequest = true;
                Log.Info("Turned the sound down while listening");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Turning the sound down to listen failed", ex);
        }
    }

    /// <summary>Back to the volume it was, unless you (or a command) changed it meanwhile.</summary>
    private void RestorePlayback()
    {
        if (_volumeBeforeTurnDown is not { } before)
        {
            return;
        }
        _volumeBeforeTurnDown = null;
        try
        {
            if (SystemActions.GetVolume() is { } now && Math.Abs(now - _turnedDownTo) < 0.02f)
            {
                SystemActions.SetVolumeQuietly(before);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Turning the sound back up failed", ex);
        }
    }

    private async Task FinishRequestAsync(bool stoppedByYou)
    {
        if (!_inRequest || _mic is null)
        {
            return;
        }

        // Deepgram has heard nearly all of it already: the last moment, then it's kept to finish with.
        StreamAudio(_mic.Position);
        await using var stream = _stream;
        _stream = null;
        EndRequest();

        // Up to a moment after you stopped speaking: given the quiet after it too, Whisper tends to "hear"
        // things in the room's hum ("Thank you.", "Okay."). (The start isn't trimmed: a soft first word
        // like "play" can come before the loudness counts as speech.) Stopped with the shortcut,
        // everything up to now (you may have been speaking softly).
        long to = stoppedByYou || !_heardSpeech ? _mic.Position : Math.Min(_mic.Position, _speechTo + _mic.ToSamples(SpeechPostRoll));
        float[] samples = _mic.Read(_requestFrom, to, playbackQuieted: _quietedThisRequest);

        if (_confirming)
        {
            _confirming = false;
            await AnswerDraftAsync(samples, heardSomething: _heardSpeech || stoppedByYou, stream);
            return;
        }

        // No follow-up question: the answer stays up a little longer, and what was paused plays again.
        bool followUp = _followingUp;
        _followingUp = false;
        if (followUp)
        {
            _view.ShowFollowUpHint(false);
            if (!_heardSpeech && !stoppedByYou)
            {
                ResumeMedia();
                return;
            }
        }

        // Pressing the shortcut again means "that's all", so try even if it sounded quiet.
        if ((!_heardSpeech && !stoppedByYou) || samples.Length < MicrophoneRecorder.SampleRate / 3)
        {
            // Only how loud, never what: around -140 dB is digital silence (something blocking the
            // microphone), -60 to -40 a quiet room; speech is usually above -35.
            Log.Info($"Heard no request in {samples.Length / (double)MicrophoneRecorder.SampleRate:0.0} s (loudest {_loudestDb:0} dB)");
            ShowStatus("I didn't hear anything.", TimeSpan.FromSeconds(3), problem: true);
            return;
        }

        LookForAccent(samples); // (alongside working out what you said)

        // What you said stays up while it's worked out (the orb turning), else "Thinking…". Without
        // Deepgram, Gemini takes a moment to hear it: meanwhile, a last look shows it to the end. (Not
        // when this PC hears it: the look would hold that up.)
        if (_liveText.Length > 0)
        {
            ShowLiveTextPill();
            SetBusy(true);
        }
        else
        {
            _view.ShowStatus("Thinking…", busy: true);
            _shape = ThinkingShape;
        }
        SetMode(Mode.Thinking);
        if (stream is not { Failed: false } && UsesCloudSpeech && _previewedTo < to && SpeechToText.CanPreview)
        {
            _previewing = LookAgainAsync(_previewing, _mic, _requestFrom, to);
        }

        string heard;
        try
        {
            heard = await TranscribeAsync(samples, stream);
        }
        catch (Exception ex)
        {
            Log.Error("Understanding speech failed", ex);
            ShowStatus("Something went wrong understanding that.", TimeSpan.FromSeconds(4), problem: true);
            return;
        }
        if (_mode != Mode.Thinking)
        {
            return; // turned off meanwhile
        }
        if (IsNothingToDo(heard))
        {
            if (followUp)
            {
                SetMode(Mode.Off); // just a sound after the answer, not a question
                return;
            }

            // Only the wake phrase again, or what Whisper "hears" in a pause: you're probably about to
            // say it. Listen once more before giving up.
            if (!_listenedAgain)
            {
                Log.Info($"Nothing to do in \"{heard}\"; listening again");
                await StartRequestAsync(from: null, requestStarted: false, again: true);
                return;
            }
            ShowStatus("Sorry, I didn't catch that.", TimeSpan.FromSeconds(3), problem: true);
            return;
        }
        ShowLiveText(Assistant.WithoutWakePhrase(heard)); // (the final wording, while it's answered)
        await AnswerAsync(heard);
    }

    private static readonly HashSet<string> s_fillers =
        ["hey", "hi", "hello", "ok", "okay", "thank", "thanks", "you", "um", "uh", "hmm", "so", "the", "end"];

    /// <summary>Empty, the wake phrase alone ("Hey Glyde, hey Glyde"), or filler Whisper makes of a quiet moment ("Okay.", "Thank you.", "The End.").</summary>
    private static bool IsNothingToDo(string heard)
    {
        var names = new HashSet<string>([Assistant.Name.ToLowerInvariant(), .. Assistant.SoundsLike, .. Assistant.AlsoHeardAs]);
        return System.Text.RegularExpressions.Regex.Replace(heard.ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}]+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => s_fillers.Contains(word) || names.Contains(word));
    }

    /// <summary>Gemini turns the requests into text, when that's on and it can (more accurate); else the PC.</summary>
    private static bool UsesCloudSpeech => AppSettings.Current.CloudSpeech && Brains.Current is { CanHear: true };

    private static readonly TimeSpan CloudSpeechWaits = TimeSpan.FromSeconds(8);

    /// <summary>
    /// What you said: by Deepgram when it heard it as you said it; else (or when it couldn't) by Gemini
    /// when that's chosen; else (or when that's busy, slow or offline) on this PC.
    /// </summary>
    private async Task<string> TranscribeAsync(float[] samples, DeepgramStream? stream)
    {
        if (stream is not null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string heard = await stream.FinishAsync(DeepgramWaits);
                Log.Info($"Deepgram heard {samples.Length / (double)MicrophoneRecorder.SampleRate:0.0} s of speech, done {watch.ElapsedMilliseconds} ms after you stopped: \"{heard}\"" +
                         (heard.Length == 0 ? $" ({stream.Summary})" : ""));
                return heard;
            }
            catch (DeepgramException ex)
            {
                Log.Info($"Deepgram couldn't make out the speech ({ex.Message}, after {watch.ElapsedMilliseconds} ms); another way instead");
            }
        }

        if (AppSettings.Current.CloudSpeech && Brains.Current is { CanHear: true } brain)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var timeout = new CancellationTokenSource(CloudSpeechWaits);
                string heard = await brain.TranscribeAsync(samples, timeout.Token);
                Log.Info($"{brain.Name} heard {samples.Length / (double)MicrophoneRecorder.SampleRate:0.0} s of speech in {watch.ElapsedMilliseconds} ms: \"{heard}\"");
                return heard;
            }
            catch (Exception ex) when (ex is BrainException or OperationCanceledException)
            {
                Log.Info($"{brain.Name} couldn't make out the speech ({ex.Message}, after {watch.ElapsedMilliseconds} ms); on this PC instead");
            }
        }
        return await _speechToText.TranscribeAsync(samples, CancellationToken.None);
    }

    /// <summary>Carries out a request, said or typed (while "Thinking…"): on the PC if it can, else with the AI brain.</summary>
    private async Task AnswerAsync(string heard)
    {
        // What was paused for listening stays paused for the conversation (it plays again when the
        // island closes), unless the request is about it: "pause" keeps it paused, "next song" needs it back.
        if (_pausedMedia.Count > 0 && VoiceCommands.AsksToPause(heard))
        {
            _pausedMedia = [];
            ShowAnswer(heard, new CommandResult("Paused.", Speak: false));
            return;
        }
        if (VoiceCommands.ControlsMedia(heard))
        {
            ResumeMedia();
        }

        try
        {
            var result = await VoiceCommands.RunAsync(heard);
            if (_mode != Mode.Thinking)
            {
                return;
            }
            if (result.Understood)
            {
                Remember(heard, result.Reply);
                ShowAnswer(heard, result);
                return;
            }

            // Nothing local: the AI brain's turn, if there is one.
            if (Brains.Current is not { } brain)
            {
                ShowAnswer(heard, new CommandResult("I can't do that yet. Add a Claude or a free Gemini key in Settings and I can do more."));
                return;
            }
            string request = Assistant.WithoutWakePhrase(heard);
            string? copied = MentionsCopiedText(request) ? await Drafts.CopiedTextAsync() : null;
            var answer = await brain.AskAsync(request, copied, Conversation(), CancellationToken.None);
            Remember(request, answer switch
            {
                BrainAnswer text => text.Text,
                EmailDraft email => $"(Wrote an email to {Recipient(email.RecipientName, email.To)}, subject \"{email.Subject}\": {email.Body})",
                WhatsAppDraft message => $"(Wrote a WhatsApp message to {Recipient(message.RecipientName, message.Phone)}: {message.Message})",
                ClipboardResult copiedResult => $"({copiedResult.Summary}: {copiedResult.Text})",
                _ => "(Done.)",
            });
            if (_mode == Mode.Thinking)
            {
                ShowBrainResult(heard, answer);
            }
        }
        catch (BrainException ex)
        {
            ShowStatus(ex.Message, TimeSpan.FromSeconds(6), problem: true);
        }
        catch (Exception ex)
        {
            Log.Error("Answering a request failed", ex);
            ShowStatus("Something went wrong with that.", TimeSpan.FromSeconds(4), problem: true);
        }
    }

    // ---------------------------------------------------------------- the conversation

    // The last few exchanges, so a follow-up ("and tomorrow?", "what about Delhi?") makes sense to the
    // brain. Forgotten after a few minutes of quiet: by then it's a new conversation.
    private static readonly TimeSpan ConversationLasts = TimeSpan.FromMinutes(3);
    private const int ConversationTurns = 6;
    private readonly List<ConversationTurn> _conversation = [];
    private DateTime _conversationAt;

    private IReadOnlyList<ConversationTurn> Conversation()
    {
        if (DateTime.UtcNow - _conversationAt > ConversationLasts)
        {
            _conversation.Clear();
        }
        return [.. _conversation];
    }

    private void Remember(string user, string assistant)
    {
        Conversation(); // forget a stale one first
        _conversation.Add(new ConversationTurn(user, assistant));
        if (_conversation.Count > ConversationTurns)
        {
            _conversation.RemoveAt(0);
        }
        _conversationAt = DateTime.UtcNow;
    }

    private static string Recipient(string name, string address) => name.Length > 0 ? name : address.Length > 0 ? address : "someone";

    /// <summary>
    /// Whether a request is about the copied text: only then is the clipboard sent along ("translate
    /// this", "summarise what I copied", "make this formal").
    /// </summary>
    private static bool MentionsCopiedText(string request) =>
        System.Text.RegularExpressions.Regex.IsMatch(request, @"\b(this|these|copied|clipboard|selected|the text)\b|इसे|इसको|यह",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------- the brain's answers and drafts

    private void ShowBrainResult(string heard, BrainResult result)
    {
        switch (result)
        {
            case BrainAnswer answer:
                ShowAnswer(heard, new CommandResult(answer.Text));
                break;

            case ClipboardResult copied:
                // Harmless (what you copied is still in the clipboard tab), so no need to ask first.
                Drafts.Open(copied);
                ShowAnswer(heard, copied.Text, $"{(copied.Summary.Length > 0 ? copied.Summary : "Done")}. It's copied.");
                break;

            default:
                _ = ShowDraftAsync(result);
                break;
        }
    }

    /// <summary>
    /// Shows a draft. Asked by voice, it asks whether to open it, then listens for yes, no, or a change;
    /// typed, it waits for its buttons (or a typed change).
    /// </summary>
    private async Task ShowDraftAsync(BrainResult draft)
    {
        _draft = draft;
        ShowDraftCard(draft);
        if (_typed)
        {
            return;
        }

        await SayAsync(Describe(draft).Question);
        if (_mode == Mode.Draft && _draft == draft && !_inRequest)
        {
            await StartRequestAsync(from: null, requestStarted: false, keepView: true);
        }
    }

    private void ShowDraftCard(BrainResult draft)
    {
        var (title, subject, body, open, _) = Describe(draft);
        _view.ShowDraft(title, subject, body, open);
        _shape = DraftShape;
        SetMode(Mode.Draft);
        HideAfter(DraftWaits);
    }

    private static (string Title, string Subject, string Body, string Open, string Question) Describe(BrainResult draft)
    {
        static string To(string name, string address) => name.Length > 0 ? $" to {name}" : address.Length > 0 ? $" to {address}" : "";
        return draft switch
        {
            EmailDraft e => ($"Email{To(e.RecipientName, e.To)}", e.Subject, e.Body, "Open in Gmail", "Shall I open it in Gmail?"),
            WhatsAppDraft w => ($"WhatsApp{To(w.RecipientName, w.Phone)}", "", w.Message, "Open WhatsApp", "Shall I open it in WhatsApp?"),
            _ => ("Draft", "", "", "Open", "Shall I open it?"),
        };
    }

    /// <summary>Your spoken answer to a draft.</summary>
    /// <param name="stream">It, as Deepgram heard it, when that's on.</param>
    private async Task AnswerDraftAsync(float[] samples, bool heardSomething, DeepgramStream? stream)
    {
        ResumeMedia(); // paused while you answered
        if (_draft is not { } draft || _mode != Mode.Draft)
        {
            return;
        }
        if (!heardSomething || samples.Length < MicrophoneRecorder.SampleRate / 3)
        {
            return; // no answer: the draft stays up, with its buttons, until it times out
        }

        SetBusy(true);
        string heard;
        try
        {
            heard = await TranscribeAsync(samples, stream);
        }
        catch (Exception ex)
        {
            Log.Error("Understanding the answer to the draft failed", ex);
            SetBusy(false);
            return;
        }
        if (_draft != draft)
        {
            return; // answered with a button meanwhile
        }
        if (heard.Length == 0)
        {
            SetBusy(false);
            return;
        }
        await ChangeDraftAsync(draft, heard);
    }

    /// <summary>Your answer to a draft, said or typed: yes opens it, no drops it, anything else changes it.</summary>
    private async Task ChangeDraftAsync(BrainResult draft, string answer)
    {
        switch (YesOrNo(answer))
        {
            case true:
                Decide(open: true);
                return;
            case false:
                Decide(open: false);
                return;
        }

        try
        {
            if (Brains.Current is not { } brain)
            {
                ShowDraftCard(draft); // the AI was turned off meanwhile: keep the draft as it is
                return;
            }
            var revised = await Brains.ReviseAsync(brain, draft, answer, CancellationToken.None);
            if (_draft == draft)
            {
                _draft = null;
                ShowBrainResult(answer, revised);
            }
        }
        catch (BrainException ex)
        {
            _draft = null;
            ShowStatus(ex.Message, TimeSpan.FromSeconds(6), problem: true);
        }
        catch (Exception ex)
        {
            Log.Error("Changing the draft failed", ex);
            if (_draft == draft)
            {
                ShowDraftCard(draft);
            }
        }
    }

    /// <summary>"Yes" / "haan" (true), "no" / "nahi" (false), or something else (null: a change to make).</summary>
    private static bool? YesOrNo(string heard)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(heard.ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}' ]+", " ").Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(text,
                @"^(yes|yeah|yep|yup|sure|ok|okay|open( it)?|send( it)?|go ahead|do it|please do|haan|han|ha|haa|theek hai|thik hai|ji|हां|हाँ|हा|ठीक है)\b"))
        {
            return true;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(text,
                @"^(no|nope|nah|cancel|don't|do not|stop|never mind|forget it|nahi|nahin|mat|नहीं|नही|ना|रहने दो)\b"))
        {
            return false;
        }
        return null;
    }

    /// <summary>Opens the draft (in Gmail or WhatsApp, for you to send) or drops it.</summary>
    private void Decide(bool open, bool quietly = false)
    {
        if (_draft is not { } draft)
        {
            return;
        }
        _draft = null;
        if (_inRequest)
        {
            EndRequest(); // stop listening for the answer
            _confirming = false;
        }
        _speaker.Stop();

        if (!open)
        {
            if (quietly)
            {
                SetMode(Mode.Off);
            }
            else
            {
                ShowStatus("OK, dropped it.", TimeSpan.FromSeconds(2));
            }
            return;
        }

        try
        {
            Drafts.Open(draft);
            Log.Info($"Opened a {(draft is EmailDraft ? "Gmail" : "WhatsApp")} draft");
            ShowStatus(draft is EmailDraft
                ? "Opened in Gmail: press Send when you're happy."
                : "Opened in WhatsApp: pick the chat and press Send.", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Error("Opening the draft failed", ex);
            ShowStatus("Couldn't open it.", TimeSpan.FromSeconds(4), problem: true);
        }
    }

    // ---------------------------------------------------------------- the speech model

    /// <summary>
    /// Downloads a speech model, showing progress in the pill. The request model is also fetched as an
    /// upgrade when only the small one is there (which stands in meanwhile).
    /// </summary>
    private async Task DownloadModelAsync(SpeechModel model)
    {
        if (_downloadProgress is not null)
        {
            return; // already on one
        }

        bool upgrade = SpeechToText.CanTranscribe; // Glyde works already; this one's just better
        SetDownloadProgress(0);
        try
        {
            await _speechToText.DownloadAsync(model, new Progress<double>(SetDownloadProgress));
            _downloadProgress = null;
            ShowStatus(upgrade
                ? $"{Assistant.Name}'s better speech model is ready."
                : WakeWordEnabled
                    ? $"{Assistant.Name} is ready: say “{Assistant.WakePhrase}” or press {Shortcut}."
                    : $"{Assistant.Name} is ready: press {Shortcut} and speak.", TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _downloadProgress = null;
            Log.Error($"Downloading {model.File} failed", ex);
            ShowStatus("Couldn't download the speech model. Try again later.", TimeSpan.FromSeconds(6), problem: true);
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

    private void ShowAnswer(string heard, CommandResult result) =>
        ShowAnswer(heard, result.Reply, result.Speak ? result.Reply : null);

    /// <param name="spoken">What to say (can differ from what's shown); null to say nothing. A typed request's answer is never said.</param>
    private void ShowAnswer(string heard, string shown, string? spoken)
    {
        // As tall as the whole answer needs (wider for long ones), up to what the island has room for;
        // beyond that it scrolls.
        _view.ShowAnswer(heard, shown);
        float width = shown.Length > 80 ? 440 : 380;
        float height = (float)Math.Clamp(Math.Ceiling(_view.AnswerHeight(width)), 56, MaxAnswerHeight);
        _shape = new PillShape(width, height, Math.Min(28, height / 2));
        SetMode(Mode.Answer);
        _hideTimer.Stop();

        if (_typed)
        {
            spoken = null;
        }
        if (spoken is null)
        {
            ResumeMedia();
            HideAfter(ReadingTime(shown));
        }
        else
        {
            _ = SpeakThenHideAsync(spoken, shown, ++_answerCount);
        }
    }

    /// <summary>
    /// Says the answer; it stays up until it's been said, then long enough to finish reading it. When
    /// follow-ups are on, Glyde listens all the while: talk over it to interrupt, or ask after it. What
    /// was paused stays paused meanwhile.
    /// </summary>
    private async Task SpeakThenHideAsync(string spoken, string shown, int answer)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var speaking = SayAsync(spoken);
        bool followUps = AppSettings.Current.FollowUps;
        if (followUps && !_inRequest)
        {
            await StartRequestAsync(from: null, requestStarted: false, keepView: true);
        }
        await speaking;
        if (answer != _answerCount || _mode != Mode.Answer)
        {
            return; // something else is up now (or you interrupted it)
        }

        HideAfter(Max(AfterSpeaking, ReadingTime(shown) - watch.Elapsed));
        if (!followUps)
        {
            ResumeMedia();
        }
    }

    /// <summary>Time to read something: a few seconds, plus a third of a second a word.</summary>
    private static TimeSpan ReadingTime(string text) =>
        TimeSpan.FromSeconds(Math.Clamp(3 + text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 0.35, 4, 40));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private void ShowStatus(string message, TimeSpan duration, bool problem = false)
    {
        // One line usually; up to three for a longer message (an error from Gemini, say).
        _view.ShowStatus(message, problem: problem);
        float width = message.Length > 45 ? 420 : StatusShape.Width;
        float height = (float)Math.Clamp(Math.Ceiling(_view.StatusHeight(width)), StatusShape.Height, 100);
        _shape = new PillShape(width, height, Math.Min(22, height / 2));
        SetMode(Mode.Status);
        HideAfter(Max(duration, ReadingTime(message)));
    }

    private void HideAfter(TimeSpan duration)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = duration;
        _hideTimer.Start();
    }

    /// <summary>
    /// Says something; meanwhile the orb and the island's glow move with the voice (see <see cref="OnVoiceTick"/>).
    /// Matching your accent, the first time it gives the brain a moment to say which it heard; after that it
    /// answers straight away in the accent heard last (the check, still going, is for next time).
    /// </summary>
    private async Task SayAsync(string text)
    {
        if (_accentLook is { IsCompleted: false } look && Speaker.HeardAccent is null)
        {
            await Task.WhenAny(look, Task.Delay(1500));
        }
        var saying = _speaker.SpeakAsync(text);
        _voiceTimer.Start();
        _view.SetSpeaking(true);
        RaiseChanged(); // the glow: speaking
        await saying;
    }

    private Task? _accentLook; // the brain listening to the last request for your accent
    private long _accentRestsUntil; // after the brain couldn't tell (busy): no more asking for a while
    private static readonly TimeSpan AccentRestsFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When replies match your accent (and there are voices with more than one), the brain listens to the
    /// request for it alongside understanding it. Its answer stays for the replies that follow (and the reminders).
    /// </summary>
    private void LookForAccent(float[] samples)
    {
        if (AppSettings.Current.ReplyAccent != AccentChoice.MatchMine || !Speaker.CanMatchAccent || Brains.Current is not { CanHear: true } brain
            || samples.Length < MicrophoneRecorder.SampleRate // (under a second: too little to tell)
            || Environment.TickCount64 < _accentRestsUntil)
        {
            return;
        }
        _accentLook = LookAsync();

        async Task LookAsync()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                if (await brain.AccentOfAsync(samples, timeout.Token) is { } accent)
                {
                    if (accent != Speaker.HeardAccent)
                    {
                        Log.Info($"Accent heard: {accent} (in {watch.ElapsedMilliseconds} ms); replies take it");
                    }
                    Speaker.HeardAccent = accent;
                }
            }
            catch (Exception ex)
            {
                // Usually Gemini's free tier being busy: let it be for a while (replies keep the last accent).
                Log.Info($"Couldn't tell the accent ({ex.Message}); not asking again for {AccentRestsFor.TotalMinutes:0} minutes");
                _accentRestsUntil = Environment.TickCount64 + (long)AccentRestsFor.TotalMilliseconds;
            }
        }
    }

    /// <summary>About 30 times a second while it speaks: how loud the voice is, until it's said.</summary>
    private void OnVoiceTick()
    {
        if (!_speaker.IsSpeaking)
        {
            _voiceTimer.Stop();
            _view.PushVoiceLevel(0);
            _view.SetSpeaking(false);
            RaiseChanged(); // the glow: done speaking
            return;
        }

        // The PC's output, which while it answers is its voice (what was playing is paused): -36 dB is
        // quiet, -6 dB about as loud as speech gets.
        float peak = _voiceMeter.Peak();
        float level = peak <= 0 ? 0 : Math.Clamp((20 * MathF.Log10(peak) + 36) / 30, 0, 1);
        _view.PushVoiceLevel(level);
        VoiceLevel?.Invoke(level);
    }

    /// <summary>Working something out with what's shown kept up (an answer to the draft, your words): the orb turns, the glow breathes.</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _view.SetBusy(busy);
        RaiseChanged();
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        _busy = false;
        if (mode is Mode.Off or Mode.Status or Mode.Typing)
        {
            // The conversation's over: what was paused plays again. (Not at an answer, which may be
            // followed up, nor a draft, which is answered first.)
            ResumeMedia();
        }
        if (mode == Mode.Off && _downloadProgress is { } progress)
        {
            SetDownloadProgress(progress); // back to showing the download
        }
        UpdateWakeWord();
        RaiseChanged();
    }

    // ---------------------------------------------------------------- --demo-assistant

    private bool _demoSpeaking; // the demo's made-up answer is being "said"

    /// <summary>
    /// For --demo-assistant: plays the assistant's looks over and over with made-up voices, and no
    /// microphone, speech or brain: listening (the microphone starting, then your words), thinking, an
    /// answer being said, a slip-up.
    /// </summary>
    public async void Demo()
    {
        var random = new Random(7);
        float Voice(double t) => (float)Math.Clamp(0.5 + 0.4 * Math.Sin(t * 11) * Math.Sin(t * 2.7) + (random.NextDouble() - 0.5) * 0.25, 0, 1);
        string[] words = "What's the weather like in Delhi this weekend?".Split(' ');
        const string Reply = "Mostly sunny in Delhi this weekend: highs around 31°C on Saturday, a little cooler on Sunday with a chance of an evening shower.";

        await Task.Delay(1500); // the island's up
        while (true)
        {
            if (!IsEnabled)
            {
                await Task.Delay(1000);
                continue;
            }

            _view.ShowListening(ready: false);
            _shape = ListeningShape;
            SetMode(Mode.Listening);
            await Task.Delay(1300);
            _view.SetListeningReady();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int said = 0;
            while (watch.ElapsedMilliseconds < 4200)
            {
                double t = watch.ElapsedMilliseconds / 1000.0;
                float level = t < 3.6 ? Voice(t) : 0.05f;
                _view.PushLevel(level);
                VoiceLevel?.Invoke(level);
                if (said < words.Length && t > 0.5 + said * 0.38)
                {
                    _liveText = string.Join(' ', words[..++said]);
                    var size = _view.ShowLiveText(_liveText, LiveTextMaxWidth, listening: true);
                    _shape = new PillShape((float)Math.Max(size.Width, Math.Max(_shape.Width, ListeningShape.Width)), (float)Math.Ceiling(size.Height), 18);
                    RaiseChanged();
                }
                await Task.Delay(33);
            }

            var resting = _view.ShowLiveText(_liveText, LiveTextMaxWidth, listening: false);
            _shape = _shape with { Height = (float)Math.Ceiling(resting.Height) };
            SetMode(Mode.Thinking);
            _view.SetBusy(true);
            await Task.Delay(2200);

            _typed = true; // (shown, not said: the demo says it with made-up levels below)
            ShowAnswer(_liveText, Reply, spoken: null);
            _hideTimer.Stop();
            _demoSpeaking = true;
            _view.SetSpeaking(true);
            RaiseChanged();
            watch.Restart();
            while (watch.ElapsedMilliseconds < 4500)
            {
                float level = Voice(watch.ElapsedMilliseconds / 1000.0 + 20);
                _view.PushVoiceLevel(level);
                VoiceLevel?.Invoke(level);
                await Task.Delay(33);
            }
            _demoSpeaking = false;
            _view.PushVoiceLevel(0);
            _view.SetSpeaking(false);
            RaiseChanged();
            await Task.Delay(2500);

            ShowStatus("Sorry, I didn't catch that.", TimeSpan.FromSeconds(3), problem: true);
            _hideTimer.Stop();
            await Task.Delay(2200);

            SetMode(Mode.Off);
            _typed = false;
            await Task.Delay(1800);
        }
    }

    /// <summary>Stops whatever the assistant is doing (it was turned off).</summary>
    private void Cancel()
    {
        if (_mode == Mode.Typing)
        {
            KeyboardWanted?.Invoke(false);
        }
        EndRequest();
        ResumeMedia();
        _confirming = false;
        _followingUp = false;
        _draft = null;
        _changing = null;
        _speaker.Stop();
        _hideTimer.Stop();
        _mode = Mode.Off;
        _busy = false;
        UpdateWakeWord();
    }
}

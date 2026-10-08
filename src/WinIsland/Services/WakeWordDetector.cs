using Microsoft.UI.Dispatching;

namespace WinIsland.Services;

/// <summary>
/// Listens for the wake phrase ("Hey Siro") in a running microphone. A cheap loudness check finds where
/// speech starts after a pause; the first two seconds of it are then checked with Whisper (see
/// <see cref="SpeechToText.CheckWakePhraseAsync"/>). Anything that isn't the wake phrase is dropped on the
/// spot: never kept, logged or sent anywhere. Feed it the microphone's levels from the audio thread;
/// <see cref="Heard"/> is raised on the UI thread.
/// </summary>
internal sealed class WakeWordDetector
{
    private static readonly TimeSpan PauseBefore = TimeSpan.FromMilliseconds(400);   // the phrase starts after a pause
    private static readonly TimeSpan PreRoll = TimeSpan.FromMilliseconds(300);       // check from a little before it was noticed
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);               // "Hey Siro, next song" starts within it
    private static readonly TimeSpan EndsAfter = TimeSpan.FromMilliseconds(400);     // a pause this long ends what was said

    // Loud readings (~30 ms apart, not necessarily in a row) before a stretch is worth checking: a short
    // "hey siro" has a dozen or more, even with its soft consonants; a click or a cough has one or two.
    private const int MinLoudReadings = 5;

    private readonly MicrophoneRecorder _mic;
    private readonly SpeechToText _speechToText;
    private readonly DispatcherQueue _dispatcher;
    private readonly SpeechDetector _speech = new();

    // Audio-thread state, in positions (samples).
    private long _lastLoudAt = long.MinValue / 2;
    private long _candidateFrom = -1; // where the stretch being followed starts (with pre-roll); -1 when none
    private int _candidateLoudReadings;

    private volatile bool _paused;
    private bool _wasPaused = true;

    // One check at a time, with at most one stretch waiting its turn.
    private readonly object _queueLock = new();
    private bool _checking;
    private (long From, long To)? _waiting;

    public WakeWordDetector(MicrophoneRecorder mic, SpeechToText speechToText, DispatcherQueue dispatcher)
    {
        _mic = mic;
        _speechToText = speechToText;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Heard the wake phrase: the position its audio starts at (what you say next follows it), and
    /// whether the request has already started (you said more after it, or you're still talking).
    /// </summary>
    public event Action<long, bool>? Heard;

    /// <summary>Not listening (the assistant is busy with a request). Listening resumes fresh.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>The microphone's latest loudness. Call on the audio thread.</summary>
    public void OnLevel(float rms, long position)
    {
        if (_paused)
        {
            _wasPaused = true;
            return;
        }
        if (_wasPaused)
        {
            _wasPaused = false;
            _speech.Reset();
            _candidateFrom = -1;
        }

        _speech.Update(SpeechDetector.ToDecibels(rms));
        bool loud = _speech.IsLoud;
        if (loud)
        {
            // Sound after a pause starts a stretch to follow; it lasts until the next pause.
            if (_candidateFrom < 0 && position - _lastLoudAt >= _mic.ToSamples(PauseBefore))
            {
                _candidateFrom = position - _mic.ToSamples(PreRoll);
                _candidateLoudReadings = 0;
            }
            _candidateLoudReadings++;
            Volatile.Write(ref _lastLoudAt, position);
        }

        if (_candidateFrom < 0)
        {
            return;
        }

        if (position - _candidateFrom >= _mic.ToSamples(Window))
        {
            // Still talking: check the opening.
            CheckCandidate(_candidateFrom + _mic.ToSamples(Window));
        }
        else if (!loud && position - _lastLoudAt >= _mic.ToSamples(EndsAfter))
        {
            // Stopped talking: check what was said.
            CheckCandidate(_lastLoudAt + _mic.ToSamples(TimeSpan.FromMilliseconds(100)));
        }
    }

    /// <summary>Checks the stretch being followed (up to <paramref name="to"/>) if there was enough sound in it to be speech.</summary>
    private void CheckCandidate(long to)
    {
        long from = _candidateFrom;
        _candidateFrom = -1;
        if (_candidateLoudReadings >= MinLoudReadings)
        {
            Check(from, to);
        }
    }

    /// <summary>Checks a stretch of audio in the background; skipped if the previous check is still running.</summary>
    private void Check(long from, long to)
    {
        lock (_queueLock)
        {
            if (_checking)
            {
                // Busy (with a cough, say): check this next instead of dropping it. Only the latest waits.
                _waiting = (from, to);
                return;
            }
            _checking = true;
        }
        _ = Task.Run(() => CheckInTurnAsync(from, to));
    }

    private async Task CheckInTurnAsync(long from, long to)
    {
        while (true)
        {
            bool heard = false;
            try
            {
                if (await _speechToText.CheckWakePhraseAsync(_mic.Read(from, to)) is { } saidMore)
                {
                    heard = true;
                    bool stillTalking = _mic.Position - Volatile.Read(ref _lastLoudAt) < _mic.ToSamples(EndsAfter);
                    long start = from;
                    _dispatcher.TryEnqueue(() => Heard?.Invoke(start, saidMore || stillTalking));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Checking for the wake phrase failed", ex);
            }

            lock (_queueLock)
            {
                // Once it's heard (or listening paused), what was queued is part of the request, or moot.
                if (_waiting is not { } next || heard || _paused)
                {
                    _waiting = null;
                    _checking = false;
                    return;
                }
                _waiting = null;
                (from, to) = next;
            }
        }
    }
}

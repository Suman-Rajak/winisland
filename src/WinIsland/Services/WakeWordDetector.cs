using Microsoft.UI.Dispatching;

namespace WinIsland.Services;

/// <summary>
/// Listens for the wake phrase ("Hey Glyde") in a running microphone. A cheap loudness check finds where
/// speech starts after a pause; the first two seconds of it are then checked with Whisper (see
/// <see cref="SpeechToText.CheckWakePhraseAsync"/>). Anything that isn't the wake phrase is dropped on the
/// spot: never kept, logged or sent anywhere. Feed it the microphone's levels from the audio thread;
/// <see cref="Heard"/> is raised on the UI thread.
/// </summary>
internal sealed class WakeWordDetector
{
    private static readonly TimeSpan PauseBefore = TimeSpan.FromMilliseconds(400);   // the phrase starts after a pause
    private static readonly TimeSpan PreRoll = TimeSpan.FromMilliseconds(300);       // check from a little before it was noticed
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);               // "Hey Glyde, next song" starts within it
    private static readonly TimeSpan EndsAfter = TimeSpan.FromMilliseconds(400);     // a pause this long ends what was said
    private static readonly TimeSpan TooLate = TimeSpan.FromSeconds(3);              // a check finished this long after: stale

    // Loud readings (~30 ms apart, not necessarily in a row) before a stretch is worth checking: a short
    // "hey glyde" has a dozen or more, even with its soft consonants; a click or a cough has one or two.
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

    // How it's going, logged about once a minute as counts only (never what was said).
    private int _checks, _checksWhilePlaying, _wakes;
    private long _countingSince = Environment.TickCount64;

    public WakeWordDetector(MicrophoneRecorder mic, SpeechToText speechToText, DispatcherQueue dispatcher)
    {
        _mic = mic;
        _speechToText = speechToText;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Heard the wake phrase: the position the request's audio starts at (just after the wake phrase,
    /// unless more was said in the same breath), and whether the request has already started (you said
    /// more after it).
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

    /// <summary>Counts a check (on the checking thread, one at a time), and logs the counts about once a minute.</summary>
    private void Count(bool heard, bool whilePlaying)
    {
        _checks++;
        _checksWhilePlaying += whilePlaying ? 1 : 0;
        _wakes += heard ? 1 : 0;
        if (Environment.TickCount64 - _countingSince >= 60_000)
        {
            Log.Info($"Wake-phrase checks since {DateTime.Now.AddMilliseconds(_countingSince - Environment.TickCount64):HH:mm}: {_checks} " +
                     $"({_checksWhilePlaying} while the PC was playing sound), {_wakes} heard");
            _checks = _checksWhilePlaying = _wakes = 0;
            _countingSince = Environment.TickCount64;
        }
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
                    if (_mic.Position - to > _mic.ToSamples(TooLate))
                    {
                        // Heard long after it was said (the speech model was still getting ready): answering
                        // now would take everything said since as the request. You've likely said it again;
                        // that's checked next.
                        Log.Info("Heard the wake phrase too late to answer; skipped");
                    }
                    else
                    {
                        heard = true;

                        // The request starts after the wake phrase (the request model mishears it, and what
                        // follows, as "take a light"), unless more was said in the same breath. Cut off
                        // while still talking, it starts a moment earlier, so a word cut in two isn't lost.
                        bool endedInPause = to - from < _mic.ToSamples(Window);
                        long start = saidMore ? from : endedInPause ? to : to - _mic.ToSamples(PreRoll);
                        // Still talking without having said more is often the wake phrase again ("hey glyde,
                        // hey glyde"): not the request yet. If it is the request, listening hears it go on.
                        _dispatcher.TryEnqueue(() => Heard?.Invoke(start, saidMore));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Checking for the wake phrase failed", ex);
            }
            Count(heard, _mic.PlayedSince(from));

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

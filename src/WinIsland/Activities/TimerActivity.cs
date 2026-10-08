using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// Timers, alarms and reminders (set by voice; see <see cref="VoiceCommands"/>). A running timer shows
/// its countdown in the pill; everything set is listed in a Timers tab, where × cancels it. When one is
/// due the island holds a ringing view with Stop (and Snooze), plays a sound (a reminder is also read
/// out), and rings for up to a minute.
/// </summary>
internal sealed class TimerActivity : IslandActivity
{
    private const int VisibleRows = 4;
    private const int RowHeight = 42;
    private static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RingFor = TimeSpan.FromMinutes(1);

    private static readonly PillShape CountdownShape = new(240, 34, 17);
    private static readonly PillShape RingingShape = new(390, 58, 26);

    private const string TimerGlyph = "";    // Stopwatch
    private const string AlarmGlyph = "";    // Recent (a clock)
    private const string ReminderGlyph = ""; // Ringer (a bell)

    private static readonly string s_alarmSound = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Alarm01.wav");
    private static readonly string s_chimeSound = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Windows Notify Calendar.wav");

    private readonly TimerService _service;
    private readonly TimerView _compact = new();
    private readonly TimersView _list = new();
    private readonly Speaker _speaker = new();
    private readonly MediaPlayer _sound = new() { AudioCategory = MediaPlayerAudioCategory.Alerts };
    private readonly DispatcherQueueTimer _quietTimer;

    public TimerActivity(DispatcherQueue dispatcher)
    {
        _service = new TimerService(dispatcher);
        _service.Changed += OnServiceChanged;
        _service.Ticked += UpdateCountdowns;
        _service.Due += OnDue;

        _compact.StopClicked += _service.Dismiss;
        _compact.SnoozeClicked += () => _service.Snooze(SnoozeFor);
        _list.CancelClicked += row => _service.Cancel((TimerItem)row.Item);

        _sound.CommandManager.IsEnabled = false; // not "now playing" media
        _quietTimer = dispatcher.CreateTimer();
        _quietTimer.IsRepeating = false;
        _quietTimer.Interval = RingFor;
        _quietTimer.Tick += (_, _) => StopSound();
    }

    public override string Name => "Timers";

    /// <summary>Ringing outranks everything but the assistant; a countdown outranks music.</summary>
    public override int Priority => _service.Ringing is not null ? 105 : 55;

    public override bool WantsCompact => _service.Ringing is not null || _service.Items.Any(i => i.Kind == TimerKind.Timer);

    public override bool HoldsIsland => _service.Ringing is not null;

    public override PillShape CompactShape => _service.Ringing is not null ? RingingShape : CountdownShape;

    public override FrameworkElement CompactView => _compact;

    public override bool IsTab => true;

    public override string Glyph => TimerGlyph;

    public override bool HasExpanded => _service.Items.Count > 0;

    public override PillShape ExpandedShape => new(420, VisibleRows * RowHeight + 12, 32);

    public override FrameworkElement ExpandedView => _list;

    public override Task StartAsync()
    {
        RebuildRows();
        UpdateCompact();
        _service.Start(); // rings anything that came due while WinIsland wasn't running
        return Task.CompletedTask;
    }

    private void OnServiceChanged()
    {
        if (_service.Ringing is null)
        {
            StopSound();
        }
        RebuildRows();
        UpdateCompact();
        RaiseChanged();
    }

    private void OnDue(TimerItem item, bool missed)
    {
        Log.Info($"{item.Kind} due{(missed ? " (missed: WinIsland wasn't running or the PC was asleep)" : "")}");
        UpdateCompact();

        if (missed)
        {
            Play(s_chimeSound, loop: false);
        }
        else if (item.Kind == TimerKind.Reminder)
        {
            Play(s_chimeSound, loop: false);
            _ = SayLaterAsync($"Reminder: {item.Label ?? "it's time"}.");
        }
        else
        {
            Play(s_alarmSound, loop: true);
        }
        RaiseChanged();
    }

    private async Task SayLaterAsync(string text)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.2)); // after the chime
        if (_service.Ringing is not null)
        {
            _speaker.Speak(text);
        }
    }

    private void Play(string path, bool loop)
    {
        if (!File.Exists(path))
        {
            path = s_alarmSound;
        }
        try
        {
            _sound.IsLoopingEnabled = loop;
            _sound.Source = MediaSource.CreateFromUri(new Uri(path));
            _sound.Play();
            _quietTimer.Stop();
            _quietTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Playing the alarm sound failed", ex);
        }
    }

    private void StopSound()
    {
        _quietTimer.Stop();
        _sound.Pause();
        _sound.Source = null;
        _speaker.Stop();
    }

    /// <summary>The pill: what's ringing, else the soonest timer's countdown.</summary>
    private void UpdateCompact()
    {
        var now = DateTime.Now;
        if (_service.Ringing is { } ringing)
        {
            string at = ringing.DueAt.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            bool missed = now - ringing.DueAt > TimeSpan.FromMinutes(1);
            var (glyph, title, subtitle) = ringing.Kind switch
            {
                TimerKind.Timer => (TimerGlyph, "Timer done", ringing.Title),
                TimerKind.Alarm => (AlarmGlyph, "Alarm", at),
                _ => (ReminderGlyph, ringing.Title, $"Reminder · {at}"),
            };
            _compact.ShowRinging(glyph, title, missed ? $"Missed · {at}" : subtitle, canSnooze: ringing.Kind != TimerKind.Timer);
            return;
        }

        var timers = _service.Items.Where(i => i.Kind == TimerKind.Timer).ToList();
        if (timers.Count > 0)
        {
            string more = timers.Count > 1 ? $"  +{timers.Count - 1}" : "";
            _compact.ShowCountdown(timers[0].Title + more, SpokenTime.Countdown(timers[0].DueAt - now));
        }
    }

    private void UpdateCountdowns()
    {
        var now = DateTime.Now;
        foreach (var row in _list.Rows)
        {
            row.Remaining = SpokenTime.Countdown(((TimerItem)row.Item).DueAt - now);
        }
        if (_service.Ringing is null)
        {
            UpdateCompact();
        }
    }

    private void RebuildRows()
    {
        var now = DateTime.Now;
        _list.Rows.Clear();
        foreach (var item in _service.Items)
        {
            string glyph = item.Kind switch { TimerKind.Timer => TimerGlyph, TimerKind.Alarm => AlarmGlyph, _ => ReminderGlyph };
            string when = item.Kind == TimerKind.Timer
                ? $"Ends at {item.DueAt.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture)}"
                : Capitalize(SpokenTime.Describe(item.DueAt, now));
            _list.Rows.Add(new TimerRow(item, glyph, item.Title, when) { Remaining = SpokenTime.Countdown(item.DueAt - now) });
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

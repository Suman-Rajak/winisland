using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;

namespace WinIsland.Services;

internal enum TimerKind
{
    Timer,
    Alarm,
    Reminder,
}

/// <param name="Length">For timers: how long it was set for.</param>
internal sealed record TimerItem(Guid Id, TimerKind Kind, DateTime DueAt, string? Label, TimeSpan? Length)
{
    /// <summary>"10 minute timer", "Alarm", "Call Mom".</summary>
    [JsonIgnore]
    public string Title => Kind switch
    {
        TimerKind.Timer => $"{SpokenTime.Describe(Length ?? TimeSpan.Zero, asAdjective: true)} timer",
        TimerKind.Alarm => "Alarm",
        _ => Label ?? "Reminder",
    };
}

/// <summary>
/// Timers, alarms and reminders: keeps them (in timers.json in the data folder, so they survive a
/// restart), and rings them when they're due. Something due while the PC was off or asleep rings as
/// soon as WinIsland next looks, marked missed. Use on the UI thread; events are raised there.
/// </summary>
internal sealed class TimerService
{
    private static readonly string s_path = Path.Combine(AppPaths.DataFolder, "timers.json");
    private static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly List<TimerItem> _items = [];
    private readonly List<TimerItem> _ringing = [];
    private readonly DispatcherQueueTimer _clock;

    public TimerService(DispatcherQueue dispatcher)
    {
        Current = this;
        _clock = dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => OnClock();
        Load();
    }

    /// <summary>The one WinIsland uses (for voice commands).</summary>
    public static TimerService? Current { get; private set; }

    /// <summary>What's set, soonest first.</summary>
    public IReadOnlyList<TimerItem> Items => _items;

    /// <summary>What's ringing now, if anything (the oldest first).</summary>
    public TimerItem? Ringing => _ringing.FirstOrDefault();

    /// <summary>The list or what's ringing changed.</summary>
    public event Action? Changed;

    /// <summary>Every second while anything is set or ringing (for countdowns).</summary>
    public event Action? Ticked;

    /// <summary>Something came due. True if it came due a while ago (the PC was off or asleep).</summary>
    public event Action<TimerItem, bool>? Due;

    /// <summary>Rings whatever came due while WinIsland wasn't running. Call once the island is up.</summary>
    public void Start() => OnClock();

    public TimerItem Add(TimerKind kind, DateTime dueAt, string? label = null, TimeSpan? length = null)
    {
        var item = new TimerItem(Guid.NewGuid(), kind, dueAt, label, length);
        _items.Add(item);
        OnItemsChanged();
        Log.Info($"Set {kind.ToString().ToLowerInvariant()} for {dueAt:g}");
        return item;
    }

    public void Cancel(TimerItem item)
    {
        if (_items.Remove(item))
        {
            OnItemsChanged();
        }
    }

    /// <summary>Cancels the soonest of a kind, or every one of them. Returns how many were cancelled.</summary>
    public int Cancel(TimerKind kind, bool all)
    {
        var doomed = _items.Where(i => i.Kind == kind).Take(all ? int.MaxValue : 1).ToList();
        foreach (var item in doomed)
        {
            _items.Remove(item);
        }
        if (doomed.Count > 0)
        {
            OnItemsChanged();
        }
        return doomed.Count;
    }

    /// <summary>Stops what's ringing.</summary>
    public void Dismiss()
    {
        if (_ringing.Count > 0)
        {
            _ringing.RemoveAt(0);
            OnItemsChanged();
        }
    }

    /// <summary>Stops what's ringing and rings it again later.</summary>
    public void Snooze(TimeSpan delay)
    {
        if (Ringing is { } item)
        {
            _ringing.RemoveAt(0);
            _items.Add(item with { Id = Guid.NewGuid(), DueAt = DateTime.Now + delay });
            OnItemsChanged();
        }
    }

    private void OnClock()
    {
        var now = DateTime.Now;
        var due = _items.Where(i => i.DueAt <= now).ToList();
        foreach (var item in due)
        {
            _items.Remove(item);
            _ringing.Add(item);
        }
        if (due.Count > 0)
        {
            OnItemsChanged();
            foreach (var item in due)
            {
                Due?.Invoke(item, now - item.DueAt > MissedAfter);
            }
        }

        Ticked?.Invoke();
        UpdateClock();
    }

    private void OnItemsChanged()
    {
        _items.Sort((a, b) => a.DueAt.CompareTo(b.DueAt));
        Save();
        UpdateClock();
        Changed?.Invoke();
    }

    /// <summary>Only ticks while there's something to count down or ring.</summary>
    private void UpdateClock()
    {
        if (_items.Count > 0 || _ringing.Count > 0)
        {
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(s_path))
            {
                _items.AddRange(JsonSerializer.Deserialize<List<TimerItem>>(File.ReadAllText(s_path), s_json) ?? []);
                _items.Sort((a, b) => a.DueAt.CompareTo(b.DueAt));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Timers file unreadable; starting empty", ex);
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(s_path, JsonSerializer.Serialize(_items, s_json));
        }
        catch (Exception ex)
        {
            Log.Error("Saving timers failed", ex);
        }
    }
}

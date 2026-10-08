using System.Globalization;
using System.Text.RegularExpressions;

namespace WinIsland.Services;

/// <param name="Reply">What to show (and say).</param>
/// <param name="Speak">Say it out loud too. Not for music commands: you'd hear it over the song.</param>
internal sealed record CommandResult(string Reply, bool Speak = true);

/// <summary>
/// Understands simple spoken commands and carries them out on the spot, without going online:
/// music ("pause", "next song"), volume ("volume 30", "louder", "mute"), brightness ("brightness 50"),
/// "open Chrome", the time and date, and locking the PC. Anything else gets a polite "not yet".
/// </summary>
internal static partial class VoiceCommands
{
    private const int Step = 10; // "louder", "dimmer"

    public static async Task<CommandResult> RunAsync(string heard)
    {
        string text = Normalize(heard);
        try
        {
            return await RunNormalizedAsync(text, heard);
        }
        catch (Exception ex)
        {
            Log.Error($"Command \"{text}\" failed", ex);
            return new CommandResult("Sorry, that didn't work.");
        }
    }

    private static async Task<CommandResult> RunNormalizedAsync(string text, string heard)
    {
        if (text.Length == 0 || Has(text, @"^(hi|hello|hey|yes|yo)$"))
        {
            return new CommandResult("Hi! Try “next song”, “volume 30” or “open Chrome”.");
        }

        if (Has(text, @"\bwhat can you do\b|^help$"))
        {
            return new CommandResult("I can set timers, alarms and reminders, play, pause and skip music, change the volume and brightness, " +
                                     "open apps, tell the time, and lock your PC.");
        }

        if (Timers(text, heard) is { } timers)
        {
            return timers;
        }

        if (Has(text, @"\b(what|tell me)\b.*\btime\b|\btime is it\b"))
        {
            return new CommandResult($"It's {DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture)}.");
        }

        if (Has(text, @"\b(what|tell me)\b.*\b(date|day)\b|\btoday'?s date\b"))
        {
            return new CommandResult($"It's {DateTime.Now:dddd, d MMMM}.");
        }

        if (Has(text, @"^lock( (the|my))?( (pc|computer|laptop|screen|system))?$"))
        {
            SystemActions.Lock();
            return new CommandResult("Locked.", Speak: false);
        }

        if (OpenTarget(text) is { } target)
        {
            return await OpenAsync(target);
        }

        if (Has(text, @"\b(volume|sound|louder|quieter|softer|mute|unmute)\b") || Has(text, @"\bturn (it|the music) (up|down)\b"))
        {
            return Volume(text);
        }

        if (Has(text, @"\b(brightness|brighter|dimmer|darker)\b") || Has(text, @"\bscreen\b.*\b(bright|dim|dark)"))
        {
            return await BrightnessAsync(text);
        }

        if (MediaCommandIn(text) is { } command)
        {
            return await MediaAsync(command);
        }

        Log.Info($"No command for \"{text}\"");
        return new CommandResult("I can't do that yet.");
    }

    // ---------------------------------------------------------------- timers, alarms and reminders

    /// <summary>
    /// "Timer 10 minutes", "alarm at 7 am", "remind me at 5 to call mom", "remind me in 20 minutes to
    /// check the oven", "how much time is left", "what are my reminders", "cancel the alarm", and while
    /// something rings, "stop" or "snooze". Null if it isn't about timers.
    /// </summary>
    /// <param name="heard">As said, for the capitals in a reminder ("call Rahul").</param>
    private static CommandResult? Timers(string text, string heard)
    {
        if (TimerService.Current is not { } timers)
        {
            return null;
        }

        string digits = SpokenTime.WithDigits(text);
        var now = DateTime.Now;

        if (timers.Ringing is not null)
        {
            if (Has(text, @"^(stop|ok|okay|dismiss|turn it off|shut up|silence|enough|got it|thanks|thank you)( (it|that|the alarm|the timer|the reminder|alarm|timer))?$"))
            {
                timers.Dismiss();
                return new CommandResult("OK.", Speak: false);
            }
            if (Has(text, @"\bsnooze\b"))
            {
                var delay = SpokenTime.Duration(digits) ?? TimeSpan.FromMinutes(5);
                timers.Snooze(delay);
                return new CommandResult($"Snoozed for {SpokenTime.Describe(delay)}.");
            }
        }

        if (Has(text, @"\b(cancel|stop|delete|remove|clear|turn off|switch off)\b") && KindIn(text) is { } cancelKind)
        {
            bool all = Has(text, @"\b(all|every|timers|alarms|reminders)\b");
            int cancelled = timers.Cancel(cancelKind, all);
            string what = cancelKind.ToString().ToLowerInvariant();
            return new CommandResult(cancelled switch
            {
                0 => $"There's no {what} to cancel.",
                1 => $"Cancelled the {what}.",
                _ => $"Cancelled {cancelled} {what}s.",
            });
        }

        if (Has(text, @"\b(how (much|long)|time)\b.*\b(left|remaining)\b"))
        {
            var next = timers.Items.FirstOrDefault(i => i.Kind == TimerKind.Timer) ?? timers.Items.FirstOrDefault();
            return new CommandResult(next is null
                ? "Nothing's set."
                : $"{SpokenTime.Describe(TimeSpan.FromSeconds(Math.Round((next.DueAt - now).TotalSeconds)))} left on the {next.Title.ToLowerInvariant()}.");
        }

        if (Has(text, @"\b(what|when|which|any|list|show)\b.*\b(timers?|alarms?|reminders?)\b"))
        {
            var kind = KindIn(text);
            var set = timers.Items.Where(i => kind is null || i.Kind == kind).ToList();
            if (set.Count == 0)
            {
                return new CommandResult($"No {(kind?.ToString().ToLowerInvariant() ?? "timer")}s are set.");
            }
            var said = set.Take(3).Select(i => i.Kind == TimerKind.Timer
                ? $"{i.Title}, {SpokenTime.Describe(TimeSpan.FromSeconds(Math.Round((i.DueAt - now).TotalSeconds)))} left"
                : $"{i.Title} {SpokenTime.Describe(i.DueAt, now)}");
            return new CommandResult(string.Join("; ", said) + (set.Count > 3 ? $"; and {set.Count - 3} more." : "."));
        }

        if (Has(text, @"\b(timer|countdown|count down)\b"))
        {
            if (SpokenTime.Duration(digits) is not { } length)
            {
                return new CommandResult("How long? Say something like “timer 10 minutes”.");
            }
            timers.Add(TimerKind.Timer, now + length, length: length);
            return new CommandResult($"Timer set for {SpokenTime.Describe(length)}.");
        }

        if (Has(text, @"\b(alarm|wake me)\b"))
        {
            var at = SpokenTime.TimeOfDay(digits, now, morning: true) ?? (SpokenTime.Duration(digits) is { } after ? now + after : null);
            if (at is null)
            {
                return new CommandResult("For what time? Say something like “alarm at 7 am”.");
            }
            timers.Add(TimerKind.Alarm, at.Value);
            return new CommandResult($"Alarm set {SpokenTime.Describe(at.Value, now)}.");
        }

        if (Has(text, @"\b(remind|reminder)\b"))
        {
            // "In 20 minutes" counts from now; otherwise a time of day ("at 5", "tomorrow").
            bool relative = Has(digits, @"\b(in|after)\s+\d");
            var at = relative && SpokenTime.Duration(digits) is { } after
                ? now + after
                : SpokenTime.TimeOfDay(digits, now) ?? (SpokenTime.Duration(digits) is { } fromNow ? now + fromNow : null);
            if (at is null)
            {
                return new CommandResult("When should I remind you? Say something like “remind me at 5 to call Mom”.");
            }

            string label = RestoreCase(ReminderText(digits), heard);
            timers.Add(TimerKind.Reminder, at.Value, label);
            string when = relative
                ? $"in {SpokenTime.Describe(TimeSpan.FromSeconds(Math.Round((at.Value - now).TotalSeconds)))}"
                : SpokenTime.Describe(at.Value, now);
            return new CommandResult($"OK, I'll remind you {when}: {label}.");
        }

        return null;
    }

    private static TimerKind? KindIn(string text) =>
        Has(text, @"\btimers?\b") ? TimerKind.Timer
        : Has(text, @"\balarms?\b") ? TimerKind.Alarm
        : Has(text, @"\breminders?\b") ? TimerKind.Reminder
        : null;

    /// <summary>"remind me at 5 to call mom" → "Call mom".</summary>
    private static string ReminderText(string digits)
    {
        string text = Regex.Replace(digits, @"^(set |create |add )?(a )?(remind me|reminder)( please)?\b", "");
        text = SpokenTime.Strip(text);
        text = Regex.Replace(text, @"^(to|that|about|of|for)\s+", "");
        text = Regex.Replace(text, @"\s+please$", "").Trim();
        return text.Length == 0 ? "Reminder" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Gives words back the capitals they had as said: "Call rahul" + "…call Rahul." → "Call Rahul".</summary>
    private static string RestoreCase(string text, string heard)
    {
        var said = Regex.Matches(heard, @"[\p{L}\p{N}']+").Select(m => m.Value)
            .GroupBy(w => w.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var words = text.Split(' ').Select(w =>
            said.TryGetValue(w.ToLowerInvariant(), out var original) && original != original.ToLowerInvariant() ? original : w);
        return string.Join(' ', words);
    }

    // ---------------------------------------------------------------- music

    private static MediaCommand? MediaCommandIn(string text)
    {
        // Only short phrases: "pause", "next song", "skip this track", "play the music".
        if (text.Split(' ').Length > 5)
        {
            return null;
        }
        if (Has(text, @"\b(next|skip)\b"))
        {
            return MediaCommand.Next;
        }
        if (Has(text, @"\b(previous|prev|last (song|track|one)|go back)\b"))
        {
            return MediaCommand.Previous;
        }
        if (Has(text, @"^(pause|stop)( (the|this))?( (music|song|track|video|it|playback))?$"))
        {
            return MediaCommand.Pause;
        }
        if (Has(text, @"^(play|resume|continue|unpause|start)( (the|this))?( (music|song|track|video|it|again))?$"))
        {
            return MediaCommand.Play; // "play despacito" is a search: later
        }
        return null;
    }

    private static async Task<CommandResult> MediaAsync(MediaCommand command)
    {
        if (!await SystemActions.SendMediaCommandAsync(command))
        {
            return new CommandResult(command == MediaCommand.Pause ? "Nothing's playing." : "There's no music to control.");
        }

        return new CommandResult(command switch
        {
            MediaCommand.Play => "Playing.",
            MediaCommand.Pause => "Paused.",
            MediaCommand.Next => "Next track.",
            _ => "Previous track.",
        }, Speak: false);
    }

    // ---------------------------------------------------------------- volume and brightness

    private static CommandResult Volume(string text)
    {
        if (Has(text, @"\bunmute\b"))
        {
            return SystemActions.SetMute(false) ? new CommandResult("Unmuted.") : NoSpeakers();
        }
        if (Has(text, @"\bmute\b"))
        {
            return SystemActions.SetMute(true) ? new CommandResult("Muted.", Speak: false) : NoSpeakers();
        }

        if (SystemActions.GetVolume() is not { } current)
        {
            return NoSpeakers();
        }

        int now = (int)Math.Round(current * 100);
        int? target = NumberIn(text) ?? (
            Has(text, @"\b(max|maximum|full)\b") ? 100
            : Has(text, @"\b(up|increase|raise|louder|higher|more)\b") ? now + Step
            : Has(text, @"\b(down|decrease|lower|reduce|quieter|softer|less)\b") ? now - Step
            : null);

        if (target is null)
        {
            return new CommandResult($"The volume is {now}%.");
        }

        int level = Math.Clamp(target.Value, 0, 100);
        SystemActions.SetVolume(level / 100f);
        return new CommandResult($"Volume {level}%.");
    }

    private static CommandResult NoSpeakers() => new("There are no speakers to change.");

    private static async Task<CommandResult> BrightnessAsync(string text)
    {
        if (await SystemActions.GetBrightnessAsync() is not { } now)
        {
            return new CommandResult("I can't change this screen's brightness.");
        }

        int? target = NumberIn(text) ?? (
            Has(text, @"\b(max|maximum|full)\b") ? 100
            : Has(text, @"\b(up|increase|raise|brighter|higher|more)\b") ? now + Step
            : Has(text, @"\b(down|decrease|lower|reduce|dim|dimmer|darker|less)\b") ? now - Step
            : null);

        if (target is null)
        {
            return new CommandResult($"Brightness is {now}%.");
        }

        int level = Math.Clamp(target.Value, 0, 100);
        return await SystemActions.SetBrightnessAsync(level)
            ? new CommandResult($"Brightness {level}%.")
            : new CommandResult("I can't change this screen's brightness.");
    }

    // ---------------------------------------------------------------- apps

    /// <summary>"open google chrome" → "google chrome"; "launch the calculator app" → "calculator".</summary>
    private static string? OpenTarget(string text)
    {
        var match = OpenPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        string target = Regex.Replace(match.Groups["app"].Value, @"^(the|my|up)\s+|\s+(app|application|program|for me|please)$", "").Trim();
        return target.Length > 0 ? target : null;
    }

    private static async Task<CommandResult> OpenAsync(string target)
    {
        return await AppCatalog.OpenAsync(target) is { } opened
            ? new CommandResult($"Opening {opened}.")
            : new CommandResult($"I couldn't find an app called {target}.");
    }

    // ---------------------------------------------------------------- understanding

    /// <summary>
    /// Lower-case, punctuation gone, the wake phrase and politeness stripped:
    /// "Hey Siro, could you please open Chrome?" → "open chrome".
    /// </summary>
    private static string Normalize(string text)
    {
        text = text.ToLowerInvariant().Replace("%", " percent ").Replace("’", "'");
        text = Regex.Replace(text, @"[^\p{L}\p{N}' ]+", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        text = s_wakePattern.Replace(text, "").Trim();
        text = Regex.Replace(text, @"^(please |can you |could you |would you |will you |i want you to |i want to |i'd like to |let's )+", "");
        text = Regex.Replace(text, @"( please| for me| now)+$", "");
        return text.Trim();
    }

    /// <summary>"volume 30", "set it to thirty percent", "volume to a hundred".</summary>
    private static int? NumberIn(string text)
    {
        var digits = Regex.Match(text, @"\b(\d{1,3})\b");
        if (digits.Success)
        {
            return int.Parse(digits.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        int? total = null;
        foreach (string word in text.Split(' '))
        {
            if (s_numberWords.TryGetValue(word, out int value))
            {
                total = word == "hundred" ? Math.Max(1, total ?? 1) * 100 : (total ?? 0) + value;
            }
            else if (total is not null && word != "and")
            {
                break; // "twenty five percent": stop at "percent"
            }
        }
        return total;
    }

    private static readonly Dictionary<string, int> s_numberWords = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
        ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["hundred"] = 100, ["half"] = 50,
    };

    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern);

    /// <summary>
    /// "hey siro", "siro", "ok zero" at the start (a real word like "zero" only after a greeting). "Hey" is
    /// sometimes heard as "it's", "say" or "a".
    /// </summary>
    private static readonly Regex s_wakePattern = new(
        $@"^((({Greetings})\s+)?({string.Join("|", [Assistant.Name.ToLowerInvariant(), .. Assistant.SoundsLike])})" +
        $@"|({Greetings})\s+({string.Join("|", Assistant.AlsoHeardAs)}))\b\s*");

    private const string Greetings = "hey|hi|high|okay|ok|hello|it's|its|say|a";

    // Not "start" or "run": "start the music" isn't opening an app called Music.
    [GeneratedRegex(@"^(open|launch)\s+(?<app>.+)$")]
    private static partial Regex OpenPattern();
}

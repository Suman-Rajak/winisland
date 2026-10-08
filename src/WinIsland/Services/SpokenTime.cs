using System.Globalization;
using System.Text.RegularExpressions;

namespace WinIsland.Services;

/// <summary>
/// Durations and times of day in spoken English, as speech recognition writes it once lower-cased and
/// stripped of punctuation: "ten minutes", "an hour and a half", "5 30 p m", "tomorrow at 7", "noon".
/// </summary>
internal static partial class SpokenTime
{
    public static readonly Dictionary<string, int> NumberWords = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
        ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["hundred"] = 100,
    };

    private static readonly string[] s_weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>"twenty five minutes" → "25 minutes"; "a minute" → "1 minute"; "half an hour" → "30 minutes".</summary>
    public static string WithDigits(string text)
    {
        text = HalfHour().Replace(text, "30 minutes");

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var output = new List<string>();
        for (int i = 0; i < words.Length;)
        {
            if (words[i] is "a" or "an" && i + 1 < words.Length && IsUnit(words[i + 1]))
            {
                output.Add("1");
                i++;
                continue;
            }
            if (!NumberWords.ContainsKey(words[i]))
            {
                output.Add(words[i++]);
                continue;
            }

            // A run of number words: "twenty five", "one hundred and five".
            int value = 0;
            while (i < words.Length)
            {
                if (words[i] == "and" && i + 1 < words.Length && NumberWords.ContainsKey(words[i + 1]))
                {
                    i++;
                    continue;
                }
                if (!NumberWords.TryGetValue(words[i], out int word))
                {
                    break;
                }
                value = word == 100 ? Math.Max(1, value) * 100 : value + word;
                i++;
            }
            output.Add(value.ToString(CultureInfo.InvariantCulture));
        }

        // "1 hour and a half" (only once "an hour" has become "1 hour").
        return AndAHalf().Replace(string.Join(' ', output), m => m.Groups["unit"].Value.StartsWith('h')
            ? $"{m.Groups["n"].Value} hours 30 minutes"
            : $"{m.Groups["n"].Value} minutes 30 seconds");
    }

    /// <summary>The total of every "N unit" in digit text: "1 hours 30 minutes" → 1:30. Null if there's none.</summary>
    public static TimeSpan? Duration(string text)
    {
        TimeSpan total = TimeSpan.Zero;
        bool found = false;
        foreach (Match match in DurationPart().Matches(text))
        {
            double n = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            total += match.Groups["unit"].Value[0] switch
            {
                'h' => TimeSpan.FromHours(n),
                'm' => TimeSpan.FromMinutes(n),
                _ => TimeSpan.FromSeconds(n),
            };
            found = true;
        }
        return found && total > TimeSpan.Zero ? total : null;
    }

    /// <summary>
    /// The next moment matching the time of day (and day) mentioned in digit text: "at 5" (whichever of
    /// 5 am / 5 pm comes first), "7 30 am", "noon", "tomorrow at 9", "on friday at 6". A day with no time
    /// means 9 am. An hour without am/pm on another day is read as daytime ("tomorrow at 5" is 5 pm), or
    /// with <paramref name="morning"/> (alarms) as morning. Null if no time or day is mentioned.
    /// </summary>
    public static DateTime? TimeOfDay(string text, DateTime now, bool morning = false)
    {
        DateTime? day = DayMentioned(text, now);

        int hour, minute = 0;
        bool definite;
        if (Noon().IsMatch(text))
        {
            (hour, definite) = (12, true);
        }
        else if (Midnight().IsMatch(text))
        {
            (hour, definite) = (0, true);
        }
        else if (ClockTime().Match(text) is { Success: true } clock)
        {
            hour = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture);
            minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            string meridiem = clock.Groups["ampm"].Value.Replace(" ", "");
            definite = meridiem.Length > 0 || hour > 12 || hour == 0;
            if (meridiem == "pm" && hour < 12)
            {
                hour += 12;
            }
            else if (meridiem == "am" && hour == 12)
            {
                hour = 0;
            }
        }
        else
        {
            return day?.AddHours(9);
        }

        if (hour > 23 || minute > 59)
        {
            return null;
        }

        // An hour without am/pm on a day other than today: "tomorrow at 7" is 7 am and "tomorrow at 5"
        // 5 pm (the daytime one), unless it's an alarm; "tonight at 9" is 9 pm.
        int OnAnotherDay(bool tonight) => !definite && hour < 12 && (tonight || (!morning && hour < 7)) ? hour + 12 : hour;

        if (day is { } on)
        {
            return on.AddHours(OnAnotherDay(Tonight().IsMatch(text))).AddMinutes(minute);
        }

        // No day: the first matching moment from now on.
        var today = now.Date;
        var candidates = definite
            ? new[] { today.AddHours(hour), today.AddDays(1).AddHours(hour) }
            : new[] { today.AddHours(hour), today.AddHours(hour + 12), today.AddDays(1).AddHours(OnAnotherDay(false)) };
        return candidates.Select(t => t.AddMinutes(minute)).First(t => t > now);
    }

    /// <summary>Removes the duration, time and day phrases (for what's left: a reminder's text).</summary>
    public static string Strip(string text)
    {
        text = RelativePhrase().Replace(text, " ");
        text = DayPhrase().Replace(text, " ");
        text = ClockPhrase().Replace(text, " ");
        text = Noon().Replace(text, " ");
        text = Midnight().Replace(text, " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>"in 10 minutes", "at 5:30 PM", "tomorrow at 7:00 AM", "on Friday at 6:00 PM".</summary>
    public static string Describe(DateTime when, DateTime now)
    {
        string time = when.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return (when.Date - now.Date).Days switch
        {
            0 => $"at {time}",
            1 => $"tomorrow at {time}",
            < 7 => $"on {when:dddd} at {time}",
            _ => $"on {when:ddd d MMM} at {time}",
        };
    }

    /// <summary>"10 minutes", "1 hour 30 minutes", "45 seconds"; with <paramref name="asAdjective"/>, "10 minute" (as in "10 minute timer").</summary>
    public static string Describe(TimeSpan length, bool asAdjective = false)
    {
        string Part(int n, string unit) => $"{n} {unit}{(n == 1 || asAdjective ? "" : "s")}";
        var parts = new List<string>();
        if (length.Hours + length.Days * 24 is int hours and > 0)
        {
            parts.Add(Part(hours, "hour"));
        }
        if (length.Minutes > 0)
        {
            parts.Add(Part(length.Minutes, "minute"));
        }
        if (length.Seconds > 0 || parts.Count == 0)
        {
            parts.Add(Part(length.Seconds, "second"));
        }
        return string.Join(" ", parts);
    }

    /// <summary>A countdown: "4:59", "1:02:30".</summary>
    public static string Countdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }
        left = TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));
        return left.TotalHours >= 1
            ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
            : $"{left.Minutes}:{left.Seconds:00}";
    }

    private static DateTime? DayMentioned(string text, DateTime now)
    {
        if (DayAfterTomorrow().IsMatch(text))
        {
            return now.Date.AddDays(2);
        }
        if (Tomorrow().IsMatch(text))
        {
            return now.Date.AddDays(1);
        }
        if (Today().IsMatch(text))
        {
            return now.Date;
        }
        for (int i = 0; i < 7; i++)
        {
            if (Regex.IsMatch(text, $@"\b{s_weekdays[i]}\b"))
            {
                int ahead = (i - (int)now.DayOfWeek + 7) % 7;
                return now.Date.AddDays(ahead == 0 ? 7 : ahead);
            }
        }
        return null;
    }

    private static bool IsUnit(string word) => UnitWord().IsMatch(word);

    [GeneratedRegex(@"\bhalf an hour\b")]
    private static partial Regex HalfHour();

    [GeneratedRegex(@"\b(?<n>\d+) (?<unit>hours?|minutes?) and a half\b")]
    private static partial Regex AndAHalf();

    [GeneratedRegex(@"^(hours?|hrs?|minutes?|mins?|seconds?|secs?)$")]
    private static partial Regex UnitWord();

    [GeneratedRegex(@"\b(?<n>\d+(?:\.\d+)?) ?(?<unit>hours?|hrs?|minutes?|mins?|seconds?|secs?)\b")]
    private static partial Regex DurationPart();

    /// <summary>"at 5", "at 5 30", "7 am", "7 30 p m", "6 o'clock", "for 7 am".</summary>
    [GeneratedRegex(@"(?:\b(?:at|by|for)\s+(?<h>\d{1,2})(?:\s(?<m>\d{2}))?(?:\s*(?<ampm>a ?m|p ?m)\b|\s*o'?clock\b)?(?!\s*(?:\d|hours?|hrs?|minutes?|mins?|seconds?|secs?)\b)" +
                    @"|\b(?<h>\d{1,2})(?:\s(?<m>\d{2}))?\s*(?:(?<ampm>a ?m|p ?m)\b|o'?clock\b))")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"(?:\b(?:at|by)\s+\d{1,2}(?:\s\d{2})?(?:\s*(?:a ?m|p ?m)\b|\s*o'?clock\b)?(?!\s*(?:\d|hours?|hrs?|minutes?|mins?|seconds?|secs?)\b)" +
                    @"|\b(?:for\s+)?\d{1,2}(?:\s\d{2})?\s*(?:(?:a ?m|p ?m)\b|o'?clock\b))")]
    private static partial Regex ClockPhrase();

    [GeneratedRegex(@"\b(?:in|for|after)\s+(?:\d+(?:\.\d+)? ?(?:hours?|hrs?|minutes?|mins?|seconds?|secs?)\s*(?:and\s+)?)+")]
    private static partial Regex RelativePhrase();

    [GeneratedRegex(@"\b(?:on\s+)?(?:the\s+)?(?:day after tomorrow|tomorrow|today|tonight|sunday|monday|tuesday|wednesday|thursday|friday|saturday)\b")]
    private static partial Regex DayPhrase();

    [GeneratedRegex(@"\b(?:at\s+)?(?:noon|midday)\b")]
    private static partial Regex Noon();

    [GeneratedRegex(@"\b(?:at\s+)?midnight\b")]
    private static partial Regex Midnight();

    [GeneratedRegex(@"\bday after tomorrow\b")]
    private static partial Regex DayAfterTomorrow();

    [GeneratedRegex(@"\btomorrow\b")]
    private static partial Regex Tomorrow();

    [GeneratedRegex(@"\b(?:today|tonight)\b")]
    private static partial Regex Today();

    [GeneratedRegex(@"\btonight\b")]
    private static partial Regex Tonight();
}

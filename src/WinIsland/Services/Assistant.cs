namespace WinIsland.Services;

/// <summary>The voice assistant's name, in one place so renaming it is a one-line change (plus how it gets misheard).</summary>
internal static class Assistant
{
    public const string Name = "Glyde";

    public static string WakePhrase => $"Hey {Name}";

    /// <summary>How speech recognition writes the name when it's expecting it (lower case).</summary>
    public static readonly string[] SoundsLike = ["glide", "glyd", "glydde", "glidde", "glied", "glyed", "glaid", "glyde"];

    /// <summary>
    /// Real words it sometimes hears instead. Taken as the name only at the start of a request, after a
    /// greeting ("hey clyde, next song"), never on their own.
    /// </summary>
    public static readonly string[] AlsoHeardAs = ["clyde", "guide", "glade"];

    /// <summary>"Hey Glyde, what's the capital of France?" → "what's the capital of France?" (as said, for the AI brain).</summary>
    public static string WithoutWakePhrase(string heard) => System.Text.RegularExpressions.Regex.Replace(heard,
        $@"^\W*((hey|hi|high|hello|ok|okay|it's|say|a)\W+)?({string.Join("|", [Name.ToLowerInvariant(), .. SoundsLike, .. AlsoHeardAs])})\b\W*",
        "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim() is { Length: > 0 } rest ? rest : heard;
}

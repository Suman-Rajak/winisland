namespace WinIsland.Services;

/// <summary>The voice assistant's name, in one place so renaming it is a one-line change (plus how it gets misheard).</summary>
internal static class Assistant
{
    public const string Name = "Siro";

    public static string WakePhrase => $"Hey {Name}";

    /// <summary>How speech recognition writes the name when it's expecting it (lower case).</summary>
    public static readonly string[] SoundsLike = ["sirow", "sero", "cero", "ciro", "cyro", "syro", "sirro", "sheero", "shiro"];

    /// <summary>
    /// Real words it sometimes hears instead. Taken as the name only at the start of a request, after a
    /// greeting ("hey zero, next song"), never on their own.
    /// </summary>
    public static readonly string[] AlsoHeardAs = ["zero", "0", "sorrow", "sirio", "cirro", "siri"];
}

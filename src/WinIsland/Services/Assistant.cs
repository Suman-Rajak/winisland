namespace WinIsland.Services;

/// <summary>The voice assistant's name, in one place so renaming it is a one-line change (plus how it gets misheard).</summary>
internal static class Assistant
{
    public const string Name = "Siro";

    public static string WakePhrase => $"Hey {Name}";

    /// <summary>How speech recognition sometimes writes the name.</summary>
    public static readonly string[] SoundsLike = ["siri", "sirow", "sero", "cero", "ciro", "cyro", "syro", "sirro", "sheero", "shiro"];
}

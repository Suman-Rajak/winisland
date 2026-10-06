using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinIsland.Services;

/// <summary>
/// A track's title and artists, cleaned up for searching and comparing. Music apps decorate titles
/// ("Tum Hi Ho (From "Aashiqui 2")", "MONTAGEM ALQUIMIA [Explicit]") and pack several artists into
/// one string ("h6itam, n7san7os &amp; Mc menor do alvorada"), which breaks catalogue searches.
/// </summary>
internal sealed partial record TrackQuery(string Title, IReadOnlyList<string> Artists)
{
    public string PrimaryArtist => Artists.Count > 0 ? Artists[0] : string.Empty;

    public static TrackQuery From(string artist, string title) =>
        new(CleanTitle(title), [.. ArtistSeparators().Split(artist).Select(a => a.Trim()).Where(a => a.Length > 0)]);

    /// <summary>
    /// True when a catalogue result is really this track: same title (ignoring decorations) and
    /// at least one artist in common. Title-only matches find other artists' songs with the same
    /// name, which is worse than showing no artwork.
    /// </summary>
    public bool Matches(string candidateArtist, string candidateTitle)
    {
        string wanted = Normalize(Title), found = Normalize(CleanTitle(candidateTitle));
        if (wanted.Length == 0 || found.Length == 0 ||
            !(wanted == found || found.StartsWith(wanted + " ") || wanted.StartsWith(found + " ")))
        {
            return false;
        }

        string foundArtist = Normalize(candidateArtist);
        return Artists.Select(Normalize).Any(a => a.Length > 0 && (foundArtist.Contains(a) || a.Contains(foundArtist)));
    }

    /// <summary>Drops bracketed decorations and "feat."/"- Remastered" style suffixes.</summary>
    public static string CleanTitle(string title)
    {
        string cleaned = Brackets().Replace(title, " ");
        cleaned = DashSuffix().Replace(cleaned, string.Empty);
        cleaned = Featuring().Replace(cleaned, string.Empty);
        return Whitespace().Replace(cleaned, " ").Trim();
    }

    /// <summary>Lowercase letters and digits only, accents removed, single spaces.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+-\s+(remaster|from |live|radio edit|single version|feat).*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashSuffix();

    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s*(?:,|&|;|/|\s+x\s+|\s+feat\.?\s+|\s+ft\.?\s+|\s+featuring\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

using System.Globalization;
using System.Text.Json;

namespace WinIsland.Services;

/// <summary>
/// Online music catalogues, for apps that publish poor metadata (Amazon Music shares no artwork,
/// and sometimes repeats one value as both title and artist). Uses the iTunes catalogue (your
/// country's store, then the US store) and MusicBrainz + the Cover Art Archive, which have many
/// independent releases iTunes lacks. Sends artist and title to Apple and MusicBrainz, so only
/// call this when <see cref="AppSettings.FindArtworkOnline"/> is on.
/// </summary>
internal static class MusicCatalog
{
    private static readonly HttpClient s_http = CreateClient();
    private static readonly Dictionary<string, byte[]?> s_artworkCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string Title, string Artist)> s_identified = new(StringComparer.OrdinalIgnoreCase);

    // MusicBrainz allows one request per second per client.
    private static readonly SemaphoreSlim s_musicBrainzGate = new(1, 1);
    private static DateTime s_lastMusicBrainzRequest;

    private static string Country => RegionInfo.CurrentRegion.TwoLetterISORegionName;

    /// <summary>Cover art for the track. Only results whose artist and title both match are accepted.</summary>
    public static async Task<byte[]?> FindArtworkAsync(string artist, string title)
    {
        var track = TrackQuery.From(artist, title);
        if (track.Title.Length == 0 || track.PrimaryArtist.Length == 0)
        {
            return null;
        }

        string key = $"{track.PrimaryArtist}|{track.Title}";
        if (s_artworkCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var sources = new List<(string Name, Func<Task<byte[]?>> Lookup)>
        {
            ("iTunes " + Country, () => ArtworkFromITunesAsync(track, Country)),
        };
        if (Country != "US")
        {
            sources.Add(("iTunes US", () => ArtworkFromITunesAsync(track, "US")));
        }
        sources.Add(("MusicBrainz", () => ArtworkFromMusicBrainzAsync(track)));

        byte[]? result = null;
        bool anyFailed = false;
        string from = "nowhere";
        foreach (var (name, lookup) in sources)
        {
            try
            {
                if ((result = await lookup()) is not null)
                {
                    from = name;
                    break;
                }
            }
            catch (Exception ex)
            {
                anyFailed = true;
                Log.Error($"Artwork lookup via {name} failed", ex);
            }
        }

        Log.Info($"Artwork for '{track.PrimaryArtist} - {track.Title}': {from}");

        // Remember "not found", but not network errors: those get another try next time the song plays.
        if (result is not null || !anyFailed)
        {
            s_artworkCache[key] = result;
        }
        return result;
    }

    /// <summary>A previous <see cref="IdentifyAsync"/> result, if there is one.</summary>
    public static bool TryGetIdentified(string value, out (string Title, string Artist) track) =>
        s_identified.TryGetValue(value, out track);

    /// <summary>
    /// Is this value a song title, and if so, whose song? If the catalogue has songs with exactly
    /// that title, it's a title, and the artist comes from the catalogue when every such song is by
    /// the same artist ("Pooja &amp; Patola" → Namr Gill); otherwise the artist is left blank rather
    /// than guessed. If there's no such song (or only one titled after its own artist), the value
    /// is taken to be an artist ("Sayfalse"): Title is empty and Artist is the value.
    /// Used when an app reports one value as both title and artist, or a title with no artist.
    /// </summary>
    public static async Task<(string Title, string Artist)> IdentifyAsync(string value)
    {
        if (s_identified.TryGetValue(value, out var known))
        {
            return known;
        }

        (string Title, string Artist) result = (string.Empty, value);
        try
        {
            string cleaned = TrackQuery.CleanTitle(value);
            string term = Uri.EscapeDataString(cleaned);
            using var json = JsonDocument.Parse(await s_http.GetStringAsync(
                $"https://itunes.apple.com/search?media=music&entity=song&limit=25&country={Country}&term={term}"));

            string wanted = TrackQuery.Normalize(cleaned);
            var exact = json.RootElement.GetProperty("results").EnumerateArray()
                .Where(hit => TrackQuery.Normalize(TrackQuery.CleanTitle(GetString(hit, "trackName"))) == wanted)
                .Select(hit => GetString(hit, "artistName"))
                .ToList();

            // A song titled after its own artist (a self-titled track) still means "this is the artist".
            bool selfTitled = exact.Any(artist => TrackQuery.Normalize(artist).Contains(wanted));
            if (exact.Count > 0 && !selfTitled)
            {
                var artists = exact.Select(a => TrackQuery.From(a, value).PrimaryArtist).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                result = (value, artists.Count == 1 ? exact[0] : string.Empty);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Identifying '{value}' failed", ex);
            return result; // don't cache: try again next time
        }

        Log.Info($"'{value}' identified as title='{result.Title}' artist='{result.Artist}'");
        s_identified[value] = result;
        return result;
    }

    private static async Task<byte[]?> ArtworkFromITunesAsync(TrackQuery track, string country)
    {
        string term = Uri.EscapeDataString($"{track.PrimaryArtist} {track.Title}");
        using var json = JsonDocument.Parse(await s_http.GetStringAsync(
            $"https://itunes.apple.com/search?media=music&entity=song&limit=10&country={country}&term={term}"));

        foreach (var hit in json.RootElement.GetProperty("results").EnumerateArray())
        {
            if (track.Matches(GetString(hit, "artistName"), GetString(hit, "trackName")) &&
                GetString(hit, "artworkUrl100") is { Length: > 0 } url)
            {
                return await s_http.GetByteArrayAsync(url.Replace("100x100bb", "600x600bb"));
            }
        }

        return null;
    }

    private static async Task<byte[]?> ArtworkFromMusicBrainzAsync(TrackQuery track)
    {
        // Lucene query; quotes inside values would break it.
        string query = $"recording:\"{track.Title.Replace("\"", "")}\" AND artist:\"{track.PrimaryArtist.Replace("\"", "")}\"";

        JsonDocument json;
        await s_musicBrainzGate.WaitAsync();
        try
        {
            var wait = s_lastMusicBrainzRequest.AddSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }
            s_lastMusicBrainzRequest = DateTime.UtcNow;
            json = JsonDocument.Parse(await s_http.GetStringAsync(
                $"https://musicbrainz.org/ws/2/recording?fmt=json&limit=10&query={Uri.EscapeDataString(query)}"));
        }
        finally
        {
            s_musicBrainzGate.Release();
        }

        using (json)
        {
            int attempts = 0;
            foreach (var recording in json.RootElement.GetProperty("recordings").EnumerateArray())
            {
                string artists = string.Join(", ", recording.TryGetProperty("artist-credit", out var credits)
                    ? credits.EnumerateArray().Select(c => GetString(c, "name"))
                    : []);
                if (!track.Matches(artists, GetString(recording, "title")) || !recording.TryGetProperty("releases", out var releases))
                {
                    continue;
                }

                // Not every release has artwork in the archive; try a few.
                foreach (var release in releases.EnumerateArray())
                {
                    if (++attempts > 4)
                    {
                        return null;
                    }

                    using var response = await s_http.GetAsync($"https://coverartarchive.org/release/{GetString(release, "id")}/front-500");
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsByteArrayAsync();
                    }
                }
            }
        }

        return null;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static HttpClient CreateClient()
    {
        // MusicBrainz and the Cover Art Archive (served from archive.org) can take several seconds.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // MusicBrainz requires an identifying user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/0.1 (+https://github.com/Suman-Rajak/WinIsland)");
        return client;
    }
}

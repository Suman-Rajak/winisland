using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinIsland.Services;

/// <summary>
/// The thumbnail of a video playing in a browser. Browsers tell Windows a YouTube video's title and
/// channel but not its picture, so the video is found on YouTube's own search page (the one result
/// with exactly that title, from that channel when there's a choice) and its thumbnail used. Sends the
/// title to YouTube, so only call this when <see cref="AppSettings.FindArtworkOnline"/> is on.
/// </summary>
internal static partial class YouTubeThumbnails
{
    private static readonly HttpClient s_http = CreateClient();
    private static readonly Dictionary<string, byte[]?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    // Widest first: 16:9 without the black bands the 4:3 sizes add.
    private static readonly string[] s_sizes = ["hq720", "maxresdefault", "mqdefault"];

    public static async Task<byte[]?> FindAsync(string title, string channel)
    {
        string key = $"{channel}|{title}";
        if (s_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        byte[]? thumbnail = null;
        string? video = null;
        try
        {
            video = await FindVideoAsync(title, channel);
            if (video is not null)
            {
                thumbnail = await DownloadAsync(video);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Looking up '{title}' on YouTube failed", ex);
            return null; // (not remembered: next time the video plays, try again)
        }

        Log.Info($"YouTube thumbnail for '{title}': {(thumbnail is not null ? video : "none")} ({watch.ElapsedMilliseconds} ms)");
        return s_cache[key] = thumbnail;
    }

    /// <summary>The video's ID: of the search results, one with exactly this title, from this channel if one is.</summary>
    private static async Task<string?> FindVideoAsync(string title, string channel)
    {
        string page = await s_http.GetStringAsync(
            $"https://www.youtube.com/results?hl=en&search_query={Uri.EscapeDataString(channel.Length > 0 ? $"{title} {channel}" : title)}");
        if (InitialData().Match(page) is not { Success: true } data)
        {
            return null;
        }

        using var json = JsonDocument.Parse(data.Groups[1].Value);
        var videos = new List<(string Id, string Title, string Channel)>();
        Collect(json.RootElement, videos);

        string wanted = TrackQuery.Normalize(title), from = TrackQuery.Normalize(channel);
        if (wanted.Length == 0)
        {
            return null; // (a title of emoji alone matches too much)
        }
        var sameTitle = videos.Where(v => TrackQuery.Normalize(v.Title) == wanted).ToList();
        return sameTitle.Where(v => from.Length > 0 && TrackQuery.Normalize(v.Channel) == from)
            .Concat(sameTitle)
            .Select(v => v.Id)
            .FirstOrDefault();
    }

    /// <summary>Every video in the page's data: ordinary results, and Shorts.</summary>
    private static void Collect(JsonElement element, List<(string Id, string Title, string Channel)> videos)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Collect(item, videos);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("videoRenderer", out var video) && Text(video, "videoId") is { Length: 11 } id)
        {
            videos.Add((id, Runs(video, "title"), Runs(video, "ownerText")));
        }
        else if (element.TryGetProperty("reelItemRenderer", out var reel) && Text(reel, "videoId") is { Length: 11 } reelId)
        {
            videos.Add((reelId, reel.TryGetProperty("headline", out var headline) ? Text(headline, "simpleText") : string.Empty, string.Empty));
        }
        else if (element.TryGetProperty("shortsLockupViewModel", out var shorts) && ShortsId().Match(Text(shorts, "entityId")) is { Success: true } shortsId)
        {
            string shortsTitle = shorts.TryGetProperty("overlayMetadata", out var overlay) && overlay.TryGetProperty("primaryText", out var primary)
                ? Text(primary, "content") : string.Empty;
            videos.Add((shortsId.Groups[1].Value, shortsTitle, string.Empty));
        }

        foreach (var property in element.EnumerateObject())
        {
            Collect(property.Value, videos);
        }
    }

    private static async Task<byte[]?> DownloadAsync(string video)
    {
        foreach (string size in s_sizes)
        {
            using var response = await s_http.GetAsync($"https://i.ytimg.com/vi/{video}/{size}.jpg");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }
        }
        return null;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>YouTube's text that comes in runs: {"runs":[{"text":"…"},…]}.</summary>
    private static string Runs(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
            ? string.Concat(runs.EnumerateArray().Select(run => Text(run, "text")))
            : string.Empty;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // YouTube serves its full page, with the results' data in it, to browsers.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    [GeneratedRegex(@"var ytInitialData = (\{.*?\});</script>", RegexOptions.Singleline)]
    private static partial Regex InitialData();

    [GeneratedRegex(@"([A-Za-z0-9_-]{11})$")]
    private static partial Regex ShortsId();
}

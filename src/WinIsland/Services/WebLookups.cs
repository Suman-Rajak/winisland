using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinIsland.Services;

/// <summary>
/// Looks things up on the web for the AI brain, from free sources that need no key: the weather from
/// the Norwegian Meteorological Institute (api.met.no), news headlines from Google News, and Wikipedia.
/// What's found is plain text for the brain to answer from. (Gemini's own Google Search needs a key with
/// billing turned on.)
/// </summary>
internal static class WebLookups
{
    private static readonly HttpClient s_http = CreateClient();

    private static HttpClient CreateClient()
    {
        // api.met.no and Wikipedia ask apps to say who they are.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Product.Name}/1.0 (+https://github.com/Suman-Rajak/WinIsland)");
        return http;
    }

    /// <summary>The weather now and over the next days at a place (its coordinates from the brain).</summary>
    public static async Task<string> WeatherAsync(string place, double latitude, double longitude, CancellationToken cancellation)
    {
        try
        {
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={latitude:0.####}&lon={longitude:0.####}");
            using var json = JsonDocument.Parse(await s_http.GetStringAsync(url, cancellation));
            var series = json.RootElement.GetProperty("properties").GetProperty("timeseries").EnumerateArray().ToList();
            if (series.Count == 0)
            {
                return $"No weather forecast found for {place}.";
            }

            var text = new StringBuilder($"Weather for {place} (forecast from the Norwegian Meteorological Institute; times in UTC, now {DateTime.UtcNow:ddd HH:mm}):\n");
            text.AppendLine($"Now: {Describe(series[0])}");
            foreach (var hour in series.Skip(1).Take(12).Where((_, i) => i % 3 == 2))
            {
                text.AppendLine($"{Time(hour):ddd HH:mm}: {Describe(hour)}");
            }

            // The next days: lowest and highest temperature, how much rain, and the sky around midday there
            // (noon is about 12:00 UTC less an hour per 15° east).
            double noonUtc = 12 - longitude / 15;
            foreach (var day in series.GroupBy(s => Time(s).Date).Skip(1).Take(3))
            {
                var temperatures = day.Select(s => Details(s).GetProperty("air_temperature").GetDouble()).ToList();
                double rain = day.Sum(Rain);
                string? midday = day.Where(s => Symbol(s) is not null)
                    .MinBy(s => Math.Abs(Time(s).TimeOfDay.TotalHours - noonUtc)) is { } step ? Symbol(step) : null;
                text.AppendLine($"{day.Key:dddd}: {temperatures.Min():0} to {temperatures.Max():0} °C, " +
                                $"{midday?.Replace("_day", "").Replace("_night", "").Replace('_', ' ') ?? "no summary"}, rain {rain:0.#} mm");
            }
            return text.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Info($"Looking up the weather failed ({ex.Message})");
            return $"Couldn't get the weather for {place} just now.";
        }

        static DateTime Time(JsonElement s) => DateTime.Parse(s.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        static JsonElement Details(JsonElement s) => s.GetProperty("data").GetProperty("instant").GetProperty("details");
        static JsonElement? Next(JsonElement s, string span) => s.GetProperty("data").TryGetProperty(span, out var next) ? next : null;
        // The forecast is hourly for a couple of days, then six-hourly: each step's rain until the next.
        static double Rain(JsonElement s) =>
            (Next(s, "next_1_hours") ?? Next(s, "next_6_hours")) is { } next
            && next.TryGetProperty("details", out var details) && details.TryGetProperty("precipitation_amount", out var mm)
                ? mm.GetDouble()
                : 0;
        static string? Symbol(JsonElement s) =>
            (Next(s, "next_1_hours") ?? Next(s, "next_6_hours"))?.GetProperty("summary").GetProperty("symbol_code").GetString();
        static string Describe(JsonElement s)
        {
            var d = Details(s);
            string Value(string name, string format, string unit) => d.TryGetProperty(name, out var v) ? v.GetDouble().ToString(format, CultureInfo.InvariantCulture) + unit : "?";
            return $"{Value("air_temperature", "0", " °C")}, {Symbol(s)?.Replace('_', ' ') ?? "no summary"}, humidity {Value("relative_humidity", "0", "%")}, " +
                   $"wind {Value("wind_speed", "0", " m/s")}";
        }
    }

    /// <summary>News headlines (Google News, India edition) and the best Wikipedia matches for a search.</summary>
    public static async Task<string> SearchAsync(string query, CancellationToken cancellation)
    {
        var news = NewsAsync(query, cancellation);
        var wiki = WikipediaAsync(query, cancellation);
        return $"Searched for “{query}” (on {DateTime.Now:dddd d MMMM yyyy}).\n\n{await news}\n\n{await wiki}";
    }

    private static async Task<string> NewsAsync(string query, CancellationToken cancellation)
    {
        try
        {
            string url = $"https://news.google.com/rss/search?q={WebUtility.UrlEncode(query)}&hl=en-IN&gl=IN&ceid=IN:en";
            var feed = XDocument.Parse(await s_http.GetStringAsync(url, cancellation));
            var items = feed.Descendants("item").Take(8)
                .Select(item => $"- {item.Element("title")?.Value} ({(DateTime.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("d MMM yyyy, HH:mm") : "undated")})")
                .ToList();
            return items.Count > 0 ? "Latest news headlines:\n" + string.Join("\n", items) : "No news headlines found.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Log.Info($"Looking up news failed ({ex.Message})");
            return "Couldn't get news headlines just now.";
        }
    }

    private static async Task<string> WikipediaAsync(string query, CancellationToken cancellation)
    {
        try
        {
            string url = $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={WebUtility.UrlEncode(query)}&srlimit=3&format=json&utf8=1";
            using var json = JsonDocument.Parse(await s_http.GetStringAsync(url, cancellation));
            var pages = json.RootElement.GetProperty("query").GetProperty("search").EnumerateArray()
                .Select(p => $"- {p.GetProperty("title").GetString()}: {WebUtility.HtmlDecode(Regex.Replace(p.GetProperty("snippet").GetString() ?? "", "<[^>]+>", ""))}")
                .ToList();
            return pages.Count > 0 ? "Wikipedia:\n" + string.Join("\n", pages) : "Nothing on Wikipedia.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            Log.Info($"Looking up Wikipedia failed ({ex.Message})");
            return "Couldn't search Wikipedia just now.";
        }
    }
}

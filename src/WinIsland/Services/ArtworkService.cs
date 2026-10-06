using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace WinIsland.Services;

/// <param name="Cover">The artwork itself, at most 256 px.</param>
/// <param name="Glow">A tiny, darkened version of the artwork; stretched behind the island it becomes a soft colour wash.</param>
/// <param name="Tint">Dark base colour for the island's glass.</param>
/// <param name="Accent">Light colour for level bars and progress.</param>
internal sealed record Artwork(WriteableBitmap Cover, WriteableBitmap Glow, Color Tint, Color Accent);

/// <summary>Loads artwork for the current track and derives the island's colours from it. Call on the UI thread.</summary>
internal static class ArtworkService
{
    private const int CoverSize = 256;
    private const int GlowSize = 6;

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Dictionary<string, byte[]?> s_lookupCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the playing app doesn't publish artwork (Amazon Music doesn't), look the track up in the
    /// iTunes Search API. This sends the artist and title to Apple; it will become a setting.
    /// </summary>
    public static bool OnlineLookupEnabled { get; set; } = true;

    public static async Task<Artwork?> LoadAsync(MediaSnapshot media)
    {
        try
        {
            using var stream = await OpenAsync(media);
            if (stream is null)
            {
                return null;
            }

            var decoder = await BitmapDecoder.CreateAsync(stream);

            uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
            double scale = Math.Min(1.0, CoverSize / (double)Math.Max(width, height));
            var (coverWidth, coverHeight) = ((int)Math.Max(1, width * scale), (int)Math.Max(1, height * scale));
            var cover = ToBitmap(await DecodeAsync(decoder, coverWidth, coverHeight), coverWidth, coverHeight);

            var glowPixels = await DecodeAsync(decoder, GlowSize, GlowSize);
            var (tint, accent) = ColorMath.Palette(glowPixels);
            ColorMath.DarkenForBackdrop(glowPixels);
            var glow = ToBitmap(glowPixels, GlowSize, GlowSize);

            return new Artwork(cover, glow, tint, accent);
        }
        catch (Exception ex)
        {
            Log.Error("Loading artwork failed", ex);
            return null;
        }
    }

    private static async Task<IRandomAccessStream?> OpenAsync(MediaSnapshot media)
    {
        if (media.Thumbnail is not null)
        {
            try
            {
                return await media.Thumbnail.OpenReadAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Opening the app's thumbnail failed", ex);
            }
        }

        if (!OnlineLookupEnabled || media.Title.Length == 0)
        {
            return null; // artist alone would find some random album
        }

        var bytes = await LookupAsync(media.Artist, media.Title);
        return bytes is null ? null : new MemoryStream(bytes).AsRandomAccessStream();
    }

    private static async Task<byte[]?> LookupAsync(string artist, string title)
    {
        string key = $"{artist}|{title}";
        if (s_lookupCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        byte[]? result = null;
        try
        {
            string term = Uri.EscapeDataString($"{artist} {title}");
            using var json = JsonDocument.Parse(await s_http.GetStringAsync(
                $"https://itunes.apple.com/search?media=music&entity=song&limit=5&term={term}"));

            // Prefer a result by the same (first) artist; otherwise trust the search ranking.
            string firstArtist = artist.Split(',', '&')[0].Trim();
            JsonElement? best = null;
            foreach (var hit in json.RootElement.GetProperty("results").EnumerateArray())
            {
                best ??= hit;
                if (firstArtist.Length > 0 &&
                    hit.TryGetProperty("artistName", out var name) &&
                    name.GetString()?.Contains(firstArtist, StringComparison.OrdinalIgnoreCase) == true)
                {
                    best = hit;
                    break;
                }
            }

            if (best?.TryGetProperty("artworkUrl100", out var url) == true && url.GetString() is { } artworkUrl)
            {
                result = await s_http.GetByteArrayAsync(artworkUrl.Replace("100x100bb", "600x600bb"));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Artwork lookup failed", ex);
        }

        s_lookupCache[key] = result;
        return result;
    }

    private static async Task<byte[]> DecodeAsync(BitmapDecoder decoder, int width, int height)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)height,
            InterpolationMode = BitmapInterpolationMode.Fant, // averages pixels when shrinking
        };

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return data.DetachPixelData();
    }

    private static WriteableBitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }
}

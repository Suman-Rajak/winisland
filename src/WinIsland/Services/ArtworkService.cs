using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace WinIsland.Services;

/// <param name="Cover">The artwork itself, at most 256 px; null when no artwork could be found.</param>
/// <param name="Glow">A tiny, darkened version of the artwork; stretched behind the island it becomes a soft colour wash.</param>
/// <param name="Tint">Dark base colour for the island's glass.</param>
/// <param name="Accent">Light colour for level bars and progress.</param>
internal sealed record Artwork(WriteableBitmap? Cover, WriteableBitmap Glow, Color Tint, Color Accent);

/// <summary>Loads artwork for the current track and derives the island's colours from it. Call on the UI thread.</summary>
internal static class ArtworkService
{
    private const int CoverSize = 256;
    private const int GlowSize = 6;

    /// <summary>
    /// The track's artwork from the app, or else (if allowed) a browser video's thumbnail from YouTube or
    /// a song's cover from online catalogues. When there is none anywhere, colours generated from the
    /// title, so the glass still gets a colour per song.
    /// </summary>
    public static async Task<Artwork> LoadAsync(MediaSnapshot media)
    {
        try
        {
            using var stream = await OpenAsync(media);
            if (stream is not null)
            {
                return await DecodeArtworkAsync(stream);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Loading artwork failed", ex);
        }

        return Generated(media);
    }

    private static async Task<IRandomAccessStream?> OpenAsync(MediaSnapshot media)
    {
        // What a browser plays is mostly YouTube, and a browser's own picture for it is often just its
        // logo (Brave's lion), so look for the video's thumbnail first. Only a video with exactly this
        // title is taken, so a song on a music site still gets that site's picture.
        bool online = AppSettings.Current.FindArtworkOnline && media.Title.Length > 0;
        if (online && s_browsers.Contains(media.AppName) && await YouTubeThumbnails.FindAsync(media.Title, media.Artist) is { } video)
        {
            return new MemoryStream(video).AsRandomAccessStream();
        }

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

        if (!online)
        {
            return null;
        }

        var bytes = await MusicCatalog.FindArtworkAsync(media.Artist, media.Title);
        return bytes is null ? null : new MemoryStream(bytes).AsRandomAccessStream();
    }

    // As MediaService names them.
    private static readonly HashSet<string> s_browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome", "Microsoft Edge", "Brave", "Firefox", "Opera",
    };

    private static async Task<Artwork> DecodeArtworkAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);

        uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        double scale = Math.Min(1.0, CoverSize / (double)Math.Max(width, height));
        var (coverWidth, coverHeight) = ((int)Math.Max(1, width * scale), (int)Math.Max(1, height * scale));
        var cover = ToBitmap(await DecodeAsync(decoder, coverWidth, coverHeight), coverWidth, coverHeight);

        var glowPixels = await DecodeAsync(decoder, GlowSize, GlowSize);
        return WithGlow(cover, glowPixels);
    }

    /// <summary>A diagonal two-hue gradient whose hue comes from the track, so each song keeps its own colour.</summary>
    private static Artwork Generated(MediaSnapshot media)
    {
        uint hash = 2166136261; // FNV-1a: stable across runs, unlike string.GetHashCode
        foreach (char c in media.TrackKey)
        {
            hash = (hash ^ c) * 16777619;
        }

        double hue = (hash % 360) / 360.0;
        var pixels = new byte[GlowSize * GlowSize * 4];
        for (int y = 0; y < GlowSize; y++)
        {
            for (int x = 0; x < GlowSize; x++)
            {
                double t = (x + y) / (2.0 * (GlowSize - 1));
                var c = ColorMath.FromHsl((hue + 0.12 * t) % 1.0, 0.65, 0.45);
                int i = (y * GlowSize + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
            }
        }

        return WithGlow(cover: null, pixels);
    }

    private static Artwork WithGlow(WriteableBitmap? cover, byte[] glowPixels)
    {
        var (tint, accent) = ColorMath.Palette(glowPixels);
        ColorMath.DarkenForBackdrop(glowPixels);
        return new Artwork(cover, ToBitmap(glowPixels, GlowSize, GlowSize), tint, accent);
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

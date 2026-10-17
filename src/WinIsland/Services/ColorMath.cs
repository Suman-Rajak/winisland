using Windows.UI;

namespace WinIsland.Services;

/// <summary>Colour helpers for turning artwork into the island's glass tint.</summary>
internal static class ColorMath
{
    /// <summary>Opaque; how see-through the glass is comes from the "glass transparency" setting.</summary>
    public static readonly Color NeutralTint = Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1F);

    /// <summary>
    /// Picks the artwork's most vivid colour (falling back to its average for greyscale art) and
    /// returns a dark tint for the glass and a light accent for bars and progress.
    /// </summary>
    public static (Color Tint, Color Accent) Palette(byte[] bgra)
    {
        double bestScore = -1, bestH = 0, bestS = 0;
        double sumR = 0, sumG = 0, sumB = 0;
        int count = bgra.Length / 4;

        for (int i = 0; i < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            sumR += r; sumG += g; sumB += b;

            var (h, s, l) = ToHsl(r, g, b);
            double score = s * (1 - Math.Abs(l - 0.5) * 2); // saturated and mid-bright
            if (score > bestScore)
            {
                (bestScore, bestH, bestS) = (score, h, s);
            }
        }

        if (bestScore < 0.12)
        {
            // Greyscale or washed-out art: use the average, which keeps any faint cast.
            (bestH, bestS, _) = ToHsl(sumR / count, sumG / count, sumB / count);
        }

        var tint = FromHsl(bestH, Math.Min(bestS, 0.55), 0.20, alpha: NeutralTint.A);
        var accent = FromHsl(bestH, bestS < 0.12 ? bestS : Math.Clamp(bestS, 0.45, 0.85), 0.72);
        return (tint, accent);
    }

    /// <summary>Keeps each pixel's hue but makes it dark enough for white text on top.</summary>
    public static void DarkenForBackdrop(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            var (h, s, l) = ToHsl(bgra[i + 2] / 255.0, bgra[i + 1] / 255.0, bgra[i] / 255.0);
            var c = FromHsl(h, Math.Min(s * 1.1, 0.8), Math.Clamp(l * 0.6, 0.08, 0.30));
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (c.B, c.G, c.R, 255);
        }
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-6)
        {
            return (0, 0, l);
        }

        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0)
                 : max == g ? (b - r) / d + 2
                 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static Color FromHsl(double h, double s, double l, byte alpha = 255)
    {
        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t
                 : t < 1 / 2.0 ? q
                 : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6
                 : p;
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double r = s == 0 ? l : Channel(p, q, h + 1 / 3.0);
        double g = s == 0 ? l : Channel(p, q, h);
        double b = s == 0 ? l : Channel(p, q, h - 1 / 3.0);
        return Color.FromArgb(alpha, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}

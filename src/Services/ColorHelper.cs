using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarMusic.Services;

/// <summary>
/// Pulls a vibrant dominant colour out of the album art and pairs it with a
/// random accent hue — the palette that drives the visualiser + corner glow.
/// </summary>
internal static class ColorHelper
{
    public static (Color primary, Color secondary) FromArt(BitmapSource? art, Random rng)
    {
        // The "+ ceva culoare random" half: a fresh vibrant hue each track.
        Color randomAccent = FromHsv(rng.NextDouble() * 360, 0.72, 0.96);

        if (art is null)
            return (FromHsv(rng.NextDouble() * 360, 0.55, 0.85), randomAccent);

        try
        {
            var conv = new FormatConvertedBitmap(art, PixelFormats.Bgra32, null, 0);
            double s = 32.0;
            var small = new TransformedBitmap(conv,
                new ScaleTransform(s / conv.PixelWidth, s / conv.PixelHeight));

            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, wsum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double bb = px[i], gg = px[i + 1], rr = px[i + 2];
                double mx = Math.Max(rr, Math.Max(gg, bb));
                double mn = Math.Min(rr, Math.Min(gg, bb));
                double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                double lum = (0.299 * rr + 0.587 * gg + 0.114 * bb) / 255.0;
                // Favour saturated, mid-bright pixels; ignore near-grey/black/white.
                double weight = sat * (1.0 - Math.Abs(lum - 0.55));
                if (weight <= 0) continue;
                r += rr * weight; g += gg * weight; b += bb * weight; wsum += weight;
            }

            Color primary = wsum < 1e-3
                ? FromHsv(rng.NextDouble() * 360, 0.5, 0.8)
                : Boost(Color.FromRgb((byte)(r / wsum), (byte)(g / wsum), (byte)(b / wsum)));

            return (primary, randomAccent);
        }
        catch
        {
            return (FromHsv(rng.NextDouble() * 360, 0.55, 0.85), randomAccent);
        }
    }

    public static Color Lerp(Color a, Color c, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (c.A - a.A) * t),
            (byte)(a.R + (c.R - a.R) * t),
            (byte)(a.G + (c.G - a.G) * t),
            (byte)(a.B + (c.B - a.B) * t));
    }

    private static Color Boost(Color c)
    {
        var (h, s, v) = ToHsv(c);
        s = Math.Min(1, s * 1.25 + 0.10);
        v = Math.Min(1, Math.Max(v, 0.78));
        return FromHsv(h, s, v);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static (double h, double s, double v) ToHsv(Color col)
    {
        double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double d = mx - mn, h = 0;
        if (d > 0)
        {
            if (mx == r) h = 60 * (((g - b) / d) % 6);
            else if (mx == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        double s = mx <= 0 ? 0 : d / mx;
        return (h, s, mx);
    }
}

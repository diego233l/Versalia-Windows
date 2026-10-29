namespace Versalia.Core;

/// <summary>Saca una paleta de los píxeles de la portada: el tono más vistoso manda, el resto se deriva de él.</summary>
public static class PaletteExtractor
{
    /// <param name="bgra">Píxeles BGRA de una miniatura (p. ej. 32×32).</param>
    public static Palette? FromBgra(byte[] bgra, int pixelCount)
    {
        var weight = new double[12];
        var sum = new (double R, double G, double B)[12];
        for (int i = 0; i < pixelCount && i * 4 + 2 < bgra.Length; i++)
        {
            double b = bgra[i * 4] / 255.0, g = bgra[i * 4 + 1] / 255.0, r = bgra[i * 4 + 2] / 255.0;
            var (h, s, v) = ToHsv(r, g, b);
            if (v < 0.18 || s < 0.2) continue;
            double w = s * s * v;
            int bucket = (int)(h / 30) % 12;
            weight[bucket] += w;
            sum[bucket] = (sum[bucket].R + r * w, sum[bucket].G + g * w, sum[bucket].B + b * w);
        }
        int best = 0;
        for (int i = 1; i < 12; i++) if (weight[i] > weight[best]) best = i;
        if (weight[best] < 0.5) return null;

        double ar = sum[best].R / weight[best], ag = sum[best].G / weight[best], ab = sum[best].B / weight[best];
        var (hue, sat, _) = ToHsv(ar, ag, ab);
        var highlight = FromHsv(hue, Math.Clamp(sat, 0.6, 1), 1.0);
        var secondary = FromHsv((hue + 40) % 360, 0.55, 0.95);
        var text = new Rgb(1, 1, 1).Mix(highlight, 0.12);
        var background = FromHsv(hue, 0.6, 0.07);
        return new Palette(text, highlight, secondary, background);
    }

    internal static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min, h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    internal static Rgb FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return new Rgb(r + m, g + m, b + m);
    }
}

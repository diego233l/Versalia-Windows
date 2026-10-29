namespace Versalia.Core;

public readonly record struct StarPoint(double X, double Y);

/// <summary>Reparto determinista de las palabras de un grupo sobre un lienzo 0–1 y de las líneas que las unen.</summary>
public static class ConstellationLayout
{
    public const int MaxPoints = 24;

    private struct SplitMix64
    {
        public ulong State;
        public ulong Next()
        {
            unchecked
            {
                State += 0x9E3779B97F4A7C15UL;
                ulong z = State;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
        public double Unit() => (Next() >> 11) / (double)(1UL << 53);
    }

    private static ulong Seed(int group) => unchecked((ulong)(long)group * 0x9E3779B97F4A7C15UL ^ 0xD1B54A32D192ED03UL);

    private static double Distance(StarPoint a, StarPoint b)
    {
        double dx = (a.X - b.X) * 1.8, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static List<StarPoint> Points(int group, int count)
    {
        int total = Math.Min(Math.Max(0, count), MaxPoints);
        var random = new SplitMix64 { State = Seed(group) };
        var result = new List<StarPoint>();
        for (int i = 0; i < total; i++)
        {
            var best = new StarPoint(0.5, 0.5);
            double bestGap = -1;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var candidate = new StarPoint(0.08 + 0.84 * random.Unit(), 0.16 + 0.68 * random.Unit());
                double gap = result.Count == 0 ? 1 : result.Min(p => Distance(p, candidate));
                if (gap > bestGap) { bestGap = gap; best = candidate; }
                if (gap >= 0.2) break;
            }
            result.Add(best);
        }
        return result;
    }

    public static List<(int, int)> Edges(int group, int count)
    {
        int total = Math.Min(Math.Max(0, count), MaxPoints);
        var result = new List<(int, int)>();
        if (total <= 1) return result;
        for (int i = 1; i < total; i++)
        {
            result.Add((i - 1, i));
            if (i >= 3)
            {
                var random = new SplitMix64 { State = unchecked(Seed(group) ^ ((ulong)i * 0xA24BAED4963EE407UL)) };
                if (random.Unit() < 0.5) result.Add((i - 2 - (int)(random.Unit() * 2), i));
            }
        }
        return result;
    }
}

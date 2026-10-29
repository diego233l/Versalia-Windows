namespace Versalia.Core;

public sealed record LyricWord(string Text, int StartMs, int EndMs);

public sealed record LyricLine(int StartMs, int EndMs, string Text, IReadOnlyList<LyricWord> Words, bool IsEstimated);

public sealed class Lyrics
{
    public IReadOnlyList<LyricLine> Lines { get; }
    /// <summary>Proveedor o formato de origen (lrclib, netease, lrc...).</summary>
    public string Source { get; }

    public Lyrics(IEnumerable<LyricLine> lines, string source)
    {
        // Orden estable por inicio, así el motor de sincronía y el flujo de palabras comparten los mismos índices.
        Lines = lines.Select((line, index) => (line, index))
            .OrderBy(x => x.line.StartMs).ThenBy(x => x.index)
            .Select(x => x.line).ToList();
        Source = source;
    }

    public bool IsEmpty => Lines.Count == 0;
    public bool IsEstimated => Lines.Any(l => l.IsEstimated);
}

public sealed record TrackInfo(string Id, string Title, string Artist, string Album, int DurationMs);

public enum PlaybackState { Playing, Paused, Stopped }

/// <summary>Lectura puntual del reproductor. <c>CapturedAt</c> en segundos del reloj monotónico.</summary>
public sealed record PlaybackSnapshot(PlaybackState State, TrackInfo? Track, double PositionMs, double CapturedAt);

public readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(Convert.ToInt32(hex[..2], 16) / 255.0, Convert.ToInt32(hex[2..4], 16) / 255.0,
            Convert.ToInt32(hex[4..6], 16) / 255.0);
    }

    public Rgb Mix(Rgb other, double t) =>
        new(R + (other.R - R) * t, G + (other.G - G) * t, B + (other.B - B) * t);
}

public sealed record Palette(Rgb Text, Rgb Highlight, Rgb Secondary, Rgb Background);

public static class ThemePresets
{
    private static Palette Make(string text, string highlight, string secondary, string background) =>
        new(Rgb.FromHex(text), Rgb.FromHex(highlight), Rgb.FromHex(secondary), Rgb.FromHex(background));

    public static readonly IReadOnlyDictionary<string, Palette> All = new Dictionary<string, Palette>
    {
        ["gold"] = Make("#FFE9A8", "#FFC21A", "#D9B65C", "#150F02"),
        ["neon"] = Make("#F7F0FF", "#FF2EC4", "#59D8FF", "#0B0214"),
        ["aurora"] = Make("#E6FFF7", "#3CFFB0", "#8C9CFF", "#021210"),
        ["sunset"] = Make("#FFE8DC", "#FF6B4A", "#FF9ACB", "#170804"),
        ["ice"] = Make("#EAF7FF", "#5CC8FF", "#B4C2FF", "#020D16"),
        ["lime"] = Make("#F4FFE6", "#B6FF3B", "#6CE6A1", "#0A1202"),
        ["rose"] = Make("#FFEFF4", "#FF4F7B", "#FFA3B8", "#16030A"),
        ["violet"] = Make("#F1E8FF", "#A57BFF", "#E0A6FF", "#0A0316"),
    };

    public static Palette Default => All["aurora"];
}

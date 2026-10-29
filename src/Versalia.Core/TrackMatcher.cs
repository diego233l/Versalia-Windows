using System.Text;
using System.Text.RegularExpressions;

namespace Versalia.Core;

/// <summary>Normalización de títulos/artistas y comparación con tolerancia de duración (±3 s).</summary>
public static class TrackMatcher
{
    public const int DurationToleranceMs = 3000;

    private static readonly Regex VersionKeywords = new(
        @"\b(remaster(ed)?|live|version|edit|mono|stereo|deluxe|bonus|radio|single|demo|session|anniversary)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DashSuffix = new(@"\s[-–—]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Bracketed = new(@"\([^)]*\)|\[[^\]]*\]|（[^）]*）", RegexOptions.Compiled);
    private static readonly Regex Featuring = new(@"\s(?:feat\.?|ft\.?|featuring)(?:\s|$).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizeTitle(string title)
    {
        var text = Bracketed.Replace(title, " ");
        text = StripVersionSuffix(text);
        text = Featuring.Replace(text, "");
        var result = Simplify(text);
        return result.Length == 0 ? Simplify(title) : result;
    }

    public static string NormalizeArtist(string artist) => Simplify(Featuring.Replace(artist, ""));

    public static bool TitlesMatch(string a, string b)
    {
        var first = NormalizeTitle(a);
        var second = NormalizeTitle(b);
        if (first.Length == 0 || second.Length == 0) return false;
        if (first == second) return true;
        var (shortText, longText) = first.Length <= second.Length ? (first, second) : (second, first);
        return shortText.Length >= 4 && longText.StartsWith(shortText + " ", StringComparison.Ordinal);
    }

    public static bool ArtistsMatch(string a, string b)
    {
        var first = new HashSet<string>(NormalizeArtist(a).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var second = new HashSet<string>(NormalizeArtist(b).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (first.Count == 0 || second.Count == 0) return true;
        return first.IsSubsetOf(second) || second.IsSubsetOf(first);
    }

    public static bool Matches(TrackInfo track, string title, string artist, int? durationMs)
    {
        if (!TitlesMatch(track.Title, title) || !ArtistsMatch(track.Artist, artist)) return false;
        if (!durationMs.HasValue || track.DurationMs <= 0 || durationMs.Value <= 0) return true;
        return Math.Abs(track.DurationMs - durationMs.Value) <= DurationToleranceMs;
    }

    private static string StripVersionSuffix(string text)
    {
        var match = DashSuffix.Match(text);
        if (!match.Success) return text;
        return VersionKeywords.IsMatch(match.Groups[1].Value) ? text[..match.Index] : text;
    }

    internal static string Simplify(string text)
    {
        var folded = WordTimingEstimator.Fold(text);
        var sb = new StringBuilder(folded.Length);
        foreach (char c in folded) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

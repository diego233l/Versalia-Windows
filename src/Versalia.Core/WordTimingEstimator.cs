using System.Globalization;
using System.Text;

namespace Versalia.Core;

/// <summary>Reparte el tiempo de una línea entre sus palabras según cómo se canta (sílabas, pausas, última sostenida).</summary>
public static class WordTimingEstimator
{
    public static List<LyricWord> Words(string text, int startMs, int endMs)
    {
        var pieces = Tokens(text);
        var result = new List<LyricWord>();
        if (pieces.Count == 0) return result;
        var weights = pieces.Select((p, i) => TimingWeight(p, i == pieces.Count - 1, pieces.Count)).ToList();
        long total = Math.Max(1, weights.Sum());
        long duration = Math.Max(0, endMs - startMs);
        long cumulative = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            int wordStart = (int)(startMs + duration * cumulative / total);
            cumulative += weights[i];
            int wordEnd = i == pieces.Count - 1 ? Math.Max(startMs, endMs) : (int)(startMs + duration * cumulative / total);
            result.Add(new LyricWord(pieces[i], wordStart, wordEnd));
        }
        return result;
    }

    /// <summary>Duración aproximada cantando tranquilo (≈450 ms por sílaba + 600 ms), mínimo 1,5 s.</summary>
    public static int NaturalDurationMs(string text)
    {
        int syllables = Tokens(text).Sum(Weight);
        return Math.Max(1500, 600 + syllables * 450);
    }

    internal static int TimingWeight(string token, bool isLast, int count)
    {
        int value = Weight(token) * 4 + 1;
        if (isLast)
        {
            if (count > 1) value += 3;
        }
        else if (token.Length > 0)
        {
            char last = token[^1];
            if (",;:".IndexOf(last) >= 0) value += 2;
            if (".!?…".IndexOf(last) >= 0) value += 3;
        }
        return value;
    }

    internal static List<string> Tokens(string text)
    {
        var result = new List<string>();
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var current = new StringBuilder();
            foreach (char c in chunk)
            {
                if (IsCjk(c))
                {
                    if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                    result.Add(c.ToString());
                }
                else current.Append(c);
            }
            if (current.Length > 0) result.Add(current.ToString());
        }
        return result;
    }

    public static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF);

    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Número aproximado de sílabas (grupos de vocales); mínimo 1.</summary>
    internal static int Weight(string token)
    {
        if (token.Length == 1 && IsCjk(token[0])) return 1;
        var folded = Fold(token);
        const string vowels = "aeiouy";
        int groups = 0;
        bool previousVowel = false;
        foreach (char c in folded)
        {
            bool isVowel = vowels.IndexOf(c) >= 0;
            if (isVowel && !previousVowel) groups++;
            previousVowel = isVowel;
        }
        // "e" final muda en inglés (love, line), salvo "-le" (table).
        if (groups > 1 && folded.EndsWith('e') && !folded.EndsWith("le") && folded.Length >= 2 &&
            vowels.IndexOf(folded[^2]) < 0)
            groups--;
        if (groups == 0)
        {
            int letters = folded.Count(char.IsLetterOrDigit);
            return Math.Max(1, (letters + 2) / 3);
        }
        return groups;
    }
}

using System.Globalization;
using System.Text;

namespace Versalia.Core;

/// <summary>Formato por palabra de NetEase: [inicio,dur](inicio,dur,0)trozo...</summary>
public static class YrcParser
{
    public static Lyrics Parse(string text, string source = "netease") => ParseTimed(text, source, '(', ')', relative: false);

    /// <summary>Líneas [inicio,dur] seguidas de trozos con tiempo. <paramref name="relative"/>: tiempos de trozo relativos a la línea (KRC).</summary>
    internal static Lyrics ParseTimed(string text, string source, char open, char close, bool relative)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('[')) continue;
            int headerEnd = line.IndexOf(']');
            if (headerEnd < 0) continue;
            var header = line[1..headerEnd].Split(',');
            if (header.Length != 2 || !int.TryParse(header[0], out var lineStart) || !int.TryParse(header[1], out var lineDuration)) continue;

            var words = new List<LyricWord>();
            string? pendingText = null; int pendingStart = 0, pendingEnd = 0;
            void Flush()
            {
                if (pendingText != null)
                {
                    var trimmed = pendingText.Trim();
                    if (trimmed.Length > 0) words.Add(new LyricWord(trimmed, pendingStart, Math.Max(pendingStart, pendingEnd)));
                }
                pendingText = null;
            }

            foreach (var (rawStart, duration, chunk) in Chunks(line[(headerEnd + 1)..], open, close))
            {
                int start = relative ? lineStart + rawStart : rawStart;
                bool startsWord = (chunk.Length > 0 && char.IsWhiteSpace(chunk[0]))
                    || chunk.Any(WordTimingEstimator.IsCjk)
                    || (pendingText != null && pendingText.Any(WordTimingEstimator.IsCjk));
                if (startsWord) Flush();
                if (pendingText != null) { pendingText += chunk; pendingEnd = start + duration; }
                else { pendingText = chunk; pendingStart = start; pendingEnd = start + duration; }
                if (chunk.Length > 0 && char.IsWhiteSpace(chunk[^1])) Flush();
            }
            Flush();
            if (words.Count == 0) continue;
            int end = Math.Max(lineStart + lineDuration, words[^1].EndMs);
            lines.Add(new LyricLine(lineStart, end, string.Join(" ", words.Select(w => w.Text)), words, false));
        }
        return new Lyrics(lines, source);
    }

    internal static List<(int Start, int Duration, string Text)> Chunks(string body, char open = '(', char close = ')')
    {
        var result = new List<(int, int, string)>();
        (int Start, int Duration)? current = null;
        var text = new StringBuilder();
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == open)
            {
                int end = body.IndexOf(close, i);
                if (end > i)
                {
                    var fields = body[(i + 1)..end].Split(',');
                    if (fields.Length >= 2 && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                        && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                    {
                        if (current.HasValue) result.Add((current.Value.Start, current.Value.Duration, text.ToString()));
                        current = (s, d);
                        text.Clear();
                        i = end + 1;
                        continue;
                    }
                }
            }
            text.Append(body[i]);
            i++;
        }
        if (current.HasValue) result.Add((current.Value.Start, current.Value.Duration, text.ToString()));
        return result;
    }
}

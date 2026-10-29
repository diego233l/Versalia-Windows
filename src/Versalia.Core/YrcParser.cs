using System.Globalization;
using System.Text;

namespace Versalia.Core;

/// <summary>Formato por palabra de NetEase: [inicio,dur](inicio,dur,0)trozo...</summary>
public static class YrcParser
{
    public static Lyrics Parse(string text, string source = "netease")
    {
        var lines = new List<LyricLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('[')) continue;
            int close = line.IndexOf(']');
            if (close < 0) continue;
            var header = line[1..close].Split(',');
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

            foreach (var (start, duration, chunk) in Chunks(line[(close + 1)..]))
            {
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

    internal static List<(int Start, int Duration, string Text)> Chunks(string body)
    {
        var result = new List<(int, int, string)>();
        (int Start, int Duration)? current = null;
        var text = new StringBuilder();
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == '(')
            {
                int close = body.IndexOf(')', i);
                if (close > i)
                {
                    var fields = body[(i + 1)..close].Split(',');
                    if (fields.Length >= 2 && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                        && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
                    {
                        if (current.HasValue) result.Add((current.Value.Start, current.Value.Duration, text.ToString()));
                        current = (s, d);
                        text.Clear();
                        i = close + 1;
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

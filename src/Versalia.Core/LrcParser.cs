using System.Globalization;
using System.Text;

namespace Versalia.Core;

/// <summary>LRC estándar y Enhanced LRC (marcas &lt;mm:ss.xx&gt; por palabra). Sin marcas por palabra se estiman.</summary>
public static class LrcParser
{
    private sealed record Entry(int Start, string Body);

    public static Lyrics Parse(string text, int? durationMs = null, string source = "lrc")
    {
        int offset = 0;
        var entries = new List<Entry>();
        foreach (var raw in text.Split('\n'))
        {
            var rest = raw.Trim();
            var starts = new List<int>();
            while (rest.StartsWith('[') && rest.IndexOf(']') is int close && close > 0)
            {
                var inner = rest[1..close];
                var time = ParseTimestamp(inner);
                if (time.HasValue) starts.Add(time.Value);
                else if (inner.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(inner["offset:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o)) offset = o;
                }
                else if (starts.Count > 0) break;
                rest = rest[(close + 1)..];
            }
            foreach (var s in starts) entries.Add(new Entry(s, rest));
        }

        var sorted = entries.Select((e, i) => (e, i)).OrderBy(x => x.e.Start).ThenBy(x => x.i).Select(x => x.e).ToList();
        int Adjusted(int v) => Math.Max(0, v - offset);

        var lines = new List<LyricLine>();
        for (int index = 0; index < sorted.Count; index++)
        {
            var entry = sorted[index];
            var body = entry.Body.Trim();
            if (body.Length == 0) continue;

            int start = Adjusted(entry.Start);
            int fallbackEnd;
            if (index + 1 < sorted.Count) fallbackEnd = Adjusted(sorted[index + 1].Start);
            else if (durationMs.HasValue && durationMs.Value > start) fallbackEnd = durationMs.Value;
            else fallbackEnd = start + 5000;

            var segments = SplitWordTags(body);
            if (segments != null && segments.Any(s => s.Text.Trim().Length > 0))
            {
                int lineEnd = fallbackEnd;
                if (segments.Count > 1 && segments[^1].Text.Trim().Length == 0 && segments[^1].Time.HasValue)
                    lineEnd = Math.Max(start, Adjusted(segments[^1].Time!.Value));
                var words = new List<LyricWord>();
                bool estimated = false;
                for (int s = 0; s < segments.Count; s++)
                {
                    var wordText = segments[s].Text.Trim();
                    if (wordText.Length == 0) continue;
                    int wordStart = Adjusted(segments[s].Time ?? entry.Start);
                    int wordEnd = lineEnd;
                    if (s + 1 < segments.Count && segments[s + 1].Time.HasValue) wordEnd = Adjusted(segments[s + 1].Time!.Value);
                    wordEnd = Math.Max(wordStart, wordEnd);
                    var split = WordTimingEstimator.Words(wordText, wordStart, wordEnd);
                    if (split.Count > 1) estimated = true;
                    words.AddRange(split);
                }
                lines.Add(new LyricLine(start, lineEnd, string.Join(" ", words.Select(w => w.Text)), words, estimated));
            }
            else
            {
                int end = Math.Min(fallbackEnd, start + WordTimingEstimator.NaturalDurationMs(body));
                lines.Add(new LyricLine(start, end, body, WordTimingEstimator.Words(body, start, end), true));
            }
        }
        return new Lyrics(lines, source);
    }

    /// <summary>mm:ss, mm:ss.xx o mm:ss:xx a milisegundos; null si no es una marca de tiempo.</summary>
    public static int? ParseTimestamp(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 2 && parts.Length != 3) return null;
        if (!IsDigits(parts[0])) return null;
        int minutes = int.Parse(parts[0], CultureInfo.InvariantCulture);
        string secondsText = parts[1];
        string fractionText = "";
        if (parts.Length == 3) fractionText = parts[2];
        else
        {
            int dot = secondsText.IndexOf('.');
            if (dot >= 0) { fractionText = secondsText[(dot + 1)..]; secondsText = secondsText[..dot]; }
        }
        if (!IsDigits(secondsText)) return null;
        int seconds = int.Parse(secondsText, CultureInfo.InvariantCulture);
        int fraction = 0;
        if (fractionText.Length > 0)
        {
            if (!IsDigits(fractionText)) return null;
            var digits = fractionText.Length > 3 ? fractionText[..3] : fractionText;
            int value = int.Parse(digits, CultureInfo.InvariantCulture);
            fraction = digits.Length switch { 1 => value * 100, 2 => value * 10, _ => value };
        }
        return (minutes * 60 + seconds) * 1000 + fraction;
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(c => c >= '0' && c <= '9');

    private sealed record Segment(int? Time, string Text);

    private static List<Segment>? SplitWordTags(string body)
    {
        var segments = new List<Segment>();
        int? currentTime = null;
        var current = new StringBuilder();
        bool sawTag = false;
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == '<')
            {
                int close = body.IndexOf('>', i);
                if (close > i)
                {
                    var time = ParseTimestamp(body[(i + 1)..close]);
                    if (time.HasValue)
                    {
                        segments.Add(new Segment(currentTime, current.ToString()));
                        currentTime = time;
                        current.Clear();
                        sawTag = true;
                        i = close + 1;
                        continue;
                    }
                }
            }
            current.Append(body[i]);
            i++;
        }
        segments.Add(new Segment(currentTime, current.ToString()));
        return sawTag ? segments : null;
    }
}

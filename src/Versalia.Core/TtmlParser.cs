using System.Globalization;
using System.Text;
using System.Xml;

namespace Versalia.Core;

/// <summary>
/// TTML (el formato de Apple Music): <c>&lt;p begin end&gt;</c> por línea y <c>&lt;span begin end&gt;</c> por sílaba o palabra.
/// Spans pegados forman una palabra; un espacio separa palabras. Los coros de fondo (<c>ttm:role="x-bg"</c>) se omiten.
/// Una línea sin spans se reparte por sílabas y queda marcada como estimada.
/// </summary>
public static class TtmlParser
{
    private sealed record Token(bool IsSpan, string Text, int? Begin, int? End);
    private sealed class SpanFrame { public readonly StringBuilder Text = new(); public int? Begin, End; }

    public static Lyrics Parse(string ttml, string source = "ttml")
    {
        var pending = new List<(int Start, int? End, string Text, List<LyricWord>? Words)>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null };
        bool inP = false;
        int? pBegin = null, pEnd = null;
        var tokens = new List<Token>();
        var stack = new List<SpanFrame>();
        int skipDepth = 0;

        void Finish()
        {
            var words = BuildWords(tokens, pBegin, pEnd);
            if (words.Count > 0)
            {
                pending.Add((pBegin ?? words[0].StartMs, pEnd ?? words[^1].EndMs, string.Join(" ", words.Select(w => w.Text)), words));
                return;
            }
            var plain = string.Join(" ", string.Concat(tokens.Select(t => t.Text))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (plain.Length > 0 && pBegin.HasValue) pending.Add((pBegin.Value, pEnd, plain, null));
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(ttml), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var name = reader.LocalName;
                        bool empty = reader.IsEmptyElement;
                        if (name == "p")
                        {
                            inP = !empty;
                            tokens.Clear(); stack.Clear(); skipDepth = 0;
                            pBegin = ParseTime(reader.GetAttribute("begin"));
                            pEnd = ParseTime(reader.GetAttribute("end"));
                            if (pEnd == null && pBegin.HasValue && ParseTime(reader.GetAttribute("dur")) is int dur) pEnd = pBegin + dur;
                        }
                        else if (inP && name == "span")
                        {
                            if (skipDepth > 0 || IsBackground(reader)) { if (!empty) skipDepth++; break; }
                            if (empty) break;
                            stack.Add(new SpanFrame { Begin = ParseTime(reader.GetAttribute("begin")), End = ParseTime(reader.GetAttribute("end")) });
                        }
                        else if (inP && name == "br" && skipDepth == 0) tokens.Add(new Token(false, " ", null, null));
                        break;
                    }
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (!inP || skipDepth > 0) break;
                        if (stack.Count == 0) tokens.Add(new Token(false, reader.Value, null, null));
                        else stack[^1].Text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement:
                    {
                        var name = reader.LocalName;
                        if (inP && name == "span")
                        {
                            if (skipDepth > 0) { skipDepth--; break; }
                            if (stack.Count == 0) break;
                            var frame = stack[^1];
                            stack.RemoveAt(stack.Count - 1);
                            var text = frame.Text.ToString();
                            var parent = stack.Count > 0 ? stack[^1] : null;
                            if (text.Trim().Length == 0) tokens.Add(new Token(false, text, null, null));
                            else tokens.Add(new Token(true, text, frame.Begin ?? parent?.Begin ?? pBegin, frame.End ?? parent?.End ?? pEnd));
                        }
                        else if (inP && name == "p")
                        {
                            Finish();
                            inP = false;
                        }
                        break;
                    }
                }
            }
        }
        catch (XmlException) when (pending.Count > 0) { /* se usa lo que se pudo leer */ }

        var sorted = pending.Select((p, i) => (p, i)).OrderBy(x => x.p.Start).ThenBy(x => x.i).Select(x => x.p).ToList();
        var lines = new List<LyricLine>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var p = sorted[i];
            int end = Math.Max(p.Start, p.End ?? (i + 1 < sorted.Count ? sorted[i + 1].Start : p.Start + 5000));
            if (p.Words != null) lines.Add(new LyricLine(p.Start, end, p.Text, p.Words, false));
            else lines.Add(new LyricLine(p.Start, end, p.Text, WordTimingEstimator.Words(p.Text, p.Start, end), true));
        }
        return new Lyrics(lines, source);
    }

    private static bool IsBackground(XmlReader reader)
    {
        if (!reader.HasAttributes) return false;
        for (int i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.LocalName == "role" && reader.Value.Contains("x-bg", StringComparison.OrdinalIgnoreCase))
            {
                reader.MoveToElement();
                return true;
            }
        }
        reader.MoveToElement();
        return false;
    }

    private static List<LyricWord> BuildWords(List<Token> tokens, int? pBegin, int? pEnd)
    {
        var words = new List<LyricWord>();
        string? text = null; int start = 0, end = 0;
        void Flush()
        {
            if (text != null && text.Length > 0) words.Add(new LyricWord(text, start, Math.Max(start, end)));
            text = null;
        }
        foreach (var t in tokens)
        {
            if (!t.IsSpan)
            {
                if (t.Text.Any(char.IsWhiteSpace)) Flush();
                continue;
            }
            if (t.Begin is not int b || t.End is not int e) continue;
            var trimmed = t.Text.Trim();
            bool cjk = trimmed.Any(WordTimingEstimator.IsCjk) || (text != null && text.Any(WordTimingEstimator.IsCjk));
            if (char.IsWhiteSpace(t.Text[0]) || cjk) Flush();
            if (text != null) { text += trimmed; end = e; }
            else { text = trimmed; start = b; end = e; }
            if (char.IsWhiteSpace(t.Text[^1])) Flush();
        }
        Flush();
        return words;
    }

    /// <summary><c>hh:mm:ss.fff</c>, <c>mm:ss.fff</c>, <c>ss.fff</c>, <c>12.5s</c>, <c>1500ms</c> → milisegundos.</summary>
    public static int? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.EndsWith("ms", StringComparison.Ordinal))
            return double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? (int)Math.Round(ms) : null;
        if (text.EndsWith('s'))
            return double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? (int)Math.Round(s * 1000) : null;
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3) return null;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
            seconds = seconds * 60 + n;
        }
        return (int)Math.Round(seconds * 1000);
    }
}

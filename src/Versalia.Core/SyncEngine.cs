namespace Versalia.Core;

/// <summary>Línea y palabra activas en un instante y progreso (0–1) dentro de la palabra.</summary>
public readonly record struct SyncPosition(int LineIndex, int WordIndex, double WordProgress, bool IsLineFinished)
{
    /// <summary>Todavía no ha empezado ninguna línea.</summary>
    public static readonly SyncPosition None = new(-1, -1, 0, false);
}

public static class Search
{
    /// <summary>Búsqueda binaria: índice del último elemento con inicio &lt;= time, o -1.</summary>
    public static int LastIndex(int count, Func<int, int> startAt, int time)
    {
        int low = 0, high = count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (startAt(middle) <= time) low = middle + 1; else high = middle;
        }
        return low - 1;
    }
}

/// <summary>Cursor que aprovecha que el tiempo casi siempre avanza; tras un seek recurre a la búsqueda binaria.</summary>
public sealed class SyncCursor
{
    private readonly IReadOnlyList<LyricLine> _lines;
    private int _lineIndex = -1;
    private int _wordIndex = -1;

    public SyncCursor(Lyrics lyrics) => _lines = lyrics.Lines;

    public SyncPosition Position(double positionMs)
    {
        int time = (int)Math.Floor(positionMs);
        if (!LineIsCurrent(_lineIndex, time))
        {
            if (LineIsCurrent(_lineIndex + 1, time)) _lineIndex++;
            else _lineIndex = Search.LastIndex(_lines.Count, i => _lines[i].StartMs, time);
            _wordIndex = -1;
        }
        if (_lineIndex < 0) return SyncPosition.None;
        var line = _lines[_lineIndex];
        int word;
        if (WordIsCurrent(line, _wordIndex, time)) word = _wordIndex;
        else if (WordIsCurrent(line, _wordIndex + 1, time)) word = _wordIndex + 1;
        else word = Search.LastIndex(line.Words.Count, i => line.Words[i].StartMs, time);
        _wordIndex = word;
        double progress = 0;
        if (word >= 0)
        {
            var w = line.Words[word];
            int duration = w.EndMs - w.StartMs;
            progress = duration > 0 ? Math.Min(1, Math.Max(0, (time - w.StartMs) / (double)duration)) : 1;
        }
        return new SyncPosition(_lineIndex, word, progress, time >= line.EndMs);
    }

    private bool LineIsCurrent(int index, int time) =>
        index >= 0 && index < _lines.Count && _lines[index].StartMs <= time &&
        (index + 1 == _lines.Count || _lines[index + 1].StartMs > time);

    private static bool WordIsCurrent(LyricLine line, int index, int time) =>
        index >= 0 && index < line.Words.Count && line.Words[index].StartMs <= time &&
        (index + 1 == line.Words.Count || line.Words[index + 1].StartMs > time);
}

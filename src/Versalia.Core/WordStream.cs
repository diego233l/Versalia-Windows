namespace Versalia.Core;

/// <summary>Todas las palabras de una letra en orden temporal, agrupadas en grupos de hasta 8 (una constelación).</summary>
public sealed class WordStream
{
    public sealed class Entry
    {
        public string Text = "";
        public int StartMs, EndMs, LineIndex, WordIndex;
        public int GroupId, GroupOrdinal, GroupSize = 1;
    }

    public const int GroupCapacity = 8;
    public const int ReleaseGapMs = 1000;
    public const double ReleaseDurationMs = 700;

    public IReadOnlyList<Entry> Entries { get; }
    /// <summary>Primer índice y número de palabras de cada grupo.</summary>
    public IReadOnlyDictionary<int, (int First, int Count)> Groups { get; }

    public WordStream(Lyrics lyrics)
    {
        var list = new List<Entry>();
        for (int li = 0; li < lyrics.Lines.Count; li++)
        {
            var line = lyrics.Lines[li];
            for (int wi = 0; wi < line.Words.Count; wi++)
            {
                var text = line.Words[wi].Text.Trim();
                if (text.Length == 0) continue;
                list.Add(new Entry { Text = text, StartMs = line.Words[wi].StartMs,
                    EndMs = Math.Max(line.Words[wi].EndMs, line.Words[wi].StartMs), LineIndex = li, WordIndex = wi });
            }
        }
        var sorted = list.Select((e, i) => (e, i)).OrderBy(x => x.e.StartMs).ThenBy(x => x.i).Select(x => x.e).ToList();
        var ordinalByLine = new Dictionary<int, int>();
        var sizeByGroup = new Dictionary<int, int>();
        foreach (var e in sorted)
        {
            ordinalByLine.TryGetValue(e.LineIndex, out int ordinal);
            ordinalByLine[e.LineIndex] = ordinal + 1;
            int chunk = Math.Min(ordinal / GroupCapacity, 15);
            e.GroupId = e.LineIndex * 16 + chunk;
            e.GroupOrdinal = ordinal - chunk * GroupCapacity;
            sizeByGroup.TryGetValue(e.GroupId, out int n);
            sizeByGroup[e.GroupId] = n + 1;
        }
        foreach (var e in sorted) e.GroupSize = sizeByGroup[e.GroupId];
        Entries = sorted;
        var groups = new Dictionary<int, (int, int)>();
        for (int i = 0; i < sorted.Count; i++)
        {
            int id = sorted[i].GroupId;
            groups[id] = groups.TryGetValue(id, out var g) ? (g.Item1, g.Item2 + 1) : (i, 1);
        }
        Groups = groups.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2));
    }

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>Índice de la última palabra ya empezada, o -1.</summary>
    public int LastStartedIndex(double positionMs)
    {
        int time = (int)Math.Floor(positionMs);
        return Search.LastIndex(Entries.Count, i => Entries[i].StartMs, time);
    }

    public int FirstIndexOfGroup(int index)
    {
        if (index < 0 || index >= Entries.Count) return 0;
        int first = index;
        int group = Entries[index].GroupId;
        while (first > 0 && Entries[first - 1].GroupId == group) first--;
        return first;
    }

    public readonly record struct Phrasing(double Hold, double Release);

    private static double Smooth(double v)
    {
        double t = Math.Min(1, Math.Max(0, v));
        return t * t * (3 - 2 * t);
    }

    public Phrasing GetPhrasing(int index, double positionMs)
    {
        if (index < 0 || index >= Entries.Count) return new Phrasing(0, 0);
        var entry = Entries[index];
        double start = entry.StartMs, end = entry.EndMs;
        long gap = index + 1 < Entries.Count ? Entries[index + 1].StartMs - entry.EndMs : long.MaxValue;
        bool endsPhrase = gap >= ReleaseGapMs;

        double release = 0;
        if (endsPhrase) release = Smooth((positionMs - (end - 150)) / ReleaseDurationMs);

        long total = 0; int count = 0;
        for (int c = index - 1; c >= 0 && Entries[c].LineIndex == entry.LineIndex; c--) { total += Entries[c].EndMs - Entries[c].StartMs; count++; }
        for (int c = index + 1; c < Entries.Count && Entries[c].LineIndex == entry.LineIndex; c++) { total += Entries[c].EndMs - Entries[c].StartMs; count++; }
        double average = count > 0 ? (double)total / count : 450;
        double duration = end - start;
        double hold = 0;
        if (duration >= Math.Max(800, average * 1.7))
        {
            hold = Smooth((positionMs - start - 200) / Math.Max(1, duration - 200));
            bool closesLine = index + 1 >= Entries.Count || Entries[index + 1].LineIndex != entry.LineIndex;
            if (closesLine)
            {
                double fade = Math.Min(ReleaseDurationMs, duration * 0.45);
                release = Math.Max(release, Smooth((positionMs - (end - fade)) / fade));
            }
            else if (!endsPhrase && positionMs > end)
                hold *= Math.Max(0, 1 - (positionMs - end) / 300);
        }
        return new Phrasing(hold, release);
    }

    /// <summary>Opacidad general (0–1): la frase se apaga si tras ella viene una pausa larga y vuelve antes de la siguiente.</summary>
    public double Visibility(int lastStarted, double positionMs)
    {
        double value = 1;
        if (lastStarted >= 0 && lastStarted < Entries.Count)
            value = 1 - GetPhrasing(lastStarted, positionMs).Release;
        int next = lastStarted + 1;
        if (next >= 0 && next < Entries.Count && value < 1 &&
            (lastStarted < 0 || Entries[next].StartMs - Entries[lastStarted].EndMs >= ReleaseGapMs))
        {
            double until = Entries[next].StartMs - positionMs;
            if (until < 1200) value = Math.Max(value, Math.Min(1, (1200 - until) / 400));
        }
        return value;
    }
}

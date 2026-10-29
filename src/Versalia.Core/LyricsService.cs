using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Versalia.Core;

/// <summary>Caché en disco (una letra por archivo JSON) con expiración. También recuerda los "no hay letra" un día.</summary>
public sealed class LyricsCache
{
    private readonly string _directory;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _negativeTtl;

    public LyricsCache(string directory, TimeSpan? ttl = null, TimeSpan? negativeTtl = null)
    {
        _directory = directory;
        _ttl = ttl ?? TimeSpan.FromDays(30);
        _negativeTtl = negativeTtl ?? TimeSpan.FromDays(1);
    }

    private sealed class Stored
    {
        public DateTime SavedUtc { get; set; }
        public bool Missing { get; set; }
        public string Source { get; set; } = "";
        public List<StoredLine> Lines { get; set; } = new();
    }
    private sealed class StoredLine
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string Text { get; set; } = "";
        public bool Estimated { get; set; }
        public List<int[]> Times { get; set; } = new();
        public List<string> Words { get; set; } = new();
    }

    private string PathFor(TrackInfo track)
    {
        var key = $"{TrackMatcher.NormalizeTitle(track.Title)}|{TrackMatcher.NormalizeArtist(track.Artist)}|{track.DurationMs / 1000}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(_directory, hash + ".json");
    }

    /// <returns>(found, lyrics). found=true con lyrics=null significa "ya sabemos que no hay letra".</returns>
    public (bool Found, Lyrics? Lyrics) TryGet(TrackInfo track)
    {
        try
        {
            var path = PathFor(track);
            if (!File.Exists(path)) return (false, null);
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored == null) return (false, null);
            var age = DateTime.UtcNow - stored.SavedUtc;
            if (age > (stored.Missing ? _negativeTtl : _ttl)) return (false, null);
            if (stored.Missing) return (true, null);
            var lines = stored.Lines.Select(l => new LyricLine(l.Start, l.End, l.Text,
                l.Words.Select((w, i) => new LyricWord(w, l.Times[i][0], l.Times[i][1])).ToList(), l.Estimated));
            return (true, new Lyrics(lines, stored.Source));
        }
        catch { return (false, null); }
    }

    public void Put(TrackInfo track, Lyrics? lyrics)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var stored = new Stored { SavedUtc = DateTime.UtcNow, Missing = lyrics == null, Source = lyrics?.Source ?? "" };
            if (lyrics != null)
                stored.Lines = lyrics.Lines.Select(l => new StoredLine
                {
                    Start = l.StartMs, End = l.EndMs, Text = l.Text, Estimated = l.IsEstimated,
                    Words = l.Words.Select(w => w.Text).ToList(),
                    Times = l.Words.Select(w => new[] { w.StartMs, w.EndMs }).ToList(),
                }).ToList();
            File.WriteAllText(PathFor(track), JsonSerializer.Serialize(stored));
        }
        catch { /* la caché es opcional */ }
    }
}

/// <summary>Caché primero, luego los proveedores. Un fallo de red no se cachea como "sin letra".</summary>
public sealed class LyricsService
{
    private readonly ILyricsProvider _provider;
    private readonly LyricsCache _cache;

    public LyricsService(ILyricsProvider provider, LyricsCache cache)
    {
        _provider = provider;
        _cache = cache;
    }

    public async Task<Lyrics?> LoadAsync(TrackInfo track, CancellationToken ct = default)
    {
        var (found, cached) = _cache.TryGet(track);
        if (found) return cached;
        var lyrics = await _provider.FetchAsync(track, ct).ConfigureAwait(false);
        _cache.Put(track, lyrics);
        return lyrics;
    }
}

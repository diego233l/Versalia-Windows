using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Versalia.Core;

/// <summary>Formato KRC de Kugou: [inicioLínea,dur]&lt;desde,dur,0&gt;trozo... con tiempos de trozo relativos a la línea.</summary>
public static class KrcParser
{
    private static readonly byte[] Key =
        { 0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69 };

    public static Lyrics Parse(string text, string source = "kugou") =>
        YrcParser.ParseTimed(text, source, '<', '>', relative: true);

    /// <summary>El contenido descargado: "krc1" + bytes cifrados con XOR + zlib.</summary>
    public static string Decode(byte[] data)
    {
        if (data.Length < 4) return "";
        var body = new byte[data.Length - 4];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(data[i + 4] ^ Key[i % Key.Length]);
        using var input = new MemoryStream(body);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray()).TrimStart('﻿');
    }

    /// <summary>Lo contrario de <see cref="Decode"/>; solo para los tests.</summary>
    public static byte[] Encode(string text)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            zlib.Write(bytes, 0, bytes.Length);
        }
        var body = output.ToArray();
        var result = new byte[body.Length + 4];
        Encoding.ASCII.GetBytes("krc1").CopyTo(result, 0);
        for (int i = 0; i < body.Length; i++) result[i + 4] = (byte)(body[i] ^ Key[i % Key.Length]);
        return result;
    }
}

/// <summary>Kugou: muchas canciones con tiempos reales por palabra (KRC). API no oficial y sin clave; nunca lanza.
/// Solo se envían título, artista y duración.</summary>
public sealed class KugouProvider : ILyricsProvider
{
    public string Id => "kugou";
    private readonly IHttp _http;
    private readonly string _baseUrl;
    private readonly Action<string> _log;

    public KugouProvider(IHttp? http = null, string baseUrl = "https://lyrics.kugou.com", Action<string>? log = null)
    {
        _http = http ?? new HttpClientAdapter(5);
        _baseUrl = baseUrl;
        _log = log ?? (_ => { });
    }

    public async Task<Lyrics?> FetchAsync(TrackInfo track, CancellationToken ct = default)
    {
        try { return await Lookup(track, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { _log("kugou: fallo interno " + e.Message); return null; }
    }

    private static readonly Dictionary<string, string> Headers = new() { ["User-Agent"] = "Mozilla/5.0" };

    private async Task<Lyrics?> Lookup(TrackInfo track, CancellationToken ct)
    {
        var title = TrackMatcher.SearchTitle(track.Title);
        var keyword = track.Artist.Length > 0 ? $"{track.Artist} - {title}" : title;
        var (body, status) = await _http.GetAsync(Url.Build(_baseUrl, "search",
            ("ver", "1"), ("man", "yes"), ("client", "pc"), ("keyword", keyword),
            ("duration", track.DurationMs.ToString()), ("hash", "")), Headers, ct).ConfigureAwait(false);
        if (status != 200) { _log($"kugou: búsqueda respondió {status}"); return null; }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            _log("kugou: sin candidatos");
            return null;
        }
        var candidates = new List<(string Id, string Key, int Duration)>();
        int total = 0;
        foreach (var c in list.EnumerateArray())
        {
            total++;
            string song = Str(c, "song"), singer = Str(c, "singer");
            int duration = c.TryGetProperty("duration", out var d) && d.TryGetInt32(out var di) ? di : 0;
            bool ok = TrackMatcher.Matches(track, song, singer, duration) ||
                      (TrackMatcher.TitlesMatch(track.Title, song) && duration > 0 && track.DurationMs > 0 &&
                       Math.Abs(duration - track.DurationMs) <= TrackMatcher.DurationToleranceMs);
            if (ok) candidates.Add((Str(c, "id"), Str(c, "accesskey"), duration));
        }
        _log($"kugou: «{keyword}» → {total} candidatos, {candidates.Count} coinciden");

        foreach (var c in candidates.OrderBy(c => Math.Abs(c.Duration - track.DurationMs)).Take(2))
        {
            var (lyricBody, lyricStatus) = await _http.GetAsync(Url.Build(_baseUrl, "download",
                ("ver", "1"), ("client", "pc"), ("id", c.Id), ("accesskey", c.Key), ("fmt", "krc"), ("charset", "utf8")),
                Headers, ct).ConfigureAwait(false);
            if (lyricStatus != 200) continue;
            using var lyricDoc = JsonDocument.Parse(lyricBody);
            var content = Str(lyricDoc.RootElement, "content");
            if (content.Length == 0) continue;
            var text = KrcParser.Decode(Convert.FromBase64String(content));
            var lyrics = KrcParser.Parse(text, Id);
            if (lyrics.Lines.Count >= 3) return lyrics;
        }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
}

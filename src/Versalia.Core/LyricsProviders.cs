using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Versalia.Core;

public class LyricsProviderException : Exception
{
    public LyricsProviderException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Proveedor sustituible. Devuelve null si no tiene la letra; lanza si no pudo consultarlo.
/// Solo recibe título, artista, álbum y duración.</summary>
public interface ILyricsProvider
{
    string Id { get; }
    Task<Lyrics?> FetchAsync(TrackInfo track, CancellationToken ct = default);
}

/// <summary>Cliente HTTP mínimo para poder sustituirlo en los tests.</summary>
public interface IHttp
{
    Task<(string Body, int Status)> GetAsync(Uri url, IDictionary<string, string> headers, CancellationToken ct);
}

public sealed class HttpClientAdapter : IHttp
{
    private readonly HttpClient _client;
    public HttpClientAdapter(double timeoutSeconds = 8) =>
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    public async Task<(string Body, int Status)> GetAsync(Uri url, IDictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (body, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new LyricsProviderException("red: " + e.Message, e);
        }
    }
}

internal static class Url
{
    public static Uri Build(string baseUrl, string path, params (string Key, string Value)[] query) =>
        new($"{baseUrl.TrimEnd('/')}/{path}?" +
            string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}")));
}

/// <summary>LRCLIB: API pública sin clave. Da tiempos por línea; el motor los reparte por sílabas.</summary>
public sealed class LrclibProvider : ILyricsProvider
{
    public string Id => "lrclib";
    private readonly IHttp _http;
    private readonly string _baseUrl;
    private readonly string _clientName;
    private readonly double[] _retryDelays;

    public LrclibProvider(IHttp? http = null, string baseUrl = "https://lrclib.net",
        string clientName = "Versalia-Windows/0.1 (https://github.com/diego233l/Versalia-Windows)",
        double[]? retryDelays = null)
    {
        _http = http ?? new HttpClientAdapter();
        _baseUrl = baseUrl;
        _clientName = clientName;
        _retryDelays = retryDelays ?? new double[] { 1, 3 };
    }

    private sealed class Record
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool? Instrumental { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }

    public async Task<Lyrics?> FetchAsync(TrackInfo track, CancellationToken ct = default)
    {
        var direct = await GetAsync(track, ct).ConfigureAwait(false);
        if (direct != null && Accepts(direct, track))
        {
            var l = ToLyrics(direct, track);
            if (l != null) return l;
        }
        var candidates = (await SearchAsync(track, ct).ConfigureAwait(false))
            .Where(r => Accepts(r, track) && !string.IsNullOrEmpty(r.SyncedLyrics))
            .OrderBy(r => r.Duration.HasValue ? Math.Abs((int)(r.Duration.Value * 1000) - track.DurationMs) : int.MaxValue);
        foreach (var c in candidates)
        {
            var l = ToLyrics(c, track);
            if (l != null) return l;
        }
        return null;
    }

    private async Task<Record?> GetAsync(TrackInfo track, CancellationToken ct)
    {
        var q = new List<(string, string)> { ("track_name", track.Title), ("artist_name", track.Artist) };
        if (track.Album.Length > 0) q.Add(("album_name", track.Album));
        if (track.DurationMs > 0) q.Add(("duration", ((int)Math.Round(track.DurationMs / 1000.0)).ToString()));
        var (body, status) = await Request("api/get", q.ToArray(), ct).ConfigureAwait(false);
        if (status == 404) return null;
        if (status != 200) throw new LyricsProviderException($"LRCLIB respondió {status}");
        return Decode<Record>(body);
    }

    private async Task<List<Record>> SearchAsync(TrackInfo track, CancellationToken ct)
    {
        var (body, status) = await Request("api/search",
            new[] { ("track_name", track.Title), ("artist_name", track.Artist) }, ct).ConfigureAwait(false);
        if (status == 404) return new List<Record>();
        if (status != 200) throw new LyricsProviderException($"LRCLIB respondió {status}");
        return Decode<List<Record>>(body) ?? new List<Record>();
    }

    private async Task<(string Body, int Status)> Request(string path, (string, string)[] query, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["Lrclib-Client"] = _clientName, ["User-Agent"] = _clientName };
        var url = Url.Build(_baseUrl, path, query);
        var result = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
        foreach (var delay in _retryDelays)
        {
            if (result.Status < 500) break;
            await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            result = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
        }
        return result;
    }

    private static T? Decode<T>(string body)
    {
        try { return JsonSerializer.Deserialize<T>(body); }
        catch (JsonException e) { throw new LyricsProviderException("respuesta no válida: " + e.Message, e); }
    }

    private static bool Accepts(Record r, TrackInfo track) =>
        TrackMatcher.Matches(track, r.TrackName ?? "", r.ArtistName ?? "", r.Duration.HasValue ? (int)(r.Duration.Value * 1000) : null);

    private Lyrics? ToLyrics(Record r, TrackInfo track)
    {
        if (r.Instrumental == true || string.IsNullOrEmpty(r.SyncedLyrics)) return null;
        var lyrics = LrcParser.Parse(r.SyncedLyrics, track.DurationMs > 0 ? track.DurationMs : null, Id);
        return lyrics.IsEmpty ? null : lyrics;
    }
}

/// <summary>NetEase: para muchas canciones da tiempos reales por palabra (YRC). API no oficial; nunca lanza.</summary>
public sealed class NetEaseProvider : ILyricsProvider
{
    public string Id => "netease";
    private readonly IHttp _http;
    private readonly string _baseUrl;

    public NetEaseProvider(IHttp? http = null, string baseUrl = "https://music.163.com")
    {
        _http = http ?? new HttpClientAdapter(5);
        _baseUrl = baseUrl;
    }

    public async Task<Lyrics?> FetchAsync(TrackInfo track, CancellationToken ct = default)
    {
        try { return await Lookup(track, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private Task<(string Body, int Status)> Request(string path, (string, string)[] query, CancellationToken ct) =>
        _http.GetAsync(Url.Build(_baseUrl, path, query), new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            ["Referer"] = "https://music.163.com/",
        }, ct);

    private async Task<Lyrics?> Lookup(TrackInfo track, CancellationToken ct)
    {
        var (searchBody, searchStatus) = await Request("api/search/get",
            new[] { ("s", $"{track.Title} {track.Artist}"), ("type", "1"), ("limit", "10") }, ct).ConfigureAwait(false);
        if (searchStatus != 200) return null;
        using var doc = JsonDocument.Parse(searchBody);
        if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)) return null;

        var candidates = new List<(long Id, int Duration)>();
        foreach (var song in songs.EnumerateArray())
        {
            string name = song.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            int duration = song.TryGetProperty("duration", out var d) && d.TryGetInt32(out var di) ? di : 0;
            var artists = song.TryGetProperty("artists", out var a)
                ? string.Join(", ", a.EnumerateArray().Select(x => x.TryGetProperty("name", out var an) ? an.GetString() ?? "" : ""))
                : "";
            if (TrackMatcher.Matches(track, name, artists, duration))
                candidates.Add((song.GetProperty("id").GetInt64(), duration));
        }
        foreach (var c in candidates.OrderBy(c => Math.Abs(c.Duration - track.DurationMs)).Take(2))
        {
            var (body, status) = await Request("api/song/lyric/v1", new[]
            {
                ("id", c.Id.ToString()), ("cp", "false"), ("lv", "0"), ("tv", "0"), ("rv", "0"),
                ("kv", "0"), ("yv", "0"), ("ytv", "0"), ("yrv", "0"),
            }, ct).ConfigureAwait(false);
            if (status != 200) continue;
            using var lyricDoc = JsonDocument.Parse(body);
            if (!lyricDoc.RootElement.TryGetProperty("yrc", out var yrc) || !yrc.TryGetProperty("lyric", out var lyric)) continue;
            var text = lyric.GetString();
            if (string.IsNullOrEmpty(text)) continue;
            var lyrics = YrcParser.Parse(text, Id);
            if (lyrics.Lines.Count >= 3) return lyrics;
        }
        return null;
    }
}

/// <summary>Prueba varios proveedores en orden. Si ninguno la tiene y alguno falló, lanza (así no se cachea un "no existe" falso).</summary>
public sealed class CompositeLyricsProvider : ILyricsProvider
{
    public string Id => "composite";
    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly Action<string> _log;

    public CompositeLyricsProvider(IReadOnlyList<ILyricsProvider> providers, Action<string>? log = null)
    {
        _providers = providers;
        _log = log ?? (_ => { });
    }

    public async Task<Lyrics?> FetchAsync(TrackInfo track, CancellationToken ct = default)
    {
        Exception? first = null;
        foreach (var provider in _providers)
        {
            try
            {
                var lyrics = await provider.FetchAsync(track, ct).ConfigureAwait(false);
                if (lyrics != null)
                {
                    _log($"{provider.Id}: letra encontrada ({lyrics.Lines.Count} líneas, {(lyrics.IsEstimated ? "estimada" : "por palabra")})");
                    return lyrics;
                }
                _log($"{provider.Id}: sin letra para «{track.Title}»");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                _log($"{provider.Id}: error {e.Message}");
                first ??= e;
            }
        }
        if (first != null) throw first;
        return null;
    }
}

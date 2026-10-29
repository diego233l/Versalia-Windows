using System.Text.Json;
using System.Text.RegularExpressions;

namespace Versalia.Core;

/// <summary>
/// Letras de Apple Music con la sesión del propio usuario (su <c>media-user-token</c> de music.apple.com).
/// Son las mismas letras que ve la app, con tiempos por sílaba alineados con la versión que suena.
/// No es una API pública: si Apple la cambia, este proveedor deja de encontrar letras y se usan los demás. Nunca lanza.
/// </summary>
public sealed class AppleMusicProvider : ILyricsProvider
{
    public string Id => "applemusic";
    private const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
    private static readonly Regex ScriptPath = new(@"/assets/index[^""'\s>]*\.js", RegexOptions.Compiled);
    private static readonly Regex Jwt = new(@"eyJh[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.Compiled);

    private readonly Func<string?> _userToken;
    private readonly IHttp _http;
    private readonly Action<string> _log;
    private readonly string _web, _api;
    private string? _developerToken;
    private string? _storefront;
    private string? _storefrontFor;

    /// <summary>Último estado, para mostrarlo en el menú.</summary>
    public string Status { get; private set; } = "sin probar";

    public AppleMusicProvider(Func<string?> userToken, IHttp? http = null, Action<string>? log = null,
        string web = "https://music.apple.com", string api = "https://amp-api.music.apple.com")
    {
        _userToken = userToken;
        _http = http ?? new HttpClientAdapter(8);
        _log = log ?? (_ => { });
        _web = web.TrimEnd('/');
        _api = api.TrimEnd('/');
    }

    public async Task<Lyrics?> FetchAsync(TrackInfo track, CancellationToken ct = default)
    {
        var token = _userToken();
        if (string.IsNullOrWhiteSpace(token)) return null;
        try { return await Lookup(track, token.Trim(), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Status = "error: " + e.Message;
            _log("applemusic: " + e.Message);
            return null;
        }
    }

    private async Task<Lyrics?> Lookup(TrackInfo track, string userToken, CancellationToken ct)
    {
        if (_storefrontFor != userToken) { _storefront = null; _storefrontFor = userToken; }
        if (_storefront == null)
        {
            var (body, status) = await Api("v1/me/storefront", userToken, ct).ConfigureAwait(false);
            if (status is 401 or 403)
            {
                Status = "token rechazado (vuelve a conectar)";
                _log($"applemusic: Apple rechazó el token ({status}). Hay que copiarlo otra vez.");
                return null;
            }
            _storefront = status == 200 ? FirstId(body) ?? "us" : "us";
            _log($"applemusic: tienda «{_storefront}»");
        }

        var title = TrackMatcher.SearchTitle(track.Title);
        var (searchBody, searchStatus) = await Api(
            $"v1/catalog/{_storefront}/search?types=songs&limit=15&term={Uri.EscapeDataString($"{title} {track.Artist}".Trim())}",
            userToken, ct).ConfigureAwait(false);
        if (searchStatus != 200) { Status = $"búsqueda respondió {searchStatus}"; _log($"applemusic: búsqueda respondió {searchStatus}"); return null; }

        var candidates = new List<(string Id, int Duration)>();
        using (var doc = JsonDocument.Parse(searchBody))
        {
            if (doc.RootElement.TryGetProperty("results", out var results) && results.TryGetProperty("songs", out var songs)
                && songs.TryGetProperty("data", out var data))
            {
                foreach (var song in data.EnumerateArray())
                {
                    if (!song.TryGetProperty("id", out var idEl) || !song.TryGetProperty("attributes", out var a)) continue;
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string artist = a.TryGetProperty("artistName", out var ar) ? ar.GetString() ?? "" : "";
                    int duration = a.TryGetProperty("durationInMillis", out var d) && d.TryGetInt32(out var di) ? di : 0;
                    if (TrackMatcher.Matches(track, name, artist, duration)) candidates.Add((idEl.GetString() ?? "", duration));
                }
            }
        }
        _log($"applemusic: {candidates.Count} canciones coinciden con «{track.Title}»");

        foreach (var c in candidates.Where(c => c.Id.Length > 0).OrderBy(c => Math.Abs(c.Duration - track.DurationMs)).Take(3))
        {
            foreach (var kind in new[] { "syllable-lyrics", "lyrics" })
            {
                var (body, status) = await Api($"v1/catalog/{_storefront}/songs/{c.Id}/{kind}", userToken, ct).ConfigureAwait(false);
                if (status != 200) { if (status != 404) _log($"applemusic: {kind} de {c.Id} respondió {status}"); continue; }
                var ttml = Ttml(body);
                if (string.IsNullOrEmpty(ttml)) continue;
                var lyrics = TtmlParser.Parse(ttml, Id);
                if (lyrics.Lines.Count == 0) continue;
                Status = "conectado";
                return lyrics;
            }
        }
        Status = "conectado";
        return null;
    }

    /// <summary>Llama a la API con el token de desarrollador de la web (se renueva una vez si caduca).</summary>
    private async Task<(string Body, int Status)> Api(string pathAndQuery, string userToken, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var developer = _developerToken ??= await FetchDeveloperToken(ct).ConfigureAwait(false);
            var result = await _http.GetAsync(new Uri($"{_api}/{pathAndQuery}"), new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + developer,
                ["media-user-token"] = userToken,
                ["Origin"] = "https://music.apple.com",
                ["Referer"] = "https://music.apple.com/",
                ["User-Agent"] = Agent,
                ["Accept"] = "application/json",
            }, ct).ConfigureAwait(false);
            // Un 401 puede ser el token de la web caducado: se pide otro y se repite una vez.
            if (result.Status == 401 && attempt == 0) { _developerToken = null; continue; }
            return result;
        }
        throw new InvalidOperationException("inalcanzable");
    }

    /// <summary>El token de desarrollador va dentro del JavaScript de music.apple.com.</summary>
    private async Task<string> FetchDeveloperToken(CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["User-Agent"] = Agent };
        var (html, status) = await _http.GetAsync(new Uri($"{_web}/us/browse"), headers, ct).ConfigureAwait(false);
        if (status != 200) throw new LyricsProviderException($"music.apple.com respondió {status}");
        var direct = Jwt.Match(html);
        if (direct.Success) return direct.Value;
        foreach (var path in ScriptPath.Matches(html).Select(m => m.Value).Distinct()
                     .OrderBy(p => p.Contains("legacy", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            var (js, jsStatus) = await _http.GetAsync(new Uri(_web + path), headers, ct).ConfigureAwait(false);
            if (jsStatus != 200) continue;
            var m = Jwt.Match(js);
            if (m.Success) return m.Value;
        }
        throw new LyricsProviderException("no se encontró el token de la web de Apple Music");
    }

    private static string? FirstId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0
                && data[0].TryGetProperty("id", out var id)) return id.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static string? Ttml(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return null;
        if (!data[0].TryGetProperty("attributes", out var a)) return null;
        foreach (var key in new[] { "ttml", "ttmlLocalizations" })
            if (a.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s) return s;
        return null;
    }

    /// <summary>Acepta el valor tal cual o pegado como «media-user-token=...» o con comillas.</summary>
    public static string CleanToken(string pasted)
    {
        var t = pasted.Trim().Trim('"', '\'');
        const string prefix = "media-user-token";
        if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            t = t[prefix.Length..].TrimStart(' ', '=', ':', '\t').Trim();
        int semicolon = t.IndexOf(';');
        if (semicolon > 0) t = t[..semicolon];
        return t.Trim().Trim('"');
    }
}

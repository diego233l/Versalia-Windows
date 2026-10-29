using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Versalia.Core;
using Windows.Media.Control;

namespace Versalia.App
{
    /// <summary>
    /// Lee la reproducción de cualquier app que use los controles multimedia de Windows (Apple Music, Spotify, navegador...).
    /// Prefiere la app de Apple Music si está sonando. Consulta cada 600 ms; además reacciona a los eventos del sistema.
    /// </summary>
    internal sealed class MediaSessionSource : IDisposable
    {
        public event Action<PlaybackSnapshot, byte[]?>? Updated;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private volatile bool _wake;
        private string _artworkKey = "";
        private int _artworkTries;
        private double _lastRawPositionMs = -1;
        private DateTimeOffset _lastRawUpdated;

        public void Start() => _ = Task.Run(Loop);

        public void Dispose() => _cts.Cancel();

        private async Task Loop()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (_, _) => _wake = true;
                _manager.SessionsChanged += (_, _) => _wake = true;
                Log.Write("Controles multimedia de Windows listos.");
            }
            catch (Exception e)
            {
                Log.Write("No se pudo abrir los controles multimedia: " + e.Message);
                return;
            }

            while (!_cts.IsCancellationRequested)
            {
                try { await PollOnce(); }
                catch (Exception e) { Log.Write("Lectura del reproductor: " + e.Message); }
                for (int i = 0; i < 12 && !_wake && !_cts.IsCancellationRequested; i++) await Task.Delay(50);
                _wake = false;
            }
        }

        private string _lastSessionIds = "";
        private int _zeroDurationPolls;

        /// <summary>Solo Apple Music y Spotify; navegadores, vídeos y demás se ignoran.</summary>
        private static bool IsSupported(GlobalSystemMediaTransportControlsSession s) =>
            s.SourceAppUserModelId.IndexOf("AppleMusic", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.SourceAppUserModelId.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0;

        private GlobalSystemMediaTransportControlsSession? Choose()
        {
            var all = _manager!.GetSessions().ToList();
            var ids = string.Join(", ", all.Select(s => s.SourceAppUserModelId));
            if (ids != _lastSessionIds)
            {
                _lastSessionIds = ids;
                Log.Write("Reproductores del sistema: " + (ids.Length == 0 ? "(ninguno)" : ids));
            }
            var sessions = all.Where(IsSupported).ToList();
            return sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus ==
                       GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                   ?? sessions.FirstOrDefault();
        }

        private async Task PollOnce()
        {
            var session = Choose();
            if (session == null)
            {
                Updated?.Invoke(new PlaybackSnapshot(PlaybackState.Stopped, null, 0, Monotonic.Now), null);
                return;
            }

            var playback = session.GetPlaybackInfo();
            var state = playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
                _ => PlaybackState.Stopped,
            };

            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var title = System.Net.WebUtility.HtmlDecode(props.Title ?? "");
            var artist = System.Net.WebUtility.HtmlDecode(props.Artist ?? "");
            var album = System.Net.WebUtility.HtmlDecode(props.AlbumTitle ?? "");

            // Apple Music para Windows suele dar «Artista — Álbum» en el campo de artista.
            int dash = artist.IndexOf(" — ", StringComparison.Ordinal);
            if (dash > 0)
            {
                if (album.Length == 0) album = artist[(dash + 3)..];
                artist = artist[..dash];
            }

            double durationMs = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalMilliseconds);
            // Al cambiar de canción el reproductor tarda un momento en dar la duración: se espera un poco antes de
            // dar la pista por buena, para buscar la letra con la duración correcta.
            if (title.Length > 0 && durationMs <= 0 && _zeroDurationPolls < 4)
            {
                _zeroDurationPolls++;
                _wake = true;
                return;
            }
            _zeroDurationPolls = 0;
            double positionMs = Math.Max(0, (timeline.Position - timeline.StartTime).TotalMilliseconds);
            // Algunos reproductores (p. ej. vídeos del navegador) repiten la misma posición y marca de tiempo: no es una lectura nueva.
            bool fresh = positionMs != _lastRawPositionMs || timeline.LastUpdatedTime != _lastRawUpdated;
            _lastRawPositionMs = positionMs;
            _lastRawUpdated = timeline.LastUpdatedTime;
            if (state == PlaybackState.Playing)
            {
                double sinceUpdate = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds;
                if (sinceUpdate > 0) positionMs += sinceUpdate;
            }
            if (durationMs > 0) positionMs = Math.Min(positionMs, durationMs);

            // «Conectando…» (sin artista ni duración) es un estado transitorio de Apple Music, no una pista.
            TrackInfo? track = title.Length == 0 || (artist.Length == 0 && durationMs <= 0)
                ? null
                : new TrackInfo($"{title}|{artist}|{album}", title, artist, album, (int)durationMs);

            byte[]? artwork = null;
            if (track != null)
            {
                if (_artworkKey != track.Id) { _artworkKey = track.Id; _artworkTries = 0; }
                if (_artworkTries >= 0 && _artworkTries < 8)
                {
                    _artworkTries++;
                    artwork = await ReadThumbnail(props);
                    if (artwork != null) _artworkTries = -1;
                }
            }
            Updated?.Invoke(new PlaybackSnapshot(state, track, positionMs, Monotonic.Now, fresh), artwork);
        }

        private static async Task<byte[]?> ReadThumbnail(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            try
            {
                if (props.Thumbnail == null) return null;
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var net = stream.AsStreamForRead();
                using var memory = new MemoryStream();
                await net.CopyToAsync(memory);
                return memory.Length > 0 ? memory.ToArray() : null;
            }
            catch { return null; }
        }
    }
}

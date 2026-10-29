using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Versalia.Core;

namespace Versalia.App
{
    internal enum LyricsStatus { NoTrack, Loading, Ready, Missing, Error }

    /// <summary>Lo que el dibujo necesita saber en un instante.</summary>
    internal sealed class Frame
    {
        public TrackInfo? Track;
        public ImageSource? Artwork;
        public Lyrics? Lyrics;
        public WordStream? Words;
        public LyricsStatus Status;
        public bool Playing;
        public double PositionMs;
        public SyncPosition Sync = SyncPosition.None;
        public int LastStarted = -1;
        public Palette Palette = ThemePresets.Default;
        public long LyricsId;
        /// <summary>Aviso breve en la pantalla de la letra (calibración, ajuste aplicado...).</summary>
        public string? Notice;
        public double NoticeUntil;
    }

    /// <summary>Lo que la barra de sincronía puede consultar y cambiar.</summary>
    internal interface ISyncControls
    {
        int TrackOffsetMs { get; }
        bool OffsetIsLearned { get; }
        bool Calibrating { get; }
        void ChangeTrackOffset(int deltaMs);
        void Calibrate();
    }

    /// <summary>Une reproductor, letras, reloj y sincronía. Todo se llama desde el hilo de la interfaz.</summary>
    internal sealed class Director : ISyncControls
    {
        private readonly LyricsService _service;
        private readonly Settings _settings;
        private readonly InterpolatedClock _clock = new InterpolatedClock();
        private CancellationTokenSource? _fetch;
        private SyncCursor? _cursor;
        private readonly Frame _frame = new Frame();
        private Palette? _artPalette;
        private long _lyricsCounter;
        private int _trackOffset;
        private bool _offsetLearned;
        private string _player = "";
        private int _calibrationLine = -1;
        private double _calibrationStarted;
        private readonly AppleMusicProvider _apple;
        // Estadística de la deriva del reloj para el registro.
        private int _statReadings, _statCorrections, _statSeeks;
        private double _statAbsDrift, _statMaxDrift, _statLastLog;

        public Director(Settings settings)
        {
            _settings = settings;
            // Apple Music primero (si hay token): sus tiempos son los de la misma versión que suena.
            _apple = new AppleMusicProvider(AppleToken.Get, log: Log.Write);
            var provider = new CompositeLyricsProvider(
                new ILyricsProvider[] { _apple, new NetEaseProvider(log: Log.Write), new KugouProvider(log: Log.Write), new LrclibProvider() }, Log.Write);
            _service = new LyricsService(provider, new LyricsCache(Paths.Cache));
            _frame.Status = LyricsStatus.NoTrack;
        }

        public TrackInfo? Track => _frame.Track;
        public string AppleStatus => _apple.Status;

        /// <summary>Vuelve a buscar la letra de la canción actual (p. ej. tras conectar Apple Music).</summary>
        public void ReloadLyrics()
        {
            if (_frame.Track != null) StartTrack(_frame.Track);
        }

        public void OnSnapshot(PlaybackSnapshot snapshot, byte[]? artwork)
        {
            bool playing = snapshot.State == PlaybackState.Playing;
            _frame.Playing = playing;
            if (snapshot.Player.Length > 0 && snapshot.Player != _player)
            {
                _player = snapshot.Player;
                if (_frame.Track != null) LoadTrackOffset();
            }

            if (snapshot.Track == null)
            {
                if (_frame.Track != null) ClearTrack();
                return;
            }

            if (_frame.Track == null || _frame.Track.Id != snapshot.Track.Id)
            {
                Log.Write($"Pista: «{snapshot.Track.Title}» — {snapshot.Track.Artist} ({snapshot.Track.DurationMs / 1000} s)");
                StartTrack(snapshot.Track);
                _clock.Reset(snapshot.PositionMs, snapshot.CapturedAt, playing);
            }
            else if (!snapshot.PositionFresh && playing)
            {
                // Posición congelada: se sigue extrapolando con el reloj propio.
            }
            else
            {
                double predicted = _clock.PositionMs(snapshot.CapturedAt);
                var outcome = _clock.Observe(snapshot.PositionMs, snapshot.CapturedAt, playing, 60);
                if (outcome == InterpolatedClock.Outcome.Seek) { Log.Write("Salto en la pista, resincronizando."); _statSeeks++; }
                else if (playing)
                {
                    double drift = Math.Abs(snapshot.PositionMs - predicted);
                    _statReadings++;
                    _statAbsDrift += drift;
                    _statMaxDrift = Math.Max(_statMaxDrift, drift);
                    if (outcome == InterpolatedClock.Outcome.Corrected) _statCorrections++;
                }
                if (snapshot.CapturedAt - _statLastLog > 60 && _statReadings > 0)
                {
                    Log.Write($"Reloj: {_statReadings} lecturas, desviación media {_statAbsDrift / _statReadings:0} ms, " +
                              $"máx {_statMaxDrift:0} ms, {_statCorrections} correcciones, {_statSeeks} saltos.");
                    _statLastLog = snapshot.CapturedAt;
                    _statReadings = _statCorrections = _statSeeks = 0;
                    _statAbsDrift = _statMaxDrift = 0;
                }
            }
            if (artwork != null) SetArtwork(artwork);
        }

        private void ClearTrack()
        {
            _fetch?.Cancel();
            _frame.Track = null; _frame.Artwork = null; _frame.Lyrics = null; _frame.Words = null;
            _frame.Status = LyricsStatus.NoTrack;
            _artPalette = null;
            _cursor = null;
        }

        private void StartTrack(TrackInfo track)
        {
            _fetch?.Cancel();
            _fetch = new CancellationTokenSource();
            _frame.Track = track;
            _calibrationLine = -1;
            _frame.Notice = null;
            LoadTrackOffset();
            if (_trackOffset != 0)
                Log.Write($"Ajuste de sincronía {(_offsetLearned ? "aprendido" : "guardado para esta canción")}: {_trackOffset:+#;-#;0} ms");
            _frame.Artwork = null;
            _frame.Lyrics = null; _frame.Words = null;
            _frame.Status = LyricsStatus.Loading;
            _artPalette = null;
            _cursor = null;
            _ = LoadLyrics(track, _fetch.Token);
        }

        private async Task LoadLyrics(TrackInfo track, CancellationToken ct)
        {
            // Un fallo de red (p. ej. el 503 de LRCLIB) se reintenta con espera creciente.
            int[] retryDelaysSeconds = { 15, 45, 120 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var lyrics = await _service.LoadAsync(track, ct);
                    if (ct.IsCancellationRequested) return;
                    if (lyrics == null || lyrics.IsEmpty)
                    {
                        _frame.Status = LyricsStatus.Missing;
                        return;
                    }
                    _cursor = new SyncCursor(lyrics);
                    _frame.Words = new WordStream(lyrics);
                    _frame.Lyrics = lyrics;
                    _frame.LyricsId = ++_lyricsCounter;
                    _frame.Status = LyricsStatus.Ready;
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Log.Write("Error al cargar la letra: " + e.Message);
                    if (ct.IsCancellationRequested) return;
                    _frame.Status = LyricsStatus.Error;
                    if (attempt >= retryDelaysSeconds.Length) return;
                    try { await Task.Delay(TimeSpan.FromSeconds(retryDelaysSeconds[attempt]), ct); }
                    catch (OperationCanceledException) { return; }
                    _frame.Status = LyricsStatus.Loading;
                    Log.Write($"Reintentando la letra (intento {attempt + 2})…");
                }
            }
        }

        private void SetArtwork(byte[] bytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                _frame.Artwork = bitmap;
                _artPalette = ExtractPalette(bitmap);
            }
            catch (Exception e) { Log.Write("Portada no válida: " + e.Message); }
        }

        private static Palette? ExtractPalette(BitmapSource source)
        {
            try
            {
                const int size = 32;
                var scaled = new TransformedBitmap(source,
                    new ScaleTransform(size / (double)source.PixelWidth, size / (double)source.PixelHeight));
                var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth, h = converted.PixelHeight;
                var pixels = new byte[w * h * 4];
                converted.CopyPixels(pixels, w * 4, 0);
                return PaletteExtractor.FromBgra(pixels, w * h);
            }
            catch { return null; }
        }

        public void ChangeOffset(int deltaMs)
        {
            _settings.OffsetMs = deltaMs == 0 ? 0 : Math.Clamp(_settings.OffsetMs + deltaMs, -2000, 2000);
            _settings.Save();
        }

        private static string TrackKey(TrackInfo t) =>
            TrackMatcher.NormalizeTitle(t.Title) + "|" + TrackMatcher.NormalizeArtist(t.Artist);

        /// <summary>Ajuste propio de la canción si existe; si no, la mediana de los ajustes hechos con este reproductor.</summary>
        private void LoadTrackOffset()
        {
            if (_frame.Track == null) { _trackOffset = 0; _offsetLearned = false; return; }
            var key = TrackKey(_frame.Track);
            if (_settings.TrackOffsets.TryGetValue(key, out var own)) { _trackOffset = own; _offsetLearned = false; return; }
            var learned = SyncCalibration.Learned(_settings.OffsetSamples.Where(x => x.Player == _player && x.Key != key).Select(x => x.Ms));
            _trackOffset = learned ?? 0;
            _offsetLearned = learned.HasValue && learned.Value != 0;
        }

        /// <summary>Ajuste en uso para esta canción (se suma al general).</summary>
        public int TrackOffsetMs => _trackOffset;
        public bool OffsetIsLearned => _offsetLearned;
        public bool Calibrating => _calibrationLine >= 0 && Monotonic.Now - _calibrationStarted < 40;

        public void ChangeTrackOffset(int deltaMs)
        {
            if (_frame.Track == null) return;
            var key = TrackKey(_frame.Track);
            if (deltaMs == 0)
            {
                // Restablecer: la canción vuelve al ajuste aprendido.
                _settings.TrackOffsets.Remove(key);
                _settings.OffsetSamples.RemoveAll(x => x.Key == key);
                LoadTrackOffset();
            }
            else
            {
                int value = Math.Clamp(_trackOffset + deltaMs, -SyncCalibration.MaxCorrectionMs, SyncCalibration.MaxCorrectionMs);
                _settings.TrackOffsets[key] = value;
                _settings.OffsetSamples.RemoveAll(x => x.Key == key && x.Player == _player);
                _settings.OffsetSamples.Add(new OffsetSample { Player = _player, Key = key, Ms = value });
                while (_settings.OffsetSamples.Count > 40) _settings.OffsetSamples.RemoveAt(0);
                _trackOffset = value;
                _offsetLearned = false;
            }
            _settings.Save();
            Log.Write($"Sincronía de «{_frame.Track.Title}»: {_trackOffset:+#;-#;0} ms{(_offsetLearned ? " (aprendido)" : "")}");
        }

        private double DisplayedPosition(double now) => _clock.PositionMs(now) + _settings.OffsetMs + _trackOffset;

        private void Notice(string text, double seconds)
        {
            _frame.Notice = text;
            _frame.NoticeUntil = Monotonic.Now + seconds;
        }

        /// <summary>
        /// Primera pulsación: elige una línea que aún no ha sonado y la muestra. Segunda pulsación (cuando se oye empezar esa
        /// línea): calcula cuánto va desfasada la letra y lo guarda para esta canción y como referencia para las demás.
        /// </summary>
        public void Calibrate()
        {
            double now = Monotonic.Now;
            var lyrics = _frame.Lyrics;
            if (_frame.Status != LyricsStatus.Ready || lyrics == null) { Notice("Todavía no hay letra para sincronizar", 3); return; }
            double position = DisplayedPosition(now);
            if (!Calibrating || _calibrationLine >= lyrics.Lines.Count)
            {
                int target = SyncCalibration.TargetLine(lyrics, position);
                if (target < 0) { Notice("No quedan líneas en esta canción para sincronizar", 3); return; }
                _calibrationLine = target;
                _calibrationStarted = now;
                Notice($"Pulsa otra vez (Espacio o Ctrl+Alt+S) justo cuando oigas: «{lyrics.Lines[target].Text}»", 40);
                Log.Write($"Calibración: esperando la línea {target} («{lyrics.Lines[target].Text}», {lyrics.Lines[target].StartMs} ms)");
                return;
            }
            var line = lyrics.Lines[_calibrationLine];
            int correction = SyncCalibration.TapCorrection(line.StartMs, position);
            _calibrationLine = -1;
            Log.Write($"Calibración: pulsado en {position:0} ms, la línea empieza en {line.StartMs} ms → corrección {correction:+#;-#;0} ms");
            if (Math.Abs(correction) > SyncCalibration.MaxCorrectionMs)
            {
                Notice("Demasiado desfase: pulsa Espacio para probar otra vez", 4);
                return;
            }
            ChangeTrackOffset(correction == 0 ? 1 : correction);
            Notice($"Sincronizado: {Format(_trackOffset)}. Se recordará para esta canción y servirá de base para las demás.", 5);
        }

        private static string Format(int ms) => ms == 0 ? "0 s" : $"{(ms > 0 ? "+" : "−")}{Math.Abs(ms) / 1000.0:0.00} s";

        public Frame Tick(double now)
        {
            double position = DisplayedPosition(now);
            if (_frame.Notice != null && now > _frame.NoticeUntil) _frame.Notice = null;
            _frame.PositionMs = position;
            if (_frame.Status == LyricsStatus.Ready && _cursor != null && _frame.Words != null)
            {
                _frame.Sync = _cursor.Position(position);
                _frame.LastStarted = _frame.Words.LastStartedIndex(position);
            }
            else
            {
                _frame.Sync = SyncPosition.None;
                _frame.LastStarted = -1;
            }
            if (_settings.Palette != "auto" && ThemePresets.All.TryGetValue(_settings.Palette, out var preset))
                _frame.Palette = preset;
            else
                _frame.Palette = _artPalette ?? ThemePresets.Default;
            return _frame;
        }
    }
}

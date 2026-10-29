using System;
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
    }

    /// <summary>Une reproductor, letras, reloj y sincronía. Todo se llama desde el hilo de la interfaz.</summary>
    internal sealed class Director
    {
        private readonly LyricsService _service;
        private readonly Settings _settings;
        private readonly InterpolatedClock _clock = new InterpolatedClock();
        private CancellationTokenSource? _fetch;
        private SyncCursor? _cursor;
        private readonly Frame _frame = new Frame();
        private Palette? _artPalette;
        private long _lyricsCounter;

        public Director(Settings settings)
        {
            _settings = settings;
            var provider = new CompositeLyricsProvider(
                new ILyricsProvider[] { new NetEaseProvider(log: Log.Write), new LrclibProvider() }, Log.Write);
            _service = new LyricsService(provider, new LyricsCache(Paths.Cache));
            _frame.Status = LyricsStatus.NoTrack;
        }

        public TrackInfo? Track => _frame.Track;

        public void OnSnapshot(PlaybackSnapshot snapshot, byte[]? artwork)
        {
            bool playing = snapshot.State == PlaybackState.Playing;
            _frame.Playing = playing;

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
            else
            {
                var outcome = _clock.Observe(snapshot.PositionMs, snapshot.CapturedAt, playing, 60);
                if (outcome == InterpolatedClock.Outcome.Seek) Log.Write("Salto en la pista, resincronizando.");
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

        public Frame Tick(double now)
        {
            double position = _clock.PositionMs(now) + _settings.OffsetMs;
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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Versalia.Core;

namespace Versalia.App
{
    internal enum SceneMode { Stage, Overlay }

    /// <summary>
    /// Toda la escena «Dúo» en coordenadas del escritorio virtual (píxeles físicos). Cada ventana dibuja solo su trozo,
    /// así que un cometa que sale de una pantalla cruza el borde y entra en la otra sin cortes.
    ///  - Pantalla grande (la 4K): portada, título, constelación de la frase y polvo de estrellas.
    ///  - Pantalla secundaria (la 1080p): la letra palabra a palabra.
    ///  - Cada palabra cantada lanza un cometa desde su estrella hasta la palabra en la otra pantalla.
    /// Con un solo monitor todo se junta en una pantalla.
    /// </summary>
    internal sealed partial class Scene
    {
        private sealed class Glyph { public Geometry Geo = Geometry.Empty; public double Width, Height; }
        private sealed class Comet { public Point From, To; public double Start; public double Duration = 0.85; }
        private sealed class Ripple { public Point At; public double Start; }
        private sealed class LineLayout
        {
            public List<Rect> Boxes = new List<Rect>();
            public List<Glyph> Glyphs = new List<Glyph>();
            public Rect Block;
        }

        private static readonly Typeface Heavy = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Normal);
        private static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        // Estado
        private Frame _f = new Frame();
        private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
        private SceneMode _mode = SceneMode.Stage;
        private int _overlayIndex = -1;
        private double _now, _prevNow, _dt;
        private MonitorInfo? _stage, _lyric;
        private Rect _desk;
        private string _layoutSignature = "";
        private Rgb _text = ThemePresets.Default.Text, _hi = ThemePresets.Default.Highlight,
            _sec = ThemePresets.Default.Secondary, _bg = ThemePresets.Default.Background;
        private double _energy;
        private int _lastStarted = -1;
        private long _lyricsId = -1;
        private int _lastLine = -2;
        private double _lineChangedAt;
        private readonly List<Comet> _comets = new List<Comet>();
        private readonly List<Ripple> _ripples = new List<Ripple>();
        private readonly Dictionary<int, double> _groupAlpha = new Dictionary<int, double>();
        private readonly Dictionary<int, List<StarPoint>> _points = new Dictionary<int, List<StarPoint>>();
        private readonly Dictionary<int, List<(int, int)>> _edges = new Dictionary<int, List<(int, int)>>();
        private readonly Dictionary<string, Glyph> _glyphs = new Dictionary<string, Glyph>();
        private readonly Dictionary<int, LineLayout> _layouts = new Dictionary<int, LineLayout>();
        private double _avgDt = 0.0167;
        public double Quality { get; private set; } = 1;

        /// <summary>Distingue monitores que han cambiado de tamaño o de sitio.</summary>
        private static string Signature(IReadOnlyList<MonitorInfo> ms, SceneMode mode, int overlay) =>
            mode + "|" + overlay + "|" + string.Join(";", ms.Select(m => $"{m.X},{m.Y},{m.Width},{m.Height}"));

        // ───────────────────────── Actualización ─────────────────────────

        public void Update(Frame f, IReadOnlyList<MonitorInfo> monitors, SceneMode mode, int overlayIndex, double now)
        {
            _f = f;
            _prevNow = _now == 0 ? now : _now;
            _now = now;
            _dt = Math.Min(0.1, Math.Max(0, _now - _prevNow));
            _monitors = monitors;
            _mode = mode;
            _overlayIndex = overlayIndex;

            // Los roles se recalculan siempre: las ventanas se recrean con objetos de monitor nuevos al abrir y cerrar el modo.
            AssignRoles();
            var signature = Signature(monitors, mode, overlayIndex);
            if (signature != _layoutSignature)
            {
                _layoutSignature = signature;
                _layouts.Clear(); _groupLayouts.Clear();
                _glyphs.Clear();
            }

            // Calidad adaptativa: si los fotogramas tardan, menos partículas y estela.
            _avgDt += (_dt - _avgDt) * Math.Min(1, _dt * 2);
            if (_avgDt > 0.026) Quality = Math.Max(0.25, Quality - _dt * 0.25);
            else if (_avgDt < 0.019) Quality = Math.Min(1, Quality + _dt * 0.05);

            double k = 1 - Math.Exp(-_dt * 2.5);
            var target = f.Palette;
            _text = _text.Mix(target.Text, k); _hi = _hi.Mix(target.Highlight, k);
            _sec = _sec.Mix(target.Secondary, k); _bg = _bg.Mix(target.Background, k);
            _energy *= Math.Exp(-_dt * 3.5);

            if (f.LyricsId != _lyricsId)
            {
                _lyricsId = f.LyricsId;
                _lastStarted = f.LastStarted;
                _lastLine = -2;
                _comets.Clear(); _ripples.Clear(); _groupAlpha.Clear();
                _points.Clear(); _edges.Clear(); _layouts.Clear(); _groupLayouts.Clear();
                if (_glyphs.Count > 400) _glyphs.Clear();
            }

            if (f.Sync.LineIndex != _lastLine)
            {
                _lastLine = f.Sync.LineIndex;
                _lineChangedAt = now;
            }

            // Palabra nueva: cometa desde su estrella hasta la palabra de la letra.
            if (f.LastStarted != _lastStarted && f.Words != null)
            {
                int from = _lastStarted, to = f.LastStarted;
                if (to > from && to - from <= 3 && f.Playing && _mode == SceneMode.Stage)
                    for (int i = Math.Max(from + 1, to - 1); i <= to; i++) SpawnComet(i);
                if (to > from) _energy = 1;
                _lastStarted = to;
            }

            UpdateGroups();

            for (int i = _comets.Count - 1; i >= 0; i--)
            {
                if (_now - _comets[i].Start >= _comets[i].Duration)
                {
                    _ripples.Add(new Ripple { At = _comets[i].To, Start = _now });
                    _energy = Math.Min(1.4, _energy + 0.4);
                    _comets.RemoveAt(i);
                }
            }
            _ripples.RemoveAll(r => _now - r.Start > 0.8);
        }

        private void AssignRoles()
        {
            if (_monitors.Count == 0) { _stage = _lyric = null; _desk = Rect.Empty; return; }
            _desk = _monitors[0].Rect;
            foreach (var m in _monitors) _desk = Rect.Union(_desk, m.Rect);
            (_stage, _lyric) = Roles(_monitors);
        }

        /// <summary>Pantalla grande = escenario; la siguiente más grande = letra (la misma si solo hay una).</summary>
        public static (MonitorInfo Stage, MonitorInfo Lyric) Roles(IReadOnlyList<MonitorInfo> monitors)
        {
            var stage = monitors.OrderByDescending(m => m.Area).ThenBy(m => m.X).First();
            var lyric = monitors.Where(m => m.Index != stage.Index).OrderByDescending(m => m.Area).ThenByDescending(m => m.X).FirstOrDefault() ?? stage;
            return (stage, lyric);
        }

        private bool Dual => _stage != null && _lyric != null && _stage != _lyric;

        private Rect ConstellationRegion()
        {
            if (_stage == null) return Rect.Empty;
            var s = _stage;
            if (Dual) return new Rect(s.X + s.Width * 0.40, s.Y + s.Height * 0.12, s.Width * 0.56, s.Height * 0.76);
            return new Rect(s.X + s.Width * 0.06, s.Y + s.Height * 0.10, s.Width * 0.88, s.Height * 0.80);
        }

        private List<StarPoint> PointsFor(int group, int count)
        {
            if (!_points.TryGetValue(group, out var list)) _points[group] = list = ConstellationLayout.Points(group, count);
            return list;
        }

        private Point StarPosition(WordStream.Entry e)
        {
            var region = ConstellationRegion();
            var pts = PointsFor(e.GroupId, e.GroupSize);
            var p = pts[Math.Min(e.GroupOrdinal, pts.Count - 1)];
            return new Point(region.X + p.X * region.Width, region.Y + p.Y * region.Height);
        }

        private void SpawnComet(int entryIndex)
        {
            var words = _f.Words; var lyrics = _f.Lyrics;
            if (words == null || lyrics == null || _lyric == null || entryIndex < 0 || entryIndex >= words.Entries.Count) return;
            var entry = words.Entries[entryIndex];
            Point target;
            if (Style == "duo")
            {
                var layout = LayoutFor(entry.LineIndex);
                if (layout == null || entry.WordIndex >= layout.Boxes.Count) return;
                var box = layout.Boxes[entry.WordIndex];
                target = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            }
            else target = StyleTarget(entryIndex);
            _comets.Add(new Comet
            {
                From = StarPosition(entry),
                To = target,
                Start = _now,
                Duration = Dual ? 0.9 : 0.6,
            });
            if (_comets.Count > 8) _comets.RemoveAt(0);
        }

        private void UpdateGroups()
        {
            var words = _f.Words;
            if (words == null || words.IsEmpty) { _groupAlpha.Clear(); return; }
            int active;
            if (_f.LastStarted >= 0) active = words.Entries[_f.LastStarted].GroupId;
            else if (words.Entries[0].StartMs - _f.PositionMs < 1500) active = words.Entries[0].GroupId;
            else active = int.MinValue;
            double visibility = words.Visibility(_f.LastStarted, _f.PositionMs);
            if (active != int.MinValue && !_groupAlpha.ContainsKey(active)) _groupAlpha[active] = 0;
            foreach (var key in _groupAlpha.Keys.ToList())
            {
                double target = key == active ? visibility : 0;
                double a = _groupAlpha[key];
                a += (target - a) * Math.Min(1, _dt * (target > a ? 8 : 3));
                if (a < 0.01 && key != active) _groupAlpha.Remove(key); else _groupAlpha[key] = a;
            }
        }

        // ───────────────────────── Utilidades de dibujo ─────────────────────────

        private static Color C(Rgb c, double alpha = 1) => Color.FromArgb(
            (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(c.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.G, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(c.B, 0, 1) * 255));

        private static Brush B(Rgb c, double alpha = 1) { var b = new SolidColorBrush(C(c, alpha)); b.Freeze(); return b; }

        private static double Ease(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        private static double Hash(int i, int k) { double v = Math.Sin(i * 127.1 + k * 311.7) * 43758.5453; return v - Math.Floor(v); }

        private Glyph GlyphFor(string text, double size, bool heavy = false)
        {
            int rounded = Math.Max(6, (int)Math.Round(size));
            string key = (heavy ? "h" : "n") + rounded + ":" + text;
            if (_glyphs.TryGetValue(key, out var g)) return g;
            if (_glyphs.Count > 900) _glyphs.Clear();
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, heavy ? Heavy : Face, rounded, Brushes.White, 1.0);
            var geo = ft.BuildGeometry(new Point(0, 0)) ?? Geometry.Empty;
            if (geo.CanFreeze) geo.Freeze();
            g = new Glyph { Geo = geo, Width = ft.WidthIncludingTrailingWhitespace, Height = ft.Height };
            _glyphs[key] = g;
            return g;
        }

        private static void DrawGlyph(DrawingContext dc, Glyph g, double x, double y, Brush fill, Pen? outline = null, double scale = 1)
        {
            dc.PushTransform(new TranslateTransform(x, y));
            if (Math.Abs(scale - 1) > 0.001) dc.PushTransform(new ScaleTransform(scale, scale, g.Width / 2, g.Height / 2));
            dc.DrawGeometry(fill, outline, g.Geo);
            if (Math.Abs(scale - 1) > 0.001) dc.Pop();
            dc.Pop();
        }

        private static void DrawCentered(DrawingContext dc, Glyph g, double cx, double y, Brush fill, double maxWidth, Pen? outline = null)
        {
            double scale = g.Width > maxWidth && g.Width > 0 ? maxWidth / g.Width : 1;
            dc.PushTransform(new TranslateTransform(cx, y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-g.Width / 2, 0));
            dc.DrawGeometry(fill, outline, g.Geo);
            dc.Pop(); dc.Pop(); dc.Pop();
        }

        private static RadialGradientBrush Glow(Point center, double radius, Rgb color, double alpha)
        {
            var brush = new RadialGradientBrush(C(color, alpha), C(color, 0))
            {
                MappingMode = BrushMappingMode.Absolute, Center = center, GradientOrigin = center,
                RadiusX = radius, RadiusY = radius,
            };
            brush.Freeze();
            return brush;
        }

        // ───────────────────────── Letra: composición ─────────────────────────

        private Rect LyricArea()
        {
            var m = _mode == SceneMode.Overlay ? OverlayMonitor() : _lyric;
            return m == null ? Rect.Empty : m.Rect;
        }

        private MonitorInfo? OverlayMonitor()
        {
            if (_monitors.Count == 0) return null;
            if (_overlayIndex >= 0 && _overlayIndex < _monitors.Count) return _monitors[_overlayIndex];
            return _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors[0];
        }

        private LineLayout? LayoutFor(int lineIndex)
        {
            var lyrics = _f.Lyrics;
            if (lyrics == null || lineIndex < 0 || lineIndex >= lyrics.Lines.Count) return null;
            if (_layouts.TryGetValue(lineIndex, out var cached)) return cached;
            var area = LyricArea();
            if (area.IsEmpty) return null;
            var line = lyrics.Lines[lineIndex];
            double size = _mode == SceneMode.Overlay ? area.Height * 0.05 : area.Height * 0.078;
            double maxWidth = area.Width * (_mode == SceneMode.Overlay ? 0.8 : 0.84);
            double cy = _mode == SceneMode.Overlay ? area.Y + area.Height * 0.86 : area.Y + area.Height * 0.47;

            LineLayout layout = new LineLayout();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                layout = new LineLayout();
                var glyphs = line.Words.Select(w => GlyphFor(w.Text, size)).ToList();
                double space = size * 0.3;
                var rows = new List<List<int>> { new List<int>() };
                double rowWidth = 0;
                for (int i = 0; i < glyphs.Count; i++)
                {
                    double add = glyphs[i].Width + (rows[^1].Count > 0 ? space : 0);
                    if (rows[^1].Count > 0 && rowWidth + add > maxWidth) { rows.Add(new List<int>()); rowWidth = 0; add = glyphs[i].Width; }
                    rows[^1].Add(i);
                    rowWidth += add;
                }
                if (rows.Count <= 2 || attempt == 3) { layout.Glyphs = glyphs; BuildBoxes(layout, rows, glyphs, size, space, area, cy); break; }
                size *= 0.82;
            }
            _layouts[lineIndex] = layout;
            return layout;
        }

        private static void BuildBoxes(LineLayout layout, List<List<int>> rows, List<Glyph> glyphs, double size, double space, Rect area, double cy)
        {
            double rowHeight = size * 1.25;
            double top = cy - rowHeight * rows.Count / 2;
            var boxes = new Rect[glyphs.Count];
            double minX = double.MaxValue, maxX = double.MinValue;
            for (int r = 0; r < rows.Count; r++)
            {
                double width = rows[r].Sum(i => glyphs[i].Width) + space * (rows[r].Count - 1);
                double x = area.X + (area.Width - width) / 2;
                foreach (int i in rows[r])
                {
                    boxes[i] = new Rect(x, top + r * rowHeight, glyphs[i].Width, glyphs[i].Height);
                    x += glyphs[i].Width + space;
                }
                minX = Math.Min(minX, area.X + (area.Width - width) / 2);
                maxX = Math.Max(maxX, area.X + (area.Width + width) / 2);
            }
            layout.Boxes = boxes.ToList();
            layout.Block = new Rect(minX, top, Math.Max(0, maxX - minX), rowHeight * rows.Count);
        }

        // ───────────────────────── Dibujo por monitor ─────────────────────────

        public void Render(DrawingContext dc, MonitorInfo mon)
        {
            if (_mode == SceneMode.Overlay)
            {
                if (OverlayMonitor()?.Index == mon.Index) DrawOverlay(dc, mon);
                return;
            }

            var rect = mon.Rect;
            dc.DrawRectangle(B(_bg), null, rect);
            DrawBackdrop(dc, mon);

            if (_lyric == null || _stage == null) return;
            bool dual = Dual;
            if (mon.Index == _stage.Index) { DrawConstellation(dc, mon, dual ? 1.0 : 0.55, labels: dual); }
            if (dual && mon.Index == _stage.Index) DrawCoverPanel(dc, mon);
            if (mon.Index == _lyric.Index) DrawLyrics(dc, mon);
            DrawCometsAndRipples(dc, mon);
        }

        private void DrawBackdrop(DrawingContext dc, MonitorInfo mon)
        {
            double t = _now;
            double unit = Math.Max(1, _desk.Height / 1080);
            double radius = Math.Max(_desk.Height * 1.05, _desk.Width * 0.45);
            var c1 = new Point(_desk.X + _desk.Width * (0.5 + 0.38 * Math.Sin(t * 0.11)), _desk.Y + _desk.Height * (0.5 + 0.30 * Math.Cos(t * 0.083)));
            var c2 = new Point(_desk.X + _desk.Width * (0.5 + 0.40 * Math.Cos(t * 0.07 + 1)), _desk.Y + _desk.Height * (0.5 + 0.32 * Math.Sin(t * 0.093 + 2)));
            dc.DrawRectangle(Glow(c1, radius, _hi, 0.30 + 0.14 * Math.Min(1, _energy)), null, mon.Rect);
            dc.DrawRectangle(Glow(c2, radius * 0.9, _sec, 0.22 + 0.08 * Math.Min(1, _energy)), null, mon.Rect);

            // Polvo de estrellas que sube por todo el escritorio.
            int count = (int)(90 * Quality);
            var rect = mon.Rect;
            for (int i = 0; i < count; i++)
            {
                double speed = 0.4 + Hash(i, 3);
                double x = _desk.X + ((Hash(i, 1) + t * 0.006 * speed * (i % 2 == 0 ? 1 : -1)) % 1 + 1) % 1 * _desk.Width;
                double y = _desk.Bottom - ((Hash(i, 2) + t * 0.012 * speed) % 1) * _desk.Height;
                if (x < rect.Left - 8 || x > rect.Right + 8 || y < rect.Top - 8 || y > rect.Bottom + 8) continue;
                double r = (1.2 + Hash(i, 4) * 3.2) * unit * (1 + 0.5 * Math.Min(1, _energy));
                double twinkle = 0.5 + 0.5 * Math.Sin(t * (0.8 + Hash(i, 5) * 2) + i);
                dc.DrawEllipse(B(i % 3 == 0 ? _sec : _hi, 0.10 + 0.35 * twinkle), null, new Point(x, y), r, r);
            }
        }

        private void DrawCoverPanel(DrawingContext dc, MonitorInfo s)
        {
            double size = s.Height * 0.30 * (1 + 0.012 * Math.Min(1, _energy));
            double x = s.X + s.Width * 0.07, y = s.Y + s.Height * 0.5 - size * 0.62;
            var cover = new Rect(x, y, size, size);
            var center = new Point(x + size / 2, y + size / 2);
            dc.DrawEllipse(Glow(center, size * 0.95, _hi, 0.45 + 0.2 * Math.Min(1, _energy)), null, center, size * 0.95, size * 0.95);
            double radius = size * 0.05;
            if (_f.Artwork != null)
            {
                var brush = new ImageBrush(_f.Artwork) { Stretch = Stretch.UniformToFill };
                dc.DrawRoundedRectangle(brush, null, cover, radius, radius);
            }
            else
            {
                var fill = new LinearGradientBrush(C(_hi), C(_sec), 45);
                dc.DrawRoundedRectangle(fill, null, cover, radius, radius);
                var note = GlyphFor("♪", size * 0.5);
                DrawGlyph(dc, note, center.X - note.Width / 2, center.Y - note.Height / 2, B(_bg, 0.8));
            }
            if (_f.Track != null)
            {
                double maxW = size * 1.25;
                var title = GlyphFor(_f.Track.Title, s.Height * 0.036);
                var artist = GlyphFor(_f.Track.Artist, s.Height * 0.027);
                DrawCentered(dc, title, center.X, cover.Bottom + s.Height * 0.035, B(_text), maxW);
                DrawCentered(dc, artist, center.X, cover.Bottom + s.Height * 0.035 + title.Height * 1.1, B(_sec, 0.9), maxW);
            }
        }

        private void DrawConstellation(DrawingContext dc, MonitorInfo mon, double strength, bool labels)
        {
            var words = _f.Words;
            if (words == null || words.IsEmpty) return;
            var region = ConstellationRegion();
            double unit = Math.Max(1, mon.Height / 1080.0);
            foreach (var kv in _groupAlpha)
            {
                double alpha = kv.Value * strength;
                if (alpha < 0.01 || !words.Groups.TryGetValue(kv.Key, out var range)) continue;
                var pts = PointsFor(kv.Key, range.Count);
                if (!_edges.TryGetValue(kv.Key, out var edges)) _edges[kv.Key] = edges = ConstellationLayout.Edges(kv.Key, range.Count);
                Point P(int local) => new Point(region.X + pts[local].X * region.Width, region.Y + pts[local].Y * region.Height);

                foreach (var (a, b) in edges)
                {
                    if (a >= range.Count || b >= range.Count) continue;
                    double grow = Ease((_f.PositionMs - words.Entries[range.First + b].StartMs) / 350.0);
                    if (grow <= 0) continue;
                    var pa = P(a); var pb = P(b);
                    var end = new Point(pa.X + (pb.X - pa.X) * grow, pa.Y + (pb.Y - pa.Y) * grow);
                    var pen = new Pen(B(_hi, 0.55 * alpha), 2.2 * unit) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawLine(pen, pa, end);
                }
                for (int local = 0; local < range.Count; local++)
                {
                    var entry = words.Entries[range.First + local];
                    var p = P(local);
                    double lit = Ease((_f.PositionMs - entry.StartMs) / 250.0);
                    bool active = range.First + local == _f.LastStarted;
                    double r = (4.5 + 5 * lit + (active ? 5 * Math.Min(1, _energy) : 0)) * unit;
                    if (lit > 0.01) dc.DrawEllipse(Glow(p, r * 5.5, _hi, 0.55 * lit * alpha), null, p, r * 5.5, r * 5.5);
                    dc.DrawEllipse(B(lit > 0.01 ? _text : _sec, (0.25 + 0.75 * lit) * alpha), null, p, r, r);
                    if (labels && lit > 0.01)
                    {
                        var g = GlyphFor(entry.Text, mon.Height * (active ? 0.036 : 0.030));
                        double lx = p.X + r * 2.2; double ly = p.Y - g.Height / 2;
                        if (lx + g.Width > region.Right + mon.Width * 0.02) lx = p.X - r * 2.2 - g.Width;
                        DrawGlyph(dc, g, lx, ly, B(active ? _hi : _text, (0.35 + 0.65 * lit) * alpha * (active ? 1 : 0.8)));
                    }
                }
            }
        }

        private void DrawLyrics(DrawingContext dc, MonitorInfo mon)
        {
            double size = mon.Height * 0.078;
            double cx = mon.X + mon.Width / 2;
            double maxW = mon.Width * 0.86;
            var f = _f;
            if (f.Status != LyricsStatus.Ready || f.Lyrics == null)
            {
                DrawIdle(dc, mon, cx, maxW);
                return;
            }
            if (Style != "duo") { DrawStyle(dc, mon); return; }
            var lines = f.Lyrics.Lines;
            int current = Math.Max(0, f.Sync.LineIndex);
            double since = Ease((_now - _lineChangedAt) / 0.45);

            // Progreso de la pista, fino, abajo.
            if (f.Track != null && f.Track.DurationMs > 0)
            {
                double w = mon.Width * 0.5, h = Math.Max(3, mon.Height * 0.004);
                double x = cx - w / 2, y = mon.Bottom - mon.Height * 0.06;
                dc.DrawRoundedRectangle(B(_text, 0.15), null, new Rect(x, y, w, h), h / 2, h / 2);
                double p = Math.Clamp(f.PositionMs / f.Track.DurationMs, 0, 1);
                dc.DrawRoundedRectangle(B(_hi, 0.85), null, new Rect(x, y, Math.Max(h, w * p), h), h / 2, h / 2);
            }

            var layout = LayoutFor(current);
            if (layout == null) return;

            // Líneas vecinas (más pequeñas y apagadas), con un pequeño deslizamiento al cambiar de línea.
            double small = size * 0.5;
            double slide = (1 - since) * size * 0.6;
            if (current - 1 >= 0)
            {
                var g = GlyphFor(lines[current - 1].Text, small);
                DrawCentered(dc, g, cx, layout.Block.Top - small * 1.9 + slide, B(_text, 0.28), maxW);
            }
            for (int n = 1; n <= 2 && current + n < lines.Count; n++)
            {
                var g = GlyphFor(lines[current + n].Text, small);
                double y = layout.Block.Bottom + small * 0.6 + (n - 1) * small * 1.45 + slide;
                DrawCentered(dc, g, cx, y, B(_text, n == 1 ? 0.5 : 0.24), maxW);
            }

            // Línea actual, palabra a palabra.
            bool started = f.Sync.LineIndex >= 0;
            double lineAlpha = started ? 0.55 + 0.45 * since : 0.35;
            DrawWordLine(dc, layout, f.Sync.WordIndex, f.Sync.WordProgress, f.Sync.IsLineFinished, started, lineAlpha, shadow: false, glowUnit: mon.Height / 1080.0);
        }

        private void DrawWordLine(DrawingContext dc, LineLayout layout, int activeWord, double progress, bool finished, bool started,
            double alpha, bool shadow, double glowUnit)
        {
            for (int i = 0; i < layout.Boxes.Count; i++)
            {
                var box = layout.Boxes[i]; var g = layout.Glyphs[i];
                bool sung = started && (finished || i < activeWord);
                bool active = started && !finished && i == activeWord;
                if (shadow)
                    DrawGlyph(dc, g, box.X + 3 * glowUnit, box.Y + 3 * glowUnit, B(new Rgb(0, 0, 0), 0.6 * alpha));
                double pulse = active ? 1 + 0.05 * Math.Sin(Math.Min(1, progress) * Math.PI) : 1;
                DrawGlyph(dc, g, box.X, box.Y, B(_text, 0.38 * alpha), null, pulse);
                if (sung)
                    DrawGlyph(dc, g, box.X, box.Y, B(_text.Mix(_hi, 0.55), alpha));
                else if (active && progress > 0)
                {
                    var halo = new Pen(B(_hi, 0.30 * alpha), 6 * glowUnit) { LineJoin = PenLineJoin.Round };
                    dc.PushClip(new RectangleGeometry(new Rect(box.X - 12, box.Y - 12, box.Width * progress + 12, box.Height + 24)));
                    DrawGlyph(dc, g, box.X, box.Y, B(_hi, alpha), halo, pulse);
                    dc.Pop();
                }
            }
        }

        private void DrawIdle(DrawingContext dc, MonitorInfo mon, double cx, double maxW)
        {
            double cy = mon.Y + mon.Height * 0.42;
            if (_f.Track == null || _f.Status == LyricsStatus.NoTrack)
            {
                var g = GlyphFor("Reproduce una canción en Apple Music", mon.Height * 0.05);
                DrawCentered(dc, g, cx, cy, B(_text, 0.55), maxW);
                return;
            }
            var title = GlyphFor(_f.Track.Title, mon.Height * 0.075);
            var artist = GlyphFor(_f.Track.Artist, mon.Height * 0.045);
            DrawCentered(dc, title, cx, cy - title.Height, B(_text), maxW);
            DrawCentered(dc, artist, cx, cy + mon.Height * 0.01, B(_sec, 0.9), maxW);
            string status = _f.Status switch
            {
                LyricsStatus.Loading => "Buscando letra…",
                LyricsStatus.Missing => "Sin letra sincronizada",
                _ => "No se pudo cargar la letra",
            };
            double pulse = _f.Status == LyricsStatus.Loading ? 0.45 + 0.25 * Math.Sin(_now * 3) : 0.5;
            DrawCentered(dc, GlyphFor(status, mon.Height * 0.03), cx, cy + mon.Height * 0.1, B(_text, pulse), maxW);
        }

        private void DrawCometsAndRipples(DrawingContext dc, MonitorInfo mon)
        {
            double unit = Math.Max(1, mon.Height / 1080.0);
            var view = Rect.Inflate(mon.Rect, 120 * unit, 120 * unit);
            int trail = Math.Max(4, (int)(14 * Quality));
            foreach (var c in _comets)
            {
                double t = Ease((_now - c.Start) / c.Duration);
                for (int k = trail; k >= 0; k--)
                {
                    double tt = Math.Max(0, t - k * 0.022);
                    var p = CometPoint(c, tt);
                    if (!view.Contains(p)) continue;
                    double fall = 1 - k / (double)(trail + 1);
                    double r = (3 + 7 * fall) * unit;
                    if (k == 0)
                    {
                        dc.DrawEllipse(Glow(p, r * 6, _hi, 0.7), null, p, r * 6, r * 6);
                        dc.DrawEllipse(B(new Rgb(1, 1, 1), 1), null, p, r, r);
                    }
                    else dc.DrawEllipse(B(_hi.Mix(_sec, 1 - fall), 0.5 * fall), null, p, r, r);
                }
            }
            foreach (var rp in _ripples)
            {
                double t = (_now - rp.Start) / 0.8;
                double r = (12 + 90 * Ease(t)) * unit;
                if (!view.Contains(rp.At)) continue;
                dc.DrawEllipse(null, new Pen(B(_hi, 0.7 * (1 - t)), 3 * unit), rp.At, r, r);
            }
        }

        private static Point CometPoint(Comet c, double t)
        {
            double dx = c.To.X - c.From.X, dy = c.To.Y - c.From.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            var ctrl = new Point((c.From.X + c.To.X) / 2, (c.From.Y + c.To.Y) / 2 - dist * 0.22);
            double u = 1 - t;
            return new Point(u * u * c.From.X + 2 * u * t * ctrl.X + t * t * c.To.X,
                             u * u * c.From.Y + 2 * u * t * ctrl.Y + t * t * c.To.Y);
        }

        // ───────────────────────── Superposición transparente ─────────────────────────

        private void DrawOverlay(DrawingContext dc, MonitorInfo mon)
        {
            var f = _f;
            if (f.Status != LyricsStatus.Ready || f.Lyrics == null || f.Sync.LineIndex < 0) return;
            var layout = LayoutFor(f.Sync.LineIndex);
            if (layout == null) return;
            double unit = mon.Height / 1080.0;
            double since = Ease((_now - _lineChangedAt) / 0.3);
            DrawWordLine(dc, layout, f.Sync.WordIndex, f.Sync.WordProgress, f.Sync.IsLineFinished, true, 0.6 + 0.4 * since, shadow: true, glowUnit: unit);
            int next = f.Sync.LineIndex + 1;
            if (next < f.Lyrics.Lines.Count)
            {
                var g = GlyphFor(f.Lyrics.Lines[next].Text, mon.Height * 0.026);
                double cx = mon.X + mon.Width / 2;
                double y = layout.Block.Bottom + mon.Height * 0.012;
                DrawCentered(dc, g, cx + 2 * unit, y + 2 * unit, B(new Rgb(0, 0, 0), 0.5), mon.Width * 0.8);
                DrawCentered(dc, g, cx, y, B(_text, 0.6), mon.Width * 0.8);
            }
        }
    }
}

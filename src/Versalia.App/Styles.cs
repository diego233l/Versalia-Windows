using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Versalia.Core;

namespace Versalia.App
{
    /// <summary>Estilos de letra palabra a palabra portados de macOS: aurora, neon, poster y orbit.</summary>
    internal sealed partial class Scene
    {
        /// <summary>duo (líneas clásicas), aurora, neon, poster u orbit.</summary>
        public string Style { get; set; } = "duo";

        private sealed class GroupLayout
        {
            public Dictionary<int, Rect> Boxes = new Dictionary<int, Rect>();
            public double FontSize;
        }

        private readonly Dictionary<int, GroupLayout> _groupLayouts = new Dictionary<int, GroupLayout>();

        private GroupLayout? GroupLayoutFor(int entryIndex)
        {
            var words = _f.Words;
            if (words == null || entryIndex < 0 || entryIndex >= words.Entries.Count) return null;
            var entry = words.Entries[entryIndex];
            if (_groupLayouts.TryGetValue(entry.GroupId, out var cached)) return cached;
            var area = LyricArea();
            if (area.IsEmpty) return null;
            var range = words.Groups[entry.GroupId];
            bool poster = Style == "poster";
            double size = poster ? Math.Min(area.Height * 0.2, area.Width * 0.12) : Math.Min(area.Height * 0.14, area.Width * 0.075);
            double maxWidth = area.Width * (poster ? 0.8 : 0.86);
            double lineFactor = poster ? 1.08 : 1.25;
            double gapFactor = poster ? 0.28 : 0.32;

            var rows = new List<List<int>>();
            for (int attempt = 0; attempt < 6; attempt++)
            {
                rows = new List<List<int>> { new List<int>() };
                double rowWidth = 0, gap = size * gapFactor;
                for (int i = range.First; i < range.First + range.Count; i++)
                {
                    double w = GlyphFor(words.Entries[i].Text, size, true).Width;
                    double next = rowWidth == 0 ? w : rowWidth + gap + w;
                    if (next > maxWidth && rowWidth > 0) { rows.Add(new List<int> { i }); rowWidth = w; }
                    else { rows[^1].Add(i); rowWidth = next; }
                }
                bool fits = poster ? rows.Count * size * lineFactor <= area.Height * 0.72 : rows.Count <= 3;
                if (fits) break;
                size *= poster ? 0.85 : 0.84;
            }

            var layout = new GroupLayout { FontSize = size };
            double lineHeight = size * lineFactor, gapPx = size * gapFactor;
            double top = area.Y + area.Height / 2 - rows.Count * lineHeight / 2;
            for (int r = 0; r < rows.Count; r++)
            {
                double width = rows[r].Sum(i => GlyphFor(words.Entries[i].Text, size, true).Width) + gapPx * (rows[r].Count - 1);
                double x = poster ? area.X + area.Width * 0.1 : area.X + (area.Width - width) / 2;
                foreach (int i in rows[r])
                {
                    var g = GlyphFor(words.Entries[i].Text, size, true);
                    layout.Boxes[i] = new Rect(x, top + r * lineHeight, g.Width, g.Height);
                    x += g.Width + gapPx;
                }
            }
            _groupLayouts[entry.GroupId] = layout;
            return layout;
        }

        /// <summary>Dónde aterriza el cometa de una palabra según el estilo.</summary>
        private Point StyleTarget(int entryIndex)
        {
            var area = LyricArea();
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height * 0.48);
            if (Style == "neon" || Style == "poster")
            {
                var layout = GroupLayoutFor(entryIndex);
                if (layout != null && layout.Boxes.TryGetValue(entryIndex, out var box))
                    return new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            }
            return center;
        }

        private void DrawStyle(DrawingContext dc, MonitorInfo mon)
        {
            var words = _f.Words;
            if (words == null || words.IsEmpty) return;
            int started = _f.LastStarted;
            double visibility = words.Visibility(started, _f.PositionMs);
            if (visibility < 0.01) return;
            switch (Style)
            {
                case "aurora": DrawAurora(dc, mon, words, started, visibility); break;
                case "neon": DrawNeon(dc, words, started, visibility); break;
                case "poster": DrawPoster(dc, mon, words, started, visibility); break;
                case "orbit": DrawOrbit(dc, mon, words, started, visibility); break;
            }
        }

        // ───── Aurora: palabra actual enorme y luminosa, frase ya cantada pequeña, barra de progreso ─────
        private void DrawAurora(DrawingContext dc, MonitorInfo mon, WordStream words, int started, double visibility)
        {
            if (started < 0) return;
            var entry = words.Entries[started];
            int first = words.FirstIndexOfGroup(started);
            double big = Math.Min(mon.Height * 0.3, mon.Width * 0.16);
            double pop = Ease((_f.PositionMs - entry.StartMs) / 380.0);
            double cx = mon.X + mon.Width / 2;
            var g = GlyphFor(entry.Text, big, true);
            var halo = new Pen(B(_hi, 0.55 * visibility * (1 - 0.4 * pop)), big * 0.07) { LineJoin = PenLineJoin.Round };
            DrawGlyph(dc, g, cx - g.Width / 2, mon.Y + mon.Height * 0.48 - g.Height / 2, B(_text, visibility * (0.15 + 0.85 * pop)), halo, 0.88 + 0.12 * pop);

            if (started > first)
            {
                var parts = new List<string>();
                for (int i = first; i < started; i++) parts.Add(words.Entries[i].Text);
                double rowSize = big * 0.24;
                string shown = string.Join("  ", parts);
                while (parts.Count > 1 && GlyphFor(shown, rowSize).Width > mon.Width * 0.9)
                {
                    parts.RemoveAt(0);
                    shown = string.Join("  ", parts);
                }
                DrawCentered(dc, GlyphFor(shown, rowSize), cx, mon.Y + mon.Height * 0.70, B(_sec, 0.9 * visibility), mon.Width * 0.9);
            }

            var group = words.Groups[entry.GroupId];
            double startMs = words.Entries[first].StartMs, endMs = words.Entries[Math.Min(group.First + group.Count - 1, words.Entries.Count - 1)].EndMs;
            if (endMs > startMs)
            {
                double progress = Math.Clamp((_f.PositionMs - startMs) / (endMs - startMs), 0, 1);
                double width = mon.Width * 0.4, y = mon.Bottom - mon.Height * 0.2, left = cx - width / 2;
                var back = new Pen(B(_sec, 0.3 * visibility), 2 * mon.Height / 1080) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var front = new Pen(B(_hi, 0.9 * visibility), 2 * mon.Height / 1080) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(back, new Point(left, y), new Point(left + width, y));
                dc.DrawLine(front, new Point(left, y), new Point(left + width * progress, y));
            }
        }

        // ───── Neón: la frase entera apagada (contorno) y cada palabra se enciende al cantarse ─────
        private void DrawNeon(DrawingContext dc, WordStream words, int started, double visibility)
        {
            int anchor = Math.Max(0, started);
            int first = words.FirstIndexOfGroup(anchor);
            double groupStart = words.Entries[first].StartMs;
            if (first > 0 && started >= first)
            {
                double fade = 1 - (_f.PositionMs - groupStart) / 300;
                if (fade > 0) DrawNeonGroup(dc, words, words.FirstIndexOfGroup(first - 1), first - 1, visibility * fade, (1 - fade) * 0.06);
            }
            double appear = started >= first ? Math.Clamp((_f.PositionMs - groupStart) / 220, 0, 1) : 1;
            var range = words.Groups[words.Entries[first].GroupId];
            DrawNeonGroup(dc, words, first, range.First + range.Count - 1, visibility * appear, 0);
        }

        private void DrawNeonGroup(DrawingContext dc, WordStream words, int first, int last, double alpha, double liftFraction)
        {
            if (alpha < 0.01 || first > last) return;
            var layout = GroupLayoutFor(first);
            if (layout == null) return;
            double lift = liftFraction * LyricArea().Height;
            int started = _f.LastStarted;
            for (int i = first; i <= last; i++)
            {
                if (!layout.Boxes.TryGetValue(i, out var box)) continue;
                var g = GlyphFor(words.Entries[i].Text, layout.FontSize, true);
                double y = box.Y - lift;
                if (i > started)
                {
                    var outline = new Pen(B(_sec, alpha * 0.35), Math.Max(1, layout.FontSize * 0.025));
                    DrawGlyph(dc, g, box.X, y, Brushes.Transparent, outline);
                    continue;
                }
                double age = _f.PositionMs - words.Entries[i].StartMs;
                double on = 1, scale = 1;
                if (i == started)
                {
                    if (age < 260) on = (Math.Sin(_f.PositionMs * 0.09) > -0.2 ? 1.0 : 0.35) * (0.5 + 0.5 * Ease(age / 260));
                    scale = 1 + 0.08 * (1 - Ease(age / 320));
                }
                var halo = new Pen(B(_hi, 0.55 * alpha * on), layout.FontSize * (i == started ? 0.09 : 0.05)) { LineJoin = PenLineJoin.Round };
                DrawGlyph(dc, g, box.X, y, B(i == started ? _text : _hi, alpha * on), halo, scale);
            }
        }

        // ───── Póster: filas grandes alineadas a la izquierda, la actual subrayada ─────
        private void DrawPoster(DrawingContext dc, MonitorInfo mon, WordStream words, int started, double visibility)
        {
            if (started < 0) return;
            int first = words.FirstIndexOfGroup(started);
            var range = words.Groups[words.Entries[first].GroupId];
            var layout = GroupLayoutFor(first);
            if (layout == null) return;
            double size = layout.FontSize;
            double labelSize = Math.Max(10, size * 0.13);
            var rowsSeen = new List<double>();
            for (int i = range.First; i < range.First + range.Count; i++)
            {
                if (!layout.Boxes.TryGetValue(i, out var box)) continue;
                int row = rowsSeen.FindIndex(y => Math.Abs(y - box.Y) < 1);
                if (row < 0)
                {
                    rowsSeen.Add(box.Y); row = rowsSeen.Count - 1;
                    var label = GlyphFor($"{row + 1:00}", labelSize);
                    DrawGlyph(dc, label, mon.X + mon.Width * 0.1 - labelSize * 1.6 - label.Width, box.Y + size * 0.55, B(_sec, 0.7 * visibility));
                }
                var g = GlyphFor(words.Entries[i].Text, size, true);
                if (i > started) { DrawGlyph(dc, g, box.X, box.Y, B(_text, 0.08 * visibility)); continue; }
                var entry = words.Entries[i];
                double reveal = Ease((_f.PositionMs - entry.StartMs) / 350.0);
                double slide = (1 - reveal) * size * 0.35;
                bool current = i == started;
                DrawGlyph(dc, g, box.X - slide, box.Y, B(current ? _hi : _text, visibility * (0.15 + 0.85 * reveal)));
                if (current)
                {
                    double progress = Math.Clamp((_f.PositionMs - entry.StartMs) / Math.Max(1, entry.EndMs - entry.StartMs), 0, 1);
                    double uy = box.Y + g.Height * 0.95;
                    var pen = new Pen(B(_hi, visibility), Math.Max(2, size * 0.05));
                    dc.DrawLine(pen, new Point(box.X - slide, uy), new Point(box.X - slide + g.Width * progress, uy));
                }
            }
        }

        // ───── Órbita: la palabra actual brilla en el centro y las anteriores giran en anillos ─────
        private void DrawOrbit(DrawingContext dc, MonitorInfo mon, WordStream words, int started, double visibility)
        {
            var entries = words.Entries;
            var center = new Point(mon.X + mon.Width / 2, mon.Y + mon.Height * 0.48);
            double big = Math.Min(mon.Height * 0.26, mon.Width * 0.13);
            double now = _f.PositionMs, time = _now;
            const double tilt = -0.2;
            double carousel = -1;
            if (started >= 0) carousel = started - 1 + Ease((now - entries[started].StartMs) / 420.0);

            (double rx, double ry) Ring(double age)
            {
                double rx = Math.Min(mon.Width * 0.46, big * 1.5 + age * mon.Width * 0.06);
                return (rx, rx * 0.4);
            }
            Point Rotated(double x, double y) =>
                new Point(center.X + x * Math.Cos(tilt) - y * Math.Sin(tilt), center.Y + x * Math.Sin(tilt) + y * Math.Cos(tilt));

            for (int ring = 1; ring <= 5; ring++)
            {
                var (rx, ry) = Ring(ring);
                dc.PushTransform(new RotateTransform(tilt * 180 / Math.PI, center.X, center.Y));
                dc.DrawEllipse(null, new Pen(B(_hi, 0.16 * visibility / ring + 0.03), 1), center, rx, ry);
                dc.Pop();
            }

            int lower = Math.Max(0, started - 7), upper = Math.Min(entries.Count - 1, started + 1);
            for (int index = lower; index <= upper; index++)
            {
                double slot = index - carousel, age = -slot;
                if (slot > 1.01) continue;
                if (slot > 0)
                {
                    var (rx5, ry5) = Ring(5);
                    double a = index * 2.399963 + time * 0.05;
                    var wait = Rotated(Math.Cos(a) * rx5, Math.Sin(a) * ry5);
                    dc.DrawEllipse(B(_hi, 0.5 * visibility * slot), null, wait, 2.5, 2.5);
                    continue;
                }
                if (age > 6.5) continue;
                double moved = Ease(Math.Min(1, age));
                var (rx, ry) = Ring(Math.Max(1, age));
                double angle = index * 2.399963 + time * 0.35 / Math.Max(1, age);
                var offset = Rotated(Math.Cos(angle) * rx, Math.Sin(angle) * ry);
                var spot = new Point(center.X + (offset.X - center.X) * moved, center.Y + (offset.Y - center.Y) * moved);
                double sizeFactor = age <= 1 ? 1 - 0.62 * age : Math.Max(0.26, 0.38 - (age - 1) * 0.02);
                double alpha = age <= 1 ? 1 - 0.15 * age : Math.Max(0, 0.85 * (1 - (age - 1) / 6));
                if (alpha < 0.02) continue;
                bool isCenter = age < 0.02;
                double entry = Ease((now - entries[index].StartMs) / 300.0);
                var g = GlyphFor(entries[index].Text, big * sizeFactor, isCenter);
                if (isCenter)
                {
                    dc.DrawEllipse(Glow(center, big * 1.9, _hi, 0.28 * visibility), null, center, big * 1.9, big * 1.9);
                    double r = big * 0.14 + Math.Sin(time * 3) * 1.5;
                    var star = new Point(center.X, center.Y + big * 0.78);
                    var pen = new Pen(B(_hi, visibility), Math.Max(2, r * 0.25)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawLine(pen, new Point(star.X - r, star.Y), new Point(star.X + r, star.Y));
                    dc.DrawLine(pen, new Point(star.X, star.Y - r), new Point(star.X, star.Y + r));
                    DrawGlyph(dc, g, spot.X - g.Width / 2, spot.Y - g.Height / 2, B(_text, alpha * visibility * (0.25 + 0.75 * entry)), null, 0.75 + 0.25 * entry);
                }
                else
                {
                    dc.DrawEllipse(B(_hi, 0.7 * alpha * visibility), null, spot, 3, 3);
                    DrawGlyph(dc, g, spot.X - g.Width / 2, spot.Y - g.Height * 1.2, B(_sec, alpha * visibility));
                }
            }
        }
    }
}

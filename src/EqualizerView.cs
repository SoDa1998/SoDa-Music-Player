using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    /// <summary>
    /// The 20 band sliders of the equalizer with the combined frequency-response curve above them. Drag a band up or down
    /// (0.5 dB steps, snaps to 0), double-click it to reset it.
    /// </summary>
    internal sealed class SEqBands : SWidget
    {
        readonly EqualizerSettings eq;
        readonly Func<bool> live;        // EQ switched on (drawn in the accent colour) or off (greyed)
        readonly Action changed;
        int hoverBand = -1, dragBand = -1, lastClickBand = -1, lastClickTime;
        PointF[] curve;
        int curveVersion = -1;
        Rectangle curveRect;

        public SEqBands(EqualizerSettings eq, Func<bool> live, Action changed) { this.eq = eq; this.live = live; this.changed = changed; }

        public override int Height { get { return Theme.S(330); } }
        public override bool WantsDrag { get { return true; } }

        const float GraphDb = 15f;   // graph spans ±15 dB (bands ±12 plus preamp)

        Rectangle Graph(Rectangle r) { return new Rectangle(r.X, r.Y + Theme.S(4), r.Width, Theme.S(116)); }
        Rectangle Sliders(Rectangle r)
        {
            int top = Graph(r).Bottom + Theme.S(16);
            return new Rectangle(r.X, top, r.Width, r.Bottom - top);
        }
        float ColW(Rectangle r) { return r.Width / (float)EqualizerSettings.Bands; }
        float ColX(Rectangle r, int i) { return r.X + ColW(r) * (i + 0.5f); }
        // Within the slider area: value label, track, frequency label.
        Rectangle Track(Rectangle r)
        {
            Rectangle s = Sliders(r);
            return new Rectangle(s.X, s.Y + Theme.S(24), s.Width, s.Height - Theme.S(24) - Theme.S(26));
        }
        int BandAt(Point p, Rectangle r)
        {
            int i = (int)((p.X - r.X) / ColW(r));
            return i >= 0 && i < EqualizerSettings.Bands ? i : -1;
        }

        // ------------------------------------------------------------------ painting

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            bool on = live();
            Color accent = on ? Theme.Accent : Theme.TextDim;
            PaintGraph(g, r, accent);

            Rectangle t = Track(r);
            float cy = t.Y + t.Height / 2f;
            using (var p = new Pen(Theme.Alpha(Theme.Overlay, 22)) { DashStyle = DashStyle.Dot })
                g.DrawLine(p, t.X, cy, t.Right, cy);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Font small = Theme.Ui(7.5f), smallBold = Theme.UiSemibold(7.5f);
            float tw = Theme.S(4f), d = Theme.S(14f);
            for (int i = 0; i < EqualizerSettings.Bands; i++)
            {
                float x = ColX(r, i), gain = eq.GetGain(i);
                bool hot = i == dragBand || (dragBand < 0 && i == hoverBand && hover > 0);
                var col = new Rectangle((int)(x - ColW(r) / 2), t.Y - Theme.S(26), (int)ColW(r), t.Height + Theme.S(52));
                if (hot) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(10 * Math.Max(hover, i == dragBand ? 1f : 0f))), col, Theme.S(8f));

                // track + fill from 0 dB to the value
                Theme.FillRound(g, Theme.Alpha(Theme.Overlay, 26), new RectangleF(x - tw / 2, t.Y, tw, t.Height), tw / 2);
                float y = ValueToY(t, gain);
                if (Math.Abs(gain) > 0.01f)
                    Theme.FillRound(g, Theme.Alpha(accent, on ? 230 : 160), new RectangleF(x - tw / 2, Math.Min(y, cy), tw, Math.Abs(y - cy)), tw / 2);

                // thumb
                float dd = hot ? d + Theme.S(2f) : d;
                using (var b = new SolidBrush(Theme.Card)) g.FillEllipse(b, x - dd / 2 - Theme.S(2f), y - dd / 2 - Theme.S(2f), dd + Theme.S(4f), dd + Theme.S(4f));
                using (var b = new SolidBrush(Math.Abs(gain) > 0.01f || hot ? accent : Theme.Mix(Theme.TextDim, Theme.Card, 0.25)))
                    g.FillEllipse(b, x - dd / 2, y - dd / 2, dd, dd);
                using (var b = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    g.FillEllipse(b, x - dd / 6, y - dd / 6, dd / 3, dd / 3);

                // value above, frequency below
                string val = Math.Abs(gain) < 0.05f ? "0" : (gain > 0 ? "+" : "−") + Math.Abs(gain).ToString(Math.Abs(gain) % 1 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
                Theme.DrawText(g, val, hot ? smallBold : small, hot ? Theme.Text : Theme.TextDim,
                    new Rectangle((int)(x - ColW(r) / 2 - Theme.S(4)), t.Y - Theme.S(24), (int)ColW(r) + Theme.S(8), Theme.S(18)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                Theme.DrawText(g, EqualizerSettings.FormatFrequency(EqualizerSettings.Frequencies[i]), hot ? smallBold : small, hot ? Theme.Text : Theme.TextDim,
                    new Rectangle((int)(x - ColW(r) / 2 - Theme.S(4)), t.Bottom + Theme.S(6), (int)ColW(r) + Theme.S(8), Theme.S(18)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            g.SmoothingMode = SmoothingMode.Default;
        }

        void PaintGraph(Graphics g, Rectangle r, Color accent)
        {
            Rectangle gr = Graph(r);
            Theme.FillRound(g, Theme.Alpha(Theme.Overlay, 7), gr, Theme.S(10f));
            Font small = Theme.Ui(7.5f);
            foreach (int db in new[] { 12, 6, 0, -6, -12 })
            {
                float y = gr.Y + gr.Height / 2f - db / GraphDb * (gr.Height / 2f);
                using (var p = new Pen(Theme.Alpha(Theme.Overlay, db == 0 ? 34 : 14)))
                    g.DrawLine(p, gr.X + Theme.S(34), y, gr.Right - Theme.S(10), y);
                Theme.DrawText(g, db == 0 ? "0 dB" : (db > 0 ? "+" : "−") + Math.Abs(db), small, Theme.TextDim,
                    new Rectangle(gr.X + Theme.S(4), (int)y - Theme.S(9), Theme.S(28), Theme.S(18)),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (curve == null || curveVersion != eq.Version || curveRect != gr) BuildCurve(r, gr);
            float zero = gr.Y + gr.Height / 2f;
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(gr.X + Theme.S(34), gr.Y + 1, gr.Width - Theme.S(44), gr.Height - 2), CombineMode.Intersect);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            {
                path.AddLine(curve[0].X, zero, curve[0].X, curve[0].Y);
                path.AddLines(curve);
                path.AddLine(curve[curve.Length - 1].X, curve[curve.Length - 1].Y, curve[curve.Length - 1].X, zero);
                path.CloseFigure();
                using (var b = new SolidBrush(Theme.Alpha(accent, 38))) g.FillPath(b, path);
            }
            using (var p = new Pen(accent, Theme.S(2f)) { LineJoin = LineJoin.Round }) g.DrawLines(p, curve);
            g.SmoothingMode = SmoothingMode.Default;
            g.Clip = oldClip;
        }

        /// <summary>The curve's x axis is aligned so each band's centre frequency sits right above its slider.</summary>
        void BuildCurve(Rectangle r, Rectangle gr)
        {
            Biquad?[] filters = EqualizerSettings.Design(eq.Snapshot(), 48000);
            float pre = eq.Preamp;
            double logSpan = Math.Log(20000.0 / 25.0);
            var pts = new System.Collections.Generic.List<PointF>();
            int x0 = gr.X + Theme.S(34), x1 = gr.Right - Theme.S(10);
            for (int x = x0; x <= x1; x += 2)
            {
                double band = (x - r.X) / (double)ColW(r) - 0.5;               // fractional band index under x
                double f = 25.0 * Math.Exp(band / (EqualizerSettings.Bands - 1) * logSpan);
                f = Math.Max(10, Math.Min(23000, f));
                double db = EqualizerSettings.ResponseDb(filters, pre, f, 48000);
                float y = (float)(gr.Y + gr.Height / 2.0 - db / GraphDb * (gr.Height / 2.0));
                pts.Add(new PointF(x, y));
            }
            curve = pts.ToArray();
            curveVersion = eq.Version;
            curveRect = gr;
        }

        static float ValueToY(Rectangle t, float db)
        {
            float half = t.Height / 2f - Theme.S(8f);
            return t.Y + t.Height / 2f - db / EqualizerSettings.MaxDb * half;
        }

        static float YToValue(Rectangle t, int y)
        {
            float half = t.Height / 2f - Theme.S(8f);
            float v = (t.Y + t.Height / 2f - y) / half * EqualizerSettings.MaxDb;
            v = (float)Math.Round(v * 2) / 2f;                 // 0.5 dB steps
            return Math.Max(-EqualizerSettings.MaxDb, Math.Min(EqualizerSettings.MaxDb, v));
        }

        // ------------------------------------------------------------------ input

        public override Cursor CursorAt(Point p, Rectangle r)
        {
            if (!IsEnabled) return Cursors.Default;
            return Sliders(r).Contains(p) && BandAt(p, r) >= 0 ? Cursors.SizeNS : Cursors.Default;
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v)
        {
            if (!Sliders(r).Contains(p)) return;
            int b = BandAt(p, r);
            if (b < 0) return;
            int now = Environment.TickCount;
            if (b == lastClickBand && unchecked(now - lastClickTime) < SystemInformation.DoubleClickTime)
            {
                eq.SetGain(b, 0);                            // double-click: back to 0 dB
                lastClickBand = -1;
                changed();
                v.Invalidate();
                return;
            }
            lastClickBand = b; lastClickTime = now;
            dragBand = b;
            // Clicking the thumb itself shouldn't make it jump; clicking elsewhere on the track moves it there.
            Rectangle t = Track(r);
            if (Math.Abs(p.Y - ValueToY(t, eq.GetGain(b))) > Theme.S(10)) SetFrom(p, r);
            v.Invalidate();
        }

        void SetFrom(Point p, Rectangle r)
        {
            float val = YToValue(Track(r), p.Y);
            if (val == eq.GetGain(dragBand)) return;
            eq.SetGain(dragBand, val);
            changed();
        }

        public override void MouseMove(Point p, Rectangle r, SettingsView v)
        {
            if (dragBand >= 0) { SetFrom(p, r); v.Invalidate(); return; }
            int h = Sliders(r).Contains(p) ? BandAt(p, r) : -1;
            if (h != hoverBand) { hoverBand = h; v.Invalidate(); }
        }

        public override void MouseUp(Point p, Rectangle r, SettingsView v)
        {
            dragBand = -1;
            hoverBand = r.Contains(p) && Sliders(r).Contains(p) ? BandAt(p, r) : -1;
            v.Invalidate();
        }
    }

    /// <summary>Small coloured status pill (e.g. "ACTIVE" / "BYPASSED").</summary>
    internal sealed class SBadge : SWidget
    {
        readonly Func<string> text; readonly Func<Color> color;
        public SBadge(Func<string> text, Func<Color> color) { this.text = text; this.color = color; }
        public override int Width { get { return TextRenderer.MeasureText(text(), Theme.UiSemibold(8f)).Width + Theme.S(22); } }
        public override int Height { get { return Theme.S(26); } }
        public override Cursor CursorAt(Point p, Rectangle r) { return Cursors.Default; }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            Color c = color();
            Theme.FillRound(g, Theme.Alpha(c, 44), r, r.Height / 2f);
            Theme.DrawText(g, text(), Theme.UiSemibold(8f), c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    // ======================================================================= settings widgets (painted, theme-aware)

    internal abstract class SWidget
    {
        public Rectangle Bounds;                 // content coordinates (set by layout)
        public Func<bool> Enabled;
        public bool IsEnabled { get { return Enabled == null || Enabled(); } }
        /// <summary>Hidden widgets are neither painted nor clickable (their space is kept).</summary>
        public Func<bool> Visible;
        public bool IsVisible { get { return Visible == null || Visible(); } }
        public virtual int Width { get { return Theme.S(140); } }
        public virtual int Height { get { return Theme.S(36); } }
        public abstract void Paint(Graphics g, Rectangle r, float hover);
        public virtual void MouseDown(Point p, Rectangle r, SettingsView v) { }
        public virtual void MouseMove(Point p, Rectangle r, SettingsView v) { }
        public virtual void MouseUp(Point p, Rectangle r, SettingsView v) { }
        public virtual bool WantsDrag { get { return false; } }
        public virtual Cursor CursorAt(Point p, Rectangle r) { return IsEnabled ? Cursors.Hand : Cursors.Default; }

        protected static void Box(Graphics g, Rectangle r, float hover, float radius)
        {
            Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(8 + 9 * hover)), r, radius);
            using (var p = new Pen(Theme.Border))
            using (var path = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius))
            {
                var old = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawPath(p, path);
                g.SmoothingMode = old;
            }
        }
    }

    internal sealed class SDropDown : SWidget
    {
        readonly Func<string[]> options; readonly Func<int> selected; readonly Action<int> select; readonly int width;
        public SDropDown(Func<string[]> options, Func<int> selected, Action<int> select, int width)
        { this.options = options; this.selected = selected; this.select = select; this.width = width; }
        public override int Width { get { return Theme.S(width); } }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            Box(g, r, IsEnabled ? hover : 0f, Theme.S(8f));
            string[] o = options();
            int i = selected();
            string text = i >= 0 && i < o.Length ? o[i] : "";
            Theme.TextLeft(g, text, Theme.Ui(9.5f), IsEnabled ? Theme.Text : Theme.TextDim, new Rectangle(r.X + Theme.S(12), r.Y, r.Width - Theme.S(44), r.Height));
            Theme.GlyphCenter(g, Glyphs.ChevronDown, 10f, Theme.TextDim, new Rectangle(r.Right - Theme.S(34), r.Y, Theme.S(24), r.Height));
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v)
        {
            if (!IsEnabled) return;
            ContextMenuStrip m = Theme.Menu();
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            m.MinimumSize = new Size(r.Width, 0);
            string[] o = options();
            int cur = selected();
            for (int i = 0; i < o.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(o[i]) { Checked = i == cur };
                it.Click += delegate { select(idx); v.Invalidate(); };
                m.Items.Add(it);
            }
            m.Show(v, new Point(r.X, r.Bottom + Theme.S(4)));
        }
    }

    internal sealed class SSegmented : SWidget
    {
        readonly string[] labels, icons; readonly Func<int> get; readonly Action<int> set;
        int hoverSeg = -1;
        public SSegmented(string[] labels, string[] icons, Func<int> get, Action<int> set) { this.labels = labels; this.icons = icons; this.get = get; this.set = set; }

        int SegWidth(int i)
        {
            int w = TextRenderer.MeasureText(labels[i], Theme.UiMedium(9.5f)).Width + Theme.S(28);
            if (icons != null && icons[i] != null) w += Theme.S(22);
            return w;
        }
        public override int Width { get { int w = Theme.S(8); for (int i = 0; i < labels.Length; i++) w += SegWidth(i); return w; } }

        Rectangle Seg(Rectangle r, int i)
        {
            int x = r.X + Theme.S(4);
            for (int k = 0; k < i; k++) x += SegWidth(k);
            return new Rectangle(x, r.Y + Theme.S(4), SegWidth(i), r.Height - Theme.S(8));
        }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            Theme.FillRound(g, Theme.Alpha(Theme.Overlay, 10), r, r.Height / 2f);
            int cur = get();
            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle s = Seg(r, i);
                bool on = i == cur;
                if (on) Theme.FillRound(g, Theme.Accent, s, s.Height / 2f);
                else if (hover > 0 && i == hoverSeg) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(15 * hover)), s, s.Height / 2f);
                Color fg = on ? Color.White : Theme.Text;
                int x = s.X + Theme.S(14);
                if (icons != null && icons[i] != null)
                {
                    Theme.GlyphCenter(g, icons[i], 11f, fg, new Rectangle(x, s.Y, Theme.S(18), s.Height));
                    x += Theme.S(22);
                }
                Theme.TextLeft(g, labels[i], Theme.UiMedium(9.5f), fg, new Rectangle(x, s.Y, s.Right - x, s.Height));
            }
        }

        public override void MouseMove(Point p, Rectangle r, SettingsView v)
        {
            int h = -1;
            for (int i = 0; i < labels.Length; i++) if (Seg(r, i).Contains(p)) h = i;
            if (h != hoverSeg) { hoverSeg = h; v.Invalidate(); }
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v)
        {
            for (int i = 0; i < labels.Length; i++) if (Seg(r, i).Contains(p)) { set(i); v.Invalidate(); return; }
        }
    }

    /// <summary>Row of colour swatches: "theme default", presets and a custom (+) swatch. The selected one gets a ring.</summary>
    internal sealed class SColorChoice : SWidget
    {
        readonly Color[] presets; readonly Func<Color?> get; readonly Action<Color?> set; readonly Action custom;
        int hoverIdx = -1;
        public SColorChoice(Color[] presets, Func<Color?> get, Action<Color?> set, Action custom) { this.presets = presets; this.get = get; this.set = set; this.custom = custom; }

        int Count { get { return presets.Length + 2; } }   // default + presets + custom
        int D { get { return Theme.S(24); } }
        int Step { get { return Theme.S(32); } }
        public override int Width { get { return Count * Step - (Step - D); } }
        public override int Height { get { return Theme.S(32); } }

        Rectangle Swatch(Rectangle r, int i) { return new Rectangle(r.X + i * Step, r.Y + (r.Height - D) / 2, D, D); }

        int Selected()
        {
            Color? cur = get();
            if (!cur.HasValue) return 0;
            for (int i = 0; i < presets.Length; i++) if (presets[i].ToArgb() == cur.Value.ToArgb()) return i + 1;
            return Count - 1;
        }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int sel = Selected();
            Color? cur = get();
            for (int i = 0; i < Count; i++)
            {
                Rectangle s = Swatch(r, i);
                if (i == hoverIdx && hover > 0) s.Inflate(Theme.S(1), Theme.S(1));
                if (i == 0)
                {
                    // Theme default: half the theme's own accent, half neutral, so it reads as "automatic".
                    Color themeAccent = Theme.ThemeAccent;
                    using (var b = new SolidBrush(themeAccent)) g.FillPie(b, s, 90, 180);
                    using (var b = new SolidBrush(Theme.TextDim)) g.FillPie(b, s, 270, 180);
                }
                else if (i == Count - 1)
                {
                    bool customSel = sel == i && cur.HasValue;
                    using (var b = new SolidBrush(customSel ? cur.Value : Theme.Alpha(Theme.Overlay, 14))) g.FillEllipse(b, s);
                    using (var p = new Pen(Theme.Border)) g.DrawEllipse(p, s);
                    Theme.GlyphCenter(g, Glyphs.Add, 10f, customSel ? Color.White : Theme.Text, s);
                }
                else using (var b = new SolidBrush(presets[i - 1])) g.FillEllipse(b, s);
                if (i == sel)
                    using (var p = new Pen(Theme.Text, Theme.S(2f)))
                        g.DrawEllipse(p, Rectangle.Inflate(Swatch(r, i), Theme.S(3), Theme.S(3)));
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        }

        public override void MouseMove(Point p, Rectangle r, SettingsView v)
        {
            int h = -1;
            for (int i = 0; i < Count; i++) if (Rectangle.Inflate(Swatch(r, i), Theme.S(4), Theme.S(4)).Contains(p)) h = i;
            if (h != hoverIdx) { hoverIdx = h; v.Invalidate(); }
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v)
        {
            for (int i = 0; i < Count; i++)
            {
                if (!Rectangle.Inflate(Swatch(r, i), Theme.S(4), Theme.S(4)).Contains(p)) continue;
                if (i == 0) set(null);
                else if (i == Count - 1) custom();
                else set(presets[i - 1]);
                v.Invalidate();
                return;
            }
        }
    }

    internal sealed class SToggle : SWidget
    {
        readonly Func<bool> get; readonly Action<bool> set;
        public SToggle(Func<bool> get, Action<bool> set) { this.get = get; this.set = set; }
        public override int Width { get { return Theme.S(46); } }
        public override int Height { get { return Theme.S(24); } }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            bool on = get();
            if (on) Theme.FillRound(g, Theme.Mix(Theme.Accent, Theme.Overlay, 0.12 * hover), r, r.Height / 2f);
            else
            {
                Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(8 + 9 * hover)), r, r.Height / 2f);
                using (var p = new Pen(Theme.TextDim))
                using (var path = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), r.Height / 2f))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawPath(p, path);
                }
            }
            int d = r.Height - Theme.S(8);
            int x = on ? r.Right - Theme.S(4) - d : r.X + Theme.S(4);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(on ? Color.White : Theme.TextDim)) g.FillEllipse(b, x, r.Y + Theme.S(4), d, d);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v) { set(!get()); v.Invalidate(); }
    }

    internal sealed class SButton : SWidget
    {
        readonly string text, icon; readonly bool accent; readonly Action click;
        public SButton(string text, string icon, bool accent, Action click) { this.text = text; this.icon = icon; this.accent = accent; this.click = click; }
        public override int Width
        {
            get
            {
                int w = Theme.S(28);
                if (!string.IsNullOrEmpty(text)) w += TextRenderer.MeasureText(text, Theme.UiMedium(9.5f)).Width;
                if (icon != null) w += string.IsNullOrEmpty(text) ? Theme.S(8) : Theme.S(24);
                return w;
            }
        }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            if (accent) Theme.FillRound(g, Theme.Mix(Theme.Accent, Theme.Overlay, 0.12 * hover), r, r.Height / 2f);
            else Box(g, r, hover, r.Height / 2f);
            Color fg = accent ? Color.White : Theme.Text;
            int x = r.X + Theme.S(14);
            if (icon != null)
            {
                if (string.IsNullOrEmpty(text)) { Theme.GlyphCenter(g, icon, 11f, fg, r); return; }
                Theme.GlyphCenter(g, icon, 11f, fg, new Rectangle(x, r.Y, Theme.S(18), r.Height));
                x += Theme.S(24);
            }
            Theme.TextLeft(g, text, Theme.UiMedium(9.5f), fg, new Rectangle(x, r.Y, r.Right - x, r.Height));
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v) { if (click != null) click(); v.Invalidate(); }
    }

    internal sealed class SSlider : SWidget
    {
        readonly Func<double> get; readonly Action<double> set; readonly Func<string> label; readonly int width;
        bool dragging;
        public SSlider(Func<double> get, Action<double> set, Func<string> label, int width) { this.get = get; this.set = set; this.label = label; this.width = width; }
        public override int Width { get { return Theme.S(width) + Theme.S(74); } }
        public override bool WantsDrag { get { return true; } }

        Rectangle Track(Rectangle r) { return new Rectangle(r.X, r.Y, r.Width - Theme.S(74), r.Height); }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            var s = new CanvasSlider { Bounds = Track(r), Value = get() };
            s.Hover = hover > 0.5f; s.Dragging = dragging;
            CanvasControl.DrawSlider(g, s, hover);
            Theme.DrawText(g, label(), Theme.UiMedium(9.5f), Theme.TextDim, new Rectangle(r.Right - Theme.S(66), r.Y, Theme.S(66), r.Height),
                           TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        void Apply(Point p, Rectangle r)
        {
            Rectangle t = Track(r);
            double v = (p.X - t.X - Theme.S(6)) / (double)Math.Max(1, t.Width - Theme.S(12));
            set(Math.Max(0, Math.Min(1, v)));
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v) { dragging = true; Apply(p, r); v.Invalidate(); }
        public override void MouseMove(Point p, Rectangle r, SettingsView v) { if (dragging) { Apply(p, r); v.Invalidate(); } }
        public override void MouseUp(Point p, Rectangle r, SettingsView v) { dragging = false; v.Invalidate(); }
    }

    internal sealed class SSwatch : SWidget
    {
        readonly Func<Color> get; readonly Action click;
        public SSwatch(Func<Color> get, Action click) { this.get = get; this.click = click; }
        public override int Width { get { return Theme.S(118); } }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            Box(g, r, hover, Theme.S(8f));
            Color c = get();
            var sw = new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(6), r.Height - Theme.S(12), r.Height - Theme.S(12));
            Theme.FillRound(g, c, sw, Theme.S(5f));
            Theme.TextLeft(g, "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"), Theme.UiMedium(9.5f), Theme.Text,
                           new Rectangle(sw.Right + Theme.S(10), r.Y, r.Right - sw.Right - Theme.S(12), r.Height));
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v) { if (click != null) click(); v.Invalidate(); }
    }

    /// <summary>Full-width list of library folders, each with a remove button.</summary>
    internal sealed class SFolderList : SWidget
    {
        readonly Func<List<string>> items; readonly Action<string> remove;
        int hoverRow = -1;
        public SFolderList(Func<List<string>> items, Action<string> remove) { this.items = items; this.remove = remove; }
        int RowH { get { return Theme.S(44); } }
        public override int Height { get { return Math.Max(1, items().Count) * RowH; } }

        Rectangle RemoveRect(Rectangle row) { return new Rectangle(row.Right - Theme.S(40), row.Y + (row.Height - Theme.S(32)) / 2, Theme.S(32), Theme.S(32)); }

        public override void Paint(Graphics g, Rectangle r, float hover)
        {
            List<string> list = items();
            if (list.Count == 0)
            {
                Theme.TextLeft(g, "No folders yet — add the folders that hold your music.", Theme.Ui(9.5f), Theme.TextDim, new Rectangle(r.X + Theme.S(12), r.Y, r.Width, RowH));
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                var row = new Rectangle(r.X, r.Y + i * RowH, r.Width, RowH - Theme.S(4));
                float rh = i == hoverRow ? hover : 0f;
                Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(7 + 8 * rh)), row, Theme.S(8f));
                Theme.GlyphCenter(g, Glyphs.FolderPlain, 12f, Theme.Accent, new Rectangle(row.X + Theme.S(10), row.Y, Theme.S(24), row.Height));
                Theme.TextLeft(g, list[i], Theme.Ui(9.5f), Theme.Text, new Rectangle(row.X + Theme.S(44), row.Y, row.Width - Theme.S(96), row.Height));
                Rectangle x = RemoveRect(row);
                if (rh > 0) Theme.FillRound(g, Theme.Alpha(Theme.Bad, (int)(40 * rh)), x, Theme.S(6f));
                Theme.GlyphCenter(g, Glyphs.Close, 11f, Theme.Mix(Theme.TextDim, Theme.Bad, rh), x);
            }
        }

        public override Cursor CursorAt(Point p, Rectangle r)
        {
            int i = (p.Y - r.Y) / RowH;
            return i >= 0 && i < items().Count && RemoveRect(new Rectangle(r.X, r.Y + i * RowH, r.Width, RowH - Theme.S(4))).Contains(p) ? Cursors.Hand : Cursors.Default;
        }

        public override void MouseMove(Point p, Rectangle r, SettingsView v)
        {
            int i = (p.Y - r.Y) / RowH;
            if (i >= items().Count) i = -1;
            if (i != hoverRow) { hoverRow = i; v.Invalidate(); }
        }

        public override void MouseDown(Point p, Rectangle r, SettingsView v)
        {
            List<string> list = items();
            int i = (p.Y - r.Y) / RowH;
            if (i < 0 || i >= list.Count) return;
            if (RemoveRect(new Rectangle(r.X, r.Y + i * RowH, r.Width, RowH - Theme.S(4))).Contains(p)) remove(list[i]);
        }
    }

    internal sealed class SRow
    {
        public string Icon, Title;
        /// <summary>Optional picture shown instead of the icon glyph (e.g. the app logo).</summary>
        public Image Picture;
        public Func<string> Desc;
        public readonly List<SWidget> Right = new List<SWidget>();
        public SWidget Full;
        internal Rectangle Bounds;
    }

    internal sealed class SSection
    {
        public string Title;
        public readonly List<SRow> Rows = new List<SRow>();
        internal Rectangle CardBounds;
    }

    // ======================================================================= the page

    /// <summary>Modern settings page: titled sections of rounded cards, each row with an icon, title, description and controls.</summary>
    internal sealed class SettingsView : ScrollCanvas
    {
        public readonly List<SSection> Sections = new List<SSection>();
        public string PageTitle = "Settings", PageSubtitle = "Appearance, audio output, lyrics and your music library";
        int contentHeight;
        SWidget hover, captured;
        Rectangle capturedRect;

        protected override int ContentHeight { get { return contentHeight; } }

        public SSection AddSection(string title) { var s = new SSection { Title = title }; Sections.Add(s); return s; }

        public static SRow Row(SSection s, string icon, string title, Func<string> desc, params SWidget[] right)
        {
            var r = new SRow { Icon = icon, Title = title, Desc = desc };
            r.Right.AddRange(right);
            s.Rows.Add(r);
            return r;
        }

        int CardWidth { get { return Math.Min(Width - Theme.S(64), Theme.S(900)); } }
        int X0 { get { return Theme.S(32); } }

        void DoLayout()
        {
            int y = Theme.S(108);
            int cw = CardWidth;
            foreach (SSection s in Sections)
            {
                y += Theme.S(34);
                int cardTop = y;
                foreach (SRow r in s.Rows)
                {
                    bool hasDesc = r.Desc != null && !string.IsNullOrEmpty(r.Desc());
                    int head = hasDesc ? Theme.S(66) : Theme.S(54);
                    int maxW = 0;
                    foreach (SWidget w in r.Right) maxW = Math.Max(maxW, w.Height);
                    head = Math.Max(head, maxW + Theme.S(20));
                    int h = head + (r.Full != null ? r.Full.Height + Theme.S(14) : 0);
                    r.Bounds = new Rectangle(X0, y, cw, h);
                    int x = X0 + cw - Theme.S(18);
                    for (int i = r.Right.Count - 1; i >= 0; i--)
                    {
                        SWidget w = r.Right[i];
                        if (!w.IsVisible) { w.Bounds = Rectangle.Empty; continue; }
                        x -= w.Width;
                        w.Bounds = new Rectangle(x, y + (head - w.Height) / 2, w.Width, w.Height);
                        x -= Theme.S(8);
                    }
                    if (r.Full != null) r.Full.Bounds = new Rectangle(X0 + Theme.S(18), y + head, cw - Theme.S(36), r.Full.Height);
                    y += h;
                }
                s.CardBounds = new Rectangle(X0, cardTop, cw, y - cardTop);
                y += Theme.S(26);
            }
            contentHeight = y + Theme.S(20);
        }

        Rectangle ToView(Rectangle r) { return new Rectangle(r.X, r.Y - ScrollY, r.Width, r.Height); }

        IEnumerable<SWidget> AllWidgets()
        {
            foreach (SSection s in Sections)
                foreach (SRow r in s.Rows)
                {
                    foreach (SWidget w in r.Right) yield return w;
                    if (r.Full != null) yield return r.Full;
                }
        }

        SWidget WidgetAt(Point p)
        {
            foreach (SWidget w in AllWidgets()) if (w.IsVisible && ToView(w.Bounds).Contains(p)) return w;
            return null;
        }

        protected override void OnResize(EventArgs e) { DoLayout(); base.OnResize(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBackground(g);
            DoLayout();
            int sy = ScrollY;
            Theme.TextLeft(g, PageTitle, Theme.UiSemibold(21f), Theme.Text, new Rectangle(X0, Theme.S(18) - sy, CardWidth, Theme.S(48)));
            Theme.TextLeft(g, PageSubtitle, Theme.Ui(9.5f), Theme.TextDim, new Rectangle(X0 + Theme.S(2), Theme.S(66) - sy, CardWidth, Theme.S(22)));

            foreach (SSection s in Sections)
            {
                Rectangle card = ToView(s.CardBounds);
                if (card.Bottom < -Theme.S(40) || card.Top > Height + Theme.S(40)) continue;
                Theme.TextLeft(g, s.Title, Theme.UiSemibold(11f), Theme.Text, new Rectangle(X0 + Theme.S(2), card.Y - Theme.S(32), CardWidth, Theme.S(26)));
                Theme.FillRound(g, Theme.PanelFill(Theme.Card), card, Theme.S(12f));
                using (var p = new Pen(Theme.PanelBorder(Theme.Border)))
                using (var path = Theme.Round(new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1, card.Height - 1), Theme.S(12f)))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawPath(p, path);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                }
                for (int i = 0; i < s.Rows.Count; i++)
                {
                    SRow r = s.Rows[i];
                    Rectangle rb = ToView(r.Bounds);
                    if (i > 0) using (var p = new Pen(Theme.PanelBorder(Theme.Border))) g.DrawLine(p, rb.X + Theme.S(18), rb.Y, rb.Right - Theme.S(18), rb.Y);
                    string desc = r.Desc != null ? r.Desc() : null;
                    bool hasDesc = !string.IsNullOrEmpty(desc);
                    int head = r.Full != null ? r.Full.Bounds.Y - r.Bounds.Y : r.Bounds.Height;
                    int textX = rb.X + Theme.S(18);
                    if (r.Picture != null)
                    {
                        int ps = Theme.S(40);
                        Theme.DrawAppIcon(g, new Rectangle(textX - Theme.S(6), rb.Y + (head - ps) / 2, ps, ps));
                        textX += Theme.S(42);
                    }
                    else if (r.Icon != null)
                    {
                        Theme.GlyphCenter(g, r.Icon, 14f, Theme.Text, new Rectangle(textX, rb.Y, Theme.S(26), head));
                        textX += Theme.S(42);
                    }
                    SWidget firstRight = r.Right.Find(w => w.IsVisible);
                    int textRight = firstRight != null ? ToView(firstRight.Bounds).X - Theme.S(16) : rb.Right - Theme.S(18);
                    if (hasDesc)
                    {
                        Theme.TextLeft(g, r.Title, Theme.UiMedium(10f), Theme.Text, new Rectangle(textX, rb.Y + head / 2 - Theme.S(21), textRight - textX, Theme.S(22)));
                        TextRenderer.DrawText(g, desc, Theme.Ui(8.5f), new Rectangle(textX, rb.Y + head / 2 + Theme.S(1), textRight - textX, Theme.S(20)), Theme.TextDim,
                            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    }
                    else Theme.TextLeft(g, r.Title, Theme.UiMedium(10f), Theme.Text, new Rectangle(textX, rb.Y, textRight - textX, head));

                    foreach (SWidget w in r.Right) PaintWidget(g, w);
                    if (r.Full != null) PaintWidget(g, r.Full);
                }
            }
            PaintScrollbar(g);
        }

        void PaintWidget(Graphics g, SWidget w)
        {
            if (!w.IsVisible) return;
            Rectangle r = ToView(w.Bounds);
            w.Paint(g, r, w.IsEnabled ? Fade.Get(w) : 0f);
            // Disabled: wash the control out with the card colour so it reads as unavailable.
            if (!w.IsEnabled) using (var b = new SolidBrush(Theme.Alpha(Theme.Card, 150))) g.FillRectangle(b, Rectangle.Inflate(r, 2, 2));
        }

        protected override Rectangle FadeBounds(object key)
        {
            var w = key as SWidget;
            return w != null ? Rectangle.Inflate(ToView(w.Bounds), 2, 2) : base.FadeBounds(key);
        }

        protected override Cursor CursorAt(Point p)
        {
            SWidget w = captured ?? WidgetAt(p);
            return w != null ? w.CursorAt(p, ToView(w.Bounds)) : Cursors.Default;
        }

        protected override void OnContentMouseMove(MouseEventArgs e)
        {
            if (captured != null) { captured.MouseMove(e.Location, ToView(captured.Bounds), this); return; }
            SWidget w = WidgetAt(e.Location);
            if (w != hover) { FadeHover(hover, w); hover = w; }
            if (w != null) w.MouseMove(e.Location, ToView(w.Bounds), this);
        }

        protected override void OnContentMouseLeave() { if (hover != null) { FadeHover(hover, null); hover = null; } }

        protected override void OnContentMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            SWidget w = WidgetAt(e.Location);
            if (w == null || !w.IsEnabled) return;
            if (w.WantsDrag) { captured = w; capturedRect = ToView(w.Bounds); Capture = true; }
            w.MouseDown(e.Location, ToView(w.Bounds), this);
        }

        protected override void OnContentMouseUp(MouseEventArgs e)
        {
            if (captured == null) return;
            captured.MouseUp(e.Location, capturedRect, this);
            captured = null; Capture = false;
        }
    }
}

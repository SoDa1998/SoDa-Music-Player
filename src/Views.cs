using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    internal enum StepState { Untouched, Changed, Info }

    /// <summary>One stage of the signal-path view: what happens to the audio there, and whether it changes the samples.</summary>
    internal sealed class SignalStep
    {
        public string Title, Detail;
        public StepState State;
        public SignalStep(string title, string detail, StepState state) { Title = title; Detail = detail; State = state; }
    }

    /// <summary>What the views need from the main window (implemented by MainForm).</summary>
    internal interface IPlayerHost
    {
        TrackInfo NowPlaying { get; }
        bool IsPlaying { get; }
        bool IsRunning { get; }
        double Position { get; }
        double Volume { get; }
        int RepeatMode { get; }
        string BadgeText { get; }
        Color BadgeColor { get; }
        string OutputLine { get; }
        string PathLine { get; }
        string StatusText { get; }
        bool StatusIsError { get; }
        bool IsMaximized { get; }
        Backdrop Backdrop { get; }
        Control BackdropHost { get; }
        List<TrackInfo> QueueSnapshot();

        /// <summary>Decoded audio for the spectrum visualizer (read-only).</summary>
        SpectrumTap SpectrumTap { get; }
        void PlayPause();
        void StopPlayback();
        void Next();
        void Previous();
        void SeekFraction(double fraction);
        void SetVolume(double v);
        void CycleRepeat();
        void ShuffleQueue();
        void SaveQueueAsPlaylist();
        void ToggleQueue();
        void ShowNowPlaying();
        /// <summary>Leaves Now Playing for the page that was open before it (the chevron in its top-left corner).</summary>
        void LeaveNowPlaying();
        /// <summary>Time left on the sleep timer ("23:41", "End of song"), or null when it is off.</summary>
        string SleepTimerText { get; }
        /// <summary>The live signal path, source file to DAC (empty when nothing is loaded).</summary>
        List<SignalStep> SignalPath();
        /// <summary>One-line verdict under the signal path.</summary>
        string SignalVerdict { get; }
        void ShowSleepMenu(Control owner, Point p);
        void PlayQueueIndex(int index);
        void RemoveFromQueue(int index);
        void ClearQueue();
        void BeginWindowDrag();
        void ToggleMaximize();
        void MinimizeWindow();
        void CloseWindow();
        bool IsFullscreen { get; }
        void ToggleFullscreen();
        void SeekSeconds(double seconds);
        void BackdropChanged();
    }

    /// <summary>Blurred, darkened album art used behind the Now Playing page (like the reference design).</summary>
    internal sealed class Backdrop
    {
        Image source;
        Bitmap heavy, light;                 // small blurred versions of the artwork (heavy = normal, light = lyrics mode)
        GdiSurface heavySurface, lightSurface;
        Size cacheSize;
        int generation;                      // bumps on every change, so stale background renders are thrown away
        volatile bool lightPending;
        Bitmap lightReady;                   // finished by the worker, adopted on the UI thread
        int lightReadyGeneration;
        readonly object lk = new object();

        /// <summary>0 = normal (heavily blurred art), 1 = lyrics mode (art only slightly blurred, as the page background).</summary>
        public double LyricsMix;

        /// <summary>True while Now Playing is shown: the backdrop then spans the whole window, seen through a translucent sidebar.</summary>
        public bool BehindSidebar;

        /// <summary>
        /// Set when a control asked for the backdrop while the window had no size (minimized, or mid-way through a
        /// full-screen / maximize change). That control is left unpainted, so MainForm repaints the window once it has a size again.
        /// </summary>
        public volatile bool MissedPaint;

        /// <summary>Returns true when the image actually changed.</summary>
        public bool SetSource(Image img)
        {
            if (ReferenceEquals(img, source)) return false;
            source = img;
            heavy = img == null ? null : Blur(img, 14);
            light = img == null ? null : Blur(img, 72);
            Invalidate();
            return true;
        }

        /// <summary>Drop the rendered layers (size or theme changed).</summary>
        public void Invalidate()
        {
            generation++;
            if (heavySurface != null) { heavySurface.Dispose(); heavySurface = null; }
            if (lightSurface != null) { lightSurface.Dispose(); lightSurface = null; }
            lock (lk) { if (lightReady != null) { lightReady.Dispose(); lightReady = null; } }
        }

        /// <summary>Downscale to <paramref name="detail"/> px, scale back up smoothly: smaller = blurrier.</summary>
        static Bitmap Blur(Image img, int detail)
        {
            var tiny = new Bitmap(detail, detail, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(img, new Rectangle(0, 0, detail, detail));
            }
            int big = Math.Max(96, detail * 6);
            var mid = new Bitmap(big, big, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(mid))
            using (var attrs = new ImageAttributes())
            {
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(tiny, new Rectangle(0, 0, big, big), 0, 0, detail, detail, GraphicsUnit.Pixel, attrs);
            }
            tiny.Dispose();
            return mid;
        }

        /// <summary>Renders a full-size layer. Uses only its arguments and Theme colours, so it is safe on a worker thread.</summary>
        static Bitmap Render(Size size, Bitmap art, bool lyrics, bool dark, Color bg, Color accent, bool plainDark, bool glass, double glassLevel)
        {
            var cache = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
            var all = new Rectangle(0, 0, cache.Width, cache.Height);
            using (var g = Graphics.FromImage(cache))
            {
                if (art != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    using (var attrs = new ImageAttributes())
                    {
                        attrs.SetWrapMode(WrapMode.TileFlipXY);
                        Theme.DrawImageCover(g, art, Rectangle.Inflate(all, all.Width / 10, all.Height / 10), attrs);
                    }
                    // Darken (dark theme) or wash out (light theme) so text stays readable; lyrics need a little more.
                    // Frosted glass lets much more of the picture through, since the panels on top carry their own tint.
                    Color top, bottom;
                    int gt = (int)Math.Round(glassLevel * 100), gb = (int)Math.Round(glassLevel * 100);   // glass: clearer glass = lighter veil
                    if (dark)
                    {
                        top = Color.FromArgb(glass ? 105 - gt * 7 / 10 : lyrics ? 150 : 120, 14, 14, 16);       // neutral charcoal tint
                        bottom = Color.FromArgb(glass ? 150 - gb * 7 / 10 : lyrics ? 215 : 205, 16, 16, 18);
                    }
                    else
                    {
                        top = Theme.Alpha(bg, glass ? 150 - gt * 8 / 10 : lyrics ? 175 : 150);
                        bottom = Theme.Alpha(bg, glass ? 205 - gb * 9 / 10 : lyrics ? 225 : 215);
                    }
                    using (var b = new LinearGradientBrush(all, top, bottom, 90f)) g.FillRectangle(b, all);
                }
                else if (glass)
                {
                    // No picture (nothing playing, no wallpaper): a soft two-tone wash of the accent colour, so glass still has something to frost.
                    Color a = Theme.Mix(bg, accent, dark ? 0.55 : 0.40), b2 = Theme.Mix(bg, Theme.Mix(accent, dark ? Color.Black : Color.White, 0.5), dark ? 0.35 : 0.25);
                    using (var b = new LinearGradientBrush(all, a, b2, 50f)) g.FillRectangle(b, all);
                }
                else
                {
                    Color a = plainDark ? Color.FromArgb(56, 56, 62) : Theme.Mix(bg, accent, dark ? 0.22 : 0.16);
                    using (var b = new LinearGradientBrush(all, a, bg, 60f)) g.FillRectangle(b, all);
                }
            }
            return cache;
        }

        Bitmap RenderNow(Size size, Bitmap art, bool lyrics)
        {
            return Render(size, art, lyrics, Theme.IsDark, Theme.Bg, Theme.Accent, Theme.IsDark && Theme.Mode == ThemeMode.Dark, Theme.Glass, Theme.GlassLevel);
        }

        /// <summary>Starts rendering the lyrics layer in the background, so the first transition frame has nothing to build.</summary>
        void PrewarmLight(Size size)
        {
            if (lightPending || lightSurface != null) return;
            lightPending = true;
            int gen = generation;
            Bitmap art = light == null ? null : (Bitmap)light.Clone();   // the worker gets its own copy
            bool dark = Theme.IsDark, plainDark = Theme.IsDark && Theme.Mode == ThemeMode.Dark, glass = Theme.Glass;
            double level = Theme.GlassLevel;
            Color bg = Theme.Bg, accent = Theme.Accent;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap result = null;
                try { result = Render(size, art, true, dark, bg, accent, plainDark, glass, level); } catch { }
                if (art != null) art.Dispose();
                lock (lk)
                {
                    if (lightReady != null) lightReady.Dispose();
                    lightReady = result; lightReadyGeneration = gen;
                }
                lightPending = false;
            });
        }

        /// <summary>Paints the part of the backdrop that lies under <paramref name="c"/> (backdrop spans <paramref name="host"/>).</summary>
        public void Paint(Graphics g, Control c, Control host)
        {
            Size hs = host.ClientSize;
            // Minimized: the window has no client area. Painting a flat colour here left a dark strip on the title bar
            // after restoring (Windows shows the stored image instead of repainting it), and resetting the cache to 0×0
            // forced a full re-render. Paint nothing; MainForm repaints everything when the window comes back.
            if (hs.Width <= 0 || hs.Height <= 0) { MissedPaint = true; return; }
            if (cacheSize != hs) { Invalidate(); cacheSize = hs; }
            if (heavySurface == null)
                using (Bitmap b = RenderNow(hs, heavy, false)) heavySurface = new GdiSurface(b);

            // Adopt a background-rendered lyrics layer if it still matches; otherwise start one.
            if (lightSurface == null)
            {
                Bitmap ready = null;
                lock (lk)
                {
                    if (lightReady != null && lightReadyGeneration == generation && lightReady.Width == hs.Width && lightReady.Height == hs.Height)
                    { ready = lightReady; lightReady = null; }
                }
                if (ready != null) { lightSurface = new GdiSurface(ready); ready.Dispose(); }
                else PrewarmLight(hs);
            }

            Point off = host.PointToClient(c.PointToScreen(Point.Empty));
            var dst = new Rectangle(0, 0, c.Width, c.Height);
            double mix = Math.Max(0, Math.Min(1, LyricsMix));
            if (mix > 0 && lightSurface == null)
                using (Bitmap b = RenderNow(hs, light, true)) lightSurface = new GdiSurface(b);   // fallback: not prewarmed yet

            if (mix < 1) heavySurface.Draw(g, dst, off, 255, false);
            if (mix > 0) lightSurface.Draw(g, dst, off, (byte)Math.Round(255 * mix), false);   // GDI AlphaBlend cross-fade
        }

        GdiSurface panel;
        Color panelColor;

        /// <summary>The sidebar's part of the backdrop under a translucent sidebar-coloured panel (one extra AlphaBlend).</summary>
        public void PaintPanel(Graphics g, Control c, Control host, Color color, int width)
        {
            Paint(g, c, host);
            if (panel == null || panelColor != color)
            {
                if (panel != null) panel.Dispose();
                panel = new GdiSurface(1, 1);
                System.Runtime.InteropServices.Marshal.WriteInt32(panel.Bits, unchecked((int)(0xFF000000u | (uint)(color.R << 16 | color.G << 8 | color.B))));
                panelColor = color;
            }
            int alpha = Theme.Glass ? Theme.GlassPanelAlpha(215, 100) : 170;
            panel.DrawStretched(g, new Rectangle(0, 0, width, c.Height), new Rectangle(0, 0, 1, 1), (byte)alpha);
            if (Theme.Glass)
                using (var p = new Pen(Theme.PanelBorder(Theme.Border))) g.DrawLine(p, width - 1, 0, width - 1, c.Height);   // lit edge
        }

    }

    // ======================================================================= window title bar

    internal sealed class TitleBar : CanvasControl
    {
        readonly IPlayerHost host;
        readonly CanvasButton min, max, close;
        public bool UseBackdrop;

        public TitleBar(IPlayerHost host)
        {
            this.host = host;
            min = AddButton(Glyphs.Minimize, BtnStyle.Window, 12f, "Minimize", host.MinimizeWindow);
            max = AddButton(Glyphs.Maximize, BtnStyle.Window, 11f, "Maximize", host.ToggleMaximize);
            close = AddButton(Glyphs.Close, BtnStyle.WindowClose, 12f, "Close", host.CloseWindow);
            Height = Theme.S(44);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int w = Theme.S(44), h = Theme.S(32), y = Theme.S(8), x = Width - Theme.S(10) - w;
            close.Bounds = new Rectangle(x, y, w, h);
            max.Bounds = new Rectangle(x - w - Theme.S(4), y, w, h);
            min.Bounds = new Rectangle(x - 2 * (w + Theme.S(4)), y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (UseBackdrop) host.Backdrop.Paint(e.Graphics, this, host.BackdropHost);
            else e.Graphics.Clear(Theme.Bg);
            max.Glyph = host.IsMaximized ? Glyphs.Restore : Glyphs.Maximize;
            max.Tip = host.IsMaximized ? "Restore" : "Maximize";
            PaintWidgets(e.Graphics);
        }

        protected override void OnCanvasMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 1) host.BeginWindowDrag();
        }

        protected override void OnCanvasDoubleClick(MouseEventArgs e) { host.ToggleMaximize(); }
    }

    // ======================================================================= sidebar

    internal enum NavPage { NowPlaying, Albums, Artists, Songs, Folders, Playlists, Equalizer, Settings }

    /// <summary>
    /// Navigation + queue. Drag the right edge to resize (220–480 px); drag it far left, or use the header button, to collapse it
    /// to an icon rail (72 px) with tooltips.
    /// </summary>
    internal sealed class Sidebar : CanvasControl
    {
        static readonly NavPage[] NavItems = { NavPage.NowPlaying, NavPage.Albums, NavPage.Artists, NavPage.Songs, NavPage.Folders, NavPage.Playlists, NavPage.Equalizer, NavPage.Settings };
        static readonly string[] NavText = { "Now Playing", "Albums", "Artists", "Songs", "Folders", "Playlists", "Equalizer", "Settings" };
        static readonly string[] NavGlyph = { Glyphs.NowPlaying, Glyphs.Album, Glyphs.Artist, Glyphs.Songs, Glyphs.FolderPlain, Glyphs.Playlist, Glyphs.Equalizer, Glyphs.Settings };

        readonly IPlayerHost host;
        readonly CanvasButton collapseBtn;
        public event Action<NavPage> NavClicked;

        // ---- animation state: everything that moves is eased on the display frame clock (see OnAnimate)
        const double WidthSeconds = 0.28;
        double wFrom, wTo, wT = 1;       // width tween (collapse / expand); wT = 1 when idle
        double selPos = -1, selTarget;   // the selection pill's position as a (fractional) nav index
        double qOpen = 1;                // queue section reveal 0 (closed) … 1 (open)
        int dragWidth = -1;              // width requested by an edge drag, followed by the next frame
        NavPage selected;
        bool queueExpanded = true;

        /// <summary>The highlighted page; the selection pill slides to it.</summary>
        public NavPage Selected
        {
            get { return selected; }
            set
            {
                int i = Array.IndexOf(NavItems, value);
                if (selected == value && selPos >= 0) return;
                selected = value;
                selTarget = i;
                if (selPos < 0 || !Visible || !IsHandleCreated) selPos = i; else Animate();
                Invalidate();
            }
        }

        public bool QueueExpanded
        {
            get { return queueExpanded; }
            set
            {
                if (queueExpanded == value) return;
                queueExpanded = value;
                if (Visible && IsHandleCreated) Animate(); else qOpen = value ? 1 : 0;
                Invalidate();
            }
        }
        /// <summary>Raised after the user resized or collapsed the sidebar (so it can be saved).</summary>
        public event Action LayoutChanged;
        int hoverNav = -1, hoverQueue = -1, selQueue = -1, queueScroll;
        double queuePos, queueTarget;   // smooth queue scrolling
        static readonly object HeaderKey = new object();
        static object NavKey(int i) { return i < 0 ? null : (object)i; }
        static object QueueKey(int i) { return i < 0 ? null : (object)(1000 + i); }
        bool hoverQueueHeader, resizing;
        bool collapsed;
        int expandedWidth;
        List<TrackInfo> queue = new List<TrackInfo>();

        public static int MinWidth { get { return Theme.S(220); } }
        public static int MaxWidth { get { return Theme.S(480); } }
        public static int RailWidth { get { return Theme.S(72); } }

        // ---- shell layout. The pages are laid out for the width the sidebar is heading to (SettleWidth) and stay put while it
        // glides there, so an animation frame repaints only the sidebar. The control itself is as wide as the larger of the
        // visual width and the settled width: when expanding, the part not yet reached is painted as page background; when
        // collapsing, the sidebar sits above the pages and uncovers them. MainForm places the pages (ShellChanged).
        int vw;
        int settleReported = -1;
        public event Action ShellChanged;

        /// <summary>The width drawn right now (animated). The control's own Width is max(VW, SettleWidth).</summary>
        int VW
        {
            get { return vw; }
            set
            {
                if (vw == value) return;
                vw = value;
                ApplyShellWidth();
                LayoutWidgets();
                Invalidate();
            }
        }

        /// <summary>Where the pages start: the width the sidebar has or is heading to (while dragging its edge: the width it has).</summary>
        public int SettleWidth { get { return resizing || dragWidth >= 0 ? vw : (collapsed ? RailWidth : expandedWidth); } }

        void ApplyShellWidth()
        {
            int cw = Math.Max(vw, SettleWidth);
            if (Width != cw) Width = cw;
            int settle = SettleWidth;
            if (settle != settleReported)
            {
                settleReported = settle;
                var h = ShellChanged;
                if (h != null) h();
            }
        }

        public Sidebar(IPlayerHost host)
        {
            this.host = host;
            BackColor = Theme.Sidebar;
            collapseBtn = AddButton(Glyphs.SidebarCollapse, BtnStyle.Ghost, 13f, "Collapse sidebar", delegate { SetCollapsed(!collapsed, true); });
            expandedWidth = Theme.S(300);
            VW = expandedWidth;
        }

        public bool Collapsed { get { return collapsed; } }
        public int ExpandedWidth { get { return expandedWidth; } }

        public void SetLayout(int width, bool isCollapsed)
        {
            expandedWidth = Math.Max(MinWidth, Math.Min(MaxWidth, width));
            SetCollapsed(isCollapsed, false);
        }

        /// <summary>Collapses to the icon rail or expands. With <paramref name="notify"/> (a user action) the width glides there.</summary>
        public void SetCollapsed(bool value, bool notify)
        {
            bool changed = collapsed != value;
            collapsed = value;
            collapseBtn.Glyph = collapsed ? Glyphs.SidebarExpand : Glyphs.SidebarCollapse;
            collapseBtn.Tip = collapsed ? "Expand sidebar" : "Collapse sidebar";
            hoverNav = hoverQueue = -1;
            int target = collapsed ? RailWidth : expandedWidth;
            if (notify && changed && Visible && IsHandleCreated && VW != target) { wFrom = VW; wTo = target; wT = 0; dragWidth = -1; Animate(); }
            else { wT = 1; if (VW != target) VW = target; LayoutWidgets(); }
            ApplyShellWidth();   // the pages move to the settled width right away (once), not frame by frame
            Invalidate();
            if (notify && LayoutChanged != null) LayoutChanged();
        }

        // ---- layout. Everything is derived from the current VW, so it stays continuous while the width animates.

        /// <summary>0 = icon rail … 1 = fully expanded.</summary>
        double Frac { get { return Math.Max(0, Math.Min(1, (VW - RailWidth) / (double)Math.Max(1, expandedWidth - RailWidth))); } }
        static double Smooth(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }
        /// <summary>Opacity of labels, queue rows and the logo: gone in the rail, fading in over the second half of the expansion.</summary>
        double TextFade { get { return Smooth((Frac - 0.35) / 0.55); } }
        static int Lerp(int a, int b, double t) { return (int)Math.Round(a + (b - a) * t); }
        /// <summary>Content laid out for the expanded sidebar keeps its size while the width is still smaller (it is clipped instead of squeezed).</summary>
        int LayoutW { get { return Math.Max(VW, MinWidth); } }

        int HeaderH { get { return Theme.S(64); } }
        int NavH { get { return Theme.S(46); } }
        Rectangle NavRect(int i) { return new Rectangle(Theme.S(10), HeaderH + i * (NavH + Theme.S(4)), VW - Theme.S(20), NavH); }
        Rectangle QueueHeaderRect
        {
            get
            {
                Rectangle last = NavRect(NavItems.Length - 1);
                return new Rectangle(Theme.S(10), last.Bottom + Theme.S(18), VW - Theme.S(20), Lerp(NavH, Theme.S(40), Frac));
            }
        }
        Rectangle StatusRect { get { return new Rectangle(Theme.S(20), Height - Theme.S(52), LayoutW - Theme.S(40), Theme.S(44)); } }
        /// <summary>The whole area the queue rows can use.</summary>
        Rectangle QueueArea
        {
            get
            {
                int top = QueueHeaderRect.Bottom + Theme.S(4);
                return new Rectangle(0, top, VW, Math.Max(0, StatusRect.Top - Theme.S(6) - top));
            }
        }
        /// <summary>The part of it currently revealed (grows / shrinks when the queue is opened or closed).</summary>
        Rectangle QueueView
        {
            get { Rectangle a = QueueArea; a.Height = (int)Math.Round(a.Height * Smooth(qOpen)); return a; }
        }
        int RowH { get { return Theme.S(60); } }
        Rectangle QueueRow(int i)
        {
            Rectangle a = QueueArea;
            return new Rectangle(Theme.S(10), a.Y + i * RowH - queueScroll, LayoutW - Theme.S(20), RowH - Theme.S(4));
        }
        bool InGrip(Point p) { return !collapsed && p.X >= VW - Theme.S(6); }

        void LayoutWidgets()
        {
            int s = Theme.S(36);
            int railX = (VW - s) / 2, wideX = VW - Theme.S(14) - s;
            collapseBtn.Bounds = new Rectangle(Lerp(railX, wideX, Frac), Theme.S(14), s, s);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutWidgets();
            if (wT >= 1 && dragWidth < 0) RefreshQueue();
            else   // while the width moves, only keep the queue scroll in range (no list copy per frame)
            {
                int max = Math.Max(0, queue.Count * RowH - QueueArea.Height);
                if (queueScroll > max) { queueScroll = max; queuePos = queueTarget = max; }
            }
        }

        /// <summary>Repaints only the playing queue row's equalizer bars.</summary>
        public void InvalidateNowPlaying()
        {
            if (collapsed || !queueExpanded || wT < 1) return;
            int i = queue.IndexOf(host.NowPlaying);
            if (i < 0) return;
            Rectangle r = QueueRow(i);
            Rectangle lead = Rectangle.Intersect(new Rectangle(r.X + Theme.S(10), r.Y, Theme.S(26), r.Height), QueueView);
            if (!lead.IsEmpty) Invalidate(lead);
        }

        public void RefreshQueue()
        {
            queue = host.QueueSnapshot();
            int max = Math.Max(0, queue.Count * RowH - QueueArea.Height);
            if (queueScroll > max) queueScroll = max;
            queuePos = queueTarget = queueScroll;
            if (selQueue >= queue.Count) selQueue = -1;
            Invalidate();
        }

        public void ScrollToCurrent()
        {
            int i = queue.IndexOf(host.NowPlaying);
            if (i < 0) return;
            Rectangle a = QueueArea;
            int top = i * RowH;
            if (top < queueScroll || top + RowH > queueScroll + a.Height) queueScroll = Math.Max(0, Math.Min(top - a.Height / 3, queue.Count * RowH - a.Height));
            queuePos = queueTarget = queueScroll;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // While expanding, the part of the control the sidebar has not reached yet shows the page background.
            if (Width > VW)
            {
                if (host.Backdrop.BehindSidebar) host.Backdrop.Paint(g, this, host.BackdropHost);
                else using (var gb = new SolidBrush(Theme.Bg)) g.FillRectangle(gb, VW, 0, Width - VW, Height);
            }
            g.SetClip(new Rectangle(0, 0, VW, Height), System.Drawing.Drawing2D.CombineMode.Intersect);
            // On Now Playing the blurred album art spans the whole window and shows through a translucent sidebar.
            if (host.Backdrop.BehindSidebar) host.Backdrop.PaintPanel(g, this, host.BackdropHost, BackColor, VW);
            else using (var sb = new SolidBrush(BackColor)) g.FillRectangle(sb, 0, 0, VW, Height);

            double fr = Frac, tf = TextFade;
            Color sbg = Theme.Sidebar;
            // Text cannot be alpha-blended with TextRenderer, so labels fade by mixing their colour toward the sidebar's.
            Func<Color, Color> F = delegate(Color c) { return tf >= 0.999 ? c : Theme.Mix(sbg, c, tf); };

            if (tf > 0.02)
            {
                var head = new Rectangle(Theme.S(16), Theme.S(12), Math.Max(Theme.S(150), VW - Theme.S(74)), Theme.S(40));
                Theme.DrawAppIcon(g, new Rectangle(head.X, head.Y, head.Height, head.Height), (float)tf);
                Theme.TextLeft(g, "SoDa Player", Theme.UiSemibold(11.5f), F(Theme.Text), new Rectangle(head.X + Theme.S(48), head.Y, head.Width - Theme.S(48), head.Height));
            }

            // The selection pill slides from one item to the next.
            if (selPos > -0.5)
            {
                Rectangle pr = NavRect(0);
                pr.Y = HeaderH + (int)Math.Round(selPos * (NavH + Theme.S(4)));
                Theme.FillRound(g, Theme.Select, pr, Theme.S(10f));
            }
            for (int i = 0; i < NavItems.Length; i++)
            {
                Rectangle r = NavRect(i);
                bool sel = Math.Abs(selPos - i) < 0.5;
                if (!sel) { float hv = Fade.Get(NavKey(i)); if (hv > 0) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(16 * hv)), r, Theme.S(10f)); }
                Color c = sel ? Theme.SelectText : Theme.Text;
                // The icon glides between the centre of the rail and its place beside the label.
                double cx = r.X + r.Width / 2.0 + ((r.X + Theme.S(26)) - (r.X + r.Width / 2.0)) * fr;
                Theme.GlyphCenter(g, NavGlyph[i], 14f, c, new Rectangle((int)Math.Round(cx - Theme.S(14)), r.Y, Theme.S(28), r.Height));
                if (tf > 0.02)
                    Theme.TextLeft(g, NavText[i], Theme.UiMedium(10f), F(c), new Rectangle(r.X + Theme.S(52), r.Y, Math.Max(Theme.S(120), r.Width - Theme.S(58)), r.Height));
            }

            Rectangle qh = QueueHeaderRect;
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, qh.X + Theme.S(8), qh.Y - Theme.S(9), qh.Right - Theme.S(8), qh.Y - Theme.S(9));
            float hh = Fade.Get(HeaderKey);
            if (hh > 0) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(14 * hh)), qh, Theme.S(10f));
            if (tf < 0.98)
            {
                // Rail look: a queue shortcut (opens the sidebar with the queue visible) with a count badge; fades out while expanding.
                double rf = 1 - tf;
                Theme.GlyphCenter(g, Glyphs.Queue, 14f, Theme.Mix(sbg, Theme.Text, rf), new Rectangle(qh.X, qh.Y, Math.Min(qh.Width, Theme.S(52)), qh.Height));
                if (queue.Count > 0 && rf > 0.5)
                {
                    string n = queue.Count > 99 ? "99+" : queue.Count.ToString();
                    Font f = Theme.UiSemibold(7f);
                    int w = Math.Max(Theme.S(16), TextRenderer.MeasureText(n, f).Width + Theme.S(6));
                    var badge = new Rectangle(qh.X + Math.Min(qh.Width, Theme.S(52)) / 2 + Theme.S(4), qh.Y + Theme.S(6), w, Theme.S(15));
                    Theme.FillRound(g, Theme.Accent, badge, badge.Height / 2f);
                    Theme.DrawText(g, n, f, Color.White, badge, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
            if (tf > 0.02)
            {
                Theme.GlyphCenter(g, qOpen > 0.5 ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10f, F(Theme.TextDim), new Rectangle(qh.X + Theme.S(10), qh.Y, Theme.S(20), qh.Height));
                Theme.TextLeft(g, "Queue", Theme.UiMedium(10f), F(Theme.Text), new Rectangle(qh.X + Theme.S(40), qh.Y, Math.Max(Theme.S(60), LayoutW - Theme.S(120)), qh.Height));
                Theme.DrawText(g, queue.Count.ToString(), Theme.Ui(9.5f), F(Theme.TextDim), new Rectangle(qh.X + LayoutW - Theme.S(20) - Theme.S(70), qh.Y, Theme.S(58), qh.Height),
                               TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }

            Rectangle view = QueueView;
            if (tf > 0.02 && view.Height > 0)
            {
                Rectangle area = QueueArea;
                g.SetClip(view);
                TrackInfo now = host.NowPlaying;
                int first = Math.Max(0, queueScroll / RowH), last = Math.Min(queue.Count - 1, (queueScroll + area.Height) / RowH + 1);
                for (int i = first; i <= last; i++)
                {
                    TrackInfo t = queue[i];
                    Rectangle r = QueueRow(i);
                    bool playing = t == now;
                    if (playing) Theme.FillRound(g, Theme.Alpha(Theme.Select, (int)(235 * tf)), r, Theme.S(10f));
                    else if (i == selQueue) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(20 * tf)), r, Theme.S(10f));
                    else { float hq = Fade.Get(QueueKey(i)); if (hq > 0) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(13 * hq * tf)), r, Theme.S(10f)); }

                    var lead = new Rectangle(r.X + Theme.S(10), r.Y, Theme.S(48), r.Height);
                    Color tc = playing ? Theme.SelectText : Theme.Text;
                    if (playing)
                    {
                        Theme.EqBars(g, new Rectangle(lead.X + Theme.S(2), r.Y + r.Height / 2 - Theme.S(7), Theme.S(12), Theme.S(14)), F(Theme.Accent), host.IsPlaying);
                        Theme.GlyphCenter(g, Glyphs.Music, 13f, F(Theme.Accent), new Rectangle(lead.X + Theme.S(22), r.Y, Theme.S(24), r.Height));
                    }
                    else Theme.GlyphCenter(g, Glyphs.Music, 13f, F(Theme.TextDim), new Rectangle(lead.X + Theme.S(18), r.Y, Theme.S(24), r.Height));

                    int tx = lead.Right + Theme.S(10), tw = r.Right - tx - Theme.S(62);
                    Theme.TextLeft(g, t.DisplayTitle, Theme.UiMedium(9.5f), F(tc), new Rectangle(tx, r.Y + Theme.S(8), tw, Theme.S(22)));
                    Theme.TextLeft(g, t.DisplayArtist, Theme.Ui(9f), F(Theme.TextDim), new Rectangle(tx, r.Y + Theme.S(30), tw, Theme.S(20)));
                    Theme.DrawText(g, Theme.Time(t.Duration), Theme.Ui(9.5f), F(Theme.TextDim), new Rectangle(r.Right - Theme.S(64), r.Y, Theme.S(52), r.Height),
                               TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                if (queue.Count == 0)
                    Theme.DrawText(g, "Play a song, album or artist to fill the queue.", Theme.Ui(9f), F(Theme.TextDim),
                               new Rectangle(area.X + Theme.S(24), area.Y + Theme.S(4), LayoutW - Theme.S(40), Theme.S(24)), TextFormatFlags.Left);
                g.ResetClip();
            }

            g.SetClip(new Rectangle(0, 0, VW, Height));   // the queue block above replaced the clip
            if (tf > 0.4)
            {
                Rectangle sr = StatusRect;
                string status = host.StatusText;
                if (!string.IsNullOrEmpty(status))
                    TextRenderer.DrawText(g, status, Theme.Ui(8.5f), sr, F(host.StatusIsError ? Theme.Bad : Theme.TextDim),
                        TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Bottom);
            }

            // Resize grip hint while dragging or hovering the edge.
            if (resizing) using (var b = new SolidBrush(Theme.Alpha(Theme.Accent, 160))) g.FillRectangle(b, VW - Theme.S(2), 0, Theme.S(2), Height);
            PaintWidgets(g);
        }

        int QueueIndexAt(Point p)
        {
            if (collapsed || !queueExpanded || !QueueView.Contains(p)) return -1;
            int i = (p.Y - QueueArea.Y + queueScroll) / RowH;
            return i >= 0 && i < queue.Count ? i : -1;
        }

        int NavIndexAt(Point p)
        {
            for (int i = 0; i < NavItems.Length; i++) if (NavRect(i).Contains(p)) return i;
            return -1;
        }

        protected override Cursor CursorAt(Point p)
        {
            if (resizing || InGrip(p)) return Cursors.SizeWE;
            return NavIndexAt(p) >= 0 || QueueHeaderRect.Contains(p) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnCanvasMouseMove(MouseEventArgs e)
        {
            if (resizing)
            {
                if (e.X < Theme.S(150)) { resizing = false; dragWidth = -1; Capture = false; SetCollapsed(true, true); return; }
                // Mouse moves arrive faster than frames: remember the request and let the frame clock follow it (OnAnimate),
                // so the window is laid out and painted once per display frame, not once per mouse event.
                expandedWidth = Math.Max(MinWidth, Math.Min(MaxWidth, e.X));
                dragWidth = expandedWidth;
                Animate();
                return;
            }
            int n = NavIndexAt(e.Location), q = QueueIndexAt(e.Location);
            bool qh = QueueHeaderRect.Contains(e.Location);
            if (n != hoverNav || q != hoverQueue || qh != hoverQueueHeader)
            {
                if (n != hoverNav) FadeHover(NavKey(hoverNav), NavKey(n));
                if (q != hoverQueue) FadeHover(QueueKey(hoverQueue), QueueKey(q));
                if (qh != hoverQueueHeader) FadeHover(qh ? null : HeaderKey, qh ? HeaderKey : null);
                hoverNav = n; hoverQueue = q; hoverQueueHeader = qh;
                if (collapsed) SetTip(n >= 0 ? NavText[n] : qh ? "Queue (" + queue.Count + ")" : null);
            }
        }

        protected override void OnCanvasMouseUp(MouseEventArgs e)
        {
            if (!resizing) return;
            resizing = false; Capture = false;
            dragWidth = -1;
            if (VW != expandedWidth) { VW = expandedWidth; SyncRedraw(); }
            ApplyShellWidth();
            Invalidate();
            if (LayoutChanged != null) LayoutChanged();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            FadeHover(NavKey(hoverNav), null); FadeHover(QueueKey(hoverQueue), null); FadeHover(HeaderKey, null);
            hoverNav = hoverQueue = -1; hoverQueueHeader = false;
            base.OnMouseLeave(e);
        }

        protected override void OnCanvasMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && InGrip(e.Location)) { resizing = true; Capture = true; Invalidate(); return; }
            if (e.Location.Y < HeaderH && e.Button == MouseButtons.Left) { host.BeginWindowDrag(); return; }
            int n = NavIndexAt(e.Location);
            if (n >= 0 && e.Button == MouseButtons.Left) { if (NavClicked != null) NavClicked(NavItems[n]); return; }
            if (QueueHeaderRect.Contains(e.Location))
            {
                if (e.Button == MouseButtons.Right)
                {
                    ContextMenuStrip qm = Theme.Menu();
                    var save = Theme.MenuItem(qm, "Save queue as playlist…", Glyphs.PlaylistAdd, delegate { host.SaveQueueAsPlaylist(); });
                    save.Enabled = queue.Count > 0;
                    Theme.MenuItem(qm, "Shuffle queue", Glyphs.Shuffle, delegate { host.ShuffleQueue(); }).Enabled = queue.Count > 1;
                    Theme.MenuItem(qm, "Clear queue", Glyphs.Trash, delegate { host.ClearQueue(); }).Enabled = queue.Count > 0;
                    qm.Show(this, e.Location);
                    return;
                }
                if (e.Button != MouseButtons.Left) return;
                if (collapsed) { QueueExpanded = true; SetCollapsed(false, true); ScrollToCurrent(); }
                else { QueueExpanded = !QueueExpanded; Invalidate(); }
                return;
            }
            int q = QueueIndexAt(e.Location);
            if (q >= 0)
            {
                selQueue = q; Invalidate();
                if (e.Button == MouseButtons.Right)
                {
                    int idx = q;
                    ContextMenuStrip m = Theme.Menu();
                    Theme.MenuItem(m, "Play", Glyphs.PlayOutline, delegate { host.PlayQueueIndex(idx); });
                    Theme.MenuItem(m, "Remove from queue", Glyphs.Close, delegate { host.RemoveFromQueue(idx); });
                    m.Items.Add(new ToolStripSeparator());
                    Theme.MenuItem(m, "Shuffle queue", Glyphs.Shuffle, delegate { host.ShuffleQueue(); });
                    Theme.MenuItem(m, "Save queue as playlist…", Glyphs.PlaylistAdd, delegate { host.SaveQueueAsPlaylist(); });
                    Theme.MenuItem(m, "Clear queue", Glyphs.Trash, delegate { host.ClearQueue(); });
                    m.Show(this, e.Location);
                }
            }
        }

        protected override void OnCanvasDoubleClick(MouseEventArgs e)
        {
            if (e.Location.Y < HeaderH) { host.ToggleMaximize(); return; }
            int q = QueueIndexAt(e.Location);
            if (q >= 0) host.PlayQueueIndex(q);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (collapsed || !queueExpanded || !QueueView.Contains(e.Location)) return;
            int max = Math.Max(0, queue.Count * RowH - QueueArea.Height);
            queueTarget = Math.Max(0, Math.Min(max, queueTarget - e.Delta * RowH * 1.5 / 120));
            Animate();
        }

        protected override Rectangle FadeBounds(object key)
        {
            if (key == HeaderKey) return QueueHeaderRect;
            if (key is int)
            {
                int k = (int)key;
                if (k < 1000) return k < NavItems.Length ? NavRect(k) : Rectangle.Empty;
                return Rectangle.Intersect(QueueRow(k - 1000), QueueView);
            }
            return base.FadeBounds(key);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        /// <summary>
        /// Paints everything that is invalid right now, in one go. A new width moves the sidebar and the pages beside it; painting
        /// them in the same frame keeps the edge between them from tearing.
        /// </summary>
        void SyncRedraw()
        {
            Form f = FindForm();
            if (f != null && f.IsHandleCreated) RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, 0x100 /* RDW_UPDATENOW */ | 0x80 /* RDW_ALLCHILDREN */);
        }

        static double EaseInOut(double t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }

        protected override bool OnAnimate(double dt)
        {
            bool more = base.OnAnimate(dt);
            bool widthMoved = false;

            // Collapse / expand: a time-based ease-in-out of the width. Labels, queue rows and the logo fade with the width
            // (see TextFade) and the icons glide, so nothing jumps or is squeezed.
            if (wT < 1)
            {
                wT = Math.Min(1, wT + dt / WidthSeconds);
                int w = (int)Math.Round(wFrom + (wTo - wFrom) * EaseInOut(wT));
                if (w != VW) { VW = w; widthMoved = true; }
                if (wT >= 1) { RefreshQueue(); Invalidate(); }
                more = true;
            }
            // Dragging the edge: follow the requested width with a light smoothing, once per frame.
            else if (dragWidth >= 0)
            {
                double diff = dragWidth - VW;
                int w = Math.Abs(diff) < 1.5 ? dragWidth : (int)Math.Round(VW + diff * (1 - Math.Exp(-dt * 40)));
                if (w != VW) { VW = w; widthMoved = true; }
                more = resizing || w != dragWidth;
            }

            // The selection pill glides to the page that was opened.
            if (selPos != selTarget)
            {
                selPos += (selTarget - selPos) * (1 - Math.Exp(-dt * 20));
                if (Math.Abs(selTarget - selPos) < 0.004) selPos = selTarget;
                Invalidate(new Rectangle(0, HeaderH - Theme.S(4), VW, NavItems.Length * (NavH + Theme.S(4)) + Theme.S(8)));
                more = true;
            }

            // The queue opens and closes by revealing its rows.
            double qGoal = queueExpanded ? 1 : 0;
            if (qOpen != qGoal)
            {
                qOpen = qGoal > qOpen ? Math.Min(qGoal, qOpen + dt / 0.24) : Math.Max(qGoal, qOpen - dt / 0.24);
                Rectangle a = QueueArea;
                Invalidate(new Rectangle(0, QueueHeaderRect.Y, VW, a.Bottom - QueueHeaderRect.Y));
                more = true;
            }

            if (widthMoved)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Invalidate();
                SyncRedraw();
                if (Perf.Enabled) Perf.Record("sidebar.frame", t0);
            }

            if (queuePos != queueTarget)
            {
                queuePos += (queueTarget - queuePos) * (1 - Math.Exp(-dt * 16));
                if (Math.Abs(queueTarget - queuePos) < 0.4) queuePos = queueTarget;
                queueScroll = (int)Math.Round(queuePos);
                Invalidate(QueueView);
                Point m = PointToClient(Cursor.Position);
                int q = QueueIndexAt(m);
                if (q != hoverQueue) { FadeHover(QueueKey(hoverQueue), QueueKey(q)); hoverQueue = q; }
                more = true;
            }
            return more;
        }
    }

    // ======================================================================= page headers

    internal sealed class PageHeader : CanvasControl
    {
        public string Title = "", Subtitle = "";
        readonly List<CanvasButton> right = new List<CanvasButton>();
        TextBox search;               // a real edit box (caret, selection, IME, clipboard), drawn inside a painted pill
        CanvasButton clearSearch;
        Rectangle searchRect;
        /// <summary>Raised on every edit of the search box.</summary>
        public event Action<string> SearchChanged;
        /// <summary>Raised when Enter or Down is pressed in the search box (move focus to the results).</summary>
        public event Action SearchSubmitted;

        public PageHeader() { Height = Theme.S(100); }

        public string SearchText { get { return search != null ? search.Text : ""; } }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Adds a search box left of the header buttons. <paramref name="cue"/> is the grey hint shown while it is empty.</summary>
        public void EnableSearch(string cue)
        {
            search = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.Ui(10.5f), BackColor = Theme.Bg, ForeColor = Theme.Text };
            search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, cue); };
            search.TextChanged += delegate
            {
                clearSearch.Visible = search.TextLength > 0;
                Invalidate(Rectangle.Inflate(searchRect, 2, 2));
                var h = SearchChanged;
                if (h != null) h(search.Text);
            };
            search.GotFocus += delegate { Invalidate(Rectangle.Inflate(searchRect, 2, 2)); };
            search.LostFocus += delegate { Invalidate(Rectangle.Inflate(searchRect, 2, 2)); };
            search.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && search.TextLength > 0) { search.Clear(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)
                {
                    e.SuppressKeyPress = true;
                    var h = SearchSubmitted;
                    if (h != null) h();
                }
            };
            Controls.Add(search);
            clearSearch = AddButton(Glyphs.Close, BtnStyle.Ghost, 9f, "Clear search (Esc)", delegate { search.Clear(); search.Focus(); });
            clearSearch.Visible = false;
            LayoutButtons();
        }

        /// <summary>Puts the caret in the search box and selects its text (Ctrl+F).</summary>
        public void FocusSearch()
        {
            if (search == null) return;
            search.Focus();
            search.SelectAll();
        }

        public void ClearSearch() { if (search != null) search.Clear(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (search != null && searchRect.Contains(e.Location) && !clearSearch.Bounds.Contains(e.Location)) search.Focus();
        }

        public CanvasButton AddRight(string glyph, string label, BtnStyle style, string tip, Action click)
        {
            CanvasButton b = AddButton(glyph, style, 11f, tip, click);
            b.Label = label;
            right.Add(b);
            LayoutButtons();
            return b;
        }

        public void LayoutButtons()
        {
            int x = Width - Theme.S(32);
            foreach (CanvasButton b in right)
            {
                int w = string.IsNullOrEmpty(b.Label) ? Theme.S(40)
                    : TextRenderer.MeasureText(b.Label, Theme.UiSemibold(9.5f)).Width + Theme.S(string.IsNullOrEmpty(b.Glyph) ? 34 : 58);
                x -= w;
                b.Bounds = new Rectangle(x, Theme.S(40), w, Theme.S(38));
                x -= Theme.S(10);
            }
            if (search != null)
            {
                // Next to the buttons, as wide as fits beside a readable title (150–300 px). In a narrow window (the Songs
                // page has four buttons) it moves to its own row under the title instead.
                int room = x - Theme.S(32) - Theme.S(170);
                bool ownRow = room < Theme.S(150);
                if (ownRow) searchRect = new Rectangle(Theme.S(32), Theme.S(98), Math.Min(Theme.S(420), Width - Theme.S(64)), Theme.S(38));
                else searchRect = new Rectangle(x - Math.Min(Theme.S(300), room), Theme.S(40), Math.Min(Theme.S(300), room), Theme.S(38));
                int wantH = Theme.S(ownRow ? 148 : 100);
                if (Height != wantH) Height = wantH;
                int th = search.PreferredHeight;
                int left = searchRect.X + Theme.S(38), clearW = Theme.S(30);
                search.Bounds = new Rectangle(left, searchRect.Y + (searchRect.Height - th) / 2, searchRect.Right - clearW - Theme.S(6) - left, th);
                clearSearch.Bounds = new Rectangle(searchRect.Right - clearW - Theme.S(4), searchRect.Y + (searchRect.Height - clearW) / 2, clearW, clearW);
            }
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButtons(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBackground(g);
            int textRight = search != null && searchRect.Y < Theme.S(60) ? searchRect.X - Theme.S(16)
                : right.Count > 0 ? right[right.Count - 1].Bounds.X - Theme.S(16) : Width - Theme.S(32);
            if (search != null)
            {
                bool focused = search.Focused;
                Color fill = Theme.Mix(BackColor, Theme.Overlay, focused ? 0.10 : 0.06);
                if (search.BackColor != fill) search.BackColor = fill;
                if (search.ForeColor != Theme.Text) search.ForeColor = Theme.Text;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Theme.FillRound(g, fill, searchRect, searchRect.Height / 2f);
                using (var p = new Pen(focused ? Theme.Accent : Theme.Alpha(Theme.Overlay, 30), focused ? Theme.S(1.5f) : 1f))
                using (var path = Theme.Round(new RectangleF(searchRect.X + 0.5f, searchRect.Y + 0.5f, searchRect.Width - 1, searchRect.Height - 1), searchRect.Height / 2f))
                    g.DrawPath(p, path);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                Theme.GlyphCenter(g, Glyphs.Search, 11f, focused ? Theme.Accent : Theme.TextDim,
                                  new Rectangle(searchRect.X + Theme.S(8), searchRect.Y, Theme.S(28), searchRect.Height));
            }
            Theme.TextLeft(g, Title, Theme.UiSemibold(21f), Theme.Text, new Rectangle(Theme.S(32), Theme.S(18), textRight - Theme.S(32), Theme.S(48)));
            Theme.TextLeft(g, Subtitle, Theme.Ui(9.5f), Theme.TextDim, new Rectangle(Theme.S(34), Theme.S(66), textRight - Theme.S(34), Theme.S(22)));
            PaintWidgets(g);
        }
    }

    /// <summary>Header of an album or artist page: back button, artwork, title, metadata, Play / Shuffle.</summary>
    internal sealed class DetailHeader : CanvasControl
    {
        public string Kind = "", Title = "", Meta = "";
        public TrackInfo ArtTrack;
        public bool Circle;
        readonly CanvasButton back, play, shuffle, rename, delete;
        public Action BackClicked, PlayClicked, ShuffleClicked, RenameClicked, DeleteClicked;

        public DetailHeader()
        {
            back = AddButton(Glyphs.Back, BtnStyle.Ghost, 12f, "Back", delegate { if (BackClicked != null) BackClicked(); });
            play = AddButton(Glyphs.Play, BtnStyle.AccentPill, 11f, "Play all", delegate { if (PlayClicked != null) PlayClicked(); });
            play.Label = "Play";
            shuffle = AddButton(Glyphs.Shuffle, BtnStyle.Pill, 11f, "Shuffle", delegate { if (ShuffleClicked != null) ShuffleClicked(); });
            shuffle.Label = "Shuffle";
            rename = AddButton(Glyphs.Pencil, BtnStyle.Pill, 11f, "Rename this playlist", delegate { if (RenameClicked != null) RenameClicked(); });
            rename.Label = "Rename";
            delete = AddButton(Glyphs.Trash, BtnStyle.Pill, 11f, "Delete this playlist (the songs stay in your library)", delegate { if (DeleteClicked != null) DeleteClicked(); });
            delete.Label = "Delete";
            rename.Visible = delete.Visible = false;
            Height = Theme.S(250);
        }

        /// <summary>Playlist pages get Rename and Delete buttons next to Play and Shuffle.</summary>
        public bool PlaylistActions
        {
            get { return rename.Visible; }
            set { if (rename.Visible != value) { rename.Visible = delete.Visible = value; Invalidate(); } }
        }

        Rectangle ArtRect { get { return new Rectangle(Theme.S(32), Theme.S(60), Theme.S(170), Theme.S(170)); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            back.Bounds = new Rectangle(Theme.S(20), Theme.S(12), Theme.S(40), Theme.S(36));
            int x = ArtRect.Right + Theme.S(30);
            play.Bounds = new Rectangle(x, Theme.S(186), Theme.S(110), Theme.S(40));
            shuffle.Bounds = new Rectangle(play.Bounds.Right + Theme.S(12), Theme.S(186), Theme.S(124), Theme.S(40));
            rename.Bounds = new Rectangle(shuffle.Bounds.Right + Theme.S(12), Theme.S(186), Theme.S(124), Theme.S(40));
            delete.Bounds = new Rectangle(rename.Bounds.Right + Theme.S(12), Theme.S(186), Theme.S(116), Theme.S(40));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBackground(g);
            Rectangle art = ArtRect;
            Theme.ArtTile(g, ArtTrack, art, Theme.S(10f), Title, Circle);
            int x = art.Right + Theme.S(30), w = Width - x - Theme.S(32);
            Theme.TextLeft(g, Kind, Theme.UiSemibold(8.5f), Theme.Accent, new Rectangle(x, Theme.S(78), w, Theme.S(20)));
            Theme.TextLeft(g, Title, Theme.UiSemibold(22f), Theme.Text, new Rectangle(x, Theme.S(98), w, Theme.S(50)));
            Theme.TextLeft(g, Meta, Theme.Ui(9.5f), Theme.TextDim, new Rectangle(x, Theme.S(148), w, Theme.S(24)));
            PaintWidgets(g);
        }
    }

    // ======================================================================= song list

    internal sealed class SongListView : ScrollCanvas
    {
        readonly IPlayerHost host;
        List<TrackInfo> items = new List<TrackInfo>();
        int hover = -1, focus = -1, anchor = -1;
        readonly HashSet<TrackInfo> selection = new HashSet<TrackInfo>();
        public bool ShowTrackNumbers;
        /// <summary>Raised when the set of selected songs changes.</summary>
        public event Action SelectionChanged;
        public bool ShowAlbum = true;
        public event Action<int> Activated;
        public event Action<int, Point> ContextRequested;

        public SongListView(IPlayerHost host) { this.host = host; }

        /// <summary>Shown when the list is empty; null = the "add a music folder" hint.</summary>
        public string EmptyText;

        /// <summary>Focuses the list and the first row (from the search box: Enter / Down).</summary>
        public void FocusFirst()
        {
            Focus();
            if (items.Count == 0) return;
            if (focus < 0) focus = 0;
            Invalidate();
        }

        public List<TrackInfo> Items { get { return items; } }

        protected override Rectangle FadeBounds(object key)
        {
            var t = key as TrackInfo;
            if (t == null) return base.FadeBounds(key);
            int i = hover >= 0 && hover < items.Count && items[hover] == t ? hover : items.IndexOf(t);
            return i < 0 ? Rectangle.Empty : Rectangle.Inflate(RowRect(i), 1, 1);
        }

        int nowIdx = -1;
        /// <summary>Repaints only the playing row's equalizer bars (called a few times a second while playing).</summary>
        public void InvalidateNowPlaying()
        {
            TrackInfo now = host.NowPlaying;
            if (now == null || items.Count == 0) return;
            if (nowIdx < 0 || nowIdx >= items.Count || items[nowIdx] != now) nowIdx = items.IndexOf(now);
            if (nowIdx < 0) return;
            Rectangle r = RowRect(nowIdx);
            if (r.Bottom < 0 || r.Top > Height) return;
            Invalidate(LeadRect(r));
        }
        int RowH { get { return Theme.S(60); } }
        int TopPad { get { return Theme.S(4); } }
        protected override int ContentHeight { get { return TopPad + items.Count * RowH + Theme.S(20); } }

        public void SetItems(List<TrackInfo> list, bool keepScroll)
        {
            TrackInfo focused = focus >= 0 && focus < items.Count ? items[focus] : null;
            items = list ?? new List<TrackInfo>();
            int before = selection.Count;
            selection.IntersectWith(items);   // keep the selection across re-sorts / library refreshes
            focus = focused != null ? items.IndexOf(focused) : -1;
            if (anchor >= items.Count) anchor = focus;
            hover = -1;
            if (selection.Count != before) RaiseSelectionChanged();
            if (!keepScroll) ScrollY = 0;
            ClampScroll();
            Invalidate();
        }

        Rectangle RowRect(int i) { return new Rectangle(Theme.S(20), TopPad + i * RowH - ScrollY + Theme.S(2), Width - Theme.S(40), RowH - Theme.S(4)); }
        Rectangle LeadRect(Rectangle row) { return new Rectangle(row.X + Theme.S(6), row.Y, Theme.S(36), row.Height); }

        int IndexAt(Point p)
        {
            if (p.X < Theme.S(20) || p.X > Width - Theme.S(20)) return -1;
            int i = (p.Y + ScrollY - TopPad) / RowH;
            return p.Y + ScrollY - TopPad >= 0 && i < items.Count ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBackground(g);
            if (items.Count == 0)
            {
                Theme.DrawText(g, EmptyText ?? "No songs yet — add a music folder with the “Add folder” button, or drop files here.", Theme.Ui(10f), Theme.TextDim,
                           new Rectangle(0, Theme.S(40), Width, Theme.S(30)), TextFormatFlags.HorizontalCenter);
                return;
            }
            TrackInfo now = host.NowPlaying;
            bool playing = host.IsPlaying;
            bool showAlbum = ShowAlbum && Width > Theme.S(720);
            bool showFormat = Width > Theme.S(900);
            // Paint only rows inside the area being repainted (after a scroll blit that is just the newly exposed strip).
            Rectangle clip = e.ClipRectangle;
            int first = Math.Max(0, (ScrollY + clip.Top - TopPad) / RowH);
            int last = Math.Min(items.Count - 1, (ScrollY + clip.Bottom - TopPad) / RowH + 1);

            for (int i = first; i <= last; i++)
            {
                TrackInfo t = items[i];
                Rectangle r = RowRect(i);
                bool isNow = t == now;
                if (selection.Contains(t)) Theme.FillRound(g, Theme.Alpha(Theme.Select, 235), r, Theme.S(8f));
                else if (isNow) Theme.FillRound(g, Theme.Alpha(Theme.Select, 130), r, Theme.S(8f));
                float hv = Fade.Get(t);
                if (!selection.Contains(t) && !isNow && hv > 0) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(14 * hv)), r, Theme.S(8f));

                Rectangle lead = LeadRect(r);
                if (isNow) Theme.EqBars(g, new Rectangle(lead.X + Theme.S(11), r.Y + r.Height / 2 - Theme.S(7), Theme.S(13), Theme.S(14)), Theme.Accent, playing);
                else
                {
                    // The track number cross-fades into a play icon on hover.
                    Color rowBg = selection.Contains(t) ? Theme.Mix(Theme.Bg, Theme.Select, 0.92) : Theme.Bg;
                    if (hv < 0.98f)
                    {
                        int n = ShowTrackNumbers && t.TrackNo > 0 ? t.TrackNo : i + 1;
                        Theme.DrawText(g, n.ToString(), Theme.Ui(9f), Theme.Mix(Theme.TextDim, rowBg, hv), lead, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    if (hv > 0.02f) Theme.GlyphCenter(g, Glyphs.Play, 11f, Theme.Mix(rowBg, Theme.Text, hv), lead);
                }

                var art = new Rectangle(lead.Right + Theme.S(8), r.Y + (r.Height - Theme.S(42)) / 2, Theme.S(42), Theme.S(42));
                Theme.ArtTile(g, t, art, Theme.S(6f), Library.AlbumTitle(t), false);

                int right = r.Right - Theme.S(12);
                var dur = new Rectangle(right - Theme.S(56), r.Y, Theme.S(56), r.Height);
                Theme.DrawText(g, Theme.Time(t.Duration), Theme.Ui(9.5f), Theme.TextDim, dur, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                right = dur.X - Theme.S(18);
                if (showFormat)
                {
                    var fr = new Rectangle(right - Theme.S(120), r.Y, Theme.S(120), r.Height);
                    Theme.TextLeft(g, t.ShortFormat, Theme.Ui(9f), Theme.TextDim, fr);
                    right = fr.X - Theme.S(16);
                }
                int textX = art.Right + Theme.S(14);
                if (showAlbum)
                {
                    int aw = Math.Min(Theme.S(280), (right - textX) / 2);
                    var ar = new Rectangle(right - aw, r.Y, aw, r.Height);
                    Theme.TextLeft(g, Library.AlbumTitle(t), Theme.Ui(9.5f), Theme.TextDim, ar);
                    right = ar.X - Theme.S(16);
                }
                Color tc = t.ProbeError != null ? Theme.Bad : isNow ? Theme.Accent : selection.Contains(t) ? Theme.SelectText : Theme.Text;
                Theme.TextLeft(g, t.DisplayTitle, Theme.UiMedium(10f), tc, new Rectangle(textX, r.Y + Theme.S(7), right - textX, Theme.S(24)));
                Theme.TextLeft(g, t.ProbeError ?? t.DisplayArtist, Theme.Ui(9f), Theme.TextDim, new Rectangle(textX, r.Y + Theme.S(31), right - textX, Theme.S(20)));
            }
            PaintScrollbar(g);
        }

        protected override void OnContentMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hover)
            {
                FadeHover(hover >= 0 && hover < items.Count ? items[hover] : null, i >= 0 ? items[i] : null);
                hover = i;
            }
        }

        protected override void OnContentMouseLeave()
        {
            if (hover == -1) return;
            FadeHover(hover < items.Count ? items[hover] : null, null);
            hover = -1;
        }

        protected override Cursor CursorAt(Point p)
        {
            int i = IndexAt(p);
            return i >= 0 && LeadRect(RowRect(i)).Contains(p) ? Cursors.Hand : Cursors.Default;
        }

        // ---- selection: click = one, Ctrl+click = toggle, Shift+click = range from the anchor, Ctrl+A = all
        public List<TrackInfo> SelectedItems
        {
            get { var l = new List<TrackInfo>(); foreach (TrackInfo t in items) if (selection.Contains(t)) l.Add(t); return l; }
        }
        public int SelectionCount { get { return selection.Count; } }

        void RaiseSelectionChanged() { if (SelectionChanged != null) SelectionChanged(); }

        void SelectOnly(int i)
        {
            selection.Clear();
            if (i >= 0 && i < items.Count) selection.Add(items[i]);
            focus = anchor = i;
            RaiseSelectionChanged();
            Invalidate();
        }

        void SelectRange(int from, int to, bool add)
        {
            if (!add) selection.Clear();
            for (int k = Math.Min(from, to); k <= Math.Max(from, to); k++) if (k >= 0 && k < items.Count) selection.Add(items[k]);
            RaiseSelectionChanged();
            Invalidate();
        }

        int clickKeys;

        protected override void WndProc(ref Message m)
        {
            // Remember the modifier keys that were down when the button was pressed (from the message itself).
            if (m.Msg == 0x201 || m.Msg == 0x204) clickKeys = (int)(m.WParam.ToInt64() & 0xFFFF);
            base.WndProc(ref m);
        }

        public void SelectAll() { selection.Clear(); selection.UnionWith(items); RaiseSelectionChanged(); Invalidate(); }
        public void ClearSelection() { if (selection.Count == 0) return; selection.Clear(); RaiseSelectionChanged(); Invalidate(); }

        protected override void OnContentMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = IndexAt(e.Location);
            bool ctrl = (clickKeys & 0x8) != 0, shift = (clickKeys & 0x4) != 0;   // MK_CONTROL / MK_SHIFT as sent with the click
            if (i < 0) { if (e.Button == MouseButtons.Left && !ctrl && !shift) ClearSelection(); return; }
            if (e.Button == MouseButtons.Right)
            {
                if (!selection.Contains(items[i])) SelectOnly(i);   // right-click inside a selection keeps it
                focus = i;
                if (ContextRequested != null) ContextRequested(i, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (shift && anchor >= 0) { SelectRange(anchor, i, ctrl); focus = i; }
            else if (ctrl)
            {
                if (!selection.Remove(items[i])) selection.Add(items[i]);
                focus = anchor = i;
                RaiseSelectionChanged();
                Invalidate();
            }
            else
            {
                SelectOnly(i);
                if (LeadRect(RowRect(i)).Contains(e.Location) && Activated != null) Activated(i);
            }
        }

        protected override void OnCanvasDoubleClick(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i >= 0 && !LeadRect(RowRect(i)).Contains(e.Location) && Activated != null) Activated(i);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Enter || keyData == Keys.PageUp || keyData == Keys.PageDown ||
                   keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (items.Count == 0) return;
            if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; return; }
            int page = Math.Max(1, Height / RowH - 1);
            int s = focus;
            switch (e.KeyCode)
            {
                case Keys.Up: s = Math.Max(0, s - 1); break;
                case Keys.Down: s = Math.Min(items.Count - 1, s + 1); break;
                case Keys.PageUp: s = Math.Max(0, s - page); break;
                case Keys.PageDown: s = Math.Min(items.Count - 1, s + page); break;
                case Keys.Home: s = 0; break;
                case Keys.End: s = items.Count - 1; break;
                case Keys.Enter: if (s >= 0 && Activated != null) Activated(s); return;
                default: return;
            }
            if (e.Shift) { if (anchor < 0) anchor = Math.Max(0, focus); SelectRange(anchor, s, false); focus = s; }
            else SelectOnly(s);
            EnsureVisible(TopPad + s * RowH, RowH);
            Invalidate();
            e.Handled = true;
        }
    }

    // ======================================================================= album / artist tiles

    internal sealed class TileItem
    {
        public string Title, Subtitle;
        public TrackInfo ArtTrack;
        public object Tag;
        internal readonly object PlayKey = new object();   // hover-fade key for the round play button
    }

    internal sealed class TileGridView : ScrollCanvas
    {
        List<TileItem> items = new List<TileItem>();
        public bool Circle;
        public string EmptyText = "Nothing here yet.";
        public event Action<TileItem> Opened, PlayRequested;
        int hover = -1, pressed = -1;
        bool hoverPlay;

        int Pad { get { return Theme.S(30); } }
        int Gap { get { return Theme.S(22); } }
        int Cols { get { return Math.Max(1, (Width - 2 * Pad + Gap) / (Theme.S(172) + Gap)); } }
        int TileW { get { return Math.Max(Theme.S(60), (Width - 2 * Pad - Gap * (Cols - 1)) / Cols); } }
        int TileH { get { return TileW + Theme.S(62); } }
        protected override int ContentHeight
        {
            get { int rows = (items.Count + Cols - 1) / Cols; return Theme.S(8) + rows * (TileH + Gap) + Pad; }
        }

        public void SetItems(List<TileItem> list, bool keepScroll)
        {
            items = list ?? new List<TileItem>();
            hover = pressed = -1;
            if (!keepScroll) ScrollY = 0;
            ClampScroll();
            Invalidate();
        }

        Rectangle TileRect(int i)
        {
            int c = i % Cols, r = i / Cols;
            return new Rectangle(Pad + c * (TileW + Gap), Theme.S(8) + r * (TileH + Gap) - ScrollY, TileW, TileH);
        }

        Rectangle PlayRect(Rectangle tile)
        {
            int s = Theme.S(46);
            int inset = Circle ? TileW / 7 : Theme.S(10);
            return new Rectangle(tile.X + TileW - s - inset, tile.Y + TileW - s - inset, s, s);
        }

        int IndexAt(Point p)
        {
            int cols = Cols, row = (p.Y + ScrollY - Theme.S(8)) / (TileH + Gap);
            if (p.Y + ScrollY - Theme.S(8) < 0) return -1;
            int col = (p.X - Pad) / (TileW + Gap);
            if (p.X < Pad || col >= cols) return -1;
            int i = row * cols + col;
            return i < items.Count && TileRect(i).Contains(p) ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBackground(g);
            if (items.Count == 0)
            {
                Theme.DrawText(g, EmptyText, Theme.Ui(10f), Theme.TextDim, new Rectangle(0, Theme.S(40), Width, Theme.S(30)), TextFormatFlags.HorizontalCenter);
                return;
            }
            int cols = Cols;
            Rectangle clip = e.ClipRectangle;
            int first = Math.Max(0, (ScrollY + clip.Top - Theme.S(8) - TileH - Gap) / (TileH + Gap)) * cols;   // only rows in the repainted area
            for (int i = first; i < items.Count; i++)
            {
                Rectangle r = TileRect(i);
                if (r.Y > clip.Bottom) break;
                if (!Rectangle.Inflate(r, Theme.S(10), Theme.S(10)).IntersectsWith(clip)) continue;
                TileItem it = items[i];
                float hv = Fade.Get(it);
                if (hv > 0) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(13 * hv)), Rectangle.Inflate(r, Theme.S(8), Theme.S(8)), Theme.S(12f));
                var art = new Rectangle(r.X, r.Y, TileW, TileW);
                Theme.ArtTile(g, it.ArtTrack, art, Theme.S(10f), it.Title, Circle);
                if (hv > 0.02f)
                {
                    // The play button fades in and rises slightly.
                    Rectangle pr = PlayRect(r);
                    pr.Offset(0, (int)((1 - hv) * Theme.S(6)));
                    float hp = Fade.Get(it.PlayKey);
                    Theme.FillRound(g, Theme.Alpha(Color.Black, (int)(60 * hv)), new Rectangle(pr.X + 1, pr.Y + Theme.S(2), pr.Width, pr.Height), pr.Height / 2f);
                    Theme.FillRound(g, Theme.Alpha(Theme.Accent, (int)((225 + 30 * hp) * hv)), pr, pr.Height / 2f);
                    Theme.GlyphCenter(g, Glyphs.Play, 13f, Theme.Mix(Theme.Accent, Color.White, hv), pr);
                }
                var flags = Circle ? TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter : TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
                Theme.DrawText(g, it.Title, Theme.UiSemibold(10f), Theme.Text, new Rectangle(r.X, art.Bottom + Theme.S(10), r.Width, Theme.S(24)), flags | TextFormatFlags.NoPadding);
                Theme.DrawText(g, it.Subtitle, Theme.Ui(9f), Theme.TextDim, new Rectangle(r.X, art.Bottom + Theme.S(34), r.Width, Theme.S(20)), flags | TextFormatFlags.NoPadding);
            }
            PaintScrollbar(g);
        }

        protected override Cursor CursorAt(Point p) { return IndexAt(p) >= 0 ? Cursors.Hand : Cursors.Default; }

        protected override Rectangle FadeBounds(object key)
        {
            int i = hover >= 0 && hover < items.Count && (items[hover] == key || items[hover].PlayKey == key) ? hover : -1;
            if (i < 0) { var it = key as TileItem; if (it != null) i = items.IndexOf(it); }
            if (i < 0) return base.FadeBounds(key);
            return Rectangle.Inflate(TileRect(i), Theme.S(10), Theme.S(10));
        }

        protected override void OnContentMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            bool hp = i >= 0 && PlayRect(TileRect(i)).Contains(e.Location);
            if (i != hover)
            {
                FadeHover(hover >= 0 && hover < items.Count ? items[hover] : null, i >= 0 ? items[i] : null);
                if (hover >= 0 && hover < items.Count) FadeHover(items[hover].PlayKey, null);
            }
            if (i >= 0 && (i != hover || hp != hoverPlay)) { if (hp) FadeHover(null, items[i].PlayKey); else FadeHover(items[i].PlayKey, null); }
            hover = i; hoverPlay = hp;
        }

        protected override void OnContentMouseLeave()
        {
            if (hover == -1) return;
            if (hover < items.Count) { FadeHover(items[hover], null); FadeHover(items[hover].PlayKey, null); }
            hover = -1;
        }

        public event Action<TileItem, Point> ContextRequested;

        protected override void OnContentMouseDown(MouseEventArgs e)
        {
            Focus();
            pressed = e.Button == MouseButtons.Left ? IndexAt(e.Location) : -1;
            int i = IndexAt(e.Location);
            if (e.Button == MouseButtons.Right && i >= 0 && ContextRequested != null) ContextRequested(items[i], e.Location);
        }

        protected override void OnContentMouseUp(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i < 0 || i != pressed) return;
            pressed = -1;
            if (PlayRect(TileRect(i)).Contains(e.Location)) { if (PlayRequested != null) PlayRequested(items[i]); }
            else if (Opened != null) Opened(items[i]);
        }
    }

    // ======================================================================= Now Playing

    /// <summary>
    /// Now Playing: artwork, title, seek bar and transport. Scales up with the window (full screen = bigger art, text and controls).
    /// The lyrics button fades the artwork into a lightly blurred page background and shows lyrics in front; synced (LRC) lyrics
    /// follow the song, highlight the current line and can be clicked to seek.
    /// </summary>
    internal sealed class NowPlayingView : CanvasControl
    {
        readonly IPlayerHost host;
        readonly CanvasButton backBtn, fullBtn, stopBtn, queueBtn, moreBtn, shuffleBtn, prevBtn, playBtn, nextBtn, repeatBtn, lyricsBtn;
        readonly CanvasSlider seek, volume;
        // Two-column layout: artwork on the left; title, actions, seek bar, transport, volume and "Up next" on the right.
        Rectangle artRect, headerRect, titleRect, subRect, actionRow, timesRect, volumeRow, upNextLabel, lyricsRect;
        int dividerY = -1;
        readonly List<Rectangle> upNextRows = new List<Rectangle>();
        readonly int[] upNextIndex = { -1, -1, -1 };          // queue index shown in each "Up next" row (-1 = empty)
        readonly object[] upNextKeys = { new object(), new object(), new object() };
        int hoverUpNext = -1;
        Rectangle badgeRect;      // the status chip, as last painted; hovering it shows the full audio path
        bool tipOnBadge;
        float k = 1f;

        bool lyricsOn;
        double anim;               // 0 = artwork, 1 = lyrics
        TrackInfo layoutTrack;
        Lyrics layoutLyrics;
        int layoutWidth;
        float layoutK;
        readonly List<RectangleF> boxes = new List<RectangleF>();
        float totalH;
        double lyricScroll;
        int manualUntil, currentLine = -1, hoverLine = -1;
        GdiSurface artSurface;     // rounded artwork + its soft shadow, pre-rendered once; drawn with one GDI blit / alpha blend
        Image artLayerSrc;
        Size artLayerSize;
        int artLayerPad;
        AnimatedCoverPlayer cover;  // animated album art, drawn over the static artwork once its first frame is decoded
        double coverFade;
        string coverFailed;
        int layoutTick;

        public NowPlayingView(IPlayerHost host)
        {
            this.host = host;
            backBtn = AddButton(Glyphs.ChevronDown, BtnStyle.Circle, 14f, "Back", host.LeaveNowPlaying);
            fullBtn = AddButton(Glyphs.Fullscreen, BtnStyle.Circle, 13f, "Full screen (F11)", host.ToggleFullscreen);
            stopBtn = AddButton(Glyphs.Stop, BtnStyle.Ghost, 11f, "Stop", host.StopPlayback);
            queueBtn = AddButton(Glyphs.Queue, BtnStyle.Ghost, 14f, "Show / hide queue", host.ToggleQueue);
            moreBtn = AddButton(Glyphs.More, BtnStyle.Ghost, 15f, "More options", delegate
            {
                if (ArtContextRequested != null) ArtContextRequested(new Point(moreBtn.Bounds.X, moreBtn.Bounds.Bottom + Theme.S(4)));
            });
            shuffleBtn = AddButton(Glyphs.Shuffle, BtnStyle.Ghost, 14f, "Shuffle queue", host.ShuffleQueue);
            prevBtn = AddButton(Glyphs.Prev, BtnStyle.Ghost, 18f, "Previous (Ctrl+Left)", host.Previous);
            playBtn = AddButton(Glyphs.Play, BtnStyle.Solid, 21f, "Play / Pause (Space)", host.PlayPause);
            nextBtn = AddButton(Glyphs.Next, BtnStyle.Ghost, 18f, "Next (Ctrl+Right)", host.Next);
            repeatBtn = AddButton(Glyphs.Repeat, BtnStyle.Ghost, 14f, "Repeat: off / all / one", host.CycleRepeat);
            lyricsBtn = AddButton(Glyphs.Microphone, BtnStyle.Ghost, 14f, "Lyrics (L)", ToggleLyrics);
            seek = AddSlider(null, host.SeekFraction);
            seek.KnobOnHover = true;
            volume = AddSlider(host.SetVolume, host.SetVolume);
        }

        public bool LyricsVisible { get { return lyricsOn; } }

        bool visualizerEnabled = true;

        /// <summary>Show the spectrum visualizer under "Up next" (when there is room). Off: the layout closes up without it.</summary>
        public bool VisualizerEnabled
        {
            get { return visualizerEnabled; }
            set
            {
                if (visualizerEnabled == value) return;
                visualizerEnabled = value;
                LayoutWidgets();
                if (value && host.IsPlaying) Animate();
            }
        }

        public void ToggleLyrics()
        {
            lyricsOn = !lyricsOn;
            lyricsBtn.Active = lyricsOn;
            if (lyricsOn)
            {
                TrackInfo t = host.NowPlaying;
                Lyrics l;
                if (t != null && LyricsService.TryGet(t, out l) && l != null && l.Source == LyricsService.ErrorSource) LyricsService.Forget(t);   // retry after a network error
                manualUntil = 0;
                layoutTrack = null;
            }
            Animate();
            Invalidate();
        }

        /// <summary>Called when lyrics for a track arrive.</summary>
        public void LyricsArrived(TrackInfo t)
        {
            TrackInfo now = host.NowPlaying;
            if (t != null && now != null && string.Equals(t.FilePath, now.FilePath, StringComparison.OrdinalIgnoreCase)) { layoutTrack = null; Invalidate(); }
        }

        static double Ease(double t) { return t * t * (3 - 2 * t); }

        /// <summary>Runs on the display-synced frame clock: the artwork ↔ lyrics transition (260 ms) and smooth lyric scrolling.</summary>
        protected override bool OnAnimate(double dt)
        {
            bool more = base.OnAnimate(dt);
            if (!Visible) return false;
            double target = lyricsOn ? 1 : 0;
            if (anim != target)
            {
                double step = dt / 0.26;
                anim = target > anim ? Math.Min(target, anim + step) : Math.Max(target, anim - step);
                host.Backdrop.LyricsMix = Ease(anim);
                more = true;
                // The title bar shows the top strip of the same backdrop: paint both in this frame so they never disagree.
                Invalidate();
                Update();
                host.BackdropChanged();
                return true;
            }
            // Outside the transition only the lyrics move: repaint just their area (a full repaint of a maximized window
            // costs several ms just to copy to the screen). Hover fades repaint their own buttons.
            if (lyricsOn && UpdateLyricScroll(dt)) { more = true; Invalidate(Rectangle.Inflate(lyricsRect, 0, Theme.S(4))); }
            if (cover != null && anim < 1)
            {
                more = true;
                bool fading = cover.HasFrame && coverFade < 1;
                if (fading) coverFade = Math.Min(1, coverFade + dt / 0.4);
                if (cover.Step(dt) || fading) Invalidate(artRect);
            }
            if (StepSpectrum(dt)) more = true;
            if (StepSeek()) more = true;
            return more;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && (lyricsOn || anim > 0)) Animate();
            UpdateCover();
        }

        /// <summary>Called when an animated-cover lookup finishes, or its settings changed.</summary>
        public void AnimatedArtChanged() { coverFailed = null; UpdateCover(); }

        /// <summary>Stops the running cover (and its ffmpeg, which holds the video file open), e.g. before clearing the cache.</summary>
        public void StopAnimatedArt()
        {
            if (cover != null) { cover.Dispose(); cover = null; Invalidate(artRect); }
        }

        /// <summary>
        /// Starts, restarts or stops the animated cover. It runs only while Now Playing shows the artwork; ffmpeg decodes it at
        /// exactly the artwork's size, so after a resize it restarts once the size has been stable for a moment.
        /// </summary>
        void UpdateCover()
        {
            TrackInfo t = host.NowPlaying;
            string file = null;
            bool want = Visible && t != null && anim < 1 && artRect.Width > 0 && AnimatedArt.TryGet(t, out file) && file != null;
            if (cover != null && cover.Failed) coverFailed = cover.File;   // an unplayable video: keep the static cover
            if (cover != null && (!want || cover.File != file || cover.Size != artRect.Size || cover.Failed))
            {
                cover.Dispose();
                cover = null;
                Invalidate(artRect);
            }
            if (cover == null && want && file != coverFailed && unchecked(Environment.TickCount - layoutTick) > 300)
            {
                try { cover = new AnimatedCoverPlayer(file, artRect.Size, (int)Math.Round(Theme.S(12f) * k)); coverFade = 0; }
                catch { cover = null; }
            }
            if (cover != null) Animate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutWidgets(); }

        /// <summary>A design size (at 100% DPI and normal window size) scaled for DPI and the window's size factor.</summary>
        int K(float v) { return (int)Math.Round(Theme.S(v) * k); }

        void LayoutWidgets()
        {
            int W = Width, H = Height;
            k = (float)Math.Max(1.0, Math.Min(1.6, Math.Min(W / (double)Theme.S(1180), H / (double)Theme.S(800))));

            // Header: back chevron (left), "Playing from album" (centre), full screen (right).
            int hb = K(40), hy = Theme.S(10);
            backBtn.Bounds = new Rectangle(Theme.S(20), hy, hb, hb);
            fullBtn.Bounds = new Rectangle(W - Theme.S(20) - hb, hy, hb, hb);
            headerRect = new Rectangle(backBtn.Bounds.Right + Theme.S(16), hy, Math.Max(0, fullBtn.Bounds.X - backBtn.Bounds.Right - Theme.S(32)), hb);
            int bodyTop = hy + hb + K(12), bodyH = Math.Max(Theme.S(200), H - K(24) - bodyTop);

            // Columns: the info column keeps a comfortable width; the artwork takes what is left (bounded by the height).
            int margin = K(48), gap = K(64), avail = W - 2 * margin;
            int iw = Math.Max(Theme.S(340), Math.Min(K(580), (int)(avail * 0.46)));
            int a = Math.Min(Math.Min(avail - gap - iw, bodyH - K(8)), K(640));
            if (a < Theme.S(180)) { gap = K(36); a = Math.Min(avail - gap - iw, bodyH - K(8)); }
            a = Math.Max(Theme.S(120), a);
            iw = Math.Max(iw, Math.Min(K(580), avail - gap - a));

            // Heights of the info column: title 50, subtitle 28, gap 20, actions 36, gap 24, seek 20, times 20,
            // gap 16, transport 72, gap 16, volume 32. "Up next" adds a divider block and up to three 58 px rows if they fit.
            int core = K(334), upHead = K(22) + 1 + K(16) + K(30), rowH = K(58);
            int rows = 3;
            while (rows > 0 && core + upHead + rows * rowH > bodyH) rows--;
            int infoH = core + (rows > 0 ? upHead + rows * rowH : 0);
            // Spectrum visualizer under "Up next", when the column has room for it.
            int visBlock = K(20) + K(64);
            bool showVis = visualizerEnabled && infoH + visBlock <= bodyH;
            if (showVis) infoH += visBlock;
            int groupH = Math.Max(a, infoH);
            int top = bodyTop + Math.Max(0, (bodyH - groupH) / 2);
            int x0 = (W - (a + gap + iw)) / 2;
            artRect = new Rectangle(x0, top + (groupH - a) / 2, a, a);
            lyricsRect = new Rectangle(x0, bodyTop, a, bodyH);   // lyrics take the artwork's column

            int ix = x0 + a + gap, y = top + (groupH - infoH) / 2;
            int nudge = K(8);   // icon buttons are wider than their glyphs: shift them so the glyphs line up with the text
            titleRect = new Rectangle(ix, y, iw, K(50)); y += K(50);
            subRect = new Rectangle(ix, y, iw, K(28)); y += K(28) + K(20);

            int ab = K(36);
            actionRow = new Rectangle(ix, y, iw, ab);
            stopBtn.Bounds = new Rectangle(ix - nudge, y, ab, ab);
            queueBtn.Bounds = new Rectangle(stopBtn.Bounds.Right + K(4), y, ab, ab);
            moreBtn.Bounds = new Rectangle(queueBtn.Bounds.Right + K(4), y, ab, ab);
            y += ab + K(24);

            // The slider's bar is inset 6 px inside its bounds: widen the bounds so the bar spans exactly the column.
            seek.Bounds = new Rectangle(ix - Theme.S(6), y, iw + Theme.S(12), K(20)); y += K(20);
            timesRect = new Rectangle(ix, y, iw, K(20)); y += K(20) + K(16);

            int cx = ix + iw / 2, cy = y + K(36);
            int big = K(64), small = K(48), side = K(40);
            int step = Math.Min(K(84), (iw - side) / 4);
            playBtn.Bounds = new Rectangle(cx - big / 2, cy - big / 2, big, big);
            prevBtn.Bounds = new Rectangle(cx - step - small / 2, cy - small / 2, small, small);
            nextBtn.Bounds = new Rectangle(cx + step - small / 2, cy - small / 2, small, small);
            shuffleBtn.Bounds = new Rectangle(cx - 2 * step - side / 2, cy - side / 2, side, side);
            repeatBtn.Bounds = new Rectangle(cx + 2 * step - side / 2, cy - side / 2, side, side);
            y += K(72) + K(16);

            volumeRow = new Rectangle(ix, y, iw, K(32));
            volume.Bounds = new Rectangle(ix + K(30), y + (K(32) - Theme.S(20)) / 2, K(124), Theme.S(20));
            lyricsBtn.Bounds = new Rectangle(ix + iw - ab + nudge, y + (K(32) - ab) / 2, ab, ab);
            y += K(32);

            upNextRows.Clear();
            if (rows > 0)
            {
                y += K(22); dividerY = y; y += 1 + K(16);
                upNextLabel = new Rectangle(ix, y, iw, K(30)); y += K(30);
                for (int i = 0; i < rows; i++) upNextRows.Add(new Rectangle(ix - nudge, y + i * rowH, iw + 2 * nudge, rowH - K(4)));
                y += rows * rowH;
            }
            else { dividerY = -1; upNextLabel = Rectangle.Empty; }
            visRect = showVis ? new Rectangle(ix, y + K(20), iw, K(64)) : Rectangle.Empty;

            backBtn.GlyphSize = 14f * k; fullBtn.GlyphSize = 13f * k;
            stopBtn.GlyphSize = 11f * k; queueBtn.GlyphSize = 14f * k; moreBtn.GlyphSize = 15f * k; lyricsBtn.GlyphSize = 14f * k;
            shuffleBtn.GlyphSize = repeatBtn.GlyphSize = 14f * k; prevBtn.GlyphSize = nextBtn.GlyphSize = 18f * k; playBtn.GlyphSize = 21f * k;
            layoutTrack = null;
            layoutTick = Environment.TickCount;
            if (cover != null && cover.Size != artRect.Size) UpdateCover();   // drop it now; restarted at the new size by UpdateState
            Invalidate();
        }

        public void UpdateState()
        {
            TrackInfo t = host.NowPlaying;
            playBtn.Glyph = host.IsPlaying ? Glyphs.Pause : Glyphs.Play;
            repeatBtn.Glyph = host.RepeatMode == 2 ? Glyphs.RepeatOne : Glyphs.Repeat;
            repeatBtn.Active = host.RepeatMode != 0;
            fullBtn.Glyph = host.IsFullscreen ? Glyphs.ExitFullscreen : Glyphs.Fullscreen;
            fullBtn.Tip = host.IsFullscreen ? "Exit full screen (Esc)" : "Full screen (F11)";
            if (!IsDraggingSlider)
            {
                double d = t != null ? t.Duration : 0;
                seek.Value = host.IsRunning && d > 0 ? host.Position / d : 0;
                volume.Value = host.Volume;
            }
            if (layoutTrack != null && layoutTrack != t) layoutTrack = null;
            if (lyricsOn) Animate();   // follows the song: a new line starts a smooth scroll
            if (host.IsPlaying) Animate();   // seek bar glide + spectrum bars
            UpdateCover();
            Invalidate();
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            host.Backdrop.Paint(g, this, host.BackdropHost);
            if (Perf.Enabled) Perf.Record("np.backdrop", p0);
            TrackInfo t = host.NowPlaying;
            double mix = Ease(anim);

            long p1 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (mix < 1) PaintArtwork(g, t, 1 - mix);
            if (Perf.Enabled) Perf.Record("np.art", p1);
            long p2 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (mix > 0) PaintLyrics(g, t, mix);
            if (Perf.Enabled) Perf.Record("np.lyrics", p2);

            Color dim = Theme.Mix(Theme.Text, Theme.TextDim, 0.45);
            var leftMid = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

            // Header: small spaced caps label over the album name, centred between the two header buttons.
            var hc = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int half = headerRect.Height / 2;
            Theme.DrawText(g, Spaced(t == null ? "NOW PLAYING" : "PLAYING FROM ALBUM"), Theme.UiMedium(7.5f * k), dim,
                           new Rectangle(headerRect.X, headerRect.Y, headerRect.Width, half - Theme.S(1)), hc | TextFormatFlags.Bottom);
            if (t != null)
                Theme.DrawText(g, Library.AlbumTitle(t).ToUpperInvariant(), Theme.UiSemibold(10f * k), Theme.Text,
                               new Rectangle(headerRect.X, headerRect.Y + half + Theme.S(2), headerRect.Width, half), hc | TextFormatFlags.Top);

            // Title and artist.
            Theme.DrawText(g, t != null ? t.DisplayTitle : "Nothing playing", Theme.UiSemibold(25f * k), Theme.Text, titleRect, leftMid);
            Theme.DrawText(g, t != null ? t.DisplayArtist.ToUpperInvariant() : "Choose a song from Songs, Albums or Artists",
                           Theme.Ui(11f * k), Theme.Mix(Theme.Text, Theme.TextDim, 0.3), subRect, leftMid);

            // Action row: stop, queue, more (buttons) + the file format and the output status chips.
            PaintChips(g, t);

            // Seek times.
            double dur = t != null ? t.Duration : 0;
            double pos = seek.Dragging ? seek.Value * dur : (host.IsRunning ? host.Position : 0);
            Theme.DrawText(g, Theme.Time(pos), Theme.Ui(8.5f * k), dim, timesRect, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Theme.DrawText(g, Theme.Time(dur), Theme.Ui(8.5f * k), dim, timesRect, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Volume (icon + slider; the lyrics button sits at the right end of the same row).
            Theme.GlyphCenter(g, host.Volume <= 0.001 ? Glyphs.Mute : Glyphs.Volume, 13f * k, dim, new Rectangle(volumeRow.X - K(2), volumeRow.Y, K(24), volumeRow.Height));

            // An error replaces nothing in the layout: it gets its own line under the volume row.
            if (host.StatusIsError && !string.IsNullOrEmpty(host.StatusText))
                Theme.DrawText(g, host.StatusText, Theme.Ui(8.5f * k), Theme.Bad, new Rectangle(volumeRow.X, volumeRow.Bottom + K(2), volumeRow.Width, K(18)), leftMid);

            PaintUpNext(g, t);
            PaintSpectrum(g);
            PaintWidgets(g);
            if (pathOpen) PaintSignalPath(g);
            if (Perf.Enabled) Perf.Record("np.total", p0);
        }

        /// <summary>Letter-spaced caps (hair spaces between letters), for the small header label.</summary>
        static string Spaced(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) { if (sb.Length > 0) sb.Append(' '); sb.Append(c); }
            return sb.ToString();
        }

        /// <summary>The file's format (outlined, like the mockup's "MP3") and the output status (BIT-PERFECT / EXCLUSIVE / …).</summary>
        void PaintChips(Graphics g, TrackInfo t)
        {
            badgeRect = Rectangle.Empty;
            int x = moreBtn.Bounds.Right + K(10), right = actionRow.Right;
            int h = K(22), y = actionRow.Y + (actionRow.Height - h) / 2;
            Font f = Theme.UiSemibold(7.5f * k);
            var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

            string badge = host.BadgeText;
            int bw = TextRenderer.MeasureText(badge, f).Width + K(18);
            if (t != null && t.Probed && t.ProbeError == null)
            {
                string fmt = t.ShortFormat.ToUpperInvariant();
                int fw = TextRenderer.MeasureText(fmt, f).Width + K(14);
                if (x + fw + K(8) + bw <= right)
                {
                    var r = new Rectangle(x, y, fw, h);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var p = new Pen(Theme.Alpha(Theme.TextDim, 150)))
                    using (var path = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(4f)))
                        g.DrawPath(p, path);
                    g.SmoothingMode = SmoothingMode.Default;
                    Theme.DrawText(g, fmt, f, Theme.Mix(Theme.Text, Theme.TextDim, 0.35), r, center);
                    x = r.Right + K(8);
                }
            }
            sleepRect = Rectangle.Empty;
            if (x + bw > right) return;
            badgeRect = new Rectangle(x, y, bw, h);
            Theme.FillRound(g, Theme.Alpha(host.BadgeColor, pathOpen ? 255 : 225), badgeRect, h / 2f);
            Theme.DrawText(g, badge, f, Color.White, badgeRect, center);
            x = badgeRect.Right + K(8);

            // Sleep timer countdown (click: change / turn off).
            string sleep = host.SleepTimerText;
            if (sleep != null)
            {
                int sw = TextRenderer.MeasureText(sleep, f).Width + K(36);
                if (x + sw <= right)
                {
                    sleepRect = new Rectangle(x, y, sw, h);
                    Theme.FillRound(g, Theme.Alpha(Theme.Accent, 60), sleepRect, h / 2f);
                    Theme.GlyphCenter(g, Glyphs.Moon, 9f * k, Theme.Text, new Rectangle(sleepRect.X + K(6), y, K(18), h));
                    Theme.DrawText(g, sleep, f, Theme.Text, new Rectangle(sleepRect.X + K(24), y, sleepRect.Width - K(30), h), center);
                }
            }
        }

        string BadgeTip()
        {
            string a = host.OutputLine;
            if (string.IsNullOrEmpty(a)) return "Nothing playing";
            return a + "\nClick to see the signal path";
        }

        /// <summary>
        /// Per frame while playing: move the seek bar with the engine's position (updated every audio buffer), repainting just
        /// the bar, so it glides instead of stepping every 150 ms. Skipped while the user drags it.
        /// </summary>
        bool StepSeek()
        {
            if (!Visible || !host.IsPlaying || IsDraggingSlider) return false;
            TrackInfo t = host.NowPlaying;
            double d = t != null ? t.Duration : 0;
            if (d <= 0) return false;
            double pos = host.Position;
            double v = Math.Max(0, Math.Min(1, pos / d));
            float px = (float)((v - seek.Value) * Math.Max(1, seek.Bounds.Width - Theme.S(12)));
            if (Math.Abs(px) >= 0.2f)
            {
                seek.Value = v;
                // Only the bar's own row: the elapsed time sits just below and is repainted whole when its second changes,
                // otherwise a strip of it would show the new digits over the old ones.
                Invalidate(new Rectangle(seek.Bounds.X - Theme.S(4), seek.Bounds.Y, seek.Bounds.Width + Theme.S(8), seek.Bounds.Height));
            }
            int second = (int)Math.Floor(pos);
            if (second != shownSecond) { shownSecond = second; Invalidate(Rectangle.Inflate(timesRect, Theme.S(2), Theme.S(2))); }
            return true;
        }

        int shownSecond = -1;

        // ------------------------------------------------------------------ spectrum visualizer

        readonly SpectrumAnalyzer spectrum = new SpectrumAnalyzer();
        Rectangle visRect;

        /// <summary>24 rounded bars with falling peak caps, bottom-aligned in visRect.</summary>
        void PaintSpectrum(Graphics g)
        {
            if (visRect.IsEmpty) return;
            int n = SpectrumAnalyzer.Bands;
            float gap = Theme.S(4f) * k, bw = (visRect.Width - gap * (n - 1)) / n;
            float baseY = visRect.Bottom, h = visRect.Height - Theme.S(4f);
            Color bar = Theme.Alpha(Theme.Text, 175), cap = Theme.Alpha(Theme.Text, 110), rest = Theme.Alpha(Theme.Overlay, 34);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int b = 0; b < n; b++)
            {
                float x = visRect.X + b * (bw + gap);
                float lh = Math.Max(Theme.S(3f), spectrum.Level[b] * h);
                Theme.FillRound(g, spectrum.Level[b] > 0.01f ? bar : rest, new RectangleF(x, baseY - lh, bw, lh), Math.Min(bw / 2, Theme.S(3f)));
                if (spectrum.Peak[b] > 0.02f)
                {
                    float py = baseY - spectrum.Peak[b] * h - Theme.S(5f);
                    Theme.FillRound(g, cap, new RectangleF(x, py, bw, Theme.S(2.5f)), Theme.S(1.2f));
                }
            }
            g.SmoothingMode = SmoothingMode.Default;
        }

        /// <summary>Per frame: advance the analyzer and repaint just the bars. Runs while music plays or the bars are falling.</summary>
        bool StepSpectrum(double dt)
        {
            if (visRect.IsEmpty || !Visible) return false;
            bool live = host.IsPlaying;
            spectrum.Update(live ? host.SpectrumTap : null, dt);
            Invalidate(Rectangle.Inflate(visRect, 2, Theme.S(8)));
            return live || spectrum.Active;
        }

        // ------------------------------------------------------------------ signal path card (opened from the status chip)

        bool pathOpen;
        Rectangle pathCard, sleepRect;

        public bool SignalPathOpen { get { return pathOpen; } }
        public void CloseSignalPath() { if (pathOpen) { pathOpen = false; Invalidate(); } }

        static Color StepColor(StepState s) { return s == StepState.Untouched ? Theme.Good : s == StepState.Changed ? Theme.Warn : Theme.TextDim; }

        void PaintSignalPath(Graphics g)
        {
            List<SignalStep> steps = host.SignalPath();
            int w = Math.Min(K(460), Width - K(32));
            int head = K(50), rowH = K(58), foot = K(52), pad = K(16);
            int h = head + steps.Count * rowH + foot;
            // Over the artwork column, so the seek bar and the transport buttons stay usable while it is open.
            // Only when there is no room there (narrow windows) does it drop below the chip.
            int x, y;
            int leftRoom = titleRect.X - K(24);
            if (leftRoom - w >= K(16))
            {
                x = leftRoom - w;
                y = Math.Max(K(10), Math.Min(Height - h - K(10), titleRect.Y));
            }
            else
            {
                x = Math.Max(K(16), Math.Min(badgeRect.X + badgeRect.Width / 2 - w / 2, Width - w - K(16)));
                y = badgeRect.Bottom + K(10);
                if (y + h > Height - K(10)) y = Math.Max(K(10), Math.Min(Height - h - K(10), badgeRect.Y - K(10) - h));
            }
            pathCard = new Rectangle(x, y, w, h);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 4; i >= 1; i--)
                Theme.FillRound(g, Color.FromArgb(18, 0, 0, 0), new Rectangle(x - i * 2, y - i * 2 + K(6), w + i * 4, h + i * 4), Theme.S(14f) + i * 2);
            Theme.FillRound(g, Theme.Card, pathCard, Theme.S(14f));
            using (var p = new Pen(Theme.Border))
            using (var path = Theme.Round(new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1), Theme.S(14f)))
                g.DrawPath(p, path);

            var ell = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Theme.DrawText(g, "Signal path", Theme.UiSemibold(11f * k), Theme.Text, new Rectangle(x + pad, y, w - 2 * pad, head), ell);
            string badge = host.BadgeText;
            Font bf = Theme.UiSemibold(7.5f * k);
            int bw = TextRenderer.MeasureText(badge, bf).Width + K(18), bh = K(22);
            var bRect = new Rectangle(x + w - pad - bw, y + (head - bh) / 2, bw, bh);
            Theme.FillRound(g, Theme.Alpha(host.BadgeColor, 230), bRect, bh / 2f);
            Theme.DrawText(g, badge, bf, Color.White, bRect, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int lineX = x + pad + K(7);
            int top = y + head;
            for (int i = 0; i < steps.Count; i++)
            {
                SignalStep s = steps[i];
                int ry = top + i * rowH;
                int cy = ry + K(14);
                if (i < steps.Count - 1)
                    using (var p = new Pen(Theme.Alpha(Theme.Overlay, 45), Theme.S(2f))) g.DrawLine(p, lineX, cy + K(8), lineX, cy + rowH - K(8));
                Color c = StepColor(s.State);
                int d = K(12);
                using (var b = new SolidBrush(Theme.Alpha(c, 60))) g.FillEllipse(b, lineX - d / 2 - K(3), cy - d / 2 - K(3), d + K(6), d + K(6));
                using (var b = new SolidBrush(c)) g.FillEllipse(b, lineX - d / 2, cy - d / 2, d, d);
                int tx = lineX + K(20), tw = x + w - pad - tx;
                Theme.DrawText(g, s.Title, Theme.UiSemibold(9.5f * k), Theme.Text, new Rectangle(tx, cy - K(10), tw, K(20)), ell);
                TextRenderer.DrawText(g, s.Detail ?? "", Theme.Ui(8.5f * k), new Rectangle(tx, cy + K(10), tw, rowH - K(26)),
                    Theme.Mix(Theme.Text, Theme.TextDim, 0.4),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            int fy = top + steps.Count * rowH;
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, x + pad, fy + K(4), x + w - pad, fy + K(4));
            TextRenderer.DrawText(g, host.SignalVerdict, Theme.UiMedium(9f * k), new Rectangle(x + pad, fy + K(10), w - 2 * pad, foot - K(14)),
                host.BadgeColor == Theme.Good ? Theme.Good : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            g.SmoothingMode = SmoothingMode.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (pathOpen)
            {
                // A click inside the card stays there. A click anywhere else closes it and still does what it was aimed at
                // (it used to be swallowed, so e.g. the play button needed a second click).
                if (pathCard.Contains(e.Location)) return;
                pathOpen = false;
                Invalidate();
                if (badgeRect.Contains(e.Location)) return;   // the chip itself just toggles the card
            }
            if (e.Button == MouseButtons.Left && badgeRect.Contains(e.Location)) { pathOpen = true; SetTip(null); tipOnBadge = false; Invalidate(); return; }
            if (e.Button == MouseButtons.Left && sleepRect.Contains(e.Location)) { sleepPressed = true; return; }   // the menu opens on release
            base.OnMouseDown(e);
        }

        bool sleepPressed;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (sleepPressed)
            {
                // Opening a menu on button-down lets the following button-up close it again: open it on release instead.
                sleepPressed = false;
                if (sleepRect.Contains(e.Location)) host.ShowSleepMenu(this, new Point(sleepRect.X, sleepRect.Bottom + K(4)));
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (pathOpen && pathCard.Contains(e.Location)) { Cursor = Cursors.Default; return; }   // nothing under the card reacts
            base.OnMouseMove(e);
        }

        void PaintUpNext(Graphics g, TrackInfo t)
        {
            for (int i = 0; i < upNextIndex.Length; i++) upNextIndex[i] = -1;
            if (dividerY < 0) return;
            using (var p = new Pen(Theme.Alpha(Theme.Overlay, 34))) g.DrawLine(p, upNextLabel.X, dividerY, upNextLabel.Right, dividerY);
            Theme.DrawText(g, "Up next", Theme.UiSemibold(10f * k), Theme.Mix(Theme.Text, Theme.TextDim, 0.25), upNextLabel,
                           TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);

            List<TrackInfo> q = host.QueueSnapshot();
            int cur = t != null ? q.IndexOf(t) : -1;
            var ell = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int shown = 0;
            for (int i = 0; i < upNextRows.Count; i++)
            {
                int qi = cur + 1 + i;
                if (qi < 0 || qi >= q.Count) break;
                upNextIndex[i] = qi;
                shown++;
                Rectangle row = upNextRows[i];
                float hv = Fade.Get(upNextKeys[i]);
                if (hv > 0.01f) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(20 * hv)), row, Theme.S(8f));
                int th = row.Height - K(10);
                var art = new Rectangle(row.X + K(8), row.Y + (row.Height - th) / 2, th, th);
                TrackInfo n = q[qi];
                Theme.ArtTile(g, n, art, Theme.S(5f), Library.AlbumTitle(n), false);
                int tx = art.Right + K(14), tw = row.Right - K(8) - tx;
                int mid = row.Y + row.Height / 2;
                Theme.DrawText(g, n.DisplayTitle, Theme.UiMedium(10.5f * k), Theme.Text, new Rectangle(tx, mid - K(20), tw, K(20)), ell);
                Theme.DrawText(g, n.DisplayArtist.ToUpperInvariant(), Theme.Ui(8f * k), Theme.Mix(Theme.Text, Theme.TextDim, 0.45),
                               new Rectangle(tx, mid + K(1), tw, K(18)), ell);
            }
            if (shown == 0 && upNextRows.Count > 0)
                Theme.DrawText(g, t == null ? "The queue is empty" : "Nothing queued after this song", Theme.Ui(9.5f * k), Theme.TextDim,
                               new Rectangle(upNextLabel.X, upNextRows[0].Y, upNextLabel.Width, K(30)), ell);
        }

        int UpNextAt(Point p)
        {
            if (anim > 0.5 && lyricsRect.Contains(p)) return -1;
            for (int i = 0; i < upNextRows.Count; i++) if (upNextIndex[i] >= 0 && upNextRows[i].Contains(p)) return i;
            return -1;
        }

        protected override Rectangle FadeBounds(object key)
        {
            for (int i = 0; i < upNextKeys.Length; i++)
                if (key == upNextKeys[i]) return i < upNextRows.Count ? Rectangle.Inflate(upNextRows[i], 2, 2) : Rectangle.Empty;
            return base.FadeBounds(key);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverUpNext >= 0) { FadeHover(upNextKeys[hoverUpNext], null); hoverUpNext = -1; }
            tipOnBadge = false;
        }

        void PaintArtwork(Graphics g, TrackInfo t, double alpha)
        {
            float radius = Theme.S(12f) * k;
            int pad = Theme.S(26);
            Image img = ArtCache.GetLarge(t);
            if (artSurface == null || artLayerSrc != img || artLayerSize != artRect.Size || artLayerPad != pad)
            {
                if (artSurface != null) artSurface.Dispose();
                // Shadow and artwork go into one premultiplied layer, so every frame is a single blit (or a single
                // constant-alpha AlphaBlend while fading into the lyrics) instead of seven anti-aliased fills plus a scaled image.
                using (var layer = new Bitmap(artRect.Width + 2 * pad, artRect.Height + 2 * pad, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (var lg = Graphics.FromImage(layer))
                    {
                        lg.SmoothingMode = SmoothingMode.AntiAlias;
                        for (int i = 6; i >= 1; i--)
                            Theme.FillRound(lg, Color.FromArgb(14, 0, 0, 0), new Rectangle(pad - i * Theme.S(3), pad - i * Theme.S(2) + Theme.S(8),
                                            artRect.Width + i * Theme.S(6), artRect.Height + i * Theme.S(6)), radius + i * Theme.S(3));
                        var r = new Rectangle(pad, pad, artRect.Width, artRect.Height);
                        Theme.Art(lg, img, r, radius, t != null ? Library.AlbumTitle(t) : "none", false);
                        using (var p = new Pen(Theme.Alpha(Theme.Overlay, 28)))
                        using (var path = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius))
                            lg.DrawPath(p, path);
                    }
                    artSurface = new GdiSurface(layer);
                }
                artLayerSrc = img;
                artLayerSize = artRect.Size;
                artLayerPad = pad;
            }
            var dst = new Rectangle(artRect.X - pad, artRect.Y - pad, artRect.Width + 2 * pad, artRect.Height + 2 * pad);
            artSurface.Draw(g, dst, Point.Empty, (byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), true);
            if (cover != null && cover.HasFrame && cover.Surface != null && cover.Size == artRect.Size)
                cover.Surface.Draw(g, artRect, Point.Empty, (byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha * coverFade))), true);
        }
        // ------------------------------------------------------------------ lyrics

        Lyrics CurrentLyrics(out bool loading)
        {
            Lyrics l;
            loading = !LyricsService.TryGet(host.NowPlaying, out l);
            return l;
        }

        Font LyricFont(Lyrics l) { return l != null && l.Synced ? Theme.UiSemibold(17f * k) : Theme.UiMedium(13f * k); }

        void EnsureLyricLayout(Graphics g, Lyrics l)
        {
            TrackInfo t = host.NowPlaying;
            if (layoutTrack == t && layoutLyrics == l && layoutWidth == lyricsRect.Width && layoutK == k) return;
            layoutTrack = t; layoutLyrics = l; layoutWidth = lyricsRect.Width; layoutK = k;
            boxes.Clear();
            totalH = 0;
            currentLine = -1;
            if (l == null) return;
            Font f = LyricFont(l);
            float gap = (l.Synced ? Theme.S(14f) : Theme.S(6f)) * k;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                foreach (LyricLine line in l.Lines)
                {
                    string text = line.Text.Length == 0 ? " " : line.Text;
                    SizeF sz = g.MeasureString(text, f, lyricsRect.Width, sf);
                    float h = line.Text.Length == 0 ? sz.Height * 0.5f : sz.Height;
                    boxes.Add(new RectangleF(0, totalH, lyricsRect.Width, h));
                    totalH += h + gap;
                }
            lyricScroll = l.Synced ? -lyricsRect.Height * 0.38 : 0;
        }

        int LineAt(double position, Lyrics l)
        {
            int idx = -1;
            for (int i = 0; i < l.Lines.Count; i++) { if (l.Lines[i].Time <= position + 0.15) idx = i; else break; }
            return idx;
        }

        /// <summary>Eases the lyrics toward the current line. Returns true while still moving.</summary>
        bool UpdateLyricScroll(double dt)
        {
            Lyrics l = layoutLyrics;
            if (l == null || boxes.Count == 0 || !l.Synced) return false;
            int line = host.IsRunning ? LineAt(host.Position, l) : -1;
            bool changed = line != currentLine;
            currentLine = line;
            if (unchecked(Environment.TickCount - manualUntil) < 0) return changed;   // the user is scrolling by hand
            double target = currentLine < 0 ? -lyricsRect.Height * 0.38
                : boxes[currentLine].Y + boxes[currentLine].Height / 2 - lyricsRect.Height * 0.38;
            lyricScroll += (target - lyricScroll) * (1 - Math.Exp(-dt * 5));
            if (Math.Abs(target - lyricScroll) < 0.3) { lyricScroll = target; return changed; }
            return true;
        }

        void PaintLyrics(Graphics g, TrackInfo t, double mix)
        {
            bool loading;
            Lyrics l = CurrentLyrics(out loading);
            if (l != null && (l.Source == LyricsService.ErrorSource || l.Instrumental)) { }
            else EnsureLyricLayout(g, l);

            string message = null;
            if (t == null) message = "Play a song to see its lyrics";
            else if (loading) message = "Looking for lyrics…";
            else if (l == null) message = (LyricsService.OnlineEnabled ? "No lyrics found for this song" : "No lyrics found — online lookup (LRCLIB) is off in Settings") +
                                          "\nTry “Search lyrics…” in the ⋯ menu";
            else if (l.Source == LyricsService.ErrorSource) message = "Couldn’t reach lrclib.net — check your connection";
            else if (l.Instrumental) message = "♪  Instrumental  ♪";

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int a = (int)(255 * mix);
            if (message != null)
            {
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(Theme.Alpha(Theme.TextDim, a)))
                    g.DrawString(message, Theme.UiMedium(13f * k), b, lyricsRect, sf);
                return;
            }

            Font f = LyricFont(l);
            float fade = Theme.S(70f);
            g.SetClip(lyricsRect);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                for (int i = 0; i < boxes.Count; i++)
                {
                    RectangleF bx = boxes[i];
                    float y = lyricsRect.Y + bx.Y - (float)lyricScroll;
                    if (y + bx.Height < lyricsRect.Top || y > lyricsRect.Bottom) continue;
                    double la;
                    if (!l.Synced) la = 225;
                    else if (i == currentLine) la = 255;
                    else if (i == hoverLine) la = 205;
                    else la = i < currentLine ? 105 : 140;
                    float edge = Math.Min(y - lyricsRect.Top, lyricsRect.Bottom - (y + bx.Height));
                    if (edge < fade) la *= Math.Max(0, edge / fade);   // soft fade at the top and bottom
                    la *= mix;
                    Color c = l.Synced && i == currentLine ? Theme.Text : Theme.Mix(Theme.Text, Theme.TextDim, l.Synced ? 0.25 : 0.1);
                    using (var b = new SolidBrush(Theme.Alpha(c, (int)la)))
                        g.DrawString(l.Lines[i].Text, f, b, new RectangleF(lyricsRect.X, y, lyricsRect.Width, bx.Height + 2), sf);
                }
            g.ResetClip();

            string credit = "Lyrics: " + (l.Source == "LRCLIB" ? "LRCLIB (lrclib.net)" : l.Source) + (l.Synced ? "  ·  synced" : "");
            using (var sf = new StringFormat { Alignment = StringAlignment.Far })
            using (var b = new SolidBrush(Theme.Alpha(Theme.TextDim, (int)(170 * mix))))
                g.DrawString(credit, Theme.Ui(8f), b, new RectangleF(lyricsRect.X, lyricsRect.Bottom - Theme.S(16), lyricsRect.Width, Theme.S(18)), sf);
        }

        int LyricLineAt(Point p)
        {
            Lyrics l = layoutLyrics;
            if (!lyricsOn || anim < 1 || l == null || !l.Synced || !lyricsRect.Contains(p)) return -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                float y = lyricsRect.Y + boxes[i].Y - (float)lyricScroll;
                if (p.Y >= y && p.Y < y + boxes[i].Height && l.Lines[i].Text.Length > 0) return i;
            }
            return -1;
        }

        protected override Cursor CursorAt(Point p)
        {
            return LyricLineAt(p) >= 0 || UpNextAt(p) >= 0 || badgeRect.Contains(p) || sleepRect.Contains(p) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnCanvasMouseMove(MouseEventArgs e)
        {
            int h = LyricLineAt(e.Location);
            if (h != hoverLine) { hoverLine = h; Invalidate(lyricsRect); }
            int u = UpNextAt(e.Location);
            if (u != hoverUpNext)
            {
                FadeHover(hoverUpNext >= 0 ? upNextKeys[hoverUpNext] : null, u >= 0 ? upNextKeys[u] : null);
                hoverUpNext = u;
            }
            bool onBadge = badgeRect.Contains(e.Location) && !IsOverWidget(e.Location);
            if (onBadge != tipOnBadge) { tipOnBadge = onBadge; SetTip(onBadge ? BadgeTip() : null); }
        }

        protected override void OnCanvasMouseDown(MouseEventArgs e)
        {
            int i = LyricLineAt(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left)
            {
                host.SeekSeconds(layoutLyrics.Lines[i].Time);
                manualUntil = 0;
                return;
            }
            int u = UpNextAt(e.Location);
            if (u >= 0 && e.Button == MouseButtons.Left) { host.PlayQueueIndex(upNextIndex[u]); return; }
            if (e.Button == MouseButtons.Right && anim < 0.5 && artRect.Contains(e.Location) && ArtContextRequested != null)
            {
                ArtContextRequested(e.Location);
                return;
            }
            if (e.Button == MouseButtons.Left && e.Y < headerRect.Bottom && !host.IsFullscreen) host.BeginWindowDrag();
        }

        /// <summary>Right-click on the artwork (the host shows the artwork menu).</summary>
        public event Action<Point> ArtContextRequested;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!lyricsOn || layoutLyrics == null || !lyricsRect.Contains(e.Location)) return;
            float min = layoutLyrics.Synced ? -lyricsRect.Height * 0.4f : 0;
            float max = Math.Max(0, totalH - lyricsRect.Height * (layoutLyrics.Synced ? 0.5f : 0.9f));
            lyricScroll = Math.Max(min, Math.Min(max, lyricScroll - e.Delta * Theme.S(60f) / 120));
            manualUntil = Environment.TickCount + 4000;   // auto-follow resumes after 4 s
            Invalidate();
        }
    }

    // ======================================================================= mini player (other pages)

    internal sealed class MiniPlayer : CanvasControl
    {
        readonly IPlayerHost host;
        readonly CanvasButton prevBtn, playBtn, nextBtn, expandBtn;
        readonly CanvasSlider seek, volume;
        Rectangle artRect, infoRect;

        public MiniPlayer(IPlayerHost host)
        {
            this.host = host;
            BackColor = Theme.Surface;
            prevBtn = AddButton(Glyphs.Prev, BtnStyle.Ghost, 14f, "Previous", host.Previous);
            playBtn = AddButton(Glyphs.Play, BtnStyle.Circle, 16f, "Play / Pause", host.PlayPause);
            nextBtn = AddButton(Glyphs.Next, BtnStyle.Ghost, 14f, "Next", host.Next);
            expandBtn = AddButton(Glyphs.ChevronUp, BtnStyle.Ghost, 11f, "Open Now Playing", host.ShowNowPlaying);
            seek = AddSlider(null, host.SeekFraction);
            seek.KnobOnHover = true;
            volume = AddSlider(host.SetVolume, host.SetVolume);
            Height = Theme.S(88);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int cx = Width / 2;
            artRect = new Rectangle(Theme.S(18), Theme.S(14), Theme.S(60), Theme.S(60));
            int big = Theme.S(46), small = Theme.S(40);
            playBtn.Bounds = new Rectangle(cx - big / 2, Theme.S(6), big, big);
            prevBtn.Bounds = new Rectangle(cx - Theme.S(70) - small / 2, Theme.S(9), small, small);
            nextBtn.Bounds = new Rectangle(cx + Theme.S(70) - small / 2, Theme.S(9), small, small);
            int sw = Math.Max(Theme.S(160), Math.Min(Theme.S(520), Width - Theme.S(720)));
            seek.Bounds = new Rectangle(cx - sw / 2, Theme.S(58), sw, Theme.S(18));
            volume.Bounds = new Rectangle(Width - Theme.S(140), Theme.S(34), Theme.S(110), Theme.S(18));
            expandBtn.Bounds = new Rectangle(volume.Bounds.X - Theme.S(30) - Theme.S(14) - Theme.S(36), Theme.S(26), Theme.S(36), Theme.S(34));
            // Title and artist end before whichever comes first: the Previous button or the seek bar's elapsed-time label
            // (drawn 60 px left of the bar), with a gap so long artist lists never run under them.
            int infoRight = Math.Min(prevBtn.Bounds.X, seek.Bounds.X - Theme.S(60)) - Theme.S(24);
            infoRect = new Rectangle(artRect.Right + Theme.S(14), Theme.S(18), Math.Max(Theme.S(60), infoRight - artRect.Right - Theme.S(14)), Theme.S(52));
        }

        public void UpdateState()
        {
            TrackInfo t = host.NowPlaying;
            playBtn.Glyph = host.IsPlaying ? Glyphs.Pause : Glyphs.Play;
            if (!IsDraggingSlider)
            {
                double d = t != null ? t.Duration : 0;
                seek.Value = host.IsRunning && d > 0 ? host.Position / d : 0;
                volume.Value = host.Volume;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBackground(g);
            if (Theme.Glass) using (var b = new SolidBrush(Theme.PanelFill(Theme.Surface))) g.FillRectangle(b, ClientRectangle);   // frosted bar
            using (var p = new Pen(Theme.Glass ? Theme.PanelBorder(Theme.Border) : Theme.Alpha(Theme.Overlay, 18))) g.DrawLine(p, 0, 0, Width, 0);
            TrackInfo t = host.NowPlaying;
            Theme.ArtTile(g, t, artRect, Theme.S(8f), t != null ? Library.AlbumTitle(t) : "none", false);
            var ell = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Theme.DrawText(g, t != null ? t.DisplayTitle : "Nothing playing", Theme.UiSemibold(10f), Theme.Text,
                           new Rectangle(infoRect.X, infoRect.Y, infoRect.Width, Theme.S(24)), ell);
            Theme.DrawText(g, t != null ? t.DisplayArtist : "", Theme.Ui(9f), Theme.TextDim,
                           new Rectangle(infoRect.X, infoRect.Y + Theme.S(24), infoRect.Width, Theme.S(22)), ell);

            double dur = t != null ? t.Duration : 0;
            double pos = seek.Dragging ? seek.Value * dur : (host.IsRunning ? host.Position : 0);
            Theme.DrawText(g, Theme.Time(pos), Theme.Ui(8.5f), Theme.TextDim, new Rectangle(seek.Bounds.X - Theme.S(60), seek.Bounds.Y, Theme.S(54), seek.Bounds.Height),
                       TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Theme.DrawText(g, Theme.Time(dur), Theme.Ui(8.5f), Theme.TextDim, new Rectangle(seek.Bounds.Right + Theme.S(6), seek.Bounds.Y, Theme.S(54), seek.Bounds.Height),
                       TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            Font cf = Theme.UiSemibold(8f);
            string badge = host.BadgeText;
            int bw = TextRenderer.MeasureText(badge, cf).Width + Theme.S(16);
            var chip = new Rectangle(expandBtn.Bounds.X - bw - Theme.S(14), Theme.S(31), bw, Theme.S(22));
            if (chip.X > infoRect.Right && chip.X > nextBtn.Bounds.Right + Theme.S(10))
            {
                Theme.FillRound(g, Theme.Alpha(host.BadgeColor, 225), chip, chip.Height / 2f);
                Theme.DrawText(g, badge, cf, Color.White, chip, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            Theme.GlyphCenter(g, host.Volume <= 0.001 ? Glyphs.Mute : Glyphs.Volume, 10f, Theme.TextDim,
                              new Rectangle(volume.Bounds.X - Theme.S(26), volume.Bounds.Y, Theme.S(22), volume.Bounds.Height));
            PaintWidgets(g);
        }

        protected override Cursor CursorAt(Point p)
        {
            return artRect.Contains(p) || infoRect.Contains(p) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnCanvasMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && (artRect.Contains(e.Location) || infoRect.Contains(e.Location))) host.ShowNowPlaying();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    internal enum ThemeMode { Dark, Light, System, Custom }

    /// <summary>
    /// Tabler Icons (MIT, tabler.io) code points. Outline icons come from the embedded "tabler-icons" font (1.5 px stroke,
    /// rounded caps); the solid transport icons from "tabler-fills" (Tabler's filled set, family renamed so both can load).
    /// </summary>
    internal static class Glyphs
    {
        // Filled (tabler-fills)
        public const string Play = "", Pause = "", Stop = "", Prev = "", Next = "";

        // Outline (tabler-icons)
        public const string Music = "", NowPlaying = "", Album = "", Artist = "", Songs = "",
            Settings = "", Queue = "", Repeat = "", RepeatOne = "", Shuffle = "",
            Volume = "", Mute = "", Back = "", ChevronDown = "", ChevronUp = "", ChevronRight = "",
            Add = "", Folder = "", FolderPlain = "", Sort = "", Refresh = "",
            Minimize = "", Maximize = "", Restore = "", Close = "",
            SidebarCollapse = "", SidebarExpand = "", Lyrics = "", Fullscreen = "", ExitFullscreen = "",
            Palette = "", Check = "", Trash = "", ExternalLink = "", PlaylistAdd = "", PlayNext = "",
            Sun = "", Moon = "", Desktop = "", Speaker = "", Info = "", PlayOutline = "";

        public const string Equalizer = "", Lock = "", Adjust = "";
        public const string More = "", Microphone = "";

        public const string Search = "";
        public const string Playlist = "", Pencil = "", Glass = "";

        public static bool IsFilled(string g) { return g == Play || g == Pause || g == Stop || g == Prev || g == Next; }
    }
    internal static class Theme
    {
        // Palette (set by Apply). Overlay is the colour used for translucent hover/press fills: white on dark, black on light.
        public static Color Bg, Sidebar, Surface, Card, Border, Text, TextDim, Accent, Select, SelectText, Overlay;
        public static readonly Color Good = Color.FromArgb(46, 184, 114);
        public static readonly Color Warn = Color.FromArgb(226, 146, 52);
        public static readonly Color Bad = Color.FromArgb(235, 92, 92);
        public static bool IsDark = true;
        public static ThemeMode Mode = ThemeMode.Dark;

        // ---- Frosted glass: the whole window shows a blurred picture (album art or the desktop wallpaper) behind translucent panels.
        /// <summary>Frosted glass on: pages and the title bar paint the blurred backdrop instead of a flat colour, panels become translucent.</summary>
        public static bool Glass;
        /// <summary>How clear the glass is, 0 (almost opaque panels) … 1 (very transparent).</summary>
        public static double GlassLevel = 0.5;
        /// <summary>Set by the main window: paints the backdrop under a control; returns false when it can't (then the flat colour is used).</summary>
        public static Func<Graphics, Control, bool> BackgroundPainter;

        /// <summary>Panel alpha (sidebar, mini player, cards) for the current glass level.</summary>
        public static int GlassPanelAlpha(int solidAlpha, int clearAlpha) { return (int)Math.Round(solidAlpha + (clearAlpha - solidAlpha) * GlassLevel); }

        /// <summary>A translucent panel colour on glass; <paramref name="solid"/> otherwise.</summary>
        public static Color PanelFill(Color solid)
        {
            if (!Glass) return solid;
            // Dark: charcoal glass; light: white glass. Colour of the theme's own surface keeps custom tints.
            return Alpha(IsDark ? Mix(solid, Color.Black, 0.35) : Mix(solid, Color.White, 0.5), GlassPanelAlpha(215, 95));
        }

        /// <summary>Edge of a glass panel: a faint light line, like the lit rim of frosted glass.</summary>
        public static Color PanelBorder(Color solid)
        {
            return Glass ? Alpha(IsDark ? Color.White : Color.White, IsDark ? 34 : 150) : solid;
        }
        public static Color CustomColor = Color.FromArgb(124, 92, 255);
        /// <summary>User-chosen accent colour; null = the theme's own accent (blue, Windows accent, or the custom colour).</summary>
        public static Color? AccentOverride;
        /// <summary>The accent the current theme would use on its own (shown as the 'theme default' swatch).</summary>
        public static Color ThemeAccent = Color.FromArgb(82, 156, 255);

        public static float Scale = 1f;
        public static string GlyphFamily = "tabler-icons", GlyphFilledFamily = "tabler-fills";
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        static Theme() { Apply(ThemeMode.Dark, CustomColor); }

        public static void Init(float scale)
        {
            Scale = scale;
            LoadEmbeddedFonts();
            if (!privateFamilies.ContainsKey(GlyphFamily)) GlyphFamily = "Segoe MDL2 Assets";
            if (!privateFamilies.ContainsKey(GlyphFilledFamily)) GlyphFilledFamily = GlyphFamily;
        }

        // ---- App icon (the SoDa can), embedded as resources by build.ps1 ----
        static Icon appIconSet;
        static Image appIcon;

        /// <summary>Multi-size icon for the window / taskbar.</summary>
        public static Icon AppIconSet
        {
            get
            {
                if (appIconSet == null)
                    try { using (var s = typeof(Theme).Assembly.GetManifestResourceStream("BitPerfectPlayer.AppIcon.ico")) if (s != null) appIconSet = new Icon(s); } catch { }
                return appIconSet;
            }
        }

        /// <summary>256 px artwork for drawing the logo inside the app.</summary>
        public static Image AppIcon
        {
            get
            {
                if (appIcon == null)
                    try
                    {
                        using (var s = typeof(Theme).Assembly.GetManifestResourceStream("BitPerfectPlayer.AppIcon.png"))
                            if (s != null) using (var img = Image.FromStream(s)) appIcon = new Bitmap(img);
                    }
                    catch { }
                return appIcon;
            }
        }

        public static void DrawAppIcon(Graphics g, Rectangle r) { DrawAppIcon(g, r, 1f); }

        /// <summary>The logo with an opacity (0–1), for fading it in and out.</summary>
        public static void DrawAppIcon(Graphics g, Rectangle r, float alpha)
        {
            Image img = AppIcon;
            if (alpha <= 0.01f) return;
            if (img == null) { GlyphCenter(g, Glyphs.Music, r.Height * 0.55f / Scale, Mix(Sidebar, Accent, alpha), r); return; }
            var oldI = g.InterpolationMode; var oldP = g.PixelOffsetMode;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            if (alpha >= 0.99f) g.DrawImage(img, r);
            else
                using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                {
                    attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
                    g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
                }
            g.InterpolationMode = oldI; g.PixelOffsetMode = oldP;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static double Luma(Color c) { return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0; }

        /// <summary>Windows "Apps use light theme" setting.</summary>
        public static bool SystemUsesDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v == null || Convert.ToInt32(v) == 0;
                }
            }
            catch { return true; }
        }

        /// <summary>Windows accent colour (DWM), or null.</summary>
        public static Color? SystemAccent()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k != null ? k.GetValue("AccentColor") : null;
                    if (v == null) return null;
                    uint abgr = unchecked((uint)Convert.ToInt32(v));
                    return Color.FromArgb((int)(abgr & 0xFF), (int)((abgr >> 8) & 0xFF), (int)((abgr >> 16) & 0xFF));
                }
            }
            catch { return null; }
        }

        /// <summary>Sets the palette. Custom: the colour becomes the accent and tints the surfaces; dark or light follows its brightness.</summary>
        public static void Apply(ThemeMode mode, Color custom)
        {
            Mode = mode;
            CustomColor = custom;
            Color accent;
            Color? tint = null;
            switch (mode)
            {
                case ThemeMode.Light: IsDark = false; accent = Color.FromArgb(0, 103, 192); break;
                case ThemeMode.System:
                    IsDark = SystemUsesDark();
                    Color? sys = SystemAccent();
                    accent = sys.HasValue ? sys.Value : (IsDark ? Color.FromArgb(82, 156, 255) : Color.FromArgb(0, 103, 192));
                    break;
                case ThemeMode.Custom:
                    IsDark = Luma(custom) < 0.6;
                    accent = custom;
                    tint = custom;
                    break;
                default: IsDark = true; accent = Color.FromArgb(82, 156, 255); break;
            }
            ThemeAccent = accent;
            if (AccentOverride.HasValue) accent = AccentOverride.Value;

            if (IsDark)
            {
                // Neutral charcoal greys (no blue cast); the accent colour stays for highlights.
                Bg = Color.FromArgb(24, 24, 26); Sidebar = Color.FromArgb(31, 31, 34); Surface = Color.FromArgb(37, 37, 41);
                Card = Color.FromArgb(34, 34, 37); Border = Color.FromArgb(54, 54, 59);
                Text = Color.FromArgb(240, 240, 242); TextDim = Color.FromArgb(158, 158, 165);
                Overlay = Color.White;
                if (tint.HasValue)
                {
                    Color t = tint.Value;
                    Bg = Mix(Color.FromArgb(17, 17, 21), t, 0.10); Sidebar = Mix(Color.FromArgb(23, 23, 28), t, 0.13);
                    Surface = Mix(Color.FromArgb(27, 27, 33), t, 0.15); Card = Mix(Color.FromArgb(25, 25, 31), t, 0.12);
                    Border = Mix(Color.FromArgb(44, 44, 52), t, 0.18);
                    if (Luma(accent) < 0.35) accent = Mix(accent, Color.White, 0.35);   // keep a very dark custom colour visible
                }
                if ((mode != ThemeMode.Dark || AccentOverride.HasValue) && Luma(accent) < 0.3) accent = Mix(accent, Color.White, 0.3);   // keep dark accents visible
                Select = mode == ThemeMode.Dark ? Color.FromArgb(62, 62, 68) : Mix(Bg, accent, 0.38);   // Dark: grey selection pill
                SelectText = Color.White;
            }
            else
            {
                Bg = Color.FromArgb(246, 247, 250); Sidebar = Color.FromArgb(236, 239, 244); Surface = Color.FromArgb(255, 255, 255);
                Card = Color.FromArgb(255, 255, 255); Border = Color.FromArgb(222, 226, 233);
                Text = Color.FromArgb(24, 28, 36); TextDim = Color.FromArgb(98, 106, 122);
                Overlay = Color.Black;
                if (tint.HasValue)
                {
                    Color t = tint.Value;
                    Bg = Mix(Color.FromArgb(248, 248, 250), t, 0.08); Sidebar = Mix(Color.FromArgb(238, 238, 242), t, 0.14);
                    Surface = Mix(Color.White, t, 0.05); Card = Mix(Color.White, t, 0.04); Border = Mix(Color.FromArgb(222, 222, 228), t, 0.2);
                }
                if (Luma(accent) > 0.55) accent = Mix(accent, Color.Black, 0.45);   // a pale accent needs contrast on light surfaces
                Select = Mix(Bg, accent, 0.2);
                SelectText = Text;
            }
            Accent = accent;
            // Secondary text sits straight on the picture on frosted glass, so it gets a little more contrast.
            if (Glass) TextDim = IsDark ? Mix(TextDim, Color.White, 0.28) : Mix(TextDim, Color.Black, 0.3);
            var h = Changed; if (h != null) h();
        }

        public static event Action Changed;

        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static float S(float px) { return px * Scale; }

        public static Font Ui(float size) { return GetFont(UiRegular, size); }
        public static Font UiMedium(float size) { return GetFont(UiMediumFamily, size); }
        public static Font UiSemibold(float size) { return GetFont(UiSemiboldFamily, size); }
        public static Font Glyph(float size) { return GetFont(GlyphFamily, size); }
        public static Font GlyphFor(string glyph, float size) { return GetFont(Glyphs.IsFilled(glyph) ? GlyphFilledFamily : GlyphFamily, size); }

        // ---- Embedded Inter (Google Fonts, SIL Open Font License) ----------------------------------------
        // The three static TTFs are compiled into the exe as resources. They are registered with GDI+
        // (PrivateFontCollection, for Font objects) and with GDI (AddFontMemResourceEx, which TextRenderer and
        // the standard controls draw with). Falls back to Segoe UI if anything fails.
        static readonly string[] EmbeddedFonts = { "Inter-Regular.ttf", "Inter-Medium.ttf", "Inter-SemiBold.ttf", "tabler-icons-300.ttf", "tabler-icons-filled.ttf" };
        static readonly System.Drawing.Text.PrivateFontCollection privateFonts = new System.Drawing.Text.PrivateFontCollection();
        static readonly Dictionary<string, FontFamily> privateFamilies = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        static readonly List<IntPtr> fontMemory = new List<IntPtr>();   // must stay allocated for the life of the process
        static string UiRegular = "Segoe UI", UiMediumFamily = "Segoe UI Semibold", UiSemiboldFamily = "Segoe UI Semibold";

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

        static void LoadEmbeddedFonts()
        {
            var asm = typeof(Theme).Assembly;
            foreach (string name in EmbeddedFonts)
            {
                try
                {
                    using (var s = asm.GetManifestResourceStream("BitPerfectPlayer.Fonts." + name))
                    {
                        if (s == null) continue;
                        var data = new byte[s.Length];
                        int read = 0;
                        while (read < data.Length) { int n = s.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
                        IntPtr mem = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(data.Length);
                        System.Runtime.InteropServices.Marshal.Copy(data, 0, mem, data.Length);
                        fontMemory.Add(mem);
                        privateFonts.AddMemoryFont(mem, data.Length);
                        uint count;
                        AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, out count);
                    }
                }
                catch { }
            }
            foreach (FontFamily fam in privateFonts.Families) privateFamilies[fam.Name] = fam;
            if (privateFamilies.ContainsKey("Inter")) UiRegular = "Inter";
            if (privateFamilies.ContainsKey("Inter Medium")) UiMediumFamily = "Inter Medium";
            if (privateFamilies.ContainsKey("Inter SemiBold")) UiSemiboldFamily = "Inter SemiBold";
            else if (UiRegular == "Inter") UiSemiboldFamily = UiMediumFamily = "Inter";
        }

        public static bool UsingInter { get { return UiRegular == "Inter"; } }

        static Font GetFont(string family, float size)
        {
            string key = family + "|" + size;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                FontFamily priv;
                try { f = privateFamilies.TryGetValue(family, out priv) ? new Font(priv, size, FontStyle.Regular) : new Font(family, size, FontStyle.Regular); }
                catch { f = new Font("Segoe UI", size, FontStyle.Regular); }
                fonts[key] = f;
            }
            return f;
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
            g.SmoothingMode = old;
        }

        public static void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis |
                                  TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
        }

        public static void TextLeft(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            DrawText(g, s, f, c, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void GlyphCenter(Graphics g, string glyph, float size, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, glyph, GlyphFor(glyph, size), r, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
        }

        /// <summary>Draws an image aspect-filled (cropped) into a rounded rectangle with anti-aliased edges.</summary>
        public static void ImageRounded(Graphics g, Image img, RectangleF r, float radius)
        {
            if (img == null) return;
            float s = Math.Max(r.Width / img.Width, r.Height / img.Height);
            float w = img.Width * s, h = img.Height * s;
            var oldS = g.SmoothingMode; var oldI = g.InterpolationMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            using (var tb = new TextureBrush(img, WrapMode.Clamp))
            using (var path = Round(r, radius))
            {
                tb.TranslateTransform(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2);
                tb.ScaleTransform(s, s);
                g.FillPath(tb, path);
            }
            g.SmoothingMode = oldS; g.InterpolationMode = oldI;
        }

        public static void ImageCircle(Graphics g, Image img, RectangleF r)
        {
            ImageRounded(g, img, r, Math.Min(r.Width, r.Height) / 2);
        }

        /// <summary>Placeholder artwork: soft gradient with a music note.</summary>
        public static void ArtPlaceholder(Graphics g, RectangleF r, float radius, string seed)
        {
            int h = (seed ?? "").GetHashCode();
            int gray = 62 + Math.Abs(h % 22);   // neutral grey placeholder (light themes get a lighter one), slight variation per album
            Color c1 = Theme.IsDark ? Color.FromArgb(255, gray, gray, gray + 4) : Color.FromArgb(255, 150 + gray, 150 + gray, 154 + gray);
            Color c2 = Theme.IsDark ? Color.FromArgb(255, 30, 30, 33) : Color.FromArgb(255, 210, 210, 216);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(r, radius))
            using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width + 1, r.Height + 1), c1, c2, 45f))
                g.FillPath(b, path);
            g.SmoothingMode = old;
            GlyphCenter(g, Glyphs.Music, Math.Max(8f, r.Height * 0.28f / Scale), Alpha(Text, 150), Rectangle.Round(r));
        }

        /// <summary>Draws a track's cover from the tile cache (pre-scaled and rounded once), so painting is a 1:1 blit.</summary>
        public static void ArtTile(Graphics g, TrackInfo t, Rectangle r, float radius, string seed, bool circle)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            Bitmap tile = ArtCache.GetTile(t, r.Width, r.Height, (int)Math.Round(radius), circle, seed);
            var oldI = g.InterpolationMode; var oldP = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(tile, r, 0, 0, tile.Width, tile.Height, GraphicsUnit.Pixel);
            g.InterpolationMode = oldI; g.PixelOffsetMode = oldP;
        }

        public static void Art(Graphics g, Image img, RectangleF r, float radius, string seed, bool circle)
        {
            if (img != null)
            {
                if (circle) ImageCircle(g, img, r); else ImageRounded(g, img, r, radius);
            }
            else ArtPlaceholder(g, r, circle ? Math.Min(r.Width, r.Height) / 2 : radius, seed);
        }

        /// <summary>Little animated "now playing" equalizer bars.</summary>
        public static void EqBars(Graphics g, Rectangle r, Color c, bool animate)
        {
            float bw = Math.Max(2f, r.Width / 5f);
            double t = Environment.TickCount / 1000.0;
            using (var b = new SolidBrush(c))
                for (int i = 0; i < 3; i++)
                {
                    double v = animate ? 0.35 + 0.65 * Math.Abs(Math.Sin(t * (3.1 + i * 1.7) + i * 1.3)) : 0.4 + 0.2 * i;
                    float h = (float)(r.Height * v);
                    g.FillRectangle(b, r.X + i * bw * 1.8f, r.Bottom - h, bw, h);
                }
        }

        public static void DrawImageCover(Graphics g, Image img, Rectangle dest, ImageAttributes attrs)
        {
            float s = Math.Max((float)dest.Width / img.Width, (float)dest.Height / img.Height);
            float sw = dest.Width / s, sh = dest.Height / s;
            float sx = (img.Width - sw) / 2, sy = (img.Height - sh) / 2;
            g.DrawImage(img, dest, sx, sy, sw, sh, GraphicsUnit.Pixel, attrs);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Modern context menu: rounded window (Windows 11), roomy items, rounded highlight, icons, theme colours.</summary>
        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip();
            StyleMenu(m);
            return m;
        }

        /// <summary>Applies the modern look to a menu or sub-menu.</summary>
        static void StyleMenu(ToolStripDropDownMenu m)
        {
            m.Renderer = new ModernMenuRenderer();
            m.ShowImageMargin = true;
            m.ShowCheckMargin = false;
            m.Font = Ui(9.5f);
            m.Padding = new Padding(S(4), S(6), S(4), S(6));
            m.ImageScalingSize = new Size(S(16), S(16));
            m.BackColor = Surface;
            m.ForeColor = Text;
            m.DropShadowEnabled = true;
            m.HandleCreated += delegate
            {
                try { int v = 2; DwmSetWindowAttribute(m.Handle, 33, ref v, 4); } catch { }   // DWMWCP_ROUND
            };
            m.ItemAdded += delegate(object s, ToolStripItemEventArgs e)
            {
                if (e.Item is ToolStripMenuItem) { e.Item.Padding = new Padding(S(2), S(5), S(10), S(5)); e.Item.Margin = new Padding(S(2), 0, S(2), 0); }
                else if (e.Item is ToolStripSeparator) e.Item.Margin = new Padding(S(8), S(3), S(8), S(3));
            };
        }

        /// <summary>An item that opens a sub-menu; add its entries with <see cref="MenuItem"/> to <c>(ToolStripDropDownMenu)item.DropDown</c>.</summary>
        public static ToolStripMenuItem SubMenu(ToolStripDropDownMenu parent, string text, string glyph)
        {
            var it = new ToolStripMenuItem(text) { Image = BlankIcon, Tag = new MenuGlyph(glyph) };
            parent.Items.Add(it);
            StyleMenu((ToolStripDropDownMenu)it.DropDown);
            return it;
        }

        public static ToolStripMenuItem MenuItem(ToolStripDropDownMenu m, string text, string glyph, Action click)
        {
            // The icon is drawn by ModernMenuRenderer straight from the icon font (centred on the row); the blank image
            // only reserves the icon column so every label starts at the same x.
            var it = new ToolStripMenuItem(text) { Image = BlankIcon, Tag = new MenuGlyph(glyph) };
            if (click != null) it.Click += delegate { click(); };
            m.Items.Add(it);
            return it;
        }

        static Bitmap blankIcon;
        static Bitmap BlankIcon
        {
            get { return blankIcon ?? (blankIcon = new Bitmap(S(16), S(16), System.Drawing.Imaging.PixelFormat.Format32bppArgb)); }
        }

        static readonly Dictionary<string, Bitmap> iconBitmaps = new Dictionary<string, Bitmap>();

        /// <summary>Renders an icon glyph into a small bitmap (for menu items), in the current text colour.</summary>
        public static Bitmap IconBitmap(string glyph, int size)
        {
            if (string.IsNullOrEmpty(glyph)) return null;
            string key = glyph + "|" + size + "|" + Text.ToArgb();
            Bitmap bmp;
            if (iconBitmaps.TryGetValue(key, out bmp)) return bmp;
            int px = S(size);
            bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(Text))
                    g.DrawString(glyph, GlyphFor(glyph, size * 0.62f), b, new RectangleF(0, 0, px, px), sf);
            }
            iconBitmaps[key] = bmp;
            return bmp;
        }

        public static string Time(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds)) return "0:00";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
                                      : string.Format("{0}:{1:00}", (int)ts.TotalMinutes, ts.Seconds);
        }
    }

    /// <summary>Flat, theme-coloured menu renderer: rounded hover pill, thin separators, accent check marks, no gradients.</summary>
    internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        public ModernMenuRenderer() : base(new ModernMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Theme.Surface); }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(Theme.S(2), 0, e.Item.Width - Theme.S(4), e.Item.Height);
            Theme.FillRound(e.Graphics, Theme.Alpha(Theme.Overlay, Theme.IsDark ? 22 : 14), r, Theme.S(6f));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Theme.S(4), y, e.Item.Width - Theme.S(4), y);
        }

        // Icons, check marks and labels are drawn here, all vertically centred on the row. (WinForms' own layout
        // top-aligns the label using the font's tall line metrics and the icon bitmaps used different metrics,
        // so they sat at different heights.)

        static Rectangle Column(ToolStripItem item, Rectangle imageRect)
        {
            return new Rectangle(imageRect.X - Theme.S(4), 0, imageRect.Width + Theme.S(8), item.Height);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Item.Tag as MenuGlyph;
            if (g == null) { base.OnRenderItemImage(e); return; }
            Theme.GlyphCenter(e.Graphics, g.Glyph, 11f, e.Item.Enabled ? Theme.Text : Theme.TextDim, Column(e.Item, e.ImageRectangle));
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Theme.GlyphCenter(e.Graphics, Glyphs.Check, 11f, Theme.Accent, Column(e.Item, e.ImageRectangle));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!(e.Item is ToolStripMenuItem)) { e.TextColor = Theme.Text; base.OnRenderItemText(e); return; }
            int x = e.TextRectangle.X;
            var r = new Rectangle(x, 0, Math.Max(1, e.Item.Width - x - Theme.S(6)), e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, e.Item.Enabled ? Theme.Text : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.TextDim; base.OnRenderArrow(e); }
    }

    /// <summary>Icon-font glyph attached to a menu item (drawn by <see cref="ModernMenuRenderer"/>).</summary>
    internal sealed class MenuGlyph
    {
        public readonly string Glyph;
        public MenuGlyph(string glyph) { Glyph = glyph; }
    }

    internal sealed class ModernMenuColors : ProfessionalColorTable
    {
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
    }

    // ======================================================================= painted widgets

    internal enum BtnStyle { Ghost, Soft, Circle, Accent, Pill, AccentPill, Window, WindowClose, Solid }

    internal sealed class CanvasButton
    {
        public Rectangle Bounds;
        public string Glyph;
        public string Label;
        public float GlyphSize = 12f;
        public BtnStyle Style = BtnStyle.Soft;
        public bool Visible = true, Enabled = true, Active;
        public string Tip;
        public Action Click;
        internal bool Hover, Pressed;
    }

    internal sealed class CanvasSlider
    {
        public Rectangle Bounds;
        public double Value;
        public bool Visible = true;
        /// <summary>Show the knob only while hovered or dragged (progress bars): otherwise the knob would hide the first seconds of progress.</summary>
        public bool KnobOnHover;
        public Action<double> Changing, Committed;
        internal bool Dragging, Hover;
    }

    /// <summary>
    /// Base for all custom-painted controls: double-buffered, hosts painted buttons/sliders with hover and press states,
    /// and lets the borderless main window keep its resize edges (returns HTTRANSPARENT near the window border).
    /// </summary>
    internal class CanvasControl : Control, IAnimated
    {
        /// <summary>Hover fades for buttons, sliders and anything a subclass keys into it (rows, tiles, nav items…).</summary>
        protected readonly Fader Fade = new Fader();
        protected readonly List<CanvasButton> Buttons = new List<CanvasButton>();
        protected readonly List<CanvasSlider> Sliders = new List<CanvasSlider>();
        CanvasButton hoverBtn, pressBtn;
        CanvasSlider dragSlider, hoverSlider;
        readonly ToolTip tip = new ToolTip { InitialDelay = 500, ReshowDelay = 200 };

        public CanvasControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
        }

        /// <summary>Paints the control's background: the blurred backdrop on frosted glass, otherwise the flat colour.</summary>
        protected void PaintBackground(Graphics g)
        {
            if (Theme.Glass && Theme.BackgroundPainter != null && Theme.BackgroundPainter(g, this)) return;
            g.Clear(BackColor);
        }

        protected CanvasButton AddButton(string glyph, BtnStyle style, float glyphSize, string tip, Action click)
        {
            var b = new CanvasButton { Glyph = glyph, Style = style, GlyphSize = glyphSize, Tip = tip, Click = click };
            Buttons.Add(b);
            return b;
        }

        protected CanvasSlider AddSlider(Action<double> changing, Action<double> committed)
        {
            var s = new CanvasSlider { Changing = changing, Committed = committed };
            Sliders.Add(s);
            return s;
        }

        protected bool IsDraggingSlider { get { return dragSlider != null; } }

        protected void SetTip(string text) { tip.SetToolTip(this, text); }

        /// <summary>Ask the frame clock for frames until Step returns false.</summary>
        protected void Animate() { AnimationClock.Request(this); }

        /// <summary>Fade one key in and the previous one out.</summary>
        protected void FadeHover(object oldKey, object newKey)
        {
            bool a = Fade.Set(oldKey, 0f), b = Fade.Set(newKey, 1f);
            if (a || b) Animate();
        }

        readonly List<object> movedKeys = new List<object>();

        bool IAnimated.Step(double dt)
        {
            if (IsDisposed) return false;
            movedKeys.Clear();
            bool faded = Fade.Step(dt, movedKeys);
            // Repaint only what is fading (a button, a row, a tile) — not the whole control.
            foreach (object k in movedKeys)
            {
                Rectangle r = FadeBounds(k);
                if (r.IsEmpty) { Invalidate(); break; }
                Invalidate(r);
            }
            bool more = OnAnimate(dt);
            return faded || more;
        }

        /// <summary>Area to repaint while a fade key animates (empty = whole control).</summary>
        protected virtual Rectangle FadeBounds(object key)
        {
            var b = key as CanvasButton;
            if (b != null) return Rectangle.Inflate(b.Bounds, 2, 2);
            var s = key as CanvasSlider;
            if (s != null) return Rectangle.Inflate(s.Bounds, Theme.S(4), Theme.S(8));
            return Rectangle.Empty;
        }

        /// <summary>Per-frame hook for subclasses (smooth scrolling, transitions). Return true while still animating.</summary>
        protected virtual bool OnAnimate(double dt) { return false; }

        protected void PaintWidgets(Graphics g)
        {
            foreach (CanvasSlider s in Sliders) if (s.Visible) DrawSlider(g, s, Fade.Get(s));
            foreach (CanvasButton b in Buttons) if (b.Visible) DrawButton(g, b, Fade.Get(b));
        }

        public static void DrawButton(Graphics g, CanvasButton b) { DrawButton(g, b, b.Hover ? 1f : 0f); }

        /// <summary>Draws a button; <paramref name="hover"/> (0–1) blends between the normal and hover look so hover fades smoothly.</summary>
        public static void DrawButton(Graphics g, CanvasButton b, float hover)
        {
            Rectangle r = b.Bounds;
            float h = b.Enabled ? hover : 0f;
            bool pressed = b.Enabled && b.Pressed;
            Func<int, int, int, int> A = delegate(int normal, int hovered, int down) { return pressed ? down : (int)Math.Round(normal + (hovered - normal) * h); };
            Color fill = Color.Empty;
            float radius = Theme.S(8f);
            switch (b.Style)
            {
                case BtnStyle.Ghost: fill = Theme.Alpha(Theme.Overlay, A(0, 22, 36)); break;
                case BtnStyle.Soft: fill = Theme.Alpha(Theme.Overlay, A(14, 30, 44)); break;
                case BtnStyle.Circle: fill = Theme.Alpha(Theme.Overlay, A(34, 52, 70)); radius = r.Height / 2f; break;
                case BtnStyle.Accent: fill = Theme.Alpha(Theme.Accent, A(220, 245, 200)); radius = r.Height / 2f; break;
                case BtnStyle.Pill: fill = Theme.Alpha(Theme.Overlay, A(20, 36, 50)); radius = r.Height / 2f; break;
                case BtnStyle.AccentPill: fill = Theme.Alpha(Theme.Accent, A(225, 250, 200)); radius = r.Height / 2f; break;
                case BtnStyle.Window: fill = Theme.Alpha(Theme.Overlay, A(0, 24, 36)); radius = Theme.S(6f); break;
                case BtnStyle.WindowClose: fill = Color.FromArgb(A(0, 230, 200), 196, 43, 28); radius = Theme.S(6f); break;
                // Solid: a filled circle in the text colour (white on dark, near-black on light) with the glyph cut out in the background colour.
                case BtnStyle.Solid: fill = Theme.Alpha(Theme.Text, A(238, 255, 215)); radius = r.Height / 2f; break;
            }
            if (b.Active && (b.Style == BtnStyle.Ghost || b.Style == BtnStyle.Soft)) fill = Theme.Alpha(Theme.Accent, A(50, 80, 80));
            if (fill.A > 0) Theme.FillRound(g, fill, r, radius);

            Color fg = !b.Enabled ? Theme.Alpha(Theme.TextDim, 120) : b.Active ? Theme.Accent : Theme.Text;
            if (b.Style == BtnStyle.Accent || b.Style == BtnStyle.AccentPill) fg = Color.White;
            if (b.Style == BtnStyle.Solid) fg = Theme.IsDark ? Color.FromArgb(24, 24, 26) : Color.White;

            if (string.IsNullOrEmpty(b.Label))
            {
                Theme.GlyphCenter(g, b.Glyph, b.GlyphSize, fg, r);
            }
            else
            {
                Font f = Theme.UiSemibold(9.5f);
                Size ts = TextRenderer.MeasureText(g, b.Label, f, Size.Empty, TextFormatFlags.NoPadding);
                int gw = string.IsNullOrEmpty(b.Glyph) ? 0 : Theme.S(16) + Theme.S(8);
                int x = r.X + (r.Width - ts.Width - gw) / 2;
                if (gw > 0) Theme.GlyphCenter(g, b.Glyph, b.GlyphSize, fg, new Rectangle(x, r.Y, Theme.S(16), r.Height));
                Theme.TextLeft(g, b.Label, f, fg, new Rectangle(x + gw, r.Y, ts.Width + 2, r.Height));
            }
        }

        public static void DrawSlider(Graphics g, CanvasSlider s) { DrawSlider(g, s, s.Hover ? 1f : 0f); }

        public static void DrawSlider(Graphics g, CanvasSlider s, float hover)
        {
            Rectangle r = s.Bounds;
            float h = Theme.S(4f);
            float y = r.Y + (r.Height - h) / 2f;
            float x1 = r.X + Theme.S(6f), x2 = r.Right - Theme.S(6f);
            float xv = x1 + (float)(Math.Max(0, Math.Min(1, s.Value)) * (x2 - x1));
            Theme.FillRound(g, Theme.Alpha(Theme.Overlay, 50), new RectangleF(x1, y, x2 - x1, h), h / 2);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Fill drawn with sub-pixel precision (anti-aliased), so a long song's progress visibly moves from the first second.
            if (xv - x1 > 0.3f)
                Theme.FillRound(g, Theme.IsDark ? Theme.Alpha(Color.White, 235) : Theme.Accent, new RectangleF(x1, y, xv - x1, h), Math.Min(h / 2, (xv - x1) / 2));
            float show = s.KnobOnHover ? (s.Dragging ? 1f : hover) : 1f;
            float kr = Theme.S(6.5f + 1.5f * (s.Dragging ? 1f : hover)) * (s.KnobOnHover ? 0.4f + 0.6f * show : 1f);   // grows smoothly on hover
            if (show > 0.02f)
                using (var b = new SolidBrush(Theme.Alpha(Theme.IsDark ? Color.White : Theme.Accent, (int)(255 * Math.Min(1f, show * 1.5f)))))
                    g.FillEllipse(b, xv - kr, r.Y + r.Height / 2f - kr, kr * 2, kr * 2);
            g.SmoothingMode = old;
        }

        static double SliderValueAt(CanvasSlider s, int x)
        {
            float x1 = s.Bounds.X + Theme.S(6f), x2 = s.Bounds.Right - Theme.S(6f);
            return Math.Max(0, Math.Min(1, (x - x1) / Math.Max(1f, x2 - x1)));
        }

        CanvasButton HitButton(Point p)
        {
            for (int i = Buttons.Count - 1; i >= 0; i--)
                if (Buttons[i].Visible && Buttons[i].Enabled && Buttons[i].Bounds.Contains(p)) return Buttons[i];
            return null;
        }

        CanvasSlider HitSlider(Point p)
        {
            foreach (CanvasSlider s in Sliders)
                if (s.Visible && Rectangle.Inflate(s.Bounds, 0, Theme.S(6)).Contains(p)) return s;
            return null;
        }

        protected bool IsOverWidget(Point p) { return HitButton(p) != null || HitSlider(p) != null; }

        protected virtual void OnCanvasMouseDown(MouseEventArgs e) { }
        protected virtual void OnCanvasMouseMove(MouseEventArgs e) { }
        protected virtual void OnCanvasMouseUp(MouseEventArgs e) { }
        protected virtual void OnCanvasDoubleClick(MouseEventArgs e) { }
        protected virtual Cursor CursorAt(Point p) { return Cursors.Default; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                CanvasSlider s = HitSlider(e.Location);
                if (s != null)
                {
                    dragSlider = s; s.Dragging = true; Capture = true;
                    s.Value = SliderValueAt(s, e.X);
                    if (s.Changing != null) s.Changing(s.Value);
                    Invalidate();
                    return;
                }
                CanvasButton b = HitButton(e.Location);
                if (b != null) { pressBtn = b; b.Pressed = true; Invalidate(b.Bounds); return; }
            }
            OnCanvasMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragSlider != null)
            {
                dragSlider.Value = SliderValueAt(dragSlider, e.X);
                if (dragSlider.Changing != null) dragSlider.Changing(dragSlider.Value);
                Invalidate();
                return;
            }
            CanvasButton b = HitButton(e.Location);
            if (b != hoverBtn)
            {
                if (hoverBtn != null) hoverBtn.Hover = false;
                FadeHover(hoverBtn, b);
                hoverBtn = b;
                if (b != null) b.Hover = true;
                tip.SetToolTip(this, b != null ? b.Tip : null);
            }
            CanvasSlider s = HitSlider(e.Location);
            if (s != hoverSlider)
            {
                if (hoverSlider != null) hoverSlider.Hover = false;
                FadeHover(hoverSlider, s);
                hoverSlider = s;
                if (s != null) s.Hover = true;
            }
            Cursor = b != null || s != null ? Cursors.Hand : CursorAt(e.Location);
            OnCanvasMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragSlider != null)
            {
                CanvasSlider s = dragSlider;
                dragSlider = null; s.Dragging = false; Capture = false;
                s.Value = SliderValueAt(s, e.X);
                if (s.Committed != null) s.Committed(s.Value);
                Invalidate();
                return;
            }
            if (pressBtn != null)
            {
                CanvasButton b = pressBtn;
                pressBtn = null; b.Pressed = false; Invalidate(b.Bounds);
                if (b.Bounds.Contains(e.Location) && b.Enabled && b.Click != null) b.Click();
                return;
            }
            OnCanvasMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!IsOverWidget(e.Location)) OnCanvasDoubleClick(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverBtn != null) { hoverBtn.Hover = false; FadeHover(hoverBtn, null); hoverBtn = null; }
            if (hoverSlider != null) { hoverSlider.Hover = false; FadeHover(hoverSlider, null); hoverSlider = null; }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            CanvasSlider s = HitSlider(e.Location);
            if (s != null && s.Committed != null)
            {
                s.Value = Math.Max(0, Math.Min(1, s.Value + (e.Delta > 0 ? 0.02 : -0.02)));
                if (s.Changing != null) s.Changing(s.Value);
                s.Committed(s.Value);
                Invalidate();
                return;
            }
            base.OnMouseWheel(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x000F && Perf.Enabled)   // WM_PAINT: time it when BPP_PERF=1
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                base.WndProc(ref m);
                Perf.Record(GetType().Name, t0);
                return;
            }
            if (!EdgeHit.PassThrough(this, ref m)) base.WndProc(ref m);
        }
    }

    /// <summary>Optional paint-time log (set BPP_PERF=1): per control, count / average / 95th percentile / max, written every 2 s.</summary>
    internal static class Perf
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("BPP_PERF") == "1";
        static readonly Dictionary<string, List<double>> samples = new Dictionary<string, List<double>>();
        static long lastFlush = System.Diagnostics.Stopwatch.GetTimestamp();

        public static void Record(string name, long startTimestamp)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            List<double> l;
            if (!samples.TryGetValue(name, out l)) samples[name] = l = new List<double>();
            l.Add(ms);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if ((now - lastFlush) * 1.0 / System.Diagnostics.Stopwatch.Frequency < 2) return;
            lastFlush = now;
            var sb = new System.Text.StringBuilder(DateTime.Now.ToString("HH:mm:ss") + "\r\n");
            foreach (var kv in samples)
            {
                if (kv.Value.Count == 0) continue;
                kv.Value.Sort();
                double sum = 0; foreach (double d in kv.Value) sum += d;
                sb.AppendFormat("  {0,-16} n={1,4}  avg={2,6:0.0} ms  p95={3,6:0.0}  max={4,6:0.0}\r\n", kv.Key, kv.Value.Count, sum / kv.Value.Count,
                                kv.Value[(int)(kv.Value.Count * 0.95)], kv.Value[kv.Value.Count - 1]);
                kv.Value.Clear();
            }
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Library.DataDir, "perf.log"), sb.ToString()); } catch { }
        }
    }

    /// <summary>Child windows near the main window's border answer HTTRANSPARENT so the border stays resizable.</summary>
    internal static class EdgeHit
    {
        public static int Border { get { return Theme.S(6); } }

        public static bool PassThrough(Control c, ref Message m)
        {
            const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
            if (m.Msg != WM_NCHITTEST) return false;
            Form f = c.FindForm();
            if (f == null || f.WindowState != FormWindowState.Normal) return false;
            long lp = m.LParam.ToInt64();
            Point fp = f.PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int b = Border;
            if (fp.X < b || fp.Y < b || fp.X >= f.ClientSize.Width - b || fp.Y >= f.ClientSize.Height - b)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return true;
            }
            return false;
        }
    }

    internal class HitPanel : Panel
    {
        public HitPanel() { DoubleBuffered = true; BackColor = Theme.Bg; }
        protected override void WndProc(ref Message m)
        {
            if (!EdgeHit.PassThrough(this, ref m)) base.WndProc(ref m);
        }
    }

    /// <summary>A canvas with vertical scrolling and a thin overlay scrollbar.</summary>
    internal class ScrollCanvas : CanvasControl
    {
        double pos, target;     // smooth scrolling: pos eases toward target every display frame
        bool thumbDrag, thumbHover;
        int thumbGrabOffset;
        Point lastMouse = new Point(-1, -1);

        protected virtual int ContentHeight { get { return 0; } }

        /// <summary>Current scroll offset. Setting it jumps immediately (keyboard, scrollbar drag, new content).</summary>
        public int ScrollY
        {
            get { return (int)Math.Round(pos); }
            set
            {
                double v = Math.Max(0, Math.Min(MaxScroll, value));
                target = v;
                if (v != pos) { pos = v; Invalidate(); OnScrolled(); }
            }
        }

        /// <summary>Scroll with easing (mouse wheel, touchpad, page keys).</summary>
        public void ScrollSmooth(double to)
        {
            target = Math.Max(0, Math.Min(MaxScroll, to));
            if (target != pos) Animate();
        }

        protected virtual void OnScrolled() { }
        int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

        public void ClampScroll()
        {
            double max = MaxScroll;
            target = Math.Max(0, Math.Min(max, target));
            double p = Math.Max(0, Math.Min(max, pos));
            if (p != pos) { pos = p; Invalidate(); OnScrolled(); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int ScrollWindowEx(IntPtr hWnd, int dx, int dy, IntPtr prcScroll, IntPtr prcClip, IntPtr hrgnUpdate, IntPtr prcUpdate, uint flags);

        protected override bool OnAnimate(double dt)
        {
            bool more = base.OnAnimate(dt);
            if (pos == target) return more;
            int before = ScrollY;
            // Exponential ease: frame-rate independent, settles in ~0.25 s, never overshoots.
            pos += (target - pos) * (1 - Math.Exp(-dt * 16));
            if (Math.Abs(target - pos) < 0.4) pos = target;
            int dy = ScrollY - before;
            if (dy != 0) Invalidate();

            OnScrolled();
            // Keep hover on whatever is under the (still) mouse while content slides past.
            if (lastMouse.X >= 0 && ClientRectangle.Contains(lastMouse))
                OnContentMouseMove(new MouseEventArgs(MouseButtons.None, 0, lastMouse.X, lastMouse.Y, 0));
            return true;
        }

        public void EnsureVisible(int top, int height)
        {
            if (top < target) ScrollSmooth(top);
            else if (top + height > target + Height) ScrollSmooth(top + height - Height);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); ClampScroll(); }

        Rectangle ThumbRect()
        {
            int ch = ContentHeight;
            if (ch <= Height || Height <= 0) return Rectangle.Empty;
            int w = Theme.S(thumbHover || thumbDrag ? 8 : 5);
            int h = Math.Max(Theme.S(36), Height * Height / ch);
            int y = (int)((long)ScrollY * (Height - h) / Math.Max(1, MaxScroll));
            return new Rectangle(Width - w - Theme.S(3), y, w, h);
        }

        protected void PaintScrollbar(Graphics g)
        {
            Rectangle t = ThumbRect();
            if (t.IsEmpty) return;
            Theme.FillRound(g, Theme.Alpha(Theme.Overlay, thumbDrag ? 110 : thumbHover ? 80 : 45), t, t.Width / 2f);
        }

        bool InScrollbarZone(Point p) { return !ThumbRect().IsEmpty && p.X >= Width - Theme.S(14); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // ~130 px per wheel notch; high-resolution wheels / touchpads send smaller deltas and scroll proportionally.
            ScrollSmooth(target - e.Delta * Theme.S(130f) / 120.0);
        }

        protected override void OnCanvasMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && InScrollbarZone(e.Location))
            {
                Rectangle t = ThumbRect();
                if (!t.Contains(new Point(t.X, e.Y)))
                    ScrollY = (int)((long)(e.Y - t.Height / 2) * MaxScroll / Math.Max(1, Height - t.Height));
                t = ThumbRect();
                thumbDrag = true; thumbGrabOffset = e.Y - t.Y; Capture = true;
                Invalidate();
                return;
            }
            OnContentMouseDown(e);
        }

        protected override void OnCanvasMouseMove(MouseEventArgs e)
        {
            if (thumbDrag)
            {
                Rectangle t = ThumbRect();
                ScrollY = (int)((long)(e.Y - thumbGrabOffset) * MaxScroll / Math.Max(1, Height - t.Height));
                return;
            }
            lastMouse = e.Location;
            bool hov = InScrollbarZone(e.Location);
            if (hov != thumbHover) { thumbHover = hov; Invalidate(); }
            OnContentMouseMove(e);
        }

        protected override void OnCanvasMouseUp(MouseEventArgs e)
        {
            if (thumbDrag) { thumbDrag = false; Capture = false; Invalidate(); return; }
            OnContentMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            thumbHover = false;
            lastMouse = new Point(-1, -1);
            base.OnMouseLeave(e);
            OnContentMouseLeave();
        }

        protected virtual void OnContentMouseDown(MouseEventArgs e) { }
        protected virtual void OnContentMouseMove(MouseEventArgs e) { }
        protected virtual void OnContentMouseUp(MouseEventArgs e) { }
        protected virtual void OnContentMouseLeave() { }
    }

    /// <summary>Simple standalone slider (used on the Settings page).</summary>
    internal sealed class SliderBar : CanvasControl
    {
        readonly CanvasSlider slider;
        public event EventHandler ValueChanged;

        public SliderBar()
        {
            slider = AddSlider(delegate { if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); },
                               delegate { if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); });
        }

        public double Value { get { return slider.Value; } set { slider.Value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            slider.Bounds = ClientRectangle;
            PaintWidgets(e.Graphics);
        }
    }
}

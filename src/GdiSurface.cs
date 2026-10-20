using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BitPerfectPlayer
{
    /// <summary>
    /// A bitmap held as a native GDI bitmap selected into a memory DC, so it can be copied with BitBlt or faded with AlphaBlend.
    /// Both are far cheaper than GDI+ DrawImage (especially GDI+'s ColorMatrix alpha) for big, unscaled layers such as the
    /// Now Playing backdrop and artwork.
    /// </summary>
    internal sealed class GdiSurface : IDisposable
    {
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int blendFunction);

        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        IntPtr hbm, hdc, old;
        public readonly int Width, Height;
        /// <summary>Pixel memory (top-down, premultiplied BGRA, stride Width × 4) for surfaces made with the (w, h) constructor.</summary>
        public readonly IntPtr Bits;

        /// <summary>An empty (transparent) surface whose pixels are written directly through <see cref="Bits"/> (video frames).</summary>
        public GdiSurface(int w, int h)
        {
            Width = w; Height = h;
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            hbm = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out Bits, IntPtr.Zero, 0);
            if (hbm == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection failed");
            hdc = CreateCompatibleDC(IntPtr.Zero);
            old = SelectObject(hdc, hbm);
        }

        /// <summary>Copies <paramref name="b"/> (32bpp; premultiplied alpha is kept for AlphaBlend).</summary>
        public GdiSurface(Bitmap b)
        {
            Width = b.Width; Height = b.Height;
            hbm = b.GetHbitmap(Color.FromArgb(0));   // transparent background = premultiplied ARGB DIB
            hdc = CreateCompatibleDC(IntPtr.Zero);
            old = SelectObject(hdc, hbm);
        }

        /// <summary>
        /// Draws src rect (same size as dst, no scaling) into g. alpha 255 + opaque = BitBlt; otherwise AlphaBlend with a
        /// constant alpha, optionally honouring the per-pixel alpha (for rounded artwork).
        /// </summary>
        public void Draw(Graphics g, Rectangle dst, Point src, byte alpha, bool perPixelAlpha)
        {
            if (hdc == IntPtr.Zero || alpha == 0) return;
            // Keep the source rectangle inside the bitmap (AlphaBlend fails otherwise).
            if (src.X < 0) { dst.X -= src.X; dst.Width += src.X; src.X = 0; }
            if (src.Y < 0) { dst.Y -= src.Y; dst.Height += src.Y; src.Y = 0; }
            dst.Width = Math.Min(dst.Width, Width - src.X);
            dst.Height = Math.Min(dst.Height, Height - src.Y);
            if (dst.Width <= 0 || dst.Height <= 0) return;

            // A WinForms double buffer may carry a translation; GetHdc does not apply it, so add it by hand.
            int ox = 0, oy = 0;
            using (Matrix m = g.Transform)
            {
                float[] e = m.Elements;
                ox = (int)Math.Round(e[4]); oy = (int)Math.Round(e[5]);
            }
            IntPtr target = g.GetHdc();
            try
            {
                if (alpha == 255 && !perPixelAlpha)
                    BitBlt(target, dst.X + ox, dst.Y + oy, dst.Width, dst.Height, hdc, src.X, src.Y, 0x00CC0020 /* SRCCOPY */);
                else
                    AlphaBlend(target, dst.X + ox, dst.Y + oy, dst.Width, dst.Height, hdc, src.X, src.Y, dst.Width, dst.Height,
                               (alpha << 16) | ((perPixelAlpha ? 1 : 0) << 24));   // AC_SRC_OVER, constant alpha, AC_SRC_ALPHA
            }
            finally { g.ReleaseHdc(target); }
        }

        /// <summary>
        /// Draws <paramref name="src"/> scaled to <paramref name="dst"/> with a constant alpha (AlphaBlend stretches with
        /// nearest-neighbour sampling, so this is meant for flat or soft layers, e.g. a 1×1 colour as a translucent panel).
        /// </summary>
        public void DrawStretched(Graphics g, Rectangle dst, Rectangle src, byte alpha)
        {
            if (hdc == IntPtr.Zero || alpha == 0 || dst.Width <= 0 || dst.Height <= 0) return;
            src.Intersect(new Rectangle(0, 0, Width, Height));
            if (src.Width <= 0 || src.Height <= 0) return;
            int ox = 0, oy = 0;
            using (Matrix m = g.Transform)
            {
                float[] e = m.Elements;
                ox = (int)Math.Round(e[4]); oy = (int)Math.Round(e[5]);
            }
            IntPtr target = g.GetHdc();
            try { AlphaBlend(target, dst.X + ox, dst.Y + oy, dst.Width, dst.Height, hdc, src.X, src.Y, src.Width, src.Height, alpha << 16); }
            finally { g.ReleaseHdc(target); }
        }

        public void Dispose()
        {
            if (hdc != IntPtr.Zero) { SelectObject(hdc, old); DeleteDC(hdc); hdc = IntPtr.Zero; }
            if (hbm != IntPtr.Zero) { DeleteObject(hbm); hbm = IntPtr.Zero; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    /// <summary>Rows of folders (icon, name, song count, chevron) for the Folders page. Click opens; hover shows a play button.</summary>
    internal sealed class FolderListView : ScrollCanvas
    {
        List<FolderNode> items = new List<FolderNode>();
        int hover = -1;
        bool hoverPlay;
        public event Action<FolderNode> Opened, PlayRequested;
        public event Action<FolderNode, Point> ContextRequested;

        public int RowH { get { return Theme.S(56); } }
        int TopPad { get { return Theme.S(4); } }
        protected override int ContentHeight { get { return TopPad + items.Count * RowH + Theme.S(8); } }
        public int Count { get { return items.Count; } }

        public void SetItems(List<FolderNode> list, bool keepScroll)
        {
            items = list ?? new List<FolderNode>();
            hover = -1;
            if (!keepScroll) ScrollY = 0;
            ClampScroll();
            Invalidate();
        }

        Rectangle RowRect(int i) { return new Rectangle(Theme.S(20), TopPad + i * RowH - ScrollY + Theme.S(2), Width - Theme.S(40), RowH - Theme.S(4)); }
        Rectangle PlayRect(Rectangle row) { int s = Theme.S(34); return new Rectangle(row.Right - Theme.S(48) - s, row.Y + (row.Height - s) / 2, s, s); }

        int IndexAt(Point p)
        {
            int i = (p.Y + ScrollY - TopPad) / RowH;
            return p.Y + ScrollY - TopPad >= 0 && i < items.Count && RowRect(i).Contains(p) ? i : -1;
        }

        protected override Rectangle FadeBounds(object key)
        {
            var n = key as FolderNode;
            if (n == null) return base.FadeBounds(key);
            int i = items.IndexOf(n);
            return i < 0 ? Rectangle.Empty : Rectangle.Inflate(RowRect(i), 1, 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBackground(g);
            var ell = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (r.Bottom < e.ClipRectangle.Top || r.Top > e.ClipRectangle.Bottom) continue;
                FolderNode n = items[i];
                float hv = Fade.Get(n);
                if (hv > 0.01f) Theme.FillRound(g, Theme.Alpha(Theme.Overlay, (int)(14 * hv)), r, Theme.S(8f));
                var icon = new Rectangle(r.X + Theme.S(8), r.Y + (r.Height - Theme.S(38)) / 2, Theme.S(38), Theme.S(38));
                Theme.FillRound(g, Theme.Alpha(Theme.Accent, 38), icon, Theme.S(8f));
                Theme.GlyphCenter(g, Glyphs.FolderPlain, 14f, Theme.Accent, icon);
                int tx = icon.Right + Theme.S(14);
                int right = r.Right - Theme.S(100);
                string meta = n.Children.Count > 0
                    ? Plural(n.Children.Count, "folder") + "  ·  " + Plural(n.TotalSongs, "song")
                    : Plural(n.TotalSongs, "song");
                if (n.IsRoot) meta = n.Path + "  ·  " + meta;
                Theme.DrawText(g, n.Name, Theme.UiMedium(10.5f), Theme.Text, new Rectangle(tx, r.Y + r.Height / 2 - Theme.S(20), right - tx, Theme.S(20)), ell);
                Theme.DrawText(g, meta, Theme.Ui(8.5f), Theme.TextDim, new Rectangle(tx, r.Y + r.Height / 2 + Theme.S(1), right - tx, Theme.S(18)), ell);
                if (hv > 0.01f)
                {
                    Rectangle pr = PlayRect(r);
                    Theme.FillRound(g, Theme.Alpha(Theme.Accent, (int)((i == hover && hoverPlay ? 255 : 215) * hv)), pr, pr.Height / 2f);
                    Theme.GlyphCenter(g, Glyphs.Play, 11f, Theme.Alpha(Color.White, (int)(255 * hv)), pr);
                }
                Theme.GlyphCenter(g, Glyphs.ChevronRight, 11f, Theme.TextDim, new Rectangle(r.Right - Theme.S(40), r.Y, Theme.S(30), r.Height));
            }
            PaintScrollbar(g);
        }

        static string Plural(int n, string w) { return n + " " + w + (n == 1 ? "" : "s"); }

        protected override Cursor CursorAt(Point p) { return IndexAt(p) >= 0 ? Cursors.Hand : Cursors.Default; }

        protected override void OnContentMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            bool onPlay = i >= 0 && PlayRect(RowRect(i)).Contains(e.Location);
            if (i != hover) { FadeHover(hover >= 0 && hover < items.Count ? items[hover] : null, i >= 0 ? items[i] : null); hover = i; }
            if (onPlay != hoverPlay && i >= 0) { hoverPlay = onPlay; Invalidate(RowRect(i)); }
        }

        protected override void OnContentMouseLeave()
        {
            if (hover >= 0 && hover < items.Count) FadeHover(items[hover], null);
            hover = -1;
        }

        protected override void OnContentMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i < 0) return;
            FolderNode n = items[i];
            if (e.Button == MouseButtons.Right) { if (ContextRequested != null) ContextRequested(n, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            if (PlayRect(RowRect(i)).Contains(e.Location)) { if (PlayRequested != null) PlayRequested(n); }
            else if (Opened != null) Opened(n);
        }
    }
}

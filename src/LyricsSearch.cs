using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    /// <summary>
    /// "Search lyrics": look a song up on LRCLIB by hand (any title / artist spelling), preview the results, then
    /// use the chosen lyrics in SoDa Player, save them as an .lrc file next to the song, or embed them in the file's tags.
    /// </summary>
    internal sealed class LyricsSearchForm : Form
    {
        readonly TrackInfo track;
        readonly TextBox titleBox, artistBox, preview;
        readonly Button searchBtn, useBtn, lrcBtn, embedBtn, closeBtn;
        readonly CheckBox syncedOnly;
        readonly Label status;
        readonly ListBox list;
        List<LyricsCandidate> all = new List<LyricsCandidate>();
        int searchId;
        bool busy;

        /// <summary>What happened (shown in the main window's status line after the dialog closes), or null.</summary>
        public string ResultMessage;

        public LyricsSearchForm(TrackInfo t)
        {
            track = t;
            DialogKit.Style(this, true);
            Text = "Search lyrics — " + t.DisplayTitle;
            ClientSize = new Size(Theme.S(900), Theme.S(620));
            MinimumSize = new Size(Theme.S(640), Theme.S(440));

            titleBox = DialogKit.MakeTextBox(); titleBox.Text = t.Title ?? Path.GetFileNameWithoutExtension(t.FilePath);
            DialogKit.SetCue(titleBox, "Song title");
            artistBox = DialogKit.MakeTextBox(); artistBox.Text = t.Artist ?? "";
            DialogKit.SetCue(artistBox, "Artist (optional)");
            searchBtn = DialogKit.MakeButton("Search", false);
            syncedOnly = new CheckBox { Text = "Synced only", Checked = true, AutoSize = true, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
            status = new Label { AutoSize = false, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft };

            list = new ListBox
            {
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.S(52), BorderStyle = BorderStyle.None,
                BackColor = Theme.Mix(Theme.Bg, Theme.Overlay, 0.04), ForeColor = Theme.Text, IntegralHeight = false
            };
            preview = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, WordWrap = true,
                BackColor = Theme.Mix(Theme.Bg, Theme.Overlay, 0.04), ForeColor = Theme.Text, Font = Theme.Ui(10f)
            };

            useBtn = DialogKit.MakeButton("Use in SoDa Player", true);
            lrcBtn = DialogKit.MakeButton("Save .lrc file", false);
            embedBtn = DialogKit.MakeButton("Embed in file", false);
            closeBtn = DialogKit.MakeButton("Close", false);
            closeBtn.DialogResult = DialogResult.Cancel;
            CancelButton = closeBtn;
            AcceptButton = searchBtn;
            var tip = new ToolTip();
            tip.SetToolTip(useBtn, "Show these lyrics for this song in SoDa Player (kept in the lyrics cache)");
            tip.SetToolTip(lrcBtn, "Write " + Path.GetFileNameWithoutExtension(t.FilePath) + ".lrc next to the song — other players read it too");
            tip.SetToolTip(embedBtn, "Write the lyrics into the song file's tags (the audio is copied unchanged)");
            Controls.AddRange(new Control[] { titleBox, artistBox, searchBtn, syncedOnly, status, list, preview, useBtn, lrcBtn, embedBtn, closeBtn });

            list.DrawItem += DrawItem;
            list.SelectedIndexChanged += delegate { ShowPreview(); };
            list.DoubleClick += delegate { Apply(0); };
            searchBtn.Click += delegate { Search(); };
            syncedOnly.CheckedChanged += delegate { Fill(); };
            useBtn.Click += delegate { Apply(0); };
            lrcBtn.Click += delegate { Apply(1); };
            embedBtn.Click += delegate { Apply(2); };
            Resize += delegate { DoLayout(); };
            DoLayout();
            UpdateButtons();
            Shown += delegate { Search(); };
        }

        void DoLayout()
        {
            int pad = Theme.S(18), W = ClientSize.Width, H = ClientSize.Height, rowH = Theme.S(36), gap = Theme.S(10);
            searchBtn.SetBounds(W - pad - searchBtn.Width, pad, searchBtn.Width, rowH);
            int boxesW = searchBtn.Left - gap - pad;
            int tw = (int)(boxesW * 0.55), th = titleBox.PreferredHeight;
            titleBox.SetBounds(pad, pad + (rowH - th) / 2, tw, th);
            artistBox.SetBounds(pad + tw + gap, pad + (rowH - th) / 2, boxesW - tw - gap, th);
            int y2 = pad + rowH + Theme.S(6);
            syncedOnly.Location = new Point(W - pad - syncedOnly.Width, y2 + (Theme.S(26) - syncedOnly.Height) / 2);
            status.SetBounds(pad, y2, syncedOnly.Left - gap - pad, Theme.S(26));
            int bottom = H - pad - rowH;
            closeBtn.SetBounds(W - pad - closeBtn.Width, bottom, closeBtn.Width, rowH);
            embedBtn.SetBounds(closeBtn.Left - gap - embedBtn.Width, bottom, embedBtn.Width, rowH);
            lrcBtn.SetBounds(embedBtn.Left - gap - lrcBtn.Width, bottom, lrcBtn.Width, rowH);
            useBtn.SetBounds(lrcBtn.Left - gap - useBtn.Width, bottom, useBtn.Width, rowH);
            int top = y2 + Theme.S(34), areaH = bottom - Theme.S(14) - top;
            int lw = (int)((W - 2 * pad - gap) * 0.46);
            list.SetBounds(pad, top, lw, areaH);
            preview.SetBounds(pad + lw + gap, top, W - 2 * pad - lw - gap, areaH);
        }

        static string Time(double s) { return s > 0 ? ((int)s / 60) + ":" + ((int)s % 60).ToString("00") : "–:––"; }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            using (var bg = new SolidBrush(list.BackColor)) e.Graphics.FillRectangle(bg, e.Bounds);
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var c = (LyricsCandidate)list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Rectangle r = Rectangle.Inflate(e.Bounds, -Theme.S(4), -Theme.S(2));
            if (sel) Theme.FillRound(e.Graphics, Theme.Alpha(Theme.Accent, 70), r, Theme.S(6f));
            var ell = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            string tag = c.Instrumental ? "INSTR." : c.IsSynced ? "SYNCED" : "PLAIN";
            Font tf = Theme.UiSemibold(7f);
            int tagW = TextRenderer.MeasureText(tag, tf).Width + Theme.S(12), tagH = Theme.S(18);
            var tagRect = new Rectangle(r.Right - Theme.S(8) - tagW, r.Y + Theme.S(6), tagW, tagH);
            Theme.FillRound(e.Graphics, c.IsSynced ? Theme.Alpha(Theme.Good, 200) : Theme.Alpha(Theme.Overlay, 40), tagRect, tagH / 2f);
            TextRenderer.DrawText(e.Graphics, tag, tf, tagRect, c.IsSynced ? Color.White : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Length, green when it matches the song (within 2 s): the best sign that the timing fits this recording.
            bool lengthMatches = track.Duration > 0 && c.Duration > 0 && Math.Abs(c.Duration - track.Duration) <= 2;
            string len = Time(c.Duration);
            var lenRect = new Rectangle(tagRect.X - Theme.S(60), r.Y + Theme.S(6), Theme.S(52), tagH);
            TextRenderer.DrawText(e.Graphics, len, Theme.UiMedium(8.5f), lenRect, lengthMatches ? Theme.Good : Theme.TextDim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int x = r.X + Theme.S(10);
            TextRenderer.DrawText(e.Graphics, c.Title, Theme.UiMedium(10f), new Rectangle(x, r.Y + Theme.S(4), lenRect.X - x - Theme.S(6), Theme.S(22)), Theme.Text, ell);
            TextRenderer.DrawText(e.Graphics, c.Artist + (string.IsNullOrEmpty(c.Album) ? "" : "  ·  " + c.Album), Theme.Ui(8.5f),
                new Rectangle(x, r.Y + Theme.S(26), r.Right - x - Theme.S(8), Theme.S(18)), Theme.TextDim, ell);
        }

        void Search()
        {
            string title = titleBox.Text.Trim(), artist = artistBox.Text.Trim();
            if (title.Length == 0) { status.Text = "Type a song title."; return; }
            int id = ++searchId;
            all = new List<LyricsCandidate>();
            Fill();
            status.Text = "Searching LRCLIB (lrclib.net)…";
            double dur = track.Duration;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<LyricsCandidate> found = null; string error = null;
                try { found = LyricsService.Search(title, artist, dur); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    if (id != searchId) return;
                    if (found == null) { status.Text = "Couldn’t reach LRCLIB — " + error; return; }
                    all = found;
                    Fill();
                });
            });
        }

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            int synced = 0;
            foreach (LyricsCandidate c in all)
            {
                if (c.IsSynced) synced++;
                if (!syncedOnly.Checked || c.IsSynced) list.Items.Add(c);
            }
            list.EndUpdate();
            if (all.Count > 0)
                status.Text = all.Count + " results, " + synced + " synced" + (list.Items.Count == 0 ? " — untick “Synced only” to see plain lyrics" : "  ·  green length = matches this song");
            else if (searchId > 0 && !status.Text.StartsWith("Searching") && !status.Text.StartsWith("Couldn")) status.Text = "No lyrics found — try another spelling, or leave the artist empty.";
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            ShowPreview();
        }

        LyricsCandidate Selected { get { return list.SelectedItem as LyricsCandidate; } }

        void ShowPreview()
        {
            LyricsCandidate c = Selected;
            preview.Text = c == null ? "" : c.Instrumental ? "♪  Instrumental  ♪" : c.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            preview.SelectionStart = 0;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            LyricsCandidate c = Selected;
            bool ok = c != null && !busy && !c.Instrumental && c.Text.Length > 0;
            useBtn.Enabled = lrcBtn.Enabled = embedBtn.Enabled = ok;
            useBtn.BackColor = ok ? Theme.Accent : Theme.Mix(Theme.Bg, Theme.Overlay, 0.10);   // a disabled flat button keeps its colour otherwise
            searchBtn.Enabled = !busy;
            if (ok) AcceptButton = useBtn;
        }

        /// <summary>0 = use in SoDa Player, 1 = save .lrc next to the song, 2 = embed in the file's tags.</summary>
        void Apply(int how)
        {
            LyricsCandidate c = Selected;
            if (c == null || busy || c.Instrumental || c.Text.Length == 0) return;
            string text = c.Text, kind = c.IsSynced ? "synced lyrics" : "lyrics";
            if (how == 0)
            {
                LyricsService.UseForSong(track, text, "LRCLIB");
                ResultMessage = "Using the chosen " + kind + " for “" + track.DisplayTitle + "”.";
                DialogResult = DialogResult.OK;
                return;
            }
            if (how == 1)
            {
                string err = LyricsService.SaveLrcFile(track, text);
                if (err != null) { status.Text = "Couldn’t save the .lrc file — " + err; return; }
                ResultMessage = "Saved " + kind + " as " + Path.GetFileNameWithoutExtension(track.FilePath) + ".lrc next to the song.";
                DialogResult = DialogResult.OK;
                return;
            }
            busy = true;
            UpdateButtons();
            status.Text = "Writing the lyrics into the file…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                try { err = LyricsService.EmbedInFile(track, text); } catch (Exception ex) { err = ex.Message; }
                Post(delegate
                {
                    busy = false;
                    UpdateButtons();
                    if (err != null) { status.Text = "Couldn’t embed the lyrics — " + err + ". Try “Save .lrc file” instead."; return; }
                    ResultMessage = "Embedded " + kind + " in " + Path.GetFileName(track.FilePath) + ".";
                    DialogResult = DialogResult.OK;
                });
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) { e.Cancel = true; return; }   // never leave a half-written file behind
            base.OnFormClosing(e);
        }

        void Post(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }
    }
}

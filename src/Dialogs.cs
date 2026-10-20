using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    /// <summary>Small helpers so standard WinForms dialogs match the app's theme (colours, Inter, dark title bar).</summary>
    internal static class DialogKit
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Style(Form f, bool resizable)
        {
            f.BackColor = Theme.Bg;
            f.ForeColor = Theme.Text;
            f.Font = Theme.Ui(9.5f);
            f.FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ShowInTaskbar = false;
            f.MinimizeBox = false;
            f.MaximizeBox = resizable;
            f.ShowIcon = false;
            f.AutoScaleMode = AutoScaleMode.None;   // sizes below are already DPI-scaled with Theme.S
            f.HandleCreated += delegate
            {
                try
                {
                    int dark = Theme.IsDark ? 1 : 0;
                    DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
                }
                catch { }
            };
        }

        public static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Theme.Accent : Theme.Mix(Theme.Bg, Theme.Overlay, 0.10),
                ForeColor = primary ? Color.White : Theme.Text,
                Font = Theme.UiSemibold(9.5f),
                Height = Theme.S(36),
                Width = Math.Max(Theme.S(100), TextRenderer.MeasureText(text, Theme.UiSemibold(9.5f)).Width + Theme.S(36)),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.Mix(b.BackColor, Theme.Overlay, 0.12);
            b.FlatAppearance.MouseDownBackColor = Theme.Mix(b.BackColor, Theme.Overlay, 0.22);
            return b;
        }

        public static TextBox MakeTextBox()
        {
            return new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Mix(Theme.Bg, Theme.Overlay, 0.07),
                ForeColor = Theme.Text,
                Font = Theme.Ui(10f)
            };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Grey hint text shown while the box is empty.</summary>
        public static void SetCue(TextBox box, string cue)
        {
            if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501, (IntPtr)1, cue);
            else box.HandleCreated += delegate { SendMessage(box.Handle, 0x1501, (IntPtr)1, cue); };
        }
    }

    // ======================================================================= album art picker

    /// <summary>
    /// "Find album art online": searches iTunes and MusicBrainz / Cover Art Archive for the album and shows the covers found.
    /// The chosen cover is downloaded at full size; the caller stores it (and optionally saves cover.jpg next to the music).
    /// </summary>
    internal sealed class ArtPickerForm : Form
    {
        readonly TextBox query;
        readonly Button searchBtn, useBtn, cancelBtn;
        readonly Label status;
        readonly FlowLayoutPanel grid;
        readonly CheckBox saveToFolder;
        ArtTile selected;
        int searchId;

        /// <summary>The chosen cover, full size (after OK).</summary>
        public Image ResultImage;
        /// <summary>The downloaded file as is, for writing cover.jpg (after OK).</summary>
        public byte[] ResultBytes;
        public bool SaveToFolder { get { return saveToFolder.Checked; } }

        /// <summary>Used from the metadata editor, where the cover goes into the files instead.</summary>
        public void HideSaveOption() { saveToFolder.Checked = false; saveToFolder.Visible = false; }

        public ArtPickerForm(string artist, string album, string folderName)
        {
            DialogKit.Style(this, true);
            Text = "Find album art online";
            ClientSize = new Size(Theme.S(860), Theme.S(640));
            MinimumSize = new Size(Theme.S(560), Theme.S(420));
            int pad = Theme.S(18);

            query = DialogKit.MakeTextBox();
            query.Text = (artist + " " + album).Trim();
            DialogKit.SetCue(query, "Artist and album");
            searchBtn = DialogKit.MakeButton("Search", false);
            status = new Label { AutoSize = false, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft };
            grid = new FlowLayoutPanel { AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(Theme.S(6)) };
            saveToFolder = new CheckBox
            {
                Text = "Also save it as cover.jpg in the album folder" + (string.IsNullOrEmpty(folderName) ? "" : " (" + folderName + ")"),
                AutoSize = true,
                ForeColor = Theme.Text,
                FlatStyle = FlatStyle.Flat
            };
            useBtn = DialogKit.MakeButton("Use this cover", true);
            useBtn.Enabled = false;
            cancelBtn = DialogKit.MakeButton("Cancel", false);
            cancelBtn.DialogResult = DialogResult.Cancel;
            CancelButton = cancelBtn;
            AcceptButton = searchBtn;
            Controls.AddRange(new Control[] { query, searchBtn, status, grid, saveToFolder, useBtn, cancelBtn });

            Resize += delegate { DoLayout(pad); };
            DoLayout(pad);
            searchBtn.Click += delegate { Search(); };
            useBtn.Click += delegate { UseSelected(); };
            Shown += delegate { Search(); };
        }

        void DoLayout(int pad)
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int rowH = Theme.S(36);
            searchBtn.SetBounds(W - pad - searchBtn.Width, pad, searchBtn.Width, rowH);
            query.SetBounds(pad, pad + (rowH - query.PreferredHeight) / 2, searchBtn.Left - Theme.S(10) - pad, query.PreferredHeight);
            status.SetBounds(pad, pad + rowH + Theme.S(6), W - 2 * pad, Theme.S(24));
            int bottom = H - pad - rowH;
            cancelBtn.SetBounds(W - pad - cancelBtn.Width, bottom, cancelBtn.Width, rowH);
            useBtn.SetBounds(cancelBtn.Left - Theme.S(10) - useBtn.Width, bottom, useBtn.Width, rowH);
            saveToFolder.Location = new Point(pad, bottom + (rowH - saveToFolder.Height) / 2);
            saveToFolder.MaximumSize = new Size(Math.Max(Theme.S(100), useBtn.Left - 2 * pad), 0);
            grid.SetBounds(pad, status.Bottom + Theme.S(6), W - 2 * pad, bottom - Theme.S(12) - status.Bottom - Theme.S(6));
        }

        void Search()
        {
            string q = query.Text.Trim();
            if (q.Length == 0) return;
            int id = ++searchId;
            foreach (Control c in grid.Controls) c.Dispose();
            grid.Controls.Clear();
            selected = null;
            useBtn.Enabled = false;
            status.Text = "Searching iTunes and MusicBrainz…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ArtCandidate> found = null;
                string error = null;
                try { found = OnlineArt.Search(q); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    if (id != searchId) return;
                    if (found == null) { status.Text = "Couldn’t search online — " + error; return; }
                    if (found.Count == 0) { status.Text = "No covers found. Try fewer or different words."; return; }
                    status.Text = found.Count + " results — click a cover to choose it, double-click to use it";
                    foreach (ArtCandidate c in found)
                    {
                        var tile = new ArtTile(c) { Size = new Size(Theme.S(176), Theme.S(238)), Margin = new Padding(Theme.S(5)) };
                        tile.Click += delegate { Select(tile); };
                        tile.DoubleClick += delegate { Select(tile); UseSelected(); };
                        grid.Controls.Add(tile);
                        LoadThumb(tile, id);
                    }
                });
            });
        }

        static readonly Semaphore thumbSlots = new Semaphore(6, 6);

        void LoadThumb(ArtTile tile, int id)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Image img = null;
                thumbSlots.WaitOne();
                try { if (id == searchId) img = OnlineArt.ToImage(OnlineArt.GetBytes(tile.Candidate.ThumbUrl)); }
                catch { img = null; }
                finally { thumbSlots.Release(); }
                Post(delegate
                {
                    if (tile.IsDisposed) { if (img != null) img.Dispose(); return; }
                    if (img == null) { grid.Controls.Remove(tile); tile.Dispose(); return; }   // no image for this result
                    tile.Candidate.Thumb = img;
                    tile.Invalidate();
                });
            });
        }

        void Select(ArtTile tile)
        {
            if (tile.Candidate.Thumb == null) return;
            if (selected != null) { selected.Selected = false; selected.Invalidate(); }
            selected = tile;
            tile.Selected = true;
            tile.Invalidate();
            useBtn.Enabled = true;
            AcceptButton = useBtn;
        }

        void UseSelected()
        {
            if (selected == null) return;
            ArtCandidate c = selected.Candidate;
            useBtn.Enabled = searchBtn.Enabled = false;
            status.Text = "Downloading the full-size cover…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] bytes = null;
                try { bytes = OnlineArt.DownloadBytes(c); } catch { }
                Image img = OnlineArt.ToImage(bytes);
                Post(delegate
                {
                    useBtn.Enabled = searchBtn.Enabled = true;
                    if (img == null) { status.Text = "Couldn’t download that cover — try another one."; return; }
                    ResultBytes = bytes;
                    ResultImage = img;
                    DialogResult = DialogResult.OK;
                });
            });
        }

        void Post(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (Control c in grid.Controls) { var t = c as ArtTile; if (t != null && t.Candidate.Thumb != null) t.Candidate.Thumb.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>One result in the picker: the cover, album, artist, year and source.</summary>
    internal sealed class ArtTile : Control
    {
        public readonly ArtCandidate Candidate;
        public bool Selected;
        bool hover;

        // Animated artwork picker only.
        /// <summary>Replaces the year · source line when set (e.g. "Checking…", "Animated").</summary>
        public string Status;
        public bool StatusGood;
        /// <summary>Greyed out and not selectable (no animated artwork).</summary>
        public bool Unavailable;
        /// <summary>The check failed (network / service timeout); clicking the tile tries again.</summary>
        public bool CheckFailed;
        /// <summary>HLS playlist of the animated artwork, once found.</summary>
        public string Master;
        /// <summary>Downloaded preview video, and the player showing it in place of the thumbnail.</summary>
        public string PreviewFile;
        public AnimatedCoverPlayer Player;

        public Rectangle ArtRect { get { int pad = Theme.S(8); return new Rectangle(pad, pad, Width - 2 * pad, Width - 2 * pad); } }

        public ArtTile(ArtCandidate c)
        {
            Candidate = c;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            if (Selected || hover) Theme.FillRound(g, Theme.Mix(Theme.Bg, Selected ? Theme.Accent : Theme.Overlay, Selected ? 0.18 : 0.07), card, Theme.S(10f));
            int pad = Theme.S(8);
            var art = new Rectangle(pad, pad, Width - 2 * pad, Width - 2 * pad);
            if (Candidate.Thumb != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var path = Theme.Round(art, Theme.S(8f)))
                {
                    g.SetClip(path);
                    g.DrawImage(Candidate.Thumb, art);
                    g.ResetClip();
                }
            }
            else
            {
                Theme.FillRound(g, Theme.Mix(Theme.Bg, Theme.Overlay, 0.06), art, Theme.S(8f));
                Theme.DrawText(g, Candidate.ThumbUrl == null ? "" : "Loading…", Theme.Ui(8.5f), Theme.TextDim, art, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (Player != null && Player.HasFrame && Player.Surface != null && Player.Size == art.Size)
                Player.Surface.Draw(g, art, Point.Empty, 255, true);   // the live preview, already rounded
            if (Unavailable)
                using (var b = new SolidBrush(Theme.Alpha(Theme.Bg, 150))) g.FillRectangle(b, art);
            if (Selected)
                using (var p = new Pen(Theme.Accent, Theme.S(2.5f)))
                using (var path = Theme.Round(card, Theme.S(10f)))
                    g.DrawPath(p, path);
            int y = art.Bottom + Theme.S(6);
            Theme.TextLeft(g, Candidate.Album, Theme.UiSemibold(9f), Theme.Text, new Rectangle(pad, y, Width - 2 * pad, Theme.S(20)));
            Theme.TextLeft(g, Candidate.Artist, Theme.Ui(8.5f), Theme.TextDim, new Rectangle(pad, y + Theme.S(19), Width - 2 * pad, Theme.S(18)));
            string meta = Status ?? (string.IsNullOrEmpty(Candidate.Year) ? "" : Candidate.Year + "  ·  ") + Candidate.Source;
            Theme.TextLeft(g, meta, Status != null ? Theme.UiSemibold(8f) : Theme.Ui(8f), StatusGood ? Theme.Accent : Theme.TextDim,
                           new Rectangle(pad, y + Theme.S(37), Width - 2 * pad, Theme.S(18)));
        }
    }

    // ======================================================================= animated artwork picker

    /// <summary>
    /// "Find animated artwork": lists Apple Music albums (iTunes Search API) and checks each with artwork.m8tec.top for an
    /// animated cover; a pasted music.apple.com album link is checked directly. Clicking an animated result downloads it and
    /// plays it in the tile as a preview. The caller then uses the chosen video (or switches animation off for the album).
    /// </summary>
    internal sealed class AnimatedArtPickerForm : Form
    {
        readonly TextBox query;
        readonly Button searchBtn, useBtn, offBtn, cancelBtn;
        readonly Label status;
        readonly FlowLayoutPanel grid;
        readonly CheckBox saveToFolder;
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch frameWatch = System.Diagnostics.Stopwatch.StartNew();
        ArtTile selected;
        int searchId;

        /// <summary>After OK: the chosen video (a preview file the caller moves into place).</summary>
        public string ResultFile;
        /// <summary>After OK: true when "No animation for this album" was chosen instead.</summary>
        public bool TurnOff;
        public bool SaveToFolder { get { return saveToFolder.Checked; } }

        public AnimatedArtPickerForm(string artist, string album, string folderName, bool currentlyOff)
        {
            DialogKit.Style(this, true);
            Text = "Find animated artwork";
            ClientSize = new Size(Theme.S(860), Theme.S(660));
            MinimumSize = new Size(Theme.S(600), Theme.S(440));
            int pad = Theme.S(18);

            query = DialogKit.MakeTextBox();
            query.Text = (artist + " " + album).Trim();
            DialogKit.SetCue(query, "Artist and album, or a music.apple.com album link");
            searchBtn = DialogKit.MakeButton("Search", false);
            status = new Label { AutoSize = false, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft };
            grid = new FlowLayoutPanel { AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(Theme.S(6)) };
            saveToFolder = new CheckBox
            {
                Text = "Also save it as cover.mp4 in the album folder" + (string.IsNullOrEmpty(folderName) ? "" : " (" + folderName + ")"),
                AutoSize = true, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat
            };
            useBtn = DialogKit.MakeButton("Use this animation", true);
            useBtn.Enabled = false;
            offBtn = DialogKit.MakeButton(currentlyOff ? "Keep it off" : "No animation for this album", false);
            cancelBtn = DialogKit.MakeButton("Cancel", false);
            cancelBtn.DialogResult = DialogResult.Cancel;
            CancelButton = cancelBtn;
            AcceptButton = searchBtn;
            Controls.AddRange(new Control[] { query, searchBtn, status, grid, saveToFolder, useBtn, offBtn, cancelBtn });

            Resize += delegate { DoLayout(pad); };
            DoLayout(pad);
            searchBtn.Click += delegate { Search(); };
            useBtn.Click += delegate
            {
                if (selected == null || selected.PreviewFile == null) return;
                ResultFile = selected.PreviewFile;
                DialogResult = DialogResult.OK;
            };
            offBtn.Click += delegate { TurnOff = true; DialogResult = DialogResult.OK; };
            clock.Tick += delegate
            {
                // Plays the selected tile's preview (the only one running).
                double dt = frameWatch.Elapsed.TotalSeconds;
                frameWatch.Restart();
                if (selected != null && selected.Player != null && selected.Player.Step(dt)) selected.Invalidate();
            };
            Shown += delegate { clock.Start(); Search(); };
            FormClosing += delegate { clock.Stop(); StopPreviews(); };
        }

        void DoLayout(int pad)
        {
            int W = ClientSize.Width, H = ClientSize.Height, rowH = Theme.S(36);
            searchBtn.SetBounds(W - pad - searchBtn.Width, pad, searchBtn.Width, rowH);
            query.SetBounds(pad, pad + (rowH - query.PreferredHeight) / 2, searchBtn.Left - Theme.S(10) - pad, query.PreferredHeight);
            status.SetBounds(pad, pad + rowH + Theme.S(6), W - 2 * pad, Theme.S(24));
            int bottom = H - pad - rowH;
            cancelBtn.SetBounds(W - pad - cancelBtn.Width, bottom, cancelBtn.Width, rowH);
            useBtn.SetBounds(cancelBtn.Left - Theme.S(10) - useBtn.Width, bottom, useBtn.Width, rowH);
            offBtn.SetBounds(pad, bottom, offBtn.Width, rowH);
            int checkY = bottom - Theme.S(10) - saveToFolder.Height;
            saveToFolder.Location = new Point(pad, checkY);
            grid.SetBounds(pad, status.Bottom + Theme.S(6), W - 2 * pad, checkY - Theme.S(10) - status.Bottom - Theme.S(6));
        }

        static bool IsAppleMusicLink(string s)
        {
            Uri u;
            return Uri.TryCreate(s, UriKind.Absolute, out u) && u.Host.EndsWith("music.apple.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Contains("/album/");
        }

        void Search()
        {
            string q = query.Text.Trim();
            if (q.Length == 0) return;
            int id = ++searchId;
            StopPreviews();
            foreach (Control c in grid.Controls) c.Dispose();
            grid.Controls.Clear();
            selected = null;
            useBtn.Enabled = false;

            if (IsAppleMusicLink(q))
            {
                var tile = AddTile(new ArtCandidate { Source = "Apple Music", Album = "Album from the link", Artist = q, PageUrl = q }, id);
                status.Text = "Checking the link…";
                CheckTiles(new List<ArtTile> { tile }, id);
                return;
            }
            status.Text = "Searching Apple Music…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ArtCandidate> found = null;
                string error = null;
                try { found = OnlineArt.SearchAppleMusicAlbums(q); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    if (id != searchId) return;
                    if (found == null) { status.Text = "Couldn’t search online — " + error; return; }
                    found.RemoveAll(c => string.IsNullOrEmpty(c.PageUrl));
                    if (found.Count == 0) { status.Text = "No albums found. Try fewer or different words, or paste an Apple Music album link."; return; }
                    status.Text = "Checking " + found.Count + " albums for animated artwork…";
                    var tiles = new List<ArtTile>();
                    foreach (ArtCandidate c in found) tiles.Add(AddTile(c, id));
                    CheckTiles(tiles, id);
                });
            });
        }

        ArtTile AddTile(ArtCandidate c, int id)
        {
            var tile = new ArtTile(c) { Size = new Size(Theme.S(176), Theme.S(238)), Margin = new Padding(Theme.S(5)), Status = "Checking…" };
            tile.Click += delegate { Select(tile); };
            grid.Controls.Add(tile);
            if (c.ThumbUrl != null)
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Image img = null;
                    try { if (id == searchId) img = OnlineArt.ToImage(OnlineArt.GetBytes(c.ThumbUrl)); } catch { }
                    Post(delegate
                    {
                        if (tile.IsDisposed) { if (img != null) img.Dispose(); return; }
                        c.Thumb = img;
                        tile.Invalidate();
                    });
                });
            return tile;
        }

        /// <summary>Asks artwork.m8tec.top about each album, two at a time, in the order shown.</summary>
        void CheckTiles(List<ArtTile> tiles, int id, bool report = true)
        {
            int next = -1, done = 0, animated = 0;
            Action worker = null;
            worker = delegate
            {
                int i = Interlocked.Increment(ref next);
                if (i >= tiles.Count || id != searchId) return;
                ArtTile tile = tiles[i];
                string master = null, error = null;
                try { master = AnimatedArt.LookupAppleMusicUrl(tile.Candidate.PageUrl); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    if (tile.IsDisposed || id != searchId) return;
                    tile.Master = master;
                    tile.Unavailable = master == null && error == null;
                    tile.CheckFailed = error != null;
                    tile.Status = master != null ? "Animated — click to preview" : error != null ? "Couldn’t check — click to retry" : "No animated artwork";
                    tile.StatusGood = master != null;
                    tile.Invalidate();
                    done++;
                    if (master != null) animated++;
                    if (report) status.Text = done < tiles.Count ? "Checking for animated artwork… " + done + " of " + tiles.Count
                        : animated == 0 ? "None of these albums has animated artwork."
                        : animated + " of " + tiles.Count + " albums have animated artwork — click one to preview it";
                });
                worker();
            };
            for (int k = 0; k < 2; k++) ThreadPool.QueueUserWorkItem(delegate { worker(); });
        }

        void Select(ArtTile tile)
        {
            if (tile.CheckFailed)
            {
                // The service looks an album up on Apple Music the first time, which can time out: ask again.
                tile.CheckFailed = false;
                tile.Status = "Checking…";
                tile.Invalidate();
                CheckTiles(new List<ArtTile> { tile }, searchId, false);
                return;
            }
            if (tile.Master == null) return;
            if (selected != null && selected != tile) { selected.Selected = false; StopPreview(selected); selected.Invalidate(); }
            selected = tile;
            tile.Selected = true;
            tile.Invalidate();
            if (tile.PreviewFile != null) { StartPreview(tile); useBtn.Enabled = true; AcceptButton = useBtn; return; }
            useBtn.Enabled = false;
            tile.Status = "Downloading preview…";
            int id = searchId;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string file = AnimatedArt.NewPreviewPath(), error = null;
                try { AnimatedArt.Download(tile.Master, file); }
                catch (Exception ex) { error = ex.Message; file = null; }
                Post(delegate
                {
                    if (tile.IsDisposed) return;
                    if (file == null) { tile.Status = "Couldn’t download it"; tile.StatusGood = false; tile.Invalidate(); return; }
                    tile.PreviewFile = file;
                    tile.Status = "Animated — previewing";
                    tile.Invalidate();
                    if (selected == tile && id == searchId) { StartPreview(tile); useBtn.Enabled = true; AcceptButton = useBtn; }
                });
            });
        }

        void StartPreview(ArtTile tile)
        {
            StopPreview(tile);
            try { tile.Player = new AnimatedCoverPlayer(tile.PreviewFile, tile.ArtRect.Size, Theme.S(8)); }
            catch { tile.Player = null; }
        }

        static void StopPreview(ArtTile tile)
        {
            if (tile.Player == null) return;
            tile.Player.Dispose();
            tile.Player = null;
        }

        /// <summary>Stops every preview player (they hold their video files open).</summary>
        void StopPreviews()
        {
            foreach (Control c in grid.Controls) { var t = c as ArtTile; if (t != null) StopPreview(t); }
        }

        void Post(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clock.Dispose();
                foreach (Control c in grid.Controls)
                {
                    var t = c as ArtTile;
                    if (t == null) continue;
                    StopPreview(t);
                    if (t.Candidate.Thumb != null) t.Candidate.Thumb.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    // ======================================================================= text prompt

    /// <summary>A small themed "type a name" dialog (new playlist, rename playlist).</summary>
    internal sealed class PromptForm : Form
    {
        readonly TextBox box;
        readonly Button ok;

        public string Value { get { return box.Text.Trim(); } }

        public PromptForm(string title, string message, string initial, string okText)
        {
            DialogKit.Style(this, false);
            Text = title;
            int pad = Theme.S(20), w = Theme.S(400);
            var msg = new Label { Text = message, ForeColor = Theme.TextDim, AutoSize = false, Bounds = new Rectangle(pad, pad, w, Theme.S(24)) };
            box = DialogKit.MakeTextBox();
            box.Text = initial ?? "";
            box.MaxLength = 120;
            box.Bounds = new Rectangle(pad, msg.Bottom + Theme.S(6), w, box.PreferredHeight);
            ok = DialogKit.MakeButton(okText, true);
            var cancel = DialogKit.MakeButton("Cancel", false);
            cancel.DialogResult = DialogResult.Cancel;
            int y = box.Bottom + Theme.S(20);
            cancel.SetBounds(pad + w - cancel.Width, y, cancel.Width, Theme.S(36));
            ok.SetBounds(cancel.Left - Theme.S(10) - ok.Width, y, ok.Width, Theme.S(36));
            ok.Enabled = Value.Length > 0;
            box.TextChanged += delegate { ok.Enabled = Value.Length > 0; };
            ok.Click += delegate { if (Value.Length > 0) DialogResult = DialogResult.OK; };
            Controls.AddRange(new Control[] { msg, box, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(w + 2 * pad, y + Theme.S(36) + pad);
            Shown += delegate { box.Focus(); box.SelectAll(); };
        }

        /// <summary>Returns the entered text, or null when cancelled.</summary>
        public static string Ask(IWin32Window owner, string title, string message, string initial, string okText)
        {
            using (var f = new PromptForm(title, message, initial, okText))
                return f.ShowDialog(owner) == DialogResult.OK ? f.Value : null;
        }
    }

    // ======================================================================= metadata editor

    /// <summary>
    /// Edit tags of one or more songs. With several songs, a field that differs between them starts empty and is left
    /// alone unless something is typed; title and track number are only editable for a single song.
    /// </summary>
    internal sealed class TagEditorForm : Form
    {
        // ffmpeg metadata key, label
        public static readonly string[][] Fields =
        {
            new[] { "title", "Title" }, new[] { "artist", "Artist" }, new[] { "album", "Album" }, new[] { "album_artist", "Album artist" },
            new[] { "date", "Year" }, new[] { "track", "Track" }, new[] { "disc", "Disc" }, new[] { "genre", "Genre" }
        };

        readonly TextBox[] boxes = new TextBox[Fields.Length];
        readonly string[] initial = new string[Fields.Length];   // null = differs between the songs
        readonly Button saveBtn, cancelBtn, chooseArtBtn, findArtBtn, removeArtBtn, keepArtBtn;
        readonly ArtPreview artPreview;
        readonly Label artNote;
        readonly Image currentArt;
        readonly string defaultArtNote;
        readonly List<string> tempFiles = new List<string>();

        /// <summary>After OK: ffmpeg metadata key → new value ("" removes the tag). Only fields that should change.</summary>
        public readonly Dictionary<string, string> Changes = new Dictionary<string, string>();
        /// <summary>After OK: what to do with the embedded album art, and the image file for Replace (deleted by the dialog's owner via <see cref="TempFiles"/>).</summary>
        public ArtChange Art = ArtChange.Keep;
        public string ArtFile;
        public List<string> TempFiles { get { return tempFiles; } }

        /// <param name="values">Current value of each field (same order as <see cref="Fields"/>) for each song.</param>
        /// <param name="art">The first song's embedded picture (null = none).</param>
        public TagEditorForm(List<TrackInfo> songs, List<string[]> values, Image art)
        {
            DialogKit.Style(this, false);
            currentArt = art;
            bool many = songs.Count > 1;
            Text = many ? "Edit metadata — " + songs.Count + " songs" : "Edit metadata — " + System.IO.Path.GetFileName(songs[0].FilePath);
            int pad = Theme.S(20), labelW = Theme.S(110), rowH = Theme.S(40), boxW = Theme.S(380), artW = Theme.S(190);
            int artX = pad + labelW + boxW + Theme.S(24);
            int y = pad;

            // ---- album art column (right)
            var artLabel = new Label { Text = "Album art", ForeColor = Theme.TextDim, Bounds = new Rectangle(artX, pad, artW, Theme.S(22)) };
            artPreview = new ArtPreview { Bounds = new Rectangle(artX, pad + Theme.S(26), artW, artW), Image = art };
            chooseArtBtn = DialogKit.MakeButton("Choose image…", false);
            findArtBtn = DialogKit.MakeButton("Find online…", false);
            removeArtBtn = DialogKit.MakeButton("Remove", false);
            keepArtBtn = DialogKit.MakeButton("Keep current", false);
            int by = artPreview.Bottom + Theme.S(10);
            foreach (Button b in new[] { chooseArtBtn, findArtBtn, removeArtBtn, keepArtBtn })
            {
                b.SetBounds(artX, by, artW, Theme.S(34));
                by += Theme.S(40);
            }
            keepArtBtn.Visible = false;
            removeArtBtn.Enabled = art != null || many;
            int embeddable = songs.FindAll(s => TagWriter.CanEmbedArt(s.FilePath)).Count;
            defaultArtNote = embeddable == 0 ? "Album art can’t be embedded in " + System.IO.Path.GetExtension(songs[0].FilePath) + " files."
                : (many ? "Changes apply to all " + songs.Count + " songs" + (embeddable < songs.Count ? " (" + (songs.Count - embeddable) + " can’t hold art)" : "") + ". " : "")
                  + (FolderHasCover(songs[0]) ? "This folder also has a cover image, which the player shows first." : "");
            chooseArtBtn.Enabled = findArtBtn.Enabled = embeddable > 0;
            artNote = new Label { Text = defaultArtNote, ForeColor = Theme.TextDim, AutoSize = false, Bounds = new Rectangle(artX, by, artW, Theme.S(80)) };
            Controls.AddRange(new Control[] { artLabel, artPreview, chooseArtBtn, findArtBtn, removeArtBtn, keepArtBtn, artNote });
            if (many && art != null) artPreview.Caption = "First song’s art";

            chooseArtBtn.Click += delegate { ChooseImage(); };
            findArtBtn.Click += delegate
            {
                string artist = boxes[3].Text.Trim().Length > 0 ? boxes[3].Text.Trim() : boxes[1].Text.Trim();
                using (var dlg = new ArtPickerForm(artist, boxes[2].Text.Trim(), null))
                {
                    dlg.HideSaveOption();
                    if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ResultBytes == null) return;
                    bool png = dlg.ResultBytes.Length > 4 && dlg.ResultBytes[0] == 0x89 && dlg.ResultBytes[1] == 0x50;
                    string tmp = NewTemp(png ? ".png" : ".jpg");
                    System.IO.File.WriteAllBytes(tmp, dlg.ResultBytes);
                    SetReplace(tmp, dlg.ResultImage);
                }
            };
            removeArtBtn.Click += delegate
            {
                Art = ArtChange.Remove;
                ArtFile = null;
                artPreview.Image = null;
                artPreview.Caption = "Will be removed";
                ArtChanged();
            };
            keepArtBtn.Click += delegate
            {
                Art = ArtChange.Keep;
                ArtFile = null;
                artPreview.Image = currentArt;
                artPreview.Caption = many && currentArt != null ? "First song’s art" : null;
                ArtChanged();
            };

            if (many)
            {
                var note = new Label
                {
                    Text = "Fields that differ between the songs are empty — leave them empty to keep each song’s own value.",
                    ForeColor = Theme.TextDim, AutoSize = false, Bounds = new Rectangle(pad, y, labelW + boxW, Theme.S(40))
                };
                Controls.Add(note);
                y += Theme.S(44);
            }

            for (int i = 0; i < Fields.Length; i++)
            {
                string first = values[0][i] ?? "";
                bool same = values.TrueForAll(v => (v[i] ?? "") == first);
                initial[i] = same ? first : null;
                var label = new Label { Text = Fields[i][1], ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(pad, y, labelW, rowH - Theme.S(8)) };
                TextBox box = DialogKit.MakeTextBox();
                box.Text = initial[i] ?? "";
                box.Bounds = new Rectangle(pad + labelW, y + (rowH - Theme.S(8) - box.PreferredHeight) / 2, boxW, box.PreferredHeight);
                if (!same) DialogKit.SetCue(box, "Multiple values");
                bool perSong = Fields[i][0] == "title" || Fields[i][0] == "track";
                if (many && perSong) { box.Enabled = false; box.Text = "(kept for each song)"; }
                boxes[i] = box;
                Controls.Add(label);
                Controls.Add(box);
                y += rowH;
            }

            var hint = new Label
            {
                Text = "Audio is copied unchanged — only the tags and the embedded picture are rewritten.",
                ForeColor = Theme.TextDim, AutoSize = false, Bounds = new Rectangle(pad, y + Theme.S(4), labelW + boxW, Theme.S(36))
            };
            Controls.Add(hint);
            y += Theme.S(48);
            y = Math.Max(y, artNote.Bottom + Theme.S(8));

            int right = artX + artW;
            saveBtn = DialogKit.MakeButton("Save", true);
            cancelBtn = DialogKit.MakeButton("Cancel", false);
            cancelBtn.DialogResult = DialogResult.Cancel;
            cancelBtn.SetBounds(right - cancelBtn.Width, y, cancelBtn.Width, Theme.S(36));
            saveBtn.SetBounds(cancelBtn.Left - Theme.S(10) - saveBtn.Width, y, saveBtn.Width, Theme.S(36));
            Controls.Add(saveBtn);
            Controls.Add(cancelBtn);
            AcceptButton = saveBtn;
            CancelButton = cancelBtn;
            ClientSize = new Size(right + pad, y + Theme.S(36) + pad);

            saveBtn.Click += delegate
            {
                Changes.Clear();
                for (int i = 0; i < Fields.Length; i++)
                {
                    if (!boxes[i].Enabled) continue;
                    string v = boxes[i].Text.Trim();
                    if (initial[i] == null) { if (v.Length > 0) Changes[Fields[i][0]] = v; }   // differed: only a typed value applies
                    else if (v != initial[i].Trim()) Changes[Fields[i][0]] = v;
                }
                DialogResult = DialogResult.OK;
            };
        }

        static bool FolderHasCover(TrackInfo t)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(t.FilePath);
                foreach (string f in System.IO.Directory.EnumerateFiles(dir))
                {
                    string e = System.IO.Path.GetExtension(f).ToLowerInvariant(), n = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if ((e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".bmp") && (n == "cover" || n == "folder" || n == "front" || n == "album" || n.StartsWith("albumart")))
                        return true;
                }
            }
            catch { }
            return false;
        }

        string NewTemp(string ext)
        {
            string f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soda-cover-" + Guid.NewGuid().ToString("N") + ext);
            tempFiles.Add(f);
            return f;
        }

        /// <summary>JPEG and PNG files are embedded as they are; other formats (BMP, GIF, TIFF…) are converted to JPEG first.</summary>
        void ChooseImage()
        {
            using (var ofd = new OpenFileDialog { Title = "Choose album art", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                Image img;
                try { using (var fs = System.IO.File.OpenRead(ofd.FileName)) using (Image src = Image.FromStream(fs)) img = new Bitmap(src); }
                catch { MessageBox.Show(this, "That file couldn’t be read as an image.", "Album art", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                string ext = System.IO.Path.GetExtension(ofd.FileName).ToLowerInvariant();
                string file = ofd.FileName;
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png")
                {
                    file = NewTemp(".jpg");
                    using (var bmp = new Bitmap(img.Width, img.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.DrawImage(img, 0, 0, img.Width, img.Height); }
                        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                }
                SetReplace(file, img);
            }
        }

        void SetReplace(string file, Image shown)
        {
            Art = ArtChange.Replace;
            ArtFile = file;
            artPreview.Image = shown;
            artPreview.Caption = "New";
            ArtChanged();
        }

        void ArtChanged()
        {
            keepArtBtn.Visible = Art != ArtChange.Keep;
            removeArtBtn.Enabled = Art != ArtChange.Remove;
            artNote.Text = Art == ArtChange.Replace ? "The new picture replaces the embedded one when you save. " + defaultArtNote
                         : Art == ArtChange.Remove ? "The embedded picture is removed when you save. " + defaultArtNote
                         : defaultArtNote;
            artPreview.Invalidate();
        }
    }

    /// <summary>Album art preview in the metadata editor: the image (rounded) or a placeholder, with an optional caption chip.</summary>
    internal sealed class ArtPreview : Control
    {
        public Image Image;
        public string Caption;

        public ArtPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (Image != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float s = Math.Max((float)Width / Image.Width, (float)Height / Image.Height);
                using (var path = Theme.Round(r, Theme.S(10f)))
                {
                    g.SetClip(path);
                    g.DrawImage(Image, (Width - Image.Width * s) / 2, (Height - Image.Height * s) / 2, Image.Width * s, Image.Height * s);
                    g.ResetClip();
                }
            }
            else
            {
                Theme.FillRound(g, Theme.Mix(Theme.Bg, Theme.Overlay, 0.06), r, Theme.S(10f));
                Theme.DrawText(g, "No album art", Theme.Ui(9f), Theme.TextDim, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (!string.IsNullOrEmpty(Caption))
            {
                Font f = Theme.UiSemibold(8f);
                int w = TextRenderer.MeasureText(Caption, f).Width + Theme.S(14);
                var chip = new Rectangle(Theme.S(8), Height - Theme.S(30), w, Theme.S(22));
                Theme.FillRound(g, Theme.Alpha(Color.Black, 170), chip, chip.Height / 2f);
                Theme.DrawText(g, Caption, f, Color.White, chip, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}

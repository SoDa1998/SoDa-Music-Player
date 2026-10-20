using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    internal sealed class MainForm : Form, IPlayerHost
    {
        static readonly int[] BufferChoices = { 5, 10, 20, 50, 100, 200 };

        readonly PlaybackEngine engine = new PlaybackEngine();
        readonly Library library = new Library();
        readonly List<TrackInfo> queue = new List<TrackInfo>();
        readonly object queueLock = new object();
        readonly BlockingCollection<TrackInfo> probeQueue = new BlockingCollection<TrackInfo>();
        readonly Backdrop backdrop = new Backdrop();
        readonly string[] startupArgs;
        readonly Random rng = new Random();

        volatile int repeatMode;           // 0 off, 1 all, 2 one
        TrackInfo nowPlaying;
        EndpointVolume endpointVolume;
        double volumeValue = 1;
        SongSort songSort = SongSort.DateAddedNewest;
        string statusText = "";
        bool statusError, suppress;
        bool libraryDirty, librarySaveDirty;
        int lastLibraryChange, lastLibrarySave, artPending, probedCount;
        NavPage currentNav = NavPage.NowPlaying, detailParent = NavPage.Albums;
        bool detailVisible;
        AlbumInfo detailAlbum;
        ArtistInfo detailArtist;
        List<TrackInfo> songItems = new List<TrackInfo>();          // what the Songs page shows (search-filtered)
        List<TrackInfo> songAll = new List<TrackInfo>();            // all songs in the current sort order
        List<TileItem> albumTilesAll = new List<TileItem>(), artistTilesAll = new List<TileItem>();
        readonly PlaylistStore playlists = new PlaylistStore();
        Playlist detailPlaylist;

        Sidebar sidebar;
        TitleBar titleBar;
        MiniPlayer mini;
        HitPanel content, pages, albumsPage, artistsPage, songsPage, playlistsPage, detailPage;
        NowPlayingView nowView;
        SettingsView settingsView, eqView;
        PageHeader albumsHeader, artistsHeader, songsHeader, playlistsHeader;
        CanvasButton sortButton;
        TileGridView albumGrid, artistGrid, playlistGrid;
        SongListView songList, detailList;
        DetailHeader detailHeader;
        System.Windows.Forms.Timer timer;

        static string SettingsFile { get { return Path.Combine(Library.DataDir, "settings.ini"); } }
        static string QueueFile { get { return Path.Combine(Library.DataDir, "queue.m3u8"); } }

        public MainForm(string[] args)
        {
            startupArgs = args;
            using (Graphics g = CreateGraphics()) Theme.Init(g.DpiX / 96f);

            Text = "SoDa Player";
            Icon = Theme.AppIconSet ?? Icon;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9.5f);
            FormBorderStyle = FormBorderStyle.Sizable;     // native frame (shadow, snap, resize) with the caption removed in WM_NCCALCSIZE
            Size = new Size(Theme.S(1280), Theme.S(830));
            MinimumSize = new Size(Theme.S(920), Theme.S(620));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            DoubleBuffered = true;

            BuildUi();
            Theme.BackgroundPainter = delegate(Graphics g, Control c) { if (!Theme.Glass) return false; backdrop.Paint(g, c, this); return true; };

            engine.NextTrackProvider = NextTrackForEngine;
            engine.TrackChanged += delegate(TrackInfo t) { UI(delegate { OnEngineTrackChanged(t); }); };
            engine.ErrorOccurred += delegate(string msg) { UI(delegate { SetStatus(msg, true); }); };
            engine.PlaybackFinished += delegate
            {
                UI(delegate
                {
                    if (sleepAfterSong) { sleepAfterSong = false; SetStatus("Sleep timer: stopped after the song.", false); nowView.Invalidate(); }
                    else SetStatus("Playback finished.", false);
                });
            };
            engine.Notice += delegate(string msg) { UI(delegate { SetStatus(msg, false); }); };
            LyricsService.Ready += delegate(TrackInfo t) { UI(delegate { nowView.LyricsArrived(t); }); };
            AnimatedArt.Ready += delegate { UI(delegate { nowView.AnimatedArtChanged(); }); };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            ArtCache.ArtReady += delegate { Interlocked.Exchange(ref artPending, 1); };

            for (int i = 0; i < 3; i++)
                new Thread(ProbeWorker) { IsBackground = true, Name = "probe", Priority = ThreadPriority.BelowNormal }.Start();

            timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += delegate { OnTick(); };
            timer.Start();
        }

        // ================================================================== UI construction

        void BuildUi()
        {
            sidebar = new Sidebar(this);
            sidebar.NavClicked += Navigate;
            sidebar.ShellChanged += LayoutShell;
            content = new HitPanel();
            pages = new HitPanel { Dock = DockStyle.Fill };
            titleBar = new TitleBar(this) { Dock = DockStyle.Top };
            mini = new MiniPlayer(this) { Dock = DockStyle.Bottom };
            content.Controls.Add(pages);
            content.Controls.Add(mini);
            content.Controls.Add(titleBar);
            Controls.Add(content);
            Controls.Add(sidebar);
            sidebar.BringToFront();   // above the pages: it overlaps them while it glides wider or narrower
            LayoutShell();

            nowView = new NowPlayingView(this) { Dock = DockStyle.Fill };
            nowView.ArtContextRequested += delegate(Point p)
            {
                TrackInfo t = nowPlaying;
                ContextMenuStrip m = Theme.Menu();
                string sleep = SleepTimerText;
                Theme.MenuItem(m, sleep == null ? "Sleep timer…" : "Sleep timer (" + sleep + ")…", Glyphs.Moon, delegate { ShowSleepMenu(nowView, p); });
                Theme.MenuItem(m, nowView.VisualizerEnabled ? "Hide visualizer" : "Show visualizer", Glyphs.Equalizer, delegate
                {
                    nowView.VisualizerEnabled = !nowView.VisualizerEnabled;
                    SetStatus(nowView.VisualizerEnabled ? "Visualizer on." : "Visualizer off.", false);
                });
                if (t == null) { m.Show(nowView, p); return; }
                Theme.MenuItem(m, "Search lyrics…", Glyphs.Lyrics, delegate { SearchLyrics(t); });
                m.Items.Add(new ToolStripSeparator());
                AddToPlaylistMenu(m, delegate { return new List<TrackInfo> { t }; });
                m.Items.Add(new ToolStripSeparator());
                Theme.MenuItem(m, "Find album art online…", Glyphs.Album, delegate { FindArtOnline(t); });
                Theme.MenuItem(m, "Find animated artwork…", Glyphs.PlayOutline, delegate { FindAnimatedArt(t); });
                Theme.MenuItem(m, "Edit metadata…", Glyphs.Adjust, delegate { EditMetadata(new List<TrackInfo> { t }); });
                m.Show(nowView, p);
            };

            albumsHeader = new PageHeader { Dock = DockStyle.Top, Title = "Albums" };
            albumGrid = new TileGridView { Dock = DockStyle.Fill, EmptyText = "No albums yet — add a music folder from the Songs page." };
            albumGrid.Opened += delegate(TileItem it) { OpenAlbum((AlbumInfo)it.Tag, NavPage.Albums, false); };
            albumGrid.PlayRequested += delegate(TileItem it) { PlayList(((AlbumInfo)it.Tag).Tracks, 0, false); };
            albumGrid.ContextRequested += delegate(TileItem it, Point p) { ShowAlbumMenu((AlbumInfo)it.Tag, albumGrid, p); };
            albumsPage = MakePage(albumsHeader, albumGrid);
            albumsHeader.EnableSearch("Search albums, artists, years");
            albumsHeader.SearchChanged += delegate { FilterAlbums(); };
            albumsHeader.SearchSubmitted += delegate { albumGrid.Focus(); };

            artistsHeader = new PageHeader { Dock = DockStyle.Top, Title = "Artists" };
            artistGrid = new TileGridView { Dock = DockStyle.Fill, Circle = true, EmptyText = "No artists yet — add a music folder from the Songs page." };
            artistGrid.Opened += delegate(TileItem it) { OpenArtist((ArtistInfo)it.Tag, false); };
            artistGrid.PlayRequested += delegate(TileItem it) { PlayList(((ArtistInfo)it.Tag).Tracks, 0, false); };
            artistGrid.ContextRequested += delegate(TileItem it, Point p) { ShowArtistMenu((ArtistInfo)it.Tag, artistGrid, p); };
            artistsPage = MakePage(artistsHeader, artistGrid);
            artistsHeader.EnableSearch("Search artists");
            artistsHeader.SearchChanged += delegate { FilterArtists(); };
            artistsHeader.SearchSubmitted += delegate { artistGrid.Focus(); };

            songsHeader = new PageHeader { Dock = DockStyle.Top, Title = "Songs" };
            songsHeader.AddRight(Glyphs.Play, "Play all", BtnStyle.AccentPill, null, delegate { PlayList(songItems, 0, false); });
            songsHeader.AddRight(Glyphs.Shuffle, "Shuffle", BtnStyle.Pill, null, delegate { PlayList(songItems, 0, true); });
            songsHeader.AddRight(Glyphs.Folder, "Add folder", BtnStyle.Pill, "Add a music folder to the library", AddFolderDialog);
            sortButton = songsHeader.AddRight(Glyphs.Sort, SortButtonText(), BtnStyle.Pill, "Sort songs", ShowSortMenu);
            songList = new SongListView(this) { Dock = DockStyle.Fill };
            songList.Activated += delegate(int i) { PlayList(songList.Items, i, false); };
            songList.ContextRequested += delegate(int i, Point p) { ShowSongMenu(songList, i, p); };
            songList.SelectionChanged += delegate { UpdateSongsSubtitle(); };
            songsPage = MakePage(songsHeader, songList);
            songsHeader.EnableSearch("Search songs, artists, albums");
            songsHeader.SearchChanged += delegate { FilterSongs(false); };
            songsHeader.SearchSubmitted += delegate { songList.FocusFirst(); };

            playlistsHeader = new PageHeader { Dock = DockStyle.Top, Title = "Playlists" };
            playlistsHeader.AddRight(Glyphs.Add, "New playlist", BtnStyle.AccentPill, "Create an empty playlist", delegate { NewPlaylist(null); });
            playlistGrid = new TileGridView { Dock = DockStyle.Fill, EmptyText = "No playlists yet — click “New playlist”, or right-click songs and choose “Add to playlist”." };
            playlistGrid.Opened += delegate(TileItem it) { OpenPlaylist((Playlist)it.Tag, false); };
            playlistGrid.PlayRequested += delegate(TileItem it) { PlayList(((Playlist)it.Tag).Resolve(library), 0, false); };
            playlistGrid.ContextRequested += delegate(TileItem it, Point p) { ShowPlaylistMenu((Playlist)it.Tag, playlistGrid, p); };
            playlistsPage = MakePage(playlistsHeader, playlistGrid);

            BuildFoldersPage();

            detailHeader = new DetailHeader { Dock = DockStyle.Top };
            detailList = new SongListView(this) { Dock = DockStyle.Fill };
            detailHeader.RenameClicked = delegate { RenamePlaylist(detailPlaylist); };
            detailHeader.DeleteClicked = delegate { DeletePlaylist(detailPlaylist); };
            detailHeader.BackClicked = delegate { Navigate(detailParent); };
            detailHeader.PlayClicked = delegate { PlayList(detailList.Items, 0, false); };
            detailHeader.ShuffleClicked = delegate { PlayList(detailList.Items, 0, true); };
            detailList.Activated += delegate(int i) { PlayList(detailList.Items, i, false); };
            detailList.ContextRequested += delegate(int i, Point p) { ShowSongMenu(detailList, i, p); };
            detailPage = MakePage(detailHeader, detailList);

            settingsView = new SettingsView { Dock = DockStyle.Fill, Visible = false };
            BuildSettings();
            eqView = new SettingsView { Dock = DockStyle.Fill, Visible = false };
            BuildEqualizer();
            sidebar.LayoutChanged += delegate { SaveSettings(); };

            pages.Controls.AddRange(new Control[] { nowView, albumsPage, artistsPage, songsPage, foldersPage, playlistsPage, detailPage, eqView, settingsView });
            EnableDrop(this);
        }

        static HitPanel MakePage(Control header, Control body)
        {
            var p = new HitPanel { Dock = DockStyle.Fill, Visible = false };
            p.Controls.Add(body);
            p.Controls.Add(header);
            return p;
        }

        void EnableDrop(Control c)
        {
            if (!(c is ComboBox) && !(c is ListBox))
            {
                c.AllowDrop = true;
                c.DragEnter += delegate(object s, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
                c.DragDrop += delegate(object s, DragEventArgs e)
                {
                    var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null) AddToLibrary(files, false);
                };
            }
            foreach (Control child in c.Controls) EnableDrop(child);
        }

        // ================================================================== window chrome (borderless, rounded, translucent)

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AnimationClock.Init(this);   // display-synced frames for scrolling, hover fades and transitions
            RegisterMediaKeys();
            TaskbarIdentity.ApplyToWindow(Handle);   // app id + relaunch icon for the taskbar / pinning
            try
            {
                int v = 1;
                DwmSetWindowAttribute(Handle, 20, ref v, 4);            // DWMWA_USE_IMMERSIVE_DARK_MODE
                v = 2;
                DwmSetWindowAttribute(Handle, 33, ref v, 4);            // DWMWA_WINDOW_CORNER_PREFERENCE = round (Windows 11)
                v = 60 | (70 << 8) | (92 << 16);
                DwmSetWindowAttribute(Handle, 34, ref v, 4);            // DWMWA_BORDER_COLOR
            }
            catch { }
        }

        // ------------------------------------------------------------------ media keys (⏯ ⏭ ⏮ ⏹)

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        static readonly uint[] MediaVk = { 0xB3, 0xB0, 0xB1, 0xB2 };   // play/pause, next, previous, stop
        readonly bool[] mediaKeyRegistered = new bool[4];

        /// <summary>
        /// Registers the keyboard media keys as global hotkeys, so they control SoDa Player even while another window is
        /// in front. A key another program already holds is skipped; it still works while SoDa Player has the focus
        /// (ProcessCmdKey / WM_APPCOMMAND below).
        /// </summary>
        void RegisterMediaKeys()
        {
            for (int i = 0; i < MediaVk.Length; i++)
                try { mediaKeyRegistered[i] = RegisterHotKey(Handle, 0x5D00 + i, 0x4000 /* MOD_NOREPEAT */, MediaVk[i]); } catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            for (int i = 0; i < MediaVk.Length; i++) if (mediaKeyRegistered[i]) try { UnregisterHotKey(Handle, 0x5D00 + i); } catch { }
            base.OnHandleDestroyed(e);
        }

        void MediaCommand(int which)
        {
            switch (which)
            {
                case 0: PlayPause(); break;
                case 1: Next(); break;
                case 2: Previous(); break;
                case 3: StopPlayback(); break;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x312, WM_APPCOMMAND = 0x319, WM_SIZE = 0x5;
            if (m.Msg == WM_SIZE) OnWmSize(m.WParam);
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32() - 0x5D00;
                if (id >= 0 && id < MediaVk.Length) { MediaCommand(id); return; }
            }
            if (m.Msg == WM_APPCOMMAND)
            {
                int cmd = (int)((m.LParam.ToInt64() >> 16) & 0x0FFF);
                int which = cmd == 14 || cmd == 46 || cmd == 47 ? 0 : cmd == 11 ? 1 : cmd == 12 ? 2 : cmd == 13 ? 3 : -1;
                if (which == 0 && cmd == 46 && engine.IsRunning && !engine.IsPaused) which = -2;   // "play" while playing: nothing to do
                if (which == 0 && cmd == 47 && (!engine.IsRunning || engine.IsPaused)) which = -2;  // "pause" while paused
                if (which != -1) { if (which >= 0) MediaCommand(which); m.Result = (IntPtr)1; return; }
            }
            const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_WINDOWPOSCHANGING = 0x46;
            if (m.Msg == WM_WINDOWPOSCHANGING) FixRestoreSize(m.LParam);
            if (m.Msg == WM_NCCALCSIZE && m.WParam == IntPtr.Zero)
            {
                // Size query (no layout change): the client rectangle equals the window rectangle.
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                // Remove the caption and borders: the client area covers the whole window.
                if (IsZoomed(Handle))
                {
                    // A maximized sizable window overhangs the screen by its frame; pull the client back in.
                    var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    int pad = GetSystemMetrics(92);  // SM_CXPADDEDBORDER
                    int fx = GetSystemMetrics(32) + pad, fy = GetSystemMetrics(33) + pad;
                    r.Left += fx; r.Top += fy; r.Right -= fx; r.Bottom -= fy;
                    Marshal.StructureToPtr(r, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (!IsZoomed(Handle))
                {
                    long lp = m.LParam.ToInt64();
                    Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    int b = EdgeHit.Border;
                    bool l = p.X < b, rr = p.X >= ClientSize.Width - b, t = p.Y < b, bo = p.Y >= ClientSize.Height - b;
                    int ht = t && l ? 13 : t && rr ? 14 : bo && l ? 16 : bo && rr ? 17 : l ? 10 : rr ? 11 : t ? 12 : bo ? 15 : 0;
                    if (ht != 0) m.Result = (IntPtr)ht;
                }
                return;
            }
            base.WndProc(ref m);
        }

        // The client area is the whole window (WM_NCCALCSIZE above), so client size == window size.
        protected override Size SizeFromClientSize(Size clientSize) { return clientSize; }

        // After a minimize/maximize, WinForms (Form.RestoreWindowBoundsIfNecessary) re-applies a restore size computed
        // with the standard frame added (+frame width, +caption height). Our window has no frame, so that would make it
        // grow on every restore: catch exactly that resize and keep the real normal size.
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        Rectangle normalBounds;

        void TrackNormalBounds()
        {
            if (IsHandleCreated && !fullscreen && !IsZoomed(Handle) && !IsIconic(Handle)) normalBounds = Bounds;
        }

        void FixRestoreSize(IntPtr windowPos)
        {
            if (normalBounds.IsEmpty || IsZoomed(Handle) || IsIconic(Handle)) return;
            int offset = IntPtr.Size * 2;                         // WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags
            int flags = Marshal.ReadInt32(windowPos, offset + 16);
            if ((flags & 0x1) != 0) return;                       // SWP_NOSIZE
            int pad = GetSystemMetrics(92);
            int extraW = 2 * (GetSystemMetrics(32) + pad);        // SM_CXSIZEFRAME
            int extraH = GetSystemMetrics(4) + 2 * (GetSystemMetrics(33) + pad);   // SM_CYCAPTION + SM_CYSIZEFRAME
            int cx = Marshal.ReadInt32(windowPos, offset + 8), cy = Marshal.ReadInt32(windowPos, offset + 12);
            if (cx == normalBounds.Width + extraW && cy == normalBounds.Height + extraH)
            {
                Marshal.WriteInt32(windowPos, offset + 8, normalBounds.Width);
                Marshal.WriteInt32(windowPos, offset + 12, normalBounds.Height);
            }
        }

        bool wasMinimized;

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); TrackNormalBounds(); }

        /// <summary>
        /// WM_SIZE: back from the taskbar, repaint every control. Windows shows the image stored while minimized, and a
        /// control that repainted in the meantime (the title bar after a track change) had no window size to draw the
        /// backdrop with. A maximized window keeps its size across minimize/restore, so SizeChanged doesn't fire: WM_SIZE does.
        /// </summary>
        void OnWmSize(IntPtr wParam)
        {
            int kind = wParam.ToInt32();   // 0 restored, 1 minimized, 2 maximized
            if (kind == 1) { wasMinimized = true; return; }
            if (wasMinimized && (kind == 0 || kind == 2))
            {
                wasMinimized = false;
                BeginInvoke((Action)delegate { if (!IsDisposed) { Invalidate(true); Update(); } });   // after the layout settles
            }
        }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); TrackNormalBounds(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutShell();
            if (titleBar != null) titleBar.Invalidate();
        }

        bool inShellLayout;

        /// <summary>
        /// Places the sidebar and the pages by hand (not with Dock): the pages start at the sidebar's settled width, and the
        /// sidebar sits above them, so it can glide wider or narrower over the pages without reflowing them every frame.
        /// </summary>
        void LayoutShell()
        {
            if (sidebar == null || content == null || inShellLayout) return;
            inShellLayout = true;
            try
            {
                Size cs = ClientSize;
                // Not sidebar.Visible: before the window is first shown every control reports Visible = false, which laid the
                // pages out without the sidebar (and gave it no height) until the next resize. Only full screen hides it.
                bool showSidebar = !fullscreen;
                int left = showSidebar ? sidebar.SettleWidth : 0;
                if (showSidebar) sidebar.SetBounds(0, 0, sidebar.Width, cs.Height);
                content.SetBounds(left, 0, Math.Max(0, cs.Width - left), cs.Height);
            }
            finally { inShellLayout = false; }
        }

        public void BeginWindowDrag()
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        }

        public void ToggleMaximize() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
        public void MinimizeWindow() { WindowState = FormWindowState.Minimized; }
        public void CloseWindow() { Close(); }
        public bool IsMaximized { get { return IsHandleCreated && IsZoomed(Handle); } }

        // ================================================================== lifecycle

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Dictionary<string, string> s = LoadSettings();
            try { library.Load(); }
            catch (Exception ex) { SetStatus("Could not read the library: " + ex.Message, true); }
            foreach (TrackInfo t in library.Songs) if (!t.Probed) probeQueue.Add(t);
            RefreshLibraryViews();
            RestoreQueue();

            string page;
            NavPage start = library.Songs.Count == 0 ? NavPage.Songs : NavPage.NowPlaying;
            if (s.TryGetValue("page", out page)) { try { start = (NavPage)Enum.Parse(typeof(NavPage), page); } catch { } }
            if (start == NavPage.NowPlaying && nowPlaying == null && library.Songs.Count > 0) start = NavPage.Songs;
            Navigate(start);

            if (!FfmpegTools.Locate())
                SetStatus("ffmpeg.exe / ffprobe.exe not found. Put them next to SoDaPlayer.exe or on PATH.", true);
            else
                SetStatus(library.Songs.Count + " songs in library.", false);

            ScanFolders(false);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (startupArgs != null && startupArgs.Length > 0) AddToLibrary(startupArgs, true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            engine.Stop();
            SaveSettings();
            SaveQueue();
            SaveLibrary();
            probeQueue.CompleteAdding();
            if (endpointVolume != null) endpointVolume.Dispose();
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool typing = ActiveControl is ComboBox || ActiveControl is ListBox || ActiveControl is TextBox;
            if (keyData == Keys.Space && !typing) { PlayPause(); return true; }
            // Media keys while focused, when another program holds the global hotkeys (otherwise they arrive as WM_HOTKEY).
            if (keyData == Keys.MediaPlayPause) { MediaCommand(0); return true; }
            if (keyData == Keys.MediaNextTrack) { MediaCommand(1); return true; }
            if (keyData == Keys.MediaPreviousTrack) { MediaCommand(2); return true; }
            if (keyData == Keys.MediaStop) { MediaCommand(3); return true; }
            if (keyData == (Keys.Control | Keys.O)) { AddFilesDialog(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { FocusSearch(); return true; }
            if (keyData == (Keys.Control | Keys.Right)) { Next(); return true; }
            if (keyData == (Keys.Control | Keys.Left)) { Previous(); return true; }
            if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
            if (keyData == Keys.Escape && nowView.Visible && nowView.SignalPathOpen) { nowView.CloseSignalPath(); return true; }
            if (keyData == Keys.Escape && fullscreen) { ToggleFullscreen(); return true; }
            if (keyData == Keys.Escape && detailVisible) { Navigate(detailParent); return true; }
            if (keyData == Keys.L && !typing && !settingsView.Focused && !eqView.Focused)
            {
                if (!nowView.Visible) Navigate(NavPage.NowPlaying);
                nowView.ToggleLyrics();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void UI(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        void SetStatus(string text, bool error)
        {
            statusText = text ?? "";
            statusError = error;
            sidebar.Invalidate();
            nowView.Invalidate();
        }

        void OnTick()
        {
            if (Interlocked.Exchange(ref probedCount, 0) > 0) MarkLibraryChanged();
            // While tags are still being read, refresh the views at most every 1.5 s; afterwards quickly.
            int quiet = probeQueue.Count > 0 ? 1500 : 300;
            if (libraryDirty && unchecked(Environment.TickCount - lastLibraryChange) > quiet) RefreshLibraryViews();
            if (librarySaveDirty && unchecked(Environment.TickCount - lastLibrarySave) > 10000) SaveLibrary();
            CheckSettingsSave();
            CheckSleepTimer();
            // A control painted while the window had no size (see Backdrop.MissedPaint): repaint everything now that it has one.
            if (backdrop.MissedPaint && IsHandleCreated && !IsIconic(Handle) && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                backdrop.MissedPaint = false;
                Invalidate(true);
            }
            SyncDeviceVolume();

            bool art = Interlocked.Exchange(ref artPending, 0) == 1;
            bool backdropChanged = backdrop.SetSource(BackdropImage(nowPlaying));
            if (art || backdropChanged) { InvalidateVisiblePage(); mini.Invalidate(); titleBar.Invalidate(); if (backdrop.BehindSidebar) sidebar.Invalidate(); }

            if (nowView.Visible) nowView.UpdateState();
            if (mini.Visible) mini.UpdateState();
            if (IsPlaying)
            {
                // Only the small "now playing" bars animate; repaint just those, not whole lists.
                sidebar.InvalidateNowPlaying();
                if (songsPage.Visible) songList.InvalidateNowPlaying();
                if (detailPage.Visible) detailList.InvalidateNowPlaying();
            }
        }

        void InvalidateVisiblePage()
        {
            foreach (Control c in pages.Controls)
                if (c.Visible) { c.Invalidate(true); }
        }

        // ================================================================== navigation

        NavPage pageBeforeNowPlaying = NavPage.Songs;

        public void LeaveNowPlaying()
        {
            if (fullscreen) ToggleFullscreen();
            Navigate(pageBeforeNowPlaying);
        }

        void Navigate(NavPage p)
        {
            if (p != NavPage.NowPlaying) pageBeforeNowPlaying = p;
            detailVisible = false;
            currentNav = p;
            ShowPages();
        }

        void ShowPages()
        {
            Control target = detailVisible ? detailPage
                : currentNav == NavPage.NowPlaying ? (Control)nowView
                : currentNav == NavPage.Albums ? albumsPage
                : currentNav == NavPage.Artists ? artistsPage
                : currentNav == NavPage.Songs ? (Control)songsPage
                : currentNav == NavPage.Folders ? (Control)foldersPage
                : currentNav == NavPage.Playlists ? (Control)playlistsPage
                : currentNav == NavPage.Equalizer ? (Control)eqView
                : settingsView;
            sidebar.Selected = detailVisible ? detailParent : currentNav;
            target.Visible = true;
            target.BringToFront();
            foreach (Control c in pages.Controls) if (c != target) c.Visible = false;
            bool np = target == nowView;
            onNowPlaying = np;
            mini.Visible = !np;
            UpdateBackdropFlags();
            titleBar.Invalidate();
            sidebar.Invalidate();
            if (target == settingsView) RefreshSettingsInfo();
            if (target == eqView) eqView.Invalidate();
            if (target == songsPage) songList.Focus();
        }

        bool onNowPlaying;

        /// <summary>Now Playing and frosted glass both spread the blurred backdrop over the whole window, sidebar and title bar included.</summary>
        void UpdateBackdropFlags()
        {
            bool full = onNowPlaying || Theme.Glass;
            backdrop.BehindSidebar = full;
            titleBar.UseBackdrop = full;
        }

        // ---- frosted glass

        int glassSource;                       // 0 = album art, 1 = desktop wallpaper
        static Image wallpaperImage;
        static bool wallpaperLoaded;

        /// <summary>What the blurred backdrop is made from: the album art, or (glass with the wallpaper option) the desktop wallpaper.</summary>
        Image BackdropImage(TrackInfo t)
        {
            if (Theme.Glass && glassSource == 1)
            {
                if (!wallpaperLoaded) { wallpaperLoaded = true; wallpaperImage = LoadWallpaper(); }
                if (wallpaperImage != null) return wallpaperImage;
            }
            return ArtCache.GetLarge(t);
        }

        /// <summary>The current desktop wallpaper, scaled down (it is only used blurred). Null for a plain colour or when unreadable.</summary>
        static Image LoadWallpaper()
        {
            try
            {
                string path = null;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    if (k != null) path = k.GetValue("WallPaper") as string;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image src = Image.FromStream(fs, false, false))
                {
                    double s = Math.Min(1.0, 1280.0 / Math.Max(src.Width, src.Height));
                    return new Bitmap(src, new Size(Math.Max(1, (int)(src.Width * s)), Math.Max(1, (int)(src.Height * s))));
                }
            }
            catch { return null; }
        }

        /// <summary>Applies the frosted-glass settings (on/off, source, transparency) to the whole window.</summary>
        void ApplyGlass()
        {
            wallpaperLoaded = false;            // re-read the wallpaper: the user may have changed it
            if (wallpaperImage != null) { wallpaperImage.Dispose(); wallpaperImage = null; }
            Theme.Apply(Theme.Mode, Theme.CustomColor);   // secondary text colour depends on glass
            backdrop.Invalidate();
            backdrop.SetSource(BackdropImage(nowPlaying));
            UpdateBackdropFlags();
            Invalidate(true);
        }

        void OpenAlbum(AlbumInfo al, NavPage parent, bool refreshOnly)
        {
            if (al == null) return;
            detailAlbum = al; detailArtist = null; detailPlaylist = null; detailParent = parent;
            detailHeader.PlaylistActions = false;
            detailList.EmptyText = null;
            detailHeader.Kind = "ALBUM";
            detailHeader.Title = al.Title;
            detailHeader.Meta = al.Artist + (string.IsNullOrEmpty(al.Year) ? "" : "  ·  " + al.Year) + "  ·  " + Plural(al.Tracks.Count, "song") +
                                "  ·  " + Theme.Time(al.Duration);
            detailHeader.ArtTrack = al.ArtTrack;
            detailHeader.Circle = false;
            detailList.ShowTrackNumbers = true;
            detailList.ShowAlbum = false;
            detailList.SetItems(new List<TrackInfo>(al.Tracks), refreshOnly);
            detailHeader.Invalidate();
            if (!refreshOnly) { detailVisible = true; ShowPages(); }
        }

        void OpenArtist(ArtistInfo ar, bool refreshOnly)
        {
            if (ar == null) return;
            detailArtist = ar; detailAlbum = null; detailPlaylist = null; detailParent = NavPage.Artists;
            detailHeader.PlaylistActions = false;
            detailList.EmptyText = null;
            detailHeader.Kind = "ARTIST";
            detailHeader.Title = ar.Name;
            detailHeader.Meta = Plural(ar.Albums.Count, "album") + "  ·  " + Plural(ar.Tracks.Count, "song");
            detailHeader.ArtTrack = ar.ArtTrack;
            detailHeader.Circle = true;
            detailList.ShowTrackNumbers = false;
            detailList.ShowAlbum = true;
            detailList.SetItems(new List<TrackInfo>(ar.Tracks), refreshOnly);
            detailHeader.Invalidate();
            if (!refreshOnly) { detailVisible = true; ShowPages(); }
        }

        static string Plural(int n, string word) { return n + " " + word + (n == 1 ? "" : "s"); }

        // ================================================================== playlists

        string PlaylistMeta(Playlist pl, List<TrackInfo> songs)
        {
            double d = 0;
            foreach (TrackInfo t in songs) d += t.Duration;
            string meta = Plural(songs.Count, "song") + (songs.Count > 0 ? "  ·  " + Theme.Time(d) : "");
            int missing = pl.Paths.Count - songs.Count;
            return missing > 0 ? meta + "  ·  " + missing + " not in library" : meta;
        }

        /// <summary>Rebuilds the Playlists page (and the open playlist, if any) from the store.</summary>
        void RefreshPlaylists()
        {
            var tiles = new List<TileItem>();
            foreach (Playlist pl in playlists.Items)
            {
                List<TrackInfo> songs = pl.Resolve(library);
                tiles.Add(new TileItem { Title = pl.Name, Subtitle = PlaylistMeta(pl, songs), ArtTrack = songs.Count > 0 ? songs[0] : null, Tag = pl });
            }
            playlistGrid.SetItems(tiles, true);
            playlistsHeader.Subtitle = Plural(playlists.Items.Count, "playlist");
            playlistsHeader.Invalidate();
            if (detailPlaylist != null)
            {
                if (playlists.Items.Contains(detailPlaylist)) OpenPlaylist(detailPlaylist, true);
                else if (detailVisible) Navigate(NavPage.Playlists);
            }
        }

        void OpenPlaylist(Playlist pl, bool refreshOnly)
        {
            if (pl == null) return;
            detailPlaylist = pl; detailAlbum = null; detailArtist = null; detailParent = NavPage.Playlists;
            List<TrackInfo> songs = pl.Resolve(library);
            detailHeader.PlaylistActions = true;
            detailHeader.Kind = "PLAYLIST";
            detailHeader.Title = pl.Name;
            detailHeader.Meta = PlaylistMeta(pl, songs);
            detailHeader.ArtTrack = songs.Count > 0 ? songs[0] : null;
            detailHeader.Circle = false;
            detailList.ShowTrackNumbers = false;   // 1, 2, 3… in playlist order
            detailList.ShowAlbum = true;
            detailList.EmptyText = "This playlist is empty — right-click songs, albums or the queue and choose “Add to playlist”.";
            detailList.SetItems(songs, refreshOnly);
            detailHeader.Invalidate();
            if (!refreshOnly) { detailVisible = true; ShowPages(); }
        }

        /// <summary>
        /// Adds an "Add to playlist" sub-menu: "New playlist…" plus one entry per playlist. <paramref name="songs"/> is asked for
        /// when an entry is chosen, so it reflects the selection at that moment.
        /// </summary>
        void AddToPlaylistMenu(ContextMenuStrip m, Func<List<TrackInfo>> songs)
        {
            ToolStripMenuItem sub = Theme.SubMenu(m, "Add to playlist", Glyphs.Playlist);
            var dd = (ToolStripDropDownMenu)sub.DropDown;
            Theme.MenuItem(dd, "New playlist…", Glyphs.Add, delegate { NewPlaylist(songs()); });
            if (playlists.Items.Count > 0) dd.Items.Add(new ToolStripSeparator());
            foreach (Playlist pl in playlists.Items)
            {
                Playlist p = pl;
                Theme.MenuItem(dd, p.Name, Glyphs.Playlist, delegate { AddToPlaylist(p, songs()); });
            }
        }

        void AddToPlaylist(Playlist pl, List<TrackInfo> songs)
        {
            if (pl == null || songs == null || songs.Count == 0) return;
            int added = playlists.AddSongs(pl, songs);
            RefreshPlaylists();
            if (added == 0) SetStatus((songs.Count == 1 ? "That song is" : "Those songs are") + " already in “" + pl.Name + "”.", false);
            else SetStatus("Added " + Plural(added, "song") + " to “" + pl.Name + "”" +
                           (added < songs.Count ? " (" + (songs.Count - added) + " already there)." : "."), false);
        }

        /// <summary>Asks for a name and creates a playlist, optionally filled with <paramref name="songs"/>.</summary>
        void NewPlaylist(List<TrackInfo> songs)
        {
            string suggestion = "New playlist";
            if (songs != null && songs.Count > 0)
            {
                string key = Library.AlbumGroupKey(songs[0]);
                if (songs.TrueForAll(s => Library.AlbumGroupKey(s) == key)) suggestion = Library.AlbumTitle(songs[0]);
            }
            string name = PromptForm.Ask(this, "New playlist", "Name your playlist", playlists.UniqueName(suggestion), "Create");
            if (name == null) return;
            Playlist pl = playlists.Create(name, songs);
            RefreshPlaylists();
            int n = songs == null ? 0 : pl.Paths.Count;
            SetStatus("Created playlist “" + pl.Name + "”" + (n > 0 ? " with " + Plural(n, "song") : "") + ".", false);
        }

        void RenamePlaylist(Playlist pl)
        {
            if (pl == null) return;
            string name = PromptForm.Ask(this, "Rename playlist", "New name for “" + pl.Name + "”", pl.Name, "Rename");
            if (name == null || name == pl.Name) return;
            playlists.Rename(pl, name);
            RefreshPlaylists();
            SetStatus("Playlist renamed to “" + pl.Name + "”.", false);
        }

        void DeletePlaylist(Playlist pl)
        {
            if (pl == null) return;
            if (MessageBox.Show(this, "Delete the playlist “" + pl.Name + "”?\n\nThe songs stay in your library and on disk.", "Delete playlist",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            playlists.Delete(pl);
            if (detailPlaylist == pl) detailPlaylist = null;
            RefreshPlaylists();
            if (detailVisible && detailParent == NavPage.Playlists) Navigate(NavPage.Playlists);
            SetStatus("Deleted playlist “" + pl.Name + "”.", false);
        }

        public void SaveQueueAsPlaylist()
        {
            List<TrackInfo> q = QueueSnapshot();
            if (q.Count == 0) { SetStatus("The queue is empty.", false); return; }
            NewPlaylist(q);
        }

        void ShowPlaylistMenu(Playlist pl, Control owner, Point p)
        {
            if (pl == null) return;
            List<TrackInfo> songs = pl.Resolve(library);
            ContextMenuStrip m = Theme.Menu();
            Theme.MenuItem(m, "Play", Glyphs.PlayOutline, delegate { PlayList(songs, 0, false); }).Enabled = songs.Count > 0;
            Theme.MenuItem(m, "Shuffle", Glyphs.Shuffle, delegate { PlayList(songs, 0, true); }).Enabled = songs.Count > 0;
            Theme.MenuItem(m, "Add to queue", Glyphs.PlaylistAdd, delegate
            {
                lock (queueLock) queue.AddRange(songs);
                sidebar.RefreshQueue();
                SetStatus("Added playlist “" + pl.Name + "” to the queue.", false);
            }).Enabled = songs.Count > 0;
            m.Items.Add(new ToolStripSeparator());
            Theme.MenuItem(m, "Rename…", Glyphs.Pencil, delegate { RenamePlaylist(pl); });
            Theme.MenuItem(m, "Delete…", Glyphs.Trash, delegate { DeletePlaylist(pl); });
            m.Show(owner, p);
        }

        void ShowArtistMenu(ArtistInfo ar, Control owner, Point p)
        {
            if (ar == null || ar.Tracks.Count == 0) return;
            ContextMenuStrip m = Theme.Menu();
            Theme.MenuItem(m, "Play", Glyphs.PlayOutline, delegate { PlayList(ar.Tracks, 0, false); });
            Theme.MenuItem(m, "Shuffle", Glyphs.Shuffle, delegate { PlayList(ar.Tracks, 0, true); });
            Theme.MenuItem(m, "Add to queue", Glyphs.PlaylistAdd, delegate
            {
                lock (queueLock) queue.AddRange(ar.Tracks);
                sidebar.RefreshQueue();
                SetStatus("Added " + Plural(ar.Tracks.Count, "song") + " by " + ar.Name + " to the queue.", false);
            });
            AddToPlaylistMenu(m, delegate { return new List<TrackInfo>(ar.Tracks); });
            m.Show(owner, p);
        }

        // ================================================================== library

        void MarkLibraryChanged()
        {
            libraryDirty = true;
            librarySaveDirty = true;
            lastLibraryChange = Environment.TickCount;
        }

        bool refreshRunning, refreshAgain;

        /// <summary>
        /// Rebuilds albums / artists / sorted songs on a worker thread (it used to run on the UI thread and stall it with big
        /// libraries), then swaps the results in. Requests that arrive meanwhile are coalesced into one follow-up refresh.
        /// </summary>
        void RefreshLibraryViews()
        {
            libraryDirty = false;
            if (refreshRunning) { refreshAgain = true; return; }
            refreshRunning = true;
            var snapshot = new List<TrackInfo>(library.Songs);
            var folderSnapshot = new List<string>(library.Folders);
            SongSort sort = songSort;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<AlbumInfo> albums = null; List<ArtistInfo> artists = null; List<TrackInfo> sorted = null;
                List<FolderNode> tree = null;
                var albumTiles = new List<TileItem>(); var artistTiles = new List<TileItem>();
                try
                {
                    Library.BuildIndex(snapshot, out albums, out artists);
                    tree = FolderNode.Build(snapshot, folderSnapshot);
                    sorted = Library.Sort(snapshot, sort);
                    foreach (AlbumInfo al in albums)
                        albumTiles.Add(new TileItem { Title = al.Title, Subtitle = al.Artist + (string.IsNullOrEmpty(al.Year) ? "" : "  ·  " + al.Year), ArtTrack = al.ArtTrack, Tag = al });
                    foreach (ArtistInfo ar in artists)
                        artistTiles.Add(new TileItem { Title = ar.Name, Subtitle = Plural(ar.Albums.Count, "album") + "  ·  " + Plural(ar.Tracks.Count, "song"), ArtTrack = ar.ArtTrack, Tag = ar });
                }
                catch { albums = null; }
                UI(delegate
                {
                    refreshRunning = false;
                    if (albums != null) ApplyLibraryRefresh(albums, artists, sorted, sort, albumTiles, artistTiles);
                    if (tree != null) { folderRoots = tree; RefreshFolderView(); }
                    if (refreshAgain) { refreshAgain = false; RefreshLibraryViews(); }
                });
            });
        }

        void ApplyLibraryRefresh(List<AlbumInfo> albums, List<ArtistInfo> artists, List<TrackInfo> sorted, SongSort sort,
                                 List<TileItem> albumTiles, List<TileItem> artistTiles)
        {
            long t0 = Stopwatch.GetTimestamp();
            library.Albums = albums;
            library.Artists = artists;
            if (sort == songSort)
            {
                songAll = sorted;
                FilterSongs(true);
            }
            else RefreshSongs(true);

            albumTilesAll = albumTiles;
            artistTilesAll = artistTiles;
            FilterAlbums(true);
            FilterArtists(true);

            if (detailAlbum != null)
            {
                AlbumInfo found = library.Albums.Find(delegate(AlbumInfo a) { return a.Key == detailAlbum.Key; });
                if (found != null) OpenAlbum(found, detailParent, true);
            }
            else if (detailArtist != null)
            {
                ArtistInfo found = library.Artists.Find(delegate(ArtistInfo a) { return Library.CompareText(a.Name, detailArtist.Name) == 0; });
                if (found != null) OpenArtist(found, true);
            }
            RefreshPlaylists();   // playlists resolve their songs through the library
            sidebar.RefreshQueue();
            if (settingsView.Visible) RefreshSettingsInfo();
            if (Perf.Enabled) Perf.Record("LibraryApply", t0);
        }

        void RefreshSongs(bool keepScroll)
        {
            songAll = library.SortedSongs(songSort);
            FilterSongs(keepScroll);
        }

        // ------------------------------------------------------------------ search

        static readonly CompareInfo SearchCompare = CultureInfo.InvariantCulture.CompareInfo;

        /// <summary>The query split into words; every word must appear somewhere (case- and accent-insensitive: "beyonce" finds "Beyoncé").</summary>
        static string[] SearchWords(string query)
        {
            return (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static bool MatchesAll(string[] words, params string[] fields)
        {
            foreach (string w in words)
            {
                bool found = false;
                foreach (string f in fields)
                    if (!string.IsNullOrEmpty(f) && SearchCompare.IndexOf(f, w, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth) >= 0)
                    { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>Applies the Songs search box to the sorted song list. "Play all" / "Shuffle" then play the results.</summary>
        void FilterSongs(bool keepScroll)
        {
            string[] words = SearchWords(songsHeader.SearchText);
            if (words.Length == 0) songItems = songAll;
            else
            {
                songItems = songAll.FindAll(delegate(TrackInfo t)
                {
                    return MatchesAll(words, t.DisplayTitle, t.Artist, t.AlbumArtist, t.Album, Path.GetFileNameWithoutExtension(t.FilePath));
                });
            }
            songList.EmptyText = words.Length == 0 ? null : "No songs match “" + songsHeader.SearchText.Trim() + "”";
            songList.SetItems(songItems, keepScroll);
            UpdateSongsSubtitle();
        }

        void FilterAlbums(bool keepScroll = false)
        {
            string[] words = SearchWords(albumsHeader.SearchText);
            List<TileItem> shown = words.Length == 0 ? albumTilesAll : albumTilesAll.FindAll(delegate(TileItem it)
            {
                var al = (AlbumInfo)it.Tag;
                return MatchesAll(words, al.Title, al.Artist, al.Year);
            });
            albumGrid.EmptyText = words.Length == 0 ? "No albums yet — add a music folder from the Songs page."
                                                    : "No albums match “" + albumsHeader.SearchText.Trim() + "”";
            albumGrid.SetItems(shown, keepScroll);
            albumsHeader.Subtitle = words.Length == 0 ? Plural(albumTilesAll.Count, "album") : shown.Count + " of " + Plural(albumTilesAll.Count, "album");
            albumsHeader.Invalidate();
        }

        void FilterArtists(bool keepScroll = false)
        {
            string[] words = SearchWords(artistsHeader.SearchText);
            List<TileItem> shown = words.Length == 0 ? artistTilesAll : artistTilesAll.FindAll(delegate(TileItem it)
            {
                return MatchesAll(words, ((ArtistInfo)it.Tag).Name);
            });
            artistGrid.EmptyText = words.Length == 0 ? "No artists yet — add a music folder from the Songs page."
                                                     : "No artists match “" + artistsHeader.SearchText.Trim() + "”";
            artistGrid.SetItems(shown, keepScroll);
            artistsHeader.Subtitle = words.Length == 0 ? Plural(artistTilesAll.Count, "artist") : shown.Count + " of " + Plural(artistTilesAll.Count, "artist");
            artistsHeader.Invalidate();
        }

        /// <summary>Ctrl+F: the search box of the current library page (Songs when on another page).</summary>
        void FocusSearch()
        {
            if (detailVisible || (currentNav != NavPage.Albums && currentNav != NavPage.Artists && currentNav != NavPage.Songs)) Navigate(NavPage.Songs);
            PageHeader h = currentNav == NavPage.Albums ? albumsHeader : currentNav == NavPage.Artists ? artistsHeader : songsHeader;
            h.FocusSearch();
        }

        string SortButtonText() { return "Sort: " + Library.SortLabel(songSort); }

        void ShowSortMenu()
        {
            ContextMenuStrip m = Theme.Menu();
            m.ShowCheckMargin = true;
            m.ShowImageMargin = false;   // check marks only: no empty icon column
            foreach (SongSort s in new[] { SongSort.NameAsc, SongSort.NameDesc, SongSort.DateAddedNewest, SongSort.DateAddedOldest })
            {
                SongSort sel = s;
                var item = new ToolStripMenuItem(Library.SortLabel(s)) { Checked = s == songSort };
                item.Click += delegate
                {
                    songSort = sel;
                    sortButton.Label = SortButtonText();
                    songsHeader.LayoutButtons();
                    RefreshSongs(false);
                };
                if (s == SongSort.DateAddedNewest) m.Items.Add(new ToolStripSeparator());
                m.Items.Add(item);
            }
            m.Show(songsHeader, new Point(sortButton.Bounds.X, sortButton.Bounds.Bottom + Theme.S(4)));
        }

        void UpdateSongsSubtitle()
        {
            int n = songList.SelectionCount;
            songsHeader.Subtitle = (songItems.Count == songAll.Count ? Plural(songAll.Count, "song") : songItems.Count + " of " + Plural(songAll.Count, "song")) +
                                   (n > 1 ? "  ·  " + n + " selected" : "");
            songsHeader.Invalidate();
        }

        // ------------------------------------------------------------------ Folders page

        HitPanel foldersPage;
        PageHeader foldersHeader;
        FolderListView folderList;
        SongListView folderSongs;
        CanvasButton folderUpBtn;
        List<FolderNode> folderRoots = new List<FolderNode>();
        FolderNode currentFolder;   // null = the list of music folders

        void BuildFoldersPage()
        {
            foldersHeader = new PageHeader { Dock = DockStyle.Top, Title = "Folders" };
            foldersHeader.AddRight(Glyphs.Play, "Play", BtnStyle.AccentPill, "Play everything in this folder", delegate { PlayFolder(currentFolder, false); });
            foldersHeader.AddRight(Glyphs.Shuffle, "Shuffle", BtnStyle.Pill, null, delegate { PlayFolder(currentFolder, true); });
            folderUpBtn = foldersHeader.AddRight(Glyphs.Back, "Up", BtnStyle.Pill, "Parent folder (Backspace)", FolderUp);
            folderList = new FolderListView { Dock = DockStyle.Top };
            folderList.Opened += ShowFolder;
            folderList.PlayRequested += delegate(FolderNode n) { PlayFolder(n, false); };
            folderList.ContextRequested += delegate(FolderNode n, Point p) { ShowFolderMenu(n, folderList, p); };
            folderSongs = new SongListView(this) { Dock = DockStyle.Fill, ShowAlbum = true, EmptyText = "No songs directly in this folder." };
            folderSongs.Activated += delegate(int i) { PlayList(folderSongs.Items, i, false); };
            folderSongs.ContextRequested += delegate(int i, Point p) { ShowSongMenu(folderSongs, i, p); };
            foldersPage = new HitPanel { Dock = DockStyle.Fill, Visible = false };
            foldersPage.Controls.Add(folderSongs);
            foldersPage.Controls.Add(folderList);
            foldersPage.Controls.Add(foldersHeader);
            foldersPage.Resize += delegate { LayoutFolderPage(); };
            folderList.KeyDown += FolderKeyDown;
            folderSongs.KeyDown += FolderKeyDown;
            ShowFolder(null);
        }

        void FolderKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Back && currentFolder != null) { FolderUp(); e.Handled = true; }
        }

        void FolderUp() { ShowFolder(currentFolder != null ? currentFolder.Parent : null); }

        /// <summary>Opens a folder (null = the music folders). Sub-folders are listed above the songs that sit directly in it.</summary>
        void ShowFolder(FolderNode n) { ShowFolder(n, false); }

        void ShowFolder(FolderNode n, bool keepScroll)
        {
            currentFolder = n;
            folderList.SetItems(n == null ? folderRoots : n.Children, keepScroll);
            folderSongs.SetItems(n == null ? new List<TrackInfo>() : new List<TrackInfo>(n.Songs), keepScroll);
            foldersHeader.Title = n == null ? "Folders" : n.Name;
            if (n == null)
            {
                int total = 0;
                foreach (FolderNode r in folderRoots) total += r.TotalSongs;
                foldersHeader.Subtitle = folderRoots.Count == 0 ? "No music folders yet — add one from the Songs page or Settings"
                                                                : Plural(folderRoots.Count, "music folder") + "  ·  " + Plural(total, "song");
            }
            else foldersHeader.Subtitle = n.Path + "  ·  " + Plural(n.TotalSongs, "song");
            folderUpBtn.Visible = n != null;
            foldersHeader.LayoutButtons();
            LayoutFolderPage();
        }

        /// <summary>Folder rows take the page when there are no songs here; otherwise up to 45% of it, above the songs.</summary>
        void LayoutFolderPage()
        {
            if (folderList == null) return;
            bool hasSongs = currentFolder != null && currentFolder.Songs.Count > 0;
            bool hasFolders = folderList.Count > 0 || !hasSongs;
            folderSongs.Visible = hasSongs;
            folderList.Visible = hasFolders;
            if (!hasSongs) folderList.Dock = DockStyle.Fill;
            else
            {
                folderList.Dock = DockStyle.Top;
                int avail = Math.Max(0, foldersPage.ClientSize.Height - foldersHeader.Height);
                folderList.Height = Math.Min(folderList.Count * folderList.RowH + Theme.S(12), (int)(avail * 0.45));
            }
        }

        /// <summary>After a library refresh: show the same folder in the new tree (or its nearest surviving parent).</summary>
        void RefreshFolderView()
        {
            FolderNode target = null;
            for (FolderNode n = currentFolder; n != null && target == null; n = n.Parent) target = FindFolder(folderRoots, n.Path);
            ShowFolder(target, true);
        }

        static FolderNode FindFolder(List<FolderNode> nodes, string path)
        {
            foreach (FolderNode n in nodes)
            {
                if (string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)) return n;
                if (path.StartsWith(n.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    FolderNode f = FindFolder(n.Children, path);
                    if (f != null) return f;
                }
            }
            return null;
        }

        void PlayFolder(FolderNode n, bool shuffle)
        {
            var list = new List<TrackInfo>();
            if (n != null) list = n.AllSongs();
            else foreach (FolderNode r in folderRoots) list.AddRange(r.AllSongs());
            if (list.Count > 0) PlayList(list, 0, shuffle);
        }

        void ShowFolderMenu(FolderNode n, Control owner, Point p)
        {
            ContextMenuStrip m = Theme.Menu();
            Theme.MenuItem(m, "Open", Glyphs.FolderPlain, delegate { ShowFolder(n); });
            Theme.MenuItem(m, "Play", Glyphs.PlayOutline, delegate { PlayFolder(n, false); });
            Theme.MenuItem(m, "Shuffle", Glyphs.Shuffle, delegate { PlayFolder(n, true); });
            Theme.MenuItem(m, "Add to queue", Glyphs.PlaylistAdd, delegate
            {
                List<TrackInfo> all = n.AllSongs();
                lock (queueLock) queue.AddRange(all);
                sidebar.RefreshQueue();
                SetStatus("Added " + Plural(all.Count, "song") + " from “" + n.Name + "” to the queue.", false);
            });
            AddToPlaylistMenu(m, delegate { return n.AllSongs(); });
            m.Items.Add(new ToolStripSeparator());
            Theme.MenuItem(m, "Show in File Explorer", Glyphs.ExternalLink, delegate
            {
                try { Process.Start("explorer.exe", "\"" + n.Path + "\""); } catch { }
            });
            m.Show(owner, p);
        }

        /// <summary>Right-click on an album tile.</summary>
        void ShowAlbumMenu(AlbumInfo al, Control owner, Point p)
        {
            if (al == null || al.Tracks.Count == 0) return;
            ContextMenuStrip m = Theme.Menu();
            Theme.MenuItem(m, "Play", Glyphs.PlayOutline, delegate { PlayList(al.Tracks, 0, false); });
            Theme.MenuItem(m, "Shuffle", Glyphs.Shuffle, delegate { PlayList(al.Tracks, 0, true); });
            Theme.MenuItem(m, "Add to queue", Glyphs.PlaylistAdd, delegate
            {
                lock (queueLock) queue.AddRange(al.Tracks);
                sidebar.RefreshQueue();
                SetStatus("Added “" + al.Title + "” to the queue.", false);
            });
            AddToPlaylistMenu(m, delegate { return new List<TrackInfo>(al.Tracks); });
            m.Items.Add(new ToolStripSeparator());
            Theme.MenuItem(m, "Find album art online…", Glyphs.Album, delegate { FindArtOnline(al.ArtTrack ?? al.Tracks[0]); });
            Theme.MenuItem(m, "Find animated artwork…", Glyphs.PlayOutline, delegate { FindAnimatedArt(al.ArtTrack ?? al.Tracks[0]); });
            Theme.MenuItem(m, "Edit metadata of " + Plural(al.Tracks.Count, "song") + "…", Glyphs.Adjust, delegate { EditMetadata(new List<TrackInfo>(al.Tracks)); });
            Theme.MenuItem(m, "Show in File Explorer", Glyphs.FolderPlain, delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + al.Tracks[0].FilePath + "\""); } catch { }
            });
            m.Show(owner, p);
        }

        /// <summary>Opens the cover picker for the album of <paramref name="t"/> and applies the chosen cover.</summary>
        void FindArtOnline(TrackInfo t)
        {
            if (t == null) return;
            string folder = Path.GetDirectoryName(t.FilePath);
            using (var dlg = new ArtPickerForm(OnlineArt.AlbumArtist(t), t.Album ?? Library.AlbumTitle(t), Path.GetFileName(folder)))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ResultImage == null) return;
                using (Image img = dlg.ResultImage)
                {
                    try { ArtCache.SetImage(t, img, true); }
                    catch (Exception ex) { SetStatus("Couldn’t store the cover: " + ex.Message, true); return; }
                }
                string saved = "";
                if (dlg.SaveToFolder && dlg.ResultBytes != null && !string.IsNullOrEmpty(folder))
                {
                    bool png = dlg.ResultBytes.Length > 4 && dlg.ResultBytes[0] == 0x89 && dlg.ResultBytes[1] == 0x50;
                    string file = Path.Combine(folder, png ? "cover.png" : "cover.jpg");
                    try { File.WriteAllBytes(file, dlg.ResultBytes); saved = " and saved as " + Path.GetFileName(file); }
                    catch (Exception ex) { SetStatus("Cover applied, but saving it in the folder failed: " + ex.Message, true); return; }
                }
                SetStatus("New cover for “" + Library.AlbumTitle(t) + "” applied" + saved + ".", false);
                InvalidateVisiblePage();
            }
        }

        /// <summary>Opens the animated artwork picker for the album of <paramref name="t"/> and applies the choice.</summary>
        void FindAnimatedArt(TrackInfo t)
        {
            if (t == null) return;
            string folder = Path.GetDirectoryName(t.FilePath);
            string album = t.Album ?? Library.AlbumTitle(t);
            try
            {
                using (var dlg = new AnimatedArtPickerForm(OnlineArt.AlbumArtist(t), album, Path.GetFileName(folder), AnimatedArt.IsDisabled(t)))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    nowView.StopAnimatedArt();   // its player holds the album's current video open
                    if (dlg.TurnOff)
                    {
                        AnimatedArt.DisableForAlbum(t);
                        SetStatus("Animated artwork turned off for “" + album + "”.", false);
                        return;
                    }
                    if (dlg.ResultFile == null) return;
                    string saved = "";
                    if (dlg.SaveToFolder && !string.IsNullOrEmpty(folder))
                    {
                        try { File.Copy(dlg.ResultFile, Path.Combine(folder, "cover.mp4"), true); saved = " and saved as cover.mp4"; }
                        catch (Exception ex) { SetStatus("Saving cover.mp4 in the folder failed: " + ex.Message, true); }
                    }
                    AnimatedArt.UseForAlbum(t, dlg.ResultFile);
                    SetStatus("New animated artwork for “" + album + "” applied" + saved + ".", false);
                }
            }
            catch (Exception ex) { SetStatus("Couldn’t apply the animated artwork: " + ex.Message, true); }
            finally { AnimatedArt.DeletePreviews(); }
        }

        /// <summary>Edits tags of the given songs (remuxed with ffmpeg, audio unchanged), then re-reads them into the library.</summary>
        void EditMetadata(List<TrackInfo> songs)
        {
            if (songs == null || songs.Count == 0) return;
            if (songs.Count > 500) { SetStatus("Select at most 500 songs to edit at once.", true); return; }
            List<string[]> values;
            Cursor = Cursors.WaitCursor;
            try { values = songs.ConvertAll(TagWriter.Read); }
            finally { Cursor = Cursors.Default; }
            Dictionary<string, string> changes;
            ArtChange art;
            string artFile;
            List<string> temps;
            Image currentArt = null;
            Cursor = Cursors.WaitCursor;
            try { currentArt = TagWriter.ReadEmbeddedArt(songs[0].FilePath); }
            finally { Cursor = Cursors.Default; }
            using (var dlg = new TagEditorForm(songs, values, currentArt))
            {
                bool ok = dlg.ShowDialog(this) == DialogResult.OK;
                temps = new List<string>(dlg.TempFiles);
                if (!ok || (dlg.Changes.Count == 0 && dlg.Art == ArtChange.Keep))
                {
                    foreach (string f in temps) try { File.Delete(f); } catch { }
                    if (currentArt != null) currentArt.Dispose();
                    return;
                }
                changes = new Dictionary<string, string>(dlg.Changes);
                art = dlg.Art;
                artFile = dlg.ArtFile;
            }
            if (currentArt != null) currentArt.Dispose();
            var oldArtKeys = new HashSet<string>(songs.ConvertAll(ArtCache.KeyFor));

            // The playing file (and a gapless pre-opened next one) is held open by the decoder: stop, write, then resume.
            TrackInfo current = engine.IsRunning ? engine.CurrentTrack : null;
            bool resume = current != null && songs.Contains(current) || engine.IsRunning && songs.Contains(NextTrackForEngine(engine.CurrentTrack));
            double resumeAt = engine.Position;
            bool wasPaused = engine.IsPaused;
            TrackInfo resumeTrack = engine.CurrentTrack;
            if (resume) engine.Stop();

            SetStatus("Saving tags of " + Plural(songs.Count, "song") + "…", false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                int ok = 0;
                string firstError = null;
                foreach (TrackInfo s in songs)
                {
                    // Songs whose format can't hold a picture (Ogg, WAV…) still get their tags.
                    ArtChange a = art == ArtChange.Replace && !TagWriter.CanEmbedArt(s.FilePath) ? ArtChange.Keep : art;
                    string err = TagWriter.Write(s.FilePath, changes, a, artFile);
                    if (err == null)
                    {
                        ok++;
                        s.ResetProbe();
                        FfmpegTools.Probe(s);
                        LyricsService.Forget(s);
                    }
                    else if (firstError == null) firstError = Path.GetFileName(s.FilePath) + ": " + err;
                }
                foreach (string f in temps) try { File.Delete(f); } catch { }
                // The cached cover (and any online/picked one) is re-derived: folder image, else the new embedded picture.
                if (art != ArtChange.Keep)
                {
                    foreach (TrackInfo s in songs) oldArtKeys.Add(ArtCache.KeyFor(s));
                    foreach (string key in oldArtKeys) ArtCache.Forget(key);
                }
                UI(delegate
                {
                    MarkLibraryChanged();
                    AnimatedArt.Reset();
                    if (art != ArtChange.Keep) backdrop.SetSource(null);   // picks up the new cover on the next tick
                    if (resume && resumeTrack != null)
                    {
                        PlayTrack(resumeTrack, resumeAt);
                        if (wasPaused) engine.Pause();
                    }
                    string what = changes.Count == 0 ? (art == ArtChange.Remove ? "Removed the album art from " : "Saved the album art in ") : "Saved tags of ";
                    if (firstError == null) SetStatus(what + Plural(ok, "song") + ".", false);
                    else SetStatus((ok > 0 ? "Saved " + Plural(ok, "song") + "; " : "") + "couldn’t save " + firstError, true);
                    InvalidateVisiblePage();
                });
            });
        }

        /// <summary>Context menu for one or more selected songs (Ctrl/Shift-click to select several).</summary>
        void ShowSongMenu(SongListView view, int index, Point p)
        {
            if (index < 0 || index >= view.Items.Count) return;
            List<TrackInfo> sel = view.SelectedItems;
            if (sel.Count == 0) sel.Add(view.Items[index]);
            TrackInfo t = sel.Count == 1 ? sel[0] : view.Items[index];
            List<TrackInfo> items = view.Items;
            bool many = sel.Count > 1;
            string what = many ? sel.Count + " songs" : "“" + t.DisplayTitle + "”";

            ContextMenuStrip m = Theme.Menu();
            if (many)
            {
                var header = new ToolStripMenuItem(sel.Count + " songs selected") { Enabled = false };
                m.Items.Add(header);
                m.Items.Add(new ToolStripSeparator());
            }
            Theme.MenuItem(m, many ? "Play selected" : "Play", Glyphs.PlayOutline, delegate
            {
                if (many) PlayList(sel, 0, false); else PlayList(items, items.IndexOf(t), false);
            });
            if (many) Theme.MenuItem(m, "Shuffle selected", Glyphs.Shuffle, delegate { PlayList(sel, 0, true); });
            Theme.MenuItem(m, "Play next", Glyphs.PlayNext, delegate
            {
                lock (queueLock)
                {
                    int i = queue.IndexOf(nowPlaying);
                    queue.InsertRange(i < 0 ? queue.Count : i + 1, sel);
                }
                sidebar.RefreshQueue();
                SetStatus(what + (many ? " will play next." : " will play next."), false);
            });
            Theme.MenuItem(m, "Add to queue", Glyphs.PlaylistAdd, delegate
            {
                lock (queueLock) queue.AddRange(sel);
                sidebar.RefreshQueue();
                SetStatus("Added " + what + " to the queue.", false);
            });
            AddToPlaylistMenu(m, delegate { return new List<TrackInfo>(sel); });
            if (view == detailList && detailPlaylist != null)
            {
                Playlist pl = detailPlaylist;
                Theme.MenuItem(m, many ? "Remove " + sel.Count + " songs from this playlist" : "Remove from this playlist", Glyphs.Close, delegate
                {
                    playlists.RemoveSongs(pl, sel);
                    RefreshPlaylists();
                    SetStatus("Removed " + what + " from “" + pl.Name + "”.", false);
                });
                if (!many)
                {
                    Theme.MenuItem(m, "Move up", Glyphs.ChevronUp, delegate { playlists.Move(pl, t, -1, library); RefreshPlaylists(); }).Enabled = items.IndexOf(t) > 0;
                    Theme.MenuItem(m, "Move down", Glyphs.ChevronDown, delegate { playlists.Move(pl, t, +1, library); RefreshPlaylists(); }).Enabled = items.IndexOf(t) < items.Count - 1;
                }
            }
            m.Items.Add(new ToolStripSeparator());
            string albumKey = Library.AlbumGroupKey(t);
            bool oneAlbum = sel.TrueForAll(x => Library.AlbumGroupKey(x) == albumKey);
            bool oneArtist = sel.TrueForAll(x => Library.CompareText(x.DisplayArtist, t.DisplayArtist) == 0);
            if (oneAlbum)
                Theme.MenuItem(m, "Go to album", Glyphs.Album, delegate
                {
                    OpenAlbum(library.Albums.Find(delegate(AlbumInfo a) { return a.Key == albumKey; }), NavPage.Albums, false);
                });
            if (oneArtist)
                Theme.MenuItem(m, "Go to artist", Glyphs.Artist, delegate
                {
                    OpenArtist(library.Artists.Find(delegate(ArtistInfo a) { return Library.CompareText(a.Name, t.DisplayArtist) == 0; }), false);
                });
            Theme.MenuItem(m, "Show in File Explorer", Glyphs.FolderPlain, delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + t.FilePath + "\""); } catch { }
            });
            m.Items.Add(new ToolStripSeparator());
            Theme.MenuItem(m, many ? "Edit metadata of " + sel.Count + " songs…" : "Edit metadata…", Glyphs.Adjust, delegate { EditMetadata(sel); });
            if (!many) Theme.MenuItem(m, "Search lyrics…", Glyphs.Lyrics, delegate { SearchLyrics(t); });
            if (oneAlbum) Theme.MenuItem(m, "Find album art online…", Glyphs.Album, delegate { FindArtOnline(t); });
            if (oneAlbum) Theme.MenuItem(m, "Find animated artwork…", Glyphs.PlayOutline, delegate { FindAnimatedArt(t); });
            if (many) Theme.MenuItem(m, "Select all", Glyphs.Check, delegate { view.SelectAll(); });
            m.Items.Add(new ToolStripSeparator());
            Theme.MenuItem(m, many ? "Remove " + sel.Count + " songs from library" : "Remove from library", Glyphs.Trash, delegate
            {
                if (many && MessageBox.Show(this, "Remove " + sel.Count + " songs from the library?\n\nNo files are deleted from disk.", "Remove songs",
                                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                foreach (TrackInfo x in sel) library.Remove(x);
                view.ClearSelection();
                MarkLibraryChanged();
                RefreshLibraryViews();
            });
            m.Show(view, p);
        }
        /// <summary>Opens the manual lyrics search for a song (LRCLIB): use the result, save it as .lrc, or embed it in the file.</summary>
        void SearchLyrics(TrackInfo t)
        {
            if (t == null) return;
            using (var f = new LyricsSearchForm(t))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.ResultMessage == null) return;
                SetStatus(f.ResultMessage, false);
                if (t == nowPlaying && !nowView.LyricsVisible) { if (!nowView.Visible) Navigate(NavPage.NowPlaying); nowView.ToggleLyrics(); }
            }
        }

        void ProbeWorker()
        {
            try
            {
                foreach (TrackInfo t in probeQueue.GetConsumingEnumerable())
                {
                    FfmpegTools.Probe(t);
                    Interlocked.Increment(ref probedCount);
                }
            }
            catch { }
        }

        static List<string> ExpandPath(string p)
        {
            var files = new List<string>();
            try
            {
                if (Directory.Exists(p))
                {
                    foreach (string f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        if (FfmpegTools.AudioExtensions.Contains(Path.GetExtension(f))) files.Add(f);
                    files.Sort(StringComparer.CurrentCultureIgnoreCase);
                    return files;
                }
                string ext = Path.GetExtension(p);
                if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    string baseDir = Path.GetDirectoryName(p);
                    foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        string line = raw.Trim().TrimStart('﻿');
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        string full = Path.IsPathRooted(line) ? line : Path.Combine(baseDir, line);
                        if (File.Exists(full)) files.Add(full);
                    }
                    return files;
                }
                if (File.Exists(p)) files.Add(p);
            }
            catch { }
            return files;
        }

        /// <summary>Adds files/folders/playlists to the library (date added = now). Folders become watched library folders.</summary>
        /// <summary>Scans the given files / folders / playlists on a worker thread (big folders used to freeze the window), then adds them.</summary>
        void AddToLibrary(IEnumerable<string> paths, bool play)
        {
            var list = new List<string>(paths);
            SetStatus("Scanning " + (list.Count == 1 ? Path.GetFileName(list[0].TrimEnd('\\')) : list.Count + " items") + "…", false);
            Cursor = Cursors.AppStarting;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new List<KeyValuePair<string, List<string>>>();
                foreach (string p in list) found.Add(new KeyValuePair<string, List<string>>(p, ExpandPath(p)));
                UI(delegate { Cursor = Cursors.Default; AddScanned(found, play); });
            });
        }

        void AddScanned(List<KeyValuePair<string, List<string>>> found, bool play)
        {
            var batch = new List<TrackInfo>();
            int added = 0;
            DateTime now = DateTime.UtcNow;
            foreach (var kv in found)
            {
                string p = kv.Key;
                if (Directory.Exists(p) && !library.Folders.Exists(delegate(string f) { return string.Equals(f, p, StringComparison.OrdinalIgnoreCase); }))
                    library.Folders.Add(p);
                foreach (string f in kv.Value)
                {
                    TrackInfo t = library.Add(f, now);
                    if (t != null) { added++; probeQueue.Add(t); }
                    else t = library.Get(f);
                    if (t != null) batch.Add(t);
                }
            }
            if (added > 0) MarkLibraryChanged();
            RefreshLibraryViews();
            SetStatus(added > 0 ? "Added " + Plural(added, "song") + " to the library." : "Those files are already in the library.", false);
            if (play && batch.Count > 0) PlayList(batch, 0, false);
        }

        void AddFolderDialog()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Add a music folder (sub-folders included)", ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) AddToLibrary(new[] { dlg.SelectedPath }, false);
            }
        }

        void AddFilesDialog()
        {
            var exts = new List<string>();
            foreach (string x in FfmpegTools.AudioExtensions) exts.Add("*" + x);
            using (var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Add audio files",
                Filter = "Audio files|" + string.Join(";", exts.ToArray()) + "|Playlists|*.m3u;*.m3u8|All files|*.*"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) AddToLibrary(dlg.FileNames, false);
            }
        }

        /// <summary>Background scan of the library folders: picks up new files and re-reads changed ones; a full rescan also drops missing files.</summary>
        void ScanFolders(bool full)
        {
            string[] folders = library.Folders.ToArray();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stamps = new List<KeyValuePair<TrackInfo, DateTime>>();
            foreach (TrackInfo t in library.Songs) { known.Add(t.FilePath); stamps.Add(new KeyValuePair<TrackInfo, DateTime>(t, t.FileModified)); }
            if (full) ArtCache.ForgetMissing();
            SetStatus(full ? "Rescanning library…" : statusText, statusError);

            ThreadPool.QueueUserWorkItem(delegate
            {
                var newFiles = new List<string>();
                var changed = new List<KeyValuePair<TrackInfo, DateTime>>();
                var missing = new List<TrackInfo>();
                foreach (string f in folders)
                    foreach (string file in ExpandPath(f))
                        if (known.Add(file)) newFiles.Add(file);
                foreach (var kv in stamps)
                {
                    try
                    {
                        var fi = new FileInfo(kv.Key.FilePath);
                        if (!fi.Exists) missing.Add(kv.Key);
                        else if (fi.LastWriteTimeUtc != kv.Value) changed.Add(new KeyValuePair<TrackInfo, DateTime>(kv.Key, fi.LastWriteTimeUtc));
                    }
                    catch { }
                }
                UI(delegate
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (string f in newFiles) { TrackInfo t = library.Add(f, now); if (t != null) probeQueue.Add(t); }
                    foreach (var kv in changed) { kv.Key.FileModified = kv.Value; kv.Key.ResetProbe(); probeQueue.Add(kv.Key); }
                    if (full) foreach (TrackInfo t in missing) library.Remove(t);
                    if (newFiles.Count > 0 || changed.Count > 0 || (full && missing.Count > 0)) MarkLibraryChanged();
                    if (full || newFiles.Count > 0)
                        SetStatus("Library scan: " + Plural(newFiles.Count, "new song") + (full ? ", " + missing.Count + " removed" : "") + ".", false);
                });
            });
        }

        void SaveLibrary()
        {
            librarySaveDirty = false;
            lastLibrarySave = Environment.TickCount;
            try { library.Save(); } catch { }
        }

        // ================================================================== queue & transport

        public List<TrackInfo> QueueSnapshot() { lock (queueLock) return new List<TrackInfo>(queue); }

        void PlayList(List<TrackInfo> list, int index, bool shuffle)
        {
            if (list == null || list.Count == 0 || index < 0 || index >= list.Count) return;
            TrackInfo start = list[index];
            var copy = new List<TrackInfo>();
            foreach (TrackInfo t in list) if (t.ProbeError == null || t == start) copy.Add(t);
            if (shuffle)
            {
                Shuffle(copy, 0);
                start = copy[0];
            }
            lock (queueLock) { queue.Clear(); queue.AddRange(copy); }
            sidebar.RefreshQueue();
            PlayTrack(start, 0);
            sidebar.ScrollToCurrent();
        }

        void Shuffle(List<TrackInfo> list, int from)
        {
            for (int i = list.Count - 1; i > from; i--)
            {
                int j = from + rng.Next(i - from + 1);
                TrackInfo tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        void PlayTrack(TrackInfo t, double start)
        {
            if (t == null) return;
            ApplyEngineSettings();
            SyncDeviceVolume();     // pick up a knob / volume-key change from the last 150 ms before guarding the level
            GuardDeviceVolume();
            nowPlaying = t;
            SetStatus("Opening " + SelectedDeviceName() +
                      (engine.ExclusiveMode ? " in exclusive mode…" : " in shared mode…"), false);
            engine.Play(t, start);
            backdrop.SetSource(BackdropImage(t));
            Invalidate(true);
        }

        void OnEngineTrackChanged(TrackInfo t)
        {
            GuardDeviceVolume();    // a gapless hand-over may have re-opened the device at a new sample rate
            nowPlaying = t;
            SetStatus("Playing " + t.DisplayTitle, false);
            backdrop.SetSource(BackdropImage(t));
            sidebar.RefreshQueue();
            sidebar.ScrollToCurrent();
            Invalidate(true);
        }

        // ------------------------------------------------------------------ sleep timer

        DateTime sleepAt = DateTime.MinValue;   // MinValue = no timed stop
        volatile bool sleepAfterSong;           // stop when the current song ends
        int sleepCustomMinutes = 20;
        const double SleepFadeSeconds = 12;
        static readonly int[] SleepChoices = { 15, 30, 45, 60, 90, 120 };

        public string SleepTimerText
        {
            get
            {
                if (sleepAfterSong) return "End of song";
                if (sleepAt == DateTime.MinValue) return null;
                TimeSpan left = sleepAt - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                return left.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)left.TotalHours, left.Minutes, left.Seconds)
                                            : string.Format("{0}:{1:00}", left.Minutes, left.Seconds);
            }
        }

        static string MinutesText(int m)
        {
            if (m < 60) return m + " min";
            return m % 60 == 0 ? (m / 60) + (m == 60 ? " hour" : " hours") : (m / 60) + " h " + (m % 60) + " min";
        }

        public void ShowSleepMenu(Control owner, Point p)
        {
            ContextMenuStrip m = Theme.Menu();
            string left = SleepTimerText;
            if (left != null)
            {
                Theme.MenuItem(m, sleepAfterSong ? "Stops at the end of this song" : "Stops in " + left, Glyphs.Moon, null).Enabled = false;
                Theme.MenuItem(m, "Turn off sleep timer", Glyphs.Close, delegate { CancelSleepTimer(true); });
                m.Items.Add(new ToolStripSeparator());
            }
            foreach (int min in SleepChoices)
            {
                int mm = min;
                Theme.MenuItem(m, MinutesText(min), Glyphs.Moon, delegate { SetSleepTimer(mm); });
            }
            Theme.MenuItem(m, "End of this song", Glyphs.Stop, delegate
            {
                sleepAt = DateTime.MinValue; engine.FadeGain = 1f;
                sleepAfterSong = true;
                SetStatus("Sleep timer: playback stops after this song.", false);
                nowView.Invalidate();
            });
            Theme.MenuItem(m, "Custom…", Glyphs.Pencil, delegate
            {
                string s = PromptForm.Ask(this, "Sleep timer", "Stop playback after how long?  Minutes (e.g. 25), or hours:minutes (e.g. 1:30).",
                                          sleepCustomMinutes >= 60 ? (sleepCustomMinutes / 60) + ":" + (sleepCustomMinutes % 60).ToString("00") : sleepCustomMinutes.ToString(), "Start");
                if (s == null) return;
                int minutes = ParseSleepMinutes(s);
                if (minutes <= 0) { SetStatus("Sleep timer: enter minutes (1–1440) or hours:minutes, e.g. 45 or 1:30.", true); return; }
                sleepCustomMinutes = minutes;
                SetSleepTimer(minutes);
            });
            m.Show(owner, p);
        }

        static int ParseSleepMinutes(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant().Replace("min", "").Replace("m", "").Trim();
            int h, m;
            string[] parts = s.Split(':', '.');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out h) && int.TryParse(parts[1].Trim(), out m) && m < 60) m += h * 60;
            else if (!int.TryParse(s, out m)) return -1;
            return m >= 1 && m <= 1440 ? m : -1;
        }

        void SetSleepTimer(int minutes)
        {
            sleepAfterSong = false;
            engine.FadeGain = 1f;
            sleepAt = DateTime.Now.AddMinutes(minutes);
            SetStatus("Sleep timer: playback stops in " + MinutesText(minutes) + " (at " + sleepAt.ToString("HH:mm") + ").", false);
            nowView.Invalidate();
        }

        void CancelSleepTimer(bool notify)
        {
            sleepAt = DateTime.MinValue;
            sleepAfterSong = false;
            engine.FadeGain = 1f;
            if (notify) SetStatus("Sleep timer turned off.", false);
            nowView.Invalidate();
        }

        /// <summary>From the UI tick: fades the volume out over the last 12 s, then pauses playback.</summary>
        void CheckSleepTimer()
        {
            if (sleepAt == DateTime.MinValue) return;
            double left = (sleepAt - DateTime.Now).TotalSeconds;
            if (left <= 0)
            {
                bool wasPlaying = IsPlaying;
                if (engine.IsRunning && !engine.IsPaused) engine.Pause();
                sleepAt = DateTime.MinValue;
                engine.FadeGain = 1f;   // paused: the stream is silence now, so the level can go back for the next play
                SetStatus(wasPlaying ? "Sleep timer: playback paused." : "Sleep timer finished.", false);
                nowView.Invalidate();
                return;
            }
            if (left < SleepFadeSeconds && IsPlaying)
            {
                double f = left / SleepFadeSeconds;
                engine.FadeGain = (float)(f * f);   // roughly linear in loudness
            }
        }

        TrackInfo NextTrackForEngine(TrackInfo current)
        {
            if (sleepAfterSong) return null;   // sleep timer "end of this song": let playback finish here
            lock (queueLock)
            {
                if (queue.Count == 0) return null;
                int i = queue.IndexOf(current);
                if (repeatMode == 2 && i >= 0) return current;
                if (i < 0) return null;
                if (i + 1 < queue.Count) return queue[i + 1];
                return repeatMode == 1 ? queue[0] : null;
            }
        }

        public void PlayPause()
        {
            if (engine.IsRunning)
            {
                if (engine.IsPaused) engine.Resume(); else engine.Pause();
                // Show the new state right away instead of on the next 150 ms tick.
                if (nowView.Visible) nowView.UpdateState();
                if (mini.Visible) mini.UpdateState();
                return;
            }
            if (nowPlaying != null) { PlayTrack(nowPlaying, 0); return; }
            if (songItems.Count > 0) PlayList(songItems, 0, false);
        }

        public void StopPlayback()
        {
            engine.Stop();
            SetStatus("Stopped.", false);
            Invalidate(true);
        }

        public void Next()
        {
            TrackInfo t = null;
            lock (queueLock)
            {
                int i = queue.IndexOf(nowPlaying);
                if (i + 1 < queue.Count) t = queue[i + 1];
                else if (repeatMode == 1 && queue.Count > 0) t = queue[0];
            }
            if (t != null) PlayTrack(t, 0);
        }

        public void Previous()
        {
            if (engine.IsRunning && engine.Position > 3) { engine.Seek(0); return; }
            TrackInfo t = null;
            lock (queueLock)
            {
                int i = queue.IndexOf(nowPlaying);
                if (i > 0) t = queue[i - 1];
                else if (i == 0) t = queue[0];
            }
            if (t != null) PlayTrack(t, 0);
        }

        public void SeekSeconds(double seconds)
        {
            if (engine.IsRunning) engine.Seek(Math.Max(0, seconds));
        }

        public void BackdropChanged()
        {
            titleBar.Invalidate(); titleBar.Update();   // repaint now, in step with Now Playing
            if (backdrop.BehindSidebar) { sidebar.Invalidate(); sidebar.Update(); }
        }

        public void SeekFraction(double fraction)
        {
            TrackInfo t = engine.CurrentTrack;
            if (!engine.IsRunning || t == null || t.Duration <= 0) return;
            engine.Seek(Math.Min(fraction, 0.999) * t.Duration);
        }

        public void CycleRepeat() { repeatMode = (repeatMode + 1) % 3; Invalidate(true); }

        public void ShuffleQueue()
        {
            lock (queueLock)
            {
                int i = queue.IndexOf(nowPlaying);
                if (i > 0) { queue.RemoveAt(i); queue.Insert(0, nowPlaying); }
                Shuffle(queue, i >= 0 ? 1 : 0);
            }
            sidebar.RefreshQueue();
            sidebar.ScrollToCurrent();
            SetStatus("Queue shuffled.", false);
        }

        public void ToggleQueue()
        {
            sidebar.QueueExpanded = !sidebar.QueueExpanded;
            sidebar.ScrollToCurrent();
            sidebar.Invalidate();
        }

        public void ShowNowPlaying() { Navigate(NavPage.NowPlaying); }

        public void PlayQueueIndex(int index)
        {
            TrackInfo t;
            lock (queueLock) t = index >= 0 && index < queue.Count ? queue[index] : null;
            if (t != null) PlayTrack(t, 0);
        }

        public void RemoveFromQueue(int index)
        {
            lock (queueLock) if (index >= 0 && index < queue.Count) queue.RemoveAt(index);
            sidebar.RefreshQueue();
        }

        public void ClearQueue()
        {
            lock (queueLock)
            {
                queue.Clear();
                if (nowPlaying != null && engine.IsRunning) queue.Add(nowPlaying);
            }
            sidebar.RefreshQueue();
        }

        // ================================================================== IPlayerHost state

        public TrackInfo NowPlaying { get { return nowPlaying; } }
        public bool IsPlaying { get { return engine.IsRunning && !engine.IsPaused; } }
        public bool IsRunning { get { return engine.IsRunning; } }
        public double Position { get { return engine.Position; } }
        public double Volume { get { return volumeValue; } }
        public int RepeatMode { get { return repeatMode; } }
        public string StatusText { get { return statusText; } }
        public bool StatusIsError { get { return statusError; } }
        public Backdrop Backdrop { get { return backdrop; } }
        public Control BackdropHost { get { return this; } }   // the whole window, so the backdrop can continue behind the sidebar

        /// <summary>Signal path for the status chip's pop-up: every stage from the file to the DAC, and whether it alters the samples.</summary>
        public SpectrumTap SpectrumTap { get { return engine.Tap; } }

        public List<SignalStep> SignalPath()
        {
            var steps = new List<SignalStep>();
            TrackInfo t = nowPlaying;
            if (t == null) return steps;
            steps.Add(new SignalStep("Source file", t.LongFormat + "  ·  " + Path.GetFileName(t.FilePath), StepState.Info));
            if (!engine.IsRunning || engine.OutputDescription.Length == 0)
            {
                steps.Add(new SignalStep("Not playing", "Press play to see the live path to your DAC", StepState.Info));
                return steps;
            }
            bool exclusive = engine.IsExclusiveActive;
            steps.Add(new SignalStep("Decoder", engine.DecoderDescription + (t.IsLossy ? "  ·  lossy source: decoded, not restored" : ""),
                                     t.IsLossy ? StepState.Info : StepState.Untouched));

            string[] notes = engine.ProcessingNotes;
            var changes = new List<string>();
            foreach (string n in notes) if (n != "Windows mixer may resample / mix" && n != "lossy source") changes.Add(n);
            steps.Add(new SignalStep("Processing", changes.Count == 0 ? "None: samples pass through unchanged" : string.Join("  ·  ", changes.ToArray()),
                                     engine.IsBitPerfect || changes.Count == 0 ? StepState.Untouched : StepState.Changed));

            string vol; StepState vs;
            if (engine.FadeGain < 1f) { vol = "Sleep timer fading out"; vs = StepState.Changed; }
            else if (volumeMode != 1) { vol = "DAC / device volume: the stream itself is not scaled"; vs = StepState.Untouched; }
            else if (engine.SoftwareGain == 1f) { vol = "Software volume at 100% (unity gain)"; vs = StepState.Untouched; }
            else if (engine.SoftwareGain <= 0f) { vol = "Software volume: muted"; vs = StepState.Changed; }
            else { vol = "Software volume " + (20 * Math.Log10(Math.Max(1e-6, engine.SoftwareGain))).ToString("0.0", CultureInfo.InvariantCulture) + " dB, with dither"; vs = StepState.Changed; }
            steps.Add(new SignalStep("Volume", vol, vs));

            steps.Add(new SignalStep("Output stream", engine.OutputDescription, exclusive ? StepState.Untouched : StepState.Info));
            steps.Add(new SignalStep("Windows audio engine", exclusive ? "Bypassed: exclusive mode writes straight to the driver"
                                                                       : "Mixes with other apps; may resample and apply effects",
                                     exclusive ? StepState.Untouched : StepState.Changed));
            steps.Add(new SignalStep("Device", SelectedDeviceName(), StepState.Info));
            return steps;
        }

        public string SignalVerdict
        {
            get
            {
                if (nowPlaying == null) return "Nothing playing";
                if (!engine.IsRunning || engine.OutputDescription.Length == 0) return "Stopped";
                if (engine.IsBitPerfect && engine.FadeGain >= 1f) return "Bit-perfect: your DAC receives the file's samples exactly as stored";
                return "Not bit-perfect: the stages marked in orange change the samples";
            }
        }

        public string BadgeText
        {
            get
            {
                if (!engine.IsRunning) return "STOPPED";
                if (engine.OutputDescription.Length == 0) return "OPENING";
                if (engine.IsPaused) return "PAUSED";
                if (engine.IsBitPerfect) return "BIT-PERFECT";
                return engine.IsExclusiveActive ? "EXCLUSIVE" : "SHARED";
            }
        }

        public Color BadgeColor
        {
            get
            {
                if (!engine.IsRunning || engine.OutputDescription.Length == 0 || engine.IsPaused) return Color.FromArgb(74, 74, 80);
                if (engine.IsBitPerfect) return Theme.Good;
                return engine.IsExclusiveActive ? Theme.Accent : Theme.Warn;
            }
        }

        public string OutputLine
        {
            get
            {
                TrackInfo t = nowPlaying;
                if (t == null) return "";
                string output = engine.IsRunning ? engine.OutputDescription : "";
                return output.Length > 0 ? t.LongFormat + "   →   " + output : t.LongFormat;
            }
        }

        public string PathLine
        {
            get
            {
                if (!engine.IsRunning || engine.OutputDescription.Length == 0) return "";
                string path = engine.ProcessingDescription;
                if (engine.IsExclusiveActive && engine.SoftwareGain != 1f)
                    path = (path.Length > 0 ? path + "  ·  " : "") + "software volume (100% or DAC volume for bit-perfect)";
                if (engine.IsBitPerfect) return "No resampling, mixing or volume processing" + (path.Length > 0 ? "  ·  " + path : "");
                return path;
            }
        }

        // ================================================================== settings, theme, devices, volume

        static readonly string[] VolumeModes = { "DAC / device volume", "Software volume" };
        List<AudioDeviceInfo> devices = new List<AudioDeviceInfo>();
        string deviceId;
        bool exclusiveMode = true;
        bool nativeDsd;   // DSD as DoP (opt-in: a DAC without DoP support would play noise)
        int bufferIndex = 2, volumeMode;
        double opacityValue = 0.97;

        void BuildSettings()
        {
            SettingsView v = settingsView;

            SSection look = v.AddSection("Appearance");
            SettingsView.Row(look, Glyphs.Palette, "Theme", delegate { return "Dark, light, follow Windows, or tint the app with your own colour"; },
                new SSegmented(new[] { "Dark", "Light", "System", "Custom" }, new[] { Glyphs.Moon, Glyphs.Sun, Glyphs.Desktop, Glyphs.Palette },
                    delegate { return (int)Theme.Mode; }, delegate(int i) { ApplyTheme((ThemeMode)i, Theme.CustomColor); }));
            SettingsView.Row(look, Glyphs.Palette, "Accent colour", AccentDescription,
                new SColorChoice(AccentPresets, delegate { return Theme.AccentOverride; }, delegate(Color? c) { SetAccent(c); }, PickAccentColor));
            SettingsView.Row(look, Glyphs.Palette, "Custom colour", delegate { return "Accent colour and background tint used by the Custom theme"; },
                new SSwatch(delegate { return Theme.CustomColor; }, PickCustomColor),
                new SButton("Choose…", null, false, PickCustomColor));
            SettingsView.Row(look, Glyphs.Fullscreen, "Window opacity", delegate { return "A little transparency lets the desktop show through"; },
                new SSlider(delegate { return (opacityValue - 0.75) / 0.25; }, delegate(double x) { opacityValue = 0.75 + 0.25 * x; ApplyOpacity(); },
                    delegate { return (int)Math.Round(opacityValue * 100) + "%"; }, 200));
            SettingsView.Row(look, Glyphs.Glass, "Frosted glass", delegate { return "See-through panels over a blurred picture behind the whole window"; },
                new SToggle(delegate { return Theme.Glass; }, delegate(bool on)
                {
                    Theme.Glass = on;
                    ApplyGlass();
                    SaveSettings();
                }));
            SettingsView.Row(look, Glyphs.Palette, "Glass background", delegate { return glassSource == 0 ? "Blurred cover of the song that is playing" : "Blurred picture of your desktop wallpaper"; },
                new SSegmented(new[] { "Album art", "Wallpaper" }, null,
                    delegate { return glassSource; }, delegate(int i) { glassSource = i; ApplyGlass(); SaveSettings(); }));
            SettingsView.Row(look, Glyphs.Adjust, "Glass transparency", delegate { return "Clearer glass shows more of the picture; frostier glass is easier to read"; },
                new SSlider(delegate { return Theme.GlassLevel; }, delegate(double x)
                {
                    Theme.GlassLevel = Math.Max(0, Math.Min(1, x));
                    if (Theme.Glass) { backdrop.Invalidate(); Invalidate(true); }   // the picture's veil changes with it
                },
                    delegate { return (int)Math.Round(Theme.GlassLevel * 100) + "%"; }, 200));
            SettingsView.Row(look, Glyphs.SidebarCollapse, "Compact sidebar", delegate { return "Icons only. Drag the sidebar's edge to resize it"; },
                new SToggle(delegate { return sidebar.Collapsed; }, delegate(bool on) { sidebar.SetCollapsed(on, true); }));

            SSection audio = v.AddSection("Audio output");
            SettingsView.Row(audio, Glyphs.Speaker, "Output device", null,
                new SDropDown(delegate { return devices.ConvertAll(d => d.ToString()).ToArray(); },
                    delegate { return devices.FindIndex(d => d.Id == deviceId); },
                    delegate(int i) { deviceId = devices[i].Id; OpenEndpointVolume(); OutputSettingsChanged(); }, 360),
                new SButton(null, Glyphs.Refresh, false, delegate { LoadDevices(deviceId); }));
            SettingsView.Row(audio, Glyphs.NowPlaying, "Mode", delegate
            {
                return exclusiveMode ? "Exclusive: the app owns the DAC and sends samples unchanged (like UAPP)"
                                     : "Shared: audio goes through the Windows mixer (equalizer available)";
            },
                new SSegmented(new[] { "Exclusive (bit-perfect)", "Shared" }, null,
                    delegate { return exclusiveMode ? 0 : 1; }, delegate(int i) { exclusiveMode = i == 0; OutputSettingsChanged(); }));
            SettingsView.Row(audio, Glyphs.Music, "DSD playback", delegate
            {
                if (!nativeDsd) return "DSF / DFF files are converted to high-resolution PCM: works with every DAC";
                return exclusiveMode ? "Native DSD as DoP (Exclusive mode). Only for DACs that support DoP; volume comes from the DAC"
                                     : "Native DSD needs Exclusive mode; in Shared mode DSD is converted to PCM";
            },
                new SDropDown(delegate { return new[] { "Convert to PCM", "Native (DoP)" }; }, delegate { return nativeDsd ? 1 : 0; }, delegate(int i)
                {
                    bool on = i == 1;
                    if (on && !nativeDsd && MessageBox.Show(this,
                        "Native DSD sends the 1-bit stream to your DAC as DoP (DSD over PCM, 176.4 kHz for DSD64).\n\n" +
                        "Only use this if your DAC supports DoP. A DAC without DoP support plays it as loud white noise, " +
                        "so turn the volume down for the first try.\n\nIf the DAC refuses the DoP format, SoDa Player converts to PCM automatically.",
                        "Native DSD (DoP)", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                    nativeDsd = on;
                    OutputSettingsChanged();
                }, 170));
            SettingsView.Row(audio, Glyphs.Equalizer, "Equalizer", EqSummary,
                new SButton("Open", null, false, delegate { Navigate(NavPage.Equalizer); }));
            SettingsView.Row(audio, Glyphs.Settings, "Buffer", delegate { return "Larger buffers are more robust, smaller ones react faster"; },
                new SDropDown(delegate { return Array.ConvertAll(BufferChoices, ms => ms + " ms"); }, delegate { return bufferIndex; },
                    delegate(int i) { bufferIndex = i; OutputSettingsChanged(); }, 140));
            SettingsView.Row(audio, Glyphs.Volume, "Volume control", delegate
            {
                return volumeMode == 0 ? "Uses the DAC's own volume, so the stream stays bit-perfect" : "32-bit float gain with dither; bit-perfect only at 100%";
            },
                new SDropDown(delegate { return VolumeModes; }, delegate { return volumeMode; }, delegate(int i)
                {
                    volumeMode = i;
                    if (i == 0 && endpointVolume != null) volumeValue = endpointVolume.Level;
                    ApplyVolume();
                }, 230));

            SSection lyrics = v.AddSection("Lyrics");
            SettingsView.Row(lyrics, Glyphs.Lyrics, "Find lyrics online", delegate
            {
                return "If a song has no embedded or .lrc lyrics, look it up on LRCLIB (lrclib.net) — sends title, artist, album and length";
            },
                new SToggle(delegate { return LyricsService.OnlineEnabled; }, delegate(bool on)
                {
                    LyricsService.OnlineEnabled = on;
                    if (nowPlaying != null) { LyricsService.Forget(nowPlaying); nowView.LyricsArrived(nowPlaying); }
                }));
            SettingsView.Row(lyrics, Glyphs.Trash, "Lyrics cache", delegate { return "Downloaded lyrics are kept on this PC so they load instantly next time"; },
                new SButton("Clear cache", null, false, delegate { LyricsService.ClearCache(); SetStatus("Lyrics cache cleared.", false); }));

            SSection artwork = v.AddSection("Artwork");
            SettingsView.Row(artwork, Glyphs.Album, "Find missing album art online", delegate
            {
                return "For albums with no embedded picture or cover image in the folder, look up the cover on iTunes — sends album artist and title. Right-click an album to pick a cover yourself";
            },
                new SToggle(delegate { return OnlineArt.Enabled; }, delegate(bool on)
                {
                    OnlineArt.Enabled = on;
                    if (on) { OnlineArt.ForgetMisses(); ArtCache.ForgetMissing(); InvalidateVisiblePage(); }
                }));
            SettingsView.Row(artwork, Glyphs.Album, "Animated album art", delegate
            {
                return "Loop a moving cover on Now Playing — a cover.mp4 (or folder.mp4, …) next to the music, or one downloaded below";
            },
                new SToggle(delegate { return AnimatedArt.Enabled; }, delegate(bool on)
                {
                    AnimatedArt.Enabled = on;
                    AnimatedArt.Reset();
                    nowView.AnimatedArtChanged();
                }));
            SettingsView.Row(artwork, Glyphs.ExternalLink, "Find animated covers online", delegate
            {
                return "Download Apple Music's animated artwork via artwork.m8tec.top — sends album artist and album title";
            },
                new SToggle(delegate { return AnimatedArt.OnlineEnabled; }, delegate(bool on)
                {
                    AnimatedArt.OnlineEnabled = on;
                    AnimatedArt.Reset();
                    nowView.AnimatedArtChanged();
                }));
            SettingsView.Row(artwork, Glyphs.Trash, "Animated artwork cache", delegate { return "Downloaded covers are kept on this PC (usually 2–15 MB per album)"; },
                new SButton("Clear cache", null, false, delegate
                {
                    nowView.StopAnimatedArt();
                    AnimatedArt.ClearCache();
                    nowView.AnimatedArtChanged();
                    SetStatus("Animated artwork cache cleared.", false);
                }));

            SSection lib = v.AddSection("Library");
            SRow folders = SettingsView.Row(lib, Glyphs.FolderPlain, "Music folders", LibraryInfoText,
                new SButton("Add folder", Glyphs.Add, true, AddFolderDialog),
                new SButton("Rescan", Glyphs.Refresh, false, delegate { ScanFolders(true); }));
            folders.Full = new SFolderList(delegate { return library.Folders; }, RemoveFolder);

            SSection save = v.AddSection("Save & restore");
            SettingsView.Row(save, Glyphs.Refresh, "Save automatically", delegate { return "Write changes to disk as soon as you make them (otherwise only on Save or when the app closes)"; },
                new SToggle(delegate { return autoSave; }, delegate(bool on) { autoSave = on; if (on) SaveSettings(); }));
            SettingsView.Row(save, Glyphs.Check, "Save settings now", SaveStatusText,
                new SButton("Save", Glyphs.Check, true, SaveSettingsNow));
            SettingsView.Row(save, Glyphs.ExternalLink, "Back up or move your settings", delegate { return "Export them to a file, or load them from one (e.g. on another PC)"; },
                new SButton("Export…", null, false, ExportSettings),
                new SButton("Import…", null, false, ImportSettings));
            SettingsView.Row(save, Glyphs.Refresh, "Reset to defaults", delegate { return "Restore the default settings — your library, queue and lyrics cache are kept"; },
                new SButton("Reset…", null, false, ResetSettings));

            SSection about = v.AddSection("About");
            SettingsView.Row(about, null, "SoDa Player", delegate { return "Version 1.0  ·  bit-perfect music player for Windows (WASAPI exclusive)"; }).Picture = Theme.AppIcon;
            SettingsView.Row(about, Glyphs.Info, "Decoder", delegate
            {
                if (FfmpegTools.BundledVersion != null) return "FFmpeg " + FfmpegTools.BundledVersion + " (built into SoDa Player)" + (FfmpegTools.HasSoxr ? "  ·  SoX resampler" : "");
                return FfmpegTools.FfmpegPath != null ? FfmpegTools.FfmpegPath + (FfmpegTools.HasSoxr ? "  ·  SoX resampler available" : "")
                                                      : "FFmpeg not found — install with: winget install Gyan.FFmpeg.Essentials";
            });
            SettingsView.Row(about, Glyphs.Info, "Third-party licences", delegate { return "FFmpeg (GPL v3) · Inter (SIL Open Font License) · Tabler Icons (MIT)"; },
                new SButton("View", null, false, ShowLicences));
        }

        /// <summary>Writes the embedded third-party notices (FFmpeg GPL text and source pointer, Tabler MIT, Inter OFL) to a file and opens it.</summary>
        void ShowLicences()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoDaPlayer");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "Third-party licences.txt");
                File.WriteAllText(file, FfmpegTools.LicenseText(), new UTF8Encoding(true));
                Process.Start(file);
            }
            catch (Exception ex) { SetStatus("Could not open the licences: " + ex.Message, true); }
        }

        string LibraryInfoText()
        {
            int pending = 0;
            foreach (TrackInfo t in library.Songs) if (!t.Probed) pending++;
            return Plural(library.Songs.Count, "song") + "  ·  " + Plural(library.Albums.Count, "album") + "  ·  " + Plural(library.Artists.Count, "artist") +
                   (pending > 0 ? "  ·  reading tags of " + pending + "…" : "");
        }

        void RefreshSettingsInfo() { settingsView.Invalidate(); }

        // ------------------------------------------------------------------ equalizer (Shared mode only)

        /// <summary>
        /// The Equalizer page. The EQ is DSP, so it can only exist on the Shared path: in Exclusive mode the engine never
        /// creates the filter stage and every control here is locked, keeping the stream bit-perfect.
        /// </summary>
        void BuildEqualizer()
        {
            SettingsView v = eqView;
            EqualizerSettings eq = engine.Equalizer;
            Func<bool> unlocked = delegate { return !exclusiveMode; };
            v.PageTitle = "Equalizer";
            v.PageSubtitle = "20-band graphic equalizer for Shared mode. Exclusive mode always stays bit-perfect";

            SSection status = v.AddSection("Availability");
            SettingsView.Row(status, Glyphs.Lock, "Shared mode only", delegate
            {
                if (exclusiveMode) return "Exclusive mode is on: the EQ is bypassed so playback stays bit-perfect";
                return eq.Enabled ? "Applied to playback in Shared mode, before the Windows mixer" : "Shared mode is on. Switch the equalizer on to use it";
            },
                new SBadge(delegate { return exclusiveMode ? "BYPASSED · EXCLUSIVE" : eq.Enabled ? "ACTIVE" : "OFF"; },
                           delegate { return exclusiveMode ? Theme.Warn : eq.Enabled ? Theme.Good : Theme.TextDim; }),
                new SButton("Switch to Shared mode", null, true, delegate
                {
                    exclusiveMode = false;
                    OutputSettingsChanged();
                    SetStatus("Shared mode: the equalizer can now be used.", false);
                }) { Visible = delegate { return exclusiveMode; } });

            SSection main = v.AddSection("Equalizer");
            SettingsView.Row(main, Glyphs.Equalizer, "Enable equalizer", delegate { return "Turn the equalizer on or off without losing your settings"; },
                new SToggle(delegate { return eq.Enabled; }, delegate(bool on) { eq.Enabled = on; eqView.Invalidate(); }) { Enabled = unlocked });
            SettingsView.Row(main, Glyphs.Adjust, "Preset", delegate { return "Start from a preset, then fine-tune the bands below"; },
                new SDropDown(EqPresetOptions, delegate { return Array.IndexOf(EqPresetOptions(), eq.Preset); }, delegate(int i)
                {
                    string[] o = EqPresetOptions();
                    if (i < 0 || i >= o.Length || o[i] == "Custom") return;
                    float[] g = EqualizerSettings.PresetGains(o[i]);
                    eq.SetAll(g, EqualizerSettings.SuggestedPreamp(g));
                    eq.Preset = o[i];
                    eq.Enabled = true;
                    eqView.Invalidate();
                }, 190) { Enabled = unlocked },
                new SButton("Reset", Glyphs.Refresh, false, delegate
                {
                    eq.SetAll(new float[EqualizerSettings.Bands], 0f);
                    eq.Preset = "Flat";
                    eqView.Invalidate();
                }) { Enabled = unlocked });
            SettingsView.Row(main, Glyphs.Volume, "Preamp", delegate { return "Lower it when boosting bands so loud passages don't clip"; },
                new SSlider(delegate { return (eq.Preamp + EqualizerSettings.MaxDb) / (2 * EqualizerSettings.MaxDb); },
                    delegate(double x) { eq.Preamp = (float)Math.Round((x * 2 - 1) * EqualizerSettings.MaxDb * 2) / 2f; },
                    delegate { return FormatDb(eq.Preamp); }, 220) { Enabled = unlocked });
            SRow bands = SettingsView.Row(main, Glyphs.Adjust, "Bands", delegate { return "Drag a band to adjust it (±12 dB). Double-click a band to reset it to 0 dB"; });
            bands.Full = new SEqBands(eq, delegate { return eq.Enabled && !exclusiveMode; }, delegate { eq.Preset = "Custom"; eqView.Invalidate(); })
                { Enabled = unlocked };
        }

        string[] EqPresetOptions()
        {
            string p = engine.Equalizer.Preset;
            if (Array.IndexOf(EqualizerSettings.PresetNames, p) >= 0) return EqualizerSettings.PresetNames;
            var list = new List<string>(EqualizerSettings.PresetNames);
            list.Add("Custom");
            return list.ToArray();
        }

        static string FormatDb(float db)
        {
            if (Math.Abs(db) < 0.05f) return "0 dB";
            return (db > 0 ? "+" : "−") + Math.Abs(db).ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        string EqSummary()
        {
            EqualizerSettings eq = engine.Equalizer;
            if (exclusiveMode) return "20-band EQ, available in Shared mode only (bypassed in Exclusive mode)";
            return "20-band EQ  ·  " + (eq.Enabled ? "On  ·  " + eq.Preset : "Off");
        }

        void ApplyEqSettings(Dictionary<string, string> s)
        {
            string v;
            var ci = CultureInfo.InvariantCulture;
            EqualizerSettings eq = engine.Equalizer;
            var gains = new float[EqualizerSettings.Bands];
            if (s.TryGetValue("eq_gains", out v))
            {
                string[] parts = v.Split(',');
                for (int i = 0; i < parts.Length && i < gains.Length; i++)
                {
                    float f;
                    if (float.TryParse(parts[i].Trim(), NumberStyles.Float, ci, out f)) gains[i] = f;
                }
            }
            float pre = 0;
            if (s.TryGetValue("eq_preamp", out v)) float.TryParse(v, NumberStyles.Float, ci, out pre);
            eq.SetAll(gains, pre);
            eq.Preset = s.TryGetValue("eq_preset", out v) && v.Length > 0 ? v : "Flat";
            eq.Enabled = s.TryGetValue("eq_enabled", out v) && v == "1";
            if (eqView != null) eqView.Invalidate();
        }

        static readonly Color[] AccentPresets =
        {
            Color.FromArgb(82, 156, 255),   // Blue
            Color.FromArgb(151, 117, 250),  // Purple
            Color.FromArgb(240, 98, 146),   // Pink
            Color.FromArgb(239, 83, 80),    // Red
            Color.FromArgb(255, 138, 61),   // Orange
            Color.FromArgb(255, 193, 7),    // Amber
            Color.FromArgb(46, 184, 114),   // Green
            Color.FromArgb(38, 198, 218),   // Teal
            Color.FromArgb(150, 150, 160)   // Graphite
        };
        static readonly string[] AccentNames = { "Blue", "Purple", "Pink", "Red", "Orange", "Amber", "Green", "Teal", "Graphite" };

        string AccentDescription()
        {
            if (!Theme.AccentOverride.HasValue) return "Highlight colour for buttons, selections and the playing song — Theme default";
            Color a = Theme.AccentOverride.Value;
            int i = Array.FindIndex(AccentPresets, p => p.ToArgb() == a.ToArgb());
            return "Highlight colour for buttons, selections and the playing song — " + (i >= 0 ? AccentNames[i] : "Custom #" + (a.ToArgb() & 0xFFFFFF).ToString("X6"));
        }

        /// <summary>null = use the theme's own accent.</summary>
        void SetAccent(Color? c)
        {
            Theme.AccentOverride = c;
            ApplyTheme(Theme.Mode, Theme.CustomColor);
        }

        void PickAccentColor()
        {
            using (var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = Theme.AccentOverride ?? Theme.Accent })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) SetAccent(Color.FromArgb(255, dlg.Color));
            }
        }

        void PickCustomColor()
        {
            using (var dlg = new ColorDialog { FullOpen = true, Color = Theme.CustomColor, AnyColor = true })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) ApplyTheme(ThemeMode.Custom, dlg.Color);
            }
        }

        /// <summary>Applies a theme to every control, the backdrop, menus and the window frame.</summary>
        void ApplyTheme(ThemeMode mode, Color custom)
        {
            Theme.Apply(mode, custom);
            ArtCache.ClearTiles();   // placeholder tiles use theme colours
            BackColor = Theme.Bg;
            ApplyThemeColors(this);
            backdrop.Invalidate();
            try
            {
                int dark = Theme.IsDark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                Color b = Theme.Border;
                int border = b.R | (b.G << 8) | (b.B << 16);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
            Invalidate(true);
        }

        static void ApplyThemeColors(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Sidebar) c.BackColor = Theme.Sidebar;
                else if (c is MiniPlayer) c.BackColor = Theme.Surface;
                else c.BackColor = Theme.Bg;
                c.ForeColor = Theme.Text;
                ApplyThemeColors(c);
            }
        }

        void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (Theme.Mode != ThemeMode.System) return;
            UI(delegate { ApplyTheme(ThemeMode.System, Theme.CustomColor); });
        }

        void ApplyOpacity()
        {
            opacityValue = Math.Max(0.75, Math.Min(1.0, opacityValue));
            Opacity = opacityValue >= 0.995 ? 1.0 : opacityValue;
        }

        // ------------------------------------------------------------------ full screen

        bool fullscreen;
        Rectangle preFullBounds;
        bool preFullMaximized, preFullCollapsed;

        public bool IsFullscreen { get { return fullscreen; } }

        public void ToggleFullscreen()
        {
            if (!fullscreen)
            {
                preFullMaximized = IsMaximized;
                preFullBounds = preFullMaximized ? normalBounds : Bounds;
                preFullCollapsed = sidebar.Collapsed;
                fullscreen = true;
                if (preFullMaximized) WindowState = FormWindowState.Normal;
                sidebar.Visible = false;
                titleBar.Visible = false;
                LayoutShell();
                Navigate(NavPage.NowPlaying);
                Bounds = Screen.FromControl(this).Bounds;   // the whole monitor, over the taskbar
                Activate();
            }
            else
            {
                fullscreen = false;
                sidebar.Visible = true;
                titleBar.Visible = true;
                LayoutShell();
                Bounds = preFullBounds;
                if (preFullMaximized) WindowState = FormWindowState.Maximized;
                ShowPages();
            }
            backdrop.Invalidate();
            nowView.UpdateState();
            Invalidate(true);
            // Once the size / maximize change has settled, repaint again: the title bar comes back at a moment when the
            // window may still be mid-change, and a paint lost then used to leave a dark or stale strip across the top.
            BeginInvoke((Action)delegate { if (!IsDisposed) { Invalidate(true); Update(); } });
        }

        // ------------------------------------------------------------------ devices & volume

        string SelectedDeviceId() { return deviceId; }

        string SelectedDeviceName()
        {
            AudioDeviceInfo d = devices.Find(x => x.Id == deviceId);
            return d != null ? d.Name : "device";
        }

        void LoadDevices(string selectId)
        {
            try { devices = AudioDevices.GetRenderDevices(); }
            catch (Exception ex) { devices = new List<AudioDeviceInfo>(); SetStatus("Could not list audio devices: " + ex.Message, true); }
            AudioDeviceInfo pick = devices.Find(d => d.Id == selectId);
            if (pick == null && devices.Count > 0) pick = devices[0];
            deviceId = pick != null ? pick.Id : null;
            OpenEndpointVolume();
            engine.DeviceId = deviceId;
            settingsView.Invalidate();
        }

        void OpenEndpointVolume()
        {
            if (endpointVolume != null) { endpointVolume.Dispose(); endpointVolume = null; }
            try { endpointVolume = new EndpointVolume(deviceId); } catch { endpointVolume = null; }
            if (volumeMode != 1 && endpointVolume != null) volumeValue = endpointVolume.Level;
            ApplyVolume();
        }

        void ApplyEngineSettings()
        {
            engine.DeviceId = deviceId;
            engine.ExclusiveMode = exclusiveMode;
            engine.NativeDsd = nativeDsd;
            engine.BufferMs = BufferChoices[Math.Max(0, Math.Min(BufferChoices.Length - 1, bufferIndex))];
            // Only the software gain: re-writing the device volume here (on every Play) used to put back a stale level
            // after the DAC's knob, the keyboard volume keys or the Windows mixer had changed it.
            ApplyVolume(false);
        }

        int volumeGuardUntil;

        /// <summary>
        /// Device-volume mode: follows changes made outside the app (DAC knob, volume keys, Windows mixer), so the slider
        /// and the next Play use the real level. For a moment after a stream opens, a jump is treated as the device
        /// resetting its volume on a sample-rate change instead (some USB DACs do), and the level is put back.
        /// </summary>
        void SyncDeviceVolume()
        {
            if (volumeMode == 1 || endpointVolume == null) return;
            float level;
            try { level = endpointVolume.Level; } catch { return; }
            if (Math.Abs(level - volumeValue) < 0.004) return;
            if (unchecked(volumeGuardUntil - Environment.TickCount) > 0) { try { endpointVolume.Level = (float)volumeValue; } catch { } }
            else volumeValue = level;
        }

        void GuardDeviceVolume() { volumeGuardUntil = Environment.TickCount + 3000; }

        void OutputSettingsChanged()
        {
            ApplyEngineSettings();
            settingsView.Invalidate();
            eqView.Invalidate();
            if (engine.IsRunning && engine.CurrentTrack != null)
            {
                bool wasPaused = engine.IsPaused;
                PlayTrack(engine.CurrentTrack, engine.Position);
                if (wasPaused) engine.Pause();
            }
        }

        public void SetVolume(double v)
        {
            volumeValue = Math.Max(0, Math.Min(1, v));
            ApplyVolume();
        }

        void ApplyVolume(bool writeDevice = true)
        {
            double v = volumeValue;
            if (v > 0.995) { v = 1; volumeValue = 1; }
            if (volumeMode != 1)
            {
                engine.SoftwareGain = 1f;   // stream untouched; the DAC / endpoint attenuates
                if (writeDevice && endpointVolume != null) endpointVolume.Level = (float)v;
            }
            else
            {
                // 50 dB range, exactly unity at 100% so the stream stays bit-perfect there.
                engine.SoftwareGain = v >= 1 ? 1f : v <= 0 ? 0f : (float)Math.Pow(10, (v - 1) * 50 / 20);
            }
        }

        void RemoveFolder(string folder)
        {
            if (folder == null) return;
            if (MessageBox.Show(this, "Remove this folder and its songs from the library?\n\n" + folder + "\n\nNo files are deleted from disk.",
                                "Remove folder", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            library.Folders.Remove(folder);
            string prefix = folder.TrimEnd('\\') + "\\";
            foreach (TrackInfo t in library.Songs.ToArray())
                if (t.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) library.Remove(t);
            MarkLibraryChanged();
            RefreshLibraryViews();
            RefreshSettingsInfo();
        }

        // ------------------------------------------------------------------ settings file: load, apply, save, export / import

        bool autoSave = true;
        string lastSavedState;           // settings (without window position / page) as last written to disk
        DateTime lastSavedAt;
        int changeSeenAt;                // TickCount when unsaved changes were first noticed (0 = none)

        static Dictionary<string, string> ReadSettingsFile(string path)
        {
            var s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string l = line.Trim();
                        if (l.Length == 0 || l[0] == '#' || l[0] == ';') continue;
                        int eq = l.IndexOf('=');
                        if (eq > 0) s[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            return s;
        }

        Dictionary<string, string> LoadSettings()
        {
            Dictionary<string, string> s = ReadSettingsFile(SettingsFile);
            ApplySettings(s, true);
            lastSavedState = BuildSettingsText(false);
            if (File.Exists(SettingsFile)) lastSavedAt = File.GetLastWriteTime(SettingsFile);
            return s;
        }

        /// <summary>Applies settings; missing keys get their defaults (so an empty dictionary = reset to defaults).</summary>
        void ApplySettings(Dictionary<string, string> s, bool includeWindow)
        {
            string v;
            var ci = CultureInfo.InvariantCulture;

            ThemeMode tm = ThemeMode.Dark;
            if (s.TryGetValue("theme", out v)) { try { tm = (ThemeMode)Enum.Parse(typeof(ThemeMode), v, true); } catch { } }
            Color custom = Color.FromArgb(124, 92, 255);
            int argb;
            if (s.TryGetValue("custom_color", out v) && int.TryParse(v.TrimStart('#'), NumberStyles.HexNumber, ci, out argb)) custom = Color.FromArgb(255, Color.FromArgb(argb));
            Theme.AccentOverride = null;
            if (s.TryGetValue("accent", out v) && int.TryParse(v.TrimStart('#'), NumberStyles.HexNumber, ci, out argb)) Theme.AccentOverride = Color.FromArgb(255, Color.FromArgb(argb));
            Theme.Glass = s.TryGetValue("glass", out v) && v == "1";
            glassSource = s.TryGetValue("glass_source", out v) && v == "wallpaper" ? 1 : 0;
            double gl;
            Theme.GlassLevel = s.TryGetValue("glass_level", out v) && double.TryParse(v, NumberStyles.Float, ci, out gl) ? Math.Max(0, Math.Min(1, gl)) : 0.5;
            ApplyTheme(tm, custom);
            ApplyGlass();

            exclusiveMode = !(s.TryGetValue("mode", out v) && v == "shared");
            nativeDsd = s.TryGetValue("dsd", out v) && v == "native";
            int buf = 20;
            if (s.TryGetValue("buffer", out v)) int.TryParse(v, out buf);
            int bi = Array.IndexOf(BufferChoices, buf);
            bufferIndex = bi >= 0 ? bi : 2;
            volumeMode = s.TryGetValue("volume_mode", out v) && v == "software" ? 1 : 0;
            double d;
            if (volumeMode == 1)
                volumeValue = s.TryGetValue("software_volume", out v) && double.TryParse(v, NumberStyles.Float, ci, out d) ? Math.Max(0, Math.Min(1, d)) : 1.0;
            int r;
            repeatMode = s.TryGetValue("repeat", out v) && int.TryParse(v, out r) ? Math.Max(0, Math.Min(2, r)) : 0;
            songSort = SongSort.DateAddedNewest;
            if (s.TryGetValue("song_sort", out v)) { try { songSort = (SongSort)Enum.Parse(typeof(SongSort), v); } catch { } }
            sortButton.Label = SortButtonText();
            songsHeader.LayoutButtons();
            double op = 0.97;
            if (s.TryGetValue("opacity", out v)) double.TryParse(v, NumberStyles.Float, ci, out op);
            opacityValue = op;
            ApplyOpacity();
            LyricsService.OnlineEnabled = !(s.TryGetValue("lyrics_online", out v) && v == "0");
            AnimatedArt.Enabled = !(s.TryGetValue("animated_art", out v) && v == "0");
            AnimatedArt.OnlineEnabled = !(s.TryGetValue("animated_art_online", out v) && v == "0");
            OnlineArt.Enabled = !(s.TryGetValue("art_online", out v) && v == "0");
            AnimatedArt.Reset();
            autoSave = !(s.TryGetValue("auto_save", out v) && v == "0");
            ApplyEqSettings(s);
            int sm;
            sleepCustomMinutes = s.TryGetValue("sleep_minutes", out v) && int.TryParse(v, out sm) && sm >= 1 && sm <= 1440 ? sm : 20;
            nowView.VisualizerEnabled = !(s.TryGetValue("visualizer", out v) && v == "0");

            int sw = Theme.S(300);
            if (s.TryGetValue("sidebar_width", out v)) { int w; if (int.TryParse(v, out w)) sw = Theme.S(w); }
            sidebar.SetLayout(sw, s.TryGetValue("sidebar_collapsed", out v) && v == "1");

            if (includeWindow && s.TryGetValue("window", out v))
            {
                string[] p = v.Split(',');
                int x, y, w, h;
                if (p.Length >= 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out w) && int.TryParse(p[3], out h))
                {
                    var rect = new Rectangle(x, y, Math.Max(MinimumSize.Width, w), Math.Max(MinimumSize.Height, h));
                    foreach (Screen sc in Screen.AllScreens)
                        if (sc.WorkingArea.IntersectsWith(rect)) { StartPosition = FormStartPosition.Manual; Bounds = rect; break; }
                    if (p.Length >= 5 && p[4] == "max") WindowState = FormWindowState.Maximized;
                }
            }

            LoadDevices(s.TryGetValue("device", out v) ? v : null);   // unknown device (e.g. from another PC) = Windows default
            ApplyEngineSettings();
        }

        /// <summary>The settings file contents. <paramref name="includeLayout"/> adds window position and current page.</summary>
        string BuildSettingsText(bool includeLayout)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("device=" + (deviceId ?? ""));
            sb.AppendLine("mode=" + (exclusiveMode ? "exclusive" : "shared"));
            sb.AppendLine("dsd=" + (nativeDsd ? "native" : "pcm"));
            sb.AppendLine("buffer=" + BufferChoices[Math.Max(0, Math.Min(BufferChoices.Length - 1, bufferIndex))]);
            sb.AppendLine("volume_mode=" + (volumeMode == 1 ? "software" : "device"));
            sb.AppendLine("software_volume=" + volumeValue.ToString("0.###", ci));
            sb.AppendLine("repeat=" + repeatMode);
            sb.AppendLine("song_sort=" + songSort);
            sb.AppendLine("opacity=" + opacityValue.ToString("0.###", ci));
            sb.AppendLine("theme=" + Theme.Mode);
            sb.AppendLine("custom_color=" + (Theme.CustomColor.ToArgb() & 0xFFFFFF).ToString("X6"));
            sb.AppendLine("accent=" + (Theme.AccentOverride.HasValue ? (Theme.AccentOverride.Value.ToArgb() & 0xFFFFFF).ToString("X6") : "default"));
            sb.AppendLine("glass=" + (Theme.Glass ? "1" : "0"));
            sb.AppendLine("glass_source=" + (glassSource == 1 ? "wallpaper" : "art"));
            sb.AppendLine("glass_level=" + Theme.GlassLevel.ToString("0.###", ci));
            sb.AppendLine("lyrics_online=" + (LyricsService.OnlineEnabled ? "1" : "0"));
            sb.AppendLine("animated_art=" + (AnimatedArt.Enabled ? "1" : "0"));
            sb.AppendLine("animated_art_online=" + (AnimatedArt.OnlineEnabled ? "1" : "0"));
            sb.AppendLine("art_online=" + (OnlineArt.Enabled ? "1" : "0"));
            sb.AppendLine("auto_save=" + (autoSave ? "1" : "0"));
            sb.AppendLine("sleep_minutes=" + sleepCustomMinutes);
            sb.AppendLine("visualizer=" + (nowView.VisualizerEnabled ? "1" : "0"));
            EqualizerSettings eq = engine.Equalizer;
            sb.AppendLine("eq_enabled=" + (eq.Enabled ? "1" : "0"));
            sb.AppendLine("eq_preset=" + eq.Preset);
            sb.AppendLine("eq_preamp=" + eq.Preamp.ToString("0.0", ci));
            sb.AppendLine("eq_gains=" + string.Join(",", Array.ConvertAll(eq.Snapshot(), x => x.ToString("0.0", ci))));
            sb.AppendLine("sidebar_width=" + (int)Math.Round(sidebar.ExpandedWidth / Theme.Scale));
            sb.AppendLine("sidebar_collapsed=" + (sidebar.Collapsed ? "1" : "0"));
            if (includeLayout)
            {
                Rectangle b = fullscreen ? preFullBounds : !normalBounds.IsEmpty ? normalBounds : Bounds;
                bool max = fullscreen ? preFullMaximized : WindowState == FormWindowState.Maximized;
                sb.AppendLine("page=" + (detailVisible ? detailParent : currentNav));
                sb.AppendLine("window=" + b.X + "," + b.Y + "," + b.Width + "," + b.Height + (max ? ",max" : ""));
            }
            return sb.ToString();
        }

        /// <summary>Writes settings.ini atomically (temp file + replace), so a crash mid-write can't corrupt it.</summary>
        bool SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Library.DataDir);
                string tmp = SettingsFile + ".tmp";
                File.WriteAllText(tmp, BuildSettingsText(true), Encoding.UTF8);
                if (File.Exists(SettingsFile)) File.Replace(tmp, SettingsFile, null);
                else File.Move(tmp, SettingsFile);
                lastSavedState = BuildSettingsText(false);
                lastSavedAt = DateTime.Now;
                changeSeenAt = 0;
                return true;
            }
            catch { return false; }
        }

        bool HasUnsavedSettings { get { return lastSavedState != null && BuildSettingsText(false) != lastSavedState; } }

        /// <summary>Called from the UI tick: with auto-save on, changes are written ~0.8 s after they stop.</summary>
        void CheckSettingsSave()
        {
            bool dirty = HasUnsavedSettings;
            if (!dirty) { if (changeSeenAt != 0) { changeSeenAt = 0; if (settingsView.Visible) settingsView.Invalidate(); } return; }
            if (changeSeenAt == 0) { changeSeenAt = Environment.TickCount; if (settingsView.Visible) settingsView.Invalidate(); return; }
            if (autoSave && unchecked(Environment.TickCount - changeSeenAt) > 800)
            {
                SaveSettings();
                if (settingsView.Visible) settingsView.Invalidate();
            }
        }

        string SaveStatusText()
        {
            if (HasUnsavedSettings) return autoSave ? "Saving changes…" : "You have unsaved changes";
            if (lastSavedAt == DateTime.MinValue) return "Not saved yet";
            return "Last saved " + (lastSavedAt.Date == DateTime.Today ? "today at " + lastSavedAt.ToString("HH:mm:ss") : lastSavedAt.ToString("d MMM yyyy, HH:mm"));
        }

        void SaveSettingsNow()
        {
            if (SaveSettings()) SetStatus("Settings saved.", false);
            else SetStatus("Could not save settings to " + SettingsFile, true);
            settingsView.Invalidate();
        }

        void ExportSettings()
        {
            using (var dlg = new SaveFileDialog { Title = "Export settings", Filter = "SoDa Player settings (*.ini)|*.ini|All files|*.*", FileName = "SoDa Player settings.ini", OverwritePrompt = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, "# SoDa Player settings — import from Settings > Save & restore\r\n" + BuildSettingsText(false), Encoding.UTF8);
                    SetStatus("Settings exported to " + Path.GetFileName(dlg.FileName) + ".", false);
                }
                catch (Exception ex) { SetStatus("Could not export settings: " + ex.Message, true); }
            }
        }

        void ImportSettings()
        {
            using (var dlg = new OpenFileDialog { Title = "Import settings", Filter = "SoDa Player settings (*.ini)|*.ini|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Dictionary<string, string> s = ReadSettingsFile(dlg.FileName);
                if (!s.ContainsKey("theme") && !s.ContainsKey("mode") && !s.ContainsKey("device"))
                {
                    SetStatus("That file doesn't look like SoDa Player settings.", true);
                    return;
                }
                ApplySettings(s, false);   // window position stays as it is
                OutputSettingsChanged();
                SaveSettings();
                settingsView.Invalidate();
                SetStatus("Settings imported from " + Path.GetFileName(dlg.FileName) + ".", false);
            }
        }

        void ResetSettings()
        {
            if (MessageBox.Show(this, "Reset all settings to their defaults?\n\nYour music library, queue and lyrics cache are kept.",
                                "Reset settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            ApplySettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), false);
            OutputSettingsChanged();
            SaveSettings();
            settingsView.Invalidate();
            SetStatus("Settings reset to defaults.", false);
        }

        void SaveQueue()
        {
            try
            {
                var sb = new StringBuilder("#EXTM3U\r\n");
                List<TrackInfo> q = QueueSnapshot();
                sb.Append("#BPP-NOW:").Append(q.IndexOf(nowPlaying)).Append("\r\n");
                foreach (TrackInfo t in q) sb.Append(t.FilePath).Append("\r\n");
                File.WriteAllText(QueueFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        void RestoreQueue()
        {
            try
            {
                if (!File.Exists(QueueFile)) return;
                int now = -1;
                var list = new List<TrackInfo>();
                foreach (string line in File.ReadAllLines(QueueFile, Encoding.UTF8))
                {
                    if (line.StartsWith("#BPP-NOW:")) { int.TryParse(line.Substring(9), out now); continue; }
                    if (line.Length == 0 || line[0] == '#') continue;
                    TrackInfo t = library.Get(line);
                    if (t == null && File.Exists(line)) { t = new TrackInfo(line); probeQueue.Add(t); }
                    if (t != null) list.Add(t);
                }
                lock (queueLock) { queue.Clear(); queue.AddRange(list); }
                if (now >= 0 && now < list.Count) nowPlaying = list[now];
                sidebar.RefreshQueue();
                sidebar.ScrollToCurrent();
            }
            catch { }
        }
    }
}

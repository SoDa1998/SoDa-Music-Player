using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BitPerfectPlayer
{
    /// <summary>
    /// Album art: looks for cover/folder/front images next to the file, otherwise extracts the embedded picture with ffmpeg.
    /// Results are cached on disk (%APPDATA%\BitPerfectPlayer\art) and in memory. Requests are asynchronous:
    /// Get* returns null until the image is ready, then <see cref="ArtReady"/> fires (on a worker thread).
    /// </summary>
    internal static class ArtCache
    {
        public static event Action ArtReady;

        const int ThumbSize = 256;
        const int LargeSize = 800;
        static readonly object lk = new object();
        static readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>();
        static readonly Dictionary<string, Image> larges = new Dictionary<string, Image>();
        static readonly LinkedList<string> largeOrder = new LinkedList<string>();
        static readonly HashSet<string> missing = new HashSet<string>();
        static readonly HashSet<string> pending = new HashSet<string>();
        static readonly Dictionary<string, object> keyLocks = new Dictionary<string, object>();
        // LIFO: the most recently requested art (what is on screen right now while scrolling) is loaded first.
        static readonly BlockingCollection<KeyValuePair<string, TrackInfo>> queue =
            new BlockingCollection<KeyValuePair<string, TrackInfo>>(new ConcurrentStack<KeyValuePair<string, TrackInfo>>());
        static readonly string[] CoverNames = { "cover", "folder", "front", "album", "albumart", "albumartlarge" };

        static string Dir { get { return Path.Combine(Library.DataDir, "art"); } }

        static ArtCache()
        {
            for (int i = 0; i < 4; i++)
                new Thread(Worker) { IsBackground = true, Name = "art", Priority = ThreadPriority.BelowNormal }.Start();
        }

        /// <summary>Disk-cache key (MD5 of the album key). Cached on the track; recomputed only if its album key changes.</summary>
        internal static string KeyFor(TrackInfo t)
        {
            string ak = Library.AlbumKey(t);
            string cached = t.ArtKey;
            if (cached != null && t.ArtKeySource == ak) return cached;
            using (var md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(ak));
                cached = BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
            }
            t.ArtKeySource = ak;
            t.ArtKey = cached;
            return cached;
        }

        // ------------------------------------------------------------------ ready-to-draw tiles (UI thread)
        // Scaling + rounding a 256/800 px cover on every paint was the main cost when scrolling big libraries.
        // Tiles are rendered once per size/shape and then drawn with a plain 1:1 blit.

        const long TileBudget = 128L * 1024 * 1024;
        static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Bitmap>>> tiles = new Dictionary<string, LinkedListNode<KeyValuePair<string, Bitmap>>>();
        static readonly LinkedList<KeyValuePair<string, Bitmap>> tileOrder = new LinkedList<KeyValuePair<string, Bitmap>>();
        static long tileBytes;

        public static void ClearTiles()
        {
            foreach (var kv in tileOrder) kv.Value.Dispose();
            tileOrder.Clear(); tiles.Clear(); tileBytes = 0;
        }

        /// <summary>A w×h tile of the track's cover (or a placeholder), already rounded (or circular). Never null.</summary>
        public static Bitmap GetTile(TrackInfo t, int w, int h, int radius, bool circle, string seed)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            Image src = null;
            string srcKey = null;
            if (t != null && t.Probed && t.ProbeError == null)
            {
                bool wantLarge = w > 260 || h > 260;
                if (wantLarge) { src = Get(t, true); if (src != null) srcKey = KeyFor(t) + "L" + Version(KeyFor(t)); }
                if (src == null) { src = Get(t, false); if (src != null) srcKey = KeyFor(t) + "T" + Version(KeyFor(t)); }
            }
            string key = (srcKey ?? "ph|" + (seed ?? "")) + "|" + w + "x" + h + "|" + radius + (circle ? "c" : "");
            LinkedListNode<KeyValuePair<string, Bitmap>> node;
            if (tiles.TryGetValue(key, out node))
            {
                tileOrder.Remove(node); tileOrder.AddLast(node);
                return node.Value.Value;
            }
            Bitmap tile = RenderTile(src, w, h, circle ? Math.Min(w, h) / 2f : radius, seed);
            node = tileOrder.AddLast(new KeyValuePair<string, Bitmap>(key, tile));
            tiles[key] = node;
            tileBytes += (long)w * h * 4;
            while (tileBytes > TileBudget && tileOrder.Count > 1)
            {
                var old = tileOrder.First.Value;
                tileOrder.RemoveFirst(); tiles.Remove(old.Key);
                tileBytes -= (long)old.Value.Width * old.Value.Height * 4;
                old.Value.Dispose();
            }
            return tile;
        }

        static Bitmap RenderTile(Image src, int w, int h, float radius, string seed)
        {
            var tile = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(tile))
            {
                if (src == null) { Theme.ArtPlaceholder(g, new RectangleF(0, 0, w, h), radius, seed); return tile; }
                // One pass: a texture brush scaled to cover the tile, filling the anti-aliased rounded shape.
                float s = Math.Max((float)w / src.Width, (float)h / src.Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var tb = new TextureBrush(src, WrapMode.Clamp))
                using (var path = Theme.Round(new RectangleF(0, 0, w, h), radius))
                {
                    tb.TranslateTransform((w - src.Width * s) / 2, (h - src.Height * s) / 2);
                    tb.ScaleTransform(s, s);
                    g.FillPath(tb, path);
                }
            }
            return tile;
        }

        public static Image GetThumb(TrackInfo t) { return Get(t, false); }
        public static Image GetLarge(TrackInfo t) { return Get(t, true); }

        static Image Get(TrackInfo t, bool large)
        {
            if (t == null || !t.Probed || t.ProbeError != null) return null;
            string key = KeyFor(t);
            lock (lk)
            {
                Image img;
                if ((large ? larges : thumbs).TryGetValue(key, out img)) return img;
                if (missing.Contains(key)) return null;
                if (pending.Add(key + (large ? "|L" : "|T"))) queue.Add(new KeyValuePair<string, TrackInfo>(key + (large ? "|L" : "|T"), t));
            }
            return null;
        }

        /// <summary>Forget "no art" results so a rescan picks up newly added cover images.</summary>
        public static void ForgetMissing()
        {
            lock (lk) missing.Clear();
            try { foreach (string f in Directory.GetFiles(Dir, "*.none")) File.Delete(f); } catch { }
        }

        static void Worker()
        {
            foreach (var req in queue.GetConsumingEnumerable())
            {
                string key = req.Key.Substring(0, req.Key.Length - 2);
                bool large = req.Key.EndsWith("|L");
                Image result = null;
                try
                {
                    object keyLock;
                    lock (lk) { if (!keyLocks.TryGetValue(key, out keyLock)) keyLocks[key] = keyLock = new object(); }
                    string file;
                    lock (keyLock) file = EnsureCached(key, req.Value);   // thumb + large requests share one extraction
                    if (file != null)
                    {
                        using (var fs = File.OpenRead(file))
                        using (var src = Image.FromStream(fs))
                            result = large ? Fit(src, LargeSize) : CropSquare(src, ThumbSize);
                    }
                }
                catch { result = null; }

                lock (lk)
                {
                    pending.Remove(req.Key);
                    if (result == null) missing.Add(key);
                    else if (large)
                    {
                        larges[key] = result;
                        largeOrder.AddLast(key);
                        while (largeOrder.Count > 8) { larges.Remove(largeOrder.First.Value); largeOrder.RemoveFirst(); }
                    }
                    else thumbs[key] = result;
                }
                var h = ArtReady;
                if (h != null) { try { h(); } catch { } }
            }
        }

        static string EnsureCached(string key, TrackInfo t)
        {
            Directory.CreateDirectory(Dir);
            string jpg = Path.Combine(Dir, key + ".jpg");
            string none = Path.Combine(Dir, key + ".none");
            if (File.Exists(jpg)) return jpg;
            if (File.Exists(none)) { OnlineArt.Enqueue(key, t); return null; }

            // 1) A cover image in the track's folder.
            string folderImage = FindFolderImage(Path.GetDirectoryName(t.FilePath));
            if (folderImage != null)
            {
                try
                {
                    using (var fs = File.OpenRead(folderImage))
                    using (var src = Image.FromStream(fs))
                    using (var fitted = Fit(src, LargeSize))
                        fitted.Save(jpg, ImageFormat.Jpeg);
                    return jpg;
                }
                catch { }
            }

            // 2) The picture embedded in the file (or the first video frame of a video container).
            if (FfmpegTools.Locate())
            {
                try
                {
                    FfmpegTools.RunAndCapture(FfmpegTools.FfmpegPath,
                        "-hide_banner -loglevel error -nostdin -y -i " + FfmpegTools.Quote(t.FilePath) +
                        " -map 0:v:0 -frames:v 1 -vf scale=w=" + LargeSize + ":h=" + LargeSize +
                        ":force_original_aspect_ratio=decrease -q:v 3 " + FfmpegTools.Quote(jpg), 20000);
                    if (File.Exists(jpg) && new FileInfo(jpg).Length > 0) return jpg;
                }
                catch { }
                try { if (File.Exists(jpg)) File.Delete(jpg); } catch { }
            }

            try { File.WriteAllText(none, ""); } catch { }
            // 3) Nothing local: look it up online in the background (if enabled); SetImage then brings it in.
            OnlineArt.Enqueue(key, t);
            return null;
        }

        static readonly Dictionary<string, int> versions = new Dictionary<string, int>();   // bumped when a cover is replaced

        /// <summary>
        /// Uses <paramref name="img"/> as the album's cover from now on (found online or picked by the user): stored in the
        /// art cache, replacing any earlier cover there. With <paramref name="replace"/> false it is only stored while the
        /// album still has no art (an automatic lookup never overrides a cover found meanwhile). Safe on any thread.
        /// </summary>
        public static void SetImage(TrackInfo t, Image img, bool replace)
        {
            string key = KeyFor(t);
            Directory.CreateDirectory(Dir);
            string jpg = Path.Combine(Dir, key + ".jpg");
            object keyLock;
            lock (lk) { if (!keyLocks.TryGetValue(key, out keyLock)) keyLocks[key] = keyLock = new object(); }
            lock (keyLock)
            {
                if (!replace && File.Exists(jpg)) return;
                string tmp = jpg + ".tmp";
                using (var fitted = Fit(img, LargeSize)) fitted.Save(tmp, ImageFormat.Jpeg);
                if (File.Exists(jpg)) File.Delete(jpg);
                File.Move(tmp, jpg);
                try { File.Delete(Path.Combine(Dir, key + ".none")); } catch { }
            }
            lock (lk)
            {
                // Images already handed out may still be painted, so they are dropped, not disposed.
                missing.Remove(key);
                thumbs.Remove(key);
                larges.Remove(key);
                largeOrder.Remove(key);
                int v;
                versions.TryGetValue(key, out v);
                versions[key] = v + 1;
            }
            var h = ArtReady;
            if (h != null) { try { h(); } catch { } }
        }

        /// <summary>
        /// Drops the album's cached cover (memory and disk, including one found online or picked) so it is looked up again
        /// from the folder and the files — after the embedded picture was changed or removed. Safe on any thread.
        /// </summary>
        public static void Forget(string key)
        {
            foreach (string ext in new[] { ".jpg", ".none" })
                try { File.Delete(Path.Combine(Dir, key + ext)); } catch { }
            lock (lk)
            {
                missing.Remove(key);
                thumbs.Remove(key);
                larges.Remove(key);
                largeOrder.Remove(key);
                int v;
                versions.TryGetValue(key, out v);
                versions[key] = v + 1;
            }
            var h = ArtReady;
            if (h != null) { try { h(); } catch { } }
        }

        static string Version(string key)
        {
            lock (lk) { int v; return versions.TryGetValue(key, out v) ? "v" + v : ""; }
        }

        static string FindFolderImage(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                string best = null;
                int bestRank = int.MaxValue;
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".bmp") continue;
                    string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    int rank = Array.IndexOf(CoverNames, name);
                    if (rank < 0 && name.StartsWith("albumart")) rank = CoverNames.Length;
                    if (rank >= 0 && rank < bestRank) { best = f; bestRank = rank; }
                }
                return best;
            }
            catch { return null; }
        }

        static Bitmap Fit(Image src, int max)
        {
            double s = Math.Min(1.0, Math.Min((double)max / src.Width, (double)max / src.Height));
            int w = Math.Max(1, (int)(src.Width * s)), h = Math.Max(1, (int)(src.Height * s));
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            return bmp;
        }

        static Bitmap CropSquare(Image src, int size)
        {
            int side = Math.Min(src.Width, src.Height);
            var srcRect = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size), srcRect, GraphicsUnit.Pixel);
            }
            return bmp;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace BitPerfectPlayer
{
    /// <summary>
    /// Animated album covers for Now Playing. Looks in this order: a video next to the music (cover.mp4, folder.mp4, …),
    /// the local cache, then artwork.m8tec.top, a free API that finds Apple Music's animated artwork for an artist + album.
    /// Apple serves those as HLS (.m3u8); ffmpeg copies the best H.264 variant into an .mp4 in the cache, with no re-encoding.
    /// Online lookups send the album artist and album title only.
    /// </summary>
    internal static class AnimatedArt
    {
        public static volatile bool Enabled = true;
        public static volatile bool OnlineEnabled = true;
        /// <summary>Raised (on a worker thread) when a lookup finishes.</summary>
        public static event Action<TrackInfo> Ready;

        static readonly object lk = new object();
        static readonly Dictionary<string, string> results = new Dictionary<string, string>();   // album key → video file; null = none
        static readonly HashSet<string> loading = new HashSet<string>();
        static string CacheDir { get { return Path.Combine(Library.DataDir, "animated-art"); } }
        const string Api = "https://artwork.m8tec.top/api/v1/artwork/search";
        const string UserAgent = "SoDaPlayer/1.0 (Windows music player; uses artwork.m8tec.top)";
        static readonly string[] VideoNames = { "cover", "folder", "front", "animated", "animatedcover", "album" };
        static readonly string[] VideoExts = { ".mp4", ".m4v", ".mov", ".webm", ".mkv" };

        static AnimatedArt()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>Returns true when the result is known (file may still be null = no animated cover). Starts a lookup otherwise.</summary>
        public static bool TryGet(TrackInfo t, out string file)
        {
            file = null;
            if (t == null || !Enabled) return true;
            if (!t.Probed) return false;   // tags (album / album artist) are not known yet
            string key = ArtCache.KeyFor(t);
            lock (lk)
            {
                if (results.TryGetValue(key, out file)) return true;
                if (loading.Add(key)) ThreadPool.QueueUserWorkItem(delegate { Fetch(key, t); });
            }
            return false;
        }

        /// <summary>Forget in-memory results (after a setting changed), so the next request looks again.</summary>
        public static void Reset() { lock (lk) results.Clear(); }

        public static void ClearCache()
        {
            Reset();
            try { if (Directory.Exists(CacheDir)) foreach (string f in Directory.GetFiles(CacheDir)) try { File.Delete(f); } catch { } } catch { }
        }

        static void Fetch(string key, TrackInfo t)
        {
            string file = null;
            bool remember = true;
            try
            {
                // Chosen by hand in the picker (wins over a folder video), or switched off for this album.
                string mp4 = Path.Combine(CacheDir, key + ".mp4");
                if (File.Exists(Path.Combine(CacheDir, key + ".off"))) file = null;
                else if (File.Exists(Path.Combine(CacheDir, key + ".picked")) && File.Exists(mp4)) file = mp4;
                else file = FromFolder(t) ?? FromCacheOrOnline(key, t, out remember);
            }
            catch { file = null; remember = false; }
            lock (lk)
            {
                loading.Remove(key);
                if (remember || file != null) results[key] = file;   // network errors are retried on the next request
            }
            var h = Ready;
            if (h != null) { try { h(t); } catch { } }
        }

        static string FromFolder(TrackInfo t)
        {
            string dir = Path.GetDirectoryName(t.FilePath);
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                string best = null;
                int bestRank = int.MaxValue;
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (Array.IndexOf(VideoExts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                    int rank = Array.IndexOf(VideoNames, Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
                    if (rank >= 0 && rank < bestRank) { best = f; bestRank = rank; }
                }
                return best;
            }
            catch { return null; }
        }

        static string FromCacheOrOnline(string key, TrackInfo t, out bool remember)
        {
            remember = true;
            Directory.CreateDirectory(CacheDir);
            string mp4 = Path.Combine(CacheDir, key + ".mp4");
            string none = Path.Combine(CacheDir, key + ".none");
            if (File.Exists(mp4)) return mp4;
            // "Not found" is remembered for a week; new animated covers appear on Apple Music over time.
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(7)) return null;
            if (!OnlineEnabled) { remember = false; return null; }
            string artist = !string.IsNullOrEmpty(t.AlbumArtist) ? t.AlbumArtist : t.Artist;
            if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(t.Album) || !FfmpegTools.Locate()) return null;

            remember = false;   // from here on, a failure is a network problem, not "no animated cover"
            string body = HttpGet(Api + "?artist=" + Uri.EscapeDataString(artist) + "&album=" + Uri.EscapeDataString(t.Album));
            // "Hybrid Theory (Hi-Res Version)", "Album [Deluxe]": try again without the edition suffix.
            string bare = Regex.Replace(t.Album, @"\s*[\(\[][^\)\]]*[\)\]]\s*$", "").Trim();
            if (body == null && bare.Length > 0 && bare != t.Album)
                body = HttpGet(Api + "?artist=" + Uri.EscapeDataString(artist) + "&album=" + Uri.EscapeDataString(bare));
            var d = body != null ? new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object> : null;
            object u;
            string master = d != null && d.TryGetValue("url", out u) ? u as string : null;
            if (string.IsNullOrEmpty(master)) { MarkNone(none); remember = true; return null; }

            Download(master, mp4);
            remember = true;
            return mp4;
        }

        /// <summary>Copies the best stream of Apple's HLS playlist into <paramref name="mp4"/> (no re-encoding). Throws on failure.</summary>
        public static void Download(string master, string mp4)
        {
            if (!FfmpegTools.Locate()) throw new IOException("FFmpeg was not found");
            Directory.CreateDirectory(Path.GetDirectoryName(mp4));
            string variant = PickVariant(master);
            string tmp = mp4 + ".part.mp4";
            try
            {
                FfmpegTools.RunAndCapture(FfmpegTools.FfmpegPath,
                    "-hide_banner -loglevel error -nostdin -y -i " + FfmpegTools.Quote(variant) +
                    " -map 0:v:0 -an -c copy -movflags +faststart " + FfmpegTools.Quote(tmp), 90000);
                if (!File.Exists(tmp) || new FileInfo(tmp).Length < 1024) throw new IOException("download failed");
                if (File.Exists(mp4)) File.Delete(mp4);
                File.Move(tmp, mp4);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        // ------------------------------------------------------------------ manual choice (the picker)

        /// <summary>
        /// Apple's animated artwork for an album page (music.apple.com/…/album/…), via artwork.m8tec.top. Returns the HLS
        /// playlist URL, or null when the album has no animated artwork. Throws on network errors.
        /// </summary>
        public static string LookupAppleMusicUrl(string albumUrl)
        {
            string body = HttpGet(Api.Replace("/search", "/url") + "?url=" + Uri.EscapeDataString(albumUrl));
            var d = body != null ? new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object> : null;
            object u;
            string master = d != null && d.TryGetValue("url", out u) ? u as string : null;
            return string.IsNullOrEmpty(master) ? null : master;
        }

        /// <summary>A new temporary file for a picker preview (deleted by <see cref="DeletePreviews"/>).</summary>
        public static string NewPreviewPath() { return Path.Combine(CacheDir, "preview-" + Guid.NewGuid().ToString("N") + ".mp4"); }

        public static void DeletePreviews()
        {
            try { foreach (string f in Directory.GetFiles(CacheDir, "preview-*")) try { File.Delete(f); } catch { } } catch { }
        }

        /// <summary>
        /// Uses <paramref name="mp4"/> as the album's animated cover from now on (it wins over a video in the album folder).
        /// The file is moved into the cache. The caller must stop any player that has the album's current video open.
        /// </summary>
        public static void UseForAlbum(TrackInfo t, string mp4)
        {
            string key = ArtCache.KeyFor(t);
            string dest = Path.Combine(CacheDir, key + ".mp4");
            if (!string.Equals(Path.GetFullPath(mp4), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(mp4, dest);
            }
            foreach (string ext in new[] { ".none", ".off" }) try { File.Delete(Path.Combine(CacheDir, key + ext)); } catch { }
            File.WriteAllText(Path.Combine(CacheDir, key + ".picked"), "");
            Changed(key, t);
        }

        /// <summary>No animated cover for this album (also ignores a folder video), until a cover is picked again.</summary>
        public static void DisableForAlbum(TrackInfo t)
        {
            string key = ArtCache.KeyFor(t);
            foreach (string ext in new[] { ".mp4", ".picked" }) try { File.Delete(Path.Combine(CacheDir, key + ext)); } catch { }
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(Path.Combine(CacheDir, key + ".off"), "");
            Changed(key, t);
        }

        public static bool IsDisabled(TrackInfo t)
        {
            try { return File.Exists(Path.Combine(CacheDir, ArtCache.KeyFor(t) + ".off")); } catch { return false; }
        }

        static void Changed(string key, TrackInfo t)
        {
            lock (lk) results.Remove(key);
            var h = Ready;
            if (h != null) { try { h(t); } catch { } }
        }

        static void MarkNone(string none) { try { File.WriteAllText(none, ""); } catch { } }

        static readonly Regex Attr = new Regex(@"([A-Z0-9-]+)=(""[^""]*""|[^,]*)");

        /// <summary>
        /// Picks a stream from Apple's master playlist: H.264 (cheaper to decode than HEVC, and in every ffmpeg build), the
        /// largest up to 1080 px. Falls back to the master playlist itself (ffmpeg then chooses).
        /// </summary>
        static string PickVariant(string master)
        {
            string text;
            try { text = HttpGet(master); } catch { return master; }
            if (text == null) return master;
            string[] lines = text.Replace("\r", "").Split('\n');
            string best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF:")) continue;
                string uri = lines[i + 1].Trim();
                if (uri.Length == 0 || uri.StartsWith("#")) continue;
                string codecs = "", res = "";
                foreach (Match m in Attr.Matches(lines[i].Substring(18)))
                {
                    string v = m.Groups[2].Value.Trim('"');
                    if (m.Groups[1].Value == "CODECS") codecs = v;
                    else if (m.Groups[1].Value == "RESOLUTION") res = v;
                }
                int h = 0;
                int x = res.IndexOf('x');
                if (x > 0) int.TryParse(res.Substring(x + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out h);
                int score = (codecs.StartsWith("avc1") ? 100000 : 0) + (h <= 1080 ? h : 1080 - h);
                if (score > bestScore) { bestScore = score; best = new Uri(new Uri(master), uri).AbsoluteUri; }
            }
            return best ?? master;
        }

        static string HttpGet(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UserAgent;
                req.Timeout = 45000;   // the API looks albums up on Apple Music the first time, which can take a while
                req.ReadWriteTimeout = 45000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                // 404 = no animated artwork for this album. Anything else (offline, timeout, 5xx) is an error, not "none".
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
        }
    }

    /// <summary>
    /// Plays an animated cover: ffmpeg decodes the video (looped, 30 fps, scaled and cropped to exactly w×h) to raw BGRA on a
    /// pipe. A reader thread holds one decoded frame ahead; the UI takes it on the frame clock, so the cover pauses with the
    /// music and never runs ahead. Corners are rounded in the reader thread, then the frame is copied into a GDI DIB that
    /// is drawn with one AlphaBlend.
    /// </summary>
    internal sealed class AnimatedCoverPlayer : IDisposable
    {
        const double Fps = 30;
        public readonly string File;
        public readonly Size Size;
        public readonly int Radius;
        public GdiSurface Surface { get; private set; }
        public bool HasFrame { get; private set; }
        /// <summary>Set when ffmpeg could not play the file (the static cover stays).</summary>
        public bool Failed { get; private set; }

        readonly byte[] frame;
        readonly byte[] mask;      // coverage of one rounded corner, radius × radius
        readonly AutoResetEvent consumed = new AutoResetEvent(false);
        volatile bool ready, stopped;
        Process proc;
        double clock;

        public AnimatedCoverPlayer(string file, Size size, int radius)
        {
            File = file; Size = size;
            Radius = Math.Max(0, Math.Min(radius, Math.Min(size.Width, size.Height) / 2));
            frame = new byte[size.Width * size.Height * 4];
            mask = new byte[Radius * Radius];
            for (int y = 0; y < Radius; y++)
                for (int x = 0; x < Radius; x++)
                {
                    double dx = Radius - (x + 0.5), dy = Radius - (y + 0.5);
                    double cov = Math.Max(0, Math.Min(1, Radius - Math.Sqrt(dx * dx + dy * dy) + 0.5));
                    mask[y * Radius + x] = (byte)Math.Round(cov * 255);
                }
            Surface = new GdiSurface(size.Width, size.Height);
            new Thread(Read) { IsBackground = true, Name = "animated cover", Priority = ThreadPriority.BelowNormal }.Start();
        }

        void Read()
        {
            try
            {
                if (!FfmpegTools.Locate()) throw new IOException("ffmpeg not found");
                int w = Size.Width, h = Size.Height;
                string vf = "fps=" + Fps.ToString(CultureInfo.InvariantCulture) +
                            ",scale=" + w + ":" + h + ":force_original_aspect_ratio=increase:flags=bicubic,crop=" + w + ":" + h;
                var psi = new ProcessStartInfo(FfmpegTools.FfmpegPath,
                    // One thread each: these are small videos, and extra threads mostly add overhead.
                    "-hide_banner -loglevel error -nostdin -threads 1 -stream_loop -1 -i " + FfmpegTools.Quote(File) +
                    " -an -sn -filter_threads 1 -vf " + vf + " -pix_fmt bgra -f rawvideo pipe:1")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                lock (this)
                {
                    if (stopped) return;
                    proc = Process.Start(psi);
                }
                proc.ErrorDataReceived += delegate { };
                proc.BeginErrorReadLine();
                Stream s = proc.StandardOutput.BaseStream;
                bool any = false;
                while (!stopped)
                {
                    int got = 0;
                    while (got < frame.Length)
                    {
                        int n = s.Read(frame, got, frame.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < frame.Length) break;   // ffmpeg ended (unreadable file) or was stopped
                    any = true;
                    RoundCorners();
                    ready = true;
                    while (ready && !stopped) consumed.WaitOne(200);
                }
                if (!any && !stopped) Failed = true;
            }
            catch { if (!stopped) Failed = true; }
        }

        /// <summary>Premultiplies the four corner squares by the rounded-corner coverage (ffmpeg's alpha is opaque).</summary>
        void RoundCorners()
        {
            int w = Size.Width, h = Size.Height, r = Radius;
            for (int y = 0; y < r; y++)
                for (int x = 0; x < r; x++)
                {
                    int a = mask[y * r + x];
                    if (a == 255) continue;
                    Scale((y * w + x) * 4, a);                           // top left
                    Scale((y * w + (w - 1 - x)) * 4, a);                 // top right
                    Scale(((h - 1 - y) * w + x) * 4, a);                 // bottom left
                    Scale(((h - 1 - y) * w + (w - 1 - x)) * 4, a);       // bottom right
                }
        }

        void Scale(int i, int a)
        {
            frame[i] = (byte)(frame[i] * a / 255);
            frame[i + 1] = (byte)(frame[i + 1] * a / 255);
            frame[i + 2] = (byte)(frame[i + 2] * a / 255);
            frame[i + 3] = (byte)a;
        }

        /// <summary>UI thread: advances the cover's clock by dt. Returns true when a new frame was shown.</summary>
        public bool Step(double dt)
        {
            if (Surface == null) return false;
            clock += dt;
            if (HasFrame && clock < 1 / Fps) return false;
            if (!ready) return false;   // decoding is behind; show this frame as soon as it arrives
            clock = HasFrame ? Math.Min(clock - 1 / Fps, 1 / Fps) : 0;
            Marshal.Copy(frame, 0, Surface.Bits, frame.Length);
            ready = false;
            consumed.Set();
            HasFrame = true;
            return true;
        }

        public void Dispose()
        {
            stopped = true;
            consumed.Set();
            lock (this)
            {
                if (proc != null) { try { if (!proc.HasExited) proc.Kill(); } catch { } try { proc.Dispose(); } catch { } proc = null; }
            }
            if (Surface != null) { Surface.Dispose(); Surface = null; }
        }
    }
}

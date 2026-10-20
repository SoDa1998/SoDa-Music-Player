using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace BitPerfectPlayer
{
    internal sealed class LyricLine
    {
        public double Time;   // seconds; -1 for unsynced lines
        public string Text;
    }

    /// <summary>One LRCLIB search result.</summary>
    internal sealed class LyricsCandidate
    {
        public string Title, Artist, Album, Synced, Plain;
        public double Duration;
        public bool Instrumental;
        public bool IsSynced { get { return !string.IsNullOrEmpty(Synced); } }
        /// <summary>The best text: synced LRC when there is one.</summary>
        public string Text { get { return IsSynced ? Synced : Plain ?? ""; } }
    }

    internal sealed class Lyrics
    {
        public bool Synced, Instrumental;
        public string Source = "";
        /// <summary>The text as read (LRC with timestamps when synced), so it can be stored exactly.</summary>
        public string RawText = "";
        public readonly List<LyricLine> Lines = new List<LyricLine>();

        static readonly Regex Stamp = new Regex(@"^\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");
        static readonly Regex Offset = new Regex(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase);
        static readonly Regex MetaTag = new Regex(@"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase);
        // Enhanced ("A2") LRC: a timestamp per word, <mm:ss.xx>. The display follows lines, so these are removed.
        static readonly Regex WordStamp = new Regex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>\s?");

        /// <summary>Parses LRC (timestamped, synced) or plain text lyrics.</summary>
        public static Lyrics Parse(string text, string source)
        {
            var l = new Lyrics { Source = source, RawText = text ?? "" };
            if (string.IsNullOrEmpty(text)) return l;
            // Byte-order marks and zero-width characters (common in tags written by other apps) hide the first timestamp.
            text = text.Replace("﻿", "").Replace("​", "").Replace("‎", "").Replace("‏", "");
            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            double offset = 0;
            var timed = new List<LyricLine>();
            foreach (string r in raw)
            {
                string line = r.Trim();
                Match om = Offset.Match(line);
                if (om.Success) { offset = int.Parse(om.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0; continue; }
                var stamps = new List<double>();
                Match m;
                while ((m = Stamp.Match(line)).Success)
                {
                    double sec = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                                 double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    stamps.Add(sec);
                    line = line.Substring(m.Length);
                }
                string clean = WordStamp.Replace(line, "").Trim();
                foreach (double s in stamps) timed.Add(new LyricLine { Time = Math.Max(0, s - offset), Text = clean });
            }
            if (timed.Count >= 1)
            {
                timed.Sort(delegate(LyricLine a, LyricLine b) { return a.Time.CompareTo(b.Time); });
                l.Synced = true;
                l.Lines.AddRange(timed);
            }
            else
            {
                foreach (string r in raw)
                {
                    string line = r.Trim();
                    if (MetaTag.IsMatch(line)) continue;
                    l.Lines.Add(new LyricLine { Time = -1, Text = WordStamp.Replace(line, "").Trim() });
                }
                while (l.Lines.Count > 0 && l.Lines[l.Lines.Count - 1].Text.Length == 0) l.Lines.RemoveAt(l.Lines.Count - 1);
            }
            // Placeholders some taggers write instead of lyrics ("None", "N/A") mean there are none.
            bool placeholder = true;
            foreach (LyricLine x in l.Lines)
            {
                string tx = x.Text.Trim().ToLowerInvariant();
                if (tx.Length > 0 && tx != "none" && tx != "n/a" && tx != "-") { placeholder = false; break; }
            }
            if (placeholder) { l.Lines.Clear(); l.Synced = false; }
            return l;
        }

        public bool IsEmpty { get { return !Instrumental && Lines.Count == 0; } }
    }

    /// <summary>
    /// Finds lyrics for a track, in this order: lyrics embedded in the file's tags, an .lrc/.txt file next to it, the local cache,
    /// then LRCLIB (lrclib.net — an open, free lyrics database with time-synced lyrics; no API key). Last.fm has no lyrics API and
    /// Genius only links to its web pages, so neither is used. Online lookups send title, artist, album and duration only.
    /// </summary>
    internal static class LyricsService
    {
        public static volatile bool OnlineEnabled = true;
        public const string ErrorSource = "error";
        /// <summary>Raised (on a worker thread) when a lookup finishes.</summary>
        public static event Action<TrackInfo> Ready;

        static readonly object lk = new object();
        // Keyed by file path (not by TrackInfo object), so a song re-added, rescanned or restored from the saved queue reuses its result.
        static readonly Dictionary<string, Lyrics> results = new Dictionary<string, Lyrics>(StringComparer.OrdinalIgnoreCase);   // null value = nothing found
        static readonly HashSet<string> loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string CacheDir { get { return Path.Combine(Library.DataDir, "lyrics"); } }
        const string UserAgent = "SoDaPlayer/1.0 (Windows music player; uses lrclib.net)";

        static LyricsService()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>Returns true when a result is known (lyrics may still be null = not found). Starts a lookup otherwise.</summary>
        public static bool TryGet(TrackInfo t, out Lyrics lyrics)
        {
            lyrics = null;
            if (t == null) return true;
            lock (lk)
            {
                if (results.TryGetValue(t.FilePath, out lyrics)) return true;
                if (loading.Add(t.FilePath)) ThreadPool.QueueUserWorkItem(delegate { Fetch(t); });
            }
            return false;
        }

        public static void ClearCache()
        {
            lock (lk) { results.Clear(); }
            try { if (Directory.Exists(CacheDir)) foreach (string f in Directory.GetFiles(CacheDir)) File.Delete(f); } catch { }
        }

        /// <summary>Forget the in-memory result for a track (e.g. after the online setting changed).</summary>
        public static void Forget(TrackInfo t) { if (t != null) lock (lk) results.Remove(t.FilePath); }

        /// <summary>
        /// Lookup order. A song whose lyrics were found once never goes back to the network (or even to ffprobe):
        ///  1. an .lrc/.txt file next to the song (the user's own file always wins; read directly, not cached);
        ///  2. this file's cache entry, if the audio file hasn't changed since it was written;
        ///  3. lyrics embedded in the file's tags (ffprobe), saved to the cache;
        ///  4. the cache entry again if the file changed but its artist/title/album/length didn't (e.g. only the year was edited);
        ///  5. LRCLIB, saved to the cache: synced LRC whenever LRCLIB has it, else plain text; "not found" is remembered for a week.
        /// </summary>
        static void Fetch(TrackInfo t)
        {
            Lyrics result = null;
            try
            {
                FfmpegTools.Probe(t);
                result = FromSidecar(t);
                if (result == null)
                {
                    CacheEntry entry = ReadEntry(t);
                    long mtime = FileTicks(t.FilePath);
                    string sig = TagSignature(t);
                    if (entry != null && entry.MTime == mtime && entry.Usable) result = entry.ToLyrics();
                    else
                    {
                        result = FromEmbeddedTags(t);
                        if (result != null && !result.IsEmpty) WriteEntry(t, "Embedded in file", result.RawText);
                        else if (entry != null && entry.Online && entry.Sig == sig && entry.Usable)
                        {
                            WriteEntry(t, entry.Source, entry.Text);   // file touched but still the same song: keep the stored lyrics
                            result = entry.ToLyrics();
                        }
                        else
                        {
                            result = FromLegacyCache(t);
                            if (result == null && OnlineEnabled) result = FromLrclib(t);
                        }
                    }
                }
                if (result != null && (result.IsEmpty || result.Source == "notfound")) result = null;
            }
            catch { result = new Lyrics { Source = ErrorSource }; }   // shown as "couldn't reach LRCLIB"; retried when lyrics are reopened (nothing is cached)
            lock (lk) { results[t.FilePath] = result; loading.Remove(t.FilePath); }
            var h = Ready;
            if (h != null) { try { h(t); } catch { } }
        }

        // ------------------------------------------------------------------ local sources

        static Lyrics FromEmbeddedTags(TrackInfo t)
        {
            if (!FfmpegTools.Locate()) return null;
            string json;
            try
            {
                json = FfmpegTools.RunAndCapture(FfmpegTools.FfprobePath,
                    "-v error -print_format json -show_entries format_tags:stream_tags " + FfmpegTools.Quote(t.FilePath), 15000);
            }
            catch { return null; }
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) return null;
            var tagSets = new List<Dictionary<string, object>>();
            object o;
            if (root.TryGetValue("format", out o) && o is Dictionary<string, object>)
            {
                object tags;
                if (((Dictionary<string, object>)o).TryGetValue("tags", out tags) && tags is Dictionary<string, object>) tagSets.Add((Dictionary<string, object>)tags);
            }
            if (root.TryGetValue("streams", out o) && o is object[])
                foreach (object s in (object[])o)
                {
                    var sd = s as Dictionary<string, object>;
                    object tags;
                    if (sd != null && sd.TryGetValue("tags", out tags) && tags is Dictionary<string, object>) tagSets.Add((Dictionary<string, object>)tags);
                }
            string synced = null, plain = null;
            foreach (var tags in tagSets)
                foreach (var kv in tags)
                {
                    string k = kv.Key.ToLowerInvariant();
                    string v = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (k.StartsWith("syncedlyrics") || (k.StartsWith("lyrics") && v.Contains("[0"))) synced = synced ?? v;
                    else if (k.StartsWith("lyrics") || k.StartsWith("unsyncedlyrics") || k == "uslt" || k.StartsWith("©lyr")) plain = plain ?? v;
                }
            string text = synced ?? plain;
            return text == null ? null : Lyrics.Parse(text, "Embedded in file");
        }

        static Lyrics FromSidecar(TrackInfo t)
        {
            try
            {
                string basePath = Path.Combine(Path.GetDirectoryName(t.FilePath) ?? "", Path.GetFileNameWithoutExtension(t.FilePath));
                foreach (string ext in new[] { ".lrc", ".txt" })
                    if (File.Exists(basePath + ext)) return Lyrics.Parse(File.ReadAllText(basePath + ext, Encoding.UTF8), Path.GetFileName(basePath + ext));
            }
            catch { }
            return null;
        }

        static string Md5(string s)
        {
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>What an online match depends on: if these tags are unchanged, stored lyrics are still the right ones.</summary>
        static string TagSignature(TrackInfo t)
        {
            return Md5((t.DisplayArtist + "|" + t.DisplayTitle + "|" + (t.Album ?? "") + "|" + Math.Round(t.Duration)).ToLowerInvariant());
        }

        static long FileTicks(string path)
        {
            try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
        }

        // ---- per-file cache entries: lyrics\f_<md5 of the file path>.lrc
        //   #soda-lyrics 1
        //   #source=LRCLIB | Embedded in file | notfound | instrumental
        //   #mtime=<audio file's last-write ticks>      #sig=<tag signature>      #saved=<UTC ticks>
        //   (blank line, then the lyrics exactly as received: LRC with timestamps when synced)

        sealed class CacheEntry
        {
            public string Source = "", Sig = "", Text = "";
            public long MTime, Saved;
            public bool Online { get { return Source == "LRCLIB" || Source == "notfound" || Source == "instrumental"; } }
            /// <summary>"Not found" is re-checked after a week (LRCLIB keeps growing); real lyrics never expire.</summary>
            public bool Usable { get { return Source != "notfound" || DateTime.UtcNow.Ticks - Saved < TimeSpan.FromDays(7).Ticks; } }
            public Lyrics ToLyrics()
            {
                if (Source == "instrumental") return new Lyrics { Instrumental = true, Source = "LRCLIB" };
                if (Source == "notfound") return new Lyrics { Source = "notfound" };
                return Lyrics.Parse(Text, Source);
            }
        }

        static string EntryPath(TrackInfo t) { return Path.Combine(CacheDir, "f_" + Md5(t.FilePath.ToLowerInvariant()) + ".lrc"); }

        static CacheEntry ReadEntry(TrackInfo t)
        {
            try
            {
                string f = EntryPath(t);
                if (!File.Exists(f)) return null;
                string all = File.ReadAllText(f, Encoding.UTF8).Replace("\r\n", "\n");
                if (!all.StartsWith("#soda-lyrics")) return null;
                var e = new CacheEntry();
                int body = all.IndexOf("\n\n", StringComparison.Ordinal);
                string head = body >= 0 ? all.Substring(0, body) : all;
                e.Text = body >= 0 ? all.Substring(body + 2) : "";
                foreach (string line in head.Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (!line.StartsWith("#") || eq < 0) continue;
                    string k = line.Substring(1, eq - 1), v = line.Substring(eq + 1);
                    if (k == "source") e.Source = v;
                    else if (k == "sig") e.Sig = v;
                    else if (k == "mtime") long.TryParse(v, out e.MTime);
                    else if (k == "saved") long.TryParse(v, out e.Saved);
                }
                return e;
            }
            catch { return null; }
        }

        static void WriteEntry(TrackInfo t, string source, string text)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string content = "#soda-lyrics 1\n#source=" + source + "\n#mtime=" + FileTicks(t.FilePath) + "\n#sig=" + TagSignature(t) +
                                 "\n#saved=" + DateTime.UtcNow.Ticks + "\n\n" + (text ?? "");
                string f = EntryPath(t), tmp = f + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
            }
            catch { }
        }

        /// <summary>Lyrics cached by earlier versions (keyed by tags only). Moved into the file's own entry when found.</summary>
        static Lyrics FromLegacyCache(TrackInfo t)
        {
            try
            {
                string f = Path.Combine(CacheDir, TagSignature(t) + ".lrc");
                if (!File.Exists(f)) return null;
                string text = File.ReadAllText(f, Encoding.UTF8);
                if (text.StartsWith("#notfound")) return null;   // look again (and remember the answer per file)
                if (text.StartsWith("#instrumental")) { WriteEntry(t, "instrumental", ""); return new Lyrics { Instrumental = true, Source = "LRCLIB" }; }
                WriteEntry(t, "LRCLIB", text);
                return Lyrics.Parse(text, "LRCLIB");
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ LRCLIB

        static Lyrics FromLrclib(TrackInfo t)
        {
            if (string.IsNullOrEmpty(t.Title) || string.IsNullOrEmpty(t.Artist)) return null;   // need real tags to search
            var q = new StringBuilder("track_name=").Append(Uri.EscapeDataString(t.Title))
                .Append("&artist_name=").Append(Uri.EscapeDataString(t.Artist));
            string exact = q.ToString();
            if (!string.IsNullOrEmpty(t.Album)) exact += "&album_name=" + Uri.EscapeDataString(t.Album);
            if (t.Duration > 0) exact += "&duration=" + ((int)Math.Round(t.Duration)).ToString(CultureInfo.InvariantCulture);

            Dictionary<string, object> best = null;
            string body = HttpGet("https://lrclib.net/api/get?" + exact);
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (body != null) best = js.DeserializeObject(body) as Dictionary<string, object>;
            // The exact match can be plain text only while another entry for the same recording has timestamps: look for that.
            bool unsynced = best != null && string.IsNullOrEmpty(Str(best, "syncedLyrics")) && !(best.ContainsKey("instrumental") && best["instrumental"] is bool && (bool)best["instrumental"]);
            if (best == null || unsynced)
            {
                Dictionary<string, object> exactMatch = best;
                best = null;
                try { body = HttpGet("https://lrclib.net/api/search?" + q); }
                catch { if (exactMatch == null) throw; body = null; }   // we already have the plain lyrics: keep them
                var arr = body != null ? js.DeserializeObject(body) as object[] : null;
                if (arr != null)
                {
                    double bestScore = double.MaxValue;
                    foreach (object o in arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        double dur = Num(d, "duration");
                        double diff = t.Duration > 0 && dur > 0 ? Math.Abs(dur - t.Duration) : 0;
                        if (t.Duration > 0 && dur > 0 && diff > 5) continue;   // a different recording
                        bool synced = !string.IsNullOrEmpty(Str(d, "syncedLyrics"));
                        // Replacing an exact plain-text match: only a synced version of the same recording (within 2 s) will do.
                        if (exactMatch != null && (!synced || (t.Duration > 0 && dur > 0 && diff > 2))) continue;
                        double score = diff + (synced ? 0 : 10);
                        if (score < bestScore) { bestScore = score; best = d; }
                    }
                }
                if (best == null) best = exactMatch;
            }

            if (best == null) { WriteEntry(t, "notfound", ""); return null; }
            object instr;
            if (best.TryGetValue("instrumental", out instr) && instr is bool && (bool)instr)
            {
                WriteEntry(t, "instrumental", "");
                return new Lyrics { Instrumental = true, Source = "LRCLIB" };
            }
            string text = Str(best, "syncedLyrics");
            if (string.IsNullOrEmpty(text)) text = Str(best, "plainLyrics");
            if (string.IsNullOrEmpty(text)) { WriteEntry(t, "notfound", ""); return null; }
            WriteEntry(t, "LRCLIB", text);
            return Lyrics.Parse(text, "LRCLIB");
        }

        // ------------------------------------------------------------------ manual search (the "Search lyrics" dialog)

        /// <summary>
        /// Searches LRCLIB. With an artist: by title + artist; without one: a free-text search over every field. Results
        /// are ordered synced first, then by how close their length is to the song's (<paramref name="duration"/>, 0 = unknown).
        /// </summary>
        public static List<LyricsCandidate> Search(string title, string artist, double duration)
        {
            string q = string.IsNullOrWhiteSpace(artist)
                ? "q=" + Uri.EscapeDataString(title.Trim())
                : "track_name=" + Uri.EscapeDataString(title.Trim()) + "&artist_name=" + Uri.EscapeDataString(artist.Trim());
            string body = HttpGet("https://lrclib.net/api/search?" + q);
            var list = new List<LyricsCandidate>();
            var arr = body != null ? new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(body) as object[] : null;
            if (arr == null) return list;
            foreach (object o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                object instr;
                var c = new LyricsCandidate
                {
                    Title = Str(d, "trackName") ?? "", Artist = Str(d, "artistName") ?? "", Album = Str(d, "albumName") ?? "",
                    Duration = Num(d, "duration"), Synced = Str(d, "syncedLyrics"), Plain = Str(d, "plainLyrics"),
                    Instrumental = d.TryGetValue("instrumental", out instr) && instr is bool && (bool)instr
                };
                if (string.IsNullOrEmpty(c.Synced) && string.IsNullOrEmpty(c.Plain) && !c.Instrumental) continue;
                list.Add(c);
            }
            list.Sort(delegate(LyricsCandidate a, LyricsCandidate b)
            {
                int s = (b.IsSynced ? 1 : 0).CompareTo(a.IsSynced ? 1 : 0);
                if (s != 0) return s;
                double da = duration > 0 && a.Duration > 0 ? Math.Abs(a.Duration - duration) : 999, db = duration > 0 && b.Duration > 0 ? Math.Abs(b.Duration - duration) : 999;
                return da.CompareTo(db);
            });
            return list;
        }

        /// <summary>Makes <paramref name="text"/> this song's lyrics in SoDa Player (stored in the lyrics cache, survives restarts).</summary>
        public static void UseForSong(TrackInfo t, string text, string source)
        {
            WriteEntry(t, source, text);
            Publish(t, Lyrics.Parse(text, source));
        }

        /// <summary>Saves <paramref name="text"/> as an .lrc file next to the song (other players read it too). Returns null or an error.</summary>
        public static string SaveLrcFile(TrackInfo t, string text)
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(t.FilePath) ?? "", Path.GetFileNameWithoutExtension(t.FilePath) + ".lrc");
                File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
                Publish(t, Lyrics.Parse(text, Path.GetFileName(path)));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Writes <paramref name="text"/> into the song's own tags (MP3: USLT frame; FLAC/Ogg: LYRICS; M4A: ©lyr), so the
        /// lyrics travel with the file. Slow (the file is rewritten): call off the UI thread. Returns null or an error.
        /// </summary>
        public static string EmbedInFile(TrackInfo t, string text)
        {
            string err = TagWriter.Write(t.FilePath, new Dictionary<string, string> { { "lyrics", text.Replace("\r\n", "\n") } });
            if (err != null) return err;
            WriteEntry(t, "Embedded in file", text);   // after the rewrite: the entry carries the new file time
            Publish(t, Lyrics.Parse(text, "Embedded in file"));
            return null;
        }

        static void Publish(TrackInfo t, Lyrics l)
        {
            lock (lk) { results[t.FilePath] = l != null && l.IsEmpty ? null : l; loading.Remove(t.FilePath); }
            var h = Ready;
            if (h != null) { try { h(t); } catch { } }
        }

        /// <summary>Number of requests sent to LRCLIB in this process (for the self-test).</summary>
        internal static int NetworkRequests;

        static string HttpGet(string url)
        {
            Interlocked.Increment(ref NetworkRequests);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UserAgent;
                req.Timeout = 12000;
                req.ReadWriteTimeout = 12000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                // 404 = not in LRCLIB. Anything else (offline, timeout, server error) aborts the lookup without caching "not found".
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : null;
        }

        static double Num(Dictionary<string, object> d, string key)
        {
            double v;
            return double.TryParse(Str(d, key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }
}

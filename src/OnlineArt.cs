using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BitPerfectPlayer
{
    /// <summary>A cover found online.</summary>
    internal sealed class ArtCandidate
    {
        public string Source, Album, Artist, Year;
        public string ThumbUrl;     // ~300 px, for the picker
        public string FullUrl;      // ~1200 px
        public string FallbackUrl;  // smaller full image if the large one is missing
        public string PageUrl;      // the album's Apple Music page (iTunes results only), for animated artwork
        public Image Thumb;         // set by the picker once downloaded
    }

    /// <summary>
    /// Album art from the internet, for albums with no embedded picture and no cover image in their folder.
    /// <para>Automatic: when <see cref="Enabled"/>, missing covers are looked up on the iTunes Search API (free, no key) one at a
    /// time in the background, at most one request every 3 s (its limit is about 20 a minute), and used only when album and
    /// artist clearly match. Albums with no match are not asked again for 30 days.</para>
    /// <para>Manual: <see cref="Search"/> asks iTunes and MusicBrainz / Cover Art Archive and returns candidates for the
    /// "Find album art online" picker. Only the artist and album title are sent.</para>
    /// </summary>
    internal static class OnlineArt
    {
        public static volatile bool Enabled = true;
        const string UserAgent = "SoDaPlayer/1.0 (Windows music player; https://github.com/)";

        static readonly BlockingCollection<KeyValuePair<string, TrackInfo>> queue =
            new BlockingCollection<KeyValuePair<string, TrackInfo>>(new ConcurrentStack<KeyValuePair<string, TrackInfo>>());
        static readonly HashSet<string> queued = new HashSet<string>();
        static readonly object lk = new object();
        static readonly object rate = new object();
        static int lastITunes = Environment.TickCount - 10000, lastMusicBrainz = Environment.TickCount - 10000;

        static string Dir { get { return Path.Combine(Library.DataDir, "art"); } }

        static OnlineArt()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            new Thread(Worker) { IsBackground = true, Name = "online art", Priority = ThreadPriority.BelowNormal }.Start();
        }

        /// <summary>Queue a background lookup for an album with no local art (called by ArtCache).</summary>
        public static void Enqueue(string key, TrackInfo t)
        {
            if (!Enabled || t == null) return;
            string marker = Path.Combine(Dir, key + ".online-none");
            try { if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(30)) return; } catch { }
            lock (lk) { if (!queued.Add(key)) return; }
            queue.Add(new KeyValuePair<string, TrackInfo>(key, t));
        }

        /// <summary>Forget "not found online" results (e.g. after the setting was turned on again or a rescan).</summary>
        public static void ForgetMisses()
        {
            lock (lk) queued.Clear();
            try { foreach (string f in Directory.GetFiles(Dir, "*.online-none")) File.Delete(f); } catch { }
        }

        static void Worker()
        {
            foreach (var req in queue.GetConsumingEnumerable())
            {
                // Errors (offline, rate limited) leave no marker, so the album is tried again next time its art is requested.
                try
                {
                    if (!Enabled) continue;
                    TrackInfo t = req.Value;
                    string artist = AlbumArtist(t), album = t.Album;
                    if (string.IsNullOrEmpty(album)) { MarkMiss(req.Key); continue; }
                    ArtCandidate best = null;
                    int bestScore = 0;
                    foreach (ArtCandidate c in SearchITunes(artist + " " + album, 15))
                    {
                        int s = MatchScore(c, artist, album);
                        if (s > bestScore) { bestScore = s; best = c; }
                    }
                    if (best == null) { MarkMiss(req.Key); continue; }
                    Image img = DownloadImage(best);
                    if (img != null) using (img) ArtCache.SetImage(t, img, false);
                }
                catch { }
                finally { lock (lk) queued.Remove(req.Key); }
            }
        }

        static void MarkMiss(string key)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, key + ".online-none"), ""); } catch { }
        }

        public static string AlbumArtist(TrackInfo t)
        {
            return !string.IsNullOrEmpty(t.AlbumArtist) ? t.AlbumArtist : t.Artist ?? "";
        }

        // ------------------------------------------------------------------ matching

        /// <summary>0 = not this album. Album titles must match after normalising ("(Deluxe Edition)", punctuation and accents
        /// are ignored); the artist must match too unless the album is a compilation (Various Artists).</summary>
        static int MatchScore(ArtCandidate c, string artist, string album)
        {
            string a = Norm(album), ca = Norm(c.Album);
            if (a.Length == 0 || ca.Length == 0) return 0;
            int score;
            if (ca == a) score = 100;
            else if (StripExtras(ca) == StripExtras(a)) score = 80;
            else if (StripExtras(ca).StartsWith(StripExtras(a)) && StripExtras(a).Length >= 4) score = 50;
            else return 0;
            string ar = Norm(artist), car = Norm(c.Artist);
            bool various = ar.Length == 0 || ar.Contains("various");
            if (!various)
            {
                if (car == ar) score += 50;
                else if (car.Contains(ar) || ar.Contains(car)) score += 30;
                else return 0;
            }
            return score;
        }

        /// <summary>Lowercase, accents removed, letters and digits only (spaces collapsed).</summary>
        static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            bool space = false;
            foreach (char ch in d)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) { sb.Append(ch); space = false; }
                else if (ch == '(' || ch == '[') { sb.Append(ch); space = false; }
                else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Drops bracketed parts and " - single/ep" style suffixes.</summary>
        static string StripExtras(string n)
        {
            int p = n.IndexOfAny(new[] { '(', '[' });
            if (p > 0) n = n.Substring(0, p);
            foreach (string suffix in new[] { " single", " ep" }) if (n.EndsWith(suffix)) n = n.Substring(0, n.Length - suffix.Length);
            return n.Replace("(", "").Replace("[", "").Trim();
        }

        // ------------------------------------------------------------------ sources

        /// <summary>Asks iTunes and MusicBrainz in parallel. Either may fail; throws only when both do.</summary>
        public static List<ArtCandidate> Search(string query)
        {
            var results = new List<ArtCandidate>[2];
            var errors = new Exception[2];
            var done = new CountdownEvent(2);
            ThreadPool.QueueUserWorkItem(delegate { try { results[0] = SearchITunes(query, 20); } catch (Exception ex) { errors[0] = ex; } done.Signal(); });
            ThreadPool.QueueUserWorkItem(delegate { try { results[1] = SearchMusicBrainz(query, 12); } catch (Exception ex) { errors[1] = ex; } done.Signal(); });
            done.Wait(40000);
            if (results[0] == null && results[1] == null) throw errors[0] ?? errors[1] ?? new TimeoutException("no answer");
            var all = new List<ArtCandidate>();
            if (results[0] != null) all.AddRange(results[0]);
            if (results[1] != null) all.AddRange(results[1]);
            return all;
        }

        /// <summary>Albums on Apple Music (iTunes Search API), each with its page URL — the animated artwork picker's results.</summary>
        public static List<ArtCandidate> SearchAppleMusicAlbums(string term) { return SearchITunes(term, 15); }

        static List<ArtCandidate> SearchITunes(string term, int limit)
        {
            Throttle(ref lastITunes, 3000);
            string body = GetString("https://itunes.apple.com/search?media=music&entity=album&limit=" + limit + "&term=" + Uri.EscapeDataString(term.Trim()));
            var list = new List<ArtCandidate>();
            var root = body != null ? new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object> : null;
            object o;
            if (root == null || !root.TryGetValue("results", out o) || !(o is object[])) return list;
            foreach (object r in (object[])o)
            {
                var d = r as Dictionary<string, object>;
                string art = Str(d, "artworkUrl100");
                if (d == null || string.IsNullOrEmpty(art) || !art.Contains("100x100")) continue;
                string date = Str(d, "releaseDate");
                list.Add(new ArtCandidate
                {
                    Source = "iTunes",
                    Album = Str(d, "collectionName"),
                    Artist = Str(d, "artistName"),
                    Year = date != null && date.Length >= 4 ? date.Substring(0, 4) : "",
                    ThumbUrl = art.Replace("100x100", "300x300"),
                    FullUrl = art.Replace("100x100", "1200x1200"),
                    FallbackUrl = art.Replace("100x100", "600x600"),
                    PageUrl = Str(d, "collectionViewUrl")
                });
            }
            return list;
        }

        static List<ArtCandidate> SearchMusicBrainz(string term, int limit)
        {
            Throttle(ref lastMusicBrainz, 1100);   // MusicBrainz asks for at most one request a second
            string body = GetString("https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=" + limit + "&query=" + Uri.EscapeDataString(term.Trim()));
            var list = new List<ArtCandidate>();
            var root = body != null ? new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object> : null;
            object o;
            if (root == null || !root.TryGetValue("release-groups", out o) || !(o is object[])) return list;
            foreach (object r in (object[])o)
            {
                var d = r as Dictionary<string, object>;
                string id = Str(d, "id");
                if (string.IsNullOrEmpty(id)) continue;
                string artist = "";
                object ac;
                if (d.TryGetValue("artist-credit", out ac) && ac is object[] && ((object[])ac).Length > 0)
                    foreach (object part in (object[])ac)
                    {
                        var pd = part as Dictionary<string, object>;
                        if (pd != null) artist += Str(pd, "name") + (Str(pd, "joinphrase") ?? "");
                    }
                string date = Str(d, "first-release-date");
                string baseUrl = "https://coverartarchive.org/release-group/" + id + "/front";
                list.Add(new ArtCandidate
                {
                    Source = "MusicBrainz",
                    Album = Str(d, "title"),
                    Artist = artist,
                    Year = date != null && date.Length >= 4 ? date.Substring(0, 4) : "",
                    ThumbUrl = baseUrl + "-250",
                    FullUrl = baseUrl + "-1200",
                    FallbackUrl = baseUrl
                });
            }
            return list;
        }

        static void Throttle(ref int last, int gapMs)
        {
            lock (rate)
            {
                int wait = gapMs - unchecked(Environment.TickCount - last);
                if (wait > 0) Thread.Sleep(wait);
                last = Environment.TickCount;
            }
        }

        /// <summary>Downloads the large image (falling back to the smaller one). Null when neither is available.</summary>
        public static Image DownloadImage(ArtCandidate c)
        {
            foreach (string url in new[] { c.FullUrl, c.FallbackUrl })
            {
                if (string.IsNullOrEmpty(url)) continue;
                byte[] data = GetBytes(url);
                Image img = ToImage(data);
                if (img != null) return img;
            }
            return null;
        }

        /// <summary>The image file bytes of the large image (for saving cover.jpg unchanged), or null.</summary>
        public static byte[] DownloadBytes(ArtCandidate c)
        {
            foreach (string url in new[] { c.FullUrl, c.FallbackUrl })
            {
                if (string.IsNullOrEmpty(url)) continue;
                byte[] data = GetBytes(url);
                using (Image img = ToImage(data)) if (img != null) return data;
            }
            return null;
        }

        public static Image ToImage(byte[] data)
        {
            if (data == null || data.Length < 100) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (Image src = Image.FromStream(ms))
                    return new Bitmap(src);   // detached from the stream
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ HTTP

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        static string GetString(string url)
        {
            byte[] b = GetBytes(url);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        /// <summary>Null on 404 (and on Cover Art Archive's 5xx for "no image"); throws on network errors.</summary>
        public static byte[] GetBytes(string url)
        {
            try
            {
                using (var resp = (HttpWebResponse)Request(url).GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && (resp.StatusCode == HttpStatusCode.NotFound || (url.Contains("coverartarchive.org") && (int)resp.StatusCode >= 500))) return null;
                throw;
            }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : null;
        }
    }
}

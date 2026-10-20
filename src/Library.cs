using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BitPerfectPlayer
{
    internal enum SongSort { NameAsc, NameDesc, DateAddedNewest, DateAddedOldest }

    internal sealed class AlbumInfo
    {
        public string Key, Title, Artist, Year;
        public readonly List<TrackInfo> Tracks = new List<TrackInfo>();
        public TrackInfo ArtTrack { get { return Tracks.Count > 0 ? Tracks[0] : null; } }

        public double Duration
        {
            get { double d = 0; foreach (TrackInfo t in Tracks) d += t.Duration; return d; }
        }
    }

    internal sealed class ArtistInfo
    {
        public string Name;
        public readonly List<TrackInfo> Tracks = new List<TrackInfo>();
        public readonly List<AlbumInfo> Albums = new List<AlbumInfo>();
        public TrackInfo ArtTrack { get { return Albums.Count > 0 ? Albums[0].ArtTrack : (Tracks.Count > 0 ? Tracks[0] : null); } }
    }

    /// <summary>A folder in the Folders page: its sub-folders, the songs directly inside it, and the song count of the whole subtree.</summary>
    internal sealed class FolderNode
    {
        public string Path, Name;
        public FolderNode Parent;
        public bool IsRoot;   // one of the library's music folders (its row also shows the full path)
        public readonly List<FolderNode> Children = new List<FolderNode>();
        public readonly List<TrackInfo> Songs = new List<TrackInfo>();
        public int TotalSongs;

        /// <summary>Every song in this folder and below, in folder order.</summary>
        public List<TrackInfo> AllSongs()
        {
            var list = new List<TrackInfo>(Songs);
            foreach (FolderNode c in Children) list.AddRange(c.AllSongs());
            return list;
        }

        /// <summary>
        /// Builds the folder tree. The roots are the library's music folders; songs added on their own (outside every music
        /// folder) get their containing folder as an extra root. Pure function of its input, so it can run on a worker thread.
        /// </summary>
        public static List<FolderNode> Build(List<TrackInfo> songs, List<string> libraryFolders)
        {
            var nodes = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<FolderNode>();
            var rootPaths = new List<string>();
            foreach (string f in libraryFolders) { string p = f.TrimEnd('\\'); if (p.Length > 0 && !rootPaths.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) rootPaths.Add(p); }

            Func<string, string> rootOf = delegate(string dir)
            {
                string best = null;
                foreach (string r in rootPaths)
                    if ((string.Equals(dir, r, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) &&
                        (best == null || r.Length > best.Length)) best = r;
                return best ?? dir;
            };
            Func<string, string, FolderNode> ensure = null;
            ensure = delegate(string dir, string root)
            {
                FolderNode n;
                if (nodes.TryGetValue(dir, out n)) return n;
                bool isRoot = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
                string leaf = System.IO.Path.GetFileName(dir);
                n = new FolderNode { Path = dir, Name = string.IsNullOrEmpty(leaf) ? dir : leaf, IsRoot = isRoot };
                nodes[dir] = n;
                if (isRoot) roots.Add(n);
                else
                {
                    string parentDir = System.IO.Path.GetDirectoryName(dir);
                    n.Parent = ensure(string.IsNullOrEmpty(parentDir) ? root : parentDir, root);
                    n.Parent.Children.Add(n);
                }
                return n;
            };

            foreach (TrackInfo t in songs)
            {
                if (t.Probed && t.ProbeError != null) continue;
                string dir = System.IO.Path.GetDirectoryName(t.FilePath);
                if (string.IsNullOrEmpty(dir)) continue;
                FolderNode n = ensure(dir, rootOf(dir));
                n.Songs.Add(t);
            }
            Comparison<FolderNode> byName = delegate(FolderNode a, FolderNode b) { return Library.CompareText(a.Name, b.Name); };
            Action<FolderNode> finish = null;
            finish = delegate(FolderNode n)
            {
                n.Children.Sort(byName);
                n.Songs.Sort(delegate(TrackInfo a, TrackInfo b)
                {
                    int c = a.Disc.CompareTo(b.Disc);
                    if (c == 0) c = a.TrackNo.CompareTo(b.TrackNo);
                    return c != 0 ? c : Library.CompareText(System.IO.Path.GetFileName(a.FilePath), System.IO.Path.GetFileName(b.FilePath));
                });
                n.TotalSongs = n.Songs.Count;
                foreach (FolderNode c in n.Children) { finish(c); n.TotalSongs += c.TotalSongs; }
            };
            foreach (FolderNode r in roots) finish(r);
            roots.Sort(byName);
            return roots;
        }
    }

    /// <summary>
    /// The music library: every song the user added (with the time it was added), the watched folders,
    /// and the derived Albums / Artists indexes. Mutated on the UI thread only.
    /// </summary>
    internal sealed class Library
    {
        public readonly List<TrackInfo> Songs = new List<TrackInfo>();
        public readonly List<string> Folders = new List<string>();
        public List<AlbumInfo> Albums = new List<AlbumInfo>();
        public List<ArtistInfo> Artists = new List<ArtistInfo>();
        readonly Dictionary<string, TrackInfo> byPath = new Dictionary<string, TrackInfo>(StringComparer.OrdinalIgnoreCase);

        static readonly CompareInfo Cmp = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions CmpOpts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        public static string DataDir
        {
            get
            {
                // BPP_DATA_DIR lets a separate profile (e.g. for testing) keep its own library and settings.
                string custom = Environment.GetEnvironmentVariable("BPP_DATA_DIR");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoDaPlayer");
            }
        }

        /// <summary>The app used to be called BitPerfect Player: move its data folder (library, queue, settings, caches) to the new name once.</summary>
        public static void MigrateOldDataDir()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BPP_DATA_DIR"))) return;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string old = Path.Combine(appData, "BitPerfectPlayer"), now = Path.Combine(appData, "SoDaPlayer");
            try
            {
                if (Directory.Exists(old) && !Directory.Exists(now)) Directory.Move(old, now);
            }
            catch { }
        }

        static string LibraryFile { get { return Path.Combine(DataDir, "library.tsv"); } }

        public TrackInfo Get(string path)
        {
            TrackInfo t;
            return byPath.TryGetValue(path, out t) ? t : null;
        }

        /// <summary>Adds a file; returns the new entry, or null when it is already in the library.</summary>
        public TrackInfo Add(string path, DateTime addedUtc)
        {
            if (byPath.ContainsKey(path)) return null;
            var t = new TrackInfo(path) { DateAdded = addedUtc };
            try
            {
                var fi = new FileInfo(path);
                t.FileCreated = fi.CreationTimeUtc;
                t.FileModified = fi.LastWriteTimeUtc;
            }
            catch { }
            Songs.Add(t);
            byPath[path] = t;
            return t;
        }

        public void Remove(TrackInfo t)
        {
            if (t == null) return;
            Songs.Remove(t);
            byPath.Remove(t.FilePath);
        }

        public void Clear()
        {
            Songs.Clear();
            byPath.Clear();
            Albums = new List<AlbumInfo>();
            Artists = new List<ArtistInfo>();
        }

        // ------------------------------------------------------------------ grouping

        public static string AlbumTitle(TrackInfo t)
        {
            if (!string.IsNullOrEmpty(t.Album)) return t.Album;
            string dir = Path.GetFileName(Path.GetDirectoryName(t.FilePath) ?? "");
            return string.IsNullOrEmpty(dir) ? "Unknown Album" : dir;
        }

        /// <summary>
        /// Multi-value artist tags ("LINKIN PARK;LINKIN PARK", "A; B", NUL-separated ID3v2.4 values): split, drop repeats, join with ", ".
        /// Only ';' and NUL are separators: '/' and '&amp;' are part of real names (AC/DC, Simon &amp; Garfunkel).
        /// </summary>
        public static string CleanArtist(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var parts = new List<string>();
            foreach (string p in s.Split(new[] { ';', '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string n = p.Trim();
                if (n.Length > 0 && !parts.Exists(x => CompareText(x, n) == 0)) parts.Add(n);
            }
            return parts.Count == 0 ? s.Trim() : string.Join(", ", parts.ToArray());
        }

        /// <summary>The album a track belongs to in the Albums page (after merging); falls back to its own key before the first index.</summary>
        public static string AlbumGroupKey(TrackInfo t) { return t.AlbumGroup ?? AlbumKey(t); }

        /// <summary>Album title for grouping: case/accents ignored, disc markers such as "(Disc 2)", "[CD1]", "- Disc 1" removed.</summary>
        static string NormAlbumTitle(string title)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(title ?? "",
                @"\s*[\(\[\-–:]?\s*(disc|disk|cd)\s*\d+\s*[\)\]]?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s.Trim().ToLowerInvariant();
        }

        /// <summary>The album's folder: disc sub-folders ("CD1", "Disc 2", "[Disc 3] Tishnagi", "CD 1 - Score Presentation") count as their parent.</summary>
        static string AlbumFolder(TrackInfo t)
        {
            string dir = Path.GetDirectoryName(t.FilePath) ?? "";
            string name = Path.GetFileName(dir);
            if (System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"^[\[\(]?\s*(cd|disc|disk)\s*\d+\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                dir = Path.GetDirectoryName(dir) ?? dir;
            return dir.ToLowerInvariant();
        }

        /// <summary>First artist of an artist string (for matching "J Balvin" with "J Balvin, Bad Bunny").</summary>
        static string PrimaryArtist(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.IndexOf(", ", StringComparison.Ordinal);
            return (i > 0 ? s.Substring(0, i) : s).Trim().ToLowerInvariant();
        }

        /// <summary>Tracks with an album artist group by (album artist, album); untagged/compilation tracks group by (album, folder).
        /// Used per track (e.g. for the artwork cache); the Albums page merges these further, see BuildIndex.</summary>
        public static string AlbumKey(TrackInfo t)
        {
            if (!string.IsNullOrEmpty(t.AlbumArtist) && !string.IsNullOrEmpty(t.Album))
                return ("aa|" + t.AlbumArtist + "|" + t.Album).ToLowerInvariant();
            return ("af|" + AlbumTitle(t) + "|" + (Path.GetDirectoryName(t.FilePath) ?? "")).ToLowerInvariant();
        }

        public static int CompareText(string a, string b)
        {
            return Cmp.Compare(a ?? "", b ?? "", CmpOpts);
        }

        static int CompareAlbumOrder(TrackInfo a, TrackInfo b)
        {
            int c = a.Disc.CompareTo(b.Disc);
            if (c == 0) c = a.TrackNo.CompareTo(b.TrackNo);
            if (c == 0) c = CompareText(Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath));
            return c;
        }

        public void RebuildIndex()
        {
            List<AlbumInfo> a; List<ArtistInfo> b;
            BuildIndex(Songs, out a, out b);
            Albums = a; Artists = b;
        }

        /// <summary>
        /// Decides which tracks form one album, so an album never shows up twice in the Albums page. Tracks are first split by
        /// (normalised) album title; within a title, tracks are joined when they share an album artist (its first name, so
        /// "J Balvin" and "J Balvin, Bad Bunny" match) or sit in the same album folder (disc sub-folders count as the album's
        /// folder). So an album whose tracks only partly carry an album-artist tag, spell it differently, or are split into
        /// CD1/CD2 folders becomes one entry, while two different albums with the same name (different artists and folders) stay apart.
        /// </summary>
        static Dictionary<TrackInfo, string> GroupAlbums(List<TrackInfo> songs)
        {
            var result = new Dictionary<TrackInfo, string>();
            var byTitle = new Dictionary<string, List<TrackInfo>>();
            foreach (TrackInfo t in songs)
            {
                if (!t.Probed || t.ProbeError != null) continue;
                string title = NormAlbumTitle(AlbumTitle(t));
                List<TrackInfo> list;
                if (!byTitle.TryGetValue(title, out list)) byTitle[title] = list = new List<TrackInfo>();
                list.Add(t);
            }
            foreach (var kv in byTitle)
            {
                List<TrackInfo> list = kv.Value;
                int n = list.Count;
                var parent = new int[n];
                for (int i = 0; i < n; i++) parent[i] = i;
                Func<int, int> find = null;
                find = delegate(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; };
                var firstWith = new Dictionary<string, int>();
                for (int i = 0; i < n; i++)
                {
                    string aa = PrimaryArtist(list[i].AlbumArtist);
                    foreach (string link in new[] { "f|" + AlbumFolder(list[i]), aa != null ? "a|" + aa : null })
                    {
                        if (link == null) continue;
                        int j;
                        if (firstWith.TryGetValue(link, out j)) parent[find(i)] = find(j);
                        else firstWith[link] = i;
                    }
                }
                // Key each group by its first member's own key: stable while the album keeps its tracks.
                var rootKey = new Dictionary<int, string>();
                for (int i = 0; i < n; i++)
                {
                    int r = find(i);
                    string key;
                    if (!rootKey.TryGetValue(r, out key)) rootKey[r] = key = AlbumKey(list[i]);
                    result[list[i]] = key;
                }
            }
            return result;
        }

        /// <summary>Groups songs into albums and artists. Pure function of its input, so it can run on a worker thread.</summary>
        public static void BuildIndex(List<TrackInfo> songs, out List<AlbumInfo> albumsOut, out List<ArtistInfo> artistsOut)
        {
            var albums = new Dictionary<string, AlbumInfo>();
            var artists = new Dictionary<string, ArtistInfo>(StringComparer.CurrentCultureIgnoreCase);
            Dictionary<TrackInfo, string> groupOf = GroupAlbums(songs);
            foreach (TrackInfo t in songs)
            {
                if (!t.Probed || t.ProbeError != null) continue;
                string key = groupOf[t];
                t.AlbumGroup = key;
                AlbumInfo al;
                if (!albums.TryGetValue(key, out al))
                {
                    al = new AlbumInfo { Key = key, Title = AlbumTitle(t), Artist = t.AlbumArtist, Year = t.Year };
                    albums[key] = al;
                }
                al.Tracks.Add(t);
                if (string.IsNullOrEmpty(al.Year)) al.Year = t.Year;
                if (string.IsNullOrEmpty(al.Artist)) al.Artist = t.AlbumArtist;   // any track's album artist names the album

                string an = t.DisplayArtist;
                ArtistInfo ar;
                if (!artists.TryGetValue(an, out ar)) { ar = new ArtistInfo { Name = an }; artists[an] = ar; }
                ar.Tracks.Add(t);
            }

            foreach (AlbumInfo al in albums.Values)
            {
                al.Tracks.Sort(CompareAlbumOrder);
                if (string.IsNullOrEmpty(al.Artist))
                {
                    // One main artist (features allowed: "J Balvin" and "J Balvin, Bad Bunny") names the album; otherwise Various Artists.
                    string first = PrimaryArtist(al.Tracks[0].DisplayArtist);
                    bool same = true;
                    foreach (TrackInfo t in al.Tracks) if (CompareText(PrimaryArtist(t.DisplayArtist), first) != 0) { same = false; break; }
                    string firstName = al.Tracks[0].DisplayArtist;
                    int comma = firstName.IndexOf(", ", StringComparison.Ordinal);
                    al.Artist = same ? (comma > 0 ? firstName.Substring(0, comma) : firstName) : "Various Artists";
                }
            }

            foreach (ArtistInfo ar in artists.Values)
            {
                var seen = new HashSet<AlbumInfo>();
                foreach (TrackInfo t in ar.Tracks)
                {
                    AlbumInfo al;
                    if (albums.TryGetValue(groupOf[t], out al) && seen.Add(al)) ar.Albums.Add(al);
                }
                ar.Albums.Sort(delegate(AlbumInfo x, AlbumInfo y)
                {
                    int c = string.CompareOrdinal(x.Year ?? "", y.Year ?? "");
                    return c != 0 ? c : CompareText(x.Title, y.Title);
                });
                // Artist page order: album by album, then track order.
                var ordered = new List<TrackInfo>();
                foreach (AlbumInfo al in ar.Albums)
                    foreach (TrackInfo t in al.Tracks)
                        if (CompareText(t.DisplayArtist, ar.Name) == 0) ordered.Add(t);
                ar.Tracks.Clear();
                ar.Tracks.AddRange(ordered);
            }

            var albumList = new List<AlbumInfo>(albums.Values);
            albumList.Sort(delegate(AlbumInfo x, AlbumInfo y) { return CompareText(x.Title, y.Title); });
            var artistList = new List<ArtistInfo>(artists.Values);
            artistList.Sort(delegate(ArtistInfo x, ArtistInfo y) { return CompareText(x.Name, y.Name); });
            albumsOut = albumList;
            artistsOut = artistList;
        }

        /// <summary>Playable songs in the requested order. Ties on date added (a whole folder imported at once) fall back to the file's creation time, then name.</summary>
        public List<TrackInfo> SortedSongs(SongSort sort) { return Sort(Songs, sort); }

        /// <summary>Thread-safe: sorts a snapshot of songs.</summary>
        public static List<TrackInfo> Sort(List<TrackInfo> songs, SongSort sort)
        {
            var list = new List<TrackInfo>();
            foreach (TrackInfo t in songs) if (!t.Probed || t.ProbeError == null) list.Add(t);
            Comparison<TrackInfo> byName = delegate(TrackInfo a, TrackInfo b)
            {
                int c = CompareText(a.DisplayTitle, b.DisplayTitle);
                if (c == 0) c = CompareText(a.DisplayArtist, b.DisplayArtist);
                return c != 0 ? c : string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
            };
            Comparison<TrackInfo> byDate = delegate(TrackInfo a, TrackInfo b)
            {
                int c = a.DateAdded.CompareTo(b.DateAdded);
                if (c == 0) c = a.FileCreated.CompareTo(b.FileCreated);
                return c != 0 ? c : byName(a, b);
            };
            switch (sort)
            {
                case SongSort.NameAsc: list.Sort(byName); break;
                case SongSort.NameDesc: list.Sort(delegate(TrackInfo a, TrackInfo b) { return byName(b, a); }); break;
                case SongSort.DateAddedOldest: list.Sort(byDate); break;
                default: list.Sort(delegate(TrackInfo a, TrackInfo b) { return byDate(b, a); }); break;
            }
            return list;
        }

        public static string SortLabel(SongSort s)
        {
            switch (s)
            {
                case SongSort.NameAsc: return "Name (A–Z)";
                case SongSort.NameDesc: return "Name (Z–A)";
                case SongSort.DateAddedOldest: return "Date added (oldest first)";
                default: return "Date added (newest first)";
            }
        }

        // ------------------------------------------------------------------ persistence

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDir);
            var sb = new StringBuilder("#BPP-LIBRARY\t1\r\n");
            foreach (string f in Folders) sb.Append("#folder\t").Append(Clean(f)).Append("\r\n");
            var ci = CultureInfo.InvariantCulture;
            foreach (TrackInfo t in Songs)
            {
                // No lock: Probe() holds the track lock while ffprobe runs, and saving must never block the UI.
                {
                    bool ok = t.Probed && t.SampleRate > 0;
                    string[] fields =
                    {
                        Clean(t.FilePath), t.DateAdded.Ticks.ToString(ci), t.FileCreated.Ticks.ToString(ci), t.FileModified.Ticks.ToString(ci),
                        ok ? "1" : "0", Clean(t.ProbeError), Clean(t.Codec), t.SampleRate.ToString(ci), t.Channels.ToString(ci),
                        t.BitDepth.ToString(ci), t.BitRate.ToString(ci), t.IsFloat ? "1" : "0", t.IsLossy ? "1" : "0", t.IsDsd ? "1" : "0",
                        t.Duration.ToString("R", ci), Clean(t.Title), Clean(t.Artist), Clean(t.AlbumArtist), Clean(t.Album),
                        t.TrackNo.ToString(ci), t.Disc.ToString(ci), Clean(t.Year)
                    };
                    sb.Append(string.Join("\t", fields)).Append("\r\n");
                }
            }
            string tmp = LibraryFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(LibraryFile)) File.Delete(LibraryFile);
            File.Move(tmp, LibraryFile);
        }

        public void Load()
        {
            Clear();
            Folders.Clear();
            if (!File.Exists(LibraryFile)) return;
            var ci = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(LibraryFile, Encoding.UTF8))
            {
                if (line.StartsWith("#folder\t")) { Folders.Add(line.Substring(8)); continue; }
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f.Length < 22 || byPath.ContainsKey(f[0])) continue;
                try
                {
                    var t = new TrackInfo(f[0])
                    {
                        DateAdded = new DateTime(long.Parse(f[1], ci), DateTimeKind.Utc),
                        FileCreated = new DateTime(long.Parse(f[2], ci), DateTimeKind.Utc),
                        FileModified = new DateTime(long.Parse(f[3], ci), DateTimeKind.Utc)
                    };
                    if (f[4] == "1")
                    {
                        t.ProbeError = f[5].Length > 0 ? f[5] : null;
                        t.Codec = f[6];
                        t.SampleRate = int.Parse(f[7], ci);
                        t.Channels = int.Parse(f[8], ci);
                        t.BitDepth = int.Parse(f[9], ci);
                        t.BitRate = long.Parse(f[10], ci);
                        t.IsFloat = f[11] == "1";
                        t.IsLossy = f[12] == "1";
                        t.IsDsd = f[13] == "1";
                        t.Duration = double.Parse(f[14], ci);
                        t.Title = Null(f[15]); t.Artist = Null(CleanArtist(f[16])); t.AlbumArtist = Null(CleanArtist(f[17])); t.Album = Null(f[18]);
                        t.TrackNo = int.Parse(f[19], ci);
                        t.Disc = int.Parse(f[20], ci);
                        t.Year = Null(f[21]);
                        t.Probed = true;
                    }
                    Songs.Add(t);
                    byPath[t.FilePath] = t;
                }
                catch { }
            }
        }

        static string Null(string s) { return string.IsNullOrEmpty(s) ? null : s; }
    }
}

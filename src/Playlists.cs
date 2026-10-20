using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BitPerfectPlayer
{
    /// <summary>A user-made playlist: a name and an ordered list of song file paths (kept as paths, so songs that leave the library return when they come back).</summary>
    internal sealed class Playlist
    {
        public string Name;
        public readonly List<string> Paths = new List<string>();
        internal string FileName;   // inside the playlists folder

        /// <summary>The playlist's songs that are in the library, in playlist order.</summary>
        public List<TrackInfo> Resolve(Library lib)
        {
            var list = new List<TrackInfo>(Paths.Count);
            foreach (string p in Paths)
            {
                TrackInfo t = lib.Get(p);
                if (t != null) list.Add(t);
            }
            return list;
        }

        public bool Contains(string path)
        {
            foreach (string p in Paths) if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// Playlists as standard .m3u8 files in %APPDATA%\SoDaPlayer\playlists (one file per playlist, "#PLAYLIST:" carries the
    /// name), written on every change so nothing is lost when the app closes. Any other player can open them.
    /// </summary>
    internal sealed class PlaylistStore
    {
        public readonly List<Playlist> Items = new List<Playlist>();

        static string Dir { get { return Path.Combine(Library.DataDir, "playlists"); } }

        public PlaylistStore() { Load(); }

        void Load()
        {
            Items.Clear();
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (string file in Directory.GetFiles(Dir, "*.m3u8"))
                {
                    try
                    {
                        var pl = new Playlist { FileName = Path.GetFileName(file), Name = Path.GetFileNameWithoutExtension(file) };
                        foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0) continue;
                            if (line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase)) { string n = line.Substring(10).Trim(); if (n.Length > 0) pl.Name = n; continue; }
                            if (line[0] == '#') continue;
                            pl.Paths.Add(line);
                        }
                        Items.Add(pl);
                    }
                    catch { }
                }
            }
            catch { }
            Sort();
        }

        void Sort() { Items.Sort(delegate(Playlist a, Playlist b) { return Library.CompareText(a.Name, b.Name); }); }

        void Write(Playlist pl)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (pl.FileName == null) pl.FileName = UniqueFileName(pl.Name);
                var sb = new StringBuilder("#EXTM3U\r\n#PLAYLIST:").Append(pl.Name).Append("\r\n");
                foreach (string p in pl.Paths) sb.Append(p).Append("\r\n");
                // Write to a temporary file first, so a crash mid-write cannot leave a half playlist.
                string path = Path.Combine(Dir, pl.FileName), tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        string UniqueFileName(string name)
        {
            var bad = new StringBuilder();
            foreach (char c in name) bad.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            string stem = bad.ToString().Trim().TrimEnd('.');
            if (stem.Length == 0) stem = "playlist";
            if (stem.Length > 80) stem = stem.Substring(0, 80);
            string file = stem + ".m3u8";
            for (int i = 2; Items.Exists(p => string.Equals(p.FileName, file, StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(Dir, file)); i++)
                file = stem + " (" + i.ToString(CultureInfo.InvariantCulture) + ").m3u8";
            return file;
        }

        public bool NameTaken(string name, Playlist except)
        {
            return Items.Exists(p => p != except && Library.CompareText(p.Name, name) == 0);
        }

        /// <summary>"Name", or "Name (2)", "Name (3)"… when that is taken.</summary>
        public string UniqueName(string name, Playlist except = null)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) name = "New playlist";
            string n = name;
            for (int i = 2; NameTaken(n, except); i++) n = name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
            return n;
        }

        public Playlist Create(string name, IEnumerable<TrackInfo> songs)
        {
            var pl = new Playlist { Name = UniqueName(name) };
            if (songs != null) foreach (TrackInfo t in songs) if (!pl.Contains(t.FilePath)) pl.Paths.Add(t.FilePath);
            Items.Add(pl);
            Sort();
            Write(pl);
            return pl;
        }

        /// <summary>Appends songs that are not already in the playlist; returns how many were added.</summary>
        public int AddSongs(Playlist pl, IEnumerable<TrackInfo> songs)
        {
            int added = 0;
            foreach (TrackInfo t in songs)
                if (!pl.Contains(t.FilePath)) { pl.Paths.Add(t.FilePath); added++; }
            if (added > 0) Write(pl);
            return added;
        }

        /// <summary>Removes the entries for these songs (all of their occurrences).</summary>
        public void RemoveSongs(Playlist pl, IEnumerable<TrackInfo> songs)
        {
            bool changed = false;
            foreach (TrackInfo t in songs)
                changed |= pl.Paths.RemoveAll(p => string.Equals(p, t.FilePath, StringComparison.OrdinalIgnoreCase)) > 0;
            if (changed) Write(pl);
        }

        /// <summary>Moves a song one place up (-1) or down (+1) in the playlist's list of library songs.</summary>
        public void Move(Playlist pl, TrackInfo t, int delta, Library lib)
        {
            // Moves within the visible (library) songs: paths of songs not in the library stay where they are.
            List<TrackInfo> visible = pl.Resolve(lib);
            int from = visible.IndexOf(t), to = from + delta;
            if (from < 0 || to < 0 || to >= visible.Count) return;
            TrackInfo other = visible[to];
            int a = pl.Paths.FindIndex(p => string.Equals(p, t.FilePath, StringComparison.OrdinalIgnoreCase));
            int b = pl.Paths.FindIndex(p => string.Equals(p, other.FilePath, StringComparison.OrdinalIgnoreCase));
            if (a < 0 || b < 0) return;
            string tmp = pl.Paths[a]; pl.Paths[a] = pl.Paths[b]; pl.Paths[b] = tmp;
            Write(pl);
        }

        public void Rename(Playlist pl, string name)
        {
            name = UniqueName(name, pl);
            if (name == pl.Name) return;
            pl.Name = name;
            Sort();
            Write(pl);   // the file keeps its name; "#PLAYLIST:" carries the new one
        }

        public void Delete(Playlist pl)
        {
            Items.Remove(pl);
            try { if (pl.FileName != null) File.Delete(Path.Combine(Dir, pl.FileName)); } catch { }
        }
    }
}

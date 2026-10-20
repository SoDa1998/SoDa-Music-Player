using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BitPerfectPlayer
{
    internal enum ArtChange { Keep, Replace, Remove }

    /// <summary>
    /// Reads and writes song tags with ffmpeg. Writing remuxes the file with "-c copy" (audio and embedded cover art are
    /// copied bit for bit; only the metadata changes) into a temporary file next to it, checks the result, then swaps it in
    /// with File.Replace. The original is untouched if anything fails.
    /// </summary>
    internal static class TagWriter
    {
        /// <summary>Current values for <see cref="TagEditorForm.Fields"/>, read from the file (falls back to the library's tags).</summary>
        public static string[] Read(TrackInfo t)
        {
            var fields = TagEditorForm.Fields;
            var v = new string[fields.Length];
            Dictionary<string, string> tags = ReadTags(t.FilePath);
            for (int i = 0; i < fields.Length; i++)
            {
                string key = fields[i][0], val;
                if (tags != null && TryTag(tags, key, out val)) { v[i] = val; continue; }
                switch (key)
                {
                    case "title": v[i] = t.Title; break;
                    case "artist": v[i] = t.Artist; break;
                    case "album": v[i] = t.Album; break;
                    case "album_artist": v[i] = t.AlbumArtist; break;
                    case "date": v[i] = t.Year; break;
                    case "track": v[i] = t.TrackNumber; break;
                    case "disc": v[i] = t.Disc > 0 ? t.Disc.ToString(CultureInfo.InvariantCulture) : null; break;
                }
            }
            return v;
        }

        static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>
        {
            { "album_artist", new[] { "album_artist", "albumartist", "album artist", "tpe2", "aart" } },
            { "date", new[] { "date", "year", "tyer", "tdrc", "originaldate" } },
            { "track", new[] { "track", "tracknumber", "trck" } },
            { "disc", new[] { "disc", "discnumber", "tpos" } },
            { "genre", new[] { "genre", "tcon" } }
        };

        static bool TryTag(Dictionary<string, string> tags, string key, out string val)
        {
            string[] names;
            if (!Aliases.TryGetValue(key, out names)) names = new[] { key };
            foreach (string n in names) if (tags.TryGetValue(n, out val)) return true;
            val = null;
            return false;
        }

        /// <summary>Format and stream tags, keys lower-cased (format tags win).</summary>
        static Dictionary<string, string> ReadTags(string path)
        {
            if (!FfmpegTools.Locate()) return null;
            try
            {
                string json = FfmpegTools.RunAndCapture(FfmpegTools.FfprobePath,
                    "-v error -print_format json -show_entries format_tags:stream_tags " + FfmpegTools.Quote(path), 15000);
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root == null) return result;
                object o, tags;
                if (root.TryGetValue("format", out o) && o is Dictionary<string, object> && ((Dictionary<string, object>)o).TryGetValue("tags", out tags))
                    Merge(result, tags as Dictionary<string, object>);
                if (root.TryGetValue("streams", out o) && o is object[])
                    foreach (object s in (object[])o)
                    {
                        var sd = s as Dictionary<string, object>;
                        if (sd != null && sd.TryGetValue("tags", out tags)) Merge(result, tags as Dictionary<string, object>);
                    }
                return result;
            }
            catch { return null; }
        }

        static void Merge(Dictionary<string, string> into, Dictionary<string, object> tags)
        {
            if (tags == null) return;
            foreach (var kv in tags)
            {
                string k = kv.Key.ToLowerInvariant();
                if (!into.ContainsKey(k)) into[k] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Writes <paramref name="changes"/> (ffmpeg key → value, "" removes) and, unless <paramref name="art"/> is Keep,
        /// replaces the embedded album art with <paramref name="artFile"/> (JPEG/PNG) or removes it. Returns null on
        /// success or an error message.
        /// </summary>
        public static string Write(string path, Dictionary<string, string> changes, ArtChange art = ArtChange.Keep, string artFile = null)
        {
            if (changes.Count == 0 && art == ArtChange.Keep) return null;
            if (!FfmpegTools.Locate()) return "FFmpeg was not found";
            string dir = Path.GetDirectoryName(path) ?? "";
            string ext = Path.GetExtension(path);
            string lower = ext.ToLowerInvariant();
            if (lower == ".dsf" || lower == ".dff" || lower == ".ape" || lower == ".wv" || lower == ".tta")
                return "FFmpeg can’t write tags to " + ext + " files";
            if (art == ArtChange.Replace && !CanEmbedArt(path))
                return "album art can’t be embedded in " + ext + " files";
            string tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".soda-edit" + ext);

            var args = new StringBuilder("-hide_banner -loglevel error -nostdin -y -i ").Append(FfmpegTools.Quote(path));
            if (art == ArtChange.Replace) args.Append(" -i ").Append(FfmpegTools.Quote(artFile));
            // Keep: every stream as is. Replace / Remove: drop the old picture stream(s); Replace adds the new one as the cover.
            args.Append(art == ArtChange.Keep ? " -map 0" : " -map 0 -map -0:v?");
            if (art == ArtChange.Replace)
                args.Append(" -map 1:v:0 -disposition:v:0 attached_pic -metadata:s:v:0 title=\"Album cover\" -metadata:s:v:0 comment=\"Cover (front)\"");
            args.Append(" -c copy -map_metadata 0");
            if (lower == ".mp3") args.Append(" -id3v2_version 3 -write_id3v1 0");
            if (lower == ".m4a" || lower == ".mp4" || lower == ".m4b" || lower == ".alac") args.Append(" -movflags +faststart");
            foreach (var kv in changes) args.Append(" -metadata ").Append(Arg(kv.Key + "=" + kv.Value));
            args.Append(' ').Append(FfmpegTools.Quote(tmp));
            try
            {
                FfmpegTools.RunAndCapture(FfmpegTools.FfmpegPath, args.ToString(), 120000);
                long before = new FileInfo(path).Length;
                if (!File.Exists(tmp)) return "FFmpeg didn’t write the file";
                long after = new FileInfo(tmp).Length;
                // A removed or smaller picture can shrink the file legitimately, so then check that the audio is all there.
                if (art == ArtChange.Keep ? after < before / 2 : !SameAudio(path, tmp))
                    return "The rewritten file looked incomplete, so the original was kept";

                // The file may be open for a moment (a tag reader, an indexer): retry briefly.
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Replace(tmp, path, null, true); return null; }
                    catch (IOException) { if (attempt >= 10) throw; Thread.Sleep(200); }
                    catch (UnauthorizedAccessException) { if (attempt >= 3) throw; Thread.Sleep(200); }
                }
            }
            catch (Exception ex)
            {
                string m = ex.Message.Trim();
                int nl = m.IndexOf('\n');
                return nl > 0 ? m.Substring(0, nl).Trim() : m;
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        /// <summary>Formats whose FFmpeg muxer can store an attached picture (Ogg/Opus keep pictures in a tag FFmpeg can’t write; WAV has none).</summary>
        public static bool CanEmbedArt(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return e == ".flac" || e == ".mp3" || e == ".m4a" || e == ".mp4" || e == ".m4b" || e == ".alac" || e == ".aif" || e == ".aiff";
        }

        /// <summary>True when both files have the same audio streams with the same packet checksums (the audio was copied, not cut).</summary>
        static bool SameAudio(string a, string b)
        {
            try
            {
                string args = " -map 0:a -c copy -f md5 -";
                string ha = FfmpegTools.RunAndCapture(FfmpegTools.FfmpegPath, "-v error -nostdin -i " + FfmpegTools.Quote(a) + args, 120000).Trim();
                string hb = FfmpegTools.RunAndCapture(FfmpegTools.FfmpegPath, "-v error -nostdin -i " + FfmpegTools.Quote(b) + args, 120000).Trim();
                return ha.Length > 0 && ha == hb;
            }
            catch { return false; }
        }

        /// <summary>The first embedded picture of <paramref name="path"/>, or null.</summary>
        public static Image ReadEmbeddedArt(string path)
        {
            if (!FfmpegTools.Locate()) return null;
            string tmp = Path.Combine(Path.GetTempPath(), "soda-art-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                FfmpegTools.RunAndCapture(FfmpegTools.FfmpegPath,
                    "-hide_banner -loglevel error -nostdin -y -i " + FfmpegTools.Quote(path) + " -map 0:v:0 -frames:v 1 " + FfmpegTools.Quote(tmp), 20000);
                if (!File.Exists(tmp)) return null;
                using (var fs = File.OpenRead(tmp))
                using (Image src = Image.FromStream(fs))
                    return new Bitmap(src);
            }
            catch { return null; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>Quotes one command-line argument for CommandLineToArgvW / the MSVC runtime (handles quotes and backslashes).</summary>
        static string Arg(string s)
        {
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}

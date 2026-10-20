using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BitPerfectPlayer
{
    internal sealed class TrackInfo
    {
        public string FilePath;
        public string Title, Artist, Album, AlbumArtist, Year, TrackNumber;
        /// <summary>The album this track was grouped into by the last library index (see Library.BuildIndex).</summary>
        public volatile string AlbumGroup;
        public int TrackNo, Disc;
        public DateTime DateAdded, FileCreated, FileModified;   // UTC
        internal string ArtKey, ArtKeySource;                      // cached art-cache key (see ArtCache.KeyFor)
        public double Duration;
        public string Codec = "";
        public int SampleRate, Channels, BitDepth;
        public long BitRate;
        public bool IsFloat, IsLossy, IsDsd;
        public volatile bool Probed;
        public string ProbeError;

        public TrackInfo(string path) { FilePath = path; }

        /// <summary>Forces the next Probe() to re-read the file (e.g. after its tags changed).</summary>
        public void ResetProbe()
        {
            lock (this) { Probed = false; ProbeError = null; }
        }

        public string DisplayArtist { get { return string.IsNullOrEmpty(Artist) ? "Unknown Artist" : Artist; } }

        public string DisplayTitle
        {
            get { return string.IsNullOrEmpty(Title) ? Path.GetFileNameWithoutExtension(FilePath) : Title; }
        }

        /// <summary>What the decoder should output to stay lossless.</summary>
        public PcmKind DecodeKind
        {
            get
            {
                if (IsFloat) return PcmKind.F32;
                return BitDepth > 0 && BitDepth <= 16 ? PcmKind.S16 : PcmKind.S32;
            }
        }

        public string CodecDisplay
        {
            get
            {
                string c = Codec ?? "";
                if (c.StartsWith("pcm_")) return c.Contains("f32") || c.Contains("f64") ? "PCM float" : "PCM";
                if (c.StartsWith("dsd_")) return "DSD";
                switch (c)
                {
                    case "flac": return "FLAC";
                    case "alac": return "ALAC";
                    case "aac": return "AAC";
                    case "mp3": case "mp3float": return "MP3";
                    case "vorbis": return "Vorbis";
                    case "opus": return "Opus";
                    case "wavpack": return "WavPack";
                    case "ape": return "APE";
                    case "tta": return "TTA";
                    case "wmav2": case "wmav1": return "WMA";
                    case "wmapro": return "WMA Pro";
                    case "wmalossless": return "WMA Lossless";
                    case "ac3": return "AC-3";
                    case "eac3": return "E-AC-3";
                    case "dts": return "DTS";
                    case "truehd": return "TrueHD";
                    default: return c.ToUpperInvariant();
                }
            }
        }

        public string ShortFormat
        {
            get
            {
                if (!Probed) return "…";
                if (ProbeError != null) return "error";
                string rate = (SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
                if (IsDsd) return "DSD" + (SampleRate * 8 / 44100);
                if (!IsLossy && BitDepth > 0) return CodecDisplay + " " + BitDepth + "/" + rate;
                if (BitRate > 0) return CodecDisplay + " " + (BitRate / 1000) + "k/" + rate;
                return CodecDisplay + " " + rate;
            }
        }

        public string LongFormat
        {
            get
            {
                if (!Probed) return "";
                if (ProbeError != null) return ProbeError;
                var parts = new List<string> { CodecDisplay };
                if (IsDsd) parts.Add("DSD" + (SampleRate * 8 / 44100) + " (" + PcmFormat.FormatRate(SampleRate * 8) + " 1-bit)");
                else
                {
                    if (!IsLossy && BitDepth > 0) parts.Add(BitDepth + "-bit");
                    else if (IsLossy && BitRate > 0) parts.Add((BitRate / 1000) + " kbps");
                    parts.Add(PcmFormat.FormatRate(SampleRate));
                }
                parts.Add(PcmFormat.FormatChannels(Channels));
                parts.Add(IsLossy ? "lossy" : "lossless");
                return string.Join(" · ", parts.ToArray());
            }
        }
    }

    internal static class FfmpegTools
    {
        public static string FfmpegPath, FfprobePath;
        public static bool HasSoxr;
        static bool located;

        static readonly HashSet<string> LosslessCodecs = new HashSet<string>
        {
            "flac", "alac", "wavpack", "ape", "tta", "mlp", "truehd", "shorten", "tak", "mp4als", "wmalossless", "ralf"
        };

        public static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".wav", ".wave", ".w64", ".rf64", ".bwf", ".aif", ".aiff", ".aifc", ".caf",
            ".mp3", ".mp2", ".mpa", ".m4a", ".m4b", ".m4v", ".mp4", ".aac", ".adts", ".alac", ".3gp", ".mov",
            ".ogg", ".oga", ".opus", ".spx", ".wma", ".asf", ".ape", ".wv", ".tta", ".tak", ".mpc",
            ".dsf", ".dff", ".mka", ".mkv", ".webm", ".ac3", ".eac3", ".dts", ".thd", ".amr", ".au", ".snd"
        };

        static readonly object locateLock = new object();
        /// <summary>FFmpeg version string when the copy bundled inside SoDaPlayer.exe is in use, else null.</summary>
        public static string BundledVersion;

        /// <summary>
        /// Finds ffmpeg/ffprobe: next to the exe (or an ffmpeg\ sub-folder), then the copy bundled inside SoDaPlayer.exe
        /// (unpacked once to %LOCALAPPDATA%\SoDaPlayer\ffmpeg\<version>), then PATH, then the WinGet links folder.
        /// </summary>
        public static bool Locate()
        {
            lock (locateLock)
            {
                if (located) return FfmpegPath != null;
                located = true;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!TryDirs(new[] { baseDir, Path.Combine(baseDir, "ffmpeg"), Path.Combine(baseDir, "ffmpeg", "bin") }))
                {
                    string bundled = ExtractBundled();
                    if (bundled == null || !TryDirs(new[] { bundled }))
                    {
                        var dirs = new List<string>();
                        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                        foreach (string p in pathVar.Split(';')) if (p.Trim().Length > 0) dirs.Add(p.Trim().Trim('"'));
                        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
                        TryDirs(dirs);
                    }
                    else BundledVersion = ReadManifest().ContainsKey("version") ? ReadManifest()["version"] : "bundled";
                }
                if (FfmpegPath == null) return false;
                try
                {
                    string conf = RunAndCapture(FfmpegPath, "-hide_banner -buildconf", 5000);
                    HasSoxr = conf.Contains("--enable-libsoxr");
                }
                catch { }
                return true;
            }
        }

        static bool TryDirs(IEnumerable<string> dirs)
        {
            foreach (string d in dirs)
            {
                try
                {
                    string ff = Path.Combine(d, "ffmpeg.exe"), fp = Path.Combine(d, "ffprobe.exe");
                    if (File.Exists(ff) && File.Exists(fp)) { FfmpegPath = ff; FfprobePath = fp; return true; }
                }
                catch { }
            }
            return false;
        }

        static Dictionary<string, string> ReadManifest()
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (Stream s = typeof(FfmpegTools).Assembly.GetManifestResourceStream("BitPerfectPlayer.ffmpeg.manifest.txt"))
            {
                if (s == null) return m;
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) m[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            return m;
        }

        /// <summary>
        /// Unpacks the GZip-compressed ffmpeg.exe / ffprobe.exe embedded in the exe (single-file sharing build). Each file is
        /// written under a temporary name and renamed only when complete with the expected size, so an interrupted first run
        /// never leaves a broken copy. Returns the folder, or null when this build has no bundled FFmpeg.
        /// </summary>
        static string ExtractBundled()
        {
            try
            {
                Dictionary<string, string> m = ReadManifest();
                string tag;
                if (!m.TryGetValue("tag", out tag)) return null;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoDaPlayer", "ffmpeg", tag);
                Directory.CreateDirectory(dir);
                var asm = typeof(FfmpegTools).Assembly;
                foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    string target = Path.Combine(dir, name);
                    long expected;
                    string sz;
                    if (!m.TryGetValue(name, out sz) || !long.TryParse(sz, out expected)) return null;
                    if (File.Exists(target) && new FileInfo(target).Length == expected) continue;
                    string tmp = target + ".part";
                    using (Stream res = asm.GetManifestResourceStream("BitPerfectPlayer." + name + ".gz"))
                    {
                        if (res == null) return null;
                        using (var gz = new System.IO.Compression.GZipStream(res, System.IO.Compression.CompressionMode.Decompress))
                        using (FileStream outFile = File.Create(tmp))
                            gz.CopyTo(outFile, 1 << 20);
                    }
                    if (new FileInfo(tmp).Length != expected) { File.Delete(tmp); return null; }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                }
                // Keep FFmpeg's licence next to the binaries too (GPLv3 asks for it to accompany them).
                foreach (string lic in new[] { "FFmpeg-LICENSE.txt", "FFmpeg-README.txt" })
                    using (Stream res = asm.GetManifestResourceStream("BitPerfectPlayer.Licenses." + lic))
                        if (res != null && !File.Exists(Path.Combine(dir, lic)))
                            using (FileStream f = File.Create(Path.Combine(dir, lic))) res.CopyTo(f);
                return dir;
            }
            catch { return null; }
        }

        /// <summary>All third-party notices embedded in the exe, as one text.</summary>
        public static string LicenseText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SoDa Player — third-party software and licences");
            sb.AppendLine("=================================================");
            sb.AppendLine();
            sb.AppendLine("FFmpeg");
            sb.AppendLine("------");
            sb.AppendLine("This program includes ffmpeg.exe and ffprobe.exe from the FFmpeg project (https://ffmpeg.org),");
            sb.AppendLine("as built by gyan.dev (https://www.gyan.dev/ffmpeg/builds/), licensed under the GNU GPL version 3.");
            sb.AppendLine("SoDa Player runs them as separate programs. The corresponding source code is listed below");
            sb.AppendLine("(FFmpeg source: see the 'Source Code' line; library sources are linked from gyan.dev).");
            sb.AppendLine();
            var asm = typeof(FfmpegTools).Assembly;
            foreach (string lic in new[] { "FFmpeg-README.txt", "FFmpeg-LICENSE.txt", "Tabler-LICENSE.txt" })
                using (Stream res = asm.GetManifestResourceStream("BitPerfectPlayer.Licenses." + lic))
                {
                    if (res == null) continue;
                    if (lic.StartsWith("Tabler")) { sb.AppendLine(); sb.AppendLine("Tabler Icons (https://tabler.io/icons) — MIT License"); sb.AppendLine("----------------------------------------------------"); }
                    using (var r = new StreamReader(res, Encoding.UTF8)) sb.AppendLine(r.ReadToEnd());
                    sb.AppendLine();
                }
            sb.AppendLine("Inter typeface (https://rsms.me/inter/) by Rasmus Andersson — SIL Open Font License 1.1");
            sb.AppendLine("Licence text: https://openfontlicense.org — the licence is also recorded inside the embedded font files.");
            return sb.ToString();
        }
        internal static string RunAndCapture(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                var err = new StringBuilder();
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new TimeoutException(exe + " timed out"); }
                p.WaitForExit();
                if (p.ExitCode != 0 && output.Trim().Length == 0)
                {
                    string e; lock (err) e = err.ToString().Trim();
                    throw new Exception(e.Length > 0 ? e : "exit code " + p.ExitCode);
                }
                return output;
            }
        }

        public static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        /// <summary>Reads stream format and tags with ffprobe. Safe to call from several threads.</summary>
        public static void Probe(TrackInfo t)
        {
            if (t.Probed) return;
            lock (t)
            {
                if (t.Probed) return;
                try
                {
                    if (!Locate()) throw new Exception("ffmpeg/ffprobe not found (put them next to the app or on PATH)");
                    string json = RunAndCapture(FfprobePath,
                        "-v error -print_format json -show_format -show_streams -select_streams a:0 " + Quote(t.FilePath), 20000);
                    var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                    object[] streams = root != null && root.ContainsKey("streams") ? root["streams"] as object[] : null;
                    if (streams == null || streams.Length == 0) throw new Exception("no audio stream");
                    var st = (Dictionary<string, object>)streams[0];
                    var fmt = root.ContainsKey("format") ? root["format"] as Dictionary<string, object> : null;

                    t.Codec = Str(st, "codec_name");
                    t.SampleRate = (int)Num(st, "sample_rate");
                    t.Channels = (int)Num(st, "channels");
                    string sampleFmt = Str(st, "sample_fmt");
                    int rawBits = (int)Num(st, "bits_per_raw_sample");
                    int bps = (int)Num(st, "bits_per_sample");
                    t.IsDsd = t.Codec.StartsWith("dsd_");
                    t.IsLossy = !t.IsDsd && !t.Codec.StartsWith("pcm_") && !LosslessCodecs.Contains(t.Codec);
                    t.IsFloat = t.IsDsd || sampleFmt.StartsWith("flt") || sampleFmt.StartsWith("dbl");
                    if (t.IsFloat && !t.IsLossy && !t.IsDsd) t.BitDepth = sampleFmt.StartsWith("dbl") ? 64 : 32;
                    else if (rawBits > 0) t.BitDepth = rawBits;
                    else if (bps > 0) t.BitDepth = bps;
                    else if (sampleFmt.StartsWith("s16")) t.BitDepth = 16;
                    else if (sampleFmt.StartsWith("s32")) t.BitDepth = 32;
                    else if (sampleFmt.StartsWith("u8")) t.BitDepth = 8;
                    if (t.IsLossy) t.BitDepth = 0;

                    t.BitRate = (long)Num(st, "bit_rate");
                    if (t.BitRate == 0 && fmt != null) t.BitRate = (long)Num(fmt, "bit_rate");
                    t.Duration = fmt != null ? Num(fmt, "duration") : 0;
                    if (t.Duration <= 0) t.Duration = Num(st, "duration");

                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddTags(tags, fmt);
                    AddTags(tags, st);
                    string v;
                    t.Title = t.Artist = t.Album = t.AlbumArtist = t.Year = t.TrackNumber = null;
                    t.TrackNo = t.Disc = 0;
                    if (tags.TryGetValue("title", out v)) t.Title = v.Trim();
                    if (tags.TryGetValue("album_artist", out v) || tags.TryGetValue("albumartist", out v) ||
                        tags.TryGetValue("album artist", out v)) t.AlbumArtist = Library.CleanArtist(v);
                    if (tags.TryGetValue("artist", out v)) t.Artist = Library.CleanArtist(v);
                    else if (!string.IsNullOrEmpty(t.AlbumArtist)) t.Artist = t.AlbumArtist;
                    if (tags.TryGetValue("album", out v)) t.Album = v.Trim();
                    if (tags.TryGetValue("track", out v)) { t.TrackNumber = v; t.TrackNo = LeadingInt(v); }
                    if (tags.TryGetValue("disc", out v) || tags.TryGetValue("discnumber", out v)) t.Disc = LeadingInt(v);
                    if (tags.TryGetValue("date", out v) || tags.TryGetValue("year", out v)) t.Year = v.Length >= 4 ? v.Substring(0, 4) : v;

                    if (t.SampleRate <= 0 || t.Channels <= 0) throw new Exception("unsupported audio stream");
                }
                catch (Exception ex)
                {
                    t.ProbeError = ex.Message;
                }
                t.Probed = true;
            }
        }

        static int LeadingInt(string s)
        {
            int n = 0, i = 0;
            s = (s ?? "").Trim();
            while (i < s.Length && char.IsDigit(s[i]) && n < 100000) n = n * 10 + (s[i++] - '0');
            return n;
        }

        static void AddTags(Dictionary<string, string> tags, Dictionary<string, object> obj)
        {
            if (obj == null || !obj.ContainsKey("tags")) return;
            var d = obj["tags"] as Dictionary<string, object>;
            if (d == null) return;
            foreach (var kv in d) if (!tags.ContainsKey(kv.Key)) tags[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "";
        }

        static double Num(Dictionary<string, object> d, string key)
        {
            double v;
            return double.TryParse(Str(d, key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }

    /// <summary>
    /// Streams raw PCM from an ffmpeg child process into a ring buffer on a background thread.
    /// Output is the source's native rate/channels/sample format unless the caller asks otherwise.
    /// </summary>
    internal sealed class FfmpegDecoder : IAudioDecoder
    {
        public TrackInfo Track { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int SourceBits { get; private set; }
        public int BytesPerFrame { get; private set; }
        public PcmKind Kind { get; private set; }
        public double StartSeconds { get; private set; }
        public long FramesConsumed { get; private set; }
        public string ErrorText { get { return errorText; } }
        public bool IsDop { get { return false; } }
        volatile string errorText;

        readonly Process proc;
        readonly Thread reader;
        readonly byte[] ring;
        readonly object lk = new object();
        int readPos, writePos, count;
        bool eof;
        volatile bool disposed;

        public FfmpegDecoder(TrackInfo track, double startSeconds, int sampleRate, int channels, PcmKind kind)
        {
            if (!FfmpegTools.Locate()) throw new Exception("ffmpeg not found");
            Track = track;
            StartSeconds = startSeconds;
            SampleRate = sampleRate;
            Channels = channels;
            Kind = kind;
            SourceBits = kind == PcmKind.S16 ? 16 : kind == PcmKind.F32 ? 32 : (track.BitDepth > 0 && track.BitDepth <= 32 ? track.BitDepth : 32);
            BytesPerFrame = channels * (kind == PcmKind.S16 ? 2 : 4);
            ring = new byte[Math.Max(BytesPerFrame * sampleRate * 3, 1 << 16)];   // ~3 s of audio

            var sb = new StringBuilder("-hide_banner -nostdin -loglevel error ");
            if (startSeconds > 0) sb.Append("-ss ").Append(startSeconds.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ');
            sb.Append("-i ").Append(FfmpegTools.Quote(track.FilePath)).Append(" -map 0:a:0 -vn -sn -dn ");
            if (channels != track.Channels) sb.Append("-ac ").Append(channels).Append(' ');
            if (sampleRate != track.SampleRate)
            {
                if (FfmpegTools.HasSoxr) sb.Append("-af aresample=resampler=soxr:precision=28 ");
                sb.Append("-ar ").Append(sampleRate).Append(' ');
            }
            string f = kind == PcmKind.S16 ? "s16le" : kind == PcmKind.S32 ? "s32le" : "f32le";
            sb.Append("-c:a pcm_").Append(f).Append(" -f ").Append(f).Append(" pipe:1");

            var psi = new ProcessStartInfo(FfmpegTools.FfmpegPath, sb.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8
            };
            proc = Process.Start(psi);
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null && e.Data.Trim().Length > 0) errorText = e.Data; };
            proc.BeginErrorReadLine();

            reader = new Thread(ReaderLoop) { IsBackground = true, Name = "ffmpeg reader", Priority = ThreadPriority.AboveNormal };
            reader.Start();
        }

        void ReaderLoop()
        {
            byte[] tmp = new byte[64 * 1024];
            try
            {
                Stream stdout = proc.StandardOutput.BaseStream;
                while (!disposed)
                {
                    int n = stdout.Read(tmp, 0, tmp.Length);
                    if (n <= 0) break;
                    int off = 0;
                    while (n > 0)
                    {
                        lock (lk)
                        {
                            while (count == ring.Length && !disposed) Monitor.Wait(lk, 250);
                            if (disposed) return;
                            int chunk = Math.Min(n, Math.Min(ring.Length - count, ring.Length - writePos));
                            Buffer.BlockCopy(tmp, off, ring, writePos, chunk);
                            writePos = (writePos + chunk) % ring.Length;
                            count += chunk;
                            off += chunk;
                            n -= chunk;
                            Monitor.PulseAll(lk);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!disposed) errorText = ex.Message;
            }
            finally
            {
                lock (lk) { eof = true; Monitor.PulseAll(lk); }
            }
        }

        /// <summary>ffmpeg has finished writing (data may still be buffered).</summary>
        public bool EndOfStream { get { lock (lk) return eof; } }

        /// <summary>ffmpeg finished and every whole frame has been consumed.</summary>
        public bool IsDrained { get { lock (lk) return eof && count < BytesPerFrame; } }

        public bool WaitForData(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            lock (lk)
            {
                while (count < BytesPerFrame && !eof)
                {
                    int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(lk, left);
                }
                return count >= BytesPerFrame;
            }
        }

        /// <summary>Copies up to maxFrames whole frames into dst (non-blocking). Returns frames copied.</summary>
        public int Read(byte[] dst, int maxFrames)
        {
            lock (lk)
            {
                int frames = Math.Min(maxFrames, count / BytesPerFrame);
                int bytes = frames * BytesPerFrame;
                int first = Math.Min(bytes, ring.Length - readPos);
                Buffer.BlockCopy(ring, readPos, dst, 0, first);
                if (bytes > first) Buffer.BlockCopy(ring, 0, dst, first, bytes - first);
                readPos = (readPos + bytes) % ring.Length;
                count -= bytes;
                FramesConsumed += frames;
                if (frames > 0) Monitor.PulseAll(lk);
                return frames;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (lk) Monitor.PulseAll(lk);
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            try { proc.Dispose(); } catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Targeting 4.6.2+ turns on modern path handling (no 260-character MAX_PATH limit when Windows long paths are enabled).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("SoDa Player")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace BitPerfectPlayer
{
    internal static class Program
    {
        /// <summary>Lets System.IO handle paths over 260 characters (music libraries often have them).</summary>
        internal static void EnableLongPaths()
        {
            try
            {
                AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
                AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            EnableLongPaths();
            Library.MigrateOldDataDir();
            TaskbarIdentity.ApplyToProcess();   // own taskbar button + icon, however the app was started
            try { NativeMethods.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    /// <summary>Console harness (soda-test.exe) for verifying the engine without the UI.</summary>
    internal static class SelfTest
    {
        [MTAThread]
        static int Main(string[] args)
        {
            Program.EnableLongPaths();
            if (args.Length == 0)
            {
                Console.WriteLine("soda-test devices");
                Console.WriteLine("soda-test probe <file>");
                Console.WriteLine("soda-test dump <file> <out.raw> <containerBits> <validBits> [float]");
                Console.WriteLine("soda-test play <file> <seconds> [shared] [bufferMs]");
                return 1;
            }
            try
            {
                switch (args[0])
                {
                    case "devices":
                        foreach (AudioDeviceInfo d in AudioDevices.GetRenderDevices()) Console.WriteLine((d.IsDefault ? "* " : "  ") + d.Name + "  " + d.Id);
                        return 0;
                    case "probe": return Probe(args[1]);
                    case "dump": return Dump(args);
                    case "play": return Play(args);
                    case "stress": return Stress(args);
                    case "eq": return EqTest(args);
                    case "lyrics": return LyricsTest(args);
                    case "lrccheck": return LrcCheck(args);
                    case "spectrum": return SpectrumTest(args);
                    case "lrcembedcheck": return LrcEmbedCheck(args);
                    case "lyricsembed": return LyricsEmbedTest(args);
                    case "albums": return AlbumsTest();
                    case "dsd": return DsdTest(args);
                }
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); return 2; }
            return 1;
        }

        static int Probe(string file)
        {
            var t = new TrackInfo(file);
            FfmpegTools.Probe(t);
            if (t.ProbeError != null) { Console.WriteLine("ERROR: " + t.ProbeError); return 2; }
            Console.WriteLine("codec={0} rate={1} ch={2} bits={3} float={4} lossy={5} dsd={6} dur={7:0.00} kind={8}",
                t.Codec, t.SampleRate, t.Channels, t.BitDepth, t.IsFloat, t.IsLossy, t.IsDsd, t.Duration, t.DecodeKind);
            Console.WriteLine("title={0} | artist={1} | album={2}", t.Title, t.Artist, t.Album);
            Console.WriteLine("format: " + t.LongFormat);
            var c = FormatNegotiator.Candidates(t);
            Console.WriteLine("first exclusive candidates: " + c[0].Describe() + " | " + c[1].Describe() + " | " + c[2].Describe());
            return 0;
        }

        // Runs decoder + converter exactly as the engine does for the given device format and writes the device-bound bytes.
        static int Dump(string[] args)
        {
            var t = new TrackInfo(args[1]);
            FfmpegTools.Probe(t);
            if (t.ProbeError != null) throw new Exception(t.ProbeError);
            var fmt = new PcmFormat
            {
                SampleRate = t.SampleRate,
                Channels = t.Channels == 1 ? 2 : t.Channels,
                ContainerBits = int.Parse(args[3]),
                ValidBits = int.Parse(args[4]),
                IsFloat = args.Length > 5 && args[5] == "float"
            };
            var conv = new SampleConverter();
            long frames = 0;
            using (var dec = new FfmpegDecoder(t, 0, t.SampleRate, t.Channels, t.DecodeKind))
            using (var md5 = MD5.Create())
            using (Stream outFile = args[2] == "-" ? Stream.Null : (Stream)File.Create(args[2]))
            {
                byte[] inBuf = new byte[4096 * dec.BytesPerFrame];
                byte[] outBuf = new byte[4096 * fmt.BlockAlign];
                while (true)
                {
                    int n = dec.Read(inBuf, 4096);
                    if (n == 0)
                    {
                        if (dec.IsDrained) break;
                        dec.WaitForData(2000);
                        continue;
                    }
                    conv.Convert(inBuf, 0, dec.Kind, dec.SourceBits, dec.Channels, n, outBuf, 0, fmt, 1f);
                    outFile.Write(outBuf, 0, n * fmt.BlockAlign);
                    md5.TransformBlock(outBuf, 0, n * fmt.BlockAlign, null, 0);
                    frames += n;
                }
                md5.TransformFinalBlock(new byte[0], 0, 0);
                Console.WriteLine("frames={0} lossless={1} md5={2}", frames,
                    SampleConverter.IsLosslessPath(dec.Kind, dec.SourceBits, fmt),
                    BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant());
            }
            return 0;
        }

        /// <summary>stress &lt;file&gt; &lt;deviceNameSubstring&gt; [shared]: pause/resume and seek cycles, reporting any engine error.</summary>
        static int Stress(string[] args)
        {
            var t = new TrackInfo(args[1]);
            var engine = new PlaybackEngine { ExclusiveMode = !(args.Length > 3 && args[3] == "shared"), BufferMs = 20 };
            foreach (AudioDeviceInfo d in AudioDevices.GetRenderDevices())
                if (d.Name.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) >= 0) { engine.DeviceId = d.Id; Console.WriteLine("device: " + d.Name); break; }
            string error = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            engine.ErrorOccurred += delegate(string m) { error = m; Console.WriteLine("{0,6:0.0}s ERROR: {1}", sw.Elapsed.TotalSeconds, m); };
            engine.Play(t, 0);
            Thread.Sleep(2000);
            Console.WriteLine("output: " + engine.OutputDescription);

            foreach (int pauseMs in new[] { 300, 1500, 4000, 12000 })
            {
                if (error != null) break;
                double before = engine.Position;
                engine.Pause();
                Thread.Sleep(pauseMs);
                engine.Resume();
                Thread.Sleep(2600);   // longer than the 2 s driver timeout
                Console.WriteLine("{0,6:0.0}s pause {1,5} ms -> resume: pos {2:0.00}s -> {3:0.00}s  running={4} {5}",
                    sw.Elapsed.TotalSeconds, pauseMs, before, engine.Position, engine.IsRunning, error == null ? "OK" : "FAILED");
            }
            for (int i = 0; i < 6 && error == null; i++)
            {
                double target = 3 + i * 4;
                engine.Seek(target);
                Thread.Sleep(i % 2 == 0 ? 150 : 1200);   // include rapid back-to-back seeks
                Console.WriteLine("{0,6:0.0}s seek -> {1:0.0}s  pos {2:0.00}s  {3}", sw.Elapsed.TotalSeconds, target, engine.Position, error == null ? "OK" : "FAILED");
            }
            if (error == null)
            {
                // Seek while paused, then resume.
                engine.Pause(); Thread.Sleep(500); engine.Seek(10); Thread.Sleep(500); engine.Resume(); Thread.Sleep(2600);
                Console.WriteLine("{0,6:0.0}s seek-while-paused -> pos {1:0.00}s  {2}", sw.Elapsed.TotalSeconds, engine.Position, error == null ? "OK" : "FAILED");
            }
            engine.Stop();
            Console.WriteLine(error == null ? "RESULT: all OK" : "RESULT: FAILED");
            return error == null ? 0 : 2;
        }

        /// <summary>eq [preset] [rate]: runs test tones through the EQ and compares the measured gain with the designed curve; then times it.</summary>
        static int EqTest(string[] args)
        {
            string preset = args.Length > 1 ? args[1] : "Rock";
            int rate = args.Length > 2 ? int.Parse(args[2]) : 48000;
            var eq = new EqualizerSettings { Enabled = true };
            float[] g = EqualizerSettings.PresetGains(preset);
            eq.SetAll(g, 0f);
            Console.WriteLine("preset {0} @ {1} Hz  (Q = {2:0.###})", preset, rate, EqualizerSettings.BandQ);
            double worst = 0;
            var probes = new List<double>(EqualizerSettings.Frequencies);
            probes.AddRange(new double[] { 30, 440, 1000, 3000, 9000 });
            foreach (double f in probes)
            {
                if (f >= rate * 0.45) continue;
                var proc = new EqProcessor(eq);
                int frames = rate * 2;
                byte[] buf = new byte[frames * 2 * 4];
                var s = new float[frames * 2];
                for (int i = 0; i < frames; i++) s[2 * i] = s[2 * i + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * f * i / rate));
                Buffer.BlockCopy(s, 0, buf, 0, buf.Length);
                proc.Process(buf, frames, 2, rate);
                Buffer.BlockCopy(buf, 0, s, 0, buf.Length);
                double sumIn = 0, sumOut = 0;
                for (int i = frames / 2; i < frames; i++)
                {
                    double x = 0.1 * Math.Sin(2 * Math.PI * f * i / rate);
                    sumIn += x * x; sumOut += s[2 * i] * s[2 * i];
                }
                double measured = 10 * Math.Log10(sumOut / sumIn);
                double designed = EqualizerSettings.ResponseDb(EqualizerSettings.Design(g, rate), 0f, f, rate);
                worst = Math.Max(worst, Math.Abs(measured - designed));
                int band = Array.IndexOf(EqualizerSettings.Frequencies, f);
                Console.WriteLine("{0,8:0.0} Hz  measured {1,6:+0.00;-0.00} dB  designed {2,6:+0.00;-0.00} dB{3}", f, measured, designed,
                    band >= 0 ? string.Format("  slider {0,5:+0.0;-0.0}  (off by {1:0.00})", g[band], Math.Abs(measured - g[band])) : "");
            }
            Console.WriteLine("max deviation measured vs designed: {0:0.000} dB", worst);

            // Disabled / flat must be a no-op (bit-identical).
            var flat = new EqualizerSettings { Enabled = true };
            var noop = new EqProcessor(flat);
            byte[] a = new byte[4800 * 8], c;
            new Random(1).NextBytes(a);
            for (int i = 0; i < a.Length; i += 4) Buffer.BlockCopy(BitConverter.GetBytes((float)(new Random(i).NextDouble() - 0.5)), 0, a, i, 4);
            c = (byte[])a.Clone();
            noop.Process(c, 4800, 2, rate);
            bool same = true;
            for (int i = 0; i < a.Length; i++) if (a[i] != c[i]) { same = false; break; }
            Console.WriteLine("flat EQ leaves samples untouched: " + same);

            // Speed: all 20 bands active, 60 s of stereo.
            var all = new EqualizerSettings { Enabled = true };
            var ones = new float[EqualizerSettings.Bands];
            for (int i = 0; i < ones.Length; i++) ones[i] = i % 2 == 0 ? 3f : -3f;
            all.SetAll(ones, -3f);
            var fast = new EqProcessor(all);
            byte[] block = new byte[480 * 2 * 4];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int n = 0; n < rate * 60 / 480; n++) fast.Process(block, 480, 2, rate);
            Console.WriteLine("60 s of stereo, 20 active bands: {0} ms ({1:0.00}% of one core)", sw.ElapsedMilliseconds, sw.ElapsedMilliseconds / 600.0);
            return worst < 0.1 && same ? 0 : 2;
        }

        /// <summary>lyrics &lt;file&gt;: looks the lyrics up as the player does and reports where they came from and how many web requests it took.</summary>
        static int LyricsTest(string[] args)
        {
            var t = new TrackInfo(args[1]);
            var done = new ManualResetEvent(false);
            LyricsService.Ready += delegate { done.Set(); };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Lyrics l;
            if (!LyricsService.TryGet(t, out l)) { done.WaitOne(30000); LyricsService.TryGet(t, out l); }
            Console.WriteLine("time: {0} ms   web requests: {1}", sw.ElapsedMilliseconds, LyricsService.NetworkRequests);
            if (l == null) { Console.WriteLine("no lyrics"); return 1; }
            Console.WriteLine("source: {0}   synced: {1}   lines: {2}", l.Source, l.Synced, l.Lines.Count);
            foreach (LyricLine line in l.Lines.GetRange(0, Math.Min(3, l.Lines.Count)))
                Console.WriteLine("  [{0}] {1}", line.Time < 0 ? "  -  " : TimeSpan.FromSeconds(line.Time).ToString(@"m\:ss\.ff"), line.Text);
            return 0;
        }

        /// <summary>albums: reads the library (read-only) and reports albums whose title appears more than once in the Albums page.</summary>
        static int AlbumsTest()
        {
            var lib = new Library();
            lib.Load();
            lib.RebuildIndex();
            Console.WriteLine("{0} songs  ->  {1} albums, {2} artists", lib.Songs.Count, lib.Albums.Count, lib.Artists.Count);
            var byTitle = new Dictionary<string, List<AlbumInfo>>(StringComparer.CurrentCultureIgnoreCase);
            foreach (AlbumInfo a in lib.Albums)
            {
                List<AlbumInfo> l;
                if (!byTitle.TryGetValue(a.Title, out l)) byTitle[a.Title] = l = new List<AlbumInfo>();
                l.Add(a);
            }
            int dup = 0;
            foreach (var kv in byTitle)
                if (kv.Value.Count > 1)
                {
                    dup++;
                    Console.WriteLine("title shared by {0} albums: {1}", kv.Value.Count, kv.Key);
                    foreach (AlbumInfo a in kv.Value)
                        Console.WriteLine("    {0} ({1} songs)  {2}", a.Artist, a.Tracks.Count, Path.GetDirectoryName(a.Tracks[0].FilePath));
                }
            Console.WriteLine(dup == 0 ? "no repeated album titles" : dup + " repeated titles");
            return 0;
        }

        // ------------------------------------------------------------------ DSD / DoP

        /// <summary>
        /// dsd &lt;dir&gt; [deviceNameSubstring]: writes test DSF and DFF files (DSD64, stereo, 3 s of known bytes), checks that
        /// ffprobe sees them as DSD, that DoP decoding + the device conversion reproduce every DSD bit with correct markers
        /// (24-bit and 24-in-32 outputs), and that seeking lands on the right byte. With a device name it also asks that
        /// device (without playing anything) whether it accepts the DoP format.
        /// </summary>
        static int DsdTest(string[] args)
        {
            string dir = args[1];
            Directory.CreateDirectory(dir);
            const int rate = 2822400, ch = 2, seconds = 3;
            int bytesPerCh = rate / 8 * seconds;
            var orig = new byte[ch][];
            var rnd = new Random(7);
            for (int c = 0; c < ch; c++) { orig[c] = new byte[bytesPerCh]; rnd.NextBytes(orig[c]); }
            string dsf = Path.Combine(dir, "test-dsd64.dsf"), dff = Path.Combine(dir, "test-dsd64.dff");
            WriteDsf(dsf, orig, rate);
            WriteDff(dff, orig, rate);
            // A silent file (DSD idle pattern 0x69) for safe playback tests.
            var quiet = new byte[ch][];
            for (int c = 0; c < ch; c++) { quiet[c] = new byte[bytesPerCh]; for (int i = 0; i < bytesPerCh; i++) quiet[c][i] = DopDecoder.SilenceByte; }
            WriteDsf(Path.Combine(dir, "silence-dsd64.dsf"), quiet, rate);
            bool ok = true;
            foreach (string file in new[] { dsf, dff })
            {
                var t = new TrackInfo(file);
                FfmpegTools.Probe(t);
                Console.WriteLine("{0}: ffprobe codec={1} dsd={2} rate={3} ch={4} dur={5:0.00}s  [{6}]", Path.GetFileName(file), t.Codec, t.IsDsd, t.SampleRate, t.Channels, t.Duration, t.ShortFormat);
                int r, c2;
                Console.WriteLine("  native reader: {0}", DsdReader.TryInfo(file, out r, out c2) ? "DSD" + (r / 44100) + ", " + c2 + " ch" : "FAILED");
                foreach (int container in new[] { 32, 24 })
                {
                    var fmt = new PcmFormat { SampleRate = rate / 16, Channels = ch, ContainerBits = container, ValidBits = 24, IsFloat = false };
                    long bad = 0, frames = 0; bool nextB = false, markersOk = true;
                    var conv = new SampleConverter();
                    using (var dec = new DopDecoder(t, 0))
                    {
                        byte[] inBuf = new byte[4096 * dec.BytesPerFrame], outBuf = new byte[4096 * fmt.BlockAlign];
                        while (true)
                        {
                            int n = dec.Read(inBuf, 4096);
                            if (n == 0) { if (dec.IsDrained) break; dec.WaitForData(2000); continue; }
                            nextB = DopDecoder.StampMarkers(inBuf, 0, n, ch, nextB);
                            conv.Convert(inBuf, 0, dec.Kind, dec.SourceBits, dec.Channels, n, outBuf, 0, fmt, 1f);
                            int bps = container / 8;
                            for (int f = 0; f < n; f++)
                                for (int c = 0; c < ch; c++)
                                {
                                    int o = (f * ch + c) * bps + (bps - 3);   // the 24 valid bits (top 3 bytes of the container)
                                    byte lo = outBuf[o], mid = outBuf[o + 1], hi = outBuf[o + 2];
                                    long i = (frames + f) * 2;
                                    byte want = (frames + f) % 2 == 0 ? DopDecoder.MarkerA : DopDecoder.MarkerB;
                                    if (hi != want) markersOk = false;
                                    if (mid != orig[c][i] || lo != orig[c][i + 1]) bad++;
                                }
                            frames += n;
                        }
                    }
                    bool pass = bad == 0 && markersOk && frames == bytesPerCh / 2;
                    ok &= pass;
                    Console.WriteLine("  DoP → {0}-bit container: {1} frames, {2} wrong DSD bytes, markers {3}  {4}",
                        container, frames, bad, markersOk ? "alternate correctly" : "WRONG", pass ? "PASS" : "FAIL");
                }
                using (var dec = new DopDecoder(t, 1.0))
                {
                    byte[] b = new byte[dec.BytesPerFrame];
                    dec.WaitForData(2000);
                    dec.Read(b, 1);
                    long at = rate / 8;   // byte offset of 1.000 s
                    bool seekOk = b[2] == orig[0][at] && b[1] == orig[0][at + 1] && b[6] == orig[1][at] && b[5] == orig[1][at + 1];
                    ok &= seekOk;
                    Console.WriteLine("  seek to 1.000 s: {0}", seekOk ? "PASS (first frame = bytes at 1.000 s)" : "FAIL");
                }
            }
            if (args.Length > 2)
            {
                foreach (AudioDeviceInfo d in AudioDevices.GetRenderDevices())
                {
                    if (d.Name.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    IMMDevice dev = AudioDevices.GetDevice(d.Id);
                    IAudioClient client = Wasapi.ActivateAudioClient(dev);
                    foreach (int r in new[] { 2822400, 5644800 })
                        foreach (int cb in new[] { 32, 24 })
                        {
                            var f = new PcmFormat { SampleRate = r / 16, Channels = 2, ContainerBits = cb, ValidBits = 24, IsFloat = false };
                            IntPtr wfx = f.AllocWaveFormat();
                            int hr = client.IsFormatSupported(ShareMode.Exclusive, wfx, IntPtr.Zero);
                            System.Runtime.InteropServices.Marshal.FreeHGlobal(wfx);
                            Console.WriteLine("{0}: DoP for DSD{1} ({2}) in {3}-bit: {4}", d.Name, r / 44100, f.Describe(), cb, hr == 0 ? "accepted" : Wasapi.Describe(hr));
                        }
                    Wasapi.Release(client); Wasapi.Release(dev);
                    break;
                }
            }
            Console.WriteLine(ok ? "RESULT: all DSD checks passed" : "RESULT: FAILED");
            return ok ? 0 : 2;
        }

        static readonly byte[] BitReverse = BuildBitReverse();
        static byte[] BuildBitReverse()
        {
            var r = new byte[256];
            for (int i = 0; i < 256; i++) { int v = 0; for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) v |= 0x80 >> b; r[i] = (byte)v; }
            return r;
        }

        /// <summary>Sony DSF: little-endian, 4096-byte blocks per channel, LSB-first bits.</summary>
        static void WriteDsf(string path, byte[][] data, int rate)
        {
            int ch = data.Length, per = data[0].Length, block = 4096;
            int blocks = (per + block - 1) / block;
            long dataLen = (long)blocks * block * ch;
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("DSD ")); w.Write(28L); w.Write(28L + 52 + 12 + dataLen); w.Write(0L);
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(52L); w.Write(1); w.Write(0); w.Write(ch == 2 ? 2 : 1); w.Write(ch);
                w.Write(rate); w.Write(1); w.Write((long)per * 8); w.Write(block); w.Write(0);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(12L + dataLen);
                var buf = new byte[block];
                for (int b = 0; b < blocks; b++)
                    for (int c = 0; c < ch; c++)
                    {
                        Array.Clear(buf, 0, block);
                        for (int i = 0; i < block && b * block + i < per; i++) buf[i] = BitReverse[data[c][b * block + i]];
                        w.Write(buf);
                    }
            }
        }

        /// <summary>DSDIFF: big-endian chunks, byte-interleaved channels, MSB-first bits.</summary>
        static void WriteDff(string path, byte[][] data, int rate)
        {
            int ch = data.Length, per = data[0].Length;
            Func<long, byte[]> be64 = v => { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; };
            Func<int, byte[]> be32 = v => { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; };
            var prop = new MemoryStream();
            Action<string, byte[]> sub = (id, body) =>
            {
                prop.Write(Encoding.ASCII.GetBytes(id), 0, 4); prop.Write(be64(body.Length), 0, 8); prop.Write(body, 0, body.Length);
                if ((body.Length & 1) == 1) prop.WriteByte(0);
            };
            prop.Write(Encoding.ASCII.GetBytes("SND "), 0, 4);
            sub("FS  ", be32(rate));
            var chnl = new MemoryStream();
            chnl.WriteByte(0); chnl.WriteByte((byte)ch);
            foreach (string id in ch == 2 ? new[] { "SLFT", "SRGT" } : new[] { "C   " }) chnl.Write(Encoding.ASCII.GetBytes(id), 0, 4);
            sub("CHNL", chnl.ToArray());
            var cmpr = new MemoryStream();
            cmpr.Write(Encoding.ASCII.GetBytes("DSD "), 0, 4);
            byte[] name = Encoding.ASCII.GetBytes("not compressed");
            cmpr.WriteByte((byte)name.Length); cmpr.Write(name, 0, name.Length);
            if (((name.Length + 1) & 1) == 1) cmpr.WriteByte(0);
            sub("CMPR", cmpr.ToArray());
            byte[] propBody = prop.ToArray();
            long dsdLen = (long)per * ch;
            using (var w = new BinaryWriter(File.Create(path)))
            {
                long total = 4 + (12 + 4) + (12 + propBody.Length) + (12 + dsdLen);
                w.Write(Encoding.ASCII.GetBytes("FRM8")); w.Write(be64(total)); w.Write(Encoding.ASCII.GetBytes("DSD "));
                w.Write(Encoding.ASCII.GetBytes("FVER")); w.Write(be64(4)); w.Write(be32(0x01050000));
                w.Write(Encoding.ASCII.GetBytes("PROP")); w.Write(be64(propBody.Length)); w.Write(propBody);
                w.Write(Encoding.ASCII.GetBytes("DSD ")); w.Write(be64(dsdLen));
                var buf = new byte[per * ch];
                for (int i = 0; i < per; i++) for (int c = 0; c < ch; c++) buf[i * ch + c] = data[c][i];
                w.Write(buf);
            }
        }

        /// <summary>lyricsembed &lt;file&gt; &lt;title&gt; [artist]: searches LRCLIB like the dialog, embeds the best synced result, reads it back.</summary>
        static int LyricsEmbedTest(string[] args)
        {
            var t = new TrackInfo(args[1]);
            FfmpegTools.Probe(t);
            List<LyricsCandidate> found = LyricsService.Search(args[2], args.Length > 3 ? args[3] : "", t.Duration);
            int synced = found.FindAll(c => c.IsSynced).Count;
            Console.WriteLine("search: {0} results, {1} synced", found.Count, synced);
            foreach (LyricsCandidate c in found.GetRange(0, Math.Min(3, found.Count)))
                Console.WriteLine("  {0} — {1} · {2} · {3:0}s · {4}", c.Title, c.Artist, c.Album, c.Duration, c.IsSynced ? "synced" : "plain");
            LyricsCandidate best = found.Find(c => c.IsSynced);
            if (best == null) { Console.WriteLine("no synced result"); return 1; }
            string md5Before = AudioMd5(args[1]);
            string err = LyricsService.EmbedInFile(t, best.Text);
            Console.WriteLine("embed: " + (err ?? "OK"));
            if (err != null) return 2;
            Console.WriteLine("audio unchanged: " + (AudioMd5(args[1]) == md5Before));
            string json = FfmpegTools.RunAndCapture(FfmpegTools.FfprobePath, "-v error -show_entries format_tags=lyrics -of default=nw=1 " + FfmpegTools.Quote(args[1]), 15000);
            string firstLine = json.Split('\n')[0].Trim();
            Console.WriteLine("tag read back: " + (firstLine.Length > 70 ? firstLine.Substring(0, 70) + "…" : firstLine));
            return 0;
        }

        static string AudioMd5(string path)
        {
            return FfmpegTools.RunAndCapture(FfmpegTools.FfmpegPath, "-v error -nostdin -i " + FfmpegTools.Quote(path) + " -map 0:a -c copy -f md5 -", 60000).Trim();
        }

        /// <summary>lrccheck &lt;dir&gt;...: parses every .lrc / .txt / cached lyrics file (read-only) and lists those whose displayed lines still show timestamps.</summary>
        static int LrcCheck(string[] args)
        {
            var stamp = new System.Text.RegularExpressions.Regex(@"[\[<]\s*\d{1,3}\s*:\s*\d{1,2}");
            int files = 0, bad = 0;
            for (int a = 1; a < args.Length; a++)
            {
                if (!Directory.Exists(args[a])) continue;
                foreach (string f in Directory.EnumerateFiles(args[a], "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lrc" && ext != ".txt") continue;
                    string text;
                    try { text = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                    if (text.StartsWith("#soda-lyrics")) { int b = text.Replace("\r\n", "\n").IndexOf("\n\n", StringComparison.Ordinal); text = b >= 0 ? text.Replace("\r\n", "\n").Substring(b + 2) : ""; }
                    else if (text.StartsWith("#notfound") || text.StartsWith("#instrumental")) continue;
                    files++;
                    Lyrics l = Lyrics.Parse(text, "check");
                    var shown = l.Lines.FindAll(x => stamp.IsMatch(x.Text));
                    if (shown.Count == 0) continue;
                    bad++;
                    Console.WriteLine("{0}  (synced={1}, {2} of {3} lines show timestamps)", f, l.Synced, shown.Count, l.Lines.Count);
                    Console.WriteLine("    e.g. " + shown[0].Text.Substring(0, Math.Min(90, shown[0].Text.Length)));
                    string raw = text.Replace("\r\n", "\n").Split('\n')[0];
                    Console.WriteLine("    first raw line: " + raw.Substring(0, Math.Min(90, raw.Length)).Replace("﻿", "<BOM>"));
                }
            }
            Console.WriteLine("{0} lyrics files checked, {1} would show timestamps", files, bad);
            return 0;
        }

        /// <summary>lrcembedcheck &lt;dir&gt;: reads the embedded lyrics of every audio file (read-only, ffprobe) and lists those that would show timestamps.</summary>
        static int LrcEmbedCheck(string[] args)
        {
            var stamp = new System.Text.RegularExpressions.Regex(@"[\[<]\s*\d{1,3}\s*:\s*\d{1,2}");
            if (!FfmpegTools.Locate()) { Console.WriteLine("ffprobe not found"); return 2; }
            var files = new List<string>();
            foreach (string f in Directory.EnumerateFiles(args[1], "*.*", SearchOption.AllDirectories))
                if (FfmpegTools.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) files.Add(f);
            int withLyrics = 0, bad = 0, done = 0;
            var lk = new object();
            var keys = new Dictionary<string, int>();
            System.Threading.Tasks.Parallel.ForEach(files, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 }, delegate(string f)
            {
                string json;
                try { json = FfmpegTools.RunAndCapture(FfmpegTools.FfprobePath, "-v error -print_format json -show_entries format_tags:stream_tags " + FfmpegTools.Quote(f), 15000); }
                catch { return; }
                var root = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                var found = new List<KeyValuePair<string, string>>();
                Action<object> scan = o =>
                {
                    var d = o as Dictionary<string, object>; object tags;
                    if (d != null && d.TryGetValue("tags", out tags) && tags is Dictionary<string, object>)
                        foreach (var kv in (Dictionary<string, object>)tags)
                            if (kv.Key.ToLowerInvariant().Contains("lyric") || kv.Key.ToLowerInvariant() == "uslt" || kv.Key.ToLowerInvariant().StartsWith("©lyr"))
                                found.Add(new KeyValuePair<string, string>(kv.Key, Convert.ToString(kv.Value)));
                };
                object fo;
                if (root != null && root.TryGetValue("format", out fo)) scan(fo);
                if (root != null && root.TryGetValue("streams", out fo) && fo is object[]) foreach (object s in (object[])fo) scan(s);
                lock (lk)
                {
                    done++;
                    if (found.Count == 0) return;
                    withLyrics++;
                    foreach (var kv in found) { int c; keys.TryGetValue(kv.Key.ToLowerInvariant(), out c); keys[kv.Key.ToLowerInvariant()] = c + 1; }
                }
                // Same choice as the player (LyricsService.FromEmbeddedTags), then the same parser.
                string synced = null, plain = null;
                foreach (var kv in found)
                {
                    string k = kv.Key.ToLowerInvariant(), v = kv.Value;
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (k.StartsWith("syncedlyrics") || (k.StartsWith("lyrics") && v.Contains("[0"))) synced = synced ?? v;
                    else if (k.StartsWith("lyrics") || k.StartsWith("unsyncedlyrics") || k == "uslt" || k.StartsWith("©lyr")) plain = plain ?? v;
                }
                string text = synced ?? plain;
                if (text == null) return;
                Lyrics l = Lyrics.Parse(text, "check");
                var shown = l.Lines.FindAll(x => stamp.IsMatch(x.Text));
                if (shown.Count == 0) return;
                lock (lk)
                {
                    bad++;
                    if (bad <= 12)
                    {
                        Console.WriteLine("{0}\n    keys: {1}  synced={2}  {3} of {4} lines show timestamps", f, string.Join(", ", found.ConvertAll(x => x.Key).ToArray()), l.Synced, shown.Count, l.Lines.Count);
                        Console.WriteLine("    e.g.: " + shown[0].Text.Substring(0, Math.Min(100, shown[0].Text.Length)).Replace("﻿", "<BOM>"));
                        string raw = text.Replace("\r\n", "\n").Split('\n')[0];
                        Console.WriteLine("    raw first line: " + raw.Substring(0, Math.Min(100, raw.Length)).Replace("﻿", "<BOM>").Replace("\r", "<CR>"));
                    }
                }
            });
            Console.WriteLine("{0} audio files, {1} with embedded lyrics, {2} would show timestamps", done, withLyrics, bad);
            foreach (var kv in keys) Console.WriteLine("  tag key {0}: {1} files", kv.Key, kv.Value);
            return 0;
        }

        /// <summary>
        /// spectrum &lt;file&gt; [startSeconds] [seconds]: runs the visualizer's analyser over the song exactly as the screen
        /// does (60 frames/s, audio fed in 10 ms render buffers) and reports how much the bars move: per-band swing
        /// (p90 − p10 of the bar height), how often the bass bar jumps, and a text plot of the bass and mid bars.
        /// </summary>
        static int SpectrumTest(string[] args)
        {
            var t = new TrackInfo(args[1]);
            FfmpegTools.Probe(t);
            double start = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 30;
            double seconds = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 8;
            int rate = t.SampleRate, ch = Math.Min(2, t.Channels);
            var tap = new SpectrumTap();
            var an = new SpectrumAnalyzer();
            var levels = new List<float[]>();
            using (var dec = new FfmpegDecoder(t, start, rate, ch, PcmKind.F32))
            {
                int chunk = rate / 100;   // 10 ms render buffers
                byte[] buf = new byte[chunk * dec.BytesPerFrame];
                double audioTime = 0, nextFrame = 0;
                while (audioTime < seconds)
                {
                    int got = 0;
                    while (got == 0) { got = dec.Read(buf, chunk); if (got == 0) { if (dec.IsDrained) break; dec.WaitForData(1000); } }
                    if (got == 0) break;
                    tap.Push(buf, got, dec.Channels, PcmKind.F32, rate);
                    tap.ForceLive();
                    audioTime += got / (double)rate;
                    while (nextFrame <= audioTime) { an.Update(tap, 1 / 60.0); levels.Add((float[])an.Level.Clone()); nextFrame += 1 / 60.0; }
                }
            }
            int n = SpectrumAnalyzer.Bands;
            Console.WriteLine("{0} frames analysed ({1:0.0} s from {2:0} s)", levels.Count, levels.Count / 60.0, start);
            double meanSwing = 0;
            var line = new System.Text.StringBuilder("swing per band (p90-p10, 0..1): ");
            for (int b = 0; b < n; b++)
            {
                var v = levels.ConvertAll(x => x[b]); v.Sort();
                double swing = v[(int)(v.Count * 0.9)] - v[(int)(v.Count * 0.1)];
                double mean = 0; foreach (float x in v) mean += x; mean /= v.Count;
                meanSwing += swing;
                line.Append(swing.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                if (b == 0) Console.WriteLine("bass band average height {0:0.00}", mean);
            }
            Console.WriteLine(line);
            Console.WriteLine("mean swing {0:0.000}", meanSwing / n);
            // Jumps: the bass bar rising by > 0.15 within 100 ms (what reads as a "kick").
            int jumps = 0;
            for (int i = 6; i < levels.Count; i++) if (levels[i][1] - levels[i - 6][1] > 0.15f) { jumps++; i += 12; }
            Console.WriteLine("bass jumps: {0} ({1:0.0}/s)", jumps, jumps / (levels.Count / 60.0));
            const string ramp = " .:-=+*#%@";
            foreach (int b in new[] { 1, 10, 17 })
            {
                var sb = new System.Text.StringBuilder(string.Format("band {0,2}: ", b));
                for (int i = 0; i < Math.Min(levels.Count, 240); i += 2) sb.Append(ramp[Math.Min(9, (int)(levels[i][b] * 9.99))]);
                Console.WriteLine(sb);
            }
            return 0;
        }

        static int Play(string[] args)
        {
            var t = new TrackInfo(args[1]);
            double seconds = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            var engine = new PlaybackEngine
            {
                ExclusiveMode = !(args.Length > 3 && args[3] == "shared"),
                BufferMs = args.Length > 4 ? int.Parse(args[4]) : 20
            };
            engine.NativeDsd = Environment.GetEnvironmentVariable("BPP_TEST_DSD") == "native";
            string eqPreset = Environment.GetEnvironmentVariable("BPP_TEST_EQ");   // e.g. "Rock": play with the EQ switched on
            if (!string.IsNullOrEmpty(eqPreset))
            {
                float[] g = EqualizerSettings.PresetGains(eqPreset);
                engine.Equalizer.SetAll(g, EqualizerSettings.SuggestedPreamp(g));
                engine.Equalizer.Enabled = true;
                Console.WriteLine("EQ on: " + eqPreset);
            }
            if (args.Length > 5)
                foreach (AudioDeviceInfo d in AudioDevices.GetRenderDevices())
                    if (d.Name.IndexOf(args[5], StringComparison.OrdinalIgnoreCase) >= 0) { engine.DeviceId = d.Id; Console.WriteLine("device: " + d.Name); break; }
            // Queue the remaining files (args[6..]) to exercise gapless hand-over / format-change re-init.
            var queue = new List<TrackInfo> { t };
            for (int i = 6; i < args.Length; i++) queue.Add(new TrackInfo(args[i]));
            engine.NextTrackProvider = delegate(TrackInfo cur) { int idx = queue.IndexOf(cur); return idx >= 0 && idx + 1 < queue.Count ? queue[idx + 1] : null; };
            var sw = new System.Diagnostics.Stopwatch();
            string error = null;
            var done = new ManualResetEvent(false);
            engine.TrackChanged += delegate(TrackInfo tr)
            {
                Console.WriteLine("{0,6:0.00}s track: {1}", sw.Elapsed.TotalSeconds, Path.GetFileName(tr.FilePath));
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(400);
                    Console.WriteLine("        output: {0}\n        path:   {1}\n        bit-perfect: {2}",
                        engine.OutputDescription, engine.ProcessingDescription, engine.IsBitPerfect);
                });
            };
            engine.ErrorOccurred += delegate(string m) { error = m; done.Set(); };
            engine.PlaybackFinished += delegate { Console.WriteLine("finished"); done.Set(); };
            sw.Start();
            engine.Play(t, 0);
            bool trace = Environment.GetEnvironmentVariable("BPP_TRACE_POS") == "1";
            double seekAt = trace && Environment.GetEnvironmentVariable("BPP_TRACE_SEEK") != null ? 1.0 : -1;
            while (!done.WaitOne(trace ? (seekAt < 0 && sw.Elapsed.TotalSeconds > 1 && sw.Elapsed.TotalSeconds < 1.6 ? 15 : 100) : 250) && sw.Elapsed.TotalSeconds < seconds)
            {
                if (seekAt > 0 && sw.Elapsed.TotalSeconds >= seekAt)
                {
                    seekAt = -1;
                    double target = double.Parse(Environment.GetEnvironmentVariable("BPP_TRACE_SEEK"), System.Globalization.CultureInfo.InvariantCulture);
                    engine.Seek(target);
                    Console.WriteLine("  wall {0,5:0.000}s  SEEK → {1:0.00}s", sw.Elapsed.TotalSeconds, target);
                }
                if (trace) Console.WriteLine("  wall {0,5:0.000}s  position {1,6:0.000}s  paused={2}", sw.Elapsed.TotalSeconds, engine.Position, engine.IsPaused);
            }
            Console.WriteLine("position: {0:0.00}s after {1:0.00}s wall", engine.Position, sw.Elapsed.TotalSeconds);
            engine.Stop();
            if (error != null) { Console.WriteLine("ERROR: " + error); return 2; }
            return 0;
        }
    }
}

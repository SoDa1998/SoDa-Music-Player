using System;
using System.IO;
using System.Text;
using System.Threading;

namespace BitPerfectPlayer
{
    /// <summary>What every decoder gives the render thread: whole PCM frames from a ring buffer, plus its format.</summary>
    internal interface IAudioDecoder : IDisposable
    {
        TrackInfo Track { get; }
        int SampleRate { get; }
        int Channels { get; }
        int SourceBits { get; }
        int BytesPerFrame { get; }
        PcmKind Kind { get; }
        double StartSeconds { get; }
        long FramesConsumed { get; }
        string ErrorText { get; }
        bool EndOfStream { get; }
        bool IsDrained { get; }
        /// <summary>True for DoP frames (DSD bits in 24-bit PCM words): they must reach the DAC unscaled and undithered.</summary>
        bool IsDop { get; }
        bool WaitForData(int timeoutMs);
        int Read(byte[] dst, int maxFrames);
    }

    /// <summary>
    /// Reads raw 1-bit DSD from DSF (Sony) and uncompressed DSDIFF (.dff) files, returning each channel's bytes MSB-first
    /// (earliest bit first), interleaved byte by byte: ch0, ch1, ch0, ch1, … DST-compressed DFF is not supported (those
    /// files are converted to PCM by FFmpeg instead).
    /// </summary>
    internal sealed class DsdReader : IDisposable
    {
        public int Channels { get; private set; }
        /// <summary>1-bit sample rate, e.g. 2 822 400 (DSD64).</summary>
        public int DsdRate { get; private set; }
        /// <summary>DSD bytes per channel in the whole file.</summary>
        public long BytesPerChannel { get; private set; }
        public bool IsDsf { get; private set; }

        readonly FileStream fs;
        long dataStart;
        int blockSize = 4096;              // DSF: bytes per channel per block
        bool lsbFirst;                     // DSF with bits-per-sample 1
        long pos;                          // bytes per channel already returned
        byte[] block;                      // DSF: one block group (blockSize × channels)
        int blockFill = -1, blockOffset;   // bytes per channel available / consumed in 'block'
        static readonly byte[] Reverse = MakeReverse();

        static byte[] MakeReverse()
        {
            var r = new byte[256];
            for (int i = 0; i < 256; i++) { int v = 0; for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) v |= 0x80 >> b; r[i] = (byte)v; }
            return r;
        }

        public DsdReader(string path)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            try
            {
                var id = new byte[4];
                ReadExact(id, 4);
                string tag = Encoding.ASCII.GetString(id);
                if (tag == "DSD ") ParseDsf();
                else if (tag == "FRM8") ParseDff();
                else throw new InvalidDataException("not a DSF or DFF file");
                if (Channels <= 0 || Channels > 8 || DsdRate < 2822400) throw new InvalidDataException("unsupported DSD stream");
                Seek(0);
            }
            catch { fs.Dispose(); throw; }
        }

        /// <summary>Quick check: can this file be played natively (DSF, or uncompressed DFF)?</summary>
        public static bool TryInfo(string path, out int dsdRate, out int channels)
        {
            dsdRate = channels = 0;
            try
            {
                using (var r = new DsdReader(path)) { dsdRate = r.DsdRate; channels = r.Channels; return true; }
            }
            catch { return false; }
        }

        void ReadExact(byte[] b, int n)
        {
            int off = 0;
            while (off < n) { int k = fs.Read(b, off, n - off); if (k <= 0) throw new EndOfStreamException(); off += k; }
        }

        byte[] Bytes(int n) { var b = new byte[n]; ReadExact(b, n); return b; }
        static long LE64(byte[] b, int o) { return BitConverter.ToInt64(b, o); }
        static int LE32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
        static long BE64(byte[] b, int o) { long v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i]; return v; }
        static int BE32(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }
        static int BE16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }

        void ParseDsf()
        {
            IsDsf = true;
            byte[] head = Bytes(24);                        // rest of the 28-byte "DSD " chunk
            long dsdChunk = LE64(head, 0);
            fs.Position = dsdChunk;
            byte[] fmt = Bytes(52);
            if (Encoding.ASCII.GetString(fmt, 0, 4) != "fmt ") throw new InvalidDataException("DSF: no fmt chunk");
            if (LE32(fmt, 16) != 0) throw new InvalidDataException("DSF: unsupported format id");
            Channels = LE32(fmt, 24);
            DsdRate = LE32(fmt, 28);
            int bits = LE32(fmt, 32);
            lsbFirst = bits == 1;
            long sampleCount = LE64(fmt, 36);
            blockSize = LE32(fmt, 44);
            if (blockSize <= 0 || blockSize > (1 << 20)) throw new InvalidDataException("DSF: bad block size");
            fs.Position = dsdChunk + LE64(fmt, 4);
            byte[] data = Bytes(12);
            if (Encoding.ASCII.GetString(data, 0, 4) != "data") throw new InvalidDataException("DSF: no data chunk");
            dataStart = fs.Position;
            BytesPerChannel = sampleCount / 8;
            block = new byte[blockSize * Channels];
        }

        void ParseDff()
        {
            IsDsf = false;
            byte[] frm = Bytes(12);                         // size(8) + form type(4)
            if (Encoding.ASCII.GetString(frm, 8, 4) != "DSD ") throw new InvalidDataException("DFF: not a DSD form");
            long end = 12 + BE64(frm, 0);
            bool compressed = false;
            while (fs.Position + 12 <= end)
            {
                byte[] ck = Bytes(12);
                string cid = Encoding.ASCII.GetString(ck, 0, 4);
                long size = BE64(ck, 4), start = fs.Position;
                if (cid == "PROP")
                {
                    fs.Position += 4;                       // "SND "
                    while (fs.Position + 12 <= start + size)
                    {
                        byte[] sk = Bytes(12);
                        string sid = Encoding.ASCII.GetString(sk, 0, 4);
                        long ssize = BE64(sk, 4), sstart = fs.Position;
                        if (sid == "FS  ") DsdRate = BE32(Bytes(4), 0);
                        else if (sid == "CHNL") Channels = BE16(Bytes(2), 0);
                        else if (sid == "CMPR") compressed = Encoding.ASCII.GetString(Bytes(4)) != "DSD ";
                        fs.Position = sstart + ssize + (ssize & 1);
                    }
                }
                else if (cid == "DSD ")
                {
                    dataStart = start;
                    BytesPerChannel = Channels > 0 ? size / Channels : 0;
                    break;
                }
                else if (cid == "DST ") { compressed = true; break; }
                fs.Position = start + size + (size & 1);
            }
            if (compressed) throw new InvalidDataException("DST-compressed DFF");
            if (dataStart == 0) throw new InvalidDataException("DFF: no DSD data");
        }

        public double DurationSeconds { get { return BytesPerChannel * 8.0 / DsdRate; } }

        /// <summary>Positions at <paramref name="seconds"/> (to the byte for DFF, to the 2-byte DoP frame for DSF).</summary>
        public void Seek(double seconds)
        {
            long byteOff = (long)(Math.Max(0, seconds) * DsdRate / 8);
            byteOff = Math.Min(byteOff & ~1L, BytesPerChannel);
            pos = byteOff;
            if (IsDsf)
            {
                long blk = byteOff / blockSize;
                fs.Position = dataStart + blk * blockSize * Channels;
                blockFill = -1;
                blockOffset = (int)(byteOff % blockSize);
            }
            else fs.Position = dataStart + byteOff * Channels;
        }

        /// <summary>Reads up to <paramref name="perChannel"/> bytes per channel, interleaved byte-wise, MSB-first. Returns bytes per channel read.</summary>
        public int Read(byte[] dst, int perChannel)
        {
            long left = BytesPerChannel - pos;
            if (left <= 0) return 0;
            int want = (int)Math.Min(perChannel, left);
            int done = 0;
            if (!IsDsf)
            {
                int bytes = want * Channels, off = 0;
                while (off < bytes) { int k = fs.Read(dst, off, bytes - off); if (k <= 0) break; off += k; }
                done = off / Channels;
            }
            else
            {
                while (done < want)
                {
                    if (blockFill < 0 || blockOffset >= blockFill)
                    {
                        int keep = blockFill < 0 ? blockOffset : 0;   // after a seek: skip into the first block
                        int got = 0, need = block.Length;
                        while (got < need) { int k = fs.Read(block, got, need - got); if (k <= 0) break; got += k; }
                        if (got < need) { if (got == 0) break; Array.Clear(block, got, need - got); }
                        blockFill = blockSize;
                        blockOffset = keep;
                    }
                    int n = Math.Min(want - done, blockFill - blockOffset);
                    for (int i = 0; i < n; i++)
                        for (int c = 0; c < Channels; c++)
                        {
                            byte b = block[c * blockSize + blockOffset + i];
                            dst[(done + i) * Channels + c] = lsbFirst ? Reverse[b] : b;
                        }
                    blockOffset += n;
                    done += n;
                }
            }
            pos += done;
            return done;
        }

        public void Dispose() { fs.Dispose(); }
    }

    /// <summary>
    /// Native DSD playback as DoP ("DSD over PCM", v1.1): every 16 DSD bits per channel travel in one 24-bit PCM sample at
    /// 1/16 of the DSD rate (DSD64 → 176.4 kHz), topped with a marker byte. The decoder leaves the marker byte empty; the
    /// render thread stamps the alternating 0x05 / 0xFA markers as it writes to the device, so they stay continuous across
    /// pauses, seeks and gapless track changes. Frames are 32-bit little-endian words: [0x00][2nd DSD byte][1st DSD byte][marker].
    /// </summary>
    internal sealed class DopDecoder : IAudioDecoder
    {
        public TrackInfo Track { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int SourceBits { get { return 24; } }
        public int BytesPerFrame { get; private set; }
        public PcmKind Kind { get { return PcmKind.S32; } }
        public double StartSeconds { get; private set; }
        public long FramesConsumed { get { return consumed; } }
        public string ErrorText { get { return error; } }
        public bool IsDop { get { return true; } }
        public int DsdRate { get; private set; }

        public const byte MarkerA = 0x05, MarkerB = 0xFA, SilenceByte = 0x69;

        readonly DsdReader reader;
        readonly Thread thread;
        readonly byte[] ring;
        readonly object lk = new object();
        int readPos, writePos, count;
        bool eof;
        volatile bool disposed;
        volatile string error;
        long consumed;

        public DopDecoder(TrackInfo track, double startSeconds)
        {
            Track = track;
            StartSeconds = startSeconds;
            reader = new DsdReader(track.FilePath);
            reader.Seek(startSeconds);
            Channels = reader.Channels;
            DsdRate = reader.DsdRate;
            SampleRate = DsdRate / 16;
            BytesPerFrame = 4 * Channels;
            ring = new byte[BytesPerFrame * SampleRate * 2];   // ~2 s
            thread = new Thread(Loop) { IsBackground = true, Name = "DSD reader", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        void Loop()
        {
            const int FramesPerChunk = 4096;
            byte[] dsd = new byte[FramesPerChunk * 2 * Channels];
            byte[] pcm = new byte[FramesPerChunk * BytesPerFrame];
            try
            {
                while (!disposed)
                {
                    int perCh = reader.Read(dsd, FramesPerChunk * 2);
                    if (perCh <= 0) break;
                    int frames = perCh / 2;                 // an odd trailing byte (never in practice) is dropped
                    for (int f = 0; f < frames; f++)
                        for (int c = 0; c < Channels; c++)
                        {
                            int o = (f * Channels + c) * 4;
                            pcm[o] = 0;
                            pcm[o + 1] = dsd[(2 * f + 1) * Channels + c];   // later byte → bits 7..0 of the 24-bit sample
                            pcm[o + 2] = dsd[(2 * f) * Channels + c];       // earlier byte → bits 15..8
                            pcm[o + 3] = 0;                                  // marker, stamped by the render thread
                        }
                    int bytes = frames * BytesPerFrame, off = 0;
                    while (bytes > 0)
                    {
                        lock (lk)
                        {
                            while (count == ring.Length && !disposed) Monitor.Wait(lk, 250);
                            if (disposed) return;
                            int chunk = Math.Min(bytes, Math.Min(ring.Length - count, ring.Length - writePos));
                            Buffer.BlockCopy(pcm, off, ring, writePos, chunk);
                            writePos = (writePos + chunk) % ring.Length;
                            count += chunk; off += chunk; bytes -= chunk;
                            Monitor.PulseAll(lk);
                        }
                    }
                }
            }
            catch (Exception ex) { if (!disposed) error = ex.Message; }
            finally { lock (lk) { eof = true; Monitor.PulseAll(lk); } }
        }

        public bool EndOfStream { get { lock (lk) return eof; } }
        public bool IsDrained { get { lock (lk) return eof && count < BytesPerFrame; } }

        public bool WaitForData(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
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
                consumed += frames;
                if (frames > 0) Monitor.PulseAll(lk);
                return frames;
            }
        }

        /// <summary>Writes <paramref name="frames"/> DoP frames of DSD silence (0x69 pattern) into dst, markers left empty.</summary>
        public static void Silence(byte[] dst, int offset, int frames, int channels)
        {
            for (int i = 0; i < frames * channels; i++)
            {
                int o = offset + i * 4;
                dst[o] = 0; dst[o + 1] = SilenceByte; dst[o + 2] = SilenceByte; dst[o + 3] = 0;
            }
        }

        /// <summary>Stamps alternating DoP markers into the top byte of each frame's samples; returns the next marker state.</summary>
        public static bool StampMarkers(byte[] buf, int offset, int frames, int channels, bool nextIsB)
        {
            for (int f = 0; f < frames; f++)
            {
                byte m = nextIsB ? MarkerB : MarkerA;
                for (int c = 0; c < channels; c++) buf[offset + (f * channels + c) * 4 + 3] = m;
                nextIsB = !nextIsB;
            }
            return nextIsB;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (lk) Monitor.PulseAll(lk);
            try { thread.Join(500); } catch { }
            reader.Dispose();
        }
    }
}

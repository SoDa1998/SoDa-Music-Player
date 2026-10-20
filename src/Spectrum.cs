using System;

namespace BitPerfectPlayer
{
    /// <summary>
    /// Read-only copy of the audio for the visualizer. The render thread pushes the decoded samples (mono, float) into a ring
    /// buffer; nothing is ever written back, so the stream to the DAC is unaffected.
    /// </summary>
    internal sealed class SpectrumTap
    {
        const int Size = 8192;                 // power of two
        readonly float[] ring = new float[Size];
        int write;                             // total samples written (wraps via mask)
        readonly object lk = new object();
        volatile int rate = 48000;
        long lastPushTicks;

        public int SampleRate { get { return rate; } }
        /// <summary>True when audio arrived within the last 0.3 s (otherwise the bars fall back to rest).</summary>
        public bool Live { get { return DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref lastPushTicks) < 3000000; } }

        /// <summary>Audio thread: interleaved PCM of the given kind → mono float into the ring.</summary>
        public void Push(byte[] buf, int frames, int channels, PcmKind kind, int sampleRate)
        {
            if (frames <= 0 || channels <= 0) return;
            rate = sampleRate;
            lock (lk)
            {
                int w = write;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int i = f * channels + c;
                        switch (kind)
                        {
                            case PcmKind.S16: sum += BitConverter.ToInt16(buf, i * 2) / 32768f; break;
                            case PcmKind.S32: sum += BitConverter.ToInt32(buf, i * 4) / 2147483648f; break;
                            default: sum += BitConverter.ToSingle(buf, i * 4); break;
                        }
                    }
                    ring[w & (Size - 1)] = sum / channels;
                    w++;
                }
                write = w;
            }
            System.Threading.Interlocked.Exchange(ref lastPushTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>Self-test: treat the tap as live regardless of the wall clock (audio is fed faster than real time).</summary>
        internal void ForceLive() { System.Threading.Interlocked.Exchange(ref lastPushTicks, DateTime.UtcNow.Ticks); }

        /// <summary>
        /// How far the newest pushed audio is ahead of what is being heard (the device buffer), in frames. The analyser
        /// looks this far back, so the bars move with the sound instead of slightly before it.
        /// </summary>
        public volatile int DelayFrames;

        /// <summary>UI thread: copies the <paramref name="n"/> samples now reaching the speakers (n + delay ≤ 8192).</summary>
        public void Latest(float[] dst, int n)
        {
            lock (lk)
            {
                int delay = Math.Max(0, Math.Min(Size - n, DelayFrames));
                int start = write - delay - n;
                for (int i = 0; i < n; i++) dst[i] = ring[(start + i) & (Size - 1)];
            }
        }
    }

    /// <summary>
    /// FFT spectrum in log-spaced bands, tuned to pulse with the music (UI thread only):
    ///  • a short analysis window (2048 samples ≈ 45 ms) so individual beats are resolved;
    ///  • auto-gain: heights are relative to the song's recent loudest band (fast up, ~6 dB/s down), so a loudly mastered
    ///    track doesn't pin every bar at the top and a quiet passage still fills the display;
    ///  • onset emphasis: a band louder than its own recent average (a kick, a snare hit) gets an extra jump;
    ///  • near-instant rise (12 ms) and a quick fall (90 ms), so bars drop between beats; peak caps hang briefly, then fall.
    /// </summary>
    internal sealed class SpectrumAnalyzer
    {
        public const int Bands = 24;
        const int N = 2048;
        const double MinHz = 40, MaxHz = 16000;
        const double RangeDb = 34;          // displayed range below the running reference level
        readonly float[] samples = new float[N];
        readonly double[] re = new double[N], im = new double[N], window = new double[N];
        public readonly float[] Level = new float[Bands];   // 0..1, smoothed
        public readonly float[] Peak = new float[Bands];    // 0..1, falling caps
        readonly float[] peakHold = new float[Bands];
        readonly double[] avgDb = new double[Bands];          // each band's recent average (for onsets)
        double refDb = -30;                                   // auto-gain reference: recent loudest band

        public SpectrumAnalyzer()
        {
            for (int i = 0; i < N; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));   // Hann
        }

        /// <summary>True while anything is still visible (bars or caps above zero).</summary>
        public bool Active
        {
            get { for (int b = 0; b < Bands; b++) if (Level[b] > 0.002f || Peak[b] > 0.002f) return true; return false; }
        }

        /// <summary>Advances the display by <paramref name="dt"/> seconds; <paramref name="tap"/> null = let everything fall.</summary>
        public void Update(SpectrumTap tap, double dt)
        {
            var target = new float[Bands];
            if (tap != null && tap.Live)
            {
                tap.Latest(samples, N);
                for (int i = 0; i < N; i++) { re[i] = samples[i] * window[i]; im[i] = 0; }
                Fft(re, im);
                int rate = tap.SampleRate;
                double binHz = rate / (double)N;
                var db = new double[Bands];
                double loudest = -120;
                for (int b = 0; b < Bands; b++)
                {
                    double f0 = MinHz * Math.Pow(MaxHz / MinHz, b / (double)Bands), f1 = MinHz * Math.Pow(MaxHz / MinHz, (b + 1) / (double)Bands);
                    int k0 = Math.Max(1, (int)Math.Round(f0 / binHz)), k1 = Math.Max(k0, Math.Min(N / 2 - 1, (int)Math.Round(f1 / binHz) - 1));
                    double power = 0;
                    for (int kk = k0; kk <= k1; kk++) power += re[kk] * re[kk] + im[kk] * im[kk];   // band energy, not one bin
                    double d = 10 * Math.Log10(power / ((N / 4.0) * (N / 4.0)) + 1e-12);
                    // Music falls off ~3 dB per octave: tilt it back so treble bars move as much as bass bars.
                    d += 3.0 * Math.Log((f0 + f1) / 2 / 1000.0, 2);
                    db[b] = d;
                    loudest = Math.Max(loudest, d);
                }
                // Auto-gain: jump up to a louder level at once, sink back slowly (≈ 6 dB/s), never below a quiet floor.
                refDb = loudest > refDb ? loudest : Math.Max(Math.Max(loudest, -60), refDb - 6 * dt);
                double avgK = 1 - Math.Exp(-dt / 0.45);
                for (int b = 0; b < Bands; b++)
                {
                    double level = Math.Max(0, Math.Min(1, (db[b] - (refDb - RangeDb)) / RangeDb));
                    double onset = Math.Max(0, Math.Min(1, (db[b] - avgDb[b] - 1.5) / 9.0));   // how far above its recent average
                    avgDb[b] += (db[b] - avgDb[b]) * avgK;
                    target[b] = (float)Math.Max(0, Math.Min(1, 0.85 * Math.Pow(level, 1.6) + 0.45 * onset));
                }
            }
            else for (int b = 0; b < Bands; b++) avgDb[b] += (-90 - avgDb[b]) * (1 - Math.Exp(-dt / 0.45));
            float rise = (float)(1 - Math.Exp(-dt / 0.012)), fall = (float)(1 - Math.Exp(-dt / 0.09));
            for (int b = 0; b < Bands; b++)
            {
                float t = target[b];
                Level[b] += (t - Level[b]) * (t > Level[b] ? rise : fall);
                if (Level[b] >= Peak[b]) { Peak[b] = Level[b]; peakHold[b] = 0.25f; }
                else if ((peakHold[b] -= (float)dt) <= 0) Peak[b] = Math.Max(Level[b], Peak[b] - (float)(dt * 1.4));
            }
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int a = i + j, b = a + len / 2;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                    }
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace BitPerfectPlayer
{
    /// <summary>
    /// 20-band graphic equalizer settings, shared between the UI and the audio thread. Bands are log-spaced from 25 Hz to
    /// 20 kHz (~½ octave apart), ±12 dB each, plus a preamp. The engine applies it in Shared mode only: Exclusive mode
    /// stays bit-perfect, so no DSP is ever applied there.
    /// </summary>
    internal sealed class EqualizerSettings
    {
        public const int Bands = 20;
        public const float MaxDb = 12f;
        public static readonly double[] Frequencies = MakeFrequencies();
        /// <summary>Q giving ~½-octave bands that overlap smoothly.</summary>
        public static readonly double BandQ = Q();

        readonly float[] gains = new float[Bands];
        volatile bool enabled;
        float preamp;
        int version;
        public string Preset = "Flat";

        static double[] MakeFrequencies()
        {
            var f = new double[Bands];
            for (int i = 0; i < Bands; i++) f[i] = 25.0 * Math.Pow(20000.0 / 25.0, i / (double)(Bands - 1));
            return f;
        }

        static double Q()
        {
            double r = Math.Pow(20000.0 / 25.0, 1.0 / (Bands - 1));   // ratio between neighbouring bands
            return Math.Sqrt(r) / (r - 1);
        }

        public bool Enabled { get { return enabled; } set { if (enabled != value) { enabled = value; Bump(); } } }
        public float Preamp { get { return preamp; } set { preamp = Clamp(value); Bump(); } }
        public int Version { get { return Volatile.Read(ref version); } }

        public float GetGain(int band) { return gains[band]; }
        public void SetGain(int band, float db) { gains[band] = Clamp(db); Bump(); }
        public float[] Snapshot() { lock (gains) return (float[])gains.Clone(); }

        public void SetAll(float[] g, float pre)
        {
            lock (gains) for (int i = 0; i < Bands; i++) gains[i] = Clamp(i < g.Length ? g[i] : 0f);
            preamp = Clamp(pre);
            Bump();
        }

        /// <summary>True when processing would change the audio at all.</summary>
        public bool IsActive
        {
            get
            {
                if (!enabled) return false;
                if (Math.Abs(preamp) > 0.01f) return true;
                for (int i = 0; i < Bands; i++) if (Math.Abs(gains[i]) > 0.01f) return true;
                return false;
            }
        }

        void Bump() { Interlocked.Increment(ref version); }
        static float Clamp(float v) { return (float)Math.Round(Math.Max(-MaxDb, Math.Min(MaxDb, v)) * 10) / 10f; }

        public static string FormatFrequency(double f)
        {
            // Two significant figures: 25, 36, 51, 72, 100, 150, … 1.2k, 1.7k, … 14k, 20k.
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(f)) - 1);
            double r = Math.Round(f / mag) * mag;
            if (r >= 1000) return (r / 1000).ToString("0.#", ci) + "k";
            return r.ToString("0", ci);
        }

        // ------------------------------------------------------------------ presets

        public static readonly string[] PresetNames =
            { "Flat", "Bass boost", "Bass reducer", "Treble boost", "Vocal", "Rock", "Pop", "Jazz", "Classical", "Electronic", "Loudness", "Acoustic" };

        /// <summary>Preset curves as (frequency Hz, dB) control points, interpolated on a log-frequency axis.</summary>
        static double[][] PresetPoints(string name)
        {
            switch (name)
            {
                case "Bass boost": return new[] { P(25, 6), P(80, 5.5), P(160, 3.5), P(300, 1.5), P(600, 0), P(20000, 0) };
                case "Bass reducer": return new[] { P(25, -6), P(80, -5), P(160, -3), P(300, -1), P(600, 0), P(20000, 0) };
                case "Treble boost": return new[] { P(25, 0), P(1500, 0), P(4000, 2), P(8000, 4.5), P(20000, 6) };
                case "Vocal": return new[] { P(25, -3), P(150, -2), P(400, 0.5), P(1200, 3), P(3000, 3.5), P(6000, 1.5), P(20000, -1) };
                case "Rock": return new[] { P(25, 4.5), P(100, 3.5), P(400, -1), P(1000, -1.5), P(3000, 1.5), P(8000, 3.5), P(20000, 4) };
                case "Pop": return new[] { P(25, -1), P(100, 1), P(400, 2.5), P(1500, 3), P(4000, 1.5), P(12000, 0.5), P(20000, 0) };
                case "Jazz": return new[] { P(25, 3), P(150, 2), P(600, -0.5), P(2000, 0), P(6000, 2), P(20000, 3) };
                case "Classical": return new[] { P(25, 3), P(150, 1.5), P(800, -0.5), P(3000, -0.5), P(8000, 2), P(20000, 3.5) };
                case "Electronic": return new[] { P(25, 5.5), P(70, 4.5), P(250, 1), P(800, -1.5), P(2500, 0.5), P(8000, 3.5), P(20000, 4.5) };
                case "Loudness": return new[] { P(25, 6), P(100, 3.5), P(400, 0), P(2000, 0), P(8000, 3), P(20000, 5) };
                case "Acoustic": return new[] { P(25, 3), P(150, 2.5), P(500, 1), P(1500, 1.5), P(4000, 2.5), P(10000, 2), P(20000, 1) };
                default: return new[] { P(25, 0), P(20000, 0) };
            }
        }

        static double[] P(double f, double db) { return new[] { f, db }; }

        public static float[] PresetGains(string name)
        {
            double[][] pts = PresetPoints(name);
            var g = new float[Bands];
            for (int i = 0; i < Bands; i++)
            {
                double lf = Math.Log(Frequencies[i]);
                double v = pts[pts.Length - 1][1];
                for (int k = 0; k < pts.Length - 1; k++)
                {
                    double a = Math.Log(pts[k][0]), b = Math.Log(pts[k + 1][0]);
                    if (lf <= a) { v = pts[k][1]; break; }
                    if (lf <= b) { double t = (lf - a) / (b - a); v = pts[k][1] + (pts[k + 1][1] - pts[k][1]) * t; break; }
                }
                g[i] = (float)Math.Round(v, 1);
            }
            return g;
        }

        /// <summary>Preamp that keeps the loudest boost from clipping (used when a preset is picked).</summary>
        public static float SuggestedPreamp(float[] g)
        {
            float max = 0;
            foreach (float v in g) max = Math.Max(max, v);
            return -(float)Math.Round(max * 0.85, 1);
        }

        // ------------------------------------------------------------------ response curve (for the UI)

        /// <summary>Combined magnitude response in dB of designed filters (null = band off) plus preamp.</summary>
        public static double ResponseDb(Biquad?[] filters, float pre, double freq, double rate)
        {
            double sum = pre;
            for (int i = 0; i < filters.Length; i++) if (filters[i].HasValue) sum += filters[i].Value.MagnitudeDb(freq, rate);
            return sum;
        }

        // ------------------------------------------------------------------ matched design
        //
        // Neighbouring bands overlap (that's what makes the curve smooth), so with plain filters the level at a band's
        // centre is its own gain plus a share of its neighbours'. The design step corrects for that: it solves for the
        // filter gains that make the response at every centre frequency equal the slider values (iterative refinement
        // on the band-interaction matrix), so what the sliders show is what you get.

        static double[,] interactionInverse;

        static double[,] InteractionInverse()
        {
            if (interactionInverse != null) return interactionInverse;
            const double unit = 6, fs = 48000;
            var m = new double[Bands, Bands];
            for (int j = 0; j < Bands; j++)
            {
                Biquad b = Biquad.Peaking(fs, Frequencies[j], BandQ, unit);
                for (int i = 0; i < Bands; i++) m[i, j] = b.MagnitudeDb(Frequencies[i], fs) / unit;
            }
            interactionInverse = Invert(m);
            return interactionInverse;
        }

        static double[,] Invert(double[,] a)
        {
            int n = a.GetLength(0);
            var m = new double[n, 2 * n];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n + i] = 1; }
            for (int c = 0; c < n; c++)
            {
                int p = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
                if (p != c) for (int k = 0; k < 2 * n; k++) { double t = m[c, k]; m[c, k] = m[p, k]; m[p, k] = t; }
                double d = m[c, c];
                for (int k = 0; k < 2 * n; k++) m[c, k] /= d;
                for (int r = 0; r < n; r++)
                {
                    if (r == c || m[r, c] == 0) continue;
                    double f = m[r, c];
                    for (int k = 0; k < 2 * n; k++) m[r, k] -= f * m[c, k];
                }
            }
            var inv = new double[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
            return inv;
        }

        /// <summary>Filters for slider gains <paramref name="g"/> at sample rate <paramref name="rate"/>; null entries are inactive bands.</summary>
        public static Biquad?[] Design(float[] g, double rate)
        {
            var result = new Biquad?[Bands];
            bool any = false;
            foreach (float v in g) if (Math.Abs(v) > 0.01f) any = true;
            if (!any) return result;

            double[,] inv = InteractionInverse();
            const double ref_fs = 48000, limit = MaxDb * 1.75;
            var x = new double[Bands];
            for (int i = 0; i < Bands; i++) x[i] = g[i];
            var err = new double[Bands];
            for (int iter = 0; iter < 8; iter++)
            {
                double worst = 0;
                for (int i = 0; i < Bands; i++)
                {
                    double r = 0;
                    for (int j = 0; j < Bands; j++)
                        if (Math.Abs(x[j]) > 1e-4) r += Biquad.Peaking(ref_fs, Frequencies[j], BandQ, x[j]).MagnitudeDb(Frequencies[i], ref_fs);
                    err[i] = g[i] - r;
                    worst = Math.Max(worst, Math.Abs(err[i]));
                }
                if (worst < 0.02) break;
                for (int i = 0; i < Bands; i++)
                {
                    double dx = 0;
                    for (int j = 0; j < Bands; j++) dx += inv[i, j] * err[j];
                    x[i] = Math.Max(-limit, Math.Min(limit, x[i] + dx));   // impossible zig-zags are clamped, not chased
                }
            }
            for (int i = 0; i < Bands; i++)
                if (Math.Abs(x[i]) > 0.01 && Frequencies[i] < rate * 0.48) result[i] = Biquad.Peaking(rate, Frequencies[i], BandQ, x[i]);
            return result;
        }
    }

    /// <summary>RBJ "Audio EQ Cookbook" peaking biquad (transposed direct form II, per-channel state).</summary>
    internal struct Biquad
    {
        public double B0, B1, B2, A1, A2;

        public static Biquad Peaking(double fs, double f0, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40), w0 = 2 * Math.PI * Math.Min(f0, fs * 0.49) / fs;
            double alpha = Math.Sin(w0) / (2 * q), cos = Math.Cos(w0);
            double a0 = 1 + alpha / a;
            return new Biquad
            {
                B0 = (1 + alpha * a) / a0, B1 = -2 * cos / a0, B2 = (1 - alpha * a) / a0,
                A1 = -2 * cos / a0, A2 = (1 - alpha / a) / a0
            };
        }

        public double MagnitudeDb(double f, double fs)
        {
            double w = 2 * Math.PI * f / fs;
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = B0 + B1 * c1 + B2 * c2, ni = -(B1 * s1 + B2 * s2);
            double dr = 1 + A1 * c1 + A2 * c2, di = -(A1 * s1 + A2 * s2);
            return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
        }
    }

    /// <summary>Audio-thread side: applies the current settings to interleaved 32-bit float PCM, in place.</summary>
    internal sealed class EqProcessor
    {
        readonly EqualizerSettings settings;
        int builtVersion = -1, builtRate, builtChannels;
        Biquad[] filters = new Biquad[0];
        int[] activeBands = new int[0];
        double[] z1 = new double[0], z2 = new double[0];   // [channel * Bands + band]
        double preGain = 1;
        bool active;
        float[] scratch = new float[0];

        public EqProcessor(EqualizerSettings settings) { this.settings = settings; }

        void Rebuild(int rate, int channels)
        {
            Biquad?[] designed = EqualizerSettings.Design(settings.Snapshot(), rate);
            var list = new List<int>();
            filters = new Biquad[EqualizerSettings.Bands];
            for (int i = 0; i < EqualizerSettings.Bands; i++)
                if (designed[i].HasValue) { filters[i] = designed[i].Value; list.Add(i); }
            activeBands = list.ToArray();
            if (channels != builtChannels || rate != builtRate)
            {
                z1 = new double[channels * EqualizerSettings.Bands];
                z2 = new double[channels * EqualizerSettings.Bands];
            }
            preGain = Math.Pow(10, settings.Preamp / 20.0);
            active = settings.IsActive;
            builtVersion = settings.Version; builtRate = rate; builtChannels = channels;
        }

        /// <summary>Processes <paramref name="frames"/> frames of interleaved float samples stored as bytes in <paramref name="buf"/>.</summary>
        public void Process(byte[] buf, int frames, int channels, int rate)
        {
            if (settings.Version != builtVersion || rate != builtRate || channels != builtChannels) Rebuild(rate, channels);
            if (!active || frames <= 0) return;
            int n = frames * channels;
            if (scratch.Length < n) scratch = new float[n];
            Buffer.BlockCopy(buf, 0, scratch, 0, n * 4);
            int nb = activeBands.Length;
            for (int ch = 0; ch < channels; ch++)
            {
                int zb = ch * EqualizerSettings.Bands;
                for (int s = ch; s < n; s += channels)
                {
                    double x = scratch[s] * preGain;
                    for (int k = 0; k < nb; k++)
                    {
                        int b = activeBands[k];
                        Biquad f = filters[b];
                        double y = f.B0 * x + z1[zb + b];
                        z1[zb + b] = f.B1 * x - f.A1 * y + z2[zb + b];
                        z2[zb + b] = f.B2 * x - f.A2 * y;
                        x = y;
                    }
                    // Gentle safety: soft-limit only what would clip (the preamp should normally prevent this).
                    if (x > 0.98) x = 0.98 + 0.02 * Math.Tanh((x - 0.98) / 0.02);
                    else if (x < -0.98) x = -0.98 + 0.02 * Math.Tanh((x + 0.98) / 0.02);
                    scratch[s] = (float)x;
                }
            }
            Buffer.BlockCopy(scratch, 0, buf, 0, n * 4);
        }

        /// <summary>Clears the filter memory (after a seek or track change) so old audio doesn't ring into new.</summary>
        public void Reset()
        {
            Array.Clear(z1, 0, z1.Length);
            Array.Clear(z2, 0, z2.Length);
        }
    }
}

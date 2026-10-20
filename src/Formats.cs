using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace BitPerfectPlayer
{
    /// <summary>Sample format of the raw PCM stream produced by the decoder.</summary>
    internal enum PcmKind { S16, S32, F32 }

    /// <summary>A device (WASAPI) PCM format: container size, valid bits, int/float, rate and channels.</summary>
    internal sealed class PcmFormat
    {
        public int SampleRate;
        public int Channels;
        public int ContainerBits;
        public int ValidBits;
        public bool IsFloat;

        static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
        static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");

        public int BytesPerSample { get { return ContainerBits / 8; } }
        public int BlockAlign { get { return Channels * BytesPerSample; } }

        public static int ChannelMask(int channels)
        {
            switch (channels)
            {
                case 1: return 0x4;     // FC
                case 2: return 0x3;     // FL FR
                case 3: return 0x7;
                case 4: return 0x33;    // FL FR BL BR
                case 5: return 0x37;
                case 6: return 0x3F;    // 5.1
                case 8: return 0x63F;   // 7.1
                default: return 0;
            }
        }

        /// <summary>Allocates a WAVEFORMATEXTENSIBLE with Marshal.AllocHGlobal. Caller frees with Marshal.FreeHGlobal.</summary>
        public IntPtr AllocWaveFormat()
        {
            IntPtr p = Marshal.AllocHGlobal(40);
            int bps = ContainerBits / 8;
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));             // WAVE_FORMAT_EXTENSIBLE
            Marshal.WriteInt16(p, 2, (short)Channels);
            Marshal.WriteInt32(p, 4, SampleRate);
            Marshal.WriteInt32(p, 8, SampleRate * Channels * bps);
            Marshal.WriteInt16(p, 12, (short)(Channels * bps));
            Marshal.WriteInt16(p, 14, (short)ContainerBits);
            Marshal.WriteInt16(p, 16, 22);                                  // cbSize
            Marshal.WriteInt16(p, 18, (short)ValidBits);
            Marshal.WriteInt32(p, 20, ChannelMask(Channels));
            byte[] g = (IsFloat ? SubtypeFloat : SubtypePcm).ToByteArray();
            Marshal.Copy(g, 0, p + 24, 16);
            return p;
        }

        public static PcmFormat FromWaveFormat(IntPtr p)
        {
            var f = new PcmFormat();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = Marshal.ReadInt16(p, 2);
            f.SampleRate = Marshal.ReadInt32(p, 4);
            f.ContainerBits = Marshal.ReadInt16(p, 14);
            f.ValidBits = f.ContainerBits;
            f.IsFloat = tag == 3;
            if (tag == 0xFFFE && Marshal.ReadInt16(p, 16) >= 22)
            {
                int valid = Marshal.ReadInt16(p, 18);
                if (valid > 0) f.ValidBits = valid;
                byte[] g = new byte[16];
                Marshal.Copy(p + 24, g, 0, 16);
                f.IsFloat = new Guid(g) == SubtypeFloat;
            }
            return f;
        }

        public static string FormatRate(int rate)
        {
            return (rate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";
        }

        public static string FormatChannels(int ch)
        {
            return ch == 1 ? "mono" : ch == 2 ? "stereo" : ch + " ch";
        }

        public string DescribeSample()
        {
            if (IsFloat) return "32-bit float";
            return ValidBits == ContainerBits ? ValidBits + "-bit" : ValidBits + "-bit in " + ContainerBits;
        }

        public string Describe()
        {
            return DescribeSample() + " / " + FormatRate(SampleRate) + " / " + FormatChannels(Channels);
        }
    }

    /// <summary>
    /// Builds the ordered list of exclusive-mode formats to try for a track — the Windows equivalent of
    /// UAPP choosing a USB alternate setting + clock rate that matches the file.
    /// </summary>
    internal static class FormatNegotiator
    {
        static readonly int[] StandardRates = { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000 };

        // {container, valid, isFloat}
        static readonly int[] I16 = { 16, 16, 0 };
        static readonly int[] I24 = { 24, 24, 0 };
        static readonly int[] I32v24 = { 32, 24, 0 };
        static readonly int[] I32 = { 32, 32, 0 };
        static readonly int[] F32 = { 32, 32, 1 };

        public static List<PcmFormat> Candidates(TrackInfo t)
        {
            var channels = new List<int> { t.Channels };
            if (t.Channels == 1 || t.Channels > 2) channels.Add(2);
            var rates = RateOrder(t.SampleRate);
            var containers = ContainerOrder(t);

            var result = new List<PcmFormat>();
            foreach (int ch in channels)
                foreach (int rate in rates)
                    foreach (int[] c in containers)
                        result.Add(new PcmFormat { SampleRate = rate, Channels = ch, ContainerBits = c[0], ValidBits = c[1], IsFloat = c[2] == 1 });
            return result;
        }

        static List<int[]> ContainerOrder(TrackInfo t)
        {
            if (t.IsFloat) return new List<int[]> { F32, I32, I32v24, I24, I16 };
            int bits = t.BitDepth > 0 ? t.BitDepth : 32;
            if (bits <= 16) return new List<int[]> { I16, I24, I32v24, I32, F32 };
            if (bits <= 24) return new List<int[]> { I24, I32v24, I32, F32, I16 };
            return new List<int[]> { I32, F32, I32v24, I24, I16 };
        }

        static List<int> RateOrder(int source)
        {
            var list = new List<int> { source };
            bool family441 = source % 11025 == 0;
            foreach (int r in StandardRates) if (r > source && (r % 11025 == 0) == family441) list.Add(r);
            foreach (int r in StandardRates) if (r > source && (r % 11025 == 0) != family441) list.Add(r);
            for (int i = StandardRates.Length - 1; i >= 0; i--)
            {
                int r = StandardRates[i];
                if (r < source && (r % 11025 == 0) == family441) list.Add(r);
            }
            for (int i = StandardRates.Length - 1; i >= 0; i--)
            {
                int r = StandardRates[i];
                if (r < source && (r % 11025 == 0) != family441) list.Add(r);
            }
            return list;
        }
    }

    /// <summary>
    /// Converts decoder PCM to the device format. When the source fits the device container and gain is 1.0
    /// it takes a pure integer path (MSB-aligned zero padding only), i.e. the output is bit-perfect.
    /// </summary>
    internal sealed class SampleConverter
    {
        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits { [FieldOffset(0)] public float F; [FieldOffset(0)] public int I; }

        uint rng = 0x9E3779B9u;

        float Tpdf()
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            float a = (rng & 0xFFFFFF) * (1f / 16777216f);
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            float b = (rng & 0xFFFFFF) * (1f / 16777216f);
            return a - b;   // triangular, -1..1 LSB
        }

        /// <summary>True when converting this decoder output to <paramref name="fmt"/> is lossless at unity gain.</summary>
        public static bool IsLosslessPath(PcmKind kind, int sourceBits, PcmFormat fmt)
        {
            if (kind == PcmKind.F32) return fmt.IsFloat;
            int bits = kind == PcmKind.S16 ? 16 : sourceBits;
            return fmt.IsFloat ? bits <= 24 : bits <= fmt.ValidBits;
        }

        public void Convert(byte[] src, int srcOffset, PcmKind kind, int sourceBits, int inChannels, int frames,
                            byte[] dst, int dstOffset, PcmFormat fmt, float gain)
        {
            int outCh = fmt.Channels;
            bool duplicate = inChannels == 1 && outCh == 2;
            if (!duplicate && inChannels != outCh)
                throw new InvalidOperationException("Channel count mismatch between decoder and device.");

            int outBps = fmt.BytesPerSample;
            int effectiveBits = kind == PcmKind.S16 ? 16 : sourceBits;
            bool intPath = kind != PcmKind.F32 && gain == 1f && !fmt.IsFloat && effectiveBits <= fmt.ValidBits;
            int reps = duplicate ? 2 : 1;
            int s = srcOffset, d = dstOffset;
            int samples = frames * inChannels;
            FloatBits fb = new FloatBits();

            for (int i = 0; i < samples; i++)
            {
                int vi = 0;
                float vf = 0f;
                if (kind == PcmKind.S16)
                {
                    vi = ((short)(src[s] | (src[s + 1] << 8))) << 16;
                    s += 2;
                }
                else if (kind == PcmKind.S32)
                {
                    vi = src[s] | (src[s + 1] << 8) | (src[s + 2] << 16) | (src[s + 3] << 24);
                    s += 4;
                }
                else
                {
                    fb.I = src[s] | (src[s + 1] << 8) | (src[s + 2] << 16) | (src[s + 3] << 24);
                    vf = fb.F;
                    s += 4;
                }

                int outVal;    // MSB-aligned 32-bit integer, or float bits when fmt.IsFloat
                if (intPath)
                {
                    outVal = vi;  // source fits in ValidBits: low bits are already zero
                }
                else
                {
                    float x = kind == PcmKind.F32 ? vf : vi * (1f / 2147483648f);
                    x *= gain;
                    if (fmt.IsFloat)
                    {
                        fb.F = x;
                        outVal = fb.I;
                    }
                    else
                    {
                        outVal = FloatToInt(x, fmt.ValidBits);
                    }
                }

                for (int r = 0; r < reps; r++)
                {
                    if (fmt.IsFloat || outBps == 4)
                    {
                        dst[d] = (byte)outVal; dst[d + 1] = (byte)(outVal >> 8);
                        dst[d + 2] = (byte)(outVal >> 16); dst[d + 3] = (byte)(outVal >> 24);
                    }
                    else if (outBps == 3)
                    {
                        dst[d] = (byte)(outVal >> 8); dst[d + 1] = (byte)(outVal >> 16); dst[d + 2] = (byte)(outVal >> 24);
                    }
                    else
                    {
                        dst[d] = (byte)(outVal >> 16); dst[d + 1] = (byte)(outVal >> 24);
                    }
                    d += outBps;
                }
            }
        }

        int FloatToInt(float x, int validBits)
        {
            double scale = (double)(1L << (validBits - 1));
            double v = x * scale;
            if (validBits <= 24) v += Tpdf();
            long iv = (long)Math.Floor(v + 0.5);
            long max = (long)scale - 1, min = -(long)scale;
            if (iv > max) iv = max; else if (iv < min) iv = min;
            return (int)(iv << (32 - validBits));
        }
    }
}

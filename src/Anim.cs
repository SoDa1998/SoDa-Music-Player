using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BitPerfectPlayer
{
    /// <summary>Something that animates: Step is called once per display frame with the elapsed time; return false when finished.</summary>
    internal interface IAnimated
    {
        bool Step(double dt);
    }

    /// <summary>
    /// Frame clock synced to the display. A background thread waits on DwmFlush (which returns at the compositor's next
    /// vertical blank — 60, 120, 144 Hz…) and posts one tick at a time to the UI thread while anything is animating.
    /// Animations are time-based (dt), so they run at the same speed on any refresh rate.
    /// </summary>
    internal static class AnimationClock
    {
        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        static readonly List<IAnimated> active = new List<IAnimated>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static Control ui;
        static volatile int activeCount;
        static volatile bool pending;
        static long last;

        public static void Init(Control uiHost)
        {
            if (ui != null) return;
            ui = uiHost;
            new Thread(Loop) { IsBackground = true, Name = "frame clock", Priority = ThreadPriority.AboveNormal }.Start();
        }

        /// <summary>UI thread only.</summary>
        public static void Request(IAnimated a)
        {
            if (ui == null || a == null || active.Contains(a)) return;
            if (active.Count == 0) last = Stopwatch.GetTimestamp();
            active.Add(a);
            activeCount = active.Count;
            wake.Set();
        }

        static void Loop()
        {
            while (true)
            {
                if (activeCount == 0) { wake.WaitOne(); continue; }
                if (DwmFlush() != 0) Thread.Sleep(7);   // no compositor: ~144 Hz fallback
                if (pending) continue;                    // the UI is still busy with the previous frame
                pending = true;
                try { ui.BeginInvoke((Action)Tick); }
                catch { pending = false; Thread.Sleep(100); }
            }
        }

        static void Tick()
        {
            pending = false;
            long now = Stopwatch.GetTimestamp();
            double dt = Math.Min(0.05, (now - last) / (double)Stopwatch.Frequency);
            last = now;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                bool keep;
                try { keep = active[i].Step(dt); } catch { keep = false; }
                if (!keep) active.RemoveAt(i);
            }
            activeCount = active.Count;
        }
    }

    /// <summary>Per-key 0→1 values that ease toward a target (hover fades). Keys are buttons, rows, tiles…</summary>
    internal sealed class Fader
    {
        sealed class V { public float Cur, Target; }
        readonly Dictionary<object, V> map = new Dictionary<object, V>();
        public float InTime = 0.12f, OutTime = 0.22f;

        /// <summary>Eased current value (0 when unknown).</summary>
        public float Get(object key)
        {
            V v;
            if (key == null || !map.TryGetValue(key, out v)) return 0f;
            float t = v.Cur;
            return t * t * (3 - 2 * t);
        }

        /// <summary>Returns true if the target changed (start animating).</summary>
        public bool Set(object key, float target)
        {
            if (key == null) return false;
            V v;
            if (!map.TryGetValue(key, out v))
            {
                if (target <= 0) return false;
                map[key] = v = new V();
            }
            if (v.Target == target) return false;
            v.Target = target;
            return true;
        }

        public void Clear() { map.Clear(); }

        /// <summary>Advances all values; returns true if anything moved this frame. Keys that moved are added to <paramref name="movedKeys"/>.</summary>
        public bool Step(double dt, List<object> movedKeys)
        {
            if (map.Count == 0) return false;
            bool moved = false;
            List<object> dead = null;
            foreach (var kv in map)
            {
                V v = kv.Value;
                bool m = false;
                if (v.Cur < v.Target) { v.Cur = Math.Min(v.Target, v.Cur + (float)(dt / InTime)); m = true; }
                else if (v.Cur > v.Target) { v.Cur = Math.Max(v.Target, v.Cur - (float)(dt / OutTime)); m = true; }
                if (m) { moved = true; if (movedKeys != null) movedKeys.Add(kv.Key); }
                if (v.Cur <= 0 && v.Target <= 0) { if (dead == null) dead = new List<object>(); dead.Add(kv.Key); }
            }
            if (dead != null) foreach (object k in dead) map.Remove(k);
            return moved;
        }
    }
}

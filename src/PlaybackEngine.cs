using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BitPerfectPlayer
{
    /// <summary>The stream died (driver stall or device reset); the run reopens the device at <see cref="Position"/>.</summary>
    internal sealed class StreamLostException : Exception
    {
        public readonly TrackInfo Track;
        public readonly double Position;
        public StreamLostException(string message, TrackInfo track, double position) : base(message) { Track = track; Position = position; }
    }

    /// <summary>An initialised WASAPI stream on one device.</summary>
    internal sealed class OutputSession : IDisposable
    {
        public IMMDevice Device;
        public IAudioClient Client;
        public IAudioRenderClient Render;
        public PcmFormat Format;
        public bool Exclusive;
        /// <summary>The stream carries native DSD as DoP frames.</summary>
        public bool Dop;
        public uint BufferFrames;
        public double PeriodMs;
        public AutoResetEvent Event;

        public void Dispose()
        {
            try { if (Client != null) Client.Stop(); } catch { }
            Wasapi.Release(Render); Render = null;
            Wasapi.Release(Client); Client = null;
            Wasapi.Release(Device); Device = null;
            if (Event != null) { Event.Close(); Event = null; }
        }
    }

    /// <summary>
    /// Public player API. Each Play() starts a <see cref="PlaybackRun"/> on its own MMCSS "Pro Audio" thread.
    /// Events are raised on the audio thread — UI code must marshal with BeginInvoke (never Invoke).
    /// </summary>
    internal sealed class PlaybackEngine
    {
        public event Action<TrackInfo> TrackChanged;
        public event Action<string> ErrorOccurred;
        public event Action PlaybackFinished;
        /// <summary>Non-fatal information (e.g. the device is being reopened after a stall).</summary>
        public event Action<string> Notice;
        /// <summary>Called on the audio thread: given the current track, return the next one (or null). Must be thread-safe.</summary>
        public Func<TrackInfo, TrackInfo> NextTrackProvider;

        public string DeviceId;
        public bool ExclusiveMode = true;
        public int BufferMs = 20;
        /// <summary>Send DSD files natively as DoP (Exclusive mode only); otherwise FFmpeg converts them to PCM.</summary>
        public volatile bool NativeDsd;

        volatile float softwareGain = 1f;
        public float SoftwareGain { get { return softwareGain; } set { softwareGain = value; } }

        volatile float fadeGain = 1f;
        /// <summary>Temporary extra attenuation (the sleep timer's fade-out); 1 = none. Multiplies SoftwareGain.</summary>
        public float FadeGain { get { return fadeGain; } set { fadeGain = value; } }

        /// <summary>20-band EQ. Applied only to Shared-mode streams; an Exclusive stream never passes through it.</summary>
        public readonly EqualizerSettings Equalizer = new EqualizerSettings();

        /// <summary>Read-only copy of the decoded audio for the spectrum visualizer.</summary>
        public readonly SpectrumTap Tap = new SpectrumTap();

        readonly object sync = new object();
        PlaybackRun run;
        Thread thread;

        // State published by the active run.
        internal volatile TrackInfo currentTrack;
        internal long positionTicks;
        internal volatile string outputText = "";
        internal volatile string processingText = "";
        internal volatile string decoderText = "";
        internal volatile string[] processingNotes = new string[0];
        internal volatile bool pathLossless;
        internal volatile bool dopActive;
        internal volatile bool exclusiveActive;
        internal volatile bool running;

        public TrackInfo CurrentTrack { get { return currentTrack; } }
        public bool IsRunning { get { return running; } }
        public bool IsPaused { get { PlaybackRun r = run; return r != null && r.Paused; } }
        public double Position { get { return Interlocked.Read(ref positionTicks) / 1e7; } }
        public string OutputDescription { get { return outputText; } }
        public string ProcessingDescription { get { return processingText; } }
        /// <summary>How the file is decoded, e.g. "FFmpeg → 24-bit integer PCM".</summary>
        public string DecoderDescription { get { return decoderText; } }
        /// <summary>Each change made between the decoder and the device (empty = none).</summary>
        public string[] ProcessingNotes { get { return processingNotes; } }
        public bool IsExclusiveActive { get { return running && exclusiveActive; } }
        // DoP is never scaled (software volume can't apply to DSD bits), so it stays bit-perfect at any volume setting.
        public bool IsBitPerfect { get { return running && pathLossless && (softwareGain == 1f || dopActive); } }
        /// <summary>Native DSD (DoP) is playing.</summary>
        public bool IsDop { get { return running && dopActive; } }

        public void Play(TrackInfo track, double startSeconds)
        {
            Stop();
            var r = new PlaybackRun(this, track, startSeconds, DeviceId, ExclusiveMode, BufferMs);
            var th = new Thread(r.ThreadMain) { IsBackground = true, Name = "Audio render", Priority = ThreadPriority.Highest };
            th.SetApartmentState(ApartmentState.MTA);
            lock (sync) { run = r; thread = th; }
            running = true;
            currentTrack = track;
            Interlocked.Exchange(ref positionTicks, (long)(startSeconds * 1e7));
            th.Start();
        }

        public void Pause() { PlaybackRun r = run; if (r != null) r.SetPaused(true); }
        public void Resume() { PlaybackRun r = run; if (r != null) r.SetPaused(false); }

        public void Seek(double seconds)
        {
            PlaybackRun r = run;
            if (r == null) return;
            r.RequestSeek(Math.Max(0, seconds));
            Interlocked.Exchange(ref positionTicks, (long)(Math.Max(0, seconds) * 1e7));
        }

        public void Stop()
        {
            PlaybackRun r; Thread th;
            lock (sync) { r = run; th = thread; run = null; thread = null; }
            if (r == null) return;
            r.RequestStop();
            if (th != null) th.Join(5000);
            running = false;
            currentTrack = null;
            outputText = processingText = decoderText = "";
            processingNotes = new string[0];
        }

        internal bool IsActive(PlaybackRun r) { return run == r; }

        internal void RaiseTrackChanged(PlaybackRun r, TrackInfo t)
        {
            if (!IsActive(r)) return;
            currentTrack = t;
            var h = TrackChanged; if (h != null) h(t);
        }

        internal void RaiseNotice(PlaybackRun r, string msg)
        {
            if (!IsActive(r)) return;
            var h = Notice; if (h != null) h(msg);
        }

        internal void RaiseError(PlaybackRun r, string msg)
        {
            if (!IsActive(r)) return;
            var h = ErrorOccurred; if (h != null) h(msg);
        }

        internal void RaiseFinished(PlaybackRun r)
        {
            if (!IsActive(r)) return;
            running = false;
            var h = PlaybackFinished; if (h != null) h();
        }
    }

    /// <summary>One playback "run": owns the WASAPI session(s) and decoders. All members except the Request*/SetPaused methods are audio-thread only.</summary>
    internal sealed class PlaybackRun
    {
        readonly PlaybackEngine engine;
        readonly string deviceId;
        readonly bool exclusive;
        readonly int bufferMs;
        TrackInfo firstTrack;
        double firstStart;

        volatile bool stopRequested;
        volatile bool paused;
        readonly ManualResetEvent resumeSignal = new ManualResetEvent(true);
        readonly object seekLock = new object();
        double pendingSeek = -1;

        OutputSession session;
        IAudioDecoder curDec, nextDec;
        TrackInfo reinitTrack;
        bool nextResolved;
        readonly SampleConverter converter = new SampleConverter();
        byte[] inBuf, outBuf;
        EqProcessor eq;          // Shared mode only
        bool eqShown;            // EQ state reflected in the published processing text
        bool dopNextB;           // DoP marker for the next frame written (0x05 / 0xFA alternate without a break)
        string dopFallback;      // why a DSD track was converted to PCM although native DSD is on

        bool WantsDop(TrackInfo t, out int dsdRate, out int channels)
        {
            dsdRate = channels = 0;
            return engine.NativeDsd && t.IsDsd && DsdReader.TryInfo(t.FilePath, out dsdRate, out channels);
        }

        static IEnumerable<PcmFormat> DopCandidates(int dsdRate, int channels)
        {
            int rate = dsdRate / 16;
            yield return new PcmFormat { SampleRate = rate, Channels = channels, ContainerBits = 32, ValidBits = 24, IsFloat = false };
            yield return new PcmFormat { SampleRate = rate, Channels = channels, ContainerBits = 24, ValidBits = 24, IsFloat = false };
        }

        public PlaybackRun(PlaybackEngine engine, TrackInfo track, double start, string deviceId, bool exclusive, int bufferMs)
        {
            this.engine = engine;
            firstTrack = track;
            firstStart = start;
            this.deviceId = deviceId;
            this.exclusive = exclusive;
            this.bufferMs = bufferMs;
        }

        public bool Paused { get { return paused; } }
        public void RequestStop() { stopRequested = true; resumeSignal.Set(); }
        public void SetPaused(bool p) { paused = p; if (p) resumeSignal.Reset(); else resumeSignal.Set(); }
        public void RequestSeek(double s) { lock (seekLock) { pendingSeek = s; seekPending = true; } }

        /// <summary>A seek was requested and not yet carried out: the old decoder's position must not be published meanwhile.</summary>
        volatile bool seekPending;

        double TakeSeek()
        {
            lock (seekLock) { double s = pendingSeek; pendingSeek = -1; return s; }
        }

        public void ThreadMain()
        {
            uint taskIndex = 0;
            IntPtr mmcss = IntPtr.Zero;
            try { mmcss = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex); } catch { }
            bool ok = false;
            try
            {
                TrackInfo track = firstTrack;
                double start = firstStart;
                int recoveries = 0;
                var sessionClock = new System.Diagnostics.Stopwatch();
                while (track != null && !stopRequested)
                {
                    sessionClock.Restart();
                    try
                    {
                        track = RunSession(track, start);   // returns a track only when the format changes
                        start = 0;
                    }
                    catch (StreamLostException ex)
                    {
                        // Driver stall or device reset: reopen the device and continue where playback was.
                        if (stopRequested) break;
                        if (sessionClock.Elapsed.TotalSeconds > 20) recoveries = 0;   // an occasional hiccup, not a loop
                        if (++recoveries > 3)
                            throw new AudioException(ex.Message + " Reopening the device failed 3 times — check the cable, " +
                                                     "or try a larger buffer or Shared mode in Settings.", 0);
                        engine.RaiseNotice(this, ex.Message + " Reopening the device…");
                        Thread.Sleep(250 * recoveries);
                        track = ex.Track;
                        start = ex.Position;
                    }
                }
                ok = true;
            }
            catch (Exception ex)
            {
                if (!stopRequested) engine.RaiseError(this, ex.Message);
            }
            finally
            {
                if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            }
            if (!stopRequested)
            {
                if (ok) engine.RaiseFinished(this);
                else if (engine.IsActive(this)) engine.running = false;
            }
        }

        double CurrentPosition() { return Interlocked.Read(ref engine.positionTicks) / 1e7; }

        static void EnsureProbed(TrackInfo t)
        {
            FfmpegTools.Probe(t);
            if (t.ProbeError != null) throw new Exception("Cannot play \"" + Path.GetFileName(t.FilePath) + "\": " + t.ProbeError);
        }

        // ---------------------------------------------------------------------------------------------
        // Session
        // ---------------------------------------------------------------------------------------------

        TrackInfo RunSession(TrackInfo track, double start)
        {
            EnsureProbed(track);
            session = null;
            dopFallback = null;
            int dsdRate, dsdCh;
            if (exclusive && WantsDop(track, out dsdRate, out dsdCh))
            {
                // Native DSD: offer the device only the DoP format. If it refuses, convert to PCM like before.
                try { session = OpenExclusive(track, DopCandidates(dsdRate, dsdCh)); session.Dop = true; }
                catch (AudioException ex)
                {
                    if (ex.Code == Wasapi.E_DEVICE_IN_USE || ex.Code == Wasapi.E_EXCLUSIVE_MODE_NOT_ALLOWED) throw;
                    dopFallback = "device refused native DSD (DoP at " + PcmFormat.FormatRate(dsdRate / 16) + ")";
                }
            }
            if (session == null) session = exclusive ? OpenExclusive(track, FormatNegotiator.Candidates(track)) : OpenShared();
            dopNextB = false;
            reinitTrack = null;
            nextResolved = false;
            // The EQ exists only for Shared streams: in Exclusive mode no DSP object is even created.
            eq = session.Exclusive ? null : new EqProcessor(engine.Equalizer);
            try
            {
                curDec = CreateDecoder(track, start);
                Interlocked.Exchange(ref engine.positionTicks, (long)(start * 1e7));
                Publish();
                engine.RaiseTrackChanged(this, track);

                int maxFrames = (int)session.BufferFrames;
                engine.Tap.DelayFrames = maxFrames;   // what the visualizer sees is one device buffer ahead of what is heard
                inBuf = new byte[maxFrames * session.Format.Channels * 4];
                outBuf = new byte[maxFrames * session.Format.BlockAlign];

                curDec.WaitForData(3000);
                if (stopRequested) return null;
                Render(session.BufferFrames);       // pre-fill before Start, as recommended for event-driven mode
                Wasapi.Check(session.Client.Start(), "IAudioClient.Start");

                long silentFrames = 0;
                while (!stopRequested)
                {
                    // Pause keeps the stream running and feeds digital silence (see Render). Stopping and restarting an
                    // exclusive event-driven stream makes some USB Audio drivers stop signalling, which used to end
                    // playback with "the audio device stopped requesting data" after a pause of a second or more.

                    double seek = TakeSeek();
                    if (seek >= 0)
                    {
                        TrackInfo t = curDec.Track;
                        DisposeDecoders();
                        reinitTrack = null;
                        nextResolved = false;
                        silentFrames = 0;
                        curDec = CreateDecoder(t, seek);
                        if (eq != null) eq.Reset();
                        Publish();
                    }
                    lock (seekLock) seekPending = pendingSeek >= 0;   // done, unless another seek arrived meanwhile

                    if (!session.Event.WaitOne(2000))
                        throw new StreamLostException("The audio device stopped requesting data.", curDec.Track, CurrentPosition());

                    bool wroteAudio;
                    uint frames = session.BufferFrames;
                    try
                    {
                        if (!session.Exclusive)
                        {
                            uint padding;
                            Wasapi.Check(session.Client.GetCurrentPadding(out padding), "GetCurrentPadding");
                            frames -= padding;
                        }
                        if (frames == 0) continue;

                        ResolveNextIfNeeded();
                        wroteAudio = Render(frames);
                    }
                    catch (AudioException ex)
                    {
                        if (ex.Code != Wasapi.E_DEVICE_INVALIDATED) throw;
                        throw new StreamLostException("The audio device was reset or reconfigured.", curDec.Track, CurrentPosition());
                    }

                    if (paused) { silentFrames = 0; continue; }
                    if (!wroteAudio && curDec.IsDrained && nextDec == null)
                    {
                        // Everything has been submitted; wait until the device has played out what is queued.
                        silentFrames += frames;
                        if (silentFrames >= 2L * session.BufferFrames) break;
                    }
                    else silentFrames = 0;
                }
                return stopRequested ? null : reinitTrack;
            }
            finally
            {
                DisposeDecoders();
                if (session != null) { session.Dispose(); session = null; }
            }
        }

        void DisposeDecoders()
        {
            if (curDec != null) { curDec.Dispose(); curDec = null; }
            if (nextDec != null) { nextDec.Dispose(); nextDec = null; }
        }

        /// <summary>Fills the device buffer with <paramref name="frames"/> frames. Returns true if any real audio was written.</summary>
        bool Render(uint frames)
        {
            PcmFormat fmt = session.Format;
            int blk = fmt.BlockAlign;
            int n = (int)frames;
            int filled = 0;
            bool any = false;
            bool dop = session.Dop;
            // DoP words carry DSD bits: any gain, fade or dither would destroy them, so they always go out at unity.
            float gain = dop ? 1f : engine.SoftwareGain * engine.FadeGain;
            if (eq != null && curDec != null && engine.Equalizer.IsActive != eqShown) Publish();   // EQ toggled live

            while (filled < n && curDec != null && !paused)   // paused: write silence, keep the decoder where it is
            {
                int got = curDec.Read(inBuf, n - filled);
                if (got > 0)
                {
                    if (dop) dopNextB = DopDecoder.StampMarkers(inBuf, 0, got, curDec.Channels, dopNextB);
                    else
                    {
                        // Shared mode decodes to 32-bit float, so the EQ runs on float samples before the gain/format conversion.
                        if (eq != null && curDec.Kind == PcmKind.F32) eq.Process(inBuf, got, curDec.Channels, curDec.SampleRate);
                        engine.Tap.Push(inBuf, got, curDec.Channels, curDec.Kind, curDec.SampleRate);   // visualizer: reads only
                    }
                    converter.Convert(inBuf, 0, curDec.Kind, curDec.SourceBits, curDec.Channels, got, outBuf, filled * blk, fmt, gain);
                    filled += got;
                    any = true;
                    continue;
                }
                if (curDec.IsDrained)
                {
                    ReportDecodeFailure(curDec);
                    if (nextDec != null)
                    {
                        // Gapless hand-over inside the same exclusive stream.
                        curDec.Dispose();
                        curDec = nextDec;
                        nextDec = null;
                        nextResolved = false;
                        Publish();
                        engine.RaiseTrackChanged(this, curDec.Track);
                        continue;
                    }
                }
                break;  // underrun (decoder not ready yet) or end of stream
            }
            if (filled < n)
            {
                if (dop)
                {
                    // DSD silence with continuing markers: the DAC stays in DSD mode (no click, no relock) while paused or waiting.
                    int rest = n - filled;
                    DopDecoder.Silence(inBuf, 0, rest, fmt.Channels);
                    dopNextB = DopDecoder.StampMarkers(inBuf, 0, rest, fmt.Channels, dopNextB);
                    converter.Convert(inBuf, 0, PcmKind.S32, 24, fmt.Channels, rest, outBuf, filled * blk, fmt, 1f);
                }
                else Array.Clear(outBuf, filled * blk, (n - filled) * blk);
            }

            IntPtr p;
            Wasapi.Check(session.Render.GetBuffer(frames, out p), "IAudioRenderClient.GetBuffer");
            Marshal.Copy(outBuf, 0, p, n * blk);
            Wasapi.Check(session.Render.ReleaseBuffer(frames, 0), "IAudioRenderClient.ReleaseBuffer");

            // While a seek waits to be carried out, the engine already reports the target: don't overwrite it with the old position.
            if (curDec != null && !seekPending)
            {
                double pos = curDec.StartSeconds + (double)curDec.FramesConsumed / curDec.SampleRate;
                Interlocked.Exchange(ref engine.positionTicks, (long)(pos * 1e7));
            }
            return any;
        }

        void ReportDecodeFailure(IAudioDecoder d)
        {
            if (d.FramesConsumed == 0 && d.ErrorText != null)
                engine.RaiseError(this, "Decode error in \"" + Path.GetFileName(d.Track.FilePath) + "\": " + d.ErrorText);
        }

        /// <summary>Once ffmpeg finishes the current file, ask for the next track and pre-start it (gapless) or schedule a re-init.</summary>
        void ResolveNextIfNeeded()
        {
            if (nextResolved || curDec == null || !curDec.EndOfStream) return;
            nextResolved = true;
            Func<TrackInfo, TrackInfo> provider = engine.NextTrackProvider;
            if (provider == null) return;

            TrackInfo nt = provider(curDec.Track);
            for (int guard = 0; nt != null && guard < 100; guard++)
            {
                FfmpegTools.Probe(nt);
                if (nt.ProbeError == null) break;
                nt = provider(nt);   // skip unreadable files
            }
            if (nt == null || nt.ProbeError != null) return;

            if (CanContinue(nt)) nextDec = CreateDecoder(nt, 0);
            else reinitTrack = nt;
        }

        /// <summary>Can <paramref name="next"/> play through the open stream without changing the device format?</summary>
        bool CanContinue(TrackInfo next)
        {
            PcmFormat f = session.Format;
            if (!session.Exclusive) return true;
            int dsdRate, dsdCh;
            bool nextDop = WantsDop(next, out dsdRate, out dsdCh);
            if (session.Dop || nextDop)   // DoP ↔ PCM always re-opens; DoP → DoP continues when rate and channels match
                return session.Dop && nextDop && dsdRate / 16 == f.SampleRate && dsdCh == f.Channels;
            bool sameRatePath = next.SampleRate == f.SampleRate || (curDec != null && next.SampleRate == curDec.Track.SampleRate);
            if (!sameRatePath) return false;
            bool channelsOk = next.Channels == f.Channels || (next.Channels == 1 && f.Channels == 2) ||
                              (curDec != null && next.Channels == curDec.Track.Channels);
            if (!channelsOk) return false;
            if (next.IsLossy || next.IsDsd) return true;
            return SampleConverter.IsLosslessPath(next.DecodeKind, next.BitDepth > 0 ? next.BitDepth : 32, f);
        }

        IAudioDecoder CreateDecoder(TrackInfo t, double start)
        {
            if (session.Dop) return new DopDecoder(t, start);   // only DSD tracks reach a DoP session (see CanContinue)
            PcmFormat f = session.Format;
            int ch = (t.Channels == f.Channels || (t.Channels == 1 && f.Channels == 2)) ? t.Channels : f.Channels;
            int rate = f.SampleRate;
            PcmKind kind = t.DecodeKind;
            if (!session.Exclusive || rate != t.SampleRate || ch != t.Channels) kind = PcmKind.F32;
            return new FfmpegDecoder(t, start, rate, ch, kind);
        }

        void Publish()
        {
            PcmFormat f = session.Format;
            TrackInfo t = curDec.Track;
            var notes = new List<string>();
            bool lossless = session.Exclusive;

            if (session.Dop)
            {
                var dd = curDec as DopDecoder;
                int dsdRate = dd != null ? dd.DsdRate : f.SampleRate * 16;
                string dsdName = "DSD" + (dsdRate / 44100);
                engine.pathLossless = true;
                engine.exclusiveActive = true;
                engine.dopActive = true;
                engine.outputText = "WASAPI Exclusive  ·  DoP " + f.Describe() + "  ·  " + session.PeriodMs.ToString("0.#") + " ms buffer";
                engine.processingNotes = new string[0];
                engine.processingText = "native " + dsdName + " sent as DoP (no conversion)";
                engine.decoderText = "Native DSD reader → DoP frames (" + dsdName + ", " + (dsdRate / 1e6).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " MHz, 1-bit)";
                if (engine.SoftwareGain != 1f)
                    engine.RaiseNotice(this, "Native DSD is playing: software volume can't change DSD, so use your DAC's volume.");
                return;
            }
            engine.dopActive = false;
            if (dopFallback != null && t.IsDsd) notes.Add(dopFallback);

            if (curDec.SampleRate != t.SampleRate)
            {
                notes.Add("resampled " + PcmFormat.FormatRate(t.SampleRate) + " → " + PcmFormat.FormatRate(curDec.SampleRate) +
                          (FfmpegTools.HasSoxr ? " (SoX VHQ)" : ""));
                lossless = false;
            }
            if (curDec.Channels != t.Channels) { notes.Add(t.Channels + " → " + curDec.Channels + " ch remix"); lossless = false; }
            else if (curDec.Channels == 1 && f.Channels == 2) notes.Add("mono duplicated to L/R");

            if (t.IsDsd) { notes.Add("DSD converted to PCM"); lossless = false; }
            else if (t.IsLossy) { notes.Add("lossy source"); lossless = false; }

            if (!t.IsLossy && !t.IsDsd)
            {
                if (!SampleConverter.IsLosslessPath(curDec.Kind, curDec.SourceBits, f))
                {
                    notes.Add(f.IsFloat ? "converted to float" : "reduced to " + f.ValidBits + "-bit with TPDF dither");
                    lossless = false;
                }
                else if (!t.IsFloat && t.BitDepth > 0 && t.BitDepth < (f.IsFloat ? 24 : f.ValidBits))
                {
                    notes.Add(t.BitDepth + "-bit zero-padded (lossless)");
                }
            }
            eqShown = eq != null && engine.Equalizer.IsActive;
            if (eqShown) { notes.Add("20-band EQ"); lossless = false; }
            if (!session.Exclusive) { notes.Add("Windows mixer may resample / mix"); lossless = false; }

            engine.pathLossless = lossless;
            engine.exclusiveActive = session.Exclusive;
            engine.outputText = (session.Exclusive ? "WASAPI Exclusive" : "WASAPI Shared") + "  ·  " + f.Describe() +
                                "  ·  " + session.PeriodMs.ToString("0.#") + " ms buffer";
            engine.processingText = string.Join("  ·  ", notes.ToArray());
            engine.processingNotes = notes.ToArray();
            engine.decoderText = "FFmpeg → " + (curDec.Kind == PcmKind.F32 ? "32-bit float PCM"
                                 : curDec.Kind == PcmKind.S16 ? "16-bit integer PCM"
                                 : (curDec.SourceBits > 0 && curDec.SourceBits < 32 ? curDec.SourceBits + "-bit integer PCM (in 32-bit words)" : "32-bit integer PCM"));
        }

        // ---------------------------------------------------------------------------------------------
        // Device opening
        // ---------------------------------------------------------------------------------------------

        OutputSession OpenExclusive(TrackInfo track, IEnumerable<PcmFormat> candidates)
        {
            IMMDevice dev = AudioDevices.GetDevice(deviceId);
            IAudioClient client = null;
            bool success = false;
            try
            {
                client = Wasapi.ActivateAudioClient(dev);
                long defPeriod, minPeriod;
                Wasapi.Check(client.GetDevicePeriod(out defPeriod, out minPeriod), "GetDevicePeriod");
                long period = Math.Max(minPeriod, bufferMs * 10000L);
                int lastHr = Wasapi.E_UNSUPPORTED_FORMAT;

                foreach (PcmFormat f in candidates)
                {
                    if (stopRequested) break;
                    IntPtr wfx = f.AllocWaveFormat();
                    try
                    {
                        int hr = client.IsFormatSupported(ShareMode.Exclusive, wfx, IntPtr.Zero);
                        if (hr == Wasapi.E_DEVICE_IN_USE || hr == Wasapi.E_EXCLUSIVE_MODE_NOT_ALLOWED)
                            throw new AudioException("Cannot open the device exclusively: " + Wasapi.Describe(hr), hr);
                        if (hr != Wasapi.S_OK) { lastHr = hr; continue; }

                        long p = period;
                        hr = client.Initialize(ShareMode.Exclusive, Wasapi.STREAMFLAGS_EVENTCALLBACK, p, p, wfx, IntPtr.Zero);
                        if (hr == Wasapi.E_BUFFER_SIZE_NOT_ALIGNED)
                        {
                            // Documented dance: query the aligned size, then re-create the client with the aligned period.
                            uint alignedFrames;
                            client.GetBufferSize(out alignedFrames);
                            p = (long)(10000.0 * 1000 * alignedFrames / f.SampleRate + 0.5);
                            Wasapi.Release(client);
                            client = Wasapi.ActivateAudioClient(dev);
                            hr = client.Initialize(ShareMode.Exclusive, Wasapi.STREAMFLAGS_EVENTCALLBACK, p, p, wfx, IntPtr.Zero);
                        }
                        if ((hr == Wasapi.E_INVALID_DEVICE_PERIOD || hr == Wasapi.E_BUFFER_SIZE_ERROR) && p != defPeriod)
                        {
                            Wasapi.Release(client);
                            client = Wasapi.ActivateAudioClient(dev);
                            p = defPeriod;
                            hr = client.Initialize(ShareMode.Exclusive, Wasapi.STREAMFLAGS_EVENTCALLBACK, p, p, wfx, IntPtr.Zero);
                        }
                        if (hr == Wasapi.S_OK)
                        {
                            OutputSession s = BuildSession(dev, client, f, true);
                            success = true;
                            return s;
                        }
                        lastHr = hr;
                        if (hr == Wasapi.E_DEVICE_IN_USE || hr == Wasapi.E_EXCLUSIVE_MODE_NOT_ALLOWED)
                            throw new AudioException("Cannot open the device exclusively: " + Wasapi.Describe(hr), hr);
                        Wasapi.Release(client);
                        client = Wasapi.ActivateAudioClient(dev);
                    }
                    finally { Marshal.FreeHGlobal(wfx); }
                }
                throw new AudioException("The device accepted none of the exclusive-mode formats for this track (" +
                                         Wasapi.Describe(lastHr) + "). Try Shared mode.", lastHr);
            }
            finally
            {
                if (!success) { Wasapi.Release(client); Wasapi.Release(dev); }
            }
        }

        OutputSession OpenShared()
        {
            IMMDevice dev = AudioDevices.GetDevice(deviceId);
            IAudioClient client = null;
            bool success = false;
            try
            {
                client = Wasapi.ActivateAudioClient(dev);
                IntPtr mix;
                Wasapi.Check(client.GetMixFormat(out mix), "GetMixFormat");
                PcmFormat f;
                try
                {
                    f = PcmFormat.FromWaveFormat(mix);
                    long duration = Math.Max(30, bufferMs * 2) * 10000L;
                    Wasapi.Check(client.Initialize(ShareMode.Shared, Wasapi.STREAMFLAGS_EVENTCALLBACK, duration, 0, mix, IntPtr.Zero),
                                 "IAudioClient.Initialize (shared)");
                }
                finally { Marshal.FreeCoTaskMem(mix); }
                OutputSession s = BuildSession(dev, client, f, false);
                success = true;
                return s;
            }
            finally
            {
                if (!success) { Wasapi.Release(client); Wasapi.Release(dev); }
            }
        }

        static OutputSession BuildSession(IMMDevice dev, IAudioClient client, PcmFormat f, bool exclusive)
        {
            var s = new OutputSession { Device = dev, Client = client, Format = f, Exclusive = exclusive };
            try
            {
                uint frames;
                Wasapi.Check(client.GetBufferSize(out frames), "GetBufferSize");
                s.BufferFrames = frames;
                s.PeriodMs = frames * 1000.0 / f.SampleRate;
                s.Event = new AutoResetEvent(false);
                Wasapi.Check(client.SetEventHandle(s.Event.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                Guid iid = Wasapi.IID_IAudioRenderClient;
                object o;
                Wasapi.Check(client.GetService(ref iid, out o), "GetService(IAudioRenderClient)");
                s.Render = (IAudioRenderClient)o;
                return s;
            }
            catch
            {
                s.Device = null; s.Client = null;   // caller releases these
                if (s.Event != null) s.Event.Close();
                throw;
            }
        }
    }
}

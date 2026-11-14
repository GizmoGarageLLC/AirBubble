using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// Plays 16-bit mono PCM on its own waveOut stream. Unlike SoundPlayer (one shared PlaySound channel per process,
    /// so a pop would wait for a voice to finish and Stop() blocks), every call here is an independent stream that
    /// Windows mixes, and a stop request ends it within ~20 ms.
    /// </summary>
    static class AudioOut
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public short Tag, Channels; public int Rate, BytesPerSec; public short BlockAlign, Bits, Extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WaveFormat f, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        const uint WAVE_MAPPER = 0xFFFFFFFF, WHDR_DONE = 1;
        static readonly int s_FlagsOfs = (int)Marshal.OffsetOf(typeof(WaveHdr), "Flags");

        /// <summary>Plays and blocks until done or until stop() returns true. onStart runs when audio starts.</summary>
        public static void Play(short[] pcm, int rate, Func<bool> stop = null, Action onStart = null)
        {
            if (pcm == null || pcm.Length == 0) return;
            var fmt = new WaveFormat { Tag = 1, Channels = 1, Rate = rate, Bits = 16, BlockAlign = 2, BytesPerSec = rate * 2 };
            IntPtr h;
            int r = waveOutOpen(out h, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new InvalidOperationException("waveOutOpen failed (" + r + ")");
            var data = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            uint size = (uint)Marshal.SizeOf(typeof(WaveHdr));
            IntPtr hdr = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(new WaveHdr { Data = data.AddrOfPinnedObject(), Length = (uint)(pcm.Length * 2) }, hdr, false);
                waveOutPrepareHeader(h, hdr, size);
                if (onStart != null) onStart();
                waveOutWrite(h, hdr, size);
                while (((uint)Marshal.ReadInt32(hdr, s_FlagsOfs) & WHDR_DONE) == 0)
                {
                    if (stop != null && stop()) { waveOutReset(h); break; }
                    Thread.Sleep(15);
                }
                for (int i = 0; i < 50 && ((uint)Marshal.ReadInt32(hdr, s_FlagsOfs) & WHDR_DONE) == 0; i++) Thread.Sleep(5);
                waveOutUnprepareHeader(h, hdr, size);
            }
            finally
            {
                waveOutClose(h);
                Marshal.FreeHGlobal(hdr);
                data.Free();
            }
        }

        /// <summary>Fire and forget (sound effects).</summary>
        public static void PlayAsync(short[] pcm, int rate, SoundSpot spot = null)
        {
            ThreadPool.QueueUserWorkItem(_ => { try { Play(pcm, rate, spot, null, null); } catch { } });
        }

        /// <summary>
        /// Like Play, but in stereo from where 'spot' is around the listener's head. The sound is rendered in 20 ms
        /// blocks just ahead of the playback position, so it follows the spot (and head turns) while it plays.
        /// </summary>
        public static void Play(short[] pcm, int rate, SoundSpot spot, Func<bool> stop, Action onStart)
        {
            if (pcm == null || pcm.Length == 0) return;
            if (spot == null) { Play(pcm, rate, stop, onStart); return; }
            const int NBuf = 3;                              // 60 ms queued: steady playback, quick to follow the head
            int block = Math.Max(64, rate / 50), bytes = block * 4;
            var fmt = new WaveFormat { Tag = 1, Channels = 2, Rate = rate, Bits = 16, BlockAlign = 4, BytesPerSec = rate * 4 };
            IntPtr h;
            int r = waveOutOpen(out h, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { Play(pcm, rate, stop, onStart); return; }   // no stereo stream: plain mono
            int size = Marshal.SizeOf(typeof(WaveHdr)), slot = size + bytes;
            IntPtr mem = Marshal.AllocHGlobal(NBuf * slot);
            var queued = new bool[NBuf];
            try
            {
                for (int i = 0; i < NBuf; i++)
                {
                    IntPtr hdr = mem + i * slot;
                    Marshal.StructureToPtr(new WaveHdr { Data = hdr + size, Length = (uint)bytes }, hdr, false);
                    waveOutPrepareHeader(h, hdr, (uint)size);
                }
                var render = new Spatializer(rate);
                var buf = new short[block * 2];
                int pos = 0, total = pcm.Length + 64;        // + the longest ear delay
                bool started = false;
                while (true)
                {
                    if (stop != null && stop()) { waveOutReset(h); break; }
                    int busy = 0;
                    for (int i = 0; i < NBuf; i++)
                    {
                        IntPtr hdr = mem + i * slot;
                        if (queued[i] && ((uint)Marshal.ReadInt32(hdr, s_FlagsOfs) & WHDR_DONE) != 0) queued[i] = false;
                        if (!queued[i] && pos < total)
                        {
                            render.Render(pcm, pos, block, spot, buf); pos += block;
                            Marshal.Copy(buf, 0, hdr + size, buf.Length);
                            Marshal.WriteInt32(hdr, s_FlagsOfs, Marshal.ReadInt32(hdr, s_FlagsOfs) & ~(int)WHDR_DONE);
                            if (!started) { started = true; if (onStart != null) onStart(); }
                            waveOutWrite(h, hdr, (uint)size); queued[i] = true;
                        }
                        if (queued[i]) busy++;
                    }
                    if (busy == 0) break;
                    Thread.Sleep(5);
                }
                for (int w = 0; w < 50; w++)                  // after a reset: wait for the buffers to come back
                {
                    bool all = true;
                    for (int i = 0; i < NBuf; i++) if (queued[i] && ((uint)Marshal.ReadInt32(mem + i * slot, s_FlagsOfs) & WHDR_DONE) == 0) all = false;
                    if (all) break;
                    Thread.Sleep(5);
                }
                for (int i = 0; i < NBuf; i++) waveOutUnprepareHeader(h, mem + i * slot, (uint)size);
            }
            finally
            {
                waveOutClose(h);
                Marshal.FreeHGlobal(mem);
            }
        }

        /// <summary>
        /// Mono -> two ears with a simple round-head model: the far ear hears the sound later (up to ~0.65 ms),
        /// quieter and duller (the head is in the way); sounds behind you are a little duller in both ears; nearer
        /// is louder. Everything glides from block to block so head turns never click.
        /// </summary>
        sealed class Spatializer
        {
            const double HeadRadius = 0.0875, SoundSpeed = 343, RefDist = 1.1;
            readonly int m_Rate; readonly double m_A;
            bool m_Init;
            double m_DL, m_DR, m_KL, m_KR, m_GL, m_GR, m_LpL, m_LpR;   // per ear: delay (samples), dullness 0..1, gain, filter state

            public Spatializer(int rate) { m_Rate = rate; m_A = 1 - Math.Exp(-2 * Math.PI * 1500 / rate); }

            public void Render(short[] src, int pos, int n, SoundSpot spot, short[] dst)
            {
                double x = 0, z = -1, dist = RefDist;
                if (spot.On) { x = Math.Max(-1, Math.Min(1, spot.X)); z = Math.Max(-1, Math.Min(1, spot.Z)); dist = Math.Max(0.2, spot.Dist); }
                double lat = Math.Asin(x), itd = HeadRadius / SoundSpeed * (lat + Math.Sin(lat)) * m_Rate;   // + = on the right: left ear later
                double side = Math.Abs(x), back = 0.45 * Math.Max(0, z);
                double g = 0.85 * Math.Max(0.6, Math.Min(1.3, RefDist / dist));
                double far = g * (1 - 0.45 * side), near = g * (1 + 0.1 * side), dull = 1 - (1 - 0.85 * side) * (1 - back);
                double dL = Math.Max(0, itd), dR = Math.Max(0, -itd);
                double kL = x > 0 ? dull : back, kR = x < 0 ? dull : back;
                double gL = x > 0 ? far : near, gR = x < 0 ? far : near;
                if (!m_Init) { m_Init = true; m_DL = dL; m_DR = dR; m_KL = kL; m_KR = kR; m_GL = gL; m_GR = gR; }
                double sDL = (dL - m_DL) / n, sDR = (dR - m_DR) / n, sKL = (kL - m_KL) / n, sKR = (kR - m_KR) / n, sGL = (gL - m_GL) / n, sGR = (gR - m_GR) / n;
                for (int i = 0; i < n; i++)
                {
                    m_DL += sDL; m_DR += sDR; m_KL += sKL; m_KR += sKR; m_GL += sGL; m_GR += sGR;
                    double l = Tap(src, pos + i - m_DL), rr = Tap(src, pos + i - m_DR);
                    m_LpL += m_A * (l - m_LpL); m_LpR += m_A * (rr - m_LpR);
                    dst[2 * i] = Clip((l + (m_LpL - l) * m_KL) * m_GL);
                    dst[2 * i + 1] = Clip((rr + (m_LpR - rr) * m_KR) * m_GR);
                }
            }

            static double Tap(short[] s, double p)
            {
                int i = (int)Math.Floor(p); double f = p - i;
                double a = i >= 0 && i < s.Length ? s[i] : 0, b = i + 1 >= 0 && i + 1 < s.Length ? s[i + 1] : 0;
                return a + (b - a) * f;
            }

            static short Clip(double v) { return (short)Math.Max(-32768, Math.Min(32767, v)); }
        }
    }

    /// <summary>Where a sound is relative to the listener's head. The render thread updates it every frame; a playing sound follows it.</summary>
    sealed class SoundSpot
    {
        public volatile float X, Y, Z = -1;   // unit direction in head space: right, up, back (0,0,-1 = straight ahead)
        public volatile float Dist = 1.1f;    // metres
        public volatile bool On;              // false: plain, centred
        public void Set(bool on, double x, double y, double z, double dist) { X = (float)x; Y = (float)y; Z = (float)z; Dist = (float)dist; On = on; }
    }
}

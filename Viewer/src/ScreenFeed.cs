using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// One captured source (a whole monitor or a single app window) on its own thread.
    /// Monitors use DXGI Desktop Duplication when possible (fast, GPU-side), falling back to GDI.
    /// Windows use PrintWindow, so they keep updating even when covered by other windows.
    /// The mouse cursor is drawn into the frame. The latest frame is exposed for GL upload.
    /// </summary>
    public sealed class ScreenFeed : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr dst, IntPtr src, IntPtr len);

        /// <summary>False in AR mouse mode: the viewer draws its own cursor on top instead.</summary>
        public static volatile bool DrawCursorInFrames = true;

        public readonly MonitorEntry Monitor;   // monitor feeds
        public readonly IntPtr Hwnd;            // window feeds
        public bool IsWindow { get { return Hwnd != IntPtr.Zero; } }
        public string Title;
        public double TargetFps = 60;
        public double CaptureMs, ActualFps;
        public string Method = "";
        public volatile bool Dead;              // window closed

        readonly int m_MaxWidth;
        readonly object m_Lock = new object();
        IntPtr m_Frame = IntPtr.Zero; int m_FrameCap;   // published frame
        int m_FrameW, m_FrameH;
        long m_Version;
        DibSurface m_Dib, m_Scratch;            // capture surface (+ PrintWindow scratch)
        IntPtr m_Clean = IntPtr.Zero;           // last desktop frame without cursor (duplication path)
        IntPtr m_Back = IntPtr.Zero;            // duplication writes here, then swaps with m_Clean / m_Frame
        IntPtr m_Held = IntPtr.Zero;            // frame currently owned by the renderer (zero-copy path)
        bool m_SwapMode;                        // m_Frame/m_Back/m_Held are equal-size and handed off by pointer

        // Paced presentation (AR mode): captured frames queue up with Windows' present timestamp; the renderer
        // shows each one a fixed delay after it was produced, so every video frame stays up for the same number
        // of glasses refreshes (2-2-2 for 30 fps) instead of an uneven 1-3-2 pattern.
        sealed class QFrame { public IntPtr Ptr; public long Qpc; }
        readonly Queue<QFrame> m_Ready = new Queue<QFrame>();
        readonly Stack<IntPtr> m_Free = new Stack<IntPtr>();
        QFrame m_Shown;                          // owned by the renderer
        long m_LastQpc; double m_PeriodMs = 16.7;
        public double SourcePeriodMs { get { return m_PeriodMs; } }
        public long DupFrames { get { var d = m_Dup; return d != null ? d.Frames : 0; } }
        public long DupDropped { get { var d = m_Dup; return d != null ? d.Dropped : 0; } }
        readonly object m_CleanLock = new object();
        bool m_HaveClean;
        static readonly List<ScreenFeed> s_MonitorFeeds = new List<ScreenFeed>();
        DesktopDuplication m_Dup;
        Thread m_Thread;
        volatile bool m_Run;

        public ScreenFeed(MonitorEntry mon, int maxWidth) { Monitor = mon; m_MaxWidth = maxWidth; Title = mon.DeviceName; }
        public ScreenFeed(IntPtr hwnd, string title) { Hwnd = hwnd; Title = title; m_MaxWidth = 3840; TargetFps = 30; }

        /// <summary>Size of the most recently published frame (0 until the first capture).</summary>
        public int Width { get { return m_FrameW; } }
        public int Height { get { return m_FrameH; } }

        public void Start()
        {
            if (!IsWindow) lock (s_MonitorFeeds) s_MonitorFeeds.Add(this);
            m_Run = true;
            m_Thread = new Thread(Loop) { IsBackground = true, Name = "capture " + Title };
            m_Thread.Start();
        }

        public void Dispose()
        {
            m_Run = false;
            lock (s_MonitorFeeds) s_MonitorFeeds.Remove(this);
            if (m_Thread != null) m_Thread.Join(1000);
            lock (m_Lock)
            {
                if (m_Dib != null) { m_Dib.Dispose(); m_Dib = null; }
                if (m_Scratch != null) { m_Scratch.Dispose(); m_Scratch = null; }
                if (m_Dup != null) { m_Dup.Dispose(); m_Dup = null; }
                if (m_Frame != IntPtr.Zero) { Marshal.FreeHGlobal(m_Frame); m_Frame = IntPtr.Zero; }
                if (m_Held != IntPtr.Zero) { Marshal.FreeHGlobal(m_Held); m_Held = IntPtr.Zero; }
                m_SwapMode = false;
                FreePool();
                lock (m_CleanLock)
                {
                    if (m_Clean != IntPtr.Zero) { Marshal.FreeHGlobal(m_Clean); m_Clean = IntPtr.Zero; }
                    if (m_Back != IntPtr.Zero) { Marshal.FreeHGlobal(m_Back); m_Back = IntPtr.Zero; }
                    m_HaveClean = false;
                }
            }
        }

        /// <summary>
        /// Zero-copy: if a newer frame exists, hand its buffer to the caller (who may read it without any lock
        /// until the next TakeFrame call). Returns false when not in zero-copy mode or nothing new.
        /// </summary>
        public bool TakeFrame(ref long seenVersion, out IntPtr ptr, out int w, out int h)
        {
            ptr = IntPtr.Zero; w = h = 0;
            lock (m_Lock)
            {
                if (!m_SwapMode || m_Version == seenVersion || m_Frame == IntPtr.Zero || m_Held == IntPtr.Zero) return false;
                var t = m_Frame; m_Frame = m_Held; m_Held = t;
                seenVersion = m_Version; ptr = m_Held; w = m_FrameW; h = m_FrameH;
                return true;
            }
        }

        /// <summary>
        /// Paced hand-off: returns the newest queued frame that Windows produced at least one source frame period
        /// (+3 ms) ago, releasing older ones. The caller owns it until the next call. False = keep showing the
        /// current frame.
        /// </summary>
        public bool TakePacedFrame(out IntPtr ptr, out int w, out int h)
        {
            ptr = IntPtr.Zero; w = h = 0;
            lock (m_Lock)
            {
                if (m_Ready.Count == 0) return false;
                long now = Stopwatch.GetTimestamp();
                double delayMs = Math.Max(8, Math.Min(50, m_PeriodMs + 3));
                long due = now - (long)(delayMs * Stopwatch.Frequency / 1000);
                QFrame pick = null;
                // if the queue is backing up (e.g. after a stall), don't fall further behind
                while (m_Ready.Count > 0 && (m_Ready.Peek().Qpc <= due || m_Ready.Count > 2))
                {
                    if (pick != null) m_Free.Push(pick.Ptr);
                    pick = m_Ready.Dequeue();
                }
                if (pick == null) return false;
                if (m_Shown != null) m_Free.Push(m_Shown.Ptr);
                m_Shown = pick;
                ptr = pick.Ptr; w = m_FrameW; h = m_FrameH;
                return true;
            }
        }

        void FreePool()
        {
            while (m_Free.Count > 0) Marshal.FreeHGlobal(m_Free.Pop());
            while (m_Ready.Count > 0) Marshal.FreeHGlobal(m_Ready.Dequeue().Ptr);
            if (m_Shown != null) { Marshal.FreeHGlobal(m_Shown.Ptr); m_Shown = null; }
        }

        /// <summary>Calls upload(ptr, w, h) with the frame if a newer one than 'seenVersion' exists.</summary>
        public bool TryUpload(ref long seenVersion, Action<IntPtr, int, int> upload)
        {
            lock (m_Lock)
            {
                if (m_Version == seenVersion || m_Frame == IntPtr.Zero) return false;
                upload(m_Frame, m_FrameW, m_FrameH);
                seenVersion = m_Version;
                return true;
            }
        }

        void Publish(DibSurface s)
        {
            lock (m_Lock)
            {
                int bytes = s.Width * s.Height * 4;
                if (bytes > m_FrameCap)
                {
                    if (m_Frame != IntPtr.Zero) Marshal.FreeHGlobal(m_Frame);
                    m_Frame = Marshal.AllocHGlobal(bytes); m_FrameCap = bytes;
                }
                CopyMemory(m_Frame, s.Bits, (IntPtr)bytes);
                m_FrameW = s.Width; m_FrameH = s.Height;
                m_Version++;
            }
        }

        void EnsureDib(int w, int h)
        {
            if (m_Dib != null && m_Dib.Width == w && m_Dib.Height == h) return;
            if (m_Dib != null) m_Dib.Dispose();
            m_Dib = new DibSurface(w, h);
        }

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            var fpsWatch = Stopwatch.StartNew(); int frames = 0;
            bool tryDup = !IsWindow;
            int dupFailures = 0;
            POINT lastCursor = new POINT(); IntPtr lastCursorShape = IntPtr.Zero;

            while (m_Run)
            {
                double start = sw.Elapsed.TotalMilliseconds;
                bool published = false;
                try
                {
                    if (IsWindow)
                    {
                        if (!Native.IsWindow(Hwnd)) { Dead = true; return; }
                        if (!Native.IsIconic(Hwnd))
                        {
                            RECT fr = Native.FrameBounds(Hwnd);
                            if (fr.Width > 0 && fr.Height > 0)
                            {
                                int w = fr.Width, h = fr.Height;
                                if (w > m_MaxWidth) { h = h * m_MaxWidth / w; w = m_MaxWidth; }
                                EnsureDib(w, h);
                                ScreenFeed mon = Native.GetForegroundWindow() == Hwnd && w == fr.Width ? MonitorFeedContaining(fr) : null;
                                if (mon != null && mon.CropDesktop(fr, m_Dib))
                                {
                                    // Foreground window = on top on the real screen: cut it out of the (fast) desktop frame.
                                    DrawCursor(fr, m_Dib);
                                    Publish(m_Dib); published = true;
                                    Method = "desktop crop";
                                }
                                else if (Capturer.CaptureWindow(Hwnd, ref m_Scratch, m_Dib))
                                {
                                    DrawCursor(fr, m_Dib);
                                    Publish(m_Dib); published = true;
                                    Method = "PrintWindow";
                                }
                            }
                        }
                    }
                    else if (tryDup)
                    {
                        if (m_Dup == null)
                        {
                            m_Dup = new DesktopDuplication(Monitor.DeviceName);
                            if (!m_Dup.Init() || m_Dup.Width != Monitor.Bounds.Width || m_Dup.Width > m_MaxWidth)
                            {
                                Method = "GDI (duplication unavailable: " + (m_Dup.Error ?? "size mismatch") + ")";
                                m_Dup.Dispose(); m_Dup = null;
                                if (++dupFailures >= 3) tryDup = false; else Thread.Sleep(500);
                                continue;
                            }
                            EnsureDib(m_Dup.Width, m_Dup.Height);
                            lock (m_CleanLock)
                            {
                                if (m_Clean != IntPtr.Zero) Marshal.FreeHGlobal(m_Clean);
                                if (m_Back != IntPtr.Zero) Marshal.FreeHGlobal(m_Back);
                                m_Clean = Marshal.AllocHGlobal(m_Dup.Width * m_Dup.Height * 4);
                                m_Back = Marshal.AllocHGlobal(m_Dup.Width * m_Dup.Height * 4);
                                m_HaveClean = false;
                            }
                            lock (m_Lock)
                            {
                                int bytes = m_Dup.Width * m_Dup.Height * 4;
                                if (m_Frame != IntPtr.Zero) Marshal.FreeHGlobal(m_Frame);
                                if (m_Held != IntPtr.Zero) Marshal.FreeHGlobal(m_Held);
                                m_Frame = Marshal.AllocHGlobal(bytes); m_Held = Marshal.AllocHGlobal(bytes); m_FrameCap = bytes;
                                m_FrameW = m_Dup.Width; m_FrameH = m_Dup.Height;
                                m_SwapMode = true;
                                FreePool();
                                for (int i = 0; i < 4; i++) m_Free.Push(Marshal.AllocHGlobal(bytes));
                            }
                            Method = "Desktop Duplication";
                            dupFailures = 0;
                        }
                        IntPtr target = m_Back;
                        if (!DrawCursorInFrames) lock (m_Lock) target = m_Free.Count > 0 ? m_Free.Pop() : (m_Ready.Count > 0 ? m_Ready.Dequeue().Ptr : m_Back);
                        int r = m_Dup.Acquire(16, target);
                        if (!DrawCursorInFrames && target != m_Back && r != 1) lock (m_Lock) m_Free.Push(target);
                        if (r < 0)
                        {
                            // Lost (mode change, UAC/lock screen, fullscreen app): recreate shortly.
                            m_Dup.Dispose(); m_Dup = null; Thread.Sleep(100);
                            continue;
                        }
                        if (!DrawCursorInFrames)
                        {
                            // AR mode: the viewer draws its own cursor, so the captured frame is final. Hand it over
                            // by swapping buffers: one GPU->CPU copy per frame, no others, no waiting on the renderer.
                            if (r == 1)
                            {
                                long q = m_Dup.LastPresentQpc;
                                if (m_LastQpc != 0 && q > m_LastQpc)
                                {
                                    double ms = (q - m_LastQpc) * 1000.0 / Stopwatch.Frequency;
                                    if (ms > 5 && ms < 120) m_PeriodMs += (ms - m_PeriodMs) * 0.1;
                                }
                                m_LastQpc = q;
                                lock (m_Lock)
                                {
                                    if (target == m_Back) { var t = m_Frame; m_Frame = m_Back; m_Back = t; m_Version++; }   // pool exhausted: fall back
                                    else
                                    {
                                        m_Ready.Enqueue(new QFrame { Ptr = target, Qpc = q });
                                        while (m_Ready.Count > 3) m_Free.Push(m_Ready.Dequeue().Ptr);
                                        // (no m_Version++: queued frames are taken only via TakePacedFrame, never the m_Frame path)
                                    }
                                    m_FrameW = m_Dup.Width; m_FrameH = m_Dup.Height;
                                }
                                published = true;
                            }
                            goto doneCapture;
                        }
                        if (r == 1) lock (m_CleanLock) { var t = m_Clean; m_Clean = m_Back; m_Back = t; m_HaveClean = true; }
                        var ci = CursorNow();
                        bool cursorChanged = ci.ptScreenPos.X != lastCursor.X || ci.ptScreenPos.Y != lastCursor.Y || ci.hCursor != lastCursorShape;
                        if (r == 1 || cursorChanged || m_Version == 0)
                        {
                            lock (m_CleanLock) CopyMemory(m_Dib.Bits, m_Clean, (IntPtr)(m_Dib.Width * m_Dib.Height * 4));
                            DrawCursor(Monitor.Bounds, m_Dib);
                            Publish(m_Dib); published = true;
                            lastCursor = ci.ptScreenPos; lastCursorShape = ci.hCursor;
                        }
                    }
                    else
                    {
                        int w = Monitor.Bounds.Width, h = Monitor.Bounds.Height;
                        if (w > m_MaxWidth) { h = h * m_MaxWidth / w; w = m_MaxWidth; }
                        EnsureDib(w, h);
                        if (Capturer.CaptureScreenRect(Monitor.Bounds, m_Dib))
                        {
                            DrawCursor(Monitor.Bounds, m_Dib);
                            Publish(m_Dib); published = true;
                        }
                    }
                }
                catch (Exception e) { Method = "error: " + e.Message; Thread.Sleep(200); }
                doneCapture:

                if (published) { frames++; CaptureMs = sw.Elapsed.TotalMilliseconds - start; }
                if (fpsWatch.ElapsedMilliseconds >= 1000) { ActualFps = frames * 1000.0 / fpsWatch.ElapsedMilliseconds; frames = 0; fpsWatch.Restart(); }
                // Duplication paces itself (Acquire waits for the next desktop frame); others sleep to the target rate.
                if (m_Dup == null)
                {
                    double fps = Method == "desktop crop" ? 60 : TargetFps;
                    double wait = 1000.0 / fps - (sw.Elapsed.TotalMilliseconds - start);
                    if (wait > 1) Thread.Sleep((int)wait);
                }
            }
        }

        static ScreenFeed MonitorFeedContaining(RECT r)
        {
            lock (s_MonitorFeeds)
                foreach (var f in s_MonitorFeeds)
                {
                    var b = f.Monitor.Bounds;
                    if (r.Left >= b.Left && r.Top >= b.Top && r.Right <= b.Right && r.Bottom <= b.Bottom) return f;
                }
            return null;
        }

        /// <summary>Copies a desktop rectangle (fully inside this monitor) from the latest duplicated frame.</summary>
        public bool CropDesktop(RECT r, DibSurface dst)
        {
            lock (m_CleanLock)
            {
                if (!m_HaveClean || m_Clean == IntPtr.Zero || dst.Width != r.Width || dst.Height != r.Height) return false;
                var b = Monitor.Bounds;
                int srcStride = b.Width * 4, row = r.Width * 4;
                IntPtr src = m_Clean + ((r.Top - b.Top) * b.Width + (r.Left - b.Left)) * 4;
                for (int y = 0; y < r.Height; y++) CopyMemory(dst.Bits + y * dst.Stride, src + y * srcStride, (IntPtr)row);
                return true;
            }
        }

        static CURSORINFO CursorNow()
        {
            var ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            Native.GetCursorInfo(ref ci);
            return ci;
        }

        /// <summary>Draws the cursor into 'dst', which shows the desktop rectangle 'area' (scaled to dst's size).</summary>
        static void DrawCursor(RECT area, DibSurface dst)
        {
            if (!DrawCursorInFrames) return;
            var ci = CursorNow();
            if ((ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
            if (ci.ptScreenPos.X < area.Left || ci.ptScreenPos.X >= area.Right || ci.ptScreenPos.Y < area.Top || ci.ptScreenPos.Y >= area.Bottom) return;
            ICONINFO ii;
            if (!GetIconInfo(ci.hCursor, out ii)) return;
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            double s = (double)dst.Width / area.Width;
            int x = (int)((ci.ptScreenPos.X - area.Left) * s) - ii.xHotspot;
            int y = (int)((ci.ptScreenPos.Y - area.Top) * s) - ii.yHotspot;
            DrawIconEx(dst.Hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /*DI_NORMAL*/);
        }
    }
}

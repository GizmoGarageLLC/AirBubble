using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// Windows won't let a normal app send clicks to a window of a more privileged process (UAC prompt,
    /// apps running as administrator): SendInput is silently dropped. The AR cursor normally swallows the
    /// physical mouse and re-injects clicks, so over such windows the real clicks have to go straight
    /// through instead. This tells which windows those are (cached per top-level window).
    /// </summary>
    static class ElevatedWindows
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int retLen);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008, GA_ROOT = 2;
        const int TokenIntegrityLevel = 25;

        static readonly int s_OwnLevel = LevelOf(GetCurrentProcess(), false);
        static IntPtr s_LastHwnd; static bool s_LastResult; static long s_LastAt;
        static readonly Stopwatch s_Clock = Stopwatch.StartNew();
        static readonly uint s_Pid = (uint)Process.GetCurrentProcess().Id;

        /// <summary>True if the window under screen point (x, y) belongs to a process more privileged than us.</summary>
        public static bool IsBlockedAt(int x, int y)
        {
            IntPtr h = WindowFromPoint(new PT { X = x, Y = y });
            if (h == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(h, GA_ROOT); if (root != IntPtr.Zero) h = root;
            long now = s_Clock.ElapsedMilliseconds;
            if (h == s_LastHwnd && now - s_LastAt < 1000) return s_LastResult;
            uint pid; GetWindowThreadProcessId(h, out pid);
            bool r = false;
            if (pid != 0 && pid != s_Pid)
            {
                IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (p == IntPtr.Zero) r = true;                         // can't even look at it: protected / SYSTEM
                else { try { r = LevelOf(p, true) > s_OwnLevel; } finally { CloseHandle(p); } }
            }
            s_LastHwnd = h; s_LastResult = r; s_LastAt = now;
            return r;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        static IntPtr s_FgHwnd; static bool s_FgResult; static long s_FgAt = -10000;

        /// <summary>
        /// True while the window that has the focus belongs to a more privileged process. Windows then delivers
        /// neither low-level mouse hook calls nor raw mouse input to us: the physical mouse moves the real pointer
        /// and we can only watch where it goes.
        /// </summary>
        public static bool ForegroundBlocked()
        {
            IntPtr h = GetForegroundWindow();
            long now = s_Clock.ElapsedMilliseconds;
            if (h == s_FgHwnd && now - s_FgAt < 500) return s_FgResult;
            bool r = false;
            if (h != IntPtr.Zero)
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != 0 && pid != s_Pid)
                {
                    IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (p == IntPtr.Zero) r = true;
                    else { try { r = LevelOf(p, true) > s_OwnLevel; } finally { CloseHandle(p); } }
                }
            }
            s_FgHwnd = h; s_FgResult = r; s_FgAt = now;
            return r;
        }

        /// <summary>Mandatory integrity level RID (0x2000 medium, 0x3000 high, 0x4000 system).</summary>
        static int LevelOf(IntPtr process, bool unknownIsHigh)
        {
            IntPtr tok;
            if (!OpenProcessToken(process, TOKEN_QUERY, out tok)) return unknownIsHigh ? 0x4000 : 0x2000;   // e.g. consent.exe (SYSTEM)
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                int len;
                if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, 256, out len)) return unknownIsHigh ? 0x4000 : 0x2000;
                IntPtr sid = Marshal.ReadIntPtr(buf);                    // TOKEN_MANDATORY_LABEL.Label.Sid
                int n = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(n - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); CloseHandle(tok); }
        }
    }
}

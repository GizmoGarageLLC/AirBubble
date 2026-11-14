using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// Applies a whole desktop arrangement atomically with the CCD API (QueryDisplayConfig/SetDisplayConfig),
    /// the same mechanism Windows' Display Settings uses: which displays stay on, their resolution, their
    /// position, and which one is primary (the one at 0,0). The legacy ChangeDisplaySettingsEx path is
    /// unreliable right after a topology switch (Windows re-attaches displays / ignores the primary).
    /// </summary>
    public static class DisplayConfig
    {
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPath, IntPtr paths, ref uint numMode, IntPtr modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPath, IntPtr paths, uint numMode, IntPtr modes, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

        const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
        const uint SDC_TOPOLOGY_SUPPLIED = 0x10, SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;
        const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
        const int PathSize = 72, ModeSize = 64;
        const uint InvalidIdx = 0xFFFFFFFF;

        public sealed class Want
        {
            public string Device;          // \\.\DISPLAYn (GDI name of the source)
            public int Width, Height;      // 0 = keep current
            public int X, Y;
        }

        /// <summary>
        /// Keeps only the displays listed in 'wanted' (others are turned off), sets their size and position
        /// (the one placed at 0,0 becomes primary). Returns 0 on success, else the Win32 error.
        /// </summary>
        // Mode index fields. Paths flagged DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE (0x8) pack two 16-bit indices:
        //   source: low16 = cloneGroupId, high16 = sourceModeInfoIdx
        //   target: low16 = desktopModeInfoIdx, high16 = targetModeInfoIdx   (0xFFFF = invalid)
        // otherwise each field is one 32-bit index (0xFFFFFFFF = invalid).
        struct Slot { public int Offset; public bool High; public bool Packed; }

        static List<Slot> Slots(IntPtr path)
        {
            bool packed = (Marshal.ReadInt32(path, 68) & 0x8) != 0;
            var l = new List<Slot>();
            if (packed)
            {
                l.Add(new Slot { Offset = 12, High = true, Packed = true });    // source mode
                l.Add(new Slot { Offset = 32, High = false, Packed = true });   // desktop image mode
                l.Add(new Slot { Offset = 32, High = true, Packed = true });    // target mode
            }
            else { l.Add(new Slot { Offset = 12 }); l.Add(new Slot { Offset = 32 }); }
            return l;
        }

        static uint Get(IntPtr path, Slot sl)
        {
            uint v = (uint)Marshal.ReadInt32(path, sl.Offset);
            if (!sl.Packed) return v;
            uint x = sl.High ? (v >> 16) : (v & 0xFFFF);
            return x == 0xFFFF ? InvalidIdx : x;
        }

        static void Set(IntPtr path, Slot sl, uint idx)
        {
            if (!sl.Packed) { Marshal.WriteInt32(path, sl.Offset, unchecked((int)idx)); return; }
            uint v = (uint)Marshal.ReadInt32(path, sl.Offset), x = idx == InvalidIdx ? 0xFFFF : (idx & 0xFFFF);
            v = sl.High ? ((v & 0x0000FFFF) | (x << 16)) : ((v & 0xFFFF0000) | x);
            Marshal.WriteInt32(path, sl.Offset, unchecked((int)v));
        }

        /// <summary>
        /// Keeps only the displays listed in 'wanted' (others are turned off), sets their size and position
        /// (the one placed at 0,0 becomes primary). Returns 0 on success, else the Win32 error.
        /// </summary>
        /// 'save' = also store it in Windows' display database (Windows re-applies stored layouts by itself when
        /// the same set of displays shows up again, e.g. when the glasses are plugged in). See SaveArLayout for the
        /// AR layout.
        public static int Apply(List<Want> wanted, bool save = true)
        {
            uint np, nm;
            int r = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm);
            if (r != 0) return r;
            IntPtr paths = Marshal.AllocHGlobal((int)Math.Max(1, np) * PathSize), modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * ModeSize);
            IntPtr newPaths = IntPtr.Zero, newModes = IntPtr.Zero;
            try
            {
                r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (r != 0) return r;
                var keepPaths = new List<int>();
                var modeMap = new Dictionary<uint, uint>();
                var newModeList = new List<int>();
                var wantFor = new Dictionary<int, Want>();
                bool anyPacked = false;
                for (int i = 0; i < np; i++)
                {
                    IntPtr pth = paths + i * PathSize;
                    string name = SourceName(pth);
                    var w = wanted.Find(x => string.Equals(x.Device, name, StringComparison.OrdinalIgnoreCase));
                    if (w == null) continue;            // display turned off
                    // a virtual monitor duplicating a real screen (Windows' Duplicate mode includes them): turn it off
                    if (IsVirtualTarget(pth) && !VirtualScreens.IsVirtualOutput(name)) continue;
                    keepPaths.Add(i); wantFor[i] = w;
                    if ((Marshal.ReadInt32(pth, 68) & 0x8) != 0) anyPacked = true;
                    foreach (var sl in Slots(pth))
                    {
                        uint idx = Get(pth, sl);
                        if (idx == InvalidIdx || idx >= nm || modeMap.ContainsKey(idx)) continue;
                        modeMap[idx] = (uint)newModeList.Count; newModeList.Add((int)idx);
                    }
                }
                if (keepPaths.Count == 0) return 87;

                newPaths = Marshal.AllocHGlobal(keepPaths.Count * PathSize);
                newModes = Marshal.AllocHGlobal(Math.Max(1, newModeList.Count) * ModeSize);
                for (int k = 0; k < newModeList.Count; k++) Copy(newModes + k * ModeSize, modes + newModeList[k] * ModeSize, ModeSize);
                for (int k = 0; k < keepPaths.Count; k++)
                {
                    IntPtr src = paths + keepPaths[k] * PathSize, dst = newPaths + k * PathSize;
                    Copy(dst, src, PathSize);
                    var slots = Slots(src);
                    foreach (var sl in slots)
                    {
                        uint idx = Get(src, sl);
                        Set(dst, sl, idx != InvalidIdx && modeMap.ContainsKey(idx) ? modeMap[idx] : InvalidIdx);
                    }
                    var w = wantFor[keepPaths[k]];
                    uint sIdx = Get(dst, slots[0]);
                    if (sIdx == InvalidIdx || sIdx >= newModeList.Count) continue;
                    IntPtr m = newModes + (int)sIdx * ModeSize;
                    if (Marshal.ReadInt32(m, 0) != 1 /*SOURCE*/) continue;
                    if (w.Width > 0 && w.Height > 0 && (Marshal.ReadInt32(m, 16) != w.Width || Marshal.ReadInt32(m, 20) != w.Height))
                    {
                        Marshal.WriteInt32(m, 16, w.Width); Marshal.WriteInt32(m, 20, w.Height);
                        // a new source size needs fresh target/desktop modes: let Windows pick them
                        for (int q = 1; q < slots.Count; q++) Set(dst, slots[q], InvalidIdx);
                    }
                    Marshal.WriteInt32(m, 28, w.X); Marshal.WriteInt32(m, 32, w.Y);
                }
                return SetDisplayConfig((uint)keepPaths.Count, newPaths, (uint)newModeList.Count, newModes,
                    SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | (save ? SDC_SAVE_TO_DATABASE : 0u) | SDC_ALLOW_CHANGES | (anyPacked ? 0x8000u /*SDC_VIRTUAL_MODE_AWARE*/ : 0u));
            }
            finally
            {
                Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes);
                if (newPaths != IntPtr.Zero) Marshal.FreeHGlobal(newPaths);
                if (newModes != IntPtr.Zero) Marshal.FreeHGlobal(newModes);
            }
        }

        public static bool IsVirtual(MonitorEntry m) { return m.IsVirtual; }
        // The AR layout is stored in Windows' display database while the viewer runs: an unstored layout is treated as
        // temporary and Windows reverts it (laptop back as primary) on the next display event, e.g. right after the
        // glasses switch 2D/3D. On exit RestorePcDesktop stores the normal layout again, and the guard fixes the
        // desktop if the viewer didn't get to (crash, unplugged while running). XR_NO_SAVE_AR=1 turns it off (tests).
        public static bool SaveArLayout = Environment.GetEnvironmentVariable("XR_NO_SAVE_AR") == null;

        /// <summary>Shifts the whole desktop so 'device' sits at 0,0 (= Windows primary); other displays keep their relative places.</summary>
        public static int MakePrimary(string device)
        {
            var mons = DisplaySetup.ActiveMonitors();
            var p = mons.Find(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));
            if (p == null) return 87;
            return Apply(mons.ConvertAll(m => new Want { Device = m.DeviceName, X = m.Bounds.Left - p.Bounds.Left, Y = m.Bounds.Top - p.Bounds.Top }), SaveArLayout);
        }

        /// <summary>Makes 'device' the Windows primary and checks that it took (CCD twice, then the legacy API).</summary>
        public static bool EnsurePrimary(string device, Action<string> log)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var p = DisplaySetup.ActiveMonitors().Find(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));
                if (p == null) { log("Primary: " + device + " is not on the desktop"); return false; }
                if (p.Primary) return true;
                int rc = attempt < 2 ? MakePrimary(device) : (VirtualScreens.SetPrimary(device) ? 0 : -1);
                log("Primary: making " + device + " the main display (attempt " + (attempt + 1) + ") -> " + rc);
                System.Threading.Thread.Sleep(800);
            }
            var q = DisplaySetup.ActiveMonitors().Find(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));
            bool ok = q != null && q.Primary;
            if (!ok) log("Primary: Windows kept another display as primary");
            return ok;
        }

        /// <summary>
        /// Puts the desktop back to normal PC use (whenever the viewer isn't showing the screens):
        /// virtual screens off, a PC screen (the preferred one if given) primary, and the glasses, if plugged in,
        /// duplicating it so the primary desktop is what you see in them. Does nothing when that's already the case.
        /// </summary>
        public static void RestorePcDesktop(string preferredPrimary, Action<string> log, bool duplicate = true)
        {
            if (PcDesktopOk() || (!duplicate && PcPrimaryNoVirtual())) return;
            log("Desktop restore from: " + Describe());
            var cur = DisplaySetup.ActiveMonitors();
            if (!cur.Exists(m => !m.IsGlasses && !IsVirtual(m)))
            {
                // no PC screen on the desktop at all (e.g. left over from a crash): switch it back on first
                log("Desktop restore: PC screen only: " + DisplaySetup.SetTopology(DisplaySetup.TOPOLOGY_INTERNAL));
                System.Threading.Thread.Sleep(1500);
                cur = DisplaySetup.ActiveMonitors();
            }
            var pcs = cur.FindAll(m => !m.IsGlasses && !IsVirtual(m));
            pcs.Sort((a, b) => Rank(a, preferredPrimary).CompareTo(Rank(b, preferredPrimary)));
            if (pcs.Count > 0)
            {
                // virtual screens off (also ones duplicating the PC screen), PC screen primary, glasses extended
                var want = new List<Want>();
                int x = 0;
                foreach (var m in pcs) { want.Add(new Want { Device = m.DeviceName, X = x, Y = 0 }); x += m.Bounds.Width; }
                var gl = cur.Find(m => m.IsGlasses);
                if (gl != null) want.Add(new Want { Device = gl.DeviceName, X = x, Y = 0 });
                log("Desktop restore: PC screen primary, virtual screens off: " + Apply(want));
                System.Threading.Thread.Sleep(800);
            }
            try { int n = VirtualScreens.DetachAll(); if (n > 0) { log("Detached " + n + " virtual screen(s)"); System.Threading.Thread.Sleep(600); } } catch { }
            if (duplicate && pcs.Count > 0 && DisplaySetup.GlassesAttached())
            {
                log("Desktop restore: Duplicate PC screen + glasses: " + SetDuplicate(pcs[0].DeviceName));
                System.Threading.Thread.Sleep(1500);
            }
            if (duplicate && !PcDesktopOk()) log("Desktop restore: not fully normal: " + Describe());
        }

        /// <summary>
        /// Normal PC desktop: no virtual monitor showing anything, a PC screen is primary, and the glasses (if
        /// plugged in) show it too (Duplicate) instead of being a separate screen.
        /// </summary>
        /// <summary>A PC screen is primary and no virtual monitor shows anything (the glasses may be anything).</summary>
        public static bool PcPrimaryNoVirtual()
        {
            var cur = DisplaySetup.ActiveMonitors();
            var prim = cur.Find(m => m.Primary);
            return prim != null && !prim.IsGlasses && !IsVirtual(prim) && !cur.Exists(IsVirtual) && !VirtualScreens.AnyVirtualMonitorActive();
        }

        public static bool PcDesktopOk()
        {
            var cur = DisplaySetup.ActiveMonitors();
            var prim = cur.Find(m => m.Primary);
            if (prim == null || prim.IsGlasses || IsVirtual(prim) || cur.Exists(IsVirtual) || VirtualScreens.AnyVirtualMonitorActive()) return false;
            if (DisplaySetup.GlassesAttached() && (cur.Exists(m => m.IsGlasses) || !DisplaySetup.GlassesShowing())) return false;
            return true;
        }

        public static string Describe()
        {
            return string.Join(", ", DisplaySetup.ActiveMonitors().ConvertAll(m => string.Format("{0}{1}{2}{3} {4}x{5} at {6},{7}",
                m.DeviceName, m.Primary ? " primary" : "", m.IsGlasses ? " glasses" : "", IsVirtual(m) ? " virtual" : "", m.Bounds.Width, m.Bounds.Height, m.Bounds.Left, m.Bounds.Top)).ToArray())
                + (VirtualScreens.AnyVirtualMonitorActive() ? " [virtual monitor active]" : "")
                + (DisplaySetup.GlassesAttached() ? (DisplaySetup.GlassesShowing() ? " [glasses showing]" : " [glasses off]") : " [no glasses]");
        }

        /// <summary>Monitor device path of a path's target (contains e.g. "MTT1337" or "MRG3132"), or null.</summary>
        static string TargetPath(IntPtr path)
        {
            const int Size = 420;   // header(20) + flags/tech/ids(16) + friendly name WCHAR[64] + device path WCHAR[128]
            IntPtr pk = Marshal.AllocHGlobal(Size);
            try
            {
                for (int i = 0; i < Size; i++) Marshal.WriteByte(pk, i, 0);
                Marshal.WriteInt32(pk, 0, 2);                              // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
                Marshal.WriteInt32(pk, 4, Size);
                Marshal.WriteInt64(pk, 8, Marshal.ReadInt64(path, 20));    // targetInfo.adapterId
                Marshal.WriteInt32(pk, 16, Marshal.ReadInt32(path, 28));   // targetInfo.id
                return DisplayConfigGetDeviceInfo(pk) == 0 ? Marshal.PtrToStringUni(pk + 164) : null;
            }
            finally { Marshal.FreeHGlobal(pk); }
        }
        static bool IsVirtualTarget(IntPtr path) { var t = TargetPath(path); return t != null && t.ToUpperInvariant().Contains(VirtualScreens.MonitorTag); }
        static bool IsGlassesTarget(IntPtr path) { var t = TargetPath(path); return t != null && t.ToUpperInvariant().Contains("MRG"); }

        /// <summary>
        /// Duplicate for exactly the PC screen 'pcDevice' and the glasses (Windows' own Duplicate also duplicates
        /// onto every virtual monitor). Everything else is switched off. Returns 0 on success.
        /// </summary>
        public static int SetDuplicate(string pcDevice)
        {
            uint np, nm;
            int r = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out np, out nm);
            if (r != 0) return r;
            IntPtr paths = Marshal.AllocHGlobal((int)Math.Max(1, np) * PathSize), modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * ModeSize);
            IntPtr two = Marshal.AllocHGlobal(2 * PathSize);
            try
            {
                r = QueryDisplayConfig(QDC_ALL_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (r != 0) return r;
                int pc = -1, gl = -1;
                for (int i = 0; i < np && pc < 0; i++)
                {
                    IntPtr p = paths + i * PathSize;
                    if ((Marshal.ReadInt32(p, 68) & 1) != 0 && string.Equals(SourceName(p), pcDevice, StringComparison.OrdinalIgnoreCase)
                        && !IsVirtualTarget(p) && !IsGlassesTarget(p)) pc = i;
                }
                if (pc < 0) return 1168;   // ERROR_NOT_FOUND: PC screen not active
                IntPtr pcp = paths + pc * PathSize;
                long srcAdapter = Marshal.ReadInt64(pcp, 0); int srcId = Marshal.ReadInt32(pcp, 8);
                for (int i = 0; i < np && gl < 0; i++)
                {
                    IntPtr p = paths + i * PathSize;
                    if (Marshal.ReadInt64(p, 0) == srcAdapter && Marshal.ReadInt32(p, 8) == srcId && Marshal.ReadInt32(p, 60) != 0 && IsGlassesTarget(p)) gl = i;
                }
                if (gl < 0) return 1169;   // the glasses can't be driven from the PC screen's output
                int k = 0;
                foreach (int idx in new[] { pc, gl })
                {
                    IntPtr dst = two + (k++) * PathSize;
                    Copy(dst, paths + idx * PathSize, PathSize);
                    Marshal.WriteInt32(dst, 12, -1);                // source mode: let Windows choose
                    Marshal.WriteInt32(dst, 32, -1);                // target mode
                    Marshal.WriteInt32(dst, 68, 1);                 // DISPLAYCONFIG_PATH_ACTIVE
                }
                return SetDisplayConfig(2, two, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); Marshal.FreeHGlobal(two); }
        }

        static int Rank(MonitorEntry m, string preferred)
        {
            if (preferred != null && string.Equals(m.DeviceName, preferred, StringComparison.OrdinalIgnoreCase)) return 0;
            return m.Primary ? 1 : 2;
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void Copy(IntPtr dst, IntPtr src, int len);

        /// <summary>GDI device name (\\.\DISPLAYn) of a path's source.</summary>
        static string SourceName(IntPtr path)
        {
            const int Size = 84;   // header(20) + WCHAR[32]
            IntPtr pk = Marshal.AllocHGlobal(Size);
            try
            {
                for (int i = 0; i < Size; i++) Marshal.WriteByte(pk, i, 0);
                Marshal.WriteInt32(pk, 0, 1);                              // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                Marshal.WriteInt32(pk, 4, Size);
                Marshal.WriteInt64(pk, 8, Marshal.ReadInt64(path, 0));     // sourceInfo.adapterId
                Marshal.WriteInt32(pk, 16, Marshal.ReadInt32(path, 8));    // sourceInfo.id
                return DisplayConfigGetDeviceInfo(pk) == 0 ? Marshal.PtrToStringUni(pk + 20) : null;
            }
            finally { Marshal.FreeHGlobal(pk); }
        }
    }
}

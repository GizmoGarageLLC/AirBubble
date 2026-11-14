using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    public sealed class MonitorEntry
    {
        public string DeviceName;   // \\.\DISPLAYn
        public string MonitorId;    // MONITOR\MRG3132\...
        public RECT Bounds;         // physical pixels
        public bool Primary;
        public bool IsGlasses;
        public bool IsVirtual;      // an output of the virtual display driver (one of our AR screens)
    }

    /// <summary>Finds the glasses among the active displays and manages Windows' Extend/Duplicate topology.</summary>
    public static class DisplaySetup
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, int idx, ref DISPLAY_DEVICE dd, int flags);
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPath, IntPtr paths, ref uint numMode, IntPtr modes, out uint topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPath, IntPtr paths, uint numMode, IntPtr modes, uint flags);

        const uint QDC_DATABASE_CURRENT = 0x4;
        const uint SDC_APPLY = 0x80;
        public const uint TOPOLOGY_INTERNAL = 1, TOPOLOGY_CLONE = 2, TOPOLOGY_EXTEND = 4, TOPOLOGY_EXTERNAL = 8;

        /// <summary>XREAL glasses report EDID manufacturer "MRG".</summary>
        static bool LooksLikeGlasses(string monitorId) { return monitorId != null && monitorId.ToUpperInvariant().Contains("\\MRG"); }

        public static List<MonitorEntry> ActiveMonitors()
        {
            // Map \\.\DISPLAYn -> monitor hardware id.
            // In Duplicate mode one output lists several monitors (e.g. the PC screen plus duplicated virtual
            // ones, or the glasses): prefer the active monitor that is neither, so the PC screen keeps its identity.
            var ids = new Dictionary<string, string>();
            var virt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; ; i++)
            {
                var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref ad, 0)) break;
                if (ad.DeviceID != null && ad.DeviceID.ToUpperInvariant().Contains(VirtualScreens.AdapterTag)) virt.Add(ad.DeviceName);
                int best = -1;
                for (int j = 0; ; j++)
                {
                    var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!EnumDisplayDevices(ad.DeviceName, j, ref md, 0)) break;
                    bool isVirtMon = md.DeviceID != null && md.DeviceID.ToUpperInvariant().Contains(VirtualScreens.MonitorTag);
                    int score = ((md.StateFlags & 1) != 0 ? 4 : 0) + (isVirtMon == virt.Contains(ad.DeviceName) ? 2 : 0) + (LooksLikeGlasses(md.DeviceID) ? 0 : 1);
                    if (score > best) { best = score; ids[ad.DeviceName] = md.DeviceID; }
                }
            }
            var list = new List<MonitorEntry>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data)
            {
                var mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!Native.GetMonitorInfo(hMon, ref mi)) return true;
                string id; ids.TryGetValue(mi.szDevice, out id);
                list.Add(new MonitorEntry { DeviceName = mi.szDevice, MonitorId = id, Bounds = mi.rcMonitor, Primary = (mi.dwFlags & 1) != 0, IsGlasses = LooksLikeGlasses(id), IsVirtual = virt.Contains(mi.szDevice) });
                return true;
            }, IntPtr.Zero);
            list.Sort((a, b) => a.Bounds.Left != b.Bounds.Left ? a.Bounds.Left.CompareTo(b.Bounds.Left) : a.Bounds.Top.CompareTo(b.Bounds.Top));
            return list;
        }

        /// <summary>Glasses are attached (EDID seen) even if not an active desktop, e.g. in Duplicate mode.</summary>
        public static bool GlassesAttached()
        {
            for (int i = 0; ; i++)
            {
                var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref ad, 0)) return false;
                for (int j = 0; ; j++)
                {
                    var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!EnumDisplayDevices(ad.DeviceName, j, ref md, 0)) break;
                    if (LooksLikeGlasses(md.DeviceID)) return true;
                }
            }
        }

        /// <summary>The glasses are showing a picture (their own screen or duplicating another one).</summary>
        public static bool GlassesShowing()
        {
            for (int i = 0; ; i++)
            {
                var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref ad, 0)) return false;
                if ((ad.StateFlags & 1) == 0) continue;
                for (int j = 0; ; j++)
                {
                    var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!EnumDisplayDevices(ad.DeviceName, j, ref md, 0)) break;
                    if ((md.StateFlags & 1) != 0 && LooksLikeGlasses(md.DeviceID)) return true;
                }
            }
        }

        public static uint CurrentTopology()
        {
            uint np, nm;
            if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out np, out nm) != 0) return 0;
            IntPtr p = Marshal.AllocHGlobal((int)Math.Max(1, np) * 128), m = Marshal.AllocHGlobal((int)Math.Max(1, nm) * 128);
            try
            {
                uint topo;
                return QueryDisplayConfig(QDC_DATABASE_CURRENT, ref np, p, ref nm, m, out topo) == 0 ? topo : 0;
            }
            finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(m); }
        }

        public static bool SetTopology(uint topology)
        {
            return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology) == 0;
        }

        /// <summary>
        /// Ensures the glasses are their own (extended) display. Returns the glasses monitor, or null.
        /// 'previousTopology' is set when we changed the topology and should restore it on exit.
        /// </summary>
        public static MonitorEntry EnsureExtended(out uint previousTopology, Action<string> log)
        {
            previousTopology = 0;
            var g = ActiveMonitors().Find(m => m.IsGlasses);
            if (g != null && ActiveMonitors().Count > 1) return g;
            if (!GlassesAttached()) { log("Glasses display not detected by Windows."); return null; }

            uint topo = CurrentTopology();
            log("Glasses are not an extended display (topology " + topo + "); switching to Extend.");
            if (!SetTopology(TOPOLOGY_EXTEND)) { log("SetDisplayConfig(EXTEND) failed."); return null; }
            previousTopology = topo;
            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(200);
                var mons = ActiveMonitors();
                g = mons.Find(m => m.IsGlasses);
                if (g != null && mons.Count > 1) { Thread.Sleep(500); return ActiveMonitors().Find(m => m.IsGlasses); }
            }
            log("Glasses did not appear as an extended display.");
            return null;
        }
    }
}

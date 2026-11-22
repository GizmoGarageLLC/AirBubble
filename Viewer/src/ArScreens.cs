using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// One PC screen floating in AR, behaving like a window: a placement on a sphere around the user
    /// (yaw/pitch/distance, facing the user), a physical width, and window state.
    /// Local panel frame: origin at the screen centre, +X right, +Y up, the screen lies in z = 0 facing +Z.
    /// </summary>
    public sealed class ScreenWin
    {
        public string Key;                 // layout key: monitor id (physical) or "virtual:<monitor id>"
        public string Title;
        public MonitorEntry Mon;           // current Windows monitor (bounds in physical px)
        public bool Virtual;
        public ScreenFeed Feed;

        public double Yaw, Pitch, Dist = 1.6, Width = 1.0;   // degrees, metres
        public bool Minimized, Locked, Maximized;
        public double SYaw, SPitch, SDist, SWidth;           // placement saved by maximize

        // GL state (render thread)
        public uint Tex, TitleTex; public long Seen; public int TexW, TexH;
        public string TitleStamp; public int TitlePxW;

        public double Aspect { get { return Mon != null && Mon.Bounds.Width > 0 ? (double)Mon.Bounds.Height / Mon.Bounds.Width : 9.0 / 16; } }
        public double Height { get { return Width * Aspect; } }
        public double TitleH { get { return Math.Max(0.03, Math.Min(0.07, Width * 0.04)); } }
        public double Gap { get { return TitleH * 0.18; } }
        public double EdgeBand { get { return TitleH * 0.55; } }

        public Quat Pose { get { return Quat.AxisAngle(0, 1, 0, Yaw * Math.PI / 180) * Quat.AxisAngle(1, 0, 0, Pitch * Math.PI / 180); } }

        /// <summary>Ray from the eye (origin) along world dir: hit point in panel-local coords. False if parallel/behind.</summary>
        public bool Intersect(double dx, double dy, double dz, out double lx, out double ly, out double t)
        {
            lx = ly = t = 0;
            Quat q = Pose;
            double nx, ny, nz; q.Rotate(0, 0, 1, out nx, out ny, out nz);
            double denom = dx * nx + dy * ny + dz * nz;
            if (denom > -1e-6) return false;
            t = -Dist / denom;
            double lz; q.Conj().Rotate(dx * t, dy * t, dz * t, out lx, out ly, out lz);
            return t > 0;
        }

        public enum Part { None, Content, Title, Lock, Minimize, Maximize, Close, Pull, DistPct, Push, EdgeL, EdgeR, EdgeT, EdgeB, CornerTL, CornerTR, CornerBL, CornerBR }

        /// <summary>Which part of this window a local point is on (content uv returned for Content).</summary>
        public Part HitPart(double lx, double ly, out double u, out double v)
        {
            double w = Width, h = Height, T = TitleH, e = EdgeBand;
            u = lx / w + 0.5; v = 0.5 - ly / h;
            double tb0 = h / 2 + Gap, tb1 = tb0 + T;
            if (ly >= tb0 && ly <= tb1 && lx >= -w / 2 && lx <= w / 2)
            {
                if (lx <= -w / 2 + T) return Part.Lock;
                if (lx >= w / 2 - T) return Part.Close;
                if (lx >= w / 2 - 2 * T) return Part.Maximize;
                if (lx >= w / 2 - 3 * T) return Part.Minimize;
                double fromRight = (w / 2 - lx) / T;   // distance controls, laid out as in Chrome.TitleBar
                if (fromRight >= Chrome.PushAt && fromRight < Chrome.PctAt) return Part.Push;
                if (fromRight >= Chrome.PctAt && fromRight < Chrome.PullAt) return Part.DistPct;
                if (fromRight >= Chrome.PullAt && fromRight < Chrome.DistEnd) return Part.Pull;
                return Part.Title;
            }
            if (Math.Abs(lx) <= w / 2 && Math.Abs(ly) <= h / 2) return Part.Content;
            // Resize band just outside the left, right and bottom edges (the top is the title bar).
            if (Math.Abs(lx) > w / 2 + e || ly < -h / 2 - e || ly > h / 2) return Part.None;
            bool l = lx < -w / 2, r = lx > w / 2, b = ly < -h / 2;
            if (b && l) return Part.CornerBL;
            if (b && r) return Part.CornerBR;
            if (l) return Part.EdgeL;
            if (r) return Part.EdgeR;
            if (b) return Part.EdgeB;
            return Part.None;
        }

        public static bool IsEdge(Part p) { return p >= Part.EdgeL; }
    }

    /// <summary>Saved placements (%APPDATA%\AirBubble\layout.ini): one line per screen key.</summary>
    public static class LayoutStore
    {
        static string PathFor() { return Path.Combine(Settings.Dir, "layout.ini"); }

        public sealed class Entry { public double Yaw, Pitch, Dist, Width; public bool Minimized, Locked, Open; }

        public static Dictionary<string, Entry> Load()
        {
            var d = new Dictionary<string, Entry>();
            try
            {
                foreach (var line in File.ReadAllLines(PathFor()))
                {
                    int eq = line.LastIndexOf('='); if (eq <= 0) continue;
                    var p = line.Substring(eq + 1).Split(',');
                    if (p.Length < 7) continue;
                    var c = CultureInfo.InvariantCulture;
                    d[line.Substring(0, eq)] = new Entry
                    {
                        Yaw = double.Parse(p[0], c), Pitch = double.Parse(p[1], c), Dist = double.Parse(p[2], c), Width = double.Parse(p[3], c),
                        Minimized = p[4] == "1", Locked = p[5] == "1", Open = p[6] == "1"
                    };
                }
            }
            catch { }
            return d;
        }

        public static void Save(Dictionary<string, Entry> d)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var lines = new List<string>();
                foreach (var kv in d)
                {
                    var e = kv.Value;
                    lines.Add(kv.Key + "=" + string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F3},{3:F3},{4},{5},{6}",
                        e.Yaw, e.Pitch, e.Dist, e.Width, e.Minimized ? 1 : 0, e.Locked ? 1 : 0, e.Open ? 1 : 0));
                }
                File.WriteAllLines(PathFor(), lines);
            }
            catch { }
        }
    }
}

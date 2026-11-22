using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    /// <summary>The AR "window chrome": title bars, the dock and the cursor, drawn with GDI+ and uploaded as GL textures.</summary>
    static class Chrome
    {
        public const int TitlePx = 64;                // title bar texture height; buttons are TitlePx squares
        public enum TitleHit { None = -1, Lock = 0, Minimize = 1, Maximize = 2, Close = 3, Bar = 4, Pull = 5, DistPct = 6, Push = 7 }

        // Distance controls left of the window buttons: [Pull][ 100% ][Push]. Positions are measured from the bar's
        // right edge in title-bar heights (the three window buttons take 0-3). ScreenWin.HitPart uses the same numbers.
        public const double PushAt = 3.25, PctAt = 4.25, PullAt = 5.85, DistEnd = 6.85;

        static readonly Color Bar = Color.FromArgb(235, 26, 30, 42), BarActive = Color.FromArgb(240, 34, 52, 84);
        static readonly Color Fg = Color.FromArgb(235, 238, 242, 250), FgDim = Color.FromArgb(200, 170, 180, 200);
        static readonly Color Hover = Color.FromArgb(255, 58, 66, 86), CloseHover = Color.FromArgb(255, 210, 60, 60), Accent = Color.FromArgb(255, 70, 150, 255);

        static Graphics G(Bitmap b)
        {
            var g = Graphics.FromImage(b);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        /// <summary>Title bar: [lock] title .... [Pull][100%][Push] [–][□][✕]. widthPx = TitlePx * (bar width / bar height).
        /// distPct: the screen's distance as a percentage of the default distance in Settings.</summary>
        public static Bitmap TitleBar(int widthPx, string title, bool locked, TitleHit hover, bool maximized, bool active, int distPct)
        {
            int w = Math.Max((int)(TitlePx * (DistEnd + 2)), widthPx), h = TitlePx;
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = G(b))
            {
                g.Clear(Color.Transparent);
                using (var path = Rounded(new RectangleF(0, 0, w, h), 14))
                using (var br = new SolidBrush(active || hover != TitleHit.None ? BarActive : Bar)) g.FillPath(br, path);

                // buttons (right): minimize, maximize, close
                for (int i = 0; i < 3; i++)
                {
                    var cell = new RectangleF(w - (3 - i) * h, 0, h, h);
                    var which = (TitleHit)(1 + i);
                    if (hover == which)
                        using (var hb = new SolidBrush(which == TitleHit.Close ? CloseHover : Hover))
                        using (var p = Rounded(new RectangleF(cell.X + 4, cell.Y + 4, cell.Width - 8, cell.Height - 8), 10)) g.FillPath(hb, p);
                    using (var pen = new Pen(Fg, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        float cx = cell.X + h / 2f, cy = h / 2f, s = h * 0.17f;
                        if (which == TitleHit.Minimize) g.DrawLine(pen, cx - s, cy + s * 0.6f, cx + s, cy + s * 0.6f);
                        else if (which == TitleHit.Maximize)
                        {
                            if (maximized) { g.DrawRectangle(pen, cx - s + 4, cy - s, s * 2 - 4, s * 2 - 4); g.DrawLine(pen, cx - s, cy - s + 6, cx - s, cy + s); }
                            else g.DrawRectangle(pen, cx - s, cy - s, s * 2, s * 2);
                        }
                        else { g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s); g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s); }
                    }
                }

                // distance: [Pull] 100% [Push] (Pull brings the screen closer, Push sends it farther away)
                using (var fb = new Font("Segoe UI Semibold", h * 0.30f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fp = new Font("Segoe UI", h * 0.32f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var tb = new SolidBrush(locked ? FgDim : Fg))
                {
                    var sfc = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    Func<double, double, RectangleF> cellAt = (from, to) => new RectangleF((float)(w - to * h), 0, (float)((to - from) * h), h);
                    var pull = cellAt(PullAt, DistEnd); var pct = cellAt(PctAt, PullAt); var push = cellAt(PushAt, PctAt);
                    foreach (var c in new[] { new { R = pull, Hit = TitleHit.Pull, Text = "Pull" }, new { R = push, Hit = TitleHit.Push, Text = "Push" } })
                    {
                        var inner = new RectangleF(c.R.X + 4, c.R.Y + 8, c.R.Width - 8, c.R.Height - 16);
                        using (var bg = new SolidBrush(hover == c.Hit && !locked ? Hover : Color.FromArgb(255, 44, 50, 66)))
                        using (var p = Rounded(inner, 10)) g.FillPath(bg, p);
                        g.DrawString(c.Text, fb, tb, c.R, sfc);
                    }
                    if (hover == TitleHit.DistPct && !locked)
                        using (var bg = new SolidBrush(Hover)) using (var p = Rounded(new RectangleF(pct.X + 4, pct.Y + 8, pct.Width - 8, pct.Height - 16), 10)) g.FillPath(bg, p);
                    using (var pb = new SolidBrush(locked ? FgDim : Accent)) g.DrawString(distPct + "%", fp, pb, pct, sfc);
                }

                // lock checkbox (left)
                var lc = new RectangleF(0, 0, h, h);
                if (hover == TitleHit.Lock) using (var hb = new SolidBrush(Hover)) using (var p = Rounded(new RectangleF(4, 4, h - 8, h - 8), 10)) g.FillPath(hb, p);
                DrawLock(g, lc, locked);

                // title
                using (var f = new Font("Segoe UI Semibold", h * 0.36f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var tb = new SolidBrush(Fg))
                {
                    var tr = new RectangleF(h + 6, 0, (float)(w - (DistEnd + 1) * h - 12), h);
                    var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                    g.DrawString(title, f, tb, tr, sf);
                }
            }
            return b;
        }

        static void DrawLock(Graphics g, RectangleF c, bool locked)
        {
            float cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2, s = c.Height * 0.16f;
            using (var pen = new Pen(locked ? Accent : FgDim, 3.5f))
            using (var fill = new SolidBrush(locked ? Accent : Color.Transparent))
            {
                var body = new RectangleF(cx - s * 1.2f, cy - s * 0.1f, s * 2.4f, s * 1.8f);
                if (locked) g.FillRectangle(fill, body); else g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
                // shackle: closed when locked, open (shifted) when unlocked
                float sx = locked ? cx - s * 0.75f : cx - s * 0.2f;
                g.DrawArc(pen, sx, cy - s * 1.4f, s * 1.5f, s * 2.4f, 180, 180);
                g.DrawLine(pen, sx + s * 1.5f, cy - s * 0.2f, sx + s * 1.5f, cy - s * 0.1f);
                if (locked) g.DrawLine(pen, sx, cy - s * 0.2f, sx, cy - s * 0.1f);
            }
        }

        // ---------------- dock ----------------
        public sealed class DockItem { public string Id, Label; public bool Accent, Disabled; }
        public const int DockPx = 96;

        /// <summary>Draws the dock; returns item rectangles in texture pixels for hit testing.</summary>
        public static Bitmap Dock(List<DockItem> items, string hoverId, string status, out List<RectangleF> rects)
        {
            rects = new List<RectangleF>();
            int h = DockPx, pad = 14;
            using (var f = new Font("Segoe UI Semibold", h * 0.27f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fs = new Font("Segoe UI", h * 0.22f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var tmp = new Bitmap(1, 1)) using (var mg = Graphics.FromImage(tmp))
            {
                float x = pad;
                var widths = new List<float>();
                float statusW = mg.MeasureString(status, fs).Width + 40;
                x += statusW + 8;
                foreach (var it in items) { float iw = Math.Max(h * 0.9f, mg.MeasureString(it.Label, f).Width + 36); widths.Add(iw); x += iw + 8; }
                int w = (int)Math.Ceiling(x + pad);
                var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = G(b))
                {
                    g.Clear(Color.Transparent);
                    using (var p = Rounded(new RectangleF(0, 0, w, h), 26)) using (var br = new SolidBrush(Color.FromArgb(225, 20, 24, 34))) g.FillPath(br, p);
                    var sfc = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    float cx = pad;
                    var sr = new RectangleF(cx, 12, statusW, h - 24);
                    using (var p = Rounded(sr, 16)) using (var br = new SolidBrush(Color.FromArgb(255, 36, 42, 58))) g.FillPath(br, p);
                    using (var tb = new SolidBrush(FgDim)) g.DrawString(status, fs, tb, sr, sfc);
                    cx += statusW + 8;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        var r = new RectangleF(cx, 12, widths[i], h - 24);
                        rects.Add(r);
                        Color bg = it.Id == hoverId && !it.Disabled ? (it.Accent ? Color.FromArgb(255, 90, 165, 255) : Hover) : (it.Accent ? Accent : Color.FromArgb(255, 44, 50, 66));
                        using (var p = Rounded(r, 16)) using (var br = new SolidBrush(bg)) g.FillPath(br, p);
                        using (var tb = new SolidBrush(it.Disabled ? Color.FromArgb(120, 200, 200, 210) : Fg)) g.DrawString(it.Label, f, tb, r, sfc);
                        cx += widths[i] + 8;
                    }
                }
                return b;
            }
        }

        // ---------------- cursor ----------------
        /// <summary>Default AR arrow (hotspot 0,0), 64x64.</summary>
        public static Bitmap Arrow()
        {
            var b = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using (var g = G(b))
            {
                g.Clear(Color.Transparent);
                var pts = new[] { new PointF(3, 3), new PointF(3, 50), new PointF(15, 39), new PointF(24, 58), new PointF(32, 54), new PointF(23, 36), new PointF(39, 36) };
                using (var br = new SolidBrush(Color.White)) g.FillPolygon(br, pts);
                using (var pen = new Pen(Color.Black, 3f) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, pts);
            }
            return b;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr h, int cx, int cy, int step, IntPtr brush, int flags);

        /// <summary>
        /// Renders a Windows cursor (incl. monochrome/XOR ones like the I-beam) to a 64x64 ARGB bitmap by drawing it
        /// on black and on white and recovering alpha from the difference. Scaled x2 for visibility.
        /// </summary>
        public static Bitmap FromCursor(IntPtr hCursor, out int hotX, out int hotY)
        {
            hotX = hotY = 0;
            ICONINFO ii;
            if (hCursor == IntPtr.Zero || !GetIconInfo(hCursor, out ii)) return null;
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            const int S = 32, K = 2;
            hotX = ii.xHotspot * K; hotY = ii.yHotspot * K;
            using (var black = new DibSurface(S, S)) using (var white = new DibSurface(S, S))
            {
                Fill(black, 0x000000); Fill(white, 0xFFFFFF);
                DrawIconEx(black.Hdc, 0, 0, hCursor, S, S, 0, IntPtr.Zero, 3);
                DrawIconEx(white.Hdc, 0, 0, hCursor, S, S, 0, IntPtr.Zero, 3);
                var bmp = new Bitmap(S * K, S * K, PixelFormat.Format32bppArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, S * K, S * K), ImageLockMode.WriteOnly, bmp.PixelFormat);
                try
                {
                    var bk = new byte[S * S * 4]; var wh = new byte[S * S * 4]; var outp = new byte[S * K * S * K * 4];
                    Marshal.Copy(black.Bits, bk, 0, bk.Length); Marshal.Copy(white.Bits, wh, 0, wh.Length);
                    for (int y = 0; y < S * K; y++)
                        for (int x = 0; x < S * K; x++)
                        {
                            int si = ((y / K) * S + x / K) * 4, di = (y * S * K + x) * 4;
                            int a = 255 - Math.Max(wh[si] - bk[si], Math.Max(wh[si + 1] - bk[si + 1], wh[si + 2] - bk[si + 2]));
                            a = Math.Max(0, Math.Min(255, a));
                            bool xor = bk[si] > wh[si] || bk[si + 1] > wh[si + 1];  // inverted pixel (I-beam): show as white with dark edge
                            if (xor) { outp[di] = outp[di + 1] = outp[di + 2] = 255; outp[di + 3] = 255; continue; }
                            for (int c = 0; c < 3; c++) outp[di + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, bk[si + c] * 255 / a);
                            outp[di + 3] = (byte)a;
                        }
                    Marshal.Copy(outp, 0, bd.Scan0, outp.Length);
                }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }
        }

        static void Fill(DibSurface s, int rgb)
        {
            var row = new int[s.Width]; for (int i = 0; i < row.Length; i++) row[i] = rgb;
            for (int y = 0; y < s.Height; y++) Marshal.Copy(row, 0, s.Bits + y * s.Stride, s.Width);
        }

        // ---------------- GL upload ----------------
        /// <summary>Uploads an ARGB bitmap into a (new or existing) GL texture with mipmaps. Returns the texture.</summary>
        public static uint Upload(Bitmap b, uint tex)
        {
            if (tex == 0)
            {
                Gl.GenTextures(1, out tex);
                Gl.BindTexture(Gl.TEXTURE_2D, tex);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.MinFilter);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            }
            else Gl.BindTexture(Gl.TEXTURE_2D, tex);
            var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Gl.TexImage2D(Gl.TEXTURE_2D, 0, 0x8058 /*RGBA8*/, b.Width, b.Height, 0, Gl.BGRA, Gl.UNSIGNED_BYTE, bd.Scan0); Gl.GenerateMipmap(); }
            finally { b.UnlockBits(bd); }
            return tex;
        }
    }
}

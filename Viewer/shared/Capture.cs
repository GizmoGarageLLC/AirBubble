using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using XrealDesktop.Net;

namespace XrealDesktop.Host
{
    /// <summary>A 32bpp top-down GDI DIB section we can blit into and read directly.</summary>
    public sealed class DibSurface : IDisposable
    {
        public readonly int Width, Height, Stride;
        public readonly IntPtr Hdc, Bits;
        readonly IntPtr m_Bitmap, m_Old;

        public DibSurface(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            Stride = Width * 4;
            var bmi = new BITMAPINFOHEADER();
            bmi.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.biWidth = Width;
            bmi.biHeight = -Height; // top-down
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            Hdc = Native.CreateCompatibleDC(screen);
            IntPtr bits;
            m_Bitmap = Native.CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            if (m_Bitmap == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection failed");
            Bits = bits;
            m_Old = Native.SelectObject(Hdc, m_Bitmap);
        }

        public void Dispose()
        {
            Native.SelectObject(Hdc, m_Old);
            Native.DeleteObject(m_Bitmap);
            Native.DeleteDC(Hdc);
        }
    }

    public static class Capturer
    {
        /// <summary>Copies a desktop rectangle into dst, scaling if sizes differ.</summary>
        public static bool CaptureScreenRect(RECT r, DibSurface dst)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            try
            {
                if (r.Width == dst.Width && r.Height == dst.Height)
                    return Native.BitBlt(dst.Hdc, 0, 0, dst.Width, dst.Height, screen, r.Left, r.Top, Native.SRCCOPY);
                Native.SetStretchBltMode(dst.Hdc, Native.HALFTONE);
                Native.SetBrushOrgEx(dst.Hdc, 0, 0, IntPtr.Zero);
                return Native.StretchBlt(dst.Hdc, 0, 0, dst.Width, dst.Height, screen, r.Left, r.Top, r.Width, r.Height, Native.SRCCOPY);
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screen); }
        }

        /// <summary>
        /// Captures a window even when it is partially covered (PrintWindow with full DWM content).
        /// 'scratch' is a reusable full-size surface owned by the caller.
        /// </summary>
        public static bool CaptureWindow(IntPtr hwnd, ref DibSurface scratch, DibSurface dst)
        {
            RECT wr;
            if (!Native.GetWindowRect(hwnd, out wr) || wr.Width <= 0 || wr.Height <= 0) return false;
            RECT fr = Native.FrameBounds(hwnd);
            if (scratch == null || scratch.Width != wr.Width || scratch.Height != wr.Height)
            {
                if (scratch != null) scratch.Dispose();
                scratch = new DibSurface(wr.Width, wr.Height);
            }
            if (!Native.PrintWindow(hwnd, scratch.Hdc, Native.PW_RENDERFULLCONTENT))
            {
                // Some windows refuse PrintWindow; fall back to grabbing what is visible on screen.
                return CaptureScreenRect(fr, dst);
            }
            int ox = fr.Left - wr.Left, oy = fr.Top - wr.Top;
            int fw = Math.Min(fr.Width, wr.Width - ox), fh = Math.Min(fr.Height, wr.Height - oy);
            if (fw <= 0 || fh <= 0) return false;
            if (fw == dst.Width && fh == dst.Height)
                return Native.BitBlt(dst.Hdc, 0, 0, fw, fh, scratch.Hdc, ox, oy, Native.SRCCOPY);
            Native.SetStretchBltMode(dst.Hdc, Native.HALFTONE);
            Native.SetBrushOrgEx(dst.Hdc, 0, 0, IntPtr.Zero);
            return Native.StretchBlt(dst.Hdc, 0, 0, dst.Width, dst.Height, scratch.Hdc, ox, oy, fw, fh, Native.SRCCOPY);
        }
    }

    /// <summary>
    /// Splits a frame into tiles, hashes them, and JPEG-encodes only the tiles that changed since the
    /// last frame that was sent. Typical desktop work (typing, scrolling a pane) only touches a few tiles,
    /// which keeps both bandwidth and the decode cost on the glasses side low.
    /// </summary>
    public sealed class TileEncoder
    {
        public const int TileSize = 256;
        static readonly ImageCodecInfo s_Jpeg = FindJpeg();

        int m_W, m_H, m_Cols, m_Rows;
        ulong[] m_Hashes;

        static ImageCodecInfo FindJpeg()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            throw new InvalidOperationException("No JPEG encoder");
        }

        public void Reset() { m_Hashes = null; }

        public unsafe List<TileData> Encode(DibSurface s, int quality, out bool keyframe)
        {
            if (m_Hashes == null || m_W != s.Width || m_H != s.Height)
            {
                m_W = s.Width; m_H = s.Height;
                m_Cols = (m_W + TileSize - 1) / TileSize;
                m_Rows = (m_H + TileSize - 1) / TileSize;
                m_Hashes = null;
            }
            keyframe = m_Hashes == null;
            var hashes = new ulong[m_Cols * m_Rows];
            var changed = new List<int>();
            byte* basePtr = (byte*)s.Bits;
            for (int ty = 0; ty < m_Rows; ty++)
            {
                for (int tx = 0; tx < m_Cols; tx++)
                {
                    int x0 = tx * TileSize, y0 = ty * TileSize;
                    int w = Math.Min(TileSize, m_W - x0), h = Math.Min(TileSize, m_H - y0);
                    ulong hash = 14695981039346656037UL;
                    for (int y = 0; y < h; y++)
                    {
                        ulong* row = (ulong*)(basePtr + (y0 + y) * s.Stride + x0 * 4);
                        int pairs = w >> 1;
                        for (int i = 0; i < pairs; i++)
                        {
                            hash ^= row[i] & 0x00FFFFFF00FFFFFFUL; // ignore the undefined alpha byte
                            hash *= 1099511628211UL;
                        }
                        if ((w & 1) != 0)
                        {
                            hash ^= ((uint*)row)[w - 1] & 0x00FFFFFFu;
                            hash *= 1099511628211UL;
                        }
                    }
                    int idx = ty * m_Cols + tx;
                    hashes[idx] = hash;
                    if (keyframe || m_Hashes[idx] != hash) changed.Add(idx);
                }
            }
            m_Hashes = hashes;

            var tiles = new TileData[changed.Count];
            var codecParams = new EncoderParameters(1);
            codecParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            int cols = m_Cols, W = m_W, H = m_H, stride = s.Stride;
            IntPtr bits = s.Bits;
            var po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
            Parallel.For(0, changed.Count, po, delegate(int i)
            {
                int idx = changed[i];
                int x0 = (idx % cols) * TileSize, y0 = (idx / cols) * TileSize;
                int w = Math.Min(TileSize, W - x0), h = Math.Min(TileSize, H - y0);
                IntPtr scan0 = new IntPtr(bits.ToInt64() + (long)y0 * stride + x0 * 4);
                using (var bmp = new Bitmap(w, h, stride, PixelFormat.Format32bppRgb, scan0))
                using (var ms = new MemoryStream(16 * 1024))
                {
                    bmp.Save(ms, s_Jpeg, codecParams);
                    var t = new TileData();
                    t.X = (ushort)x0; t.Y = (ushort)y0; t.W = (ushort)w; t.H = (ushort)h;
                    t.Jpeg = ms.ToArray();
                    tiles[i] = t;
                }
            });
            return new List<TileData>(tiles);
        }
    }
}

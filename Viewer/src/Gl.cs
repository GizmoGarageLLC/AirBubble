using System;
using System.Runtime.InteropServices;

namespace XrealDesktop.PcViewer
{
    /// <summary>Minimal OpenGL 1.x / WGL interop. Fixed-function GL is plenty for a few textured quads.</summary>
    static class Gl
    {
        const string GL = "opengl32.dll";

        public const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
        public const uint TEXTURE_2D = 0x0DE1, BLEND = 0x0BE2, DEPTH_TEST = 0x0B71, CULL_FACE = 0x0B44;
        public const uint SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
        public const uint PROJECTION = 0x1701, MODELVIEW = 0x1700;
        public const uint QUADS = 0x0007, LINE_LOOP = 0x0002, LINES = 0x0001;
        public const uint TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
        public const int NEAREST = 0x2600, LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, CLAMP_TO_EDGE = 0x812F;
        public const uint GENERATE_MIPMAP = 0x8191, TEXTURE_MAX_ANISOTROPY = 0x84FE;
        public const int RGB8 = 0x8051;
        public const uint BGRA = 0x80E1, UNSIGNED_BYTE = 0x1401;
        public const uint UNPACK_ALIGNMENT = 0x0CF5;

        [DllImport(GL, EntryPoint = "glClear")] public static extern void Clear(uint mask);
        [DllImport(GL, EntryPoint = "glClearColor")] public static extern void ClearColor(float r, float g, float b, float a);
        [DllImport(GL, EntryPoint = "glViewport")] public static extern void Viewport(int x, int y, int w, int h);
        [DllImport(GL, EntryPoint = "glMatrixMode")] public static extern void MatrixMode(uint mode);
        [DllImport(GL, EntryPoint = "glLoadIdentity")] public static extern void LoadIdentity();
        [DllImport(GL, EntryPoint = "glFrustum")] public static extern void Frustum(double l, double r, double b, double t, double n, double f);
        [DllImport(GL, EntryPoint = "glOrtho")] public static extern void Ortho(double l, double r, double b, double t, double n, double f);
        [DllImport(GL, EntryPoint = "glMultMatrixd")] public static extern void MultMatrixd(double[] m);
        [DllImport(GL, EntryPoint = "glPushMatrix")] public static extern void PushMatrix();
        [DllImport(GL, EntryPoint = "glPopMatrix")] public static extern void PopMatrix();
        [DllImport(GL, EntryPoint = "glRotated")] public static extern void Rotated(double a, double x, double y, double z);
        [DllImport(GL, EntryPoint = "glTranslated")] public static extern void Translated(double x, double y, double z);
        [DllImport(GL, EntryPoint = "glEnable")] public static extern void Enable(uint cap);
        [DllImport(GL, EntryPoint = "glDisable")] public static extern void Disable(uint cap);
        [DllImport(GL, EntryPoint = "glBlendFunc")] public static extern void BlendFunc(uint s, uint d);
        [DllImport(GL, EntryPoint = "glAlphaFunc")] public static extern void AlphaFunc(uint func, float reference);
        public const uint ALPHA_TEST = 0x0BC0, GREATER = 0x0204;
        [DllImport(GL, EntryPoint = "glBegin")] public static extern void Begin(uint mode);
        [DllImport(GL, EntryPoint = "glEnd")] public static extern void End();
        [DllImport(GL, EntryPoint = "glVertex3d")] public static extern void Vertex3d(double x, double y, double z);
        [DllImport(GL, EntryPoint = "glVertex2d")] public static extern void Vertex2d(double x, double y);
        [DllImport(GL, EntryPoint = "glTexCoord2d")] public static extern void TexCoord2d(double s, double t);
        [DllImport(GL, EntryPoint = "glColor4d")] public static extern void Color4d(double r, double g, double b, double a);
        [DllImport(GL, EntryPoint = "glLineWidth")] public static extern void LineWidth(float w);
        [DllImport(GL, EntryPoint = "glGenTextures")] public static extern void GenTextures(int n, out uint tex);
        [DllImport(GL, EntryPoint = "glDeleteTextures")] public static extern void DeleteTextures(int n, ref uint tex);
        [DllImport(GL, EntryPoint = "glBindTexture")] public static extern void BindTexture(uint target, uint tex);
        [DllImport(GL, EntryPoint = "glTexParameteri")] public static extern void TexParameteri(uint target, uint name, int value);
        [DllImport(GL, EntryPoint = "glTexParameterf")] public static extern void TexParameterf(uint target, uint name, float value);
        [DllImport(GL, EntryPoint = "glPixelStorei")] public static extern void PixelStorei(uint name, int value);
        [DllImport(GL, EntryPoint = "glTexImage2D")] public static extern void TexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr data);
        [DllImport(GL, EntryPoint = "glTexSubImage2D")] public static extern void TexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, IntPtr data);
        [DllImport(GL, EntryPoint = "glRasterPos2d")] public static extern void RasterPos2d(double x, double y);
        [DllImport(GL, EntryPoint = "glListBase")] public static extern void ListBase(uint b);
        [DllImport(GL, EntryPoint = "glCallLists")] public static extern void CallLists(int n, uint type, byte[] lists);
        [DllImport(GL, EntryPoint = "glGetError")] public static extern uint GetError();
        [DllImport(GL, EntryPoint = "glGetString")] static extern IntPtr GetStringPtr(uint name);
        public static string GetString(uint name) { var p = GetStringPtr(name); return p == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(p); }
        public const uint VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02;

        [DllImport(GL)] public static extern IntPtr wglCreateContext(IntPtr hdc);
        [DllImport(GL)] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr ctx);
        [DllImport(GL)] public static extern bool wglDeleteContext(IntPtr ctx);
        [DllImport(GL)] public static extern IntPtr wglGetProcAddress(string name);
        [DllImport(GL)] public static extern bool wglUseFontBitmapsW(IntPtr hdc, uint first, uint count, uint listBase);
        [DllImport(GL, EntryPoint = "glGenLists")] public static extern uint GenLists(int range);

        // glGenerateMipmap (GL 3.0). The old automatic GL_GENERATE_MIPMAP flag is unreliable on some drivers:
        // after glTexSubImage2D the smaller mip levels can keep the FIRST image, which shows as a ghost/"burn-in"
        // whenever a screen is drawn smaller than its real resolution. So mipmaps are rebuilt explicitly.
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void GenerateMipmapFn(uint target);
        static GenerateMipmapFn s_GenMip;
        public static bool HasGenerateMipmap { get { return s_GenMip != null; } }
        /// <summary>Call once after the context is current.</summary>
        public static void LoadExtensions()
        {
            IntPtr p = wglGetProcAddress("glGenerateMipmap");
            if (p == IntPtr.Zero || p == (IntPtr)1 || p == (IntPtr)2 || p == (IntPtr)3 || p == (IntPtr)(-1)) p = wglGetProcAddress("glGenerateMipmapEXT");
            if (p != IntPtr.Zero && p != (IntPtr)1 && p != (IntPtr)2 && p != (IntPtr)3 && p != (IntPtr)(-1))
                s_GenMip = (GenerateMipmapFn)Marshal.GetDelegateForFunctionPointer(p, typeof(GenerateMipmapFn));
        }
        /// <summary>Rebuilds the bound texture's mip levels (no-op if unsupported; textures then use plain LINEAR).</summary>
        public static void GenerateMipmap() { if (s_GenMip != null) s_GenMip(TEXTURE_2D); }
        /// <summary>Min filter to use: trilinear when mipmaps can be rebuilt, else plain bilinear (no stale levels).</summary>
        public static int MinFilter { get { return s_GenMip != null ? LINEAR_MIPMAP_LINEAR : LINEAR; } }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool SwapIntervalFn(int interval);
        public static bool SetSwapInterval(int interval)
        {
            IntPtr p = wglGetProcAddress("wglSwapIntervalEXT");
            if (p == IntPtr.Zero) return false;
            var fn = (SwapIntervalFn)Marshal.GetDelegateForFunctionPointer(p, typeof(SwapIntervalFn));
            return fn(interval);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift,
            cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
            cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved; public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }
        [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr hdc);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
            uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);

        /// <summary>Creates a double-buffered RGBA context on the window DC and makes it current.</summary>
        public static IntPtr CreateContext(IntPtr hdc)
        {
            var pfd = new PIXELFORMATDESCRIPTOR();
            pfd.nSize = (ushort)Marshal.SizeOf(typeof(PIXELFORMATDESCRIPTOR));
            pfd.nVersion = 1;
            pfd.dwFlags = 0x4 | 0x20 | 0x1; // DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER
            pfd.iPixelType = 0; pfd.cColorBits = 32; pfd.cDepthBits = 24;
            int fmt = ChoosePixelFormat(hdc, ref pfd);
            if (fmt == 0 || !SetPixelFormat(hdc, fmt, ref pfd)) throw new Exception("SetPixelFormat failed");
            IntPtr ctx = wglCreateContext(hdc);
            if (ctx == IntPtr.Zero || !wglMakeCurrent(hdc, ctx)) throw new Exception("wglCreateContext failed");
            return ctx;
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// DXGI Desktop Duplication for one monitor, via raw COM vtable calls (no SharpDX / SDK needed).
    /// The GPU hands us each new desktop frame, so this is far cheaper than GDI BitBlt (which forces a
    /// DWM readback) and runs at the desktop's own refresh rate. The cursor is not included; the caller
    /// draws it.
    ///
    /// Interface IDs and vtable slots are from the DXGI/D3D11 IDL (IUnknown 0-2, IDXGIObject 3-6).
    /// </summary>
    public sealed class DesktopDuplication : IDisposable
    {
        static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
            uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        // ---- vtable call helpers ----
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnQI(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint FnRelease(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnEnum(IntPtr self, uint idx, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGetDesc(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnDuplicateOutput(IntPtr self, IntPtr device, out IntPtr dup);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnAcquire(IntPtr self, uint timeoutMs, IntPtr frameInfo, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnReleaseFrame(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnCreateTexture2D(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FnCopyResource(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnMap(IntPtr self, IntPtr res, uint sub, int mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FnUnmap(IntPtr self, IntPtr res, uint sub);

        static T Vt<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T)) as T;
        }
        static void Release(ref IntPtr p) { if (p != IntPtr.Zero) { Vt<FnRelease>(p, 2)(p); p = IntPtr.Zero; } }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }

        // ---- state ----
        IntPtr m_Device, m_Context, m_Output1, m_Dup, m_Staging;
        readonly IntPtr m_FrameInfo = Marshal.AllocHGlobal(64);
        readonly string m_DeviceName;
        public int Width, Height;
        public string Error;
        public long Frames, Dropped;     // delivered vs. skipped desktop frames (AccumulatedFrames > 1)
        public long LastPresentQpc;      // QueryPerformanceCounter time Windows presented the last frame

        // Cached delegates (hot path).
        FnAcquire m_Acquire; FnReleaseFrame m_ReleaseFrame; FnCopyResource m_Copy; FnMap m_Map; FnUnmap m_Unmap;

        public DesktopDuplication(string deviceName) { m_DeviceName = deviceName; }

        /// <summary>Finds the output for the monitor, creates a D3D11 device on its adapter and starts duplication.</summary>
        public bool Init()
        {
            Cleanup();
            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid g = IID_IDXGIFactory1;
                int hr = CreateDXGIFactory1(ref g, out factory);
                if (hr < 0) { Error = "CreateDXGIFactory1 0x" + hr.ToString("X8"); return false; }

                IntPtr desc = Marshal.AllocHGlobal(256);
                try
                {
                    for (uint a = 0; m_Output1 == IntPtr.Zero; a++)
                    {
                        IntPtr adapter;
                        if (Vt<FnEnum>(factory, 12)(factory, a, out adapter) < 0) break;   // EnumAdapters1
                        try
                        {
                            for (uint o = 0; ; o++)
                            {
                                IntPtr output;
                                if (Vt<FnEnum>(adapter, 7)(adapter, o, out output) < 0) break; // EnumOutputs
                                try
                                {
                                    Vt<FnGetDesc>(output, 7)(output, desc);                     // IDXGIOutput::GetDesc
                                    string name = Marshal.PtrToStringUni(desc);                   // DeviceName[32] first
                                    if (!string.Equals(name, m_DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                                    int left = Marshal.ReadInt32(desc, 64), top = Marshal.ReadInt32(desc, 68);
                                    int right = Marshal.ReadInt32(desc, 72), bottom = Marshal.ReadInt32(desc, 76);
                                    Width = right - left; Height = bottom - top;

                                    Guid io1 = IID_IDXGIOutput1;
                                    hr = Vt<FnQI>(output, 0)(output, ref io1, out m_Output1);
                                    if (hr < 0) { Error = "QI IDXGIOutput1 0x" + hr.ToString("X8"); return false; }
                                    int fl;
                                    hr = D3D11CreateDevice(adapter, 0 /*UNKNOWN*/, IntPtr.Zero, 0x20 /*BGRA_SUPPORT*/, IntPtr.Zero, 0, 7, out m_Device, out fl, out m_Context);
                                    if (hr < 0) { Error = "D3D11CreateDevice 0x" + hr.ToString("X8"); return false; }
                                    break;
                                }
                                finally { Release(ref output); }
                            }
                        }
                        finally { Release(ref adapter); }
                    }
                }
                finally { Marshal.FreeHGlobal(desc); }
                if (m_Output1 == IntPtr.Zero) { Error = "no DXGI output for " + m_DeviceName; return false; }

                hr = Vt<FnDuplicateOutput>(m_Output1, 22)(m_Output1, m_Device, out m_Dup);
                if (hr < 0) { Error = "DuplicateOutput 0x" + hr.ToString("X8"); return false; }

                var td = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = 87 /*B8G8R8A8_UNORM*/,
                    SampleCount = 1, Usage = 3 /*STAGING*/, CPUAccessFlags = 0x20000 /*READ*/
                };
                hr = Vt<FnCreateTexture2D>(m_Device, 5)(m_Device, ref td, IntPtr.Zero, out m_Staging);
                if (hr < 0) { Error = "CreateTexture2D 0x" + hr.ToString("X8"); return false; }

                m_Acquire = Vt<FnAcquire>(m_Dup, 8);
                m_ReleaseFrame = Vt<FnReleaseFrame>(m_Dup, 14);
                m_Copy = Vt<FnCopyResource>(m_Context, 47);
                m_Map = Vt<FnMap>(m_Context, 14);
                m_Unmap = Vt<FnUnmap>(m_Context, 15);
                Error = null;
                return true;
            }
            catch (Exception e) { Error = e.Message; return false; }
            finally { Release(ref factory); if (Error != null) Cleanup(); }
        }

        /// <summary>
        /// Waits up to timeoutMs for a new desktop frame and copies it (BGRA, Width*4 stride) into dst.
        /// Returns 1 = new frame copied, 0 = no change, -1 = duplication lost (call Init again).
        /// </summary>
        public int Acquire(uint timeoutMs, IntPtr dst)
        {
            if (m_Dup == IntPtr.Zero) return -1;
            IntPtr res;
            int hr = m_Acquire(m_Dup, timeoutMs, m_FrameInfo, out res);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 0;
            if (hr < 0) { Error = "AcquireNextFrame 0x" + hr.ToString("X8"); return -1; }
            try
            {
                long lastPresent = Marshal.ReadInt64(m_FrameInfo, 0);
                if (lastPresent == 0) return 0;   // only the pointer changed
                int acc = Marshal.ReadInt32(m_FrameInfo, 16);
                LastPresentQpc = lastPresent;
                Frames++; if (acc > 1) Dropped += acc - 1;

                IntPtr tex; Guid it = IID_ID3D11Texture2D;
                hr = Vt<FnQI>(res, 0)(res, ref it, out tex);
                if (hr < 0) { Error = "QI ID3D11Texture2D 0x" + hr.ToString("X8"); return -1; }
                try { m_Copy(m_Context, m_Staging, tex); }
                finally { Release(ref tex); }

                D3D11_MAPPED_SUBRESOURCE map;
                hr = m_Map(m_Context, m_Staging, 0, 1 /*READ*/, 0, out map);
                if (hr < 0) { Error = "Map 0x" + hr.ToString("X8"); return -1; }
                try
                {
                    int row = Width * 4;
                    if (map.RowPitch == row) CopyMemory(dst, map.pData, (IntPtr)(row * Height));
                    else for (int y = 0; y < Height; y++) CopyMemory(dst + y * row, map.pData + (int)(y * map.RowPitch), (IntPtr)row);
                }
                finally { m_Unmap(m_Context, m_Staging, 0); }
                return 1;
            }
            finally { Release(ref res); m_ReleaseFrame(m_Dup); }
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr dst, IntPtr src, IntPtr len);

        void Cleanup()
        {
            Release(ref m_Staging); Release(ref m_Dup); Release(ref m_Output1); Release(ref m_Context); Release(ref m_Device);
        }

        public void Dispose() { Cleanup(); Marshal.FreeHGlobal(m_FrameInfo); }
    }
}

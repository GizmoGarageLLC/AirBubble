using System;
using System.Runtime.InteropServices;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// Finds supported XREAL glasses and their HID interface paths.
    /// Product IDs and interface numbers from the open-source xrealAirLinuxDriver (hid_ids.c).
    /// Only the original Air has been verified on hardware with this viewer.
    /// </summary>
    public sealed class XrealDevice
    {
        public const ushort Vid = 0x3318;
        sealed class Model { public ushort Pid; public string Name; public int ImuIf, McuIf; }
        static readonly Model[] Models =
        {
            new Model { Pid = 0x0424, Name = "XREAL Air",          ImuIf = 3, McuIf = 4 },
            new Model { Pid = 0x0428, Name = "XREAL Air 2",        ImuIf = 3, McuIf = 4 },
            new Model { Pid = 0x0432, Name = "XREAL Air 2 Pro",    ImuIf = 3, McuIf = 4 },
            new Model { Pid = 0x0426, Name = "XREAL Air 2 Ultra",  ImuIf = 2, McuIf = 0 },
        };

        public string Name, ImuPath, McuPath;
        public ushort Pid;

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr di, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        public static XrealDevice Find()
        {
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            XrealDevice found = null;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4), lp = path.ToLowerInvariant();
                        if (!lp.Contains("vid_3318")) continue;
                        foreach (var m in Models)
                        {
                            if (!lp.Contains("pid_" + m.Pid.ToString("x4"))) continue;
                            if (found == null) found = new XrealDevice { Name = m.Name, Pid = m.Pid };
                            if (found.Pid != m.Pid) continue;
                            if (lp.Contains("&mi_" + m.ImuIf.ToString("x2"))) found.ImuPath = path;
                            if (lp.Contains("&mi_" + m.McuIf.ToString("x2"))) found.McuPath = path;
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return found;
        }
    }
}

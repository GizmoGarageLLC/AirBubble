// Installs / removes the bundled Virtual Display Driver (MIT, github.com/VirtualDrivers/Virtual-Display-Driver).
// Must run elevated. Equivalent of "devcon install MttVDD.inf Root\MttVDD", done with SetupAPI so no extra tools ship.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace XrealDesktop.Setup
{
    static class VddInstall
    {
        public const string HardwareId = @"Root\MttVDD";
        static readonly Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        public static string ConfigDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XrealPcViewer", "VirtualDisplayDriver"); } }
        const string VddRegKey = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid g, string desc, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int idx, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA d, int prop, out int type, byte[] buf, int size, out int req);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA d, int prop, byte[] buf, int size);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(int fn, IntPtr set, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA d, int scope, int profile, int keyType, int access);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupUninstallOEMInf(string inf, int flags, IntPtr reserved);
        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hwid, string inf, int flags, out bool reboot);

        [StructLayout(LayoutKind.Sequential)]
        struct SP_PROPCHANGE_PARAMS { public int HeaderSize; public int InstallFunction; public int StateChange; public int Scope; public int HwProfile; }
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SP_DEVINFO_DATA d, ref SP_PROPCHANGE_PARAMS p, int size);

        const int SPDRP_HARDWAREID = 1, DIF_REGISTERDEVICE = 0x19, DIF_REMOVE = 5, DICD_GENERATE_ID = 1, INSTALLFLAG_FORCE = 1;
        const int DIF_PROPERTYCHANGE = 0x12, DICS_PROPCHANGE = 3, DICS_FLAG_GLOBAL = 1;
        public const int MaxScreens = 8;

        /// <summary>Number of virtual screens in our driver config, or -1 if there is no config of ours.</summary>
        public static int ConfiguredCount()
        {
            try
            {
                var m = Regex.Match(File.ReadAllText(Path.Combine(ConfigDir, "vdd_settings.xml")), @"<count>\s*(\d+)\s*</count>");
                return m.Success ? int.Parse(m.Groups[1].Value) : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Changes the number of virtual screens of an installed driver: rewrites our config and restarts the device so the
        /// driver re-reads it (all virtual screens blink off and on). Must run elevated. Returns "" or an error.
        /// </summary>
        public static string SetScreens(int monitors, Action<string> log)
        {
            monitors = Math.Max(1, Math.Min(MaxScreens, monitors));
            string cfg = Path.Combine(ConfigDir, "vdd_settings.xml");
            if (!File.Exists(cfg)) return "no driver config at " + cfg;
            string xml = File.ReadAllText(cfg);
            File.WriteAllText(cfg, Regex.Replace(xml, @"<count>\s*\d+\s*</count>", "<count>" + monitors + "</count>"));
            log("Virtual display config: " + monitors + " screens");
            string err = "";
            int restarted = 0;
            ForEachDevice((set, d) =>
            {
                var p = new SP_PROPCHANGE_PARAMS { HeaderSize = 8, InstallFunction = DIF_PROPERTYCHANGE, StateChange = DICS_PROPCHANGE, Scope = DICS_FLAG_GLOBAL };
                var dd = d;
                if (!SetupDiSetClassInstallParams(set, ref dd, ref p, Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS))) ||
                    !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref dd))
                    err = "device restart failed (" + Marshal.GetLastWin32Error() + ")";
                else restarted++;
                return true;
            });
            if (err.Length == 0 && restarted == 0) err = "virtual display device not found";
            if (err.Length == 0) log("Virtual display device restarted");
            return err;
        }

        /// <summary>True if a device with our hardware ID exists (installed by us or by the driver's own app).</summary>
        public static bool IsInstalled() { bool found = false; ForEachDevice((set, d) => { found = true; return false; }); return found; }

        static void ForEachDevice(Func<IntPtr, SP_DEVINFO_DATA, bool> visit)
        {
            Guid g = DisplayClass;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0);   // all devices of the class, present or not
            if (set == new IntPtr(-1)) return;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    if (!SetupDiEnumDeviceInfo(set, i, ref d)) break;
                    var buf = new byte[2048]; int type, req;
                    if (!SetupDiGetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID, out type, buf, buf.Length, out req)) continue;
                    string ids = Encoding.Unicode.GetString(buf, 0, Math.Max(0, req));
                    bool match = false;
                    foreach (var id in ids.Split('\0')) if (string.Equals(id, HardwareId, StringComparison.OrdinalIgnoreCase)) match = true;
                    if (match && !visit(set, d)) break;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        /// <summary>Installs the driver from 'packageDir' with 'monitors' virtual monitors. Returns "" or an error.</summary>
        public static string Install(string packageDir, int monitors, Action<string> log)
        {
            // 1) Config: our own folder (readable by the driver host), pointed to via the driver's VDDPATH registry value.
            Directory.CreateDirectory(ConfigDir);
            string xml = File.ReadAllText(Path.Combine(packageDir, "vdd_settings.xml"));
            xml = Regex.Replace(xml, @"<count>\s*\d+\s*</count>", "<count>" + Math.Max(1, Math.Min(MaxScreens, monitors)) + "</count>");
            File.WriteAllText(Path.Combine(ConfigDir, "vdd_settings.xml"), xml);
            using (var k = Registry.LocalMachine.CreateSubKey(VddRegKey)) k.SetValue("VDDPATH", ConfigDir);
            log("Virtual display config: " + monitors + " screens");

            string inf = Path.Combine(packageDir, "MttVDD.inf");
            if (!IsInstalled())
            {
                using (var k = Registry.LocalMachine.CreateSubKey(VddRegKey)) k.SetValue("InstalledBy", "XrealPcViewer");
                // 2) Root-enumerated device node with our hardware ID.
                Guid g = DisplayClass;
                IntPtr set = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
                try
                {
                    var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    if (!SetupDiCreateDeviceInfo(set, "Display", ref g, null, IntPtr.Zero, DICD_GENERATE_ID, ref d))
                        return "create device failed (" + Marshal.GetLastWin32Error() + ")";
                    var hw = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                    if (!SetupDiSetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID, hw, hw.Length))
                        return "set hardware id failed (" + Marshal.GetLastWin32Error() + ")";
                    if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref d))
                        return "register device failed (" + Marshal.GetLastWin32Error() + ")";
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
                log("Created virtual display device");
            }
            // 3) Install (or update) the signed driver onto it. Windows may ask to trust the publisher (SignPath Foundation).
            bool reboot;
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, INSTALLFLAG_FORCE, out reboot))
                return "driver install failed (" + Marshal.GetLastWin32Error() + ")";
            log("Virtual display driver installed" + (reboot ? " (restart needed)" : ""));
            return "";
        }

        /// <summary>Removes the device(s) and the driver package from the driver store.</summary>
        public static bool InstalledByUs()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(VddRegKey))
                return k != null && (k.GetValue("InstalledBy") as string) == "XrealPcViewer";
        }

        public static void Remove(Action<string> log)
        {
            if (!InstalledByUs()) { log("Virtual display driver was not installed by this app; leaving it."); return; }
            ForEachDevice((set, d) =>
            {
                string infName = null;
                IntPtr hk = SetupDiOpenDevRegKey(set, ref d, 1 /*GLOBAL*/, 0, 2 /*DIREG_DRV*/, 0x20019 /*KEY_READ*/);
                if (hk != new IntPtr(-1) && hk != IntPtr.Zero)
                    using (var k = RegistryKey.FromHandle(new SafeRegistryHandle(hk, true))) infName = k.GetValue("InfPath") as string;
                log("device driver package: " + (infName ?? "(unknown)"));
                var dd = d;
                if (SetupDiCallClassInstaller(DIF_REMOVE, set, ref dd)) log("Removed virtual display device");
                else log("DIF_REMOVE failed (" + Marshal.GetLastWin32Error() + ")");
                if (!string.IsNullOrEmpty(infName) && infName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
                {
                    if (SetupUninstallOEMInf(infName, 1 /*SUOI_FORCEDELETE*/, IntPtr.Zero)) log("Removed driver package " + infName);
                    else log("SetupUninstallOEMInf(" + infName + ") failed (" + Marshal.GetLastWin32Error() + ")");
                }
                return true;
            });
            try { Registry.LocalMachine.DeleteSubKeyTree(VddRegKey, false); } catch { }
            try { if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, true); } catch { }
            try { var parent = Path.GetDirectoryName(ConfigDir); if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent); } catch { }
        }
    }
}

// ImuTest: reads the XREAL Air's IMU directly over USB HID on Windows (no SDK, no Android).
// Usage: ImuTest.exe [seconds] [csvPath]
// Packet layout verified on hardware: ts@4 gyroMul@12 gyroDiv@14 gyro@18/21/24 accMul@27 accDiv@29 acc@33/36/39.
// Protocol notes (from the open-source xrealAirLinuxDriver / AirAPI projects):
//   - IMU lives on HID interface 3 (MI_03) of VID 0x3318.
//   - Commands: 0xAA, crc32(LE), length(LE)=3+len, cmd, data...  (crc covers length..data)
//   - START_IMU_DATA = 0x19 with payload 0x01.
//   - IMU reports start 0x01 0x02; gyro/accel are 24-bit LE ints scaled by multiplier/divisor.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

static class ImuTest
{
    // ---------- Win32 / HID interop ----------
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;

    static List<string> FindHidPaths(string mustContain)
    {
        var result = new List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
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
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(buf + 4);
                        if (path.ToLowerInvariant().Contains(mustContain)) result.Add(path);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    // ---------- CRC32 (standard zlib polynomial) ----------
    static readonly uint[] CrcTable = BuildCrc();
    static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    static uint Crc32(byte[] b, int off, int len)
    {
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < len; i++) c = CrcTable[(c ^ b[off + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    // Builds a HID output report: [reportId=0][0xAA][crc32][len][cmd][data] padded to outLen.
    static byte[] BuildCommand(byte cmd, byte[] data, int outLen)
    {
        int packetLen = 3 + data.Length;
        var p = new byte[Math.Max(outLen, 1 + 5 + packetLen)];
        int o = 1;                       // byte 0 = report id 0
        p[o] = 0xAA;
        p[o + 5] = (byte)packetLen; p[o + 6] = (byte)(packetLen >> 8);
        p[o + 7] = cmd;
        Array.Copy(data, 0, p, o + 8, data.Length);
        uint crc = Crc32(p, o + 5, packetLen);
        p[o + 1] = (byte)crc; p[o + 2] = (byte)(crc >> 8); p[o + 3] = (byte)(crc >> 16); p[o + 4] = (byte)(crc >> 24);
        return p;
    }

    static int I24(byte[] b, int o) { int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16); return (v << 8) >> 8; }
    static short I16(byte[] b, int o) { return (short)(b[o] | (b[o + 1] << 8)); }
    static int I32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }

    static int Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 10;
        string csvPath = args.Length > 1 ? args[1] : null;

        var paths = FindHidPaths("vid_3318&pid_0424&mi_03");
        if (paths.Count == 0) { Console.WriteLine("FAIL: no XREAL Air IMU interface (VID_3318 PID_0424 MI_03) found."); return 2; }
        Console.WriteLine("IMU interface: " + paths[0]);

        var h = CreateFile(paths[0], GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine("FAIL: cannot open interface, Win32 error " + Marshal.GetLastWin32Error()); return 3; }

        IntPtr pp; HIDP_CAPS caps;
        HidD_GetPreparsedData(h, out pp); HidP_GetCaps(pp, out caps); HidD_FreePreparsedData(pp);
        Console.WriteLine("Report sizes: in=" + caps.InputReportByteLength + " out=" + caps.OutputReportByteLength);

        using (var fs = new FileStream(h, FileAccess.ReadWrite, 1, false))
        {
            var start = BuildCommand(0x19, new byte[] { 0x01 }, caps.OutputReportByteLength);
            try { fs.Write(start, 0, caps.OutputReportByteLength > 0 ? caps.OutputReportByteLength : start.Length); fs.Flush(); Console.WriteLine("Sent START_IMU_DATA"); }
            catch (Exception e) { Console.WriteLine("WARN: start command write failed: " + e.Message); }

            StreamWriter csv = csvPath != null ? new StreamWriter(csvPath) : null;
            if (csv != null) csv.WriteLine("t_s,gx_dps,gy_dps,gz_dps,ax_g,ay_g,az_g,intZ,intX,intY,phase,tempC,mx,my,mz");

            var buf = new byte[Math.Max((int)caps.InputReportByteLength, 65)];
            var sw = Stopwatch.StartNew();
            long imuPackets = 0, otherPackets = 0; ulong lastTs = 0; bool haveTs = false;
            double yaw = 0, pitch = 0, roll = 0;
            // Gyro bias estimated from the first 2 s (hold still).
            double bx = 0, by = 0, bz = 0; int biasN = 0; bool biasDone = false;
            double nextPrint = 0;

            // Read on a worker thread so a silent device can't hang us past the deadline.
            Exception readErr = null;
            var q = new System.Collections.Concurrent.BlockingCollection<byte[]>();
            var reader = new Thread(() =>
            {
                try { while (true) { int n = fs.Read(buf, 0, buf.Length); if (n <= 0) break; var c = new byte[n]; Array.Copy(buf, c, n); q.Add(c); } }
                catch (Exception e) { readErr = e; }
            }) { IsBackground = true };
            reader.Start();

            // Guided mode (IMU_GUIDED=1): spoken cues so the wearer can follow along. Times in seconds.
            string guidedMode = Environment.GetEnvironmentVariable("IMU_GUIDED");
            bool guided = guidedMode != null;
            bool yawMode = guidedMode == "yaw", driftMode = guidedMode == "drift", waveMode = guidedMode == "wave";
            double biasStart = guided ? (driftMode ? 9 : 5) : 0, biasEnd = guided ? (driftMode ? 27 : yawMode ? 9 : 8) : 2;
            if (waveMode) driftMode = false;
            var cueT = waveMode ? new double[] { 0, 8, 20, 33 } : driftMode ? new double[] { 0, 8, 28, 34, 40, 46, 52, 57, 62, 67, 87 }
                     : yawMode ? new double[] { 0, 5, 9, 14, 19, 24, 29 } : new double[] { 0, 5, 8, 14, 20, 26, 32 };
            var cueS = waveMode ? new string[] {
                "Compass calibration. Take the glasses off and hold them in your hand, away from the laptop. Starting in eight seconds.",
                "Now slowly rotate the glasses in every direction. Draw a big figure eight, turn them upside down, and on their sides.",
                "Keep going, every direction.",
                "Done. Thank you. You can put the glasses back on." }
                : driftMode ? new string[] {
                "Drift test. Starting in eight seconds. Put the glasses on, look straight at the middle of your screen, and get comfortable.",
                "Now hold still and keep looking at the same spot for twenty seconds.",
                "Slowly turn your head to the left as far as is comfortable, and hold.",
                "Back to the same spot in the middle, and hold.",
                "Slowly turn your head to the right as far as is comfortable, and hold.",
                "Back to the same spot in the middle, and hold.",
                "Look up, and hold.",
                "Back to the middle.",
                "Look down, and hold.",
                "Back to the same spot in the middle, and hold still for twenty seconds.",
                "Done. Thank you." }
                : yawMode ? new string[] {
                "Starting in five seconds. Sit still and look straight ahead.",
                "Hold still.",
                "Turn your head to the left, and hold it there.",
                "Back to center, and hold.",
                "Turn your head to the right, and hold it there.",
                "Back to center, and hold.",
                "Done. Thank you." } : new string[] {
                "Starting in five seconds. Put on the glasses, sit still, and look straight ahead.",
                "Hold still.",
                "Slowly turn your head to the left, then back to center.",
                "Slowly look up at the ceiling, then back to center.",
                "Slowly tilt your head toward your right shoulder, then back to center.",
                "Look straight ahead and hold still.",
                "Done. Thank you." };
            int cueIdx = 0, phase = -1;
            System.Speech.Synthesis.SpeechSynthesizer tts = null;
            if (guided) { try { tts = new System.Speech.Synthesis.SpeechSynthesizer(); } catch { } }

            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (guided && cueIdx < cueT.Length && sw.Elapsed.TotalSeconds >= cueT[cueIdx])
                {
                    phase = cueIdx;
                    Console.WriteLine(string.Format("[{0:F1}s] CUE {1}: {2}", sw.Elapsed.TotalSeconds, cueIdx, cueS[cueIdx]));
                    if (tts != null) tts.SpeakAsync(cueS[cueIdx]); else Console.Beep(880, 200);
                    cueIdx++;
                }
                byte[] r;
                if (!q.TryTake(out r, 250)) { if (readErr != null) { Console.WriteLine("FAIL: read error " + readErr.Message); break; } continue; }
                int o = 1; // skip report id
                if (r.Length < o + 64) { otherPackets++; continue; }
                if (r[o] != 0x01 || r[o + 1] != 0x02) { otherPackets++; if (otherPackets <= 3) Console.WriteLine("non-IMU packet: " + BitConverter.ToString(r, 0, 12)); continue; }
                imuPackets++; if (Environment.GetEnvironmentVariable("IMU_DUMP") != null && imuPackets % 200 == 1) Console.WriteLine("RAW " + BitConverter.ToString(r, 1, 64));

                ulong ts = BitConverter.ToUInt64(r, o + 4);
                double gMul = I16(r, o + 12), gDiv = I32(r, o + 14);
                double aMul = I16(r, o + 27), aDiv = I32(r, o + 29);
                if (gDiv == 0 || aDiv == 0) continue;
                double gx = I24(r, o + 18) * gMul / gDiv, gy = I24(r, o + 21) * gMul / gDiv, gz = I24(r, o + 24) * gMul / gDiv;
                double ax = I24(r, o + 33) * aMul / aDiv, ay = I24(r, o + 36) * aMul / aDiv, az = I24(r, o + 39) * aMul / aDiv;

                double tempC = I16(r, o + 2) / 326.8 + 25.0;                       // ICM-style scale (raw at bytes 2-3)
                int mx = (r[o + 48] | (r[o + 49] << 8)) - 32768, my = (r[o + 50] | (r[o + 51] << 8)) - 32768, mz = (r[o + 52] | (r[o + 53] << 8)) - 32768;
                double dt = haveTs && ts > lastTs ? (ts - lastTs) / 1e9 : 0; lastTs = ts; haveTs = true;
                if (dt > 0.1) dt = 0;
                double t = sw.Elapsed.TotalSeconds;

                if (t < biasStart) { }
                else if (!biasDone)
                {
                    bx += gx; by += gy; bz += gz; biasN++;
                    if (t >= biasEnd) { bx /= biasN; by /= biasN; bz /= biasN; biasDone = true; Console.WriteLine(string.Format("Gyro bias (deg/s): {0:F3} {1:F3} {2:F3}  -- now move your head", bx, by, bz)); }
                }
                else
                {
                    // Raw per-axis integration (axis mapping is what this test is meant to discover).
                    yaw += (gz - bz) * dt; pitch += (gx - bx) * dt; roll += (gy - by) * dt;
                }

                if (csv != null) csv.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F4},{1:F3},{2:F3},{3:F3},{4:F4},{5:F4},{6:F4},{7:F2},{8:F2},{9:F2},{10},{11:F2},{12},{13},{14}", t, gx, gy, gz, ax, ay, az, yaw, pitch, roll, phase, tempC, mx, my, mz));

                if (t >= nextPrint)
                {
                    nextPrint = t + 0.5;
                    Console.WriteLine(string.Format("t={0,5:F1}s  gyro[{1,7:F1} {2,7:F1} {3,7:F1}] dps  acc[{4,6:F2} {5,6:F2} {6,6:F2}] g  intZ={7,7:F1} intX={8,7:F1} intY={9,7:F1}  rate={10:F0}Hz",
                        t, gx, gy, gz, ax, ay, az, yaw, pitch, roll, imuPackets / Math.Max(t, 0.001)));
                }
            }
            if (csv != null) csv.Dispose();
            Console.WriteLine(string.Format("SUMMARY: imuPackets={0} otherPackets={1} avgRate={2:F0}Hz", imuPackets, otherPackets, imuPackets / sw.Elapsed.TotalSeconds));
            return imuPackets > 0 ? 0 : 4;
        }
    }
}

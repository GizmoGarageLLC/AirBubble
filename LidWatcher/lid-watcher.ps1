# AirBubble lid watcher
# Watches whether the laptop's built-in screen is active (it switches off when the lid closes
# and on when it opens). Each time that changes while the glasses are plugged in, it restarts
# AirBubble so the AR screens, layout and Bubble load back in for the new display setup.
# Log: %APPDATA%\AirBubble\lid-watcher.log

$ErrorActionPreference = 'Continue'
$viewerExe = Join-Path $env:LOCALAPPDATA 'Programs\AirBubble\AirBubble.exe'
$exeName   = 'AirBubble.exe'
if (-not (Test-Path $viewerExe)) {   # pre-1.7 install ("XREAL PC Viewer")
    $viewerExe = Join-Path $env:LOCALAPPDATA 'Programs\XREAL PC Viewer\XrealPcViewer.exe'
    $exeName   = 'XrealPcViewer.exe'
}
$logDir  = Join-Path $env:APPDATA 'AirBubble'
if (-not (Test-Path $logDir)) { $logDir = Join-Path $env:APPDATA 'XrealPcViewer' }
$logFile = Join-Path $logDir 'lid-watcher.log'

# Only one watcher at a time
$mutex = New-Object System.Threading.Mutex($false, 'Local\XrealLidWatcher')
if (-not $mutex.WaitOne(0)) { exit }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DispInfo {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
    const int ACTIVE = 0x1;

    // Returns true if any active monitor's hardware ID contains the given text.
    public static bool MonitorActive(string idPart) {
        for (uint a = 0; ; a++) {
            var ad = new DISPLAY_DEVICE(); ad.cb = Marshal.SizeOf(ad);
            if (!EnumDisplayDevices(null, a, ref ad, 0)) break;
            if ((ad.StateFlags & ACTIVE) == 0) continue;
            for (uint m = 0; ; m++) {
                var md = new DISPLAY_DEVICE(); md.cb = Marshal.SizeOf(md);
                if (!EnumDisplayDevices(ad.DeviceName, m, ref md, 0)) break;
                if ((md.StateFlags & ACTIVE) != 0 && md.DeviceID != null &&
                    md.DeviceID.IndexOf(idPart, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        return false;
    }
}
'@

function Log($msg) {
    try { Add-Content -Path $logFile -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg) } catch {}
}

# Built-in panel = LGD0764, glasses = MRG3132 (from Device Manager)
function LaptopScreenOn { [DispInfo]::MonitorActive('LGD0764') }
function GlassesOn      { [DispInfo]::MonitorActive('MRG3132') }

function Restart-Viewer($why) {
    # the viewer itself, not the "--guard" display helper
    $ids = Get-CimInstance Win32_Process -Filter "Name='$exeName'" |
           Where-Object { $_.CommandLine -notmatch '--guard' } | ForEach-Object { $_.ProcessId }
    if (-not $ids) { Log "$why - viewer not running, nothing to restart"; return }
    Log "$why - restarting viewer (pid $($ids -join ','))"
    Start-Process -FilePath $viewerExe -ArgumentList '--quit' -WindowStyle Hidden   # clean quit, restores desktop
    foreach ($id in $ids) { Wait-Process -Id $id -Timeout 10 -ErrorAction SilentlyContinue }
    foreach ($id in $ids) {
        if (Get-Process -Id $id -ErrorAction SilentlyContinue) {
            Log 'viewer did not quit in 10 s, ending it'
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Seconds 4          # let the guard helper settle the desktop first
    Start-Process -FilePath $viewerExe
    Log 'viewer started'
}

Log 'watcher started'
$last = LaptopScreenOn
while ($true) {
    Start-Sleep -Seconds 2
    $now = LaptopScreenOn
    if ($now -ne $last) {
        Start-Sleep -Seconds 3                      # wait for Windows to finish switching
        $now = LaptopScreenOn
        if ($now -ne $last) {
            $what = if ($now) { 'lid opened' } else { 'lid closed' }
            if (GlassesOn) { Restart-Viewer $what } else { Log "$what - glasses not plugged in, ignoring" }
            $last = $now
        }
    }
}

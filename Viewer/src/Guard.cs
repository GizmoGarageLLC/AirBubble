using System;
using System.IO;
using System.Threading;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// "--guard": a tiny background process (started at logon, no window) that keeps the Windows desktop sane
    /// while the viewer is NOT running:
    ///   - glasses not plugged in -> the PC screen is the only screen and primary (no stray virtual screens);
    ///   - glasses plugged in     -> Duplicate, so the primary desktop is what you see in the glasses.
    /// Windows re-applies the last layout it stored for a set of displays when they show up again (e.g. the AR
    /// layout with a virtual screen as primary); this undoes that. It only steps in when the glasses are plugged
    /// in or unplugged (and at logon), so a layout you choose afterwards with Win+P is left alone. Stray virtual
    /// screens are always switched off. It never touches anything while the viewer runs.
    /// </summary>
    static class Guard
    {
        public const string QuitEventName = "XrealPcViewer.GuardQuit";
        const string MutexName = "XrealPcViewer.Guard";
        const string ViewerMutexName = Program.ShowingMutexName;   // held only while the viewer shows the screens (not while it waits in the tray)
        static string s_Log;

        static void L(string msg)
        {
            try
            {
                if (s_Log == null) return;
                var fi = new FileInfo(s_Log);
                if (fi.Exists && fi.Length > 256 * 1024) File.WriteAllText(s_Log, "");
                File.AppendAllText(s_Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        static bool ViewerRunning()
        {
            Mutex m;
            if (Mutex.TryOpenExisting(ViewerMutexName, out m)) { m.Dispose(); return true; }
            return false;
        }

        public static int Run()
        {
            bool created;
            using (var single = new Mutex(true, MutexName, out created))
            {
                if (!created) return 0;
                try { Directory.CreateDirectory(Settings.Dir); s_Log = Path.Combine(Settings.Dir, "guard.log"); } catch { }
                L("Guard started");
                using (var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName))
                {
                    bool? lastGlasses = null;          // null = first check (logon)
                    DateTime enforceUntil = DateTime.MinValue, settleAt = DateTime.MinValue;
                    bool viewerWasRunning = false;
                    while (!quit.WaitOne(1500))
                    {
                        try
                        {
                            if (ViewerRunning()) { lastGlasses = DisplaySetup.GlassesAttached(); enforceUntil = DateTime.MinValue; viewerWasRunning = true; continue; }
                            bool glasses = DisplaySetup.GlassesAttached();
                            if (viewerWasRunning)
                            {
                                // the viewer just closed (it restores the desktop itself; this catches a crash or a
                                // display that came back late)
                                viewerWasRunning = false;
                                settleAt = DateTime.Now.AddSeconds(3); enforceUntil = settleAt.AddSeconds(12);
                                L("Viewer closed: " + DisplayConfig.Describe());
                            }
                            if (lastGlasses != glasses)
                            {
                                L((lastGlasses == null ? "Start" : glasses ? "Glasses plugged in" : "Glasses unplugged") + ": " + DisplayConfig.Describe());
                                // Windows applies its stored layout a moment after a display arrives or leaves:
                                // wait for that, then make sure things are right for the next 12 s
                                settleAt = DateTime.Now.AddSeconds(lastGlasses == null ? 0 : 2);
                                enforceUntil = settleAt.AddSeconds(12);
                                lastGlasses = glasses;
                            }
                            bool strayVirtual = DisplaySetup.ActiveMonitors().Exists(DisplayConfig.IsVirtual) || VirtualScreens.AnyVirtualMonitorActive();
                            bool inWindow = DateTime.Now >= settleAt && DateTime.Now < enforceUntil;
                            if ((strayVirtual || inWindow) && !DisplayConfig.PcDesktopOk())
                            {
                                Thread.Sleep(300);
                                if (ViewerRunning()) continue;   // the viewer just started: it owns the displays now
                                L("Fixing: " + DisplayConfig.Describe());
                                DisplayConfig.RestorePcDesktop(null, L);
                                L("Now: " + DisplayConfig.Describe());
                            }
                        }
                        catch (Exception ex) { L("Error: " + ex.Message); }
                    }
                }
                L("Guard stopped");
            }
            return 0;
        }
    }
}

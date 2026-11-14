using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// Start-with-Windows via shortcuts in the user's Startup folder (shell:startup):
    ///   "AirBubble.lnk"        -> AirBubble.exe --autostart  (waits in the tray for the glasses)
    ///   "AirBubble helper.lnk" -> AirBubble.exe --guard      (keeps the desktop sane while the viewer is closed)
    /// Older versions used HKCU\...\Run values, and before 1.7 the shortcuts were named "XREAL PC Viewer*.lnk";
    /// those are removed when the shortcuts are written.
    /// </summary>
    public static class StartupLinks
    {
        public const string ViewerLinkName = "AirBubble.lnk", GuardLinkName = "AirBubble helper.lnk";
        const string LegacyViewerLinkName = "XREAL PC Viewer.lnk", LegacyGuardLinkName = "XREAL PC Viewer helper.lnk";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string[] LegacyRunValues = { "XrealPcViewer", "XrealPcViewer.Guard" };

        public static string Dir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); } }
        public static string ViewerLink { get { return Path.Combine(Dir, ViewerLinkName); } }
        public static string GuardLink { get { return Path.Combine(Dir, GuardLinkName); } }

        public static bool ViewerEnabled { get { return File.Exists(ViewerLink) || File.Exists(Path.Combine(Dir, LegacyViewerLinkName)); } }

        public static void SetViewer(bool on, string exe = null)
        {
            exe = exe ?? Assembly.GetExecutingAssembly().Location;
            if (on) CreateShortcut(ViewerLink, exe, "--autostart", "AirBubble: waits for the glasses, then shows your screens");
            else if (File.Exists(ViewerLink)) File.Delete(ViewerLink);
            RemoveLegacyLink(LegacyViewerLinkName);
            RemoveLegacyRunValue("XrealPcViewer");
        }

        public static void SetGuard(bool on, string exe = null)
        {
            exe = exe ?? Assembly.GetExecutingAssembly().Location;
            if (on) CreateShortcut(GuardLink, exe, "--guard", "AirBubble: keeps the laptop/glasses main display right while the viewer is closed");
            else if (File.Exists(GuardLink)) File.Delete(GuardLink);
            RemoveLegacyLink(LegacyGuardLinkName);
            RemoveLegacyRunValue("XrealPcViewer.Guard");
        }

        static void RemoveLegacyLink(string name)
        {
            try { string p = Path.Combine(Dir, name); if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>True if an older version registered the viewer to start with Windows (registry Run value).</summary>
        public static bool LegacyViewerAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(LegacyRunValues[0]) != null;
        }

        static void RemoveLegacyRunValue(string name)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null && k.GetValue(name) != null) k.DeleteValue(name); } catch { }
        }

        public static void CreateShortcut(string link, string target, string args, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            var t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
    }
}

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace XrealDesktop.PcViewer
{
    /// <summary>How much an action changes: decides whether Bubble has to ask first.</summary>
    enum Risk { Small = 0, Large = 1, Dangerous = 2, Critical = 3 }

    /// <summary>
    /// How much Bubble does on his own (Settings.BubbleAutonomy):
    ///   0 ask every time  - every click / typed text / key press / opened app is a yes/no question;
    ///   1 semi-autonomous - small steps just happen, large or possibly dangerous ones are asked;
    ///   2 fully autonomous - anything the user asked for or clearly implied just happens (large changes too);
    ///                        large or risky things nobody asked for are still asked.
    /// Critical actions (commands that wipe data or weaken the PC) are asked in every mode.
    /// </summary>
    static class Autonomy
    {
        public const int AskAlways = 0, Semi = 1, Full = 2;
        public static readonly string[] Names = { "Ask every time", "Semi-autonomous", "Fully autonomous" };
        public static string Name(int level) { return Names[Math.Max(0, Math.Min(Names.Length - 1, level))]; }

        /// <summary>After this many unasked actions in one job Bubble checks in ("keep going?").</summary>
        public const int ActionsPerCheckIn = 25;

        public static bool NeedsAsk(int level, Risk risk, bool requested)
        {
            if (risk >= Risk.Critical) return true;
            if (level <= AskAlways) return true;
            if (risk == Risk.Small) return false;
            return level == Semi || !requested;
        }
    }

    /// <summary>
    /// The app's own opinion of an action, independent of what the model says about it. The higher of the two
    /// ratings counts, so a wrong (or talked-into) "small" from the model can't wave a risky action through.
    /// </summary>
    static class ActionGuard
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>The model's own rating. Anything missing or unknown counts as large (ask in semi mode).</summary>
        public static Risk ParseImpact(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "small": return Risk.Small;
                case "dangerous": return Risk.Dangerous;
                default: return Risk.Large;
            }
        }

        // ---- which app would receive it ----
        static readonly string[] s_Shells =
        {
            "cmd", "powershell", "pwsh", "powershell_ise", "windowsterminal", "openconsole", "conhost", "wt", "bash", "wsl", "wslhost",
            "ubuntu", "debian", "mintty", "git-bash", "alacritty", "regedit", "mmc", "diskpart", "taskmgr", "msconfig", "python", "node"
        };

        static string ProcessOf(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero) return "";
                uint pid; GetWindowThreadProcessId(h, out pid);
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant();
            }
            catch { return ""; }
        }

        /// <summary>True for command windows and system tools (and the Win+R "Run" box), where text is executed rather than written.</summary>
        static bool IsCommandWindow(IntPtr h, out string name)
        {
            name = ProcessOf(h);
            if (Array.IndexOf(s_Shells, name) >= 0) return true;
            try
            {
                var cls = new StringBuilder(64); var title = new StringBuilder(64);
                GetClassName(h, cls, 64); GetWindowText(h, title, 64);
                if (name == "explorer" && cls.ToString() == "#32770" && title.ToString() == "Run") { name = "the Run box"; return true; }
            }
            catch { }
            return false;
        }

        public static Func<IntPtr> ForegroundForTest;     // --autonomy-test: pretend nothing special has the focus
        static IntPtr Foreground() { return ForegroundForTest != null ? ForegroundForTest() : GetForegroundWindow(); }

        // ---- typed text ----
        static readonly Regex s_Wipe = new Regex(
            @"\bformat(\.com)?\s+[a-z]:|\bdiskpart\b|\b(clear-disk|format-volume|remove-partition|initialize-disk)\b|\bmkfs\b|\bdd\s+if=|" +
            @"\brm\s+(-[a-z]*\s+)*-[a-z]*(r[a-z]*f|f[a-z]*r)|\b(del|erase)\b[^\r\n]*\s/[sq]\b|\b(rd|rmdir)\b[^\r\n]*\s/s\b|\bremove-item\b[^\r\n]*-(recurse|r)\b|" +
            @"\breg(\.exe)?\s+delete\b|\bbcdedit\b|\bcipher\s+/w|\bvssadmin\b[^\r\n]*\bdelete\b|\bwbadmin\b[^\r\n]*\bdelete\b|" +
            @"\b(iex|invoke-expression)\b|\b(curl|wget|iwr|irm|invoke-webrequest|invoke-restmethod)\b[^\r\n]*\|\s*(sh|bash|iex|powershell|pwsh|cmd)\b|" +
            @"\bset-mppreference\b|\bset-executionpolicy\b|\bnet(\.exe)?\s+(user|localgroup)\b[^\r\n]*/(add|delete)|\bnetsh\b[^\r\n]*\bfirewall\b[^\r\n]*\b(off|disable)|" +
            @"\bmanage-bde\b|\btakeown\b|\bsdelete\b|\b-encodedcommand\b|\bfrombase64string\b", IC);

        static readonly Regex s_Command = new Regex(
            @"\b(shutdown|taskkill|stop-process|stop-computer|restart-computer|sc(\.exe)?\s+(delete|stop|config)|schtasks|wmic|msiexec|regedit|reg(\.exe)?\s+add|icacls|attrib|" +
            @"netsh|powershell|pwsh|cmd(\.exe)?\s*/[ck]|start-process|new-service|winget\s+(install|uninstall)|choco\s+(install|uninstall)|pip\s+install|npm\s+(install|i)\b|git\s+(push|reset|clean)|" +
            @"remove-item|move-item|rename-item)\b|" +
            @"(^|[\r\n;&|])\s*(del|erase|rd|rmdir|ren|move|sudo|chmod|chown|kill|rm)\s", IC);   // ordinary words only count at the start of a line

        public static Risk TypeRisk(string text, out string why)
        {
            text = text ?? "";
            if (s_Wipe.IsMatch(text)) { why = "it looks like a command that could wipe data or weaken this PC"; return Risk.Critical; }
            string app;
            if (IsCommandWindow(Foreground(), out app)) { why = "it goes into " + Friendly(app) + ", where text is run as a command"; return Risk.Dangerous; }
            if (s_Command.IsMatch(text) && (text.IndexOf('\n') >= 0 || text.Length < 200)) { why = "it looks like a system command"; return Risk.Dangerous; }
            if (text.IndexOf('\n') >= 0) { why = "it includes Enter, which can send or submit it"; return Risk.Large; }
            if (text.Length > 400) { why = "it's a lot of text"; return Risk.Large; }
            why = null; return Risk.Small;
        }

        // ---- key presses ----
        public static Risk KeysRisk(string keys, out string why)
        {
            string k = Regex.Replace((keys ?? "").ToLowerInvariant(), @"\s+", "").Replace("control", "ctrl").Replace("windows", "win").Replace("return", "enter").Replace("delete", "del");
            var parts = new System.Collections.Generic.HashSet<string>(k.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries));
            if (parts.Contains("del") && parts.Contains("shift")) { why = "Shift+Delete deletes for good, without the Recycle Bin"; return Risk.Dangerous; }
            string app;
            if (parts.Contains("enter") && IsCommandWindow(Foreground(), out app)) { why = "Enter in " + Friendly(app) + " runs whatever is typed there"; return Risk.Dangerous; }
            if (parts.Contains("del")) { why = "Delete removes whatever is selected"; return Risk.Large; }
            if (parts.Contains("alt") && parts.Contains("f4")) { why = "Alt+F4 closes the app (or offers to shut the PC down)"; return Risk.Large; }
            if (parts.Contains("win") && (parts.Contains("r") || parts.Contains("x"))) { why = "it opens Windows' command menus"; return Risk.Large; }
            why = null; return Risk.Small;
        }

        // ---- clicks ----
        static readonly Regex s_ClickDanger = new Regex(
            @"\b(delete|erase|format|wipe|uninstall|permanently|factory|reset (this )?pc|empty (the )?(recycle|trash|bin)|buy|purchase|pay|checkout|check out|place (the |my |your )?order|order now|" +
            @"subscribe|transfer|donate|withdraw|remove (account|device|user)|disable|turn off (antivirus|firewall|defender|protection)|shut ?down|power off)\b", IC);
        static readonly Regex s_ClickLarge = new Regex(
            @"\b(send|submit|post|publish|reply|share|install|update|upgrade|sign out|log ?out|log off|restart|reboot|discard|don'?t save|overwrite|replace( all)?|remove|clear|" +
            @"allow|accept|agree|i agree|confirm|run|download|import|merge|commit|push|unsubscribe|block|report|close all|end task|sync)\b", IC);

        public static Risk ClickRisk(int x, int y, string what, out string why)
        {
            what = what ?? "";
            if (s_ClickDanger.IsMatch(what)) { why = "it could delete something, cost money or change how the PC is protected"; return Risk.Dangerous; }
            if (s_ClickLarge.IsMatch(what)) { why = "it's hard to take back"; return Risk.Large; }
            try
            {
                string app;
                if (ForegroundForTest == null && IsCommandWindow(GetAncestor(WindowFromPoint(new PT { X = x, Y = y }), 2), out app)) { why = "it's in " + Friendly(app); return Risk.Large; }
            }
            catch { }
            why = null; return Risk.Small;
        }

        // ---- opening things ----
        static readonly string[] s_RunExt = { ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".reg", ".scr", ".com", ".pif", ".hta", ".cpl", ".msc", ".jar", ".py", ".sh", ".lnk", ".url", ".inf", ".gadget", ".application", ".appref-ms" };
        static readonly string[] s_DangerApps = { "shutdown", "diskpart", "format", "logoff", "bcdedit", "cipher", "sdelete", "cleanmgr", "sysprep", "systemreset", "rstrui" };
        static readonly string[] s_SystemApps = { "cmd", "powershell", "pwsh", "powershell_ise", "wt", "bash", "wsl", "regedit", "mmc", "msconfig", "gpedit", "control", "taskmgr", "services", "compmgmt", "diskmgmt", "devmgmt", "netplwiz", "optionalfeatures", "appwiz", "firewall", "secpol", "lusrmgr", "taskschd" };

        public static bool IsWeb(string target)
        {
            return target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }

        public static Risk OpenRisk(string target, out string why)
        {
            target = (target ?? "").Trim().Trim('"');
            why = null;
            if (IsWeb(target)) return Risk.Small;
            if (Regex.IsMatch(target, @"^[a-z][a-z0-9+.-]{1,30}:", IC))                              // an app link: spotify:, ms-settings:display, mailto:...
            {
                if (Regex.IsMatch(target, @"^(javascript|vbscript|data|file|search-ms|ms-msdt|shell|ftp|ssh|telnet):", IC)) { why = "that kind of link can run or fetch things"; return Risk.Dangerous; }
                return Risk.Small;
            }
            bool exists = false;
            try { exists = File.Exists(target) || Directory.Exists(target); } catch { }
            if (!exists && Regex.IsMatch(target, @"\s[-/]\S")) { why = "it has extra command options"; return Risk.Large; }
            string name = target, ext = "";
            try { name =Path.GetFileNameWithoutExtension(target).ToLowerInvariant(); ext = Path.GetExtension(target).ToLowerInvariant(); } catch { }
            bool path = target.IndexOf('\\') >= 0 || target.IndexOf('/') >= 0 || (target.Length > 1 && target[1] == ':');
            bool app = ext == "" || ext == ".exe" || ext == ".com" || ext == ".msc" || ext == ".cpl";
            if (app && Array.IndexOf(s_DangerApps, name) >= 0) { why = name + " can wipe data or shut the PC down"; return Risk.Dangerous; }
            if (app && Array.IndexOf(s_SystemApps, name) >= 0) { why = name + " is a system tool"; return Risk.Large; }
            if (!path && (ext == "" || ext == ".exe"))                                              // an installed app by name: "notepad", "calc", "ms paint"
            {
                if (!Regex.IsMatch(target, @"^[\w .&'+-]+$") || Regex.IsMatch(target, @"\s[-/]")) { why = "it has extra command options"; return Risk.Large; }
                return Risk.Small;
            }
            if (Array.IndexOf(s_RunExt, ext) >= 0) { why = "it runs a program or script (" + ext + ")"; return Risk.Dangerous; }
            if (target.StartsWith("\\\\")) { why = "it's on another computer on the network"; return Risk.Large; }
            return Risk.Small;                                                                      // a document or folder
        }

        static string Friendly(string app)
        {
            switch (app)
            {
                case "cmd": case "conhost": case "openconsole": return "a Command Prompt";
                case "powershell": case "pwsh": case "powershell_ise": return "PowerShell";
                case "windowsterminal": case "wt": return "a terminal";
                case "regedit": return "the Registry Editor";
                case "taskmgr": return "Task Manager";
                default: return app.StartsWith("the ") ? app : "a command window (" + app + ")";
            }
        }
    }

    /// <summary>Everything Bubble did or wanted to do with the PC, one line each (%APPDATA%\AirBubble\bubble_actions.log).</summary>
    static class ActionLog
    {
        static readonly object s_Lock = new object();
        public static string PathFor() { return Path.Combine(Settings.Dir, "bubble_actions.log"); }

        public static void Write(int level, string who, string action, Risk risk, bool requested, string outcome)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + Autonomy.Name(level) + "\t" + who + "\t" +
                          risk.ToString().ToLowerInvariant() + (requested ? ", requested" : ", not requested") + "\t" + outcome + "\t" + action.Replace('\r', ' ').Replace('\n', ' ');
            Log.W("Bubble action: " + line.Substring(20));
            lock (s_Lock)
            {
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    string p = PathFor();
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 512 * 1024) { string old = p + ".old"; File.Delete(old); File.Move(p, old); }
                    File.AppendAllText(p, line + "\r\n", Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}

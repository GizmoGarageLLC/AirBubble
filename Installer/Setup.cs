// Installer / uninstaller for AirBubble (formerly "PC Viewer for XREAL glasses").
// Per-user install (no admin): %LOCALAPPDATA%\Programs\AirBubble
// Installing over a pre-1.7 "XREAL PC Viewer" install removes the old files, shortcuts and Apps entry
// (keeping the desktop shortcut / start-with-Windows choices); settings move over when the viewer first starts.
//
//   AirBubbleSetup.exe                     interactive install
//   AirBubbleSetup.exe /S [/desktop] [/noautostart] [/launch] [/nouac] [/dir=PATH]   silent install
//   (/nouac: leave Windows' UAC "secure desktop" alone; by default setup turns it off so permission prompts
//    show in the glasses, and the uninstaller puts the old value back)
//   (the viewer starts with Windows by default: shortcut in the Startup folder; /noautostart leaves it out.
//    The display helper "--guard" always gets a Startup-folder shortcut.)
//   Uninstall.exe /uninstall [/S] [/keepsettings]                               uninstall (copied into the install folder)
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("AirBubble - Setup")]
[assembly: System.Reflection.AssemblyDescription("Installer for AirBubble (floating PC screens and an AI buddy for XREAL Air glasses). Not affiliated with XREAL.")]
[assembly: System.Reflection.AssemblyProduct("AirBubble")]
[assembly: System.Reflection.AssemblyCompany("Independent build (not affiliated with XREAL)")]
[assembly: System.Reflection.AssemblyCopyright("Independent build")]
[assembly: System.Reflection.AssemblyVersion("1.7.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.7.1.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.7.1")]
namespace XrealDesktop.Setup
{
    static class Product
    {
        public const string Name = "AirBubble";
        public const string Version = "1.7.1";
        public const string FolderName = "AirBubble";
        public const string ExeName = "AirBubble.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string RegKey = "AirBubble";          // Uninstall key name
        // Internal IPC name, deliberately unchanged by the rename so a new installer can stop an old guard too
        public const string GuardQuitEvent = "XrealPcViewer.GuardQuit";
        public static readonly string[] Payload = { "AirBubble.exe", "README.txt" };
        public static readonly string[] DriverPayload = { "MttVDD.dll", "mttvdd.cat", "MttVDD.inf", "vdd_settings.xml", "LICENSE.txt" };
        public const string DriverFolder = "VirtualDisplayDriver";
        public const int VirtualScreens = 8;   // 4 up to 1.7.0; an update raises an older driver config (one admin prompt)

        public static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", FolderName); } }
        public static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); } }
        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); } }
        public static string SettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirBubble"); } }
        public const string UninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + RegKey;
        public const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";   // used by versions up to 1.3.3 (cleaned up now)
        // Start with Windows: shortcuts in the user's Startup folder (same names as the viewer's StartupLinks)
        public static string StartupDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); } }
        public static string ViewerStartupLink { get { return Path.Combine(StartupDir, "AirBubble.lnk"); } }
        public static string GuardStartupLink { get { return Path.Combine(StartupDir, "AirBubble helper.lnk"); } }

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallRegPath))
                return k == null ? null : k.GetValue("InstallLocation") as string;
        }
    }

    /// <summary>Names used before the rename to AirBubble (1.7.0), for upgrading and cleaning up old installs.</summary>
    static class Legacy
    {
        public const string Name = "PC Viewer for XREAL glasses";
        public const string ExeName = "XrealPcViewer.exe";
        public const string RegKey = "XrealPcViewer";               // Uninstall key name and (up to 1.3.3) Run value name
        public const string GuardRegKey = "XrealPcViewer.Guard";    // Run value (up to 1.3.3)
        public const string UninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + RegKey;
        public static readonly string[] Payload = { "XrealPcViewer.exe", "README.txt" };
        public static string SettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XrealPcViewer"); } }
        public static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); } }
        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); } }
        public static string ViewerStartupLink { get { return Path.Combine(Product.StartupDir, "XREAL PC Viewer.lnk"); } }
        public static string GuardStartupLink { get { return Path.Combine(Product.StartupDir, "XREAL PC Viewer helper.lnk"); } }
        public static string[] AllLinks { get { return new[] { StartMenuLink, DesktopLink, ViewerStartupLink, GuardStartupLink }; } }

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallRegPath))
                return k == null ? null : k.GetValue("InstallLocation") as string;
        }

        public static bool AutostartWasOn()
        {
            if (File.Exists(ViewerStartupLink)) return true;
            using (var k = Registry.CurrentUser.OpenSubKey(Product.RunRegPath)) return k != null && k.GetValue(RegKey) != null;
        }

        /// <summary>Removes an old "XREAL PC Viewer" install: its shortcuts, Run values, Apps entry and files. Settings are left for the viewer to move.</summary>
        public static void RemoveOldInstall(Action<string> log)
        {
            string dir = InstalledDir();
            bool any = dir != null;
            foreach (var l in AllLinks) if (File.Exists(l)) { any = true; try { File.Delete(l); } catch { } }
            using (var k = Registry.CurrentUser.OpenSubKey(Product.RunRegPath, true))
            {
                if (k != null && k.GetValue(RegKey) != null) k.DeleteValue(RegKey);
                if (k != null && k.GetValue(GuardRegKey) != null) k.DeleteValue(GuardRegKey);
            }
            if (!any) return;
            log("Removing the old \"XREAL PC Viewer\" install...");
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegPath, false);
            // Only delete files we installed, and only from a folder that holds the old exe.
            if (dir != null && Directory.Exists(dir) && File.Exists(Path.Combine(dir, ExeName)))
            {
                foreach (var name in Payload) try { File.Delete(Path.Combine(dir, name)); } catch { }
                try { File.Delete(Path.Combine(dir, Product.UninstallerName)); } catch { }
                try { string dd = Path.Combine(dir, Product.DriverFolder); if (Directory.Exists(dd)) Directory.Delete(dd, true); } catch { }
                try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }
            }
        }
    }

    sealed class Options
    {
        public string Dir = Product.DefaultDir;
        public bool Desktop, Autostart = true, Launch = true, Driver = true, UacInGlasses = true;
    }

    static class Installer
    {
        /// <summary>Asks a running viewer to quit cleanly (it restores the display mode), then waits; kills as a last resort.</summary>
        public static void StopViewer(Action<string> log)
        {
            // the display guard (--guard) quits on its own event
            try { EventWaitHandle ev; if (EventWaitHandle.TryOpenExisting(Product.GuardQuitEvent, out ev)) { ev.Set(); ev.Dispose(); } } catch { }
            var procs = new System.Collections.Generic.List<Process>(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Product.ExeName)));
            procs.AddRange(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Legacy.ExeName)));   // pre-1.7 "XREAL PC Viewer"
            if (procs.Count == 0) return;
            log("Closing the running viewer...");
            foreach (var p in procs)
            {
                try
                {
                    string exe = p.MainModule.FileName;
                    Process.Start(new ProcessStartInfo(exe, "--quit") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(5000);
                    break;
                }
                catch { }
            }
            foreach (var p in procs)
            {
                try { if (!p.WaitForExit(12000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            }
        }

        public static void Install(Options o, Action<string> log)
        {
            StopViewer(log);
            Legacy.RemoveOldInstall(log);
            Directory.CreateDirectory(o.Dir);

            log("Copying files...");
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in Product.Payload)
            {
                using (var src = asm.GetManifestResourceStream("payload/" + name))
                {
                    if (src == null) throw new Exception("Installer is missing " + name);
                    string dst = Path.Combine(o.Dir, name);
                    using (var f = File.Create(dst)) src.CopyTo(f);
                }
            }
            string drvDir = Path.Combine(o.Dir, Product.DriverFolder);
            Directory.CreateDirectory(drvDir);
            foreach (var name in Product.DriverPayload)
                using (var src = asm.GetManifestResourceStream("payload/vdd/" + name))
                {
                    if (src == null) throw new Exception("Installer is missing driver file " + name);
                    using (var f = File.Create(Path.Combine(drvDir, name))) src.CopyTo(f);
                }
            string self = asm.Location, uninst = Path.Combine(o.Dir, Product.UninstallerName);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninst, true);

            string exe = Path.Combine(o.Dir, Product.ExeName);
            bool needUac = o.UacInGlasses && !XrealDesktop.PcViewer.UacPrompts.ShownInGlasses;
            if (needUac && !(o.Driver && !VddInstall.IsInstalled()))
            {
                log("Letting permission (UAC) prompts show in the glasses (Windows will ask for permission)...");
                int code = RunElevated(uninst, "/uacprompts on");
                if (code == -1223) log("UAC prompts setting skipped (permission declined). It can be changed later in the viewer's Settings.");
            }
            if (o.Driver && !VddInstall.IsInstalled())
            {
                log("Installing the virtual screens driver (Windows will ask for permission)...");
                int code = RunElevated(uninst, "/installdriver \"" + drvDir + "\" " + Product.VirtualScreens + (needUac ? " /uacprompts on" : ""));
                if (code == -1223) log("Virtual screens driver skipped (permission declined). Run setup again to add it.");
                else if (code != 0) log("Virtual screens driver could not be installed (code " + code + ").");
                else
                {
                    // New virtual monitors attach to the desktop right away; keep them off until the viewer shows them.
                    try { Process.Start(new ProcessStartInfo(exe, "--detach-virtual") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(15000); } catch { }
                }
            }
            else if (o.Driver && NeedsMoreScreens())
            {
                log("Adding virtual screens: " + VddInstall.ConfiguredCount() + " to " + Product.VirtualScreens + " (Windows will ask for permission; screens blink once)...");
                int code = RunElevated(uninst, "/setscreens " + Product.VirtualScreens);
                if (code == -1223) log("More virtual screens skipped (permission declined). Run setup again to add them.");
                else if (code != 0) log("Virtual screens could not be added (code " + code + "; details in AirBubble-driver.txt in the TEMP folder).");
                else
                {
                    Thread.Sleep(3000);   // the restarted driver brings its monitors back attached; keep them off until the viewer shows them
                    try { Process.Start(new ProcessStartInfo(exe, "--detach-virtual") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(15000); } catch { }
                }
            }
            log("Creating shortcuts...");
            CreateShortcut(Product.StartMenuLink, exe, "", o.Dir);
            if (o.Desktop) CreateShortcut(Product.DesktopLink, exe, "", o.Dir);
            else if (File.Exists(Product.DesktopLink)) File.Delete(Product.DesktopLink);

            log("Registering...");
            long sizeKb = 0;
            foreach (var f in Directory.GetFiles(o.Dir)) sizeKb += new FileInfo(f).Length / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(Product.UninstallRegPath))
            {
                k.SetValue("DisplayName", Product.Name);
                k.SetValue("DisplayVersion", Product.Version);
                k.SetValue("InstallLocation", o.Dir);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /S");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            log("Setting up start with Windows...");
            // Startup folder shortcuts: the display helper always, the viewer (waits in the tray) unless turned off.
            // (Older versions' registry Run values were removed by Legacy.RemoveOldInstall, so nothing starts twice.)
            CreateShortcut(Product.GuardStartupLink, exe, "--guard", o.Dir);
            if (o.Autostart) CreateShortcut(Product.ViewerStartupLink, exe, "--autostart", o.Dir);
            else if (File.Exists(Product.ViewerStartupLink)) File.Delete(Product.ViewerStartupLink);
            try { Process.Start(new ProcessStartInfo(exe, "--guard") { WorkingDirectory = o.Dir, UseShellExecute = false }); } catch { }

            if (o.Launch)
            {
                log("Starting the viewer...");
                // --autostart: if the glasses aren't plugged in yet it waits quietly (tray icon) instead of showing an error.
                Process.Start(new ProcessStartInfo(exe, "--autostart") { WorkingDirectory = o.Dir, UseShellExecute = false });
            }
            log("Done.");
        }

        /// <summary>True if our driver is installed with fewer virtual screens than this version provides.</summary>
        public static bool NeedsMoreScreens()
        {
            if (!VddInstall.IsInstalled() || !VddInstall.InstalledByUs()) return false;
            int n = VddInstall.ConfiguredCount();
            return n >= 0 && n < Product.VirtualScreens;
        }

        /// <summary>Runs an exe elevated (UAC). Returns its exit code, or -1223 if the user declined.</summary>
        public static int RunElevated(string exe, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                if (e.NativeErrorCode == 1223) return -1223;
                throw;
            }
        }

        public static void Uninstall(bool keepSettings, bool removeDriver, Action<string> log)
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string regDir = Product.InstalledDir();
            if (regDir != null && Directory.Exists(regDir)) dir = regDir;

            StopViewer(log);
            log("Removing shortcuts and registry entries...");
            foreach (var l in new[] { Product.StartMenuLink, Product.DesktopLink, Product.ViewerStartupLink, Product.GuardStartupLink }) try { if (File.Exists(l)) File.Delete(l); } catch { }
            foreach (var l in Legacy.AllLinks) try { if (File.Exists(l)) File.Delete(l); } catch { }
            using (var k = Registry.CurrentUser.OpenSubKey(Product.RunRegPath, true))
            {
                if (k != null && k.GetValue(Legacy.RegKey) != null) k.DeleteValue(Legacy.RegKey);
                if (k != null && k.GetValue(Legacy.GuardRegKey) != null) k.DeleteValue(Legacy.GuardRegKey);
            }
            Registry.CurrentUser.DeleteSubKeyTree(Product.UninstallRegPath, false);
            if (!keepSettings)
                foreach (var sd in new[] { Product.SettingsDir, Legacy.SettingsDir })
                    try { if (Directory.Exists(sd)) Directory.Delete(sd, true); } catch { }

            bool restoreUac = XrealDesktop.PcViewer.UacPrompts.ChangedByUs;
            if (restoreUac && !(removeDriver && VddInstall.IsInstalled() && VddInstall.InstalledByUs()))
            {
                log("Putting back the Windows UAC prompt setting...");
                RunElevated(Assembly.GetExecutingAssembly().Location, "/uacprompts restore");
            }
            if (removeDriver && VddInstall.IsInstalled() && VddInstall.InstalledByUs())
            {
                log("Removing the virtual screens driver...");
                int code = RunElevated(Assembly.GetExecutingAssembly().Location, "/removedriver" + (restoreUac ? " /uacprompts restore" : ""));
                if (code == -1223) log("Driver removal skipped (permission declined); it can be removed later from Device Manager.");
                else if (code != 0) log("Driver removal reported code " + code + " (details in AirBubble-driver.txt in the TEMP folder).");
            }
            log("Removing files...");
            // Only delete files we installed, and only from a folder that holds our exe.
            if (File.Exists(Path.Combine(dir, Product.ExeName)) || File.Exists(Path.Combine(dir, Product.UninstallerName)))
            {
                foreach (var name in Product.Payload) try { File.Delete(Path.Combine(dir, name)); } catch { }
                try { string dd = Path.Combine(dir, Product.DriverFolder); if (Directory.Exists(dd)) Directory.Delete(dd, true); } catch { }
                // The uninstaller can't delete itself while running: a hidden cmd finishes after we exit.
                // 'rmdir' without /s only removes the folder if it is now empty.
                string uninst = Path.Combine(dir, Product.UninstallerName);
                string cmd = string.Format("/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{0}\" & rmdir \"{1}\"", uninst, dir);
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            log("Done.");
        }

        static void CreateShortcut(string link, string target, string args, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            var t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { Product.Name });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
    }

    sealed class SetupForm : Form
    {
        readonly Options m_O = new Options();
        readonly TextBox m_Dir;
        readonly CheckBox m_Desktop, m_Autostart, m_Launch, m_Driver, m_Uac;
        readonly Button m_Install, m_Cancel, m_Browse;
        readonly Label m_Status;
        bool m_Done;

        public SetupForm()
        {
            Text = Product.Name + " Setup";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            ClientSize = new Size(560, 470);

            var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Color.FromArgb(18, 22, 34) };
            var pic = new PictureBox { Image = Icon != null ? new Icon(Icon, 48, 48).ToBitmap() : null, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Location = new Point(18, 18) };
            var title = new Label { Text = Product.Name, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = new Point(78, 16) };
            var sub = new Label { Text = "Version " + Product.Version + "  Â·  floating PC screens and Bubble AI", ForeColor = Color.FromArgb(170, 185, 210), AutoSize = true, Location = new Point(80, 48) };
            header.Controls.AddRange(new Control[] { pic, title, sub });

            string existing = Product.InstalledDir();
            if (existing != null) m_O.Dir = existing;
            // A pre-1.7 "XREAL PC Viewer" install is updated into the new AirBubble folder (the old one is removed)
            bool legacy = existing == null && (Legacy.InstalledDir() != null || File.Exists(Legacy.StartMenuLink));
            var intro = new Label
            {
                Text = (existing != null ? "An earlier version is installed. Setup will update it.\n\n" :
                        legacy ? "\"PC Viewer for XREAL glasses\" is now AirBubble. Setup will replace the old install and keep your settings.\n\n" : "") +
                       "Plug your XREAL Air glasses into this PC with USB-C. The viewer shows your monitors and app windows as " +
                       "screens that stay fixed in space as you turn your head. No admin rights needed.",
                AutoSize = false, Location = new Point(20, 98), Size = new Size(520, 70)
            };
            var dirLabel = new Label { Text = "Install to:", AutoSize = true, Location = new Point(20, 178) };
            m_Dir = new TextBox { Text = m_O.Dir, Location = new Point(20, 200), Width = 420 };
            m_Browse = new Button { Text = "Browse...", Location = new Point(448, 197), Size = new Size(92, 30) };
            m_Browse.Click += delegate
            {
                using (var d = new FolderBrowserDialog { SelectedPath = m_Dir.Text, Description = "Choose an install folder" })
                    if (d.ShowDialog(this) == DialogResult.OK) m_Dir.Text = Path.Combine(d.SelectedPath, Product.FolderName);
            };
            m_Desktop = new CheckBox { Text = "Create a desktop shortcut", AutoSize = true, Location = new Point(20, 240) };
            m_Autostart = new CheckBox { Text = "Start with Windows (waits in the tray; shows your screens when the glasses are plugged in)", AutoSize = true, Location = new Point(20, 266) };
            m_Launch = new CheckBox { Text = "Start the viewer when setup finishes", AutoSize = true, Location = new Point(20, 292), Checked = true };
            m_Driver = new CheckBox { Text = "Install the virtual screens driver (" + Product.VirtualScreens + " extra screens; asks for admin once)", AutoSize = true, Location = new Point(20, 318), Checked = true };
            if (Installer.NeedsMoreScreens()) m_Driver.Text = "Add virtual screens: " + VddInstall.ConfiguredCount() + " to " + Product.VirtualScreens + " (asks for admin once)";
            else if (VddInstall.IsInstalled()) { m_Driver.Text = "Virtual screens driver: already installed"; m_Driver.Checked = false; m_Driver.Enabled = false; }
            m_Uac = new CheckBox { Text = "Show Windows permission (UAC) prompts in the glasses (turns off the dimmed secure desktop)", AutoSize = true, Location = new Point(20, 344), Checked = true };
            if (XrealDesktop.PcViewer.UacPrompts.ShownInGlasses) { m_Uac.Text = "Windows permission (UAC) prompts: already shown in the glasses"; m_Uac.Checked = false; m_Uac.Enabled = false; }
            // on by default; an update keeps what was chosen before (Startup shortcut, or the old registry entry)
            if (legacy) m_Autostart.Checked = Legacy.AutostartWasOn();
            else m_Autostart.Checked = existing == null || File.Exists(Product.ViewerStartupLink);
            m_Desktop.Checked = File.Exists(Product.DesktopLink) || File.Exists(Legacy.DesktopLink);

            m_Status = new Label { AutoSize = false, Location = new Point(20, 376), Size = new Size(520, 40), ForeColor = Color.FromArgb(60, 70, 90) };
            m_Install = new Button { Text = existing != null || legacy ? "Update" : "Install", Location = new Point(352, 424), Size = new Size(92, 30) };
            m_Cancel = new Button { Text = "Cancel", Location = new Point(448, 424), Size = new Size(92, 30) };
            m_Install.Click += delegate { if (m_Done) Close(); else DoInstall(); };
            m_Cancel.Click += delegate { Close(); };
            AcceptButton = m_Install; CancelButton = m_Cancel;

            Controls.AddRange(new Control[] { header, intro, dirLabel, m_Dir, m_Browse, m_Desktop, m_Autostart, m_Launch, m_Driver, m_Uac, m_Status, m_Install, m_Cancel });
        }

        void DoInstall()
        {
            m_O.Dir = m_Dir.Text.Trim();
            m_O.Desktop = m_Desktop.Checked; m_O.Autostart = m_Autostart.Checked; m_O.Launch = m_Launch.Checked; m_O.Driver = m_Driver.Checked; m_O.UacInGlasses = m_Uac.Checked;
            if (m_O.Dir.Length < 4 || !Path.IsPathRooted(m_O.Dir)) { MessageBox.Show(this, "Please choose a full folder path.", Text); return; }
            foreach (Control c in new Control[] { m_Dir, m_Browse, m_Desktop, m_Autostart, m_Launch, m_Driver, m_Uac, m_Install, m_Cancel }) c.Enabled = false;
            UseWaitCursor = true;
            var t = new Thread(() =>
            {
                Exception err = null;
                try { Installer.Install(m_O, s => BeginInvoke((Action)(() => m_Status.Text = s))); }
                catch (Exception e) { err = e; }
                BeginInvoke((Action)(() =>
                {
                    UseWaitCursor = false;
                    if (err != null)
                    {
                        m_Status.Text = "Setup failed: " + err.Message;
                        m_Cancel.Enabled = true; m_Install.Enabled = true; m_Dir.Enabled = true; m_Browse.Enabled = true;
                        m_Desktop.Enabled = m_Autostart.Enabled = m_Launch.Enabled = true;
                        return;
                    }
                    m_Done = true;
                    m_Status.Text = "Installed. Find it in the Start menu as \"" + Product.Name + "\". Press Ctrl+Alt+S in the viewer for settings.";
                    m_Install.Text = "Finish"; m_Install.Enabled = true;
                }));
            }) { IsBackground = true };
            t.Start();
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool silent = Has(args, "/S"), uninstall = Has(args, "/uninstall");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (Environment.OSVersion.Version.Major < 10 && !silent)
                MessageBox.Show("AirBubble needs Windows 10 or 11.", Product.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            string arch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
            if (!Environment.Is64BitOperatingSystem || arch == "ARM64")
            {
                if (!silent) MessageBox.Show("AirBubble needs 64-bit Windows on an Intel or AMD processor (x64).\n\nThis PC: " + (arch.Length > 0 ? arch : "32-bit") + ".",
                    Product.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 5;
            }

            // Elevated step (alone, or together with /installdriver or /removedriver): /uacprompts on|restore
            int ui = Array.FindIndex(args, a => string.Equals(a, "/uacprompts", StringComparison.OrdinalIgnoreCase));
            if (ui >= 0)
            {
                string what = ui + 1 < args.Length ? args[ui + 1].ToLowerInvariant() : "";
                try
                {
                    if (what == "on") XrealDesktop.PcViewer.UacPrompts.Apply(true);
                    else if (what == "off") XrealDesktop.PcViewer.UacPrompts.Apply(false);
                    else if (what == "restore") XrealDesktop.PcViewer.UacPrompts.RestoreIfChangedByUs();
                }
                catch (Exception e) { try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AirBubble-uac.txt"), e.ToString()); } catch { } }
                if (!Has(args, "/installdriver") && !Has(args, "/removedriver")) return 0;
            }

            int di = Array.FindIndex(args, a => string.Equals(a, "/installdriver", StringComparison.OrdinalIgnoreCase));
            if (di >= 0)
            {
                // Elevated step: /installdriver "<package dir>" <screens>
                string pkg = di + 1 < args.Length ? args[di + 1] : "";
                int n; if (di + 2 >= args.Length || !int.TryParse(args[di + 2], out n)) n = Product.VirtualScreens;
                try
                {
                    string err = VddInstall.Install(pkg, n, s => { });
                    if (err.Length > 0) File.WriteAllText(Path.Combine(Path.GetTempPath(), "AirBubble-driver.txt"), err);
                    return err.Length == 0 ? 0 : 3;
                }
                catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AirBubble-driver.txt"), e.ToString()); return 4; }
            }
            int si = Array.FindIndex(args, a => string.Equals(a, "/setscreens", StringComparison.OrdinalIgnoreCase));
            if (si >= 0)
            {
                // Elevated step: /setscreens <n>  (more or fewer virtual screens for an installed driver)
                int n; if (si + 1 >= args.Length || !int.TryParse(args[si + 1], out n)) n = Product.VirtualScreens;
                string logPath = Path.Combine(Path.GetTempPath(), "AirBubble-driver.txt");
                var lines = new System.Collections.Generic.List<string>();
                try
                {
                    string err = VddInstall.SetScreens(n, s => lines.Add(s));
                    if (err.Length > 0) lines.Add(err);
                    File.WriteAllLines(logPath, lines);
                    return err.Length == 0 ? 0 : 3;
                }
                catch (Exception e) { lines.Add(e.ToString()); File.WriteAllLines(logPath, lines); return 4; }
            }
            if (Has(args, "/removedriver"))
            {
                // Elevated step. Log to %TEMP% so the (non-elevated) uninstaller can report the outcome.
                string logPath = Path.Combine(Path.GetTempPath(), "AirBubble-driver.txt");
                var lines = new System.Collections.Generic.List<string>();
                try { VddInstall.Remove(s => lines.Add(s)); File.WriteAllLines(logPath, lines); return VddInstall.IsInstalled() ? 3 : 0; }
                catch (Exception e) { lines.Add(e.ToString()); File.WriteAllLines(logPath, lines); return 4; }
            }

            try
            {
                if (uninstall)
                {
                    bool keep = Has(args, "/keepsettings"), removeDriver = !Has(args, "/keepdriver");
                    if (!silent)
                    {
                        if (MessageBox.Show("Uninstall " + Product.Name + "?", Product.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                        keep = MessageBox.Show("Keep your settings (distance, screen size, 3D...) in case you reinstall?", Product.Name,
                                               MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    }
                    Installer.Uninstall(keep, removeDriver, s => { });
                    if (!silent) MessageBox.Show(Product.Name + " was uninstalled.", Product.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (silent)
                {
                    var o = new Options { Desktop = Has(args, "/desktop"), Autostart = !Has(args, "/noautostart"), Launch = Has(args, "/launch"), Driver = !Has(args, "/nodriver"), UacInGlasses = !Has(args, "/nouac") };
                    foreach (var a in args) if (a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase)) o.Dir = a.Substring(5).Trim('"');
                    Installer.Install(o, s => { });
                    return 0;
                }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show(e.Message, Product.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }

        static bool Has(string[] args, string flag)
        {
            foreach (var a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}

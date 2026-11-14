using System;
using Microsoft.Win32;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// Windows normally shows permission (UAC) prompts on the "secure desktop": a separate, dimmed desktop that
    /// no app can capture, so the prompt never reaches the glasses (they show a frozen, dimmed image and the
    /// prompt sits invisibly on the AR main screen). Turning the secure desktop off for UAC
    /// (PromptOnSecureDesktop = 0, the same as the "Notify me ... (do not dim my desktop)" option in the UAC
    /// settings) makes the prompt a normal window on the main screen, so it shows in AR like anything else.
    /// Only the dimming/isolation changes: UAC itself stays on, and normal apps still can't click the prompt
    /// (Windows blocks that), only your real mouse and keyboard can.
    ///
    /// Changing it needs admin. The previous value is remembered (HKLM\SOFTWARE\XrealPcViewer) so turning it
    /// off again, or uninstalling, puts back exactly what was there before.
    /// Shared by the viewer (Settings checkbox, --uac-prompts) and the installer (/uacprompts).
    /// </summary>
    public static class UacPrompts
    {
        const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        const string Value = "PromptOnSecureDesktop";
        const string OurKey = @"SOFTWARE\XrealPcViewer";
        const string SavedValue = "PrevPromptOnSecureDesktop";   // -1 = the value didn't exist

        static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

        /// <summary>True when UAC prompts appear on the normal desktop (so they show in the glasses).</summary>
        public static bool ShownInGlasses
        {
            get
            {
                try
                {
                    using (var b = Hklm()) using (var k = b.OpenSubKey(PolicyKey))
                    {
                        if (k == null) return false;
                        object lua = k.GetValue("EnableLUA");
                        if (lua is int && (int)lua == 0) return true;          // UAC off entirely: no prompts at all
                        object v = k.GetValue(Value);
                        return v is int && (int)v == 0;                        // missing = Windows default (secure desktop on)
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>True if we changed the setting (and remember the old value to put back).</summary>
        public static bool ChangedByUs
        {
            get
            {
                try { using (var b = Hklm()) using (var k = b.OpenSubKey(OurKey)) return k != null && k.GetValue(SavedValue) != null; }
                catch { return false; }
            }
        }

        /// <summary>Needs admin. on: prompts on the normal desktop. off: back to what it was before we changed it.</summary>
        public static void Apply(bool showInGlasses)
        {
            using (var b = Hklm())
            using (var pol = b.CreateSubKey(PolicyKey))
            using (var our = b.CreateSubKey(OurKey))
            {
                if (showInGlasses)
                {
                    if (our.GetValue(SavedValue) == null)
                    {
                        object cur = pol.GetValue(Value);
                        our.SetValue(SavedValue, cur is int ? (int)cur : -1, RegistryValueKind.DWord);
                    }
                    pol.SetValue(Value, 0, RegistryValueKind.DWord);
                }
                else
                {
                    // "off" always means the secure desktop is back on (the Windows default)
                    object saved = our.GetValue(SavedValue);
                    int prev = saved is int ? (int)saved : -1;
                    if (prev == -1 || prev == 0) { if (pol.GetValue(Value) != null) pol.DeleteValue(Value); }
                    else pol.SetValue(Value, prev, RegistryValueKind.DWord);
                    if (saved != null) our.DeleteValue(SavedValue);
                }
            }
            if (!showInGlasses) TryDeleteOurKey();
        }

        /// <summary>Uninstall: put back exactly the old value, but only if we were the ones who changed it.</summary>
        public static void RestoreIfChangedByUs()
        {
            using (var b = Hklm())
            using (var our = b.OpenSubKey(OurKey, true))
            {
                object saved = our == null ? null : our.GetValue(SavedValue);
                if (!(saved is int)) return;
                using (var pol = b.CreateSubKey(PolicyKey))
                {
                    if ((int)saved == -1) { if (pol.GetValue(Value) != null) pol.DeleteValue(Value); }
                    else pol.SetValue(Value, (int)saved, RegistryValueKind.DWord);
                }
                our.DeleteValue(SavedValue);
            }
            TryDeleteOurKey();
        }

        static void TryDeleteOurKey()
        {
            try
            {
                using (var b = Hklm())
                {
                    using (var k = b.OpenSubKey(OurKey)) if (k == null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
                    b.DeleteSubKey(OurKey, false);
                }
            }
            catch { }
        }
    }
}

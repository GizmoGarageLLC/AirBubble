using System;
using System.Drawing;
using System.Windows.Forms;

namespace XrealDesktop.PcViewer
{
    /// <summary>Settings window (shown on the PC monitor). Changes apply immediately; 3D restarts the viewer.</summary>
    sealed class SettingsForm : Form
    {
        readonly Settings m_S;
        readonly Action m_Restart, m_Calibrate3D;
        bool m_Loading;

        public SettingsForm(Settings s, Action restartViewer, Action calibrate3D, string deviceName)
        {
            m_S = s; m_Restart = restartViewer; m_Calibrate3D = calibrate3D;
            Text = "AirBubble settings";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; MinimizeBox = false;
            SizeGripStyle = SizeGripStyle.Show;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5f);
            ShowInTaskbar = true;
            TopMost = true;
            MinimumSize = new Size(380, 260);

            // The settings live in a scrolling area; the Close button stays in a fixed bar underneath.
            // The table is never narrower than its content (scroll bars appear instead) and stretches when the window is wider.
            m_Scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var t = new TableLayoutPanel { Location = Point.Empty, ColumnCount = 3, Padding = new Padding(14, 14, 14, 6), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            m_Table = t;
            m_Scroll.Controls.Add(t);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 6, 14, 10) };
            Controls.Add(m_Scroll);
            Controls.Add(bar);

            m_Loading = true;
            AddHeader(t, "New screens and Arrange");
            AddSlider(t, "Distance", 50, 400, (int)(s.Distance * 100), v => string.Format("{0:F2} m", v / 100.0), v => { s.Distance = v / 100f; });
            AddSlider(t, "Screen size", 30, 300, (int)(s.PanelWidth * 100), v => string.Format("{0:F2} m", v / 100.0), v => { s.PanelWidth = v / 100f; });
            AddNote(t, "Each screen can also be moved, resized and pushed/pulled by its title bar in AR.");
            AddCheck(t, "Also show this PC's own screen in AR", s.ShowPcScreens, v => { s.ShowPcScreens = v; });
            AddScreensOption(t);

            AddHeader(t, "Mouse");
            AddSlider(t, "Cursor speed", 30, 300, (int)(s.MouseSpeed * 100), v => string.Format("{0:F1}x", v / 100.0), v => { s.MouseSpeed = v / 100f; });

            AddCheck(t, "Keep the mouse on the AR screens (it never goes onto this PC's own screen while the glasses are in use)", s.ConfineMouse, v => { s.ConfineMouse = v; });
            AddNote(t, "Ctrl+Alt+G still gives the mouse back to the normal desktop.");

            AddHeader(t, "3D");
            AddCheck(t, "Side-by-side 3D (each eye gets its own image; restarts the viewer)", s.Sbs3D, v =>
            {
                s.Sbs3D = v;
                s.Save();
                if (MessageBox.Show(this, "The viewer will restart and the glasses will blank for a moment.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                    m_Restart();
            });
            AddSlider(t, "Eye distance (IPD)", 50, 80, (int)s.IpdMm, v => v + " mm", v => { s.IpdMm = v; });
            AddCheck(t, "Match the edges of the two eye images (no flat/double strip at the left and right edges)", s.StereoEdgeMatch, v => { s.StereoEdgeMatch = v; });
            var cal = new Button { Text = "Calibrate 3D...  (Ctrl+Alt+K)", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
            cal.Click += delegate { if (m_Calibrate3D != null) m_Calibrate3D(); };
            t.Controls.Add(cal, 0, t.RowCount); t.SetColumnSpan(cal, 3); t.RowCount++;
            AddNote(t, "Shows a test pattern in the glasses: line up the two eye images (depth, up/down, eye distance) with the arrow keys.");

            AddHeader(t, "Tracking");
            AddSlider(t, "Field of view", 150, 400, (int)(s.FovV * 10), v => string.Format("{0:F1} deg", v / 10.0), v => { s.FovV = v / 10f; });
            AddNote(t, "If screens slide when you turn your head, adjust this until they hold still.");
            AddSlider(t, "Prediction", 0, 40, (int)s.PredictMs, v => v + " ms", v => { s.PredictMs = v; });
            AddSlider(t, "Stabilization", 0, 3, s.Stabilization, v => new[] { "Off", "Light", "Medium", "Strong" }[v], v => { s.Stabilization = v; });
            AddNote(t, "Ignores small head wobble (chewing, breathing, walking). Stronger = steadier, but screens trail slightly behind fast turns.");
            AddCheck(t, "Keep the main screen centred (absorbs slow sideways drift, at most 3 degrees a minute)", s.AutoDrift, v => { s.AutoDrift = v; });

            AddHeader(t, "Windows permission prompts (UAC)");
            bool uac = UacPrompts.ShownInGlasses;
            AddCheck(t, "Show permission (UAC) prompts in the glasses", uac, v => SetUacPrompts(v));
            AddNote(t, "Windows normally shows these on a separate dimmed \"secure desktop\" that no app can capture, so they never reach the glasses. " +
                       "This turns the dimming off (UAC stays on; other apps still can't click the prompt). Changing it asks for admin once.");

            AddHeader(t, "Bubble AI");
            AddCheck(t, "Bubble floats around in AR (click him to pop him)", s.ShowBubble, v => { s.ShowBubble = v; });
            AddCheck(t, "Bubble listens for his name and talks back (Claude + ElevenLabs). Popped, he only comes back when you say \"Bubble\".", s.BubbleAi, v => { s.BubbleAi = v; });
            AddCheck(t, "Spatial audio: bubbles' voices and pops come from where they float (stereo speakers or headphones)", s.BubbleSpatialAudio, v => { s.BubbleSpatialAudio = v; });
            AddKey(t, "Claude API key", "anthropic", Secrets.Anthropic != null);
            AddKey(t, "ElevenLabs API key", "elevenlabs", Secrets.ElevenLabs != null);
            AddCheck(t, "Let Bubble click, type and press keys for me", s.BubbleAppControl, v => { s.BubbleAppControl = v; });
            AddChoice(t, "Bubble works", Autonomy.Names, s.BubbleAutonomy, v =>
            {
                if (!ConfirmAutonomy(this, v)) return false;
                s.BubbleAutonomy = v; return true;
            });
            AddNote(t, "Ask every time: he asks before each click, typed text, key press and opened app or file.\n" +
                       "Semi-autonomous: routine steps just happen; he asks before anything large or possibly dangerous (deleting, sending, installing, running programs or commands, system settings).\n" +
                       "Fully autonomous: he also carries out large changes you asked for or clearly implied without asking. He still asks before large or risky things you didn't ask for, and always before commands that could wipe data. " +
                       "He can never click Windows permission prompts or administrator windows.\n" +
                       "Say \"stop\" or pop him to halt him at once. Everything he does is listed in %APPDATA%\\AirBubble\\bubble_actions.log.");
            AddNote(t, "Say \"Bubble\" (or press Ctrl+Alt+B), then talk. Say a helper's colour (\"Pink, ...\") to talk to that helper; pop a helper to dismiss it. " +
                       "Windows' own speech recognizer listens offline for the names; only what you say to a bubble goes to ElevenLabs (transcript, voice) and Claude. " +
                       "Screenshots are sent to Claude only when Bubble looks at a screen. Keys are stored encrypted for your Windows account.");

            AddHeader(t, "General");
            bool sw = false; try { sw = Settings.StartWithWindows; } catch { }
            AddCheck(t, "Start with Windows", sw, v => { try { Settings.StartWithWindows = v; } catch (Exception e) { MessageBox.Show(this, e.Message); } });
            if (!string.IsNullOrEmpty(deviceName)) AddNote(t, "Glasses: " + deviceName);

            var close = new Button { Text = "Close", AutoSize = true, Margin = new Padding(0) };
            close.Click += delegate { Close(); };
            bar.Controls.Add(close);
            AcceptButton = close; CancelButton = close;
            m_Loading = false;

            // Open at the size that shows everything; OnShown shrinks it to the screen if that is too big.
            m_Natural = t.GetPreferredSize(Size.Empty);
            ClientSize = new Size(m_Natural.Width, m_Natural.Height + bar.GetPreferredSize(Size.Empty).Height);
            m_Scroll.ClientSizeChanged += delegate { FitTable(); };
            FitTable();
        }

        readonly Panel m_Scroll;
        readonly TableLayoutPanel m_Table;
        Size m_Natural;

        /// <summary>Stretches the table to the window's width (never below its content width, so it scrolls sideways instead of clipping).</summary>
        void FitTable()
        {
            var min = new Size(Math.Max(m_Natural.Width, m_Scroll.ClientSize.Width), 0);
            if (m_Table.MinimumSize != min) m_Table.MinimumSize = min;
        }

        // The mouse wheel scrolls the page wherever the pointer is, instead of changing whichever slider has the focus.
        sealed class WheelFilter : IMessageFilter
        {
            readonly SettingsForm m_F;
            public WheelFilter(SettingsForm f) { m_F = f; }
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != 0x020A) return false;   // WM_MOUSEWHEEL
                var c = Control.FromChildHandle(m.HWnd);
                if (c == null || c.FindForm() != m_F) return false;
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                int lines = SystemInformation.MouseWheelScrollLines; if (lines <= 0 || lines > 20) lines = 3;
                var p = m_F.m_Scroll.AutoScrollPosition;   // reads negative, is set positive
                m_F.m_Scroll.AutoScrollPosition = new Point(-p.X, -p.Y - delta * lines * m_F.Font.Height / 120);
                return true;
            }
        }
        WheelFilter m_Wheel;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (m_Wheel == null) { m_Wheel = new WheelFilter(this); Application.AddMessageFilter(m_Wheel); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (m_Wheel != null) { Application.RemoveMessageFilter(m_Wheel); m_Wheel = null; }
            base.OnFormClosed(e);
        }

        void Changed() { if (!m_Loading) m_S.NotifyChanged(); }

        /// <summary>
        /// "Allow up to 8 screens": the virtual screens pool is the driver's config, so changing it restarts the driver
        /// (admin prompt) and the viewer. Unticking a change that is refused puts the tick back.
        /// </summary>
        void AddScreensOption(TableLayoutPanel t)
        {
            int n = Program.ConfiguredScreens();
            if (n < 0) { AddNote(t, "Virtual screens: the driver wasn't installed by AirBubble setup, so its number of screens is managed elsewhere."); return; }
            var c = new CheckBox { Text = "Allow up to " + Program.MaxScreens + " screens (quality may be degraded)", Checked = n > Program.DefaultScreens, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            bool reverting = false;
            c.CheckedChanged += delegate
            {
                if (m_Loading || reverting) return;
                int want = c.Checked ? Program.MaxScreens : Program.DefaultScreens;
                string msg = (c.Checked
                    ? "Up to " + Program.MaxScreens + " screens: every screen is captured and drawn live, so with many screens open the glasses can get jittery and less sharp, especially on integrated graphics.\n\n"
                    : "Back to " + Program.DefaultScreens + " screens (smoothest). Screens 5 to 8 close; their windows move to your other screens.\n\n") +
                    "Windows will ask for permission once, AirBubble restarts and the screens blink while the virtual screens driver restarts. " +
                    "Open screens come back as new screens: press Ctrl+Alt+N to add more.";
                if (MessageBox.Show(this, msg, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                {
                    reverting = true; c.Checked = !c.Checked; reverting = false; return;
                }
                Program.PendingScreens = want;
                m_Restart();
            };
            t.Controls.Add(c, 0, t.RowCount); t.SetColumnSpan(c, 3); t.RowCount++;
            AddNote(t, "Off: " + Program.DefaultScreens + " screens, smoothest. On: up to " + Program.MaxScreens + "; with many screens open (especially video) the glasses may stutter. Changing it asks for admin once.");
        }

        /// <summary>Runs this exe elevated with --uac-prompts on|off (the admin prompt for it shows on the PC screen).</summary>
        void SetUacPrompts(bool on)
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--uac-prompts " + (on ? "on" : "off"))
                    { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
                p.WaitForExit(30000);
                if (p.HasExited && p.ExitCode == 0) return;
                MessageBox.Show(this, "The setting could not be changed (code " + (p.HasExited ? p.ExitCode.ToString() : "timeout") + ").", Text);
            }
            catch (Exception e)
            {
                var w = e as System.ComponentModel.Win32Exception;
                if (w == null || w.NativeErrorCode != 1223) MessageBox.Show(this, e.Message, Text);   // 1223: declined
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Keep the whole window on the screen it opened on (small or highly scaled laptop screens).
            var wa = Screen.FromControl(this).WorkingArea;
            if (Height > wa.Height) { Height = wa.Height; Width += SystemInformation.VerticalScrollBarWidth; }   // room for the scroll bar
            if (Width > wa.Width) Width = wa.Width;
            Top= Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
        }

        static void AddHeader(TableLayoutPanel t, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, t.RowCount == 0 ? 0 : 7, 0, 2) };
            t.Controls.Add(l, 0, t.RowCount); t.SetColumnSpan(l, 3); t.RowCount++;
        }

        static void AddNote(TableLayoutPanel t, string text)
        {
            var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0), Margin = new Padding(3, 0, 3, 4) };
            t.Controls.Add(l, 0, t.RowCount); t.SetColumnSpan(l, 3); t.RowCount++;
        }

        void AddSlider(TableLayoutPanel t, string label, int min, int max, int value, Func<int, string> fmt, Action<int> apply)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 12, 1) };
            var tb = new TrackBar { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), TickStyle = TickStyle.None, Dock = DockStyle.Fill, AutoSize = false, Height = 26, MinimumSize = new Size(260, 26), Margin = new Padding(3, 1, 3, 1) };
            tb.SmallChange = 1; tb.LargeChange = Math.Max(1, (max - min) / 10);
            var v = new Label { Text = fmt(tb.Value), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 3, 1) };
            tb.ValueChanged += delegate { v.Text = fmt(tb.Value); apply(tb.Value); Changed(); };
            int r = t.RowCount++;
            t.Controls.Add(l, 0, r); t.Controls.Add(tb, 1, r); t.Controls.Add(v, 2, r);
        }

        /// <summary>A masked key box: shows "saved" when one is stored; Save stores it encrypted (empty = remove).</summary>
        void AddKey(TableLayoutPanel t, string label, string name, bool have)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 1) };
            var tb = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 1) };
            if (have) tb.Text = "••••••••";
            var save = new Button { Text = "Save", AutoSize = true, Margin = new Padding(3, 2, 3, 1) };
            save.Click += delegate
            {
                if (tb.Text == "••••••••") return;
                try { Secrets.Set(name, tb.Text); tb.Text = tb.Text.Length > 0 ? "••••••••" : ""; save.Text = "Saved"; }
                catch (Exception e) { MessageBox.Show(this, e.Message, Text); }
            };
            tb.TextChanged += delegate { save.Text = "Save"; };
            int r = t.RowCount++;
            t.Controls.Add(l, 0, r); t.Controls.Add(tb, 1, r); t.Controls.Add(save, 2, r);
        }

        /// <summary>Asks before switching Bubble to a mode where he acts without asking. True = go ahead.</summary>
        public static bool ConfirmAutonomy(IWin32Window owner, int level)
        {
            if (level != Autonomy.Full) return true;
            return MessageBox.Show(owner,
                "Fully autonomous: Bubble will click, type, press keys and open apps and files without asking, for anything you asked him to do or clearly implied. " +
                "That includes big changes such as deleting, sending or installing things.\n\n" +
                "He still asks before large or risky things you didn't ask for, and always before commands that could wipe data. He can't click Windows permission prompts.\n\n" +
                "Say \"stop\" or pop him to halt him at once.\n\nLet Bubble work fully on his own?",
                "Bubble: fully autonomous", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>A drop-down list. apply returns false to refuse the choice (the previous one comes back).</summary>
        void AddChoice(TableLayoutPanel t, string label, string[] items, int value, Func<int, bool> apply)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 1) };
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 1) };
            cb.Items.AddRange(items);
            int current = Math.Max(0, Math.Min(items.Length - 1, value));
            cb.SelectedIndex = current;
            bool reverting = false;
            cb.SelectedIndexChanged += delegate
            {
                if (m_Loading || reverting || cb.SelectedIndex == current) return;
                if (apply(cb.SelectedIndex)) { current = cb.SelectedIndex; Changed(); }
                else { reverting = true; cb.SelectedIndex = current; reverting = false; }
            };
            int r = t.RowCount++;
            t.Controls.Add(l, 0, r); t.Controls.Add(cb, 1, r); t.SetColumnSpan(cb, 2);
        }

        void AddCheck(TableLayoutPanel t, string label, bool value, Action<bool> apply)
        {
            var c = new CheckBox { Text = label, Checked = value, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            c.CheckedChanged += delegate { if (m_Loading) return; apply(c.Checked); Changed(); };
            t.Controls.Add(c, 0, t.RowCount); t.SetColumnSpan(c, 3); t.RowCount++;
        }
    }
}

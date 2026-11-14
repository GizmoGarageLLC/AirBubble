using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace XrealDesktop.PcViewer
{
    /// <summary>Bubble and his helper bubbles in AR: sprites, speech cards, listening ring, the pointing ring.</summary>
    sealed partial class ViewerForm
    {
        Crew m_Crew;
        readonly Dictionary<CrewAgent, Bubble> m_HelperSprites = new Dictionary<CrewAgent, Bubble>();   // render thread
        volatile Bubble m_HoverBubble;
        bool m_CrewSummoned, m_YesNoKeys;
        readonly Random m_Rnd = new Random();

        sealed class CardTex { public uint Tex; public int W, H; public long Used; }
        readonly Dictionary<string, CardTex> m_Cards = new Dictionary<string, CardTex>();
        static readonly Color[] s_TintColor = { Color.FromArgb(140, 196, 255), Color.FromArgb(255, 140, 210), Color.FromArgb(120, 230, 140), Color.FromArgb(255, 210, 90), Color.FromArgb(190, 150, 255), Color.FromArgb(255, 110, 110), Color.FromArgb(255, 170, 80), Color.FromArgb(90, 225, 215) };

        // ---------------------------------------------------------------- UI thread
        void StartCrewIfWanted()
        {
            if (m_Crew != null || !m_Settings.ShowBubble || !m_Settings.BubbleAi) return;
            try
            {
                var c = new Crew(m_Settings);
                c.Toast = s => Ui(() => Toast(s));
                m_Crew = c; m_CrewSummoned = false;
                if (!c.Ears.Ok) Toast("Bubble can't hear you: " + c.Ears.Error);
                else if (Secrets.Anthropic == null || Secrets.ElevenLabs == null) Toast("Bubble needs your Claude and ElevenLabs keys: Settings (Ctrl+Alt+S) > Bubble AI");
            }
            catch (Exception ex) { Log.W("Bubble AI failed to start: " + ex); }
        }

        void StopCrew()
        {
            var c = m_Crew; m_Crew = null;
            if (c != null) try { c.Dispose(); } catch { }
        }

        void CrewUiTick()
        {
            bool want = m_Settings.ShowBubble && m_Settings.BubbleAi;
            if (want && m_Crew == null && (m_SelfTestSeconds <= 0 || Program.AiTest)) StartCrewIfWanted();
            else if (!want && m_Crew != null) StopCrew();
            // while Bubble waits for a yes/no: Ctrl+Alt+Y = yes, Ctrl+Alt+N = no (N is "new screen" otherwise)
            bool q = m_Crew != null && m_Crew.PendingQuestion != null;
            if (q == m_YesNoKeys) return;
            m_YesNoKeys = q;
            uint mods = 0x0002 | 0x0001 | 0x4000;
            if (q) { UnregisterHotKey(Handle, (int)Hk.NewScreen); RegisterHotKey(Handle, (int)Hk.Yes, mods, 0x59); RegisterHotKey(Handle, (int)Hk.No, mods, 0x4E); }
            else { UnregisterHotKey(Handle, (int)Hk.Yes); UnregisterHotKey(Handle, (int)Hk.No); RegisterHotKey(Handle, (int)Hk.NewScreen, mods, 0x4E); }
        }

        // ---------------------------------------------------------------- render thread
        void UpdateBubble(double dt)
        {
            if (m_BubbleTestPlace) { m_BubbleTestPlace = false; m_Bubble.ShowAt(14, 5, 1.0); }
            if (!m_Settings.ShowBubble) { m_Bubble.Hovered = false; m_HoverBubble = null; return; }
            var main = MainScreen();
            double by = 0, bp = 0, bd = m_Settings.Distance;
            if (main != null && !main.Minimized) { by = main.Yaw; bp = main.Pitch; bd = main.Dist; }

            var crew = m_Crew;
            m_Bubble.AutoRespawn = crew == null;          // with his AI on, only his name brings him back
            if (crew != null)
            {
                if (!m_CrewSummoned) { m_CrewSummoned = true; m_Bubble.Summon(); }
                if (crew.SummonMain) { crew.SummonMain = false; m_Bubble.Summon(); }
                Mouth(crew, crew.Main, m_Bubble);
                foreach (var a in crew.Snapshot())
                    if (a.IsHelper && !m_HelperSprites.ContainsKey(a))
                    {
                        var b = new Bubble { Tint = a.Tint, AutoRespawn = false };
                        b.SummonAt(m_Bubble.Yaw + (m_Rnd.NextDouble() * 2 - 1) * 12, m_Bubble.Pitch + (m_Rnd.NextDouble() * 2 - 1) * 6, m_Bubble.Dist);
                        m_HelperSprites[a] = b;
                    }
            }
            bool free = m_Settings.BubbleFreeFloat, live = m_Input != null && m_Input.Active;
            // the patch of sky the open screens cover (degrees, yaw relative to the main screen): bubbles stay around it
            double y0 = -20, y1 = 20, p0 = bp - 12, p1 = bp + 12; bool any = false;
            foreach (var s in Snapshot())
            {
                if (s.Minimized) continue;
                double hw = Math.Atan(s.Width / 2 / s.Dist) / Deg, hh = Math.Atan(s.Height / 2 / s.Dist) / Deg + 2;   // + title bar
                double cy = Bubble.AngleDiff(s.Yaw, by);
                if (!any) { y0 = cy - hw; y1 = cy + hw; p0 = s.Pitch - hh; p1 = s.Pitch + hh; any = true; }
                else { y0 = Math.Min(y0, cy - hw); y1 = Math.Max(y1, cy + hw); p0 = Math.Min(p0, s.Pitch - hh); p1 = Math.Max(p1, s.Pitch + hh); }
            }
            m_Bubble.SetArea(by + y0, by + y1, p0, p1);
            m_Bubble.FreeFloat = free; m_Bubble.CursorLive = live;
            foreach (var kv in m_HelperSprites.ToList())
            {
                kv.Value.SetArea(by + y0, by + y1, p0, p1);
                kv.Value.FreeFloat = free; kv.Value.CursorLive = live;
                if (crew == null || kv.Key.Gone)
                {
                    if (kv.Value.Visible) kv.Value.Pop();
                    else if (kv.Value.Hidden) m_HelperSprites.Remove(kv.Key);
                }
                if (crew != null) Mouth(crew, kv.Key, kv.Value);
                kv.Value.Update(dt, by, bp, bd, m_CurYaw, m_CurPitch);
            }
            m_Bubble.Update(dt, by, bp, bd, m_CurYaw, m_CurPitch);
            var all = Sprites().Select(kv => kv.Key).ToList();
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++) Bubble.Collide(all[i], all[j]);

            // spatial audio: each bubble's direction in head space (right, up, back), so its voice and pops come from it
            bool spatial = m_Settings.BubbleSpatialAudio;
            Quat toHead = m_Rel.Conj();
            foreach (var b in all)
            {
                double wx, wy, wz, hx, hy, hz;
                Dir(b.Yaw, b.DrawPitch, out wx, out wy, out wz);
                toHead.Rotate(wx, wy, wz, out hx, out hy, out hz);
                b.Spot.Set(spatial && !b.Hidden, hx, hy, hz, b.Dist);
            }

            CrewAgent agent;
            var hov = m_Mode == Mode.Idle ? SpriteUnderCursor(out agent) : null;
            m_Bubble.Hovered = hov == m_Bubble;
            foreach (var b in m_HelperSprites.Values) b.Hovered = hov == b;
            m_HoverBubble = hov;
        }

        void Mouth(Crew crew, CrewAgent a, Bubble b)
        {
            a.Spot = b.Spot;
            var env = a.Envelope;
            b.Talking = a.Speaking && env != null;
            if (!b.Talking) return;
            int i = (int)((crew.NowMs - a.SpeakStartMs) / (1000.0 / 30));
            b.Mouth = i >= 0 && i < env.Length ? env[i] : 0;
        }

        IEnumerable<KeyValuePair<Bubble, CrewAgent>> Sprites()
        {
            yield return new KeyValuePair<Bubble, CrewAgent>(m_Bubble, m_Crew != null ? m_Crew.Main : null);
            foreach (var kv in m_HelperSprites) yield return new KeyValuePair<Bubble, CrewAgent>(kv.Value, kv.Key);
        }

        /// <summary>The bubble the AR cursor is on (in front of whatever else it points at), or null.</summary>
        Bubble SpriteUnderCursor(out CrewAgent agent)
        {
            agent = null;
            if (!m_Settings.ShowBubble || m_Input == null || !m_Input.Active) return null;
            double dx, dy, dz; Dir(m_CurYaw, m_CurPitch, out dx, out dy, out dz);
            Bubble best = null; double bt = double.MaxValue;
            foreach (var kv in Sprites())
            {
                double t = kv.Key.Hit(dx, dy, dz);
                if (t > 0 && t < bt) { bt = t; best = kv.Key; agent = kv.Value; }
            }
            if (best == null) return null;
            var h = HitTest();
            return (h.S == null && !h.OnDock) || bt < h.T ? best : null;
        }

        bool PopUnderCursor()
        {
            CrewAgent agent;
            var b = SpriteUnderCursor(out agent);
            if (b == null) return false;
            b.Pop();
            Log.W((b == m_Bubble ? "Bubble" : "Helper " + (agent != null ? agent.Name : "?")) + " popped");
            var crew = m_Crew;
            if (crew != null && agent != null) crew.Popped(agent);
            return true;
        }

        void DrawBubbles()
        {
            if (!m_Settings.ShowBubble) return;
            var crew = m_Crew;
            long now = crew != null ? crew.NowMs : 0;
            // far to near, and fully transparent texels (the sprite's square, the card's corners) never touch depth,
            // so overlapping bubbles don't cut boxes out of each other
            var sprites = Sprites().OrderByDescending(kv => kv.Key.Dist).ToList();
            Gl.Enable(Gl.ALPHA_TEST); Gl.AlphaFunc(Gl.GREATER, 0.02f);
            foreach (var kv in sprites) kv.Key.Draw();
            foreach (var kv in sprites)
            {
                var b = kv.Key;
                var a = kv.Value;
                if (crew == null || a == null || !b.Visible) continue;
                Color tint = s_TintColor[Math.Max(0, Math.Min(s_TintColor.Length - 1, a.Tint))];
                if (a.Listening) Ring(b, tint, now);
                string text = null;
                if (a == crew.Main && crew.PendingQuestion != null) text = crew.PendingQuestion + "\n(say yes or no, or Ctrl+Alt+Y / N)";
                else if (a.Listening && a.Card != null && a.Card != "Listening…") text = a.Card + "\n(listening…)";   // waiting for an answer
                else if (a.Listening) text = "Listening…";
                else if (a.Doing != null && now < a.DoingUntilMs && !a.Speaking) text = a.Doing + "…";   // acting on his own: say what
                else if (a.Thinking && !a.Speaking) text = new string('•', 1 + (int)(now / 350 % 3));
                else if (a.Card != null && now < a.CardUntilMs) text = a.Card;
                if (text != null) Card(b, text, tint);
            }
            Gl.Disable(Gl.ALPHA_TEST);
            DrawPointer(crew, now);
            // forget speech cards nobody has drawn for a while
            if (m_Cards.Count > 24)
                foreach (var k in m_Cards.Where(x => now - x.Value.Used > 10000).Select(x => x.Key).ToList()) { uint t = m_Cards[k].Tex; Gl.DeleteTextures(1, ref t); m_Cards.Remove(k); }
        }

        void Ring(Bubble b, Color c, long now)
        {
            double pulse = 0.5 + 0.5 * Math.Sin(now / 160.0);
            Gl.PushMatrix();
            Gl.MultMatrixd((Quat.AxisAngle(0, 1, 0, b.Yaw * Deg) * Quat.AxisAngle(1, 0, 0, b.DrawPitch * Deg)).ToGlMatrix());
            Gl.Translated(0, 0, -b.Dist + 0.002);
            Gl.Disable(Gl.TEXTURE_2D);
            Gl.Color4d(c.R / 255.0, c.G / 255.0, c.B / 255.0, 0.5 + 0.5 * pulse);
            Gl.LineWidth(3);
            double r = 0.062 + 0.006 * pulse;
            Gl.Begin(Gl.LINE_LOOP);
            for (int i = 0; i < 48; i++) Gl.Vertex3d(Math.Cos(i * Math.PI / 24) * r, Math.Sin(i * Math.PI / 24) * r, 0);
            Gl.End();
            Gl.Color4d(1, 1, 1, 1); Gl.Enable(Gl.TEXTURE_2D);
            Gl.PopMatrix();
        }

        /// <summary>A speech card to the right of a bubble.</summary>
        void Card(Bubble b, string text, Color tint)
        {
            var ct = CardFor(text, tint, true);
            double k = 0.00021 * b.Dist;                          // metres per card pixel (same angular size at any distance)
            double w = ct.W * k, h = ct.H * k, x0 = 0.058, y1 = Math.Min(0.03, h / 2);
            Gl.PushMatrix();
            Gl.MultMatrixd((Quat.AxisAngle(0, 1, 0, b.Yaw * Deg) * Quat.AxisAngle(1, 0, 0, b.DrawPitch * Deg)).ToGlMatrix());
            Gl.Translated(0, 0, -b.Dist + 0.004);
            Gl.Color4d(1, 1, 1, 1);
            Gl.BindTexture(Gl.TEXTURE_2D, ct.Tex);
            Quad(x0, y1 - h, x0 + w, y1, 0);
            Gl.PopMatrix();
        }

        CardTex CardFor(string text, Color tint, bool tail)
        {
            if (text.Length > 320) text = text.Substring(0, 317) + "…";
            string key = tint.ToArgb() + "|" + tail + "|" + text;
            CardTex ct;
            if (m_Cards.TryGetValue(key, out ct)) { ct.Used = m_Crew != null ? m_Crew.NowMs : 0; return ct; }
            const int maxW = 440, pad = 14, tailW = tailLen;
            using (var font = new Font("Segoe UI Semibold", 21f, GraphicsUnit.Pixel))
            using (var probe = new Bitmap(1, 1))
            using (var gp = Graphics.FromImage(probe))
            {
                var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };
                var size = gp.MeasureString(text, font, maxW - 2 * pad, fmt);
                int tw = (int)Math.Ceiling(size.Width), th = (int)Math.Min(Math.Ceiling(size.Height), 8 * font.Height);
                int W = tw + 2 * pad + (tail ? tailW : 0), H = th + 2 * pad - 4;
                using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    int ox = tail ? tailW : 0;
                    var r = new Rectangle(ox + 1, 1, W - ox - 3, H - 3);
                    using (var path = Round(r, 12))
                    using (var bg = new SolidBrush(Color.FromArgb(232, 18, 22, 38)))
                    using (var pen = new Pen(tint, 3))
                    {
                        g.FillPath(bg, path); g.DrawPath(pen, path);
                        if (tail)
                        {
                            var tri = new[] { new PointF(ox + 2, Math.Min(H / 2f, 30) - 9), new PointF(1, Math.Min(H / 2f, 30)), new PointF(ox + 2, Math.Min(H / 2f, 30) + 9) };
                            g.FillPolygon(bg, tri); g.DrawLines(pen, tri);
                        }
                    }
                    using (var fg = new SolidBrush(Color.FromArgb(255, 240, 244, 255)))
                        g.DrawString(text, font, fg, new RectangleF(ox + pad, pad - 2, tw + 2, th), fmt);
                    ct = new CardTex { Tex = Chrome.Upload(bmp, 0), W = W, H = H, Used = m_Crew != null ? m_Crew.NowMs : 0 };
                }
            }
            m_Cards[key] = ct;
            return ct;
        }
        const int tailLen = 14;

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>Bubble's "look here" ring on one of the screens.</summary>
        void DrawPointer(Crew crew, long now)
        {
            if (crew == null) return;
            var p = crew.Pointer;
            if (p == null || now > p.UntilMs) return;
            var s = Snapshot().Find(x => x.Mon != null && x.Mon.DeviceName == p.Device && !x.Minimized);
            if (s == null) return;
            double lx = ((double)p.X / s.Mon.Bounds.Width - 0.5) * s.Width, ly = (0.5 - (double)p.Y / s.Mon.Bounds.Height) * s.Height;
            double pulse = 0.5 + 0.5 * Math.Sin(now / 140.0), r = s.Width * (0.018 + 0.006 * pulse);
            Gl.PushMatrix();
            Gl.MultMatrixd(s.Pose.ToGlMatrix());
            Gl.Translated(0, 0, -s.Dist + 0.004);
            Gl.Disable(Gl.TEXTURE_2D);
            Gl.LineWidth(4);
            for (int ring = 0; ring < 2; ring++)
            {
                double rr = r * (ring == 0 ? 1 : 1.35);
                Gl.Color4d(1, 0.85, 0.25, ring == 0 ? 1 : 0.45 * pulse);
                Gl.Begin(Gl.LINE_LOOP);
                for (int i = 0; i < 40; i++) Gl.Vertex3d(lx + Math.Cos(i * Math.PI / 20) * rr, ly + Math.Sin(i * Math.PI / 20) * rr, 0);
                Gl.End();
            }
            Gl.Color4d(1, 1, 1, 1); Gl.Enable(Gl.TEXTURE_2D);
            if (!string.IsNullOrEmpty(p.Label))
            {
                var ct = CardFor(p.Label, Color.FromArgb(255, 216, 64), false);
                double k = s.Width / 1600.0, w = ct.W * k, h = ct.H * k;
                Quad(lx - w / 2, ly + r * 1.5, lx + w / 2, ly + r * 1.5 + h, 0.001);
            }
            Gl.PopMatrix();
        }
    }
}

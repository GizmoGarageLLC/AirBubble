using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// "Bubble": a 16-bit style pixel-art soap bubble with big eyes and a toothy grin (fan art, drawn in code)
    /// that floats around you in AR. Click him with the AR cursor to pop him; a new one floats in a few
    /// seconds later. His eyes follow the cursor. Render thread only (Update/Draw/TryPop).
    /// </summary>
    sealed class Bubble
    {
        const double Deg = Math.PI / 180;
        const int N = 32;              // sprite size (pixels)
        const int PN = 48;             // pop-animation canvas (pixels)
        const double Size = 0.10;      // m, sprite quad width at scale 1
        const double PopTime = 0.42;   // s

        enum State { Hidden, Appearing, Floating, Popping }
        State m_State = State.Hidden;
        readonly Random m_Rng = new Random();
        double m_T, m_StateT, m_NextBlink = 2, m_BlinkUntil, m_NextTarget;
        public double Yaw, Pitch, Dist = 1.1;
        double m_VYaw, m_VPitch, m_TYaw, m_TPitch, m_TDist = 1.1, m_Tilt, m_HideFor = 1.5;
        int m_PupilX, m_PupilY;
        public bool Hovered;
        public int Popped;
        public int Tint;                 // colour: 0 Bubble's blue, 1 pink, 2 green, 3 gold, 4 purple
        public bool AutoRespawn = true;  // false: after a pop he stays away until Summon()
        public bool Talking;             // mouth follows Mouth (0..1) instead of the big grin
        public float Mouth;
        public bool PopFinished;         // the pop animation has played out (and he isn't coming back by himself)
        bool m_Summon; bool m_HaveSpawn; double m_SpawnYaw, m_SpawnPitch, m_SpawnDist;
        public bool Hidden { get { return m_State == State.Hidden; } }

        // textures: face variants keyed by (blink, pupil dx, pupil dy); pop frames
        readonly Dictionary<int, uint> m_Tex = new Dictionary<int, uint>();
        uint[] m_PopTex;
        static byte[] s_PopWav, s_AppearWav;

        public bool Visible { get { return m_State == State.Appearing || m_State == State.Floating; } }

        // =====================================================================================================
        // behaviour
        // =====================================================================================================
        /// <summary>baseYaw/basePitch/baseDist: the main screen (he hangs around it, mostly off its middle).</summary>
        public void Update(double dt, double baseYaw, double basePitch, double baseDist, double curYaw, double curPitch)
        {
            m_T += dt; m_StateT += dt;
            double near = Clamp(baseDist * 0.7, 0.75, 1.5);
            switch (m_State)
            {
                case State.Hidden:
                    if (AutoRespawn ? m_StateT < m_HideFor : !m_Summon) return;
                    m_Summon = false; PopFinished = false;
                    // float in at a random spot around the main screen (or where we were told)
                    PickTarget(baseYaw, basePitch, near);
                    if (m_HaveSpawn) { m_TYaw = m_SpawnYaw; m_TPitch = m_SpawnPitch; m_TDist = m_SpawnDist; m_HaveSpawn = false; }
                    Yaw = m_TYaw; Pitch = m_TPitch; Dist = m_TDist; m_VYaw = m_VPitch = 0;
                    PickTarget(baseYaw, basePitch, near);
                    m_State = State.Appearing; m_StateT = 0;
                    PlaySound(ref s_AppearWav, false);
                    break;
                case State.Appearing:
                    if (m_StateT >= 0.5) { m_State = State.Floating; m_StateT = 0; }
                    break;
                case State.Popping:
                    if (m_StateT >= PopTime) { m_State = State.Hidden; m_StateT = 0; m_HideFor = 6 + m_Rng.NextDouble() * 8; if (!AutoRespawn) PopFinished = true; }
                    return;
            }

            // wander: steer toward a target point, a new one every few seconds
            if (Frozen) { m_VYaw = m_VPitch = 0; }
            else if (!FreeFloat) Hover(dt, curYaw, curPitch);
            else
            {
            m_HaveHome = m_HaveCursor = false;   // back in hover mode he settles wherever he is then
            if (m_T >= m_NextTarget || (Math.Abs(AngleDiff(m_TYaw, Yaw)) < 2.5 && Math.Abs(m_TPitch - Pitch) < 2)) PickTarget(baseYaw, basePitch, near);
            double ay = AngleDiff(m_TYaw, Yaw) * 0.55 - m_VYaw * 1.1, ap = (m_TPitch - Pitch) * 0.55 - m_VPitch * 1.1;
            m_VYaw += ay * dt; m_VPitch += ap * dt;
            double sp = Math.Sqrt(m_VYaw * m_VYaw + m_VPitch * m_VPitch), max = Hovered ? 4 : 11;   // deg/s; slows down when you aim at him
            if (sp > max) { m_VYaw *= max / sp; m_VPitch *= max / sp; }
            Yaw += m_VYaw * dt; Pitch = Clamp(Pitch + m_VPitch * dt, -45, 50);
            Dist += (m_TDist - Dist) * Math.Min(1, dt * 0.4);
            m_Tilt += (Clamp(-m_VYaw * 1.2, -14, 14) - m_Tilt) * Math.Min(1, dt * 3);
            }

            // blink now and then
            if (m_T >= m_NextBlink) { m_BlinkUntil = m_T + 0.13; m_NextBlink = m_T + 2.2 + m_Rng.NextDouble() * 3.5; }

            // eyes follow the cursor (whole pixels, max 1 each way)
            double dy = AngleDiff(curYaw, Yaw), dp = curPitch - Pitch;
            m_PupilX = dy < -2 ? 1 : dy > 2 ? -1 : 0;          // yaw grows to the left
            m_PupilY = dp > 2 ? -1 : dp < -2 ? 1 : 0;
        }

        /// <summary>false (default): he hovers around one spot and the AR cursor nudges him. true: he roams freely
        /// around the main screen and the cursor passes through him.</summary>
        public bool FreeFloat;
        /// <summary>The AR cursor is live (set each frame): only then does it push him.</summary>
        public bool CursorLive;
        double m_HomeYaw, m_HomePitch, m_LastCurYaw, m_LastCurPitch, m_LastPushT = -10;
        bool m_HaveCursor, m_HaveHome;

        const double PushMax = 7;       // deg/s: the fastest a cursor bump can send him; a faster cursor slides over him
        const double PushGain = 0.45;   // he moves at under half the cursor's speed, so the cursor always catches up and gets on top
        const double PushRim = 0.6;     // only the outer part of him gets pushed: once the cursor is this far in it's on top, no push

        /// <summary>Hover mode: drift gently around a home spot (the bob and wobble keep him floaty), get nudged
        /// by the cursor like a puck on ice, glide to a stop and make that the new home.</summary>
        void Hover(double dt, double curYaw, double curPitch)
        {
            if (!m_HaveHome || m_State == State.Appearing) { m_HomeYaw = Yaw; m_HomePitch = Pitch; m_HaveHome = true; }

            // cursor bump: only from cursor movement toward him, so a still cursor resting on him never shoves (you can click)
            if (CursorLive && dt > 0)
            {
                double cosP = Math.Cos(Pitch * Deg);
                double cvx = m_HaveCursor ? AngleDiff(curYaw, m_LastCurYaw) * cosP / dt : 0, cvy = m_HaveCursor ? (curPitch - m_LastCurPitch) / dt : 0;
                if (Math.Abs(cvx) * dt > 20 || Math.Abs(cvy) * dt > 20) cvx = cvy = 0;    // the cursor jumped (another screen / came back), not a push
                double nx = AngleDiff(Yaw, curYaw) * cosP, ny = Pitch - curPitch, d = Math.Sqrt(nx * nx + ny * ny);
                double rad = Math.Atan(Size * Scale * 0.5 / Dist) / Deg, contact = rad + 0.5;   // his angular radius plus the cursor tip
                if (d < contact && d > rad * PushRim)
                {
                    nx /= d; ny /= d;
                    double toward = cvx * nx + cvy * ny;                                     // cursor speed into him
                    if (toward > 0)
                    {
                        double want = Math.Min(toward * PushGain, PushMax);
                        double vn = m_VYaw * cosP * nx + m_VPitch * ny;
                        if (vn < want)
                        {
                            double add = (want - vn) * Math.Min(1, dt * 25);
                            m_VYaw += add * nx / cosP; m_VPitch += add * ny;
                            m_LastPushT = m_T;
                        }
                    }
                }
                m_LastCurYaw = curYaw; m_LastCurPitch = curPitch; m_HaveCursor = true;
            }
            else m_HaveCursor = false;

            bool gliding = m_T - m_LastPushT < 0.3 || Math.Sqrt(m_VYaw * m_VYaw + m_VPitch * m_VPitch) > 1.5;
            if (gliding)
            {
                // coast with water-like drag; wherever he comes to rest becomes his new spot
                double drag = Math.Exp(-3.0 * dt);
                m_VYaw *= drag; m_VPitch *= drag;
                m_HomeYaw = Yaw; m_HomePitch = Pitch;
                m_TYaw = Yaw; m_TPitch = Pitch; m_NextTarget = m_T + 1 + m_Rng.NextDouble() * 2;
            }
            else
            {
                // pushed (or left) too far from the screens: his spot slowly floats back within reach
                bool returning = false;
                if (m_HaveArea)
                {
                    const double leash = 10;
                    double cy = (m_AY0 + m_AY1) / 2, hw = (m_AY1 - m_AY0) / 2 + leash, cp = (m_AP0 + m_AP1) / 2, hh = (m_AP1 - m_AP0) / 2 + leash;
                    double ry = AngleDiff(m_HomeYaw, cy), rp = m_HomePitch - cp;
                    double ty = Clamp(ry, -hw, hw), tp = Clamp(rp, -hh, hh), k = Math.Min(1, dt * 0.5);
                    if (ty != ry || tp != rp) { m_HomeYaw += (ty - ry) * k; m_HomePitch += (tp - rp) * k; m_TYaw = m_HomeYaw; m_TPitch = m_HomePitch; m_NextTarget = m_T + 1; returning = true; }
                }
                // idle: a lazy little drift within a few degrees of home
                if (m_T >= m_NextTarget)
                {
                    m_TYaw = m_HomeYaw + (m_Rng.NextDouble() * 2 - 1) * 2.5;
                    m_TPitch = m_HomePitch + (m_Rng.NextDouble() * 2 - 1) * 1.5;
                    m_NextTarget = m_T + 3 + m_Rng.NextDouble() * 4;
                }
                double ay = AngleDiff(m_TYaw, Yaw) * 0.3 - m_VYaw * 1.3, ap = (m_TPitch - Pitch) * 0.3 - m_VPitch * 1.3;
                m_VYaw += ay * dt; m_VPitch += ap * dt;
                double sp = Math.Sqrt(m_VYaw * m_VYaw + m_VPitch * m_VPitch), max = returning ? 5 : 1.2;   // deg/s
                if (sp > max) { m_VYaw *= max / sp; m_VPitch *= max / sp; }
            }
            Yaw += m_VYaw * dt; Pitch = Clamp(Pitch + m_VPitch * dt, -45, 50);
            Dist += (m_TDist - Dist) * Math.Min(1, dt * 0.4);
            m_Tilt += (Clamp(-m_VYaw * 1.2, -14, 14) - m_Tilt) * Math.Min(1, dt * 3);
        }

        // the area the screens cover (degrees; set every frame by the viewer)
        double m_AY0 = -25, m_AY1 = 25, m_AP0 = -12, m_AP1 = 12; bool m_HaveArea;
        const double Outside = 7, Inside = 4;   // the band bubbles live in: up to 7 deg beyond the screens' edges, 4 deg in over them
        public void SetArea(double yaw0, double yaw1, double pitch0, double pitch1) { m_AY0 = yaw0; m_AY1 = yaw1; m_AP0 = pitch0; m_AP1 = pitch1; m_HaveArea = true; }

        void PickTarget(double baseYaw, double basePitch, double near)
        {
            if (!m_HaveArea) { m_AY0 = baseYaw - 25; m_AY1 = baseYaw + 25; m_AP0 = basePitch - 12; m_AP1 = basePitch + 12; }
            double cy = (m_AY0 + m_AY1) / 2, hw = (m_AY1 - m_AY0) / 2, cp = (m_AP0 + m_AP1) / 2, hh = (m_AP1 - m_AP0) / 2;
            for (int i = 0; i < 16; i++)
            {
                // a spot in the band hugging the screens' outline, so he's always within easy reach of the cursor
                m_TYaw = cy + (m_Rng.NextDouble() * 2 - 1) * (hw + Outside);
                m_TPitch = Clamp(cp + (m_Rng.NextDouble() * 2 - 1) * (hh + Outside), -45, 50);
                double iny = hw - Math.Abs(m_TYaw - cy), inp = hh - Math.Abs(m_TPitch - cp);   // how far inside the edges
                if (Math.Min(iny, inp) < Inside) break;                                          // near an edge, not over the middle
            }
            m_TDist = near * (0.85 + m_Rng.NextDouble() * 0.3);
            m_NextTarget = m_T + 4 + m_Rng.NextDouble() * 5;
        }

        double Scale
        {
            get
            {
                if (m_State == State.Appearing) { double k = m_StateT / 0.5; return k < 0.7 ? k / 0.7 * 1.15 : 1.15 - (k - 0.7) / 0.3 * 0.15; }   // grow with a little overshoot
                return 1;
            }
        }

        /// <summary>Angular hit test against the cursor ray. Returns the distance along the ray, or -1.</summary>
        public double Hit(double dx, double dy, double dz)
        {
            if (!Visible) return -1;
            double bx, by, bz; Dir(Yaw, Pitch + Bob, out bx, out by, out bz);
            double cos = dx * bx + dy * by + dz * bz;
            double r = Math.Atan(Size * Scale * 0.5 / Dist) * 1.1;          // a little generous
            return cos >= Math.Cos(r) ? Dist : -1;
        }

        /// <summary>Self-test: show him right here and keep him still.</summary>
        public void ShowAt(double yaw, double pitch, double dist)
        {
            Yaw = m_TYaw = yaw; Pitch = m_TPitch = pitch; Dist = m_TDist = dist; m_VYaw = m_VPitch = m_Tilt = 0;
            m_State = State.Floating; m_StateT = 0; Frozen = true;
        }
        public bool Frozen;

        /// <summary>Come back (only used when AutoRespawn is off). Optionally at a given spot.</summary>
        public void Summon() { if (m_State == State.Hidden) { m_Summon = true; Frozen = false; } }
        public void SummonAt(double yaw, double pitch, double dist) { m_SpawnYaw = yaw; m_SpawnPitch = pitch; m_SpawnDist = dist; m_HaveSpawn = true; Summon(); }

        public void Pop()
        {
            if (!Visible) return;
            m_State = State.Popping; m_StateT = 0; Popped++; Hovered = false;
            PlaySound(ref s_PopWav, true);
        }

        /// <summary>Bubbles bounce off each other: if two overlap as seen from your head, push them apart and
        /// swap their velocities along the line between them (a springy elastic bump), then both wander off elsewhere.</summary>
        public static void Collide(Bubble a, Bubble b)
        {
            if (!a.Visible || !b.Visible) return;
            double cosP = Math.Cos((a.Pitch + b.Pitch) * 0.5 * Deg);
            double dx = AngleDiff(b.Yaw, a.Yaw) * cosP, dy = b.Pitch - a.Pitch;
            double d = Math.Sqrt(dx * dx + dy * dy);
            double ra = Math.Atan(Size * 0.5 / a.Dist) / Deg, rb = Math.Atan(Size * 0.5 / b.Dist) / Deg;   // angular radii (deg)
            double min = (ra + rb) * 0.95;
            if (d >= min) return;
            if (d < 1e-4) { dx = 1; dy = 0; d = 1e-4; } else { dx /= d; dy /= d; }
            // separate (half each)
            double push = (min - d) * 0.5;
            a.Yaw -= dx * push / cosP; a.Pitch -= dy * push;
            b.Yaw += dx * push / cosP; b.Pitch += dy * push;
            // bounce: exchange the normal components of their velocities (deg/s, yaw scaled to real angle)
            double va = a.m_VYaw * cosP * dx + a.m_VPitch * dy, vb = b.m_VYaw * cosP * dx + b.m_VPitch * dy;
            if (va - vb <= 0) return;                                      // already moving apart
            double kick = Math.Max(va - vb, 3) * 1.1;                     // a little springy
            a.m_VYaw -= kick * dx / cosP; a.m_VPitch -= kick * dy;
            b.m_VYaw += kick * dx / cosP; b.m_VPitch += kick * dy;
            a.m_NextTarget = a.m_T; b.m_NextTarget = b.m_T;               // and both head somewhere new
        }

        double Bob { get { return Math.Sin(m_T * 2.1) * 0.7; } }
        public double DrawPitch { get { return Pitch + Bob; } }

        // =====================================================================================================
        // drawing (inside the per-eye world view, depth test on, blending on, textures on)
        // =====================================================================================================
        public void Draw()
        {
            if (m_State == State.Hidden) return;
            if (m_PopTex == null) BuildTextures();
            double size = Size * Scale, sx = 1, sy = 1, shake = 0;
            uint tex;
            if (m_State == State.Popping)
            {
                int f = Math.Min(m_PopTex.Length - 1, (int)(m_StateT / PopTime * m_PopTex.Length));
                tex = m_PopTex[f]; size = Size * PN / N;
            }
            else
            {
                bool blink = m_T < m_BlinkUntil;
                int mouth = Talking ? (Mouth < 0.18f ? 0 : Mouth < 0.5f ? 1 : 2) : 2;
                int key = mouth * 1000 + (blink ? 100 : 0) + (m_PupilX + 1) * 10 + (m_PupilY + 1);
                if (!m_Tex.TryGetValue(key, out tex)) { using (var b = Face(blink, m_PupilX, m_PupilY, mouth, Tint)) tex = Upload(b); m_Tex[key] = tex; }
                double w = Math.Sin(m_T * 3.3) * 0.045;                      // gentle wobble (squash and stretch)
                sx = 1 + w; sy = 1 - w;
                if (Hovered) shake = Math.Sin(m_T * 40) * 0.12;               // nervous when you aim at him
            }
            Gl.PushMatrix();
            Gl.MultMatrixd((Quat.AxisAngle(0, 1, 0, (Yaw + shake) * Deg) * Quat.AxisAngle(1, 0, 0, (Pitch + Bob) * Deg)).ToGlMatrix());
            Gl.Translated(0, 0, -Dist);
            Gl.Rotated(m_Tilt, 0, 0, 1);
            Gl.Color4d(1, 1, 1, 1);
            Gl.BindTexture(Gl.TEXTURE_2D, tex);
            double up = 0;
            if (Tint > 0 && m_State != State.Popping) { up = size * 4 / N; size *= (double)HN / N; }   // helpers: taller canvas, body stays put
            double hw = size / 2 * sx, hh = size / 2 * sy;
            Gl.Begin(Gl.QUADS);
            Gl.TexCoord2d(0, 0); Gl.Vertex3d(-hw, hh + up, 0);
            Gl.TexCoord2d(0, 1); Gl.Vertex3d(-hw, -hh + up, 0);
            Gl.TexCoord2d(1, 1); Gl.Vertex3d(hw, -hh + up, 0);
            Gl.TexCoord2d(1, 0); Gl.Vertex3d(hw, hh + up, 0);
            Gl.End();
            Gl.PopMatrix();
        }

        // =====================================================================================================
        // pixel art
        // =====================================================================================================
        // 16-bit style palette
        static readonly Color Outline = Color.FromArgb(255, 40, 70, 150);
        static readonly Color Rim = Color.FromArgb(235, 140, 196, 255);
        static readonly Color Fill = Color.FromArgb(70, 220, 240, 255);
        static readonly Color Shade = Color.FromArgb(120, 160, 205, 255);
        static readonly Color Pink = Color.FromArgb(150, 255, 170, 230);
        static readonly Color Mint = Color.FromArgb(130, 175, 255, 215);
        static readonly Color Shine = Color.FromArgb(255, 255, 255, 255);
        static readonly Color Ink = Color.FromArgb(255, 26, 24, 46);
        static readonly Color EyeW = Color.FromArgb(255, 255, 255, 255);
        static readonly Color EyeS = Color.FromArgb(255, 200, 214, 240);
        static readonly Color MouthC = Color.FromArgb(255, 78, 14, 36);
        static readonly Color Tooth = Color.FromArgb(255, 255, 255, 255);
        static readonly Color ToothS = Color.FromArgb(255, 196, 206, 228);
        static readonly Color Tongue = Color.FromArgb(255, 232, 84, 124);
        static readonly Color TongueD = Color.FromArgb(255, 170, 46, 82);
        static readonly Color PopYellow = Color.FromArgb(255, 255, 224, 72);

        /// <summary>Helper colours (Tint = index + 1) and the thing each one wears. Bubble himself (Tint 0) wears nothing.</summary>
        public static readonly string[] HelperColors = { "Pink", "Green", "Gold", "Purple", "Red", "Orange", "Teal" };
        public static readonly string[] HelperLooks = { "a big pink bow", "a leafy sprout growing out of your head", "a little gold crown with gems",
                                                        "a starry wizard hat", "tiny devil horns", "a propeller beanie", "round glasses" };
        static readonly double[] s_TintHue = { -1, 325, 125, 45, 275, 2, 25, 175 };

        const int HN = 40;   // helper canvas: the 32 px body sits at (4, 8) with room for the hat on top

        static readonly Dictionary<char, Color> s_Acc = new Dictionary<char, Color>
        {
            { 'o', Color.FromArgb(255, 34, 26, 48) },                                    // outline
            { 'p', Color.FromArgb(255, 236, 72, 150) }, { 'P', Color.FromArgb(255, 255, 160, 205) }, { 'k', Color.FromArgb(255, 170, 30, 96) },
            { 'l', Color.FromArgb(255, 40, 150, 60) },  { 'L', Color.FromArgb(255, 130, 225, 100) }, { 's', Color.FromArgb(255, 92, 120, 50) },
            { 'y', Color.FromArgb(255, 255, 206, 58) }, { 'd', Color.FromArgb(255, 196, 140, 28) },  { 'r', Color.FromArgb(255, 230, 40, 60) }, { 'b', Color.FromArgb(255, 70, 200, 255) },
            { 'h', Color.FromArgb(255, 112, 56, 184) }, { 'H', Color.FromArgb(255, 168, 112, 232) }, { 'B', Color.FromArgb(255, 78, 36, 136) }, { '*', Color.FromArgb(255, 255, 236, 90) },
            { 'n', Color.FromArgb(255, 110, 18, 30) },  { 'N', Color.FromArgb(255, 176, 44, 56) },
            { 'c', Color.FromArgb(255, 220, 52, 52) },  { 'C', Color.FromArgb(255, 255, 214, 64) }, { 'w', Color.FromArgb(255, 80, 180, 255) }, { 'm', Color.FromArgb(255, 120, 120, 130) },
        };

        static void Art(Bitmap b, int ox, int oy, params string[] rows)
        {
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                {
                    Color c; if (!s_Acc.TryGetValue(rows[y][x], out c)) continue;
                    int px = ox + x, py = oy + y;
                    if (px >= 0 && py >= 0 && px < b.Width && py < b.Height) b.SetPixel(px, py, c);
                }
        }

        /// <summary>Puts a helper's face on the taller canvas and dresses it up.</summary>
        static Bitmap Dress(Bitmap face, int tint)
        {
            var b = new Bitmap(HN, HN, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) g.DrawImageUnscaled(face, 4, 8);
            face.Dispose();
            switch (tint)
            {
                case 1:   // Pink: a big bow on the top right
                    Art(b, 22, 1, "ooo.......ooo", "oPPpo...opPPo", "oPPPpo.opPPPo", "oPPPpkkkpPPPo", "opPPpkkkpPPpo", "oppppkkkppppo", "opppo.o.opppo", "ooo..opo..ooo", ".....o.o....."); break;
                case 2:   // Green: a sprout
                    Art(b, 14, 1, ".oo.....oo.", "oLLo...oLLo", "olLLo.oLLlo", ".ollloslllo.", "..oo.s.oo..", ".....s.....", ".....s....."); break;
                case 3:   // Gold: a crown with gems
                    Art(b, 14, 2, "o....o....o", "yo..oyo..oy", "yyooyyyooyy", "yyyyyyyyyyy", "yryyybyyyry", "yyyyyyyyyyy", "ddddddddddd"); break;
                case 4:   // Purple: a wizard hat with a star
                    Art(b, 13, 0, "........o.....", ".......ohh....", "......ohHh....", ".....ohhHho...", ".....oh*hho...", "....ohhhhhho..", "...ohhhhhhhho.", "oBBBBBBBBBBBBo", ".oooooooooooo."); break;
                case 5:   // Red: little horns
                    Art(b, 11, 4, "o...", "No..", "nNo.", "nnNo", "onno");
                    Art(b, 25, 4, "...o", "..oN", ".oNn", "oNnn", "onno"); break;
                case 6:   // Orange: a propeller beanie
                    Art(b, 14, 1, "wwwww.wwwww", ".....m.....", "...ooooo...", "..oCcCcCo..", ".occCcCcco.", "occCccCccCo", "ooooooooooo"); break;
                case 7:   // Teal: round glasses over the eyes (eyes at x 12-18 and 21-27, y 13-21)
                    Ring(b, 15.5, 17.5, 6.2); Ring(b, 24.5, 17.5, 6.2);
                    Art(b, 20, 16, "o"); Art(b, 20, 15, "o");
                    Art(b, 6, 16, "ooooo"); Art(b, 30, 16, "ooooo"); break;
            }
            return b;
        }

        static void Ring(Bitmap b, double cx, double cy, double r)
        {
            for (int y = 0; y < HN; y++)
                for (int x = 0; x < HN; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                    if (d > r - 0.6 && d <= r + 0.5) b.SetPixel(x, y, s_Acc['o']);
                    else if (d > r - 1.6 && d <= r - 0.6) b.SetPixel(x, y, s_Acc['m']);      // a grey frame inside the outline
                }
        }
        /// <summary>Recolours the bubble's skin for the helpers (eyes and mouth stay as they are).</summary>
        static Color T(Color c, int tint)
        {
            if (tint <= 0 || tint >= s_TintHue.Length) return c;
            float h = c.GetHue(), sat = c.GetSaturation(), l = c.GetBrightness();
            h = (float)s_TintHue[tint]; sat = Math.Min(1f, sat * 1.1f + 0.1f);
            // HSL -> RGB
            double q = l < 0.5 ? l * (1 + sat) : l + sat - l * sat, p = 2 * l - q;
            Func<double, int> ch = t => { t = (t % 1 + 1) % 1; double v = t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p; return (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255); };
            double hh = h / 360.0;
            return Color.FromArgb(c.A, ch(hh + 1 / 3.0), ch(hh), ch(hh - 1 / 3.0));
        }

        static void Body(Bitmap b, int ox, int oy) { Body(b, ox, oy, 0); }
        static void Body(Bitmap b, int ox, int oy, int tint)
        {
            Color Outline = T(Bubble.Outline, tint), Rim = T(Bubble.Rim, tint), Fill = T(Bubble.Fill, tint), Shade = T(Bubble.Shade, tint);
            const double c = 16, R = 15.5;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    double px = x + 0.5 - c, py = y + 0.5 - c, d = Math.Sqrt(px * px + py * py);
                    if (d > R) continue;
                    Color col;
                    if (d > R - 1.1) col = Outline;
                    else if (d > R - 2.3) col = Rim;
                    else
                    {
                        double nx = px / d, ny = py / d, light = (nx + ny) * 0.707;   // lower right is in shadow
                        bool dither = ((x + y) & 1) == 0;
                        col = Fill;
                        if (d > 8 && (light > 0.55 || (light > 0.35 && dither))) col = Shade;
                        if (d > R - 4.3 && d <= R - 2.3)
                        {
                            if (nx < -0.2 && ny > 0.25) col = Pink;                              // iridescent sheen
                            else if (nx > 0.3 && ny < -0.1 && (dither || ny < -0.5)) col = Mint;
                        }
                        // highlight streak (upper left) and glints
                        double ang = Math.Atan2(py, px) / Deg;
                        if (d > 10.4 && d < 12.4 && ang < -112 && ang > -160) col = Shine;
                    }
                    b.SetPixel(ox + x, oy + y, col);
                }
            foreach (var p in new[] { new Point(9, 9), new Point(10, 9), new Point(9, 10), new Point(23, 24), new Point(24, 23) })
                b.SetPixel(ox + p.X, oy + p.Y, Shine);
        }

        static Bitmap Face(bool blink, int pdx, int pdy) { return Face(blink, pdx, pdy, 2, 0); }
        /// <summary>mouth: 0 nearly closed, 1 half open, 2 the big grin.</summary>
        static Bitmap Face(bool blink, int pdx, int pdy, int mouth, int tint)
        {
            var b = new Bitmap(N, N, PixelFormat.Format32bppArgb);
            Body(b, 0, 0, tint);
            // eyes: big ovals, 7 x 9
            foreach (int ex in new[] { 8, 17 })
            {
                const int ey = 5, ew = 7, eh = 9;
                double cx = ex + ew / 2.0, cy = ey + eh / 2.0;
                if (blink)
                {
                    // closed: happy arcs  ^ ^
                    for (int x = 0; x < ew; x++) { int yy = ey + 5 - (x == 0 || x == ew - 1 ? 0 : x == 1 || x == ew - 2 ? 1 : 2); b.SetPixel(ex + x, yy, Ink); }
                    continue;
                }
                for (int y = ey; y < ey + eh; y++)
                    for (int x = ex; x < ex + ew; x++)
                    {
                        double u = (x + 0.5 - cx) / (ew / 2.0), v = (y + 0.5 - cy) / (eh / 2.0), r = u * u + v * v;
                        if (r > 1.08) continue;
                        b.SetPixel(x, y, r > 0.62 ? Ink : (u + v > 0.55 ? EyeS : EyeW));
                    }
                // pupil 2 x 3 with a glint
                int px = ex + (ex == 8 ? 3 : 2) + pdx, py = ey + 3 + pdy;   // pupils sit a little inward (cute), +-1 px toward the cursor
                for (int y = py; y < py + 3; y++) for (int x = px; x < px + 2; x++) b.SetPixel(x, y, Ink);
                b.SetPixel(px, py, EyeW);
            }
            // mouth: a huge grin full of pointy teeth
            const double mcx = 16, top = 16.5, halfW = 10;
            double depth = mouth == 2 ? 9 : mouth == 1 ? 6 : 3;
            for (int x = 6; x < 26; x++)
            {
                double u = (x + 0.5 - mcx) / halfW; if (Math.Abs(u) >= 1) continue;
                int y0 = (int)Math.Round(top + u * u * 1.5), y1 = (int)Math.Round(top + depth * Math.Sqrt(1 - u * u));
                if (y1 <= y0 + 1) { b.SetPixel(x, y0, Ink); continue; }
                for (int y = y0; y <= y1; y++)
                {
                    Color col = MouthC;
                    if (y == y0 || y == y1) col = Ink;
                    else if (y == y0 + 1) col = x % 3 == 0 ? ToothS : Tooth;                         // upper teeth
                    else if (y == y0 + 2 && y1 - y0 > 5) col = x % 3 == 1 ? Tooth : MouthC;           // their points
                    else if (y == y1 - 1 && y1 - y0 > 4) col = x % 3 == 2 ? ToothS : Tooth;          // lower teeth
                    else if (y == y1 - 2 && y1 - y0 > 6) col = x % 3 == 0 ? Tooth : MouthC;
                    else
                    {
                        double tu = (x + 0.5 - mcx) / 4.5, tv = (y + 0.5 - (y1 - 2.2)) / 2.4;   // tongue
                        if (tu * tu + tv * tv < 1) col = tv > 0.1 ? TongueD : Tongue;
                    }
                    b.SetPixel(x, y, col);
                }
                // close the mouth corners
                if (Math.Abs(u) > 0.9) b.SetPixel(x, y0, Ink);
            }
            return tint > 0 ? Dress(b, tint) : b;
        }

        // 3x5 pixel font for "POP!"
        static readonly string[] s_Font = { "111101111100100", "111101101101111", "111101111100100", "010010010000010" };

        static Bitmap PopFrame(int f, int frames) { return PopFrame(f, frames, 0); }
        static Bitmap PopFrame(int f, int frames, int tint)
        {
            Color Rim = T(Bubble.Rim, tint);
            var b = new Bitmap(PN, PN, PixelFormat.Format32bppArgb);
            double k = (f + 1.0) / frames, c = PN / 2.0;
            var rng = new Random(7);
            if (f == 0)
            {
                // the skin tears: a broken ring and a flash
                for (int y = 0; y < PN; y++)
                    for (int x = 0; x < PN; x++)
                    {
                        double px = x + 0.5 - c, py = y + 0.5 - c, d = Math.Sqrt(px * px + py * py), a = Math.Atan2(py, px) / Deg;
                        if (d > 15 && d < 17.2 && ((int)((a + 180) / 22) & 1) == 0) b.SetPixel(x, y, (int)(a + 180) % 44 < 8 ? Shine : Rim);
                        else if (d < 6 && ((x + y) & 1) == 0) b.SetPixel(x, y, Color.FromArgb(150, 255, 255, 255));
                    }
                return b;
            }
            // droplets flying out, getting smaller and fainter
            int alpha = (int)(255 * (1 - k * 0.75));
            for (int i = 0; i < 14; i++)
            {
                double a = (i / 14.0 * 360 + rng.Next(-9, 9)) * Deg, r = 13 + k * (9 + rng.Next(0, 5));
                int x = (int)(c + Math.Cos(a) * r), y = (int)(c + Math.Sin(a) * r), s = f < 3 ? 2 : 1;
                Color col = Color.FromArgb(alpha, (i % 3 == 0 ? Shine : i % 3 == 1 ? Rim : Pink));
                for (int yy = y; yy < y + s; yy++) for (int xx = x; xx < x + s; xx++) if (xx >= 0 && yy >= 0 && xx < PN && yy < PN) b.SetPixel(xx, yy, col);
            }
            // sparkles
            for (int i = 0; i < 4 && f < frames - 1; i++)
            {
                double a = (45 + i * 90) * Deg, r = 7 + k * 6;
                int x = (int)(c + Math.Cos(a) * r), y = (int)(c + Math.Sin(a) * r);
                foreach (var p in new[] { new Point(0, 0), new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) })
                    if (x + p.X >= 0 && y + p.Y >= 0 && x + p.X < PN && y + p.Y < PN) b.SetPixel(x + p.X, y + p.Y, Color.FromArgb(alpha, PopYellow));
            }
            // "POP!" rising
            int tx = (PN - 15) / 2, ty = 6 - f;
            for (int ch = 0; ch < 4; ch++)
                for (int i = 0; i < 15; i++)
                {
                    if (s_Font[ch][i] != '1') continue;
                    int x = tx + ch * 4 + i % 3, y = ty + i / 3;
                    if (y < 0) continue;
                    for (int oy = -1; oy <= 1; oy++) for (int ox = -1; ox <= 1; ox++)
                    {
                        int xx = x + ox, yy = y + oy;
                        if (xx >= 0 && yy >= 0 && xx < PN && yy < PN && b.GetPixel(xx, yy).A == 0) b.SetPixel(xx, yy, Color.FromArgb(alpha, Ink));
                    }
                    b.SetPixel(x, y, Color.FromArgb(alpha, PopYellow));
                }
            return b;
        }

        void BuildTextures()
        {
            const int frames = 6;
            m_PopTex = new uint[frames];
            for (int i = 0; i < frames; i++) using (var b = PopFrame(i, frames, Tint)) m_PopTex[i] = Upload(b);
        }

        /// <summary>Saves the sprite sheet (faces + pop frames, 4x) for a look without the glasses.</summary>
        public static void SaveSheet(string path)
        {
            const int z = 4;
            var frames = new List<Bitmap> { Face(false, 0, 0), Face(false, -1, 1), Face(true, 0, 0), Face(false, 0, 0, 1, 0), Face(false, 0, 0, 0, 0) };
            for (int t = 1; t <= HelperColors.Length; t++) frames.Add(Face(false, 0, 0, 2, t));
            for (int i = 0; i < 6; i++) frames.Add(PopFrame(i, 6));
            int w = 0; foreach (var f in frames) w += f.Width * z + 8;
            using (var sheet = new Bitmap(w, PN * z, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(255, 30, 34, 48));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                int x = 0;
                foreach (var f in frames) { g.DrawImage(f, x, (PN - f.Height) * z / 2, f.Width * z, f.Height * z); x += f.Width * z + 8; f.Dispose(); }
                sheet.Save(path, ImageFormat.Png);
            }
        }

        static uint Upload(Bitmap b)
        {
            uint tex; Gl.GenTextures(1, out tex);
            Gl.BindTexture(Gl.TEXTURE_2D, tex);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);   // crisp pixels
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Gl.TexImage2D(Gl.TEXTURE_2D, 0, 0x8058 /*RGBA8*/, b.Width, b.Height, 0, Gl.BGRA, Gl.UNSIGNED_BYTE, bd.Scan0); }
            finally { b.UnlockBits(bd); }
            return tex;
        }

        // =====================================================================================================
        // chiptune sound effects (made in code, played on the default audio device)
        // =====================================================================================================
        /// <summary>Where this bubble is around the listener's head (set every frame by the viewer): its sounds and voice come from there.</summary>
        public readonly SoundSpot Spot = new SoundSpot();

        void PlaySound(ref byte[] wav, bool pop)
        {
            try
            {
                if (wav == null) wav = pop ? Synth(0.11, 1100, 180, true) : Synth(0.14, 300, 700, false);
                // own audio stream (mixes with the bubbles' voices instead of waiting for them)
                var pcm = new short[(wav.Length - 44) / 2];
                Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length * 2);
                AudioOut.PlayAsync(pcm, 22050, Spot);
            }
            catch { }
        }

        /// <summary>Square-wave pitch sweep with a fast decay (and a noise click for the pop), 22 kHz 16-bit mono WAV.</summary>
        static byte[] Synth(double seconds, double f0, double f1, bool click)
        {
            const int rate = 22050;
            int n = (int)(rate * seconds);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(0x46464952); w.Write(36 + n * 2); w.Write(0x45564157);      // RIFF....WAVE
            w.Write(0x20746D66); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(0x61746164); w.Write(n * 2);                                 // data
            var rng = new Random(3); double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n, f = f0 * Math.Pow(f1 / f0, t);
                phase += f / rate;
                double s = (phase % 1) < 0.5 ? 1 : -1, env = Math.Exp(-t * 4.5) * Math.Min(1, i / 40.0);
                if (click && i < rate * 0.006) s = s * 0.4 + (rng.NextDouble() * 2 - 1) * 0.9;
                w.Write((short)(s * env * 0.22 * short.MaxValue));
            }
            w.Flush();
            return ms.ToArray();
        }

        // =====================================================================================================
        static void Dir(double yawDeg, double pitchDeg, out double x, out double y, out double z)
        {
            double cy = Math.Cos(pitchDeg * Deg);
            x = -Math.Sin(yawDeg * Deg) * cy; y = Math.Sin(pitchDeg * Deg); z = -Math.Cos(yawDeg * Deg) * cy;
        }
        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
        internal static double AngleDiff(double a, double b) { double d = (a - b) % 360; if (d > 180) d -= 360; if (d < -180) d += 360; return d; }
    }
}

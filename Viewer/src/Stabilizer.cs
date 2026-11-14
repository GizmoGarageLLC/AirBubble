using System;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// Head-pose "jitter buffer". Small involuntary head motion (chewing, breathing, the bob of walking) stays
    /// inside a small cone around the stabilised pose and is ignored, apart from a slow catch-up so the view
    /// settles where you are actually looking. Deliberate turns leave the cone and are followed at once - the
    /// stabilised pose trails by at most the cone radius, then catches up smoothly when you stop.
    /// </summary>
    public sealed class Stabilizer
    {
        public enum Level { Off = 0, Light = 1, Medium = 2, Strong = 3 }

        // cone radius (deg) and catch-up cut-off (Hz) per level
        static readonly double[] Radius = { 0, 0.35, 0.8, 1.6 };
        static readonly double[] CatchUpHz = { 0, 1.2, 0.7, 0.45 };

        Quat m_Q = Quat.Identity;
        bool m_Init;

        public Level Mode = Level.Medium;

        public void Reset() { m_Init = false; }

        public Quat Update(Quat raw, double dt)
        {
            if (Mode == Level.Off || dt <= 0) { m_Q = raw; m_Init = true; return raw; }
            if (!m_Init) { m_Q = raw; m_Init = true; return raw; }
            double r = Radius[(int)Mode] * Math.PI / 180;
            double d = Angle(m_Q, raw);
            // 1) never trail a deliberate movement by more than the cone radius
            if (d > r) m_Q = Slerp(m_Q, raw, (d - r) / d);
            // 2) inside the cone: drift slowly toward the (wobbling) head, i.e. towards its average
            double tau = 1.0 / (2 * Math.PI * CatchUpHz[(int)Mode]);
            m_Q = Slerp(m_Q, raw, dt / (tau + dt));
            return m_Q;
        }

        public static double Angle(Quat a, Quat b)
        {
            double dot = Math.Abs(a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z);
            return 2 * Math.Acos(Math.Min(1, dot));
        }

        public static Quat Slerp(Quat a, Quat b, double t)
        {
            double dot = a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            if (dot < 0) { b = new Quat(-b.W, -b.X, -b.Y, -b.Z); dot = -dot; }
            if (dot > 0.9995)
                return new Quat(a.W + (b.W - a.W) * t, a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t).Normalized();
            double th = Math.Acos(dot), s = Math.Sin(th);
            double wa = Math.Sin((1 - t) * th) / s, wb = Math.Sin(t * th) / s;
            return new Quat(a.W * wa + b.W * wb, a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb);
        }
    }
}

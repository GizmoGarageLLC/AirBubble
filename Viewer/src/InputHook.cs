using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    public enum InputKind { Move, Button, Wheel }
    public struct InputEvt
    {
        public InputKind Kind;
        public int Dx, Dy;          // Move: raw mouse counts
        public int Button;          // 0 left, 1 right, 2 middle, 3 x1, 4 x2
        public bool Down;
        public int Wheel;           // +/-120 per notch
        public bool Horizontal;
    }

    /// <summary>
    /// AR mouse mode: the physical mouse stops moving the Windows cursor and drives the AR cursor instead.
    /// A low-level hook swallows physical mouse messages; raw input supplies the movement deltas. Everything
    /// the viewer injects back (SendInput) carries InjectTag so it passes through.
    /// Safety: Ctrl+Alt+G (handled by the viewer) toggles the mode; the hook dies with the process.
    /// </summary>
    public sealed class InputHook : IDisposable
    {
        public static readonly IntPtr InjectTag = new IntPtr(0x58524541); // "XREA"
        public readonly ConcurrentQueue<InputEvt> Events = new ConcurrentQueue<InputEvt>();
        public volatile bool Active;
        public DateTime LastRaw = DateTime.MinValue;

        readonly LowLevelProc m_Proc;     // keep the delegate alive
        IntPtr m_Hook;

        public InputHook(IntPtr rawInputWindow)
        {
            m_Proc = Hook;
            m_Hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, m_Proc, Native.GetModuleHandle(null), 0);
            if (m_Hook == IntPtr.Zero) Log.W("WARNING: mouse hook failed (" + Marshal.GetLastWin32Error() + ")");
            var rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x01; rid[0].usUsage = 0x02; rid[0].dwFlags = Native.RIDEV_INPUTSINK; rid[0].hwndTarget = rawInputWindow;
            if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                Log.W("Raw input unavailable; using hook deltas");
        }

        IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            m_RawSinceHook = 0;
            if (nCode < 0 || !Active) return Native.CallNextHookEx(m_Hook, nCode, wParam, lParam);
            var info =(MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
            if (info.dwExtraInfo == InjectTag) return Native.CallNextHookEx(m_Hook, nCode, wParam, lParam);
            int msg = wParam.ToInt32();
            short hi = (short)((info.mouseData >> 16) & 0xFFFF);
            // Over a more privileged window (UAC prompt, admin apps) our injected clicks would be dropped, so the
            // real buttons/wheel go straight through to it (the real cursor already sits under the AR cursor).
            // A button that went through on its way down also goes through on its way up (never left stuck).
            int btn = ButtonOf(msg, hi), bit = btn >= 0 ? 1 << btn : 0;
            bool down = msg == 0x0201 || msg == 0x0204 || msg == 0x0207 || msg == 0x020B;
            if (btn >= 0 && !down && (m_Passed & bit) != 0) { m_Passed &= ~bit; return Native.CallNextHookEx(m_Hook, nCode, wParam, lParam); }
            if (PassClicks && msg != 0x0200 && !(btn >= 0 && !down))   // (an up whose down we handled stays ours)
            {
                if (btn >= 0) m_Passed |= bit;
                return Native.CallNextHookEx(m_Hook, nCode, wParam, lParam);
            }
            switch (msg)
            {
                case 0x0200: // WM_MOUSEMOVE: deltas normally come from raw input; fall back to hook positions.
                    if ((DateTime.UtcNow - LastRaw).TotalMilliseconds > 1000)
                    {
                        POINT cur; Native.GetCursorPos(out cur);
                        int dx = info.pt.X - cur.X, dy = info.pt.Y - cur.Y;
                        if (dx != 0 || dy != 0) Events.Enqueue(new InputEvt { Kind = InputKind.Move, Dx = dx, Dy = dy });
                    }
                    break;
                case 0x0201: Btn(0, true); break;  case 0x0202: Btn(0, false); break;
                case 0x0204: Btn(1, true); break;  case 0x0205: Btn(1, false); break;
                case 0x0207: Btn(2, true); break;  case 0x0208: Btn(2, false); break;
                case 0x020B: Btn(hi == 2 ? 4 : 3, true); break;
                case 0x020C: Btn(hi == 2 ? 4 : 3, false); break;
                case 0x020A: Events.Enqueue(new InputEvt { Kind = InputKind.Wheel, Wheel = hi }); break;
                case 0x020E: Events.Enqueue(new InputEvt { Kind = InputKind.Wheel, Wheel = hi, Horizontal = true }); break;
            }
            return new IntPtr(1); // swallow
        }

        void Btn(int b, bool down) { Events.Enqueue(new InputEvt { Kind = InputKind.Button, Button = b, Down = down }); }

        /// <summary>Set by the viewer while the AR cursor is over a window that only real input can click.</summary>
        public volatile bool PassClicks;
        int m_Passed;   // buttons whose press went straight through (hook thread only)

        static int ButtonOf(int msg, short hi)
        {
            switch (msg)
            {
                case 0x0201: case 0x0202: return 0;
                case 0x0204: case 0x0205: return 1;
                case 0x0207: case 0x0208: return 2;
                case 0x020B: case 0x020C: return hi == 2 ? 4 : 3;
                default: return -1;
            }
        }

        /// <summary>Call from the owner window's WndProc for WM_INPUT.</summary>
        public void OnRawInput(IntPtr hRaw)
        {
            if (!Active) return;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)), size = 0;
            Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0 || size > 1024) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, headerSize) != size) return;
                var header = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                if (header.dwType != Native.RIM_TYPEMOUSE || header.hDevice == IntPtr.Zero) return;   // hDevice 0 = injected
                var mouse = (RAWMOUSE)Marshal.PtrToStructure(buf + (int)headerSize, typeof(RAWMOUSE));
                if ((mouse.usFlags & 1) != 0) return;                                               // absolute devices
                LastRaw = DateTime.UtcNow;
                // Every physical move should also pass through the hook. If it stopped being called, Windows has
                // dropped it (it does that silently when the UI thread was busy for too long) and the real cursor
                // is moving freely again next to the AR one: put the hook back.
                if (++m_RawSinceHook > 60 && Environment.TickCount - m_LastReinstall > 3000) Reinstall();
                if (mouse.lLastX != 0 || mouse.lLastY != 0) Events.Enqueue(new InputEvt { Kind = InputKind.Move, Dx = mouse.lLastX, Dy = mouse.lLastY });
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        int m_RawSinceHook, m_LastReinstall;   // UI thread (hook and raw input both arrive there)
        public int Reinstalls;

        void Reinstall()
        {
            m_LastReinstall = Environment.TickCount; m_RawSinceHook = 0;
            if (m_Hook != IntPtr.Zero) Native.UnhookWindowsHookEx(m_Hook);
            m_Hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, m_Proc, Native.GetModuleHandle(null), 0);
            Reinstalls++;
            if (Reinstalls <= 5 || Reinstalls % 20 == 0)
                Log.W("Mouse hook had gone quiet while the mouse was moving: reinstalled (" + Reinstalls + (m_Hook == IntPtr.Zero ? ", FAILED " + Marshal.GetLastWin32Error() : "") + ")");
        }

        // ---------------- injection (tagged so the hook lets it through) ----------------
        static readonly int s_InputSize = Marshal.SizeOf(typeof(INPUT));
        static INPUT Mouse(uint flags, uint data)
        {
            var i = new INPUT(); i.type = Native.INPUT_MOUSE;
            i.u.mi.dwFlags = flags; i.u.mi.mouseData = data; i.u.mi.dwExtraInfo = InjectTag;
            return i;
        }

        public static void InjectButton(int b, bool down)
        {
            uint f, data = 0;
            switch (b)
            {
                case 1: f = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
                case 2: f = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
                case 3: f = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; break;
                case 4: f = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; break;
                default: f = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
            }
            Native.SendInput(1, new[] { Mouse(f, data) }, s_InputSize);
        }

        public static void InjectWheel(int delta, bool horizontal)
        {
            Native.SendInput(1, new[] { Mouse(horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL, unchecked((uint)delta)) }, s_InputSize);
        }

        public void Dispose()
        {
            Active = false;
            if (m_Hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(m_Hook); m_Hook = IntPtr.Zero; }
        }
    }
}

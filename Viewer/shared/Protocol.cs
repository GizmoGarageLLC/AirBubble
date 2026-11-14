// Shared wire protocol between the Windows host (XrealDesktopHost) and the glasses app.
// This exact file is compiled into both projects, so it is intentionally written in C# 5
// (the host builds with the csc.exe that ships with Windows / .NET Framework 4.8).
//
// Framing: [int32 length][byte type][payload...]   (length = 1 + payload length, little endian)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XrealDesktop.Net
{
    public static class Proto
    {
        public const int Version = 1;
        public const int TcpPort = 47801;
        public const int DiscoveryPort = 47800;
        public const string DiscoveryMagic = "XRDESK1";
        public const int MaxMessageBytes = 64 * 1024 * 1024;
    }

    public enum MsgType : byte
    {
        // ---- client (glasses) -> host (PC)
        Hello = 1,
        RequestSources = 2,
        Subscribe = 3,          // also used to update stream parameters
        Unsubscribe = 4,
        FrameAck = 5,
        MouseMove = 6,
        MouseButton = 7,
        MouseWheel = 8,
        Key = 9,
        ResizeWindow = 10,
        FocusSource = 11,
        SetCapture = 12,
        Ping = 13,
        RequestKeyframe = 14,

        // ---- host (PC) -> client (glasses)
        Welcome = 64,
        SourceList = 65,
        Frame = 66,
        SourceGone = 67,
        InputMouseDelta = 68,
        InputMouseButton = 69,
        InputWheel = 70,
        InputKey = 71,
        CaptureState = 72,
        Pong = 73,
        CursorShape = 74,
    }

    public enum SourceKind : byte { Monitor = 0, Window = 1 }

    public enum MouseBtn : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

    public enum CursorKind : byte
    {
        Arrow = 0, IBeam = 1, Hand = 2, Wait = 3, SizeWE = 4, SizeNS = 5,
        SizeNWSE = 6, SizeNESW = 7, SizeAll = 8, Cross = 9, No = 10, AppStarting = 11
    }

    public class SourceInfo
    {
        public int Id;
        public SourceKind Kind;
        public int Width;
        public int Height;
        public bool Primary;
        public string Title = "";
        public string Key = "";      // stable-ish identity used to restore layouts
    }

    public class TileData
    {
        public ushort X, Y, W, H;
        public byte[] Jpeg;
    }

    public class FrameData
    {
        public int SourceId;
        public int FrameId;
        public int Width;
        public int Height;
        public bool Keyframe;
        public List<TileData> Tiles = new List<TileData>();
    }

    /// <summary>Builds one framed message.</summary>
    public sealed class MsgWriter
    {
        readonly MemoryStream m_Stream;
        public readonly BinaryWriter W;

        public MsgWriter(MsgType type, int capacity)
        {
            m_Stream = new MemoryStream(capacity + 5);
            W = new BinaryWriter(m_Stream, Encoding.UTF8);
            W.Write(0);              // length placeholder
            W.Write((byte)type);
        }

        public MsgWriter(MsgType type) : this(type, 64) { }

        public void Str(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            W.Write(b.Length);
            W.Write(b);
        }

        public byte[] Finish()
        {
            W.Flush();
            byte[] buf = m_Stream.ToArray();
            int len = buf.Length - 4;
            buf[0] = (byte)len; buf[1] = (byte)(len >> 8); buf[2] = (byte)(len >> 16); buf[3] = (byte)(len >> 24);
            return buf;
        }
    }

    /// <summary>Reads framed messages from a stream (blocking).</summary>
    public sealed class MsgReader
    {
        readonly Stream m_Stream;
        readonly byte[] m_Header = new byte[4];

        public MsgReader(Stream s) { m_Stream = s; }

        static void ReadExactly(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }

        /// <summary>Returns the message type; payload reader is positioned after the type byte.</summary>
        public MsgType Next(out BinaryReader payload)
        {
            ReadExactly(m_Stream, m_Header, 4);
            int len = m_Header[0] | (m_Header[1] << 8) | (m_Header[2] << 16) | (m_Header[3] << 24);
            if (len < 1 || len > Proto.MaxMessageBytes) throw new InvalidDataException("Bad message length " + len);
            byte[] body = new byte[len];
            ReadExactly(m_Stream, body, len);
            payload = new BinaryReader(new MemoryStream(body, 1, len - 1, false), Encoding.UTF8);
            return (MsgType)body[0];
        }

        public static string Str(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1 << 20) throw new InvalidDataException("Bad string length");
            return Encoding.UTF8.GetString(r.ReadBytes(n));
        }
    }

    /// <summary>Encoders/decoders for every message. Both sides use these so they can never drift.</summary>
    public static class Msg
    {
        // ------------------------------------------------------------ client -> host
        public static byte[] Hello(string deviceName)
        {
            var m = new MsgWriter(MsgType.Hello);
            m.W.Write(Proto.Version); m.Str(deviceName);
            return m.Finish();
        }

        public static byte[] Simple(MsgType t) { return new MsgWriter(t).Finish(); }

        public static byte[] Subscribe(int sourceId, int maxFps, int quality, int maxWidth)
        {
            var m = new MsgWriter(MsgType.Subscribe);
            m.W.Write(sourceId); m.W.Write(maxFps); m.W.Write(quality); m.W.Write(maxWidth);
            return m.Finish();
        }

        public static byte[] SourceOnly(MsgType t, int sourceId)
        {
            var m = new MsgWriter(t);
            m.W.Write(sourceId);
            return m.Finish();
        }

        public static byte[] FrameAck(int sourceId, int frameId)
        {
            var m = new MsgWriter(MsgType.FrameAck);
            m.W.Write(sourceId); m.W.Write(frameId);
            return m.Finish();
        }

        public static byte[] MouseMove(int sourceId, float u, float v)
        {
            var m = new MsgWriter(MsgType.MouseMove);
            m.W.Write(sourceId); m.W.Write(u); m.W.Write(v);
            return m.Finish();
        }

        public static byte[] MouseButton(int sourceId, float u, float v, MouseBtn b, bool down)
        {
            var m = new MsgWriter(MsgType.MouseButton);
            m.W.Write(sourceId); m.W.Write(u); m.W.Write(v); m.W.Write((byte)b); m.W.Write(down);
            return m.Finish();
        }

        public static byte[] MouseWheel(int sourceId, float u, float v, int delta, bool horizontal)
        {
            var m = new MsgWriter(MsgType.MouseWheel);
            m.W.Write(sourceId); m.W.Write(u); m.W.Write(v); m.W.Write(delta); m.W.Write(horizontal);
            return m.Finish();
        }

        public static byte[] Key(MsgType t, ushort vk, ushort scan, bool extended, bool down)
        {
            var m = new MsgWriter(t);
            m.W.Write(vk); m.W.Write(scan); m.W.Write(extended); m.W.Write(down);
            return m.Finish();
        }

        public static byte[] ResizeWindow(int sourceId, int width, int height)
        {
            var m = new MsgWriter(MsgType.ResizeWindow);
            m.W.Write(sourceId); m.W.Write(width); m.W.Write(height);
            return m.Finish();
        }

        public static byte[] Bool(MsgType t, bool value)
        {
            var m = new MsgWriter(t);
            m.W.Write(value);
            return m.Finish();
        }

        public static byte[] Time(MsgType t, long ticks)
        {
            var m = new MsgWriter(t);
            m.W.Write(ticks);
            return m.Finish();
        }

        // ------------------------------------------------------------ host -> client
        public static byte[] Welcome(string hostName, bool captureActive)
        {
            var m = new MsgWriter(MsgType.Welcome);
            m.W.Write(Proto.Version); m.Str(hostName); m.W.Write(captureActive);
            return m.Finish();
        }

        public static byte[] SourceList(List<SourceInfo> list)
        {
            var m = new MsgWriter(MsgType.SourceList, 64 * list.Count + 16);
            m.W.Write(list.Count);
            foreach (var s in list)
            {
                m.W.Write(s.Id); m.W.Write((byte)s.Kind); m.W.Write(s.Width); m.W.Write(s.Height);
                m.W.Write(s.Primary); m.Str(s.Title); m.Str(s.Key);
            }
            return m.Finish();
        }

        public static List<SourceInfo> ReadSourceList(BinaryReader r)
        {
            int n = r.ReadInt32();
            var list = new List<SourceInfo>(n);
            for (int i = 0; i < n; i++)
            {
                var s = new SourceInfo();
                s.Id = r.ReadInt32(); s.Kind = (SourceKind)r.ReadByte(); s.Width = r.ReadInt32(); s.Height = r.ReadInt32();
                s.Primary = r.ReadBoolean(); s.Title = MsgReader.Str(r); s.Key = MsgReader.Str(r);
                list.Add(s);
            }
            return list;
        }

        public static byte[] Frame(FrameData f)
        {
            int size = 32;
            foreach (var t in f.Tiles) size += 12 + t.Jpeg.Length;
            var m = new MsgWriter(MsgType.Frame, size);
            m.W.Write(f.SourceId); m.W.Write(f.FrameId); m.W.Write(f.Width); m.W.Write(f.Height); m.W.Write(f.Keyframe);
            m.W.Write(f.Tiles.Count);
            foreach (var t in f.Tiles)
            {
                m.W.Write(t.X); m.W.Write(t.Y); m.W.Write(t.W); m.W.Write(t.H);
                m.W.Write(t.Jpeg.Length); m.W.Write(t.Jpeg);
            }
            return m.Finish();
        }

        public static FrameData ReadFrame(BinaryReader r)
        {
            var f = new FrameData();
            f.SourceId = r.ReadInt32(); f.FrameId = r.ReadInt32(); f.Width = r.ReadInt32(); f.Height = r.ReadInt32();
            f.Keyframe = r.ReadBoolean();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var t = new TileData();
                t.X = r.ReadUInt16(); t.Y = r.ReadUInt16(); t.W = r.ReadUInt16(); t.H = r.ReadUInt16();
                int len = r.ReadInt32();
                t.Jpeg = r.ReadBytes(len);
                f.Tiles.Add(t);
            }
            return f;
        }

        public static byte[] MouseDelta(int dx, int dy)
        {
            var m = new MsgWriter(MsgType.InputMouseDelta);
            m.W.Write(dx); m.W.Write(dy);
            return m.Finish();
        }

        public static byte[] InputMouseButton(MouseBtn b, bool down)
        {
            var m = new MsgWriter(MsgType.InputMouseButton);
            m.W.Write((byte)b); m.W.Write(down);
            return m.Finish();
        }

        public static byte[] InputWheel(int delta, bool horizontal)
        {
            var m = new MsgWriter(MsgType.InputWheel);
            m.W.Write(delta); m.W.Write(horizontal);
            return m.Finish();
        }

        public static byte[] Cursor(CursorKind k)
        {
            var m = new MsgWriter(MsgType.CursorShape);
            m.W.Write((byte)k);
            return m.Finish();
        }
    }
}

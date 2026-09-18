using System;
using System.Collections.Generic;
using System.Text;

namespace KoiCardTexTool
{
    /// <summary>Reference to a msgpack bin32 payload inside the card byte array.</summary>
    public sealed class BinRef
    {
        public int Off;
        public int Len;
        public byte[] B;
        public byte[] Bytes()
        {
            var o = new byte[Len];
            Buffer.BlockCopy(B, Off, o, 0, Len);
            return o;
        }
    }

    /// <summary>Minimal msgpack map (keys may be strings or ints, so no dictionary comparer games).</summary>
    public sealed class MMap
    {
        public readonly List<object> Keys = new List<object>();
        public readonly List<object> Vals = new List<object>();
        public object Get(string k)
        {
            for (int i = 0; i < Keys.Count; i++)
                if (Keys[i] as string == k) return Vals[i];
            return null;
        }
    }

    public sealed class MArr
    {
        public readonly List<object> Items = new List<object>();
    }

    /// <summary>Just enough MessagePack to read the MaterialEditor blobs and locate nested bins.</summary>
    public static class MP
    {
        public static object Read(byte[] b, ref int p)
        {
            byte c = b[p++];
            if (c <= 0x7f) return (long)c;
            if (c >= 0xe0) return (long)(sbyte)c;
            if (c >= 0xa0 && c <= 0xbf) return Str(b, ref p, c & 0x1f);
            if (c >= 0x90 && c <= 0x9f) return Arr(b, ref p, c & 0x0f);
            if (c >= 0x80 && c <= 0x8f) return Map(b, ref p, c & 0x0f);
            switch (c)
            {
                case 0xc0: return null;
                case 0xc2: return false;
                case 0xc3: return true;
                case 0xc4: return Bin(b, ref p, (int)U(b, ref p, 1));
                case 0xc5: return Bin(b, ref p, (int)U(b, ref p, 2));
                case 0xc6: return Bin(b, ref p, (int)U(b, ref p, 4));
                case 0xca: { var v = BitConverter.ToSingle(b, p); p += 4; return (double)v; }
                case 0xcb: { var v = BitConverter.ToDouble(b, p); p += 8; return v; }
                case 0xcc: return (long)U(b, ref p, 1);
                case 0xcd: return (long)U(b, ref p, 2);
                case 0xce: return (long)U(b, ref p, 4);
                case 0xcf: return (long)U(b, ref p, 8);
                case 0xd0: return (long)(sbyte)U(b, ref p, 1);
                case 0xd1: return (long)(short)U(b, ref p, 2);
                case 0xd2: return (long)(int)U(b, ref p, 4);
                case 0xd3: return (long)U(b, ref p, 8);
                case 0xd9: return Str(b, ref p, (int)U(b, ref p, 1));
                case 0xda: return Str(b, ref p, (int)U(b, ref p, 2));
                case 0xdb: return Str(b, ref p, (int)U(b, ref p, 4));
                case 0xdc: return Arr(b, ref p, (int)U(b, ref p, 2));
                case 0xdd: return Arr(b, ref p, (int)U(b, ref p, 4));
                case 0xde: return Map(b, ref p, (int)U(b, ref p, 2));
                case 0xdf: return Map(b, ref p, (int)U(b, ref p, 4));
            }
            throw new InvalidOperationException(string.Format("unknown msgpack byte 0x{0:X2} at {1}", c, p - 1));
        }

        static ulong U(byte[] b, ref int p, int n)
        {
            ulong v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | b[p + i];
            p += n;
            return v;
        }

        static string Str(byte[] b, ref int p, int n)
        {
            var s = Encoding.UTF8.GetString(b, p, n);
            p += n;
            return s;
        }

        static BinRef Bin(byte[] b, ref int p, int n)
        {
            var r = new BinRef { B = b, Off = p, Len = n };
            p += n;
            return r;
        }

        static MArr Arr(byte[] b, ref int p, int n)
        {
            var a = new MArr();
            for (int i = 0; i < n; i++) a.Items.Add(Read(b, ref p));
            return a;
        }

        static MMap Map(byte[] b, ref int p, int n)
        {
            var m = new MMap();
            for (int i = 0; i < n; i++)
            {
                m.Keys.Add(Read(b, ref p));
                m.Vals.Add(Read(b, ref p));
            }
            return m;
        }

        // ---- writing ----
        public static byte[] MapHeader(int n)
        {
            if (n <= 15) return new byte[] { (byte)(0x80 | n) };
            if (n <= 0xffff) return new byte[] { 0xde, (byte)(n >> 8), (byte)n };
            return new byte[] { 0xdf, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        }

        public static byte[] Int(long v)
        {
            if (v >= 0 && v <= 0x7f) return new byte[] { (byte)v };
            if (v >= 0 && v <= 0xff) return new byte[] { 0xcc, (byte)v };
            if (v >= 0 && v <= 0xffff) return new byte[] { 0xcd, (byte)(v >> 8), (byte)v };
            return new byte[] { 0xce, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        public static byte[] Bin32Header(int len)
        {
            return new byte[] { 0xc6, (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len };
        }

        public static byte[] Bin32(byte[] data)
        {
            var o = new byte[5 + data.Length];
            o[0] = 0xc6;
            o[1] = (byte)(data.Length >> 24);
            o[2] = (byte)(data.Length >> 16);
            o[3] = (byte)(data.Length >> 8);
            o[4] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, o, 5, data.Length);
            return o;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace KoiCardTexTool
{
    /// <summary>The block table at the front of a KoiKatu card's data, and keeping it true after the
    /// data behind it changed size.
    ///
    /// After the cover PNG (and, for a character card, a face PNG) a card has
    ///   int32 header length | msgpack header {"lstInfo": [{name, version, pos, size}, ...]} |
    ///   int64 data length   | the data (the blocks laid end to end; "pos" is relative to its start).
    /// Compressing the textures changes the size of the last block (KKEx), so its "size" and the data
    /// length have to follow, and the header length with them when an integer needs a wider encoding.
    /// Readers that trust these numbers (or that write the card back out) then see a consistent card.</summary>
    public static class CardHeader
    {
        sealed class Field { public int At; public int Width; public long Value; public long NewValue; }
        sealed class Block { public Field Pos; public Field Size; }

        static readonly byte[] LstInfoKey = { 0x81, 0xa7, (byte)'l', (byte)'s', (byte)'t', (byte)'I', (byte)'n', (byte)'f', (byte)'o' };

        /// <summary>`card` is the new card bytes. Everything before `spliceAt` is identical to the old card
        /// and the bytes from there on were replaced by something `delta` bytes longer (negative: shorter).
        /// Returns the card with the block table updated for that, or `card` itself, unchanged, when the
        /// card isn't laid out the way this expects (it never throws, and never makes a card worse).</summary>
        public static byte[] AfterSplice(byte[] card, int spliceAt, int delta)
        {
            if (delta == 0) return card;
            try { return Fix(card, spliceAt, delta) ?? card; }
            catch { return card; }
        }

        static byte[] Fix(byte[] b, int spliceAt, int delta)
        {
            int end = Card.PngEnd(b);
            if (end < 0) return null;
            int hs = new ReadOnlySpan<byte>(b, end, b.Length - end).IndexOf(LstInfoKey);
            if (hs < 0) return null;
            hs += end;                                    // where the msgpack header starts
            if (hs < 4) return null;

            int p = hs + LstInfoKey.Length;
            int count;
            byte c = b[p];
            if (c >= 0x90 && c <= 0x9f) { count = c & 0x0f; p += 1; }
            else if (c == 0xdc) { count = (b[p + 1] << 8) | b[p + 2]; p += 3; }
            else return null;

            var blocks = new List<Block>();
            for (int i = 0; i < count; i++)
            {
                int keys;
                c = b[p];
                if (c >= 0x80 && c <= 0x8f) { keys = c & 0x0f; p += 1; }
                else if (c == 0xde) { keys = (b[p + 1] << 8) | b[p + 2]; p += 3; }
                else return null;
                var blk = new Block();
                for (int k = 0; k < keys; k++)
                {
                    string key = MP.Read(b, ref p) as string;
                    if (key == "pos" || key == "size")
                    {
                        var f = ReadUInt(b, ref p);
                        if (f == null) return null;
                        if (key == "pos") blk.Pos = f; else blk.Size = f;
                    }
                    else MP.Read(b, ref p);               // name, version: skip
                }
                if (blk.Pos == null || blk.Size == null) return null;
                blocks.Add(blk);
            }

            int hdrEnd = p;
            if (BitConverter.ToInt32(b, hs - 4) != hdrEnd - hs) return null;   // not the header we think it is
            if (hdrEnd + 8 > b.Length) return null;
            long dataLen = BitConverter.ToInt64(b, hdrEnd);
            int dataStart = hdrEnd + 8;

            // the block the splice is inside of (the last one, KKEx, when it is the MaterialEditor data)
            long rel = spliceAt - dataStart;
            Block hit = null;
            foreach (var blk in blocks)
                if (blk.Pos.Value <= rel && rel < blk.Pos.Value + blk.Size.Value) { hit = blk; break; }
            if (hit == null) return null;

            var fields = new List<Field>();
            foreach (var blk in blocks)
            {
                blk.Pos.NewValue = blk.Pos.Value;
                blk.Size.NewValue = blk.Size.Value;
                if (blk == hit) blk.Size.NewValue += delta;
                else if (blk.Pos.Value > hit.Pos.Value) blk.Pos.NewValue += delta;   // blocks behind it move
                if (blk.Pos.NewValue < 0 || blk.Size.NewValue < 0) return null;
                fields.Add(blk.Pos); fields.Add(blk.Size);
            }
            long newDataLen = dataLen + delta;
            if (newDataLen < 0) return null;
            fields.Sort((x, y) => x.At.CompareTo(y.At));

            // the header again, with the changed integers re-encoded (never narrower than they were)
            var h = new MemoryStream();
            int cur = hs;
            foreach (var f in fields)
            {
                h.Write(b, cur, f.At - cur);
                if (f.NewValue == f.Value) h.Write(b, f.At, f.Width);
                else { var e = EncodeUInt(f.NewValue, f.Width); h.Write(e, 0, e.Length); }
                cur = f.At + f.Width;
            }
            h.Write(b, cur, hdrEnd - cur);
            byte[] nh = h.ToArray();

            var o = new byte[hs + nh.Length + 8 + (b.Length - dataStart)];
            Buffer.BlockCopy(b, 0, o, 0, hs - 4);
            WriteLE(o, hs - 4, nh.Length, 4);
            Buffer.BlockCopy(nh, 0, o, hs, nh.Length);
            WriteLE(o, hs + nh.Length, newDataLen, 8);
            Buffer.BlockCopy(b, dataStart, o, hs + nh.Length + 8, b.Length - dataStart);
            return o;
        }

        static Field ReadUInt(byte[] b, ref int p)
        {
            var f = new Field { At = p };
            byte c = b[p];
            if (c <= 0x7f) { f.Width = 1; f.Value = c; }
            else if (c == 0xcc) { f.Width = 2; f.Value = b[p + 1]; }
            else if (c == 0xcd) { f.Width = 3; f.Value = (b[p + 1] << 8) | b[p + 2]; }
            else if (c == 0xce) { f.Width = 5; f.Value = ((long)b[p + 1] << 24) | ((long)b[p + 2] << 16) | ((long)b[p + 3] << 8) | b[p + 4]; }
            else if (c == 0xcf) { f.Width = 9; long v = 0; for (int i = 1; i <= 8; i++) v = (v << 8) | b[p + i]; f.Value = v; }
            else return null;
            p += f.Width;
            return f;
        }

        static byte[] EncodeUInt(long v, int minWidth)
        {
            int w = v <= 0x7f ? 1 : v <= 0xff ? 2 : v <= 0xffff ? 3 : v <= 0xffffffffL ? 5 : 9;
            if (w < minWidth) w = minWidth;
            switch (w)
            {
                case 1: return new byte[] { (byte)v };
                case 2: return new byte[] { 0xcc, (byte)v };
                case 3: return new byte[] { 0xcd, (byte)(v >> 8), (byte)v };
                case 5: return new byte[] { 0xce, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                default:
                    var o = new byte[9];
                    o[0] = 0xcf;
                    for (int i = 0; i < 8; i++) o[1 + i] = (byte)(v >> (56 - 8 * i));
                    return o;
            }
        }

        static void WriteLE(byte[] o, int at, long v, int n)
        {
            for (int i = 0; i < n; i++) o[at + i] = (byte)(v >> (8 * i));
        }
    }
}

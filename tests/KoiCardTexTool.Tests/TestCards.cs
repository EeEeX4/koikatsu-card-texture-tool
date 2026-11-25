using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace KoiCardTexTool.Tests
{
    /// <summary>Builds small synthetic KoiKatu cards (no game assets in the repo) and reads their block table
    /// back with the tool's own msgpack reader, independently of CardHeader.</summary>
    static class TestCards
    {
        public sealed class Entry { public string Name; public long Pos; public long Size; }

        public sealed class Table
        {
            public List<Entry> Entries = new List<Entry>();
            public long DeclaredDataLength;
            public int DataStart;
            public int HeaderStart;
            public int HeaderLength;
        }

        static readonly byte[] LstInfo = { 0x81, 0xa7, (byte)'l', (byte)'s', (byte)'t', (byte)'I', (byte)'n', (byte)'f', (byte)'o' };

        public static byte[] Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var o = new byte[1 + b.Length];
            o[0] = (byte)(0xa0 | b.Length);                  // fixstr: names here are all under 32 bytes
            Array.Copy(b, 0, o, 1, b.Length);
            return o;
        }

        /// <summary>The way the game stores pos/size: the narrowest of uint16 / uint32.</summary>
        public static byte[] UInt(long v)
        {
            if (v <= 0xffff) return new byte[] { 0xcd, (byte)(v >> 8), (byte)v };
            return new byte[] { 0xce, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        public static byte[] Png()
        {
            var l = new List<byte>(Card.PngMagic);
            l.AddRange(new byte[] { 0, 0, 0, 0 });           // IEND chunk: length 0,
            l.AddRange(Encoding.ASCII.GetBytes("IEND"));     // type,
            l.AddRange(new byte[4]);                         // crc
            return l.ToArray();
        }

        /// <summary>A msgpack map {"a":1, "TextureDictionary": bin(payload), "z":2}, the bin written with a bin8 (8),
        /// bin16 (16) or bin32 (32) header.</summary>
        public static byte[] KkexBody(int binBits, byte[] payload)
        {
            var l = new List<byte> { 0x83 };
            l.AddRange(Str("a")); l.Add(1);
            l.AddRange(Str("TextureDictionary"));            // 17 bytes -> fixstr 0xb1
            int n = payload.Length;
            if (binBits == 8) { l.Add(0xc4); l.Add((byte)n); }
            else if (binBits == 16) { l.Add(0xc5); l.Add((byte)(n >> 8)); l.Add((byte)n); }
            else { l.Add(0xc6); l.Add((byte)(n >> 24)); l.Add((byte)(n >> 16)); l.Add((byte)(n >> 8)); l.Add((byte)n); }
            l.AddRange(payload);
            l.AddRange(Str("z")); l.Add(2);
            return l.ToArray();
        }

        public static byte[] Fill(int n, byte v)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = v;
            return b;
        }

        /// <summary>PNG, product number, tag, version, a face image, then int32 header length, the msgpack
        /// {"lstInfo": [...]} header, int64 data length and the blocks end to end (as a character card is laid out).</summary>
        public static byte[] Build(params KeyValuePair<string, byte[]>[] blocks)
        {
            var hdr = new List<byte> { 0x81 };
            hdr.AddRange(Str("lstInfo"));
            hdr.Add((byte)(0x90 | blocks.Length));
            var data = new List<byte>();
            long pos = 0;
            foreach (var blk in blocks)
            {
                hdr.Add(0x84);
                hdr.AddRange(Str("name")); hdr.AddRange(Str(blk.Key));
                hdr.AddRange(Str("version")); hdr.AddRange(Str("0.0.0"));
                hdr.AddRange(Str("pos")); hdr.AddRange(UInt(pos));
                hdr.AddRange(Str("size")); hdr.AddRange(UInt(blk.Value.Length));
                data.AddRange(blk.Value);
                pos += blk.Value.Length;
            }
            var o = new List<byte>(Png());
            o.AddRange(BitConverter.GetBytes(100));          // product number
            o.AddRange(Str("tag")); o.AddRange(Str("0.0.0"));
            o.AddRange(BitConverter.GetBytes(4)); o.AddRange(new byte[] { 1, 2, 3, 4 });   // face image
            o.AddRange(BitConverter.GetBytes(hdr.Count)); o.AddRange(hdr);
            o.AddRange(BitConverter.GetBytes((long)data.Count)); o.AddRange(data);
            return o.ToArray();
        }

        public static KeyValuePair<string, byte[]> Block(string name, byte[] body)
        {
            return new KeyValuePair<string, byte[]>(name, body);
        }

        /// <summary>Reads the block table. Asserts that the header length field is right.</summary>
        public static Table ReadTable(byte[] card)
        {
            int end = Card.PngEnd(card);
            Assert.True(end > 0, "no PNG");
            int hs = -1;
            for (int i = end; i + LstInfo.Length <= card.Length && hs < 0; i++)
            {
                bool ok = true;
                for (int k = 0; k < LstInfo.Length && ok; k++) ok = card[i + k] == LstInfo[k];
                if (ok) hs = i;
            }
            Assert.True(hs >= 4, "no lstInfo header");

            var t = new Table { HeaderStart = hs, HeaderLength = BitConverter.ToInt32(card, hs - 4) };
            int p = hs;
            var map = MP.Read(card, ref p) as MMap;
            Assert.NotNull(map);
            Assert.Equal(hs + t.HeaderLength, p);            // the header length field matches the header
            t.DeclaredDataLength = BitConverter.ToInt64(card, p);
            t.DataStart = p + 8;
            var list = map.Get("lstInfo") as MArr;
            Assert.NotNull(list);
            foreach (var item in list.Items)
            {
                var m = (MMap)item;
                t.Entries.Add(new Entry
                {
                    Name = (string)m.Get("name"),
                    Pos = Convert.ToInt64(m.Get("pos")),
                    Size = Convert.ToInt64(m.Get("size")),
                });
            }
            return t;
        }

        public static byte[] BlockBytes(byte[] card, Table t, string name)
        {
            foreach (var e in t.Entries)
            {
                if (e.Name != name) continue;
                var o = new byte[e.Size];
                Array.Copy(card, t.DataStart + e.Pos, o, 0, e.Size);
                return o;
            }
            throw new Exception("no block " + name);
        }

        public static Entry Find(Table t, string name)
        {
            foreach (var e in t.Entries) if (e.Name == name) return e;
            throw new Exception("no block " + name);
        }

        /// <summary>What CompressOne does to a card: replace the TextureDictionary bin, then fix the header.</summary>
        public static byte[] Repack(byte[] card, byte[] newPayload, bool fixHeader)
        {
            int kpos = Card.FindKey(card, "TextureDictionary");
            Assert.True(kpos > 0, "no TextureDictionary key");
            var bin = MP.Read(card, ref kpos) as BinRef;
            Assert.NotNull(bin);
            int at;
            byte[] o = MP.ReplaceBin(card, bin, newPayload, out at);
            return fixHeader ? CardHeader.AfterSplice(o, at, o.Length - card.Length) : o;
        }
    }
}

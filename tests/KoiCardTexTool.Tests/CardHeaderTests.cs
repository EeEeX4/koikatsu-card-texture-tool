using System;
using Xunit;
using static KoiCardTexTool.Tests.TestCards;

namespace KoiCardTexTool.Tests
{
    public class CardHeaderTests
    {
        static byte[] Custom() { return Fill(100, 0x43); }
        static byte[] Parameter() { return Fill(50, 0x50); }

        [Fact]
        public void Shrink_UpdatesTheLastBlockAndTheDataLength()
        {
            byte[] card = Build(Block("Custom", Custom()), Block("Parameter", Parameter()),
                                Block("KKEx", KkexBody(16, Fill(3000, 1))));
            byte[] o = Repack(card, Fill(1000, 2), true);

            var before = ReadTable(card);
            var after = ReadTable(o);
            long delta = o.Length - card.Length;
            Assert.True(delta < 0);
            Assert.Equal(before.Entries.Count, after.Entries.Count);
            Assert.Equal(Find(before, "Custom").Size, Find(after, "Custom").Size);
            Assert.Equal(Find(before, "Parameter").Pos, Find(after, "Parameter").Pos);
            Assert.Equal(Find(before, "KKEx").Pos, Find(after, "KKEx").Pos);
            Assert.Equal(Find(before, "KKEx").Size + delta, Find(after, "KKEx").Size);
            Assert.Equal(before.DeclaredDataLength + delta, after.DeclaredDataLength);
            Assert.Equal((long)(o.Length - after.DataStart), after.DeclaredDataLength);   // the data length is the data
        }

        [Fact]
        public void Shrink_TheLastBlockEndsExactlyWhereTheCardEnds()
        {
            byte[] card = Build(Block("Custom", Custom()), Block("KKEx", KkexBody(32, Fill(70000, 1))));
            byte[] o = Repack(card, Fill(60000, 2), true);
            var t = ReadTable(o);
            var kkex = Find(t, "KKEx");
            Assert.Equal((long)o.Length, t.DataStart + kkex.Pos + kkex.Size);

            // and that block is a whole msgpack map with the new texture dictionary
            byte[] body = BlockBytes(o, t, "KKEx");
            int p = 0;
            var map = MP.Read(body, ref p) as MMap;
            Assert.NotNull(map);
            Assert.Equal(body.Length, p);
            Assert.Equal(60000, ((BinRef)map.Get("TextureDictionary")).Len);
        }

        [Fact]
        public void Growth_PastUInt16_WidensTheIntegerAndTheHeaderLength()
        {
            byte[] card = Build(Block("Custom", Custom()), Block("KKEx", KkexBody(32, Fill(60000, 1))));
            var before = ReadTable(card);
            Assert.True(Find(before, "KKEx").Size < 65536);   // starts as a uint16 in the header

            byte[] o = Repack(card, Fill(90000, 2), true);
            var after = ReadTable(o);                         // (asserts the header length field is right)
            Assert.True(Find(after, "KKEx").Size > 65535);
            Assert.True(after.HeaderLength > before.HeaderLength);   // uint16 -> uint32 made the header longer
            Assert.Equal((long)(o.Length - after.DataStart), after.DeclaredDataLength);
            Assert.Equal((long)o.Length, after.DataStart + Find(after, "KKEx").Pos + Find(after, "KKEx").Size);
        }

        [Fact]
        public void BlocksBehindTheChangedOne_Move()
        {
            byte[] tail = new byte[] { 1, 2, 3, 4, 5 };
            byte[] card = Build(Block("Custom", Custom()), Block("KKEx", KkexBody(16, Fill(3000, 1))), Block("Tail", tail));
            byte[] o = Repack(card, Fill(3500, 2), true);

            var before = ReadTable(card);
            var after = ReadTable(o);
            long delta = o.Length - card.Length;
            Assert.Equal(Find(before, "KKEx").Size + delta, Find(after, "KKEx").Size);
            Assert.Equal(Find(before, "Tail").Pos + delta, Find(after, "Tail").Pos);
            Assert.Equal(Find(before, "Tail").Size, Find(after, "Tail").Size);
            Assert.Equal(Find(before, "Custom").Pos, Find(after, "Custom").Pos);
            Assert.Equal(tail, BlockBytes(o, after, "Tail"));
        }

        [Fact]
        public void WithoutTheHeaderFix_TheTableIsStale()
        {
            // documents what the fix is for: the splice alone leaves the old sizes in the header
            byte[] card = Build(Block("Custom", Custom()), Block("KKEx", KkexBody(16, Fill(3000, 1))));
            byte[] o = Repack(card, Fill(1000, 2), false);
            var t = ReadTable(o);
            Assert.NotEqual((long)(o.Length - t.DataStart), t.DeclaredDataLength);
        }

        [Fact]
        public void NoSizeChange_ReturnsTheSameArray()
        {
            byte[] card = Build(Block("KKEx", KkexBody(16, Fill(300, 1))));
            Assert.Same(card, CardHeader.AfterSplice(card, 100, 0));
        }

        [Fact]
        public void NotACard_ReturnsTheSameArray()
        {
            byte[] junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.Same(junk, CardHeader.AfterSplice(junk, 4, 3));
            byte[] png = Png();
            Assert.Same(png, CardHeader.AfterSplice(png, 4, 3));        // a PNG with nothing after it
        }

        [Fact]
        public void HeaderLengthThatDoesNotMatch_LeavesTheCardAlone()
        {
            byte[] card = Build(Block("KKEx", KkexBody(16, Fill(300, 1))));
            var t = ReadTable(card);
            byte[] bad = (byte[])card.Clone();
            Array.Copy(BitConverter.GetBytes(t.HeaderLength + 1), 0, bad, t.HeaderStart - 4, 4);
            Assert.Same(bad, CardHeader.AfterSplice(bad, t.DataStart + 10, 5));
        }

        [Fact]
        public void SpliceOutsideEveryBlock_LeavesTheCardAlone()
        {
            byte[] card = Build(Block("KKEx", KkexBody(16, Fill(300, 1))));
            Assert.Same(card, CardHeader.AfterSplice(card, 0, 5));      // before the data
            Assert.Same(card, CardHeader.AfterSplice(card, card.Length + 50, 5));   // past the last block
        }
    }
}

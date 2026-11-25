using System;
using Xunit;

namespace KoiCardTexTool.Tests
{
    public class MsgpackBinTests
    {
        [Theory]
        [InlineData(8, 100, 2)]
        [InlineData(16, 300, 3)]
        [InlineData(32, 70000, 5)]
        public void Read_RecordsTheRealHeaderLength(int bits, int size, int headerLen)
        {
            byte[] body = TestCards.KkexBody(bits, TestCards.Fill(size, 0x55));
            int kpos = Card.FindKey(body, "TextureDictionary");   // find the bin by its key, as the tool does
            int p = kpos;
            var bin = MP.Read(body, ref p) as BinRef;
            Assert.NotNull(bin);
            Assert.Equal(headerLen, bin.HeaderLen);
            Assert.Equal(size, bin.Len);
            Assert.Equal(kpos + headerLen, bin.Off);
        }

        // old payload size / new payload size, per header form. The new payload is always written as bin32.
        [Theory]
        [InlineData(8, 100, 40)]
        [InlineData(8, 100, 100)]
        [InlineData(16, 300, 5000)]
        [InlineData(16, 52788, 51474)]                       // the case seen on real cards: a bin16 dictionary of ~52 KB
        [InlineData(16, 300, 70000)]
        [InlineData(32, 70000, 69000)]
        [InlineData(32, 70000, 100)]
        public void ReplaceBin_KeepsTheKeyAndTheEntriesAroundIt(int bits, int oldSize, int newSize)
        {
            byte[] body = TestCards.KkexBody(bits, TestCards.Fill(oldSize, 0x11));
            int kpos = Card.FindKey(body, "TextureDictionary");
            var bin = MP.Read(body, ref kpos) as BinRef;
            byte[] payload = TestCards.Fill(newSize, 0x22);

            int at;
            byte[] o = MP.ReplaceBin(body, bin, payload, out at);

            // everything before the old header is untouched (the key, in particular)
            for (int i = 0; i < at; i++) Assert.Equal(body[i], o[i]);
            Assert.True(Card.FindKey(o, "TextureDictionary") > 0, "the TextureDictionary key was damaged");

            // and the result reads back as a whole map: same entries, the new payload
            int p = 0;
            var map = MP.Read(o, ref p) as MMap;
            Assert.NotNull(map);
            Assert.Equal(o.Length, p);
            Assert.Equal(1L, Convert.ToInt64(map.Get("a")));
            Assert.Equal(2L, Convert.ToInt64(map.Get("z")));
            var nb = map.Get("TextureDictionary") as BinRef;
            Assert.NotNull(nb);
            Assert.Equal(5, nb.HeaderLen);
            Assert.Equal(payload, nb.Bytes());
        }
    }
}

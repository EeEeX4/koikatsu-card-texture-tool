using System;
using System.Collections.Generic;
using System.IO;

namespace KoiCardTexTool
{
    /// <summary>JPEG **无损**再优化：把基线 JPEG 重新做一遍"最优哈夫曼编码"。
    ///
    /// 原理：像素内容由"量化后的 DCT 系数"决定，系数在熵编码段里用哈夫曼表编码；
    /// 编码器通常用标准表，而按实际符号频率算出来的最优表更省。只换第二层编码 ——
    /// **系数一个都不动，解出来的像素逐位相同**，典型省 5%~9%（本项目实测 mozjpeg 的
    /// jpegtran -optimize 对 GDI+ 产物省 8.6%，本实现做的是同一件事）。
    ///
    /// 自己实现的好处：不引入任何原生依赖，仍然是单文件 EXE。任何不确定的情况一律返回 null，
    /// 调用方保留原字节 —— 绝不能因为"优化"把卡片搞坏。
    ///
    /// 只处理：基线（SOF0/SOF1）、8 位精度、无 restart marker、1 或 3 个分量。</summary>
    static class JpegOpt
    {
        [ThreadStatic] public static int Used;
        [ThreadStatic] public static long Saved;
        /// <summary>总开关（默认开：像素逐位不变，只有更小才替换）。</summary>
        public static bool Enabled = true;
        /// <summary>调试：最近一次为什么放弃（自检命令会打印）。</summary>
        public static string LastReason = "";

        // ---------------- 哈夫曼表 ----------------
        sealed class Huff
        {
            public byte[] Bits = new byte[17];       // Bits[1..16] = 该长度的码字数
            public byte[] Values = new byte[256];
            public int SymCount;                     // Σ Bits（码字数）
            public int Emitted;                      // 实际写进 Values 的符号数
            public int[] MinCode = new int[17];      // 必须用 int：16 位码字最大 65535，short 会溢出成负数
            public int[] MaxCode = new int[18];
            public int[] ValPtr = new int[17];
            public int[] Code = new int[256];        // 符号 → 码字
            public int[] Len = new int[256];         // 符号 → 码长

            public void BuildDecode()
            {
                int code = 0, k = 0;
                for (int l = 1; l <= 16; l++)
                {
                    if (Bits[l] > 0)
                    {
                        ValPtr[l] = k; MinCode[l] = code;
                        code += Bits[l]; k += Bits[l];
                        MaxCode[l] = code - 1;
                    }
                    else { MinCode[l] = -1; MaxCode[l] = -1; }
                    code <<= 1;
                }
                MaxCode[17] = 0x7FFF;
            }

            public void BuildEncode()
            {
                int code = 0, k = 0;
                for (int l = 1; l <= 16; l++)
                {
                    for (int i = 0; i < Bits[l]; i++)
                    {
                        int sym = Values[k++];
                        Code[sym] = code; Len[sym] = l;
                        code++;
                    }
                    code <<= 1;
                }
            }
        }

        sealed class Comp
        {
            public int Id, H, V, Td, Ta;
            public int BlocksPerLine, BlocksPerCol;
            public short[] Coef;                     // 每块 64 个（锯齿序，和码流里一致）
            public int Predictor;
        }

        static byte[] Slice(byte[] b, int off, int len)
        {
            var r = new byte[len];
            Array.Copy(b, off, r, 0, len);
            return r;
        }

        /// <summary>无损再优化；成功且更小返回新字节，否则 null。</summary>
        public static byte[] Optimize(byte[] jpg)
        {
            try { return OptimizeCore(jpg); }
            catch (Exception ex) { LastReason = "异常：" + ex.GetType().Name + " " + ex.Message; return null; }
        }

        static byte[] OptimizeCore(byte[] jpg)
        {
            if (jpg == null || jpg.Length < 128) { LastReason = "太短"; return null; }
            if (jpg[0] != 0xFF || jpg[1] != 0xD8) { LastReason = "不是 JPEG"; return null; }

            int p = 2;
            var dht = new Dictionary<int, Huff>();
            var headers = new List<byte[]>();
            var comps = new List<Comp>();
            int width = 0, height = 0, ncomp = 0, sosOff = -1;

            while (p + 3 < jpg.Length)
            {
                if (jpg[p] != 0xFF) { LastReason = "段结构异常 @" + p; return null; }
                int m = jpg[p + 1];
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
                if (m == 0xD9) break;
                if (m == 0xFF) { p++; continue; }                  // 填充字节
                int len = (jpg[p + 2] << 8) | jpg[p + 3];
                if (len < 2 || p + 2 + len > jpg.Length) { LastReason = "段长度越界 m=" + m.ToString("X2"); return null; }

                if (m == 0xC0 || m == 0xC1)                        // 基线 SOF
                {
                    int precision = jpg[p + 4];
                    height = (jpg[p + 5] << 8) | jpg[p + 6];
                    width = (jpg[p + 7] << 8) | jpg[p + 8];
                    ncomp = jpg[p + 9];
                    if (precision != 8 || (ncomp != 1 && ncomp != 3)) { LastReason = "精度/分量不支持 p=" + precision + " n=" + ncomp; return null; }
                    if (p + 10 + ncomp * 3 > jpg.Length) return null;
                    for (int i = 0; i < ncomp; i++)
                        comps.Add(new Comp
                        {
                            Id = jpg[p + 10 + i * 3],
                            H = jpg[p + 11 + i * 3] >> 4,
                            V = jpg[p + 11 + i * 3] & 15
                        });
                    headers.Add(Slice(jpg, p, len + 2));
                }
                else if (m == 0xC2 || m == 0xC3 || (m >= 0xC5 && m <= 0xCF)) { LastReason = "渐进/其它 SOF m=" + m.ToString("X2"); return null; }
                else if (m == 0xC4)                                // DHT
                {
                    int q = p + 4, endq = p + 2 + len;
                    while (q + 17 <= endq)
                    {
                        int tc = jpg[q] >> 4, th = jpg[q] & 15;
                        var h = new Huff();
                        int total = 0;
                        for (int l = 1; l <= 16; l++) { h.Bits[l] = jpg[q + l]; total += h.Bits[l]; }
                        if (total > 256 || q + 17 + total > endq) { LastReason = "DHT 异常"; return null; }
                        for (int i = 0; i < total; i++) h.Values[i] = jpg[q + 17 + i];
                        dht[(tc << 4) | th] = h;
                        q += 17 + total;
                    }
                }
                else if (m == 0xDD)
                {
                    int ri = (jpg[p + 4] << 8) | jpg[p + 5];
                    if (ri != 0) { LastReason = "有 restart marker"; return null; }
                }
                else if (m == 0xDA) { sosOff = p; break; }
                else if (m == 0xDB || m == 0xE0) headers.Add(Slice(jpg, p, len + 2));  // DQT / JFIF 保留
                else if (m == 0xFE || (m >= 0xE1 && m <= 0xEF)) { /* COM / APPn：丢掉 */ }
                else { LastReason = "不认识的段 m=" + m.ToString("X2"); return null; }
                p += 2 + len;
            }
            if (sosOff < 0 || width <= 0 || height <= 0 || comps.Count != ncomp) { LastReason = "没找到 SOS/尺寸异常"; return null; }

            int sosLen = (jpg[sosOff + 2] << 8) | jpg[sosOff + 3];
            if (sosOff + 2 + sosLen > jpg.Length) { LastReason = "SOS 越界"; return null; }
            for (int i = 0; i < ncomp; i++)
            {
                // SOS 布局：L(2) Ns(1) 之后每个分量占 2 字节（Cs, Td|Ta）——别少算那个 Ns
                int cs = jpg[sosOff + 5 + i * 2];
                var c = comps.Find(x => x.Id == cs);
                if (c == null) { LastReason = "SOS 里的分量 ID 对不上"; return null; }
                c.Td = jpg[sosOff + 6 + i * 2] >> 4;
                c.Ta = jpg[sosOff + 6 + i * 2] & 15;
                if (!dht.ContainsKey((0 << 4) | c.Td) || !dht.ContainsKey((1 << 4) | c.Ta)) { LastReason = "缺哈夫曼表"; return null; }
            }
            headers.Add(Slice(jpg, sosOff, sosLen + 2));

            int hmax = 1, vmax = 1;
            foreach (var c in comps) { if (c.H > hmax) hmax = c.H; if (c.V > vmax) vmax = c.V; }
            if (hmax < 1 || vmax < 1 || hmax > 4 || vmax > 4) { LastReason = "采样因子异常"; return null; }
            int mcusX = (width + 8 * hmax - 1) / (8 * hmax);
            int mcusY = (height + 8 * vmax - 1) / (8 * vmax);
            foreach (var c in comps)
            {
                c.BlocksPerLine = mcusX * c.H;
                c.BlocksPerCol = mcusY * c.V;
                long n = (long)c.BlocksPerLine * c.BlocksPerCol * 64;
                if (n <= 0 || n > 64L * 1024 * 1024) { LastReason = "系数数组太大"; return null; }
                c.Coef = new short[n];
            }

            // ---- 第 1 遍：解出系数，并统计"按表索引"的符号频率 ----
            var dcFreq = new Dictionary<int, int[]>();
            var acFreq = new Dictionary<int, int[]>();
            foreach (var c in comps)
            {
                if (!dcFreq.ContainsKey(c.Td)) dcFreq[c.Td] = new int[257];
                if (!acFreq.ContainsKey(c.Ta)) acFreq[c.Ta] = new int[257];
                dht[(0 << 4) | c.Td].BuildDecode();
                dht[(1 << 4) | c.Ta].BuildDecode();
            }
            var br = new BitReader(jpg, sosOff + 2 + sosLen, jpg.Length);
            int dbgMy = 0, dbgMx = 0, dbgCi = 0;
            try
            {
            for (int my = 0; my < mcusY; my++)
            {
                dbgMy = my;
                for (int mx = 0; mx < mcusX; mx++)
                {
                    dbgMx = mx;
                    foreach (var c in comps)
                    {
                        dbgCi = comps.IndexOf(c);
                        var dcH = dht[(0 << 4) | c.Td];
                        var acH = dht[(1 << 4) | c.Ta];
                        var dcf = dcFreq[c.Td];
                        var acf = acFreq[c.Ta];
                        for (int by = 0; by < c.V; by++)
                            for (int bx = 0; bx < c.H; bx++)
                            {
                                int off = ((my * c.V + by) * c.BlocksPerLine + (mx * c.H + bx)) * 64;
                                int s = br.DecodeHuff(dcH);
                                dcf[s]++;
                                c.Predictor += s == 0 ? 0 : br.ReceiveExtend(s);
                                c.Coef[off] = (short)c.Predictor;
                                int k = 1;
                                while (k < 64)
                                {
                                    int rs = br.DecodeHuff(acH);
                                    acf[rs]++;
                                    int r = rs >> 4, sz = rs & 15;
                                    if (sz == 0)
                                    {
                                        if (r == 15) { k += 16; continue; }
                                        break;                              // EOB
                                    }
                                    k += r;
                                    if (k > 63) { LastReason = "AC 游程越界"; return null; }
                                    c.Coef[off + k] = (short)br.ReceiveExtend(sz);
                                    k++;
                                }
                            }
                    }
                }
            }
            }
            catch (Exception ex)
            {
                LastReason = L.F("解码失败 @MCU({0},{1}) 分量{2}：{3}；结构 ncomp={4} hmax={5} vmax={6} mcus={7}x{8}",
                    dbgMx, dbgMy, dbgCi, ex.Message, ncomp, hmax, vmax, mcusX, mcusY);
                return null;
            }

            // ---- 建最优哈夫曼表 ----
            var newDc = new Dictionary<int, Huff>();
            var newAc = new Dictionary<int, Huff>();
            foreach (var kv in dcFreq) { var h = new Huff(); BuildOptimal(kv.Value, h); h.BuildEncode(); newDc[kv.Key] = h; }
            foreach (var kv in acFreq) { var h = new Huff(); BuildOptimal(kv.Value, h); h.BuildEncode(); newAc[kv.Key] = h; }

            // ---- 第 2 遍：用新表重编码 ----
            var bw = new BitWriter();
            foreach (var c in comps) c.Predictor = 0;
            var newDcSyms = new HashSet<int>(); var newAcSyms = new HashSet<int>();
            foreach (var kv in newDc) for (int i = 0; i < 256; i++) if (kv.Value.Len[i] > 0) newDcSyms.Add(kv.Key * 1000 + i);
            foreach (var kv in newAc) for (int i = 0; i < 256; i++) if (kv.Value.Len[i] > 0) newAcSyms.Add(kv.Key * 1000 + i);
            foreach (var kv in dcFreq) for (int i = 0; i < 257; i++) if (kv.Value[i] > 0 && !newDcSyms.Contains(kv.Key * 1000 + i)) { LastReason = L.F("新 DC 表缺符号 {0}(表{1}) 频次{2}", i, kv.Key, kv.Value[i]); return null; }
            foreach (var kv in acFreq) for (int i = 0; i < 257; i++) if (kv.Value[i] > 0 && !newAcSyms.Contains(kv.Key * 1000 + i)) { LastReason = L.F("新 AC 表缺符号 {0:X2}(表{1}) 频次{2}", i, kv.Key, kv.Value[i]); return null; }
            foreach (var kv in newDc) { int tot = 0; for (int l = 1; l <= 16; l++) tot += kv.Value.Bits[l]; if (tot != kv.Value.Emitted) { LastReason = L.F("DC 表码字数({0})≠ 符号数({1})", tot, kv.Value.Emitted); return null; } }
            foreach (var kv in newAc) { int tot = 0; for (int l = 1; l <= 16; l++) tot += kv.Value.Bits[l]; if (tot != kv.Value.Emitted) { LastReason = L.F("AC 表码字数({0})≠ 符号数({1})", tot, kv.Value.Emitted); return null; } }
            try
            {
            for (int my = 0; my < mcusY; my++)
                for (int mx = 0; mx < mcusX; mx++)
                    foreach (var c in comps)
                    {
                        var dcH = newDc[c.Td];
                        var acH = newAc[c.Ta];
                        for (int by = 0; by < c.V; by++)
                            for (int bx = 0; bx < c.H; bx++)
                            {
                                int off = ((my * c.V + by) * c.BlocksPerLine + (mx * c.H + bx)) * 64;
                                int dc = c.Coef[off];
                                int diff = dc - c.Predictor;
                                c.Predictor = dc;
                                int s = Category(diff);
                                bw.Write(dcH.Code[s], dcH.Len[s]);
                                if (s > 0) bw.Write(Amplitude(diff, s), s);
                                int run = 0;
                                for (int k = 1; k < 64; k++)
                                {
                                    int v = c.Coef[off + k];
                                    if (v == 0) { run++; continue; }
                                    while (run > 15) { bw.Write(acH.Code[0xF0], acH.Len[0xF0]); run -= 16; }
                                    int sz = Category(v);
                                    int sym = (run << 4) | sz;
                                    bw.Write(acH.Code[sym], acH.Len[sym]);
                                    bw.Write(Amplitude(v, sz), sz);
                                    run = 0;
                                }
                                if (run > 0) bw.Write(acH.Code[0x00], acH.Len[0x00]);   // EOB
                            }
                    }
            }
            catch (Exception ex)
            {
                LastReason = "重编码失败：" + ex.GetType().Name + " " + ex.Message;
                return null;
            }
            bw.Flush();

            // ---- 组装（基线）----
            byte[] outp = AssembleBase(jpg, headers, newDc, newAc, bw.ToArray());
            LastReason = L.F("产出 {0} vs 原 {1}（{2}）", outp.Length, jpg.Length, outp.Length < jpg.Length ? "更小" : "没更小");
            if (outp.Length >= jpg.Length) return null;                 // 没更小就不换
            Used++; Saved += jpg.Length - outp.Length;
            return outp;
        }

        static byte[] AssembleBase(byte[] jpg, List<byte[]> headers, Dictionary<int, Huff> newDc, Dictionary<int, Huff> newAc, byte[] entropy)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0xFF); ms.WriteByte(0xD8);
            foreach (var h in headers)
            {
                if (Tag(h) == "SOS") break;                              // SOS 最后单独写
                ms.Write(h, 0, h.Length);
            }
            foreach (var kv in newDc) WriteDht(ms, 0, kv.Key, kv.Value);
            foreach (var kv in newAc) WriteDht(ms, 1, kv.Key, kv.Value);
            var sos = headers[headers.Count - 1];
            ms.Write(sos, 0, sos.Length);
            ms.Write(entropy, 0, entropy.Length);
            ms.WriteByte(0xFF); ms.WriteByte(0xD9);
            return ms.ToArray();
        }

        static string Tag(byte[] seg)
        {
            if (seg.Length < 4) return "?";
            int m = seg[1];
            if (m == 0xDA) return "SOS";
            if (m == 0xC0) return "SOF0";
            if (m == 0xDB) return "DQT";
            return "M" + m.ToString("X2");
        }

        static void WriteDht(Stream s, int tc, int th, Huff h)
        {
            int n = 0;
            for (int l = 1; l <= 16; l++) n += h.Bits[l];
            int len = 2 + 1 + 16 + n;
            s.WriteByte(0xFF); s.WriteByte(0xC4);
            s.WriteByte((byte)(len >> 8)); s.WriteByte((byte)len);
            s.WriteByte((byte)((tc << 4) | th));
            for (int l = 1; l <= 16; l++) s.WriteByte(h.Bits[l]);
            s.Write(h.Values, 0, n);
        }

        static int Category(int v)
        {
            int a = v < 0 ? -v : v, s = 0;
            while (a != 0) { s++; a >>= 1; }
            return s;
        }

        static int Amplitude(int v, int s)
        {
            return v >= 0 ? v : v + (1 << s) - 1;
        }

        /// <summary>libjpeg 的 jpeg_gen_optimal_table 同款算法（含 16 位长度限制）。</summary>
        static void BuildOptimal(int[] freqIn, Huff h)
        {
            const int MAX_CLEN = 32;
            var freq = new int[257];
            Array.Copy(freqIn, freq, Math.Min(freqIn.Length, 257));
            freq[256] = 1;                                             // 保留一个全 1 码
            var codesize = new int[257];
            var others = new int[257];
            for (int i = 0; i < 257; i++) { codesize[i] = 0; others[i] = -1; }

            while (true)
            {
                int c1 = -1, c2 = -1, v = int.MaxValue;
                for (int i = 0; i < 257; i++) if (freq[i] != 0 && freq[i] <= v) { v = freq[i]; c1 = i; }
                v = int.MaxValue;
                for (int i = 0; i < 257; i++) if (freq[i] != 0 && i != c1 && freq[i] <= v) { v = freq[i]; c2 = i; }
                if (c2 < 0) break;
                freq[c1] += freq[c2]; freq[c2] = 0;
                codesize[c1]++;
                while (others[c1] >= 0) { c1 = others[c1]; codesize[c1]++; }
                others[c1] = c2;
                codesize[c2]++;
                while (others[c2] >= 0) { c2 = others[c2]; codesize[c2]++; }
            }

            var bits = new int[MAX_CLEN + 1];
            for (int i = 0; i < 257; i++) if (codesize[i] > MAX_CLEN) codesize[i] = MAX_CLEN;
            for (int i = 0; i < 257; i++) bits[codesize[i]]++;
            for (int i = MAX_CLEN; i > 16; i--)
                while (bits[i] > 0)
                {
                    int j = i - 2;
                    while (j > 0 && bits[j] == 0) j--;
                    bits[i] -= 2; bits[i - 1] += 1; bits[j + 1] += 2; bits[j] -= 1;
                }
            // libjpeg 这一步不能漏：把"伪符号 256"的数量从最长码长里减掉，
            // 否则 bits[] 会比实际写出的符号数多 1（解码端按长度读码就会错位）
            int ii = 16;
            while (ii > 0 && bits[ii] == 0) ii--;
            if (ii > 0) bits[ii]--;
            h.SymCount = 0;
            for (int i = 1; i <= 16; i++) { h.Bits[i] = (byte)bits[i]; h.SymCount += bits[i]; }
            // 注意：这里要扫到 MAX_CLEN（不是 16）——长度限制只改了 bits[] 的分布，
            // codesize[] 仍是原始长度；按 1..16 收符号会把超长的那些符号整个丢掉（实测漏过符号 0x36）
            int k2 = 0;
            for (int i = 1; i <= MAX_CLEN; i++)
                for (int j = 0; j < 256; j++)
                    if (codesize[j] == i) h.Values[k2++] = (byte)j;
            h.Emitted = k2;
        }

        // ---------------- 位读写 ----------------
        sealed class BitReader
        {
            readonly byte[] d; int p; int cur; int cnt;
            public BitReader(byte[] data, int start, int end) { d = data; p = start; }
            public int Bit()
            {
                if (cnt == 0)
                {
                    if (p >= d.Length) throw new EndOfStreamException();
                    cur = d[p++];
                    if (cur == 0xFF)
                    {
                        if (p < d.Length && d[p] == 0x00) p++;              // 字节填充
                        else throw new EndOfStreamException();              // 撞到 marker
                    }
                    cnt = 8;
                }
                cnt--;
                return (cur >> cnt) & 1;
            }
            public int Bits(int n) { int v = 0; for (int i = 0; i < n; i++) v = (v << 1) | Bit(); return v; }
            public int DecodeHuff(Huff h)
            {
                int code = Bit(), l = 1;
                while (l <= 16 && (h.MaxCode[l] < 0 || code > h.MaxCode[l])) { code = (code << 1) | Bit(); l++; }
                if (l > 16) throw new InvalidDataException("bad huffman code");
                return h.Values[h.ValPtr[l] + code - h.MinCode[l]];
            }
            public int ReceiveExtend(int s)
            {
                if (s == 0) return 0;
                int v = Bits(s);
                return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v;
            }
        }

        sealed class BitWriter
        {
            readonly MemoryStream ms = new MemoryStream();
            int acc, n;
            public void Write(int code, int len)
            {
                for (int i = len - 1; i >= 0; i--)
                {
                    acc = (acc << 1) | ((code >> i) & 1);
                    n++;
                    if (n == 8)
                    {
                        ms.WriteByte((byte)acc);
                        if ((byte)acc == 0xFF) ms.WriteByte(0x00);          // 字节填充
                        acc = 0; n = 0;
                    }
                }
            }
            public void Flush()
            {
                if (n > 0)
                {
                    acc <<= (8 - n);
                    acc |= (1 << (8 - n)) - 1;                              // 用 1 补齐（和 libjpeg 一致）
                    ms.WriteByte((byte)acc);
                    if ((byte)acc == 0xFF) ms.WriteByte(0x00);
                    acc = 0; n = 0;
                }
            }
            public byte[] ToArray() { return ms.ToArray(); }
        }
    }
}
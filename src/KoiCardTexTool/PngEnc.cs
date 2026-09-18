using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KoiCardTexTool
{
    /// <summary>自己写的 PNG 编码器（纯托管、无第三方依赖）：
    ///
    ///  · 逐行试 5 种滤波（None/Sub/Up/Average/Paeth），按"滤波后字节绝对值之和最小"选（PNG 的经典启发式）
    ///  · 用 .NET 的 DeflateStream(SmallestSize) 压（.NET 8 走 zlib-ng，比 GDI+ 的固定策略好）
    ///  · 通道按需降：不透明 → RGB（去掉整条 alpha），灰度 + 不透明 → 8 位灰度
    ///
    /// 为什么值得写：GDI+ 的 PNG 编码器既不能设压缩级别、也没有自适应滤波，
    /// 实测同一张 2048² 贴图能差 5%~25%（彩色贴图），而这是**无损**的。</summary>
    static class PngEnc
    {
        /// <summary>只做 **RGB / RGBA** 两种颜色类型（通道语义与 GDI+ 完全一致、像素逐位无损）；
        /// 灰度/调色板那条老路仍交给 GDI+（游戏里已验证过）。
        /// 非 24/32 位位图（例如 8bppIndexed）返回 null，调用方继续用 GDI+。</summary>
        public static byte[] EncodeColor(Bitmap bmp, out string info)
        {
            info = "";
            var pf = bmp.PixelFormat;
            if (pf != PixelFormat.Format24bppRgb && pf != PixelFormat.Format32bppArgb) return null;
            return Encode(bmp, out info, false);
        }

        /// <summary>**无损再压缩**：拿 GDI+ 已经写好的 PNG，把它的扫描线解出来重新做自适应滤波 + zlib-ng 压缩，
        /// 颜色类型/位深/调色板/透明度全都不动 —— 像素与 GDI+ 的产物**逐位相同**，只赢在"压缩得更好"。
        ///
        /// 为什么不用"自己从 Bitmap 读像素"那条路：GDI+ 内部是预乘 alpha 的缓冲区，
        /// `LockBits(Format32bppArgb)` 反预乘时会有 ±1~3 的取整误差（半透明像素上实测到了），
        /// 那就不是无损了。走扫描线则完全没有像素层面的解释，天然无损。
        ///
        /// 失败（隔行扫描 / 位深不认识 / 数据异常）返回 null，调用方继续用原 PNG。</summary>
        public static byte[] Recompress(byte[] png, bool allowGrayAlpha, out string kind)
        {
            kind = "";
            try
            {
                NRecomp++;
                if (png == null || png.Length < 8 + 25) return null;
                for (int i = 0; i < 8; i++) if (png[i] != Card.PngMagic[i]) return null;

                int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
                var keep = new MemoryStream();          // IHDR + 其它非 IDAT 块（PLTE/tRNS/gAMA…）按原顺序保留
                var idat = new MemoryStream();
                int p = 8;
                while (p + 8 <= png.Length)
                {
                    int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
                    if (len < 0 || p + 12 + len > png.Length) return null;
                    string type = Encoding.ASCII.GetString(png, p + 4, 4);
                    if (type == "IHDR")
                    {
                        w = (png[p + 8] << 24) | (png[p + 9] << 16) | (png[p + 10] << 8) | png[p + 11];
                        h = (png[p + 12] << 24) | (png[p + 13] << 16) | (png[p + 14] << 8) | png[p + 15];
                        bitDepth = png[p + 16]; colorType = png[p + 17]; interlace = png[p + 18];
                    }
                    if (type == "IDAT") idat.Write(png, p + 8, len);
                    else if (type != "IEND") { keep.Write(png, p, len + 12); }
                    p += 12 + len;
                    if (type == "IEND") break;
                }
                if (w <= 0 || h <= 0 || bitDepth != 8 || interlace != 0) return null;

                int chans;
                switch (colorType)
                {
                    case 0: chans = 1; break;
                    case 2: chans = 3; break;
                    case 3: chans = 1; break;          // 调色板：每像素 1 字节索引
                    case 4: chans = 2; break;
                    case 6: chans = 4; break;
                    default: return null;              // 16 位 / 不认识的类型不动
                }

                byte[] z = idat.ToArray();
                if (z.Length < 6) return null;
                int stride = w * chans;
                // 反滤波 → 原始字节。**逐行流式**：不再把整幅 inflate 结果 raw((stride+1)*h)
                // 和 MemoryStream 的翻倍缓冲一起留在内存里（8192² RGBA 一份就是 268 MB，
                // 峰值内存实测 2.7 GB/卡，并行化的拦路虎就是这个）。
                var swI = System.Diagnostics.Stopwatch.StartNew();
                var swF = System.Diagnostics.Stopwatch.StartNew();
                var lines = new byte[h * stride];
                var rb = new byte[stride + 1];
                using (var ms = new MemoryStream(z, 2, z.Length - 6))     // 跳过 2 字节 zlib 头 / 尾部 adler32
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    for (int y = 0; y < h; y++)
                    {
                        if (!ReadFull(ds, rb, rb.Length)) return null;    // 数据比声明的短 → 不认
                        int ft = rb[0];
                        int dof = y * stride;
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= chans ? lines[dof + i - chans] : 0;
                            int b = y > 0 ? lines[dof - stride + i] : 0;
                            int c = (i >= chans && y > 0) ? lines[dof - stride + i - chans] : 0;
                            int v = rb[1 + i];
                            switch (ft)
                            {
                                case 0: break;
                                case 1: v += a; break;
                                case 2: v += b; break;
                                case 3: v += (a + b) >> 1; break;
                                case 4:
                                    int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                                    v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                                    break;
                                default: return null;
                            }
                            lines[dof + i] = (byte)v;
                        }
                    }
                    if (ds.ReadByte() >= 0) return null;                  // 数据比声明的长 → 也不认
                }
                TInflate += swI.Elapsed.TotalMilliseconds;
                TFilter += swF.Elapsed.TotalMilliseconds;

                // ---- 灰度优先 ----
                // 只有"每个像素 R==G==B"才允许降通道，而这种情况下降成灰度**信息量完全相同、
                // 通道数少 2~4 倍**，所以灰度版几乎必然更小。旧写法是"先算彩色版 → 再算灰度版 → 谁小用谁"，
                // 于是彩色版那整轮自适应滤波 + deflate 全是白算 —— 实测占送进 deflate 字节的 21.4%。
                // 现在先算灰度版：它只要比**原图**小，调用方就一定会采用，直接返回、彩色版一轮不跑。
                if (allowGrayAlpha)
                {
                    int nch; byte ntype; string tag;
                    byte[] conv = TryGray(lines, w, h, chans, colorType, out nch, out ntype, out tag);
                    if (conv != null)
                    {
                        byte[] gray = Pack(conv, w, h, nch, ntype, keep.ToArray(), png, out _);
                        if (gray.Length < png.Length)
                        {
                            kind = tag;
                            GraySkippedColor++;
                            return gray;                       // 灰度版已经赢了，彩色版不用算
                        }
                        // 灰度版没能比原图更小 → 退回旧行为，把彩色版也算出来比一比（保证结果与旧版一致）
                        byte[] col = Pack(lines, w, h, chans, (byte)colorType, keep.ToArray(), png, out _);
                        if (col.Length < gray.Length) return col;
                        kind = tag;
                        return gray;
                    }
                }
                return Pack(lines, w, h, chans, (byte)colorType, keep.ToArray(), png, out _);
            }
            catch { return null; }
        }

        /// <summary>能不能无损降通道：RGBA 且每像素 R==G==B → 灰度+alpha(2 通道)；RGB 且每像素 R==G==B → 灰度(1 通道)。
        /// 不能就返回 null。转换只搬 R(=G=B) 与 A，数值一个 bit 都不改。</summary>
        static byte[] TryGray(byte[] lines, int w, int h, int chans, int colorType, out int nch, out byte ntype, out string tag)
        {
            nch = 0; ntype = 0; tag = "";
            bool rgba = colorType == 6 && chans == 4;
            bool rgb = colorType == 2 && chans == 3;
            if (!rgba && !rgb) return null;
            int stride = w * chans;
            for (int y = 0; y < h; y++)
            {
                int o = y * stride;
                for (int x = 0; x < w; x++, o += chans)
                    if (lines[o] != lines[o + 1] || lines[o + 1] != lines[o + 2]) return null;   // 不是灰的
            }
            nch = rgba ? 2 : 1;
            ntype = rgba ? (byte)4 : (byte)0;
            tag = rgba ? "LA(2通道)" : "L(1通道)";
            var outb = new byte[h * w * nch];
            int p2 = 0;
            for (int y = 0; y < h; y++)
            {
                int o = y * stride;
                for (int x = 0; x < w; x++, o += chans)
                {
                    outb[p2++] = lines[o];                       // 灰阶 = R（R==G==B，取哪个都一样）
                    if (rgba) outb[p2++] = lines[o + 3];         // alpha 原样
                }
            }
            return outb;
        }

        /// <summary>给一行挑滤波表并写进 dst[dstOff .. dstOff+stride]（dstOff 处放滤波表编号）。
        /// 两遍：先只算 5 张表的 MSAD 分（不回写候选行，省掉 5 次逐字节写），
        /// 再按胜出的那张表回写一遍。逐位等价于"算一张存一张再比大小"。</summary>
        static void FilterRow(byte[] lines, byte[] dst, int dstOff, int stride, int chans, int y)
        {
            int dof = y * stride;
            int bestF = 0; long bestScore = long.MaxValue;
            for (int f = 0; f < 5; f++)
            {
                long s = 0;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= chans ? lines[dof + i - chans] : 0;
                    int b = y > 0 ? lines[dof - stride + i] : 0;
                    int c = (i >= chans && y > 0) ? lines[dof - stride + i - chans] : 0;
                    int v;
                    switch (f)
                    {
                        case 0: v = lines[dof + i]; break;
                        case 1: v = lines[dof + i] - a; break;
                        case 2: v = lines[dof + i] - b; break;
                        case 3: v = lines[dof + i] - ((a + b) >> 1); break;
                        default:
                            int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                            v = lines[dof + i] - ((pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c));
                            break;
                    }
                    s += Math.Abs((int)(sbyte)(byte)v);
                }
                if (s < bestScore) { bestScore = s; bestF = f; }
            }
            int wr = dstOff + 1;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= chans ? lines[dof + i - chans] : 0;
                int b = y > 0 ? lines[dof - stride + i] : 0;
                int c = (i >= chans && y > 0) ? lines[dof - stride + i - chans] : 0;
                int v;
                switch (bestF)
                {
                    case 0: v = lines[dof + i]; break;
                    case 1: v = lines[dof + i] - a; break;
                    case 2: v = lines[dof + i] - b; break;
                    case 3: v = lines[dof + i] - ((a + b) >> 1); break;
                    default:
                        int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                        v = lines[dof + i] - ((pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c));
                        break;
                }
                dst[wr + i] = (byte)v;
            }
            dst[dstOff] = (byte)bestF;
        }

        /// <summary>自适应滤波的行并行度（KOITEX_PNG_THREADS 覆盖；KOITEX_NOPAR=1 退回单线程）。</summary>
        public static int ParThreads = InitPar();
        public static long ParMinBytes = 1 << 20;      // 小图并行反而亏，给个门槛
        public static int BlockBytes = InitBlock();    // 流式打包时每块的目标字节数（块内并行，块整块压）

        /// <summary>块大小（KOITEX_PNG_BLOCK 覆盖，单位字节；设得比整幅大 = 退回"一次性压"）。</summary>
        static int InitBlock()
        {
            string e = Environment.GetEnvironmentVariable("KOITEX_PNG_BLOCK");
            int n;
            if (!string.IsNullOrEmpty(e) && int.TryParse(e, out n) && n > 0) return n;
            return 4 << 20;
        }

        static int InitPar()
        {
            if (Environment.GetEnvironmentVariable("KOITEX_NOPAR") == "1") return 1;
            string e = Environment.GetEnvironmentVariable("KOITEX_PNG_THREADS");
            int n;
            if (!string.IsNullOrEmpty(e) && int.TryParse(e, out n) && n > 0) return n;
            return Math.Max(1, Environment.ProcessorCount);
        }

        /// <summary>把一段"原始扫描线"按给定通道数/颜色类型重新滤波+压缩，组装成完整 PNG。
        ///
        /// **流式**：逐行"挑滤波表 → 写进 DeflateStream"，不再先攒出整幅滤波结果 rows((stride+1)*h)。
        /// 8192² RGBA 的 rows 就是 268 MB —— 去掉它对峰值内存影响最大，而 deflate 的输出与
        /// "一次性压一大块"完全相同（zlib 的输出只取决于输入字节序列与档位，与喂入的分块无关）。</summary>
        static byte[] Pack(byte[] lines, int w, int h, int chans, byte colorType, byte[] keepBytes, byte[] origPng, out int idatLen)
        {
            NPack++;
            int stride = w * chans;
            int rowLen = stride + 1;
            // 分块：块内并行挑滤波表，块整块喂给 deflate。
            // 这样既保住"行并行"（deflate 之前那块最贵的活），又只占一块的内存（≈4 MB），
            // 而不是整幅 rows((stride+1)*h)。deflate 收到的仍是一条连续流，输出与一次性压完全一致。
            int per = (int)Math.Max(1, Math.Min(h, BlockBytes / Math.Max(1, rowLen)));
            var buf = new byte[rowLen * per];
            var comp = new MemoryStream();
            DeflateStream ds = null;
            uint adA = 1, adB = 0;
            int nth = ParThreads;
            for (int y0 = 0; y0 < h; y0 += per)
            {
                int n = Math.Min(per, h - y0);
                var swF = System.Diagnostics.Stopwatch.StartNew();
                if (nth > 1 && (long)rowLen * n >= (long)ParMinBytes && n >= 4)
                {
                    int yy0 = y0;
                    System.Threading.Tasks.Parallel.For(0, n,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = nth },
                        delegate (int i) { FilterRow(lines, buf, i * rowLen, stride, chans, yy0 + i); });
                }
                else
                {
                    for (int i = 0; i < n; i++) FilterRow(lines, buf, i * rowLen, stride, chans, y0 + i);
                }
                TFilter += swF.Elapsed.TotalMilliseconds;
                var swD = System.Diagnostics.Stopwatch.StartNew();
                if (ds == null) ds = new DeflateStream(comp, Level(), true);
                ds.Write(buf, 0, n * rowLen);
                TDeflate += swD.Elapsed.TotalMilliseconds;
                int lim = n * rowLen;
                for (int i = 0; i < lim; i++)                     // Adler-32（对 deflate 的输入求）
                { adA = (adA + buf[i]) % 65521; adB = (adB + adA) % 65521; }
            }
            if (ds != null) ds.Dispose();
            TDeflateBytes += (long)rowLen * h;
            byte[] nz = ZlibWrap(comp.ToArray(), adA, adB);
            idatLen = nz.Length;
            var ms2 = new MemoryStream();
            ms2.Write(origPng, 0, 8);
            Chunk(ms2, "IHDR", BuildIhdr(w, h, 8, colorType));
            int q = 0;
            while (q + 8 <= keepBytes.Length)      // 原样搬 PLTE/tRNS/gAMA… 等（IHDR 由我们重写）
            {
                int len = (keepBytes[q] << 24) | (keepBytes[q + 1] << 16) | (keepBytes[q + 2] << 8) | keepBytes[q + 3];
                string type = Encoding.ASCII.GetString(keepBytes, q + 4, 4);
                // tRNS 只对 0/2/3 号颜色类型合法；降通道成 4/0 号时必须丢掉它
                bool trnsOK = colorType == 0 || colorType == 2 || colorType == 3;
                if (type != "IHDR" && !(type == "tRNS" && !trnsOK)) ms2.Write(keepBytes, q, len + 12);
                q += 12 + len;
            }
            Chunk(ms2, "IDAT", nz);
            Chunk(ms2, "IEND", new byte[0]);
            return ms2.ToArray();
        }

        /// <summary>给一段裸 deflate 数据套上 zlib 头（0x78 0xDA）与 Adler-32 尾巴。</summary>
        static byte[] ZlibWrap(byte[] def, uint adA, uint adB)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0xDA);
            ms.Write(def, 0, def.Length);
            uint ad = (adB << 16) | adA;
            ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
            return ms.ToArray();
        }

        /// <summary>把 stream 读满 buf（DeflateStream.Read 可能只给一部分）。读不满返回 false。</summary>
        static bool ReadFull(Stream s, byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int r = s.Read(buf, got, len - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        static byte[] BuildIhdr(int w, int h, int bitDepth, int colorType)
        {
            var b = new byte[13];
            WriteBE(b, 0, w); WriteBE(b, 4, h);
            b[8] = (byte)bitDepth; b[9] = (byte)colorType; b[10] = 0; b[11] = 0; b[12] = 0;
            return b;
        }

        /// <summary>从 Bitmap 编码 PNG。allowGray=true 时才允许降到灰度 / 灰度+alpha 通道。</summary>
        public static byte[] Encode(Bitmap bmp, out string info, bool allowGray = true)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            bool hasAlpha = false;
            bool gray = true;
            byte[] px;
            try
            {
                int stride = data.Stride;
                px = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
                for (int y = 0; y < h && (!hasAlpha || gray); y++)
                {
                    int o = y * stride;
                    for (int x = 0; x < w; x++, o += 4)
                    {
                        byte b0 = px[o], g0 = px[o + 1], r0 = px[o + 2], a0 = px[o + 3];
                        if (a0 != 255) hasAlpha = true;
                        if (gray && (b0 != g0 || r0 != g0)) gray = false;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }

            int ch = hasAlpha ? (gray ? 2 : 4) : (gray ? 1 : 3);      // 灰度 / 灰度+alpha / RGB / RGBA
            if (!allowGray) { gray = false; ch = hasAlpha ? 4 : 3; }  // 只走 RGB/RGBA（通道语义与 GDI+ 一致）
            byte colorType = hasAlpha ? (gray ? (byte)4 : (byte)6) : (gray ? (byte)0 : (byte)2);
            byte[] rows = new byte[(w * ch + 1) * h];
            int stride2 = data.Stride;
            int best = 0;
            var cand = new byte[w * ch];
            var line = new byte[w * ch];
            var prev = new byte[w * ch];
            long scoreNone = 0, scoreSub = 0, scoreUp = 0, scoreAvg = 0, scorePaeth = 0;
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                int o = y * stride2;
                for (int x = 0; x < w; x++, o += 4)
                {
                    byte b0 = px[o], g0 = px[o + 1], r0 = px[o + 2], a0 = px[o + 3];
                    int t = x * ch;
                    if (ch == 1) line[t] = g0;
                    else if (ch == 2) { line[t] = g0; line[t + 1] = a0; }
                    else if (ch == 3) { line[t] = r0; line[t + 1] = g0; line[t + 2] = b0; }
                    else { line[t] = r0; line[t + 1] = g0; line[t + 2] = b0; line[t + 3] = a0; }
                }
                long[] sc = new long[5];
                byte[] bestRow = null;
                for (int f = 0; f < 5; f++)
                {
                    long s = 0;
                    for (int i = 0; i < line.Length; i++)
                    {
                        byte a1 = i >= ch ? line[i - ch] : (byte)0;
                        byte b1 = prev[i];
                        byte c1 = i >= ch ? prev[i - ch] : (byte)0;
                        int v;
                        switch (f)
                        {
                            case 0: v = line[i]; break;
                            case 1: v = line[i] - a1; break;
                            case 2: v = line[i] - b1; break;
                            case 3: v = line[i] - (a1 + b1) / 2; break;
                            default:
                                int pp = a1 + b1 - c1;
                                int pa = Math.Abs(pp - a1), pb = Math.Abs(pp - b1), pc = Math.Abs(pp - c1);
                                int pr = (pa <= pb && pa <= pc) ? a1 : (pb <= pc ? b1 : c1);
                                v = line[i] - pr;
                                break;
                        }
                        cand[i] = (byte)v;
                        s += Math.Abs((int)(sbyte)cand[i]);   // 先转 int：Math.Abs(sbyte.MinValue) 会溢出
                    }
                    if (bestRow == null || s < sc[best]) { best = f; bestRow = (byte[])cand.Clone(); sc[f] = s; }
                    else sc[f] = s;
                }
                rows[p++] = (byte)best;
                Buffer.BlockCopy(bestRow, 0, rows, p, bestRow.Length);
                p += bestRow.Length;
                Buffer.BlockCopy(line, 0, prev, 0, line.Length);
                scoreNone += sc[0]; scoreSub += sc[1]; scoreUp += sc[2]; scoreAvg += sc[3]; scorePaeth += sc[4];
            }

            byte[] z = Zlib(rows);
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, 0, 8);
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            Chunk(ms, "IHDR", ihdr);
            Chunk(ms, "IDAT", z);
            Chunk(ms, "IEND", new byte[0]);
            info = L.F("{0} {1}x{2} 通道{3} 滤波{4} 原始{5}B→压缩{6}B",
                hasAlpha ? (gray ? "灰度+alpha" : "RGBA") : (gray ? "灰度" : "RGB"), w, h, ch, FilterName(best), rows.Length, z.Length);
            return ms.ToArray();
        }

        static string FilterName(int f)
        {
            switch (f) { case 0: return "None"; case 1: return "Sub"; case 2: return "Up"; case 3: return "Avg"; default: return "Paeth"; }
        }

        /// <summary>PNG 压缩档：固定 zlib level 6（Optimal）。
        /// 曾经的「PNG 极限压缩」（level 9）已在 2.14 删除 —— 实测慢 3.3 倍只再省 2.9% 字节，
        /// 而它以"勾一下"的形式出现在界面上，用户完全看不到这个代价。</summary>
        static CompressionLevel Level()
        {
            // 仅供回归/测量用（界面上没有入口）：KOITEX_PNG_LEVEL=fastest|optimal|smallest|none
            string e = Environment.GetEnvironmentVariable("KOITEX_PNG_LEVEL");
            if (e == "fastest") return CompressionLevel.Fastest;       // = zlib 1
            if (e == "smallest") return CompressionLevel.SmallestSize; // = zlib 9
            if (e == "none") return CompressionLevel.NoCompression;
            return CompressionLevel.Optimal;                           // = zlib 6
        }
        /// <summary>诊断计数（KOITEX_TIME=1 时打印）：Recompress 调用次数、Pack 次数、各阶段耗时。</summary>
        public static int NRecomp, NPack;
        public static double TInflate, TFilter, TDeflate;
        /// <summary>送进 deflate 的扫描线总字节数；以及"灰度优先"救回来的次数（省掉整轮彩色版）。</summary>
        public static long TDeflateBytes;
        public static int GraySkippedColor;
        /// <summary>实验项：把「每个像素 R==G==B」的贴图存成灰度 / 灰度+alpha（2 或 1 通道，无损但游戏兼容性待验证）。
        /// 界面勾选框 / 命令行 pnga=1。</summary>
        public static bool UseGrayAlpha = true;   // 2.8 起默认开（用户实测游戏加载正常）

        static byte[] Zlib(byte[] raw)
        {
            var body = new MemoryStream();
            using (var ds = new DeflateStream(body, Level(), true))
                ds.Write(raw, 0, raw.Length);
            byte[] def = body.ToArray();
            var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0xDA);                    // zlib header（最大压缩档）
            ms.Write(def, 0, def.Length);
            uint a = 1, b = 0;                                          // Adler-32
            for (int i = 0; i < raw.Length; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
            uint ad = (b << 16) | a;
            ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
            return ms.ToArray();
        }

        static void WriteBE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        static readonly uint[] CrcTable = BuildCrc();
        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
            var tb = Encoding.ASCII.GetBytes(type);
            s.Write(tb, 0, 4); s.Write(data, 0, data.Length);
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < 4; i++) c = CrcTable[(c ^ tb[i]) & 0xFF] ^ (c >> 8);
            for (int i = 0; i < data.Length; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            c ^= 0xFFFFFFFFu;
            var cb = new byte[4]; WriteBig(cb, c); s.Write(cb, 0, 4);
        }

        static void WriteBig(byte[] b, uint v)
        {
            b[0] = (byte)(v >> 24); b[1] = (byte)(v >> 16); b[2] = (byte)(v >> 8); b[3] = (byte)v;
        }
    }
}

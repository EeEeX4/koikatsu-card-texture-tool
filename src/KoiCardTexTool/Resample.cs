using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace KoiCardTexTool
{
    /// <summary>"分辨率不变、少丢细节"的降采样路径（预研结论，默认关闭）。
    ///
    /// 背景（离线实测，见 _work\resample\H4-H5预研报告.md 附录 A）：
    ///   细节图（绑在 NormalMap 槽上的灰度数据图）在 ×4 降采样时，
    ///   局部对比度只剩 7%~25%；而换掉降采样核 + 在低分辨率域轻度锐化后，
    ///   对比度保持能提升 40%+，且 SSIM/RMSE 同时改善。
    ///
    /// ① <see cref="Area"/>：**面积平均**（整数倍时为精确块平均）。
    ///    GDI+ 没有这个滤波器；它的负瓣会抵消部分高频，所以双三次更"平滑"、文件更小，
    ///    但细节也更少。实测换核后 gray_30 的对比度保持 7.4% → 14.5%（翻倍）。
    /// ② <see cref="UnsharpInPlace"/>：在**低分辨率域**（存进卡里的那张小图上）做轻度锐化，
    ///    而不是放大后再做——后者只会放大双线性插值的伪影。alpha 通道不动（它是覆盖率，
    ///    锐化会让掩罩边缘产生光晕）。
    ///
    /// 只对"数据类"贴图生效（法线/掩罩/高度），彩色贴图仍走原来的双三次。**默认关闭**。
    /// </summary>
    static class Resample
    {
        /// <summary>总开关（命令行 kernel=bicubic 可关）。**默认开**。
        /// 用户拿 2.10 的同分辨率测试卡进游戏实测后确认「新方案细节更好」，故设为默认：
        /// 人物卡 +0.5 MB（+2.0%）换对比度保持 +23%、光照误差 −18%；衣服卡 +0.2 MB（+0.9%）。</summary>
        public static bool DetailMode = true;
        /// <summary>低分辨率域锐化强度（命令行 sharpen=，0 表示不锐化）。
        /// 0.6 是三张真实细节图上"SSIM/RMSE/对比度同时改善"的甜点；1.2 起会有 5% 像素打到饱和。</summary>
        public static double Sharpen = 0.6;
        /// <summary>锐化强度百分比（界面上的「锐化」滑条 / 命令行 sharpenpct=）。100 = 用下面这两档基准值，
        /// 0 = 完全关闭锐化。预设里存字段 sharpenPct；老预设没有这个字段 → 100 → 行为与以前一致。</summary>
        public static int SharpenPct = 100;

        /// <summary>实际生效的平坦区锐化量（= Sharpen × 百分比）。</summary>
        public static double SharpenK { get { return Sharpen * SharpenPct / 100.0; } }
        /// <summary>实际生效的边缘区锐化量（未设边缘上限时退回平坦区值）。</summary>
        public static double SharpenEdgeK
        {
            get { return (SharpenEdgeMax > Sharpen ? SharpenEdgeMax : Sharpen) * SharpenPct / 100.0; }
        }

        /// <summary>是否连 **alpha 通道** 一起锐化。只对**数据类贴图**（法线/掩罩/高度）开：
        /// 实测 17 张掩罩里有 7 张（41%）的掩罩数据住在 alpha 通道，而旧实现只锐化 BGR，
        /// 于是这些掩罩降采样后只会变糊、永远等不到锐化（典型例子 alpha 方差占比 0.892）。
        /// 彩色贴图的 alpha 是"透明度渐变"，锐化它会长出硬边 → 一律不动。
        /// 命令行 alphaSharp=0 可关，KOITEX_NOALPHASHARP=1 亦可。</summary>
        public static bool SharpAlphaData = true;

        /// <summary>锐化方式：cas = 对比度自适应（默认）；edge = 边缘感知 USM；
        /// struct = 结构保留降采样（实验项 E，见 <see cref="StructGamma"/>）。</summary>
        public static string SharpMode = "cas";

        /// <summary>「锐化方式」是不是**用户明确选过**的。
        /// 只有明确选过（界面动过下拉框 / 命令行写过 sharpcas=）才写进预设并生效；
        /// 没选过就跟随"当前默认"—— 这样 2.15/2.16 存下的老预设（当时默认是 edge）
        /// 在 2.17 换成 cas 之后会自动跟着走，而用户真挑过的不会被悄悄改掉。</summary>
        public static bool SharpModeExplicit = false;

        /// <summary>【实验项 E】结构保留降采样的强度 gamma（0 = 关闭，走普通面积平均）。
        /// 越大细线/蕾丝这类"少数结构"保留得越实，代价是局部亮度偏移越大。
        /// 命令行 struct=0.6；界面暂不暴露（先测清楚再决定要不要做成选项）。</summary>
        public static double StructGamma = 0;
        // 逐卡/逐贴图的统计计数：**[ThreadStatic]** —— 并行处理时每个线程各数各的，互不串台。
        // 调用方在处理一张卡（或一张贴图）前后各读一次、取差值，就得到这一张的准确数字。
        [ThreadStatic] public static int AreaUsed;
        [ThreadStatic] public static int SharpUsed;
        [ThreadStatic] public static int SameSize;
        [ThreadStatic] public static int UpUsed;        // v2.33：真的走了放大几张

        /// <summary>哪些类算"数据贴图"（预研只验证了这几类；彩色贴图走另一套开关）。</summary>
        public static bool IsDataClass(string cls)
        {
            return cls == "normal" || cls == "mask" || cls == "height";
        }

        /// <summary>彩色类是否也走面积平均+锐化（命令行 colorarea=0/1，界面「彩色贴图也走面积平均」）。
        /// **2.32 起默认开**（2.31 及以前默认关）。
        ///
        /// 改成默认开的依据 —— 全语料单变量 A/B（example\ 14 张卡，1024/auto/90，plan=p_zl.json，
        /// own=1 ownmax=4096，唯一变量就是 colorarea）：
        ///   · 内容发生变化的 **15 张全是彩色类**（maintex 12 / reflection 2 / matcap 1），
        ///     其余 196 张贴图**逐字节不变**（含全部法线/掩罩/高度）。
        ///   · SSIM **15/15 全部上升**（均值 +0.0033，中位 +0.0023，最小 +0.0001）；
        ///     振铃/过冲下降（均值 −0.90pp）；边缘保持均值 −1.0pp（面积平均本来就更软，符合预期）。
        ///   · 这 15 张合计 25,878,778 → 24,826,268 字节（**−4.1%**）；
        ///     整卡合计 356,122,243 → 355,069,733（**−0.30%**），耗时 28s → 25s。
        ///
        /// 再拆一刀（加一条 colorarea=1 premul=0 的臂）看是谁的功劳：
        ///   · **只换核**（premul 两边都是关）：SSIM 均值 −0.0012（11 涨 4 跌，平手）、
        ///     边缘保持 +1.3pp、字节 −2.5%；
        ///   · **预乘 alpha 平均**补上了剩下的 SSIM 增益（它只对真有 alpha 的贴图起作用）。
        ///   也就是说：这条路**同时**打开了预乘平均 —— GDI+ 那条路根本不用 premul
        ///   （它对透明区的 RGB 也照插值，边缘会把垃圾色混进来）。
        ///
        /// 旧注释里写的"代价 +17~36% 字节"是早期**没有锐化、单张贴图**条件下量的最坏情况，
        /// 与本轮全语料实测不符：本轮 14/15 张反而更小，唯一 +31.2% 的那张
        /// （KKCoordeF_20250126215045638 TexID 47）SSIM 也 +0.0050、边缘保持 +25.5pp，
        /// 属于"细节留得更多所以更大"，不是变差。已按实测改写。
        /// 命令行 colorarea=0 可退回旧行为（只影响彩色类，法线/掩罩/高度仍走面积平均）。</summary>
        public static bool ColorMode = true;

        /// <summary>含真 alpha 的**彩色**贴图是否按预乘 alpha 平均（命令行 premul=0 可关）。
        /// H1b 预研实测：真实贴图边缘误差 −11.5%，透明区垃圾色最坏情况 −61%，
        /// 而且结果与"透明区填了什么颜色"完全无关。对数据贴图的 alpha（它是独立掩罩）不适用。</summary>
        public static bool Premul = true;

        /// <summary>某一类是否要走新降采样路径。</summary>
        public static bool Applies(string cls)
        {
            if (!DetailMode) return false;
            return IsDataClass(cls) || (ColorMode && !IsDataClass(cls));
        }

        /// <summary>【实验项 E】结构保留降采样（structure-preserving）。
        ///
        /// 与"降采样之后再锐化"是**不同的轴**：锐化是在低分辨率域里放大已有的对比，
        /// 而这一步是在**平均的时候**就保住"少数结构"（细线、蕾丝、绑带）。
        ///
        /// 对每个目标像素覆盖的源 f×f 块：
        ///   1) 算块均值 m、块内亮度的 min/max
        ///   2) 数少数派占比 r = min(暗于均值的像素数, 亮于均值的像素数) / n
        ///   3) 朝少数派的极值推：push = gamma * max(0, 1 − 2r)，delta = ±push·ext
        ///   4) 均衡的块（真正的边）r≈0.5 → push≈0 → **完全等于面积平均**
        ///
        /// 受控图实测（÷4）：阶跃边 / 棋盘格 / 线性渐变 / 纯平 → 与 Box **输出完全一致**
        /// （不会在普通边上产生光晕，也不会在平坦区造出假纹理）；
        /// 1 像素亮线 → 峰值从 63.75（=255/4，面积平均的精确解）提到 159.38（γ=1.0），
        /// 且**邻格仍为 0**（不像 Lanczos 那样把能量摊到邻居身上）。
        ///
        /// 代价：块均值被有意改变（细线画得更"实" = 局部更亮/更暗），
        /// 所以 gamma 越大整体亮度偏移越大（细线测试图整体均值 1.00 → 2.49）。
        /// 只在整数倍可用；非整数倍返回 null，由调用方退回 AreaF。</summary>
        public static unsafe Bitmap AreaStruct(Bitmap src, int w, int h, bool premul, double gamma)
        {
            int sw = src.Width, sh = src.Height;
            if (w <= 0 || h <= 0 || w > sw || h > sh) return null;
            if (sw % w != 0 || sh % h != 0) return null;
            int fx = sw / w, fy = sh / h;
            int n = fx * fy;
            if (n <= 1) return null;

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int half = n / 2;
                for (int y = 0; y < h; y++)
                {
                    byte* drow = (byte*)dd.Scan0 + y * dd.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        // 第一遍：各通道和、亮度 min/max/和
                        int sb = 0, sg = 0, sr = 0, sa = 0;
                        int lmin = 255, lmax = 0; long lsum = 0;
                        for (int yy = 0; yy < fy; yy++)
                        {
                            byte* srow = (byte*)sd.Scan0 + (y * fy + yy) * sd.Stride + x * fx * 4;
                            for (int xx = 0; xx < fx; xx++)
                            {
                                int bb = srow[xx * 4], gg = srow[xx * 4 + 1], rr = srow[xx * 4 + 2], aa = srow[xx * 4 + 3];
                                sb += bb; sg += gg; sr += rr; sa += aa;
                                int l = (rr * 77 + gg * 151 + bb * 28) >> 8;
                                if (l < lmin) lmin = l;
                                if (l > lmax) lmax = l;
                                lsum += l;
                            }
                        }
                        int ml = (int)((lsum + half) / n);
                        // 第二遍：数少数派（相对块均值）
                        int nlo = 0, nhi = 0;
                        for (int yy = 0; yy < fy; yy++)
                        {
                            byte* srow = (byte*)sd.Scan0 + (y * fy + yy) * sd.Stride + x * fx * 4;
                            for (int xx = 0; xx < fx; xx++)
                            {
                                int l = (srow[xx * 4 + 2] * 77 + srow[xx * 4 + 1] * 151 + srow[xx * 4] * 28) >> 8;
                                if (l < ml) nlo++; else if (l > ml) nhi++;
                            }
                        }
                        bool dark = nlo <= nhi;
                        double r = (double)(dark ? nlo : nhi) / n;
                        double push = gamma * (1.0 - 2.0 * r);
                        if (push < 0) push = 0;
                        double ext = dark ? (ml - lmin) : (lmax - ml);
                        int delta = (int)Math.Round((dark ? -1.0 : 1.0) * push * ext);

                        int mb = (sb + half) / n, mg = (sg + half) / n, mr = (sr + half) / n, ma = (sa + half) / n;
                        if (premul && sa < n * 255 && sa > 0)
                        {
                            int pb = 0, pg = 0, pr = 0, pla = 0;
                            for (int yy = 0; yy < fy; yy++)
                            {
                                byte* srow = (byte*)sd.Scan0 + (y * fy + yy) * sd.Stride + x * fx * 4;
                                for (int xx = 0; xx < fx; xx++)
                                {
                                    int al = srow[xx * 4 + 3];
                                    pb += srow[xx * 4] * al; pg += srow[xx * 4 + 1] * al; pr += srow[xx * 4 + 2] * al; pla += al;
                                }
                            }
                            if (pla > 0) { mb = (pb + pla / 2) / pla; mg = (pg + pla / 2) / pla; mr = (pr + pla / 2) / pla; }
                        }
                        byte* d = drow + x * 4;
                        int ob = mb + delta, og = mg + delta, orr = mr + delta;
                        d[0] = (byte)(ob < 0 ? 0 : (ob > 255 ? 255 : ob));
                        d[1] = (byte)(og < 0 ? 0 : (og > 255 ? 255 : og));
                        d[2] = (byte)(orr < 0 ? 0 : (orr > 255 ? 255 : orr));
                        d[3] = (byte)ma;
                    }
                }
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        /// <summary>面积平均。只在整数倍、且是缩小时可用；否则返回 null（调用方退回 GDI+）。
        /// premul=true 时按"覆盖率加权"（预乘 alpha）平均彩色值，edge 不会混进透明区的垃圾色。</summary>
        public static unsafe Bitmap Area(Bitmap src, int w, int h, bool premul)
        {
            int sw = src.Width, sh = src.Height;
            if (w <= 0 || h <= 0 || w > sw || h > sh) return null;
            if (sw % w != 0 || sh % h != 0) return null;
            int fx = sw / w, fy = sh / h;
            if (fx * fy <= 1) return null;

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = fx * fy, half = n / 2;
                for (int y = 0; y < h; y++)
                {
                    byte* drow = (byte*)dd.Scan0 + y * dd.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int b = 0, g = 0, r = 0, a = 0;
                        for (int yy = 0; yy < fy; yy++)
                        {
                            byte* srow = (byte*)sd.Scan0 + (y * fy + yy) * sd.Stride + x * fx * 4;
                            for (int xx = 0; xx < fx; xx++)
                            {
                                b += srow[xx * 4]; g += srow[xx * 4 + 1];
                                r += srow[xx * 4 + 2]; a += srow[xx * 4 + 3];
                            }
                        }
                        byte* d = drow + x * 4;
                        if (premul && a < n * 255)
                        {
                            // 预乘：先按覆盖率加权求和，再用平均后的覆盖率解出来
                            int sb = 0, sg = 0, sr = 0, sa = 0;
                            for (int yy = 0; yy < fy; yy++)
                            {
                                byte* srow = (byte*)sd.Scan0 + (y * fy + yy) * sd.Stride + x * fx * 4;
                                for (int xx = 0; xx < fx; xx++)
                                {
                                    int al = srow[xx * 4 + 3];
                                    sb += srow[xx * 4] * al; sg += srow[xx * 4 + 1] * al;
                                    sr += srow[xx * 4 + 2] * al; sa += al;
                                }
                            }
                            d[0] = sa > 0 ? (byte)((sb + sa / 2) / sa) : (byte)0;
                            d[1] = sa > 0 ? (byte)((sg + sa / 2) / sa) : (byte)0;
                            d[2] = sa > 0 ? (byte)((sr + sa / 2) / sa) : (byte)0;
                            d[3] = (byte)((a + half) / n);
                        }
                        else
                        {
                            d[0] = (byte)((b + half) / n);
                            d[1] = (byte)((g + half) / n);
                            d[2] = (byte)((r + half) / n);
                            d[3] = (byte)((a + half) / n);
                        }
                    }
                }
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        /// <summary>边缘感知锐化上限（命令行为 0 时按均匀锐化）。
        /// 平坦区用 Sharpen，边缘区线性过渡到 SharpenEdgeMax。
        /// 理由（离线实测）：均匀强锐化会把平坦区的插值噪声一起放大，看起来"脏"；
        /// 只在梯度大的地方加强，线条更实而平坦区保持干净。</summary>
        public static double SharpenEdgeMax = 0;

        /// <summary>任意倍率的面积平均（分数权重，可分离两趟）。
        /// 整数倍请用 <see cref="Area"/>（更快、整数累加更精确）；这里是给 1536 这种非整数倍用的。
        /// 之前的实现只处理整数倍，非整数倍会静默退回 GDI+ 双三次 —— 既没有面积平均、**也不会锐化**，
        /// 是个隐蔽的行为不一致，这里补上。</summary>
        public static unsafe Bitmap AreaF(Bitmap src, int w, int h, bool premul)
        {
            int sw = src.Width, sh = src.Height;
            if (w <= 0 || h <= 0 || w > sw || h > sh) return null;
            if (sw == w && sh == h) return null;
            if (sw % w == 0 && sh % h == 0) return null;               // 整数倍交给 Area()

            // 权重表（每个目标像素覆盖的源像素及其权重；权重和 = 源/目标 比例）
            int[] xo, xc, xi; float[] xw;
            BuildWeights(sw, w, out xo, out xc, out xi, out xw);
            int[] yo, yc, yi; float[] yw;
            BuildWeights(sh, h, out yo, out yc, out yi, out yw);

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBuf = new float[w * 4];                          // 水平重采样后的源行
                var acc = new float[w * 4];                             // 垂直累加
                int maxCnt = 0;
                for (int i = 0; i < w; i++) if (xc[i] > maxCnt) maxCnt = xc[i];
                for (int dy = 0; dy < h; dy++)
                {
                    Array.Clear(acc, 0, acc.Length);
                    for (int k = 0; k < yc[dy]; k++)
                    {
                        int srcy = yi[yo[dy] + k];
                        float wy = yw[yo[dy] + k];
                        byte* srow = (byte*)sd.Scan0 + srcy * sd.Stride;
                        // 水平一趟（每行重算，省内存；非整数倍时最多重复 ~2 次，开销可接受）
                        for (int dx = 0; dx < w; dx++)
                        {
                            float b = 0, g = 0, r = 0, a = 0;
                            for (int j = 0; j < xc[dx]; j++)
                            {
                                int sx = xi[xo[dx] + j];
                                float wx = xw[xo[dx] + j];
                                byte* p = srow + sx * 4;
                                float av = p[3];
                                if (premul) { b += p[0] * av * wx; g += p[1] * av * wx; r += p[2] * av * wx; }
                                else { b += p[0] * wx; g += p[1] * wx; r += p[2] * wx; }
                                a += av * wx;
                            }
                            rowBuf[dx * 4] = b; rowBuf[dx * 4 + 1] = g; rowBuf[dx * 4 + 2] = r; rowBuf[dx * 4 + 3] = a;
                        }
                        for (int i = 0; i < w * 4; i++) acc[i] += rowBuf[i] * wy;
                    }
                    byte* drow = (byte*)dd.Scan0 + dy * dd.Stride;
                    for (int dx = 0; dx < w; dx++)
                    {
                        float a = acc[dx * 4 + 3];
                        float b, g, r;
                        if (premul && a > 0.5f)
                        {
                            b = acc[dx * 4] / a; g = acc[dx * 4 + 1] / a; r = acc[dx * 4 + 2] / a;
                        }
                        else { b = acc[dx * 4]; g = acc[dx * 4 + 1]; r = acc[dx * 4 + 2]; }
                        byte* d = drow + dx * 4;
                        d[0] = Clamp(b); d[1] = Clamp(g); d[2] = Clamp(r); d[3] = Clamp(a);
                    }
                }
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        static byte Clamp(float v)
        {
            int i = (int)(v + 0.5f);
            return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i));
        }

        /// <summary>构造"每个目标像素 ← 源像素区间与权重"的压缩表。</summary>
        static void BuildWeights(int srcN, int dstN, out int[] off, out int[] cnt, out int[] idx, out float[] wt)
        {
            double s = (double)srcN / dstN;
            off = new int[dstN]; cnt = new int[dstN];
            var idxL = new System.Collections.Generic.List<int>();
            var wtL = new System.Collections.Generic.List<float>();
            for (int d = 0; d < dstN; d++)
            {
                double lo = d * s, hi = (d + 1) * s;
                int i0 = (int)Math.Floor(lo), i1 = (int)Math.Ceiling(hi) - 1;
                if (i1 < i0) i1 = i0;
                if (i1 > srcN - 1) i1 = srcN - 1;
                off[d] = idxL.Count;
                int n = 0;
                for (int i = i0; i <= i1; i++)
                {
                    double a = Math.Max(lo, i), b = Math.Min(hi, i + 1);
                    double ov = b - a;
                    if (ov <= 0) continue;
                    idxL.Add(i); wtL.Add((float)(ov / s)); n++;
                }
                cnt[d] = n;
            }
            idx = idxL.ToArray(); wt = wtL.ToArray();
        }

        // ============================ 放大（v2.33） ============================
        //
        // 和降采样相反的方向：目标尺寸**大于**源尺寸。用得着的场景只有一个 ——
        // mod 的主贴图本身就是 1024，游戏里近距离看很糊，放大到 2048 让重建更平滑、锐化更有效。
        // 代价：像素 ×4，PNG 实测 2.68~2.88 倍（不是想当然的 3~4 倍）。
        //
        // 两个容易写错的地方（第一版离线实验两处都错了，值得写下来）：
        //  ① **核宽度只在缩小时展宽**。项目里给降采样写的公式是 w = K((idx-c)/s)（s=源/目标）——
        //     对放大（s<1）照抄会把核**收窄**：×2 时算出 [1.125, -0.125] 这种 2 抽头"锐化"权重，
        //     插值退化成最近邻，而且 lanczos2 与 catmull 会给出逐字节相同的结果（权重巧合相等）。
        //     正确做法是 f = max(1, s)：放大不低通，用核的天然宽度。
        //  ② **振铃要拿"核支撑范围内的源像素 min/max"当参考**，不是"覆盖到的那一个源像素"
        //     （后者是降采样的口径，放大时双线性也会被判成"越界"，所有核都会报 ~8% 的假振铃）。

        /// <summary>放大用哪个核。**只读**静态：没有"按贴图指定放大核"这种需求，
        /// 所以不像锐化那样逐贴图传参（逐贴图写静态才会串味）。默认 lanczos3。</summary>
        public static string UpKernel = "lanczos3";

        /// <summary>全局放大倍率（命令行 up=0/2/4，界面「放大」下拉）。**默认 0 = 不放大**：
        /// 这条路会让体积涨到 2.7~2.9 倍，必须是用户明确开启的。
        /// 规则链里哪一级写了明确的 Up（≥0），就以哪一级为准（单张贴图 > 按部位 > 类型表）。</summary>
        public static int UpGlobal = 0;

        /// <summary>放大的硬上限（像素）。不管「最大边」写多大，放大都不越过它 ——
        /// 游戏侧的尺寸上限 + "体积必须可控"的最后一道闸。默认 4096。</summary>
        public static int UpCeiling = 4096;

        /// <summary>支持的放大核名（命令行 upkernel= 与界面下拉共用）。</summary>
        public static readonly string[] UpKernels = { "lanczos3", "lanczos2", "catmull", "mitchell", "bilinear", "nearest" };

        public static bool IsUpKernel(string k)
        {
            if (string.IsNullOrEmpty(k)) return false;
            foreach (var s in UpKernels) if (s == k) return true;
            return false;
        }

        static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-9) return 1.0;
            return Math.Sin(Math.PI * x) / (Math.PI * x);
        }

        /// <summary>Mitchell/Catmull 这类 B-C 三次核（x 为距离，单位=源像素）。</summary>
        static double CubicAt(double x, double B, double C)
        {
            double a = Math.Abs(x), a2 = a * a, a3 = a2 * a;
            if (a < 1.0) return ((12 - 9 * B - 6 * C) * a3 + (-18 + 12 * B + 6 * C) * a2 + (6 - 2 * B)) / 6.0;
            if (a < 2.0) return ((-B - 6 * C) * a3 + (6 * B + 30 * C) * a2 + (-12 * B - 48 * C) * a + (8 * B + 24 * C)) / 6.0;
            return 0.0;
        }

        /// <summary>核函数：x = 距离（以源像素为单位）。未知名字按 lanczos3 处理。</summary>
        static double KernelAt(string k, double x)
        {
            double a = Math.Abs(x);
            switch (k)
            {
                case "nearest": return a < 0.5 ? 1.0 : 0.0;
                case "bilinear": return a < 1.0 ? 1.0 - a : 0.0;
                case "mitchell": return CubicAt(x, 1.0 / 3.0, 1.0 / 3.0);
                case "catmull": return CubicAt(x, 0.0, 0.5);
                case "lanczos2": return a < 2.0 ? Sinc(x) * Sinc(x / 2.0) : 0.0;
                default: return a < 3.0 ? Sinc(x) * Sinc(x / 3.0) : 0.0;
            }
        }

        static double RadiusOf(string k)
        {
            switch (k)
            {
                case "nearest": return 0.5;
                case "bilinear": return 1.0;
                case "lanczos3": return 3.0;
                default: return 2.0;          // mitchell / catmull / lanczos2
            }
        }

        /// <summary>构造"放大"用的权重表。f = max(1, 源/目标)：**只在小图放大时不展宽**，
        /// 与降采样那条（<see cref="BuildWeights"/> 的面积权重）是两套东西，故意不复用。</summary>
        static void BuildKernelWeights(int srcN, int dstN, string kernel, double radius,
                                       out int[] off, out int[] cnt, out int[] idx, out float[] wt)
        {
            double s = (double)srcN / dstN;
            double f = s > 1.0 ? s : 1.0;
            double R = radius * f;
            off = new int[dstN]; cnt = new int[dstN];
            var idxL = new System.Collections.Generic.List<int>();
            var wtL = new System.Collections.Generic.List<float>();
            for (int d = 0; d < dstN; d++)
            {
                double c = (d + 0.5) * s - 0.5;
                int i0 = (int)Math.Floor(c - R), i1 = (int)Math.Ceiling(c + R);
                off[d] = idxL.Count;
                double sum = 0;
                for (int i = i0; i <= i1; i++)
                {
                    double v = KernelAt(kernel, (i - c) / f);
                    if (v == 0.0) continue;
                    int ic = i < 0 ? 0 : (i > srcN - 1 ? srcN - 1 : i);
                    idxL.Add(ic); wtL.Add((float)v); sum += v;
                }
                int n = idxL.Count - off[d];
                if (n == 0 || Math.Abs(sum) < 1e-9)
                {
                    // 退化（例如最近邻的 c 正好落在两个源像素正中）：退回最近的那个源像素。
                    int ic = (int)Math.Round(c);
                    if (ic < 0) ic = 0; if (ic > srcN - 1) ic = srcN - 1;
                    idxL.Add(ic); wtL.Add(1f); n = 1;
                }
                else
                {
                    // 归一化：权重和 = 1，保证直流电平不变（不然整体会变亮/变暗）
                    for (int t = 0; t < n; t++) wtL[off[d] + t] = (float)(wtL[off[d] + t] / sum);
                }
                cnt[d] = n;
            }
            idx = idxL.ToArray(); wt = wtL.ToArray();
        }

        /// <summary>放大：目标尺寸大于源尺寸时用。可分离两趟（每输出行重算水平趟，省内存 ——
        /// 和 <see cref="AreaF"/> 同一套路，4096² 的中间结果会要 134 MB，不能整张存）。</summary>
        public static unsafe Bitmap Upscale(Bitmap src, int w, int h, string kernel, bool premul)
        {
            int sw = src.Width, sh = src.Height;
            if (w <= sw && h <= sh) return null;                   // 不是放大，交给别的路径
            string k = IsUpKernel(kernel) ? kernel : UpKernel;
            double r = RadiusOf(k);

            int[] xo, xc, xi; float[] xw;
            BuildKernelWeights(sw, w, k, r, out xo, out xc, out xi, out xw);
            int[] yo, yc, yi; float[] yw;
            BuildKernelWeights(sh, h, k, r, out yo, out yc, out yi, out yw);

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBuf = new float[w * 4];                      // 某一源行的水平结果
                var acc = new float[w * 4];                         // 垂直累加
                for (int dy = 0; dy < h; dy++)
                {
                    Array.Clear(acc, 0, acc.Length);
                    for (int t = 0; t < yc[dy]; t++)
                    {
                        int srcy = yi[yo[dy] + t];
                        float wy = yw[yo[dy] + t];
                        byte* srow = (byte*)sd.Scan0 + srcy * sd.Stride;
                        for (int dx = 0; dx < w; dx++)
                        {
                            float b = 0, g = 0, rr = 0, a = 0;
                            for (int j = 0; j < xc[dx]; j++)
                            {
                                byte* p = srow + xi[xo[dx] + j] * 4;
                                float wx = xw[xo[dx] + j];
                                float av = p[3];
                                if (premul) { b += p[0] * av * wx; g += p[1] * av * wx; rr += p[2] * av * wx; }
                                else { b += p[0] * wx; g += p[1] * wx; rr += p[2] * wx; }
                                a += av * wx;
                            }
                            rowBuf[dx * 4] = b; rowBuf[dx * 4 + 1] = g;
                            rowBuf[dx * 4 + 2] = rr; rowBuf[dx * 4 + 3] = a;
                        }
                        for (int i = 0; i < w * 4; i++) acc[i] += rowBuf[i] * wy;
                    }
                    byte* drow = (byte*)dd.Scan0 + dy * dd.Stride;
                    for (int dx = 0; dx < w; dx++)
                    {
                        float a = acc[dx * 4 + 3];
                        float b, g, rr;
                        if (premul && a > 0.5f)
                        {
                            b = acc[dx * 4] / a; g = acc[dx * 4 + 1] / a; rr = acc[dx * 4 + 2] / a;
                        }
                        else { b = acc[dx * 4]; g = acc[dx * 4 + 1]; rr = acc[dx * 4 + 2]; }
                        byte* d = drow + dx * 4;
                        d[0] = Clamp(b); d[1] = Clamp(g); d[2] = Clamp(rr); d[3] = Clamp(a);
                    }
                }
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }
        // ========================== 放大结束 ==========================

        /// <summary>低分辨率域轻度锐化：RGB = RGB + k*(RGB - blur3x3)。
        /// alphaToo=true 时**连 alpha 一起锐化** —— 见 <see cref="SharpAlphaData"/>。
        /// premul=true 时在**预乘 alpha 空间**里锐化（彩色贴图的 alpha 是"覆盖率"）：
        ///   只放大"实际看得见的那部分颜色"，全透明区的垃圾 RGB 不参与，避免透明边缘长出彩边。
        ///   实测（某卡半透明区的彩色偏差）：不预乘 4.41 → 9.46，预乘后应回到接近基线。</summary>
        public static unsafe void UnsharpInPlace(Bitmap bmp, double k, double kEdge, bool alphaToo, bool premul)
        {
            if (k <= 0) return;
            int w = bmp.Width, h = bmp.Height;
            if (w < 3 || h < 3) return;
            var srcCopy = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(srcCopy)) g.DrawImage(bmp, new Rectangle(0, 0, w, h));
            if (premul)
            {
                // 把源图变成"预乘"版本：BGR *= A/255。alpha 本身保持不动（它是覆盖率）。
                var pd = srcCopy.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = (byte*)pd.Scan0 + y * pd.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            int a = row[x * 4 + 3];
                            if (a == 255) continue;
                            row[x * 4] = (byte)(row[x * 4] * a / 255);
                            row[x * 4 + 1] = (byte)(row[x * 4 + 1] * a / 255);
                            row[x * 4 + 2] = (byte)(row[x * 4 + 2] * a / 255);
                        }
                    }
                }
                finally { srcCopy.UnlockBits(pd); }
            }
            var sd = srcCopy.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                bool edgeMode = kEdge > k;
                double refG = 1.0;
                if (edgeMode)
                {
                    // 梯度幅值的 95 分位（用 256 桶直方图近似，避免排序开销）
                    var hist = new int[256];
                    for (int y = 0; y < h; y++)
                    {
                        byte* srow = (byte*)sd.Scan0 + y * sd.Stride;
                        byte* up = (byte*)sd.Scan0 + (y > 0 ? y - 1 : 0) * sd.Stride;
                        byte* dn = (byte*)sd.Scan0 + (y < h - 1 ? y + 1 : h - 1) * sd.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                            int gx = (int)Lum(up, xp) - (int)Lum(up, xm) + 2 * ((int)Lum(srow, xp) - (int)Lum(srow, xm))
                                   + (int)Lum(dn, xp) - (int)Lum(dn, xm);
                            int gy = (int)Lum(dn, xm) - (int)Lum(up, xm) + 2 * ((int)Lum(dn, x) - (int)Lum(up, x))
                                   + (int)Lum(dn, xp) - (int)Lum(up, xp);
                            int mag = (int)Math.Sqrt((double)gx * gx + (double)gy * gy) >> 2;
                            hist[mag > 255 ? 255 : mag]++;
                        }
                    }
                    int total = w * h, want = (int)(total * 0.95), acc = 0, idx = 1;
                    for (; idx < 256; idx++) { acc += hist[idx]; if (acc >= want) break; }
                    refG = Math.Max(idx, 4);
                }
                for (int y = 0; y < h; y++)
                {
                    byte* drow = (byte*)dd.Scan0 + y * dd.Stride;
                    int ym = y > 0 ? y - 1 : 0, yp = y < h - 1 ? y + 1 : h - 1;
                    for (int x = 0; x < w; x++)
                    {
                        int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                        double kk = k;
                        if (edgeMode)
                        {
                            byte* up = (byte*)sd.Scan0 + ym * sd.Stride;
                            byte* dn = (byte*)sd.Scan0 + yp * sd.Stride;
                            byte* cc = (byte*)sd.Scan0 + y * sd.Stride;
                            int gx = (int)Lum(up, xp) - (int)Lum(up, xm) + 2 * ((int)Lum(cc, xp) - (int)Lum(cc, xm))
                                   + (int)Lum(dn, xp) - (int)Lum(dn, xm);
                            int gy = (int)Lum(dn, xm) - (int)Lum(up, xm) + 2 * ((int)Lum(dn, x) - (int)Lum(up, x))
                                   + (int)Lum(dn, xp) - (int)Lum(up, xp);
                            double m = Math.Sqrt((double)gx * gx + (double)gy * gy) / 4.0 / refG;
                            if (m > 1) m = 1;
                            kk = k + (kEdge - k) * m;
                        }
                        int nch = alphaToo ? 4 : 3;
                        byte* scc = (byte*)sd.Scan0 + y * sd.Stride;
                        for (int c = 0; c < nch; c++)                     // 默认只锐化 BGR，不动 alpha
                        {
                            int blur = 0;
                            int[] wx = { 1, 2, 1 }, wy = { 1, 2, 1 };
                            int[] xs = { xm, x, xp }, ys = { ym, y, yp };
                            for (int j = 0; j < 3; j++)
                                for (int i = 0; i < 3; i++)
                                {
                                    byte* p = (byte*)sd.Scan0 + ys[j] * sd.Stride + xs[i] * 4;
                                    blur += wy[j] * wx[i] * p[c];
                                }
                            blur = (blur + 8) >> 4;
                            // 预乘模式下 v 必须取**预乘副本**的值（blur 也是从副本算的），
                            // 否则"_预乘值 − 非预乘值的模糊_"会得出完全错误的量。
                            int v = (premul && c < 3) ? scc[x * 4 + c] : drow[x * 4 + c];
                            int nv = (int)Math.Round(v + kk * (v - blur));
                            if (premul && c < 3)
                            {
                                int a = scc[x * 4 + 3];                  // 覆盖率：锐化后解回直通颜色
                                // a 太小就不要动：解回直通色要"除以 a"，会把 8 位量化误差放大 255/a 倍
                                // （a=16 → ×16）。这些像素最多只有 12% 不透明度，改它收益极小、彩边风险很大。
                                if (a < 32) continue;                    // 直接跳过 → drow 保持原本的直通色
                                nv = (int)Math.Round(nv * 255.0 / a);
                            }
                            drow[x * 4 + c] = (byte)(nv < 0 ? 0 : (nv > 255 ? 255 : nv));
                        }
                    }
                }
            }
            finally { srcCopy.UnlockBits(sd); bmp.UnlockBits(dd); srcCopy.Dispose(); }
        }

        static unsafe int Lum(byte* row, int x)
        {
            return (row[x * 4 + 2] * 77 + row[x * 4 + 1] * 151 + row[x * 4] * 28) >> 8;   // 0.299/0.587/0.114
        }

        /// <summary>对比度自适应锐化（CAS 思路，AMD FidelityFX CAS 同款算子）。
        ///
        /// 与上面的 USM 的区别在"哪里锐得多"：
        ///   · USM（当前默认）：边缘感知 → **梯度越大锐得越狠**（kEdge 上限）。目标是把降采样糊掉的边找回来。
        ///   · CAS：局部对比度越大 → 权重越小 → **强边附近反而收敛**，把锐化量放在中频纹理上。
        ///     好处是几乎不产生 halo/振铃；代价是"恢复边缘锐度"不如 USM 猛。
        ///
        /// 算子（5 抽头十字，对 0..1 归一化的每通道独立做）：
        ///   mn = min(上下左右中)，mx = max(...)
        ///   amp = sqrt( saturate( min(mn, 1-mx) / mx ) )        // 对比度大 → amp 小
        ///   w   = amp * peak，peak = -1/lerp(8, 5, sharpness)   // sharpness∈[0,1]
        ///   out = (N+W+E+S)*w + C) / (4w + 1)
        ///
        /// sharpness 由 SharpenK 映射：s01 = SharpenK / 2.5（2.5 对应 200% 强度）。
        /// **允许 s01 > 1**（界面上的数字框可以填到 500%）：peak 会继续变负 = 更用力，
        /// 但钳在 -1.0（再负下去分母 4w+1 会趋近 0，算出来的值会炸）。</summary>
        public static unsafe void CasInPlace(Bitmap bmp, double sharpness, bool alphaToo, bool premul)
        {
            double s01 = sharpness / 2.5;
            if (s01 < 0) s01 = 0;
            if (s01 <= 0) return;
            // s01 最大 4（= 500% 强度）；peak 从 -0.125 一路到 -1.0 后饱和
            if (s01 > 4.0) s01 = 4.0;
            double peak = -1.0 / (8.0 + (5.0 - 8.0) * s01);
            if (peak < -1.0) peak = -1.0;

            int w = bmp.Width, h = bmp.Height;
            if (w < 3 || h < 3) return;
            var srcCopy = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(srcCopy)) g.DrawImage(bmp, new Rectangle(0, 0, w, h));
            var sd = srcCopy.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int nch = alphaToo ? 4 : 3;
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)sd.Scan0 + y * sd.Stride;
                    byte* up = (byte*)sd.Scan0 + (y > 0 ? y - 1 : 0) * sd.Stride;
                    byte* dn = (byte*)sd.Scan0 + (y < h - 1 ? y + 1 : h - 1) * sd.Stride;
                    byte* drow = (byte*)dd.Scan0 + y * dd.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                        for (int c = 0; c < nch; c++)
                        {
                            double n = up[xp * 4 + c], e = row[xp * 4 + c], so = dn[xp * 4 + c];
                            double we = row[xm * 4 + c], cc = row[x * 4 + c];
                            double mn = Math.Min(Math.Min(Math.Min(n, e), Math.Min(so, we)), cc);
                            double mx = Math.Max(Math.Max(Math.Max(n, e), Math.Max(so, we)), cc);
                            if (mx <= 1e-6) continue;                       // 全黑，无可锐化
                            double head = Math.Min(mn, 255.0 - mx);         // 离两端还有多少余量
                            double amp = Math.Sqrt(Math.Max(0.0, Math.Min(head, mx) / mx));
                            double ww = amp * peak;
                            double v = (n + e + so + we) * ww + cc;
                            v /= (4.0 * ww + 1.0);
                            drow[x * 4 + c] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : (int)Math.Round(v)));
                        }
                    }
                }
            }
            finally { srcCopy.UnlockBits(sd); bmp.UnlockBits(dd); srcCopy.Dispose(); }
        }

        // ================= 降噪 / 去块（方案 3 与方案 4）=================
        //
        // 重要前提（离线实测）：这批贴图的"细颗粒"**是内容**（织纹/刺绣/皮肤细节），
        // 不分青红皂白地降噪 = 抹掉真内容（实测对比度 57% → 12%，观感变塑料）。
        // 所以降噪只在两种情况下有意义：
        //   ① 配合"提高分辨率"一起用（把颗粒换成像素）→ Denoise = "all"
        //   ② **只处理源贴图自己就带 JPEG 伪影的**（作者从 JPEG 导出的）→ Denoise = "dirty"
        //      —— 这类图的"颗粒"里混的是压缩噪声（假细节），去掉它才是净收益。

        /// <summary>降噪模式：off / all / dirty（只处理块状度超阈值的源）。默认 off。</summary>
        public static string Denoise = "off";
        public static int DenoiseRadius = 3;
        public static double DenoiseEps = 0.01;     // 越大越强的平滑（0.01 ≈ 局部标准差阈值 25 级）
        public static double DirtyThreshold = 0.5;  // 源块状度阈值（干净源中位数约 0.03）
        [ThreadStatic] public static int Denoised;
        [ThreadStatic] public static int DirtSeen;

        /// <summary>源贴图自身的 8×8 块状度（块边界梯度 − 块内梯度）。>0.5 视为"疑似 JPEG 来源"。</summary>
        public static unsafe double Blockiness(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            if (w < 32 || h < 32) return 0;
            var bd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                double edge = 0, inner = 0; long ne = 0, ni = 0;
                int step = Math.Max(1, h / 256);
                for (int y = 4; y < h; y += step)
                {
                    byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                    for (int x = 8; x < w; x += 8)
                    {
                        edge += Math.Abs(Lum(row, x) - Lum(row, x - 1)); ne++;
                        if (x + 4 < w) { inner += Math.Abs(Lum(row, x + 4) - Lum(row, x + 3)); ni++; }
                    }
                }
                for (int x = 4; x < w; x += Math.Max(1, w / 256))
                {
                    for (int y = 8; y < h; y += 8)
                    {
                        byte* p1 = (byte*)bd.Scan0 + y * bd.Stride + x * 4;
                        byte* p0 = (byte*)bd.Scan0 + (y - 1) * bd.Stride + x * 4;
                        edge += Math.Abs((p1[0] * 28 + p1[1] * 151 + p1[2] * 77) >> 8
                                       - (p0[0] * 28 + p0[1] * 151 + p0[2] * 77) >> 8); ne++;
                        if (y + 4 < h)
                        {
                            byte* q1 = (byte*)bd.Scan0 + (y + 4) * bd.Stride + x * 4;
                            byte* q0 = (byte*)bd.Scan0 + (y + 3) * bd.Stride + x * 4;
                            inner += Math.Abs((q1[0] * 28 + q1[1] * 151 + q1[2] * 77) >> 8
                                            - (q0[0] * 28 + q0[1] * 151 + q0[2] * 77) >> 8); ni++;
                        }
                    }
                }
                if (ne == 0 || ni == 0) return 0;
                return edge / ne - inner / ni;
            }
            finally { src.UnlockBits(bd); }
        }

        /// <summary>保边降噪（guided filter，自引导）。逐通道处理 BGR，alpha 不动。</summary>
        public static unsafe void Guided(Bitmap bmp, int r, double eps01)
        {
            int w = bmp.Width, h = bmp.Height;
            if (w < 2 * r + 2 || h < 2 * r + 2) return;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int n = w * h;
                var ch = new float[n];
                var mI = new float[n]; var vI = new float[n]; var A = new float[n]; var B = new float[n];
                var t1 = new float[n]; var t2 = new float[n];
                double eps = eps01 * 255.0 * 255.0;
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                        for (int x = 0; x < w; x++) ch[y * w + x] = row[x * 4 + c];
                    }
                    BoxF(ch, t1, w, h, r);
                    Buffer.BlockCopy(t1, 0, mI, 0, n * 4);
                    for (int i = 0; i < n; i++) t2[i] = ch[i] * ch[i];
                    BoxF(t2, t1, w, h, r);
                    for (int i = 0; i < n; i++) vI[i] = Math.Max(t1[i] - mI[i] * mI[i], 0f);
                    for (int i = 0; i < n; i++) { A[i] = (float)(vI[i] / (vI[i] + eps)); B[i] = mI[i] - A[i] * mI[i]; }
                    BoxF(A, t1, w, h, r);
                    BoxF(B, t2, w, h, r);
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            int i = y * w + x;
                            int v = (int)Math.Round(t1[i] * ch[i] + t2[i]);
                            row[x * 4 + c] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
        }

        /// <summary>可分离盒子滤波（滑动窗口，O(n)），结果写入 dst。</summary>
        static void BoxF(float[] src, float[] dst, int w, int h, int r)
        {
            var tmp = new float[src.Length];
            float norm = 1f / (2 * r + 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float sum = 0;
                for (int x = -r; x <= r; x++) sum += src[row + Math.Min(Math.Max(x, 0), w - 1)];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = sum * norm;
                    int add = Math.Min(x + r + 1, w - 1), sub = Math.Max(x - r, 0);
                    sum += src[row + add] - src[row + sub];
                }
            }
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int y = -r; y <= r; y++) sum += tmp[Math.Min(Math.Max(y, 0), h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum * norm;
                    int add = Math.Min(y + r + 1, h - 1), sub = Math.Max(y - r, 0);
                    sum += tmp[add * w + x] - tmp[sub * w + x];
                }
            }
        }
    }
}

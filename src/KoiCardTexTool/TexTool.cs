using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace KoiCardTexTool
{
    /// <summary>一条贴图规则：格式 + 最大边，外加三项**可按部位/按贴图单独指定**的处理方式。
    ///
    /// 三项的"跟随"用哨兵值表示（-1 / 空串），表示"用界面上那套全局设置"：
    ///   SharpenPct  -1 = 跟随全局锐化强度（0 表示这张不锐化）
    ///   SharpenMode "" = 跟随全局锐化方式（cas | edge）
    ///   GrayA       -1 = 跟随全局「灰度用 2 通道」
    /// 这样老预设/老 plan（没这三个字段）读进来就是"全部跟随"，行为与以前完全一致。</summary>
    public sealed class Rule
    {
        public string Format = "keep";   // keep | png | jpeg | auto
        public int Size = 0;             // max edge, 0 = do not rescale
        public int SharpenPct = -1;      // -1 = 跟随全局；0 = 这张不锐化
        public string SharpenMode = "";  // "" = 跟随全局；cas | edge
        public int GrayA = -1;           // -1 = 跟随全局；0 = 关；1 = 开
        /// <summary>放大倍率（v2.33）：-1 = 跟随全局；0 = 不放大；2 = ×2；4 = ×4。
        /// 只对**彩色类**生效（法线/掩罩/高度忽略并记一行日志）。
        /// 放大要受「最大边」和 Resample.UpCeiling 双上限约束：装不下就降级（×4→×2→不放大）。</summary>
        public int Up = -1;
        public Rule() { }
        public Rule(string f, int s) { Format = f; Size = s; }
        public Rule Clone()
        {
            return new Rule(Format, Size) { SharpenPct = SharpenPct, SharpenMode = SharpenMode, GrayA = GrayA, Up = Up };
        }
        /// <summary>四项都是"跟随"吗？（决定要不要写进 JSON）</summary>
        public bool AllFollow { get { return SharpenPct < 0 && string.IsNullOrEmpty(SharpenMode) && GrayA < 0 && Up < 0; } }
    }

    public static class Classes
    {
        public static readonly string[] Order =
        {
            "maintex", "normal", "mask", "height", "emission", "reflection", "matcap", "highcolor", "other"
        };

        public static string Label(string c)
        {
            switch (c)
            {
                case "maintex": return "主贴图 MainTex（衣服主体颜色）";
                case "normal": return "法线贴图 Normal/Bump";
                case "mask": return "遮罩 Mask";
                case "height": return "高度/视差 Parallax/Height";
                case "emission": return "发光 Emission";
                case "reflection": return "反射 Reflection";
                case "matcap": return "材质球 MatCap";
                case "highcolor": return "高光色 HighColor";
                default: return "其它 / 未分类";
            }
        }

        /// <summary>短标签（下拉框用；完整标签见 <see cref="Label"/>）。</summary>
        public static string Short(string c)
        {
            switch (c)
            {
                case "maintex": return "主贴图";
                case "normal": return "法线贴图";
                case "mask": return "遮罩";
                case "height": return "高度/视差";
                case "emission": return "发光";
                case "reflection": return "反射";
                case "matcap": return "材质球";
                case "highcolor": return "高光色";
                default: return "其它/未分类";
            }
        }

        public static string Note(string c)
        {
            switch (c)
            {
                case "maintex": return "JPEG 安全（无 alpha 时），压缩收益最大";
                case "normal": return "JPEG 会产生明暗色带，建议原样或只降分辨率";
                case "mask": return "JPEG 噪声变成阴影条带，建议原样";
                case "height": return "凹凸细节，建议原样";
                case "emission": return "发光图，JPEG 一般可行";
                case "reflection": return "反射图，通常很小";
                case "matcap": return "球面高光图，JPEG 一般可行";
                case "highcolor": return "高光颜色图，JPEG 一般可行";
                default: return "无法判定的贴图，默认不动最安全";
            }
        }

        /// <summary>Classify by shader property name (first rule hit wins) - never by TexID.
        /// 法线/遮罩/高度 优先（怕 JPEG），其余按 MainTex 优先，这样"主贴图"行覆盖所有含 MainTex 的贴图。</summary>
        public static string Classify(List<string> props)
        {
            string[] order = { "mask", "normal", "height", "maintex", "emission", "reflection", "matcap", "highcolor" };
            foreach (var key in order)
            {
                foreach (var p0 in props)
                {
                    string p = (p0 ?? "").ToLowerInvariant();
                    switch (key)
                    {
                        case "mask": if (p.Contains("mask")) return key; break;
                        case "normal": if (p.Contains("normal") || p.Contains("bump")) return key; break;
                        case "height": if (p.Contains("parallax") || p.Contains("height")) return key; break;
                        case "emission": if (p.Contains("emission") || p.Contains("emitt")) return key; break;
                        case "reflection": if (p.Contains("reflection") || p.Contains("reflective")) return key; break;
                        case "matcap": if (p.Contains("matcap")) return key; break;
                        case "highcolor": if (p.Contains("highcolor") || p.Contains("high_color")) return key; break;
                        case "maintex": if (p.Contains("maintex") || p.Contains("main_tex")) return key; break;
                    }
                }
            }
            return "other";
        }

        public static Dictionary<string, Rule> DefaultPlan()
        {
            var plan = new Dictionary<string, Rule>();
            foreach (var c in Order) plan[c] = new Rule("keep", 0);
            plan["maintex"] = new Rule("auto", 1024);
            return plan;
        }

        public static bool IsNoop(Dictionary<string, Rule> plan)
        {
            foreach (var kv in plan) if (kv.Value.Format != "keep") return false;
            return true;
        }
    }

    public sealed class TexStat
    {
        public object Id;
        public string Cls = "", Before = "-", After = "-", Fmt = "", Mode = "", Props = "-";
        public long InBytes, OutBytes;
    }

    /// <summary>一张贴图的处理产出（贴图级并行的单位）。所有原本写进 RepackResult 的计数都先记在这里，
    /// 等串行收尾时再按原顺序合并 —— 这样并行度不影响任何输出。</summary>
    public sealed class TexJob
    {
        public object Key;
        public byte[] Out;
        public TexStat St;
        public readonly List<string> Lines = new List<string>();        // 这一张要打的日志（按原顺序缓冲）
        public readonly List<string> Skipped = new List<string>();
        public readonly List<string> GrayAlphaIds = new List<string>();
        public readonly List<string> UsedTex = new List<string>();
        public int NSame, PngPacked, NKeepOpt, NOwnProtected, NGrayAlpha, PresetMatched, NShaderJpeg, NForcedAlphaLost;
        public long PngSaved, KeepOptSaved, GrayAlphaSaved;
        public double TDecode, TResize, TPng, TJpg, TReopt;
        // v2.33 放大：真的放大了几张 / 被挡下几张（数据类、上限装不下），以及放大过的 TexID（审计用）
        public int NUp, NUpBlocked, NUpBlockedData, NUpBlockedCap;
        public long UpPixelsBefore, UpPixelsAfter;
        public readonly List<string> UpIds = new List<string>();
    }

    public sealed class RepackResult
    {
        public int Rc;
        public int NTex, NJpg, NPng, NKeep, NHdr;
        /// <summary>分阶段耗时（毫秒，仅诊断用；KOITEX_TIME=1 时打印）。</summary>
        public double TDecode, TResize, TPng, TJpg, TReopt, TPack;
        /// <summary>有几张 PNG 换用了自写编码器、一共省了多少字节（无损）。</summary>
        public int PngPacked;
        public long PngSaved;
        /// <summary>有几张改成了灰度/灰度+alpha 通道、省了多少（无损；只对"每个像素 R==G==B"的贴图生效）。</summary>
        public int NGrayAlpha;
        public long GrayAlphaSaved;
        public readonly List<string> GrayAlphaIds = new List<string>();
        /// <summary>真的解码重编码过、但结果没比原字节更小 → 保留原字节的那几张（和"没打算动"的 NKeep 不是一回事）。</summary>
        public int NSame;
        public int NKeepOpt;                 // "原图更小、差点逐字节沿用"但被我们无损优化过的张数
        public int NOwnProtected;            // 因「独占贴图保护」被放宽上限的张数
        public int NShaderJpeg;              // 【测试】被强制转成 JPEG 的张数
        public int NForcedAlphaLost;         // 【测试】其中 alpha 非平坦（会丢 alpha）的张数
        public long KeepOptSaved;            // 这一步省下的字节（像素逐位不变）
        /// <summary>v2.33 放大：真的放大了几张、被挡下几张（哪一类原因），以及放大过的 TexID。</summary>
        public int NUp, NUpBlocked, NUpBlockedData, NUpBlockedCap;
        public long UpPixelsBefore, UpPixelsAfter;
        public readonly List<string> UpIds = new List<string>();
        public long CardBefore, CardAfter, PayloadBefore, PayloadAfter;
        /// <summary>【不匹配/处理不了的卡直接复制】本卡没有压缩，而是原样复制到了输出目录。
        /// 此时 Rc == 0 且 CardAfter == CardBefore（体积不变），但**不算"压缩成功"**，
        /// 汇总里单独记一笔，免得看起来像"压了但没变小"。</summary>
        public bool Copied;
        /// <summary>Rc != 0 时的一句话原因（汇总日志要用），成功时为空。</summary>
        public string Reason = "";
        /// <summary>被跳过的贴图：形如 "TexID 6 (HDR): 原因"。这些贴图原样保留，卡片本身仍算成功。</summary>
        public readonly List<string> Skipped = new List<string>();
        /// <summary>预设命中了几张单贴图规则 / 预设里哪些单贴图规则在本卡没找到对应贴图。</summary>
        public int PresetMatched;
        public readonly List<string> PresetMissed = new List<string>();
        /// <summary>与预设的"同款匹配度"（0~1；没套预设时是 1）。</summary>
        public double OutfitScore = 1.0;
        public readonly List<TexStat> Stats = new List<TexStat>();
        /// <summary>返回码 → 人话。</summary>
        public static string RcText(int rc)
        {
            switch (rc)
            {
                case 0: return "成功";
                case 2: return "不是服装卡（没有 PNG/IEND 结构）";
                case 3: return "卡里没有贴图（没有 MaterialEditor TextureDictionary）";
                case 4: return "卡片结构异常（TextureDictionary 不是预期结构）";
                case 5: return "已取消";
                case 6: return "与预设不是同款服装，已跳过";
            }
            return "未知返回码 " + rc;
        }
    }

    /// <summary>【不匹配/处理不了的卡 → 直接复制】的开关（命令行 `copyskips=1`）。
    /// 打开后，本来会"跳过不处理"的卡（不是卡片 / 结构异常 / 与预设不匹配）不再报错跳过，
    /// 而是把**原文件**复制到输出目录，方便"一批卡里混了几张处理不了的"时输出目录仍然完整。
    /// 复制出来的卡不会被压缩，体积与原来一样。</summary>
    public static class SkipCopy
    {
        public static bool On = false;
        /// <summary>这次运行一共复制了几张（汇总用）。</summary>
        [ThreadStatic] public static int Count;

        /// <summary>把跳过改成"复制过去"。返回 true 表示已复制（调用方直接返回 res）。</summary>
        public static bool TryCopy(string src, string dst, RepackResult res, Action<string> log, string why)
        {
            if (!On) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
                Count++;
                res.Rc = 0; res.Copied = true; res.Reason = "";
                long n = 0; try { n = new FileInfo(src).Length; } catch { }
                res.CardBefore = res.CardAfter = n;
                log(L.F("  [直接复制] {0} —— 原样复制到输出目录（不压缩）", why));
            }
            catch (Exception ex)
            {
                res.Rc = 7; res.Reason = "复制失败：" + ex.Message;
                log("  [X] 复制失败：" + ex.Message);
            }
            return true;
        }
    }

    public sealed class ScanResult
    {
        public int NTex;
        public long Bytes;
        public readonly Dictionary<string, long[]> Classes = new Dictionary<string, long[]>(); // [count, bytes, maxdim]
        public readonly Dictionary<object, List<string>> Props = new Dictionary<object, List<string>>();
    }

    public static class TexTool
    {
        public static string Human(long n)
        {
            double d = n;
            string[] u = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (d >= 1024 && i < 3) { d /= 1024; i++; }
            return i == 0 ? string.Format("{0} {1}", n, u[i]) : string.Format("{0:0.00} {1}", d, u[i]);
        }

        /// <summary>「独占贴图保护」：只被**一个部位**引用的贴图，往往"体积占比小、观感权重高"，
        /// 统一上限会把它砍得最狠。实测 Chronopattern 卡：胸罩独占的法线 4096²→1024² 后边缘锐度
        /// 只剩 42.7%，而同样这张图走无损路径能**零损失省 46%**。
        /// 打开后这类贴图改用 <see cref="ProtectOwnMax"/> 作为放宽上限（0 = 完全不降采样）。
        /// 默认开——花的是"最不该丢的那部分精度"的钱。</summary>
        public static bool ProtectOwn = true;
        public static int ProtectOwnMax = 4096;

        static byte[] ReadAll(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var b = new byte[fs.Length];
                int off = 0;
                while (off < b.Length)
                {
                    int n = fs.Read(b, off, b.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                FullReads++; FullReadBytes += b.Length;
                return b;
            }
        }

        /// <summary>诊断用（v2.24）：整文件读取的次数与字节数 —— 用来定位"读卡慢"到底读了几遍。</summary>
        public static int FullReads;
        public static long FullReadBytes;
        /// <summary>只读文件头部若干字节（判断卡种/读卡名用，不用把 270MB 全读进来）。</summary>
        public static byte[] ReadHead(string path, int maxBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n = (int)Math.Min(maxBytes, fs.Length);
                    var b = new byte[n];
                    int off = 0;
                    while (off < n) { int k = fs.Read(b, off, n - off); if (k <= 0) break; off += k; }
                    if (off < n) Array.Resize(ref b, off);
                    return b;
                }
            }
            catch { return new byte[0]; }
        }

        // ---- 卡片字节缓存：同一张卡在 扫描 / 缩略图预览 / 压缩 之间只读一次 ----
        // （人物卡动辄 230~270 MB，反复读盘会明显变慢；按 路径+修改时间+长度 判定有效性）
        static string cachePath;
        static long cacheTicks, cacheLen;
        static byte[] cacheBytes;

        public static byte[] ReadAllCached(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (cacheBytes != null && path == cachePath && fi.LastWriteTimeUtc.Ticks == cacheTicks && fi.Length == cacheLen)
                    return cacheBytes;
                var b = File.ReadAllBytes(path);
                FullReads++; FullReadBytes += b.Length;
                if (b.Length <= 400L * 1024 * 1024)          // 只缓存 400MB 以内，别把内存吃光
                {
                    cachePath = path; cacheTicks = fi.LastWriteTimeUtc.Ticks; cacheLen = fi.Length; cacheBytes = b;
                }
                else { cacheBytes = null; }
                return b;
            }
            catch { return ReadAll(path); }
        }

        /// <summary>取一张贴图的原始字节（给缩略图预览用）。返回 null 表示没有这张/读不了。</summary>
        public static byte[] TextureBytes(string path, object texId)
        {
            try
            {
                byte[] b = ReadAllCached(path);
                return TextureBytes(b, texId);
            }
            catch { return null; }
        }

        /// <summary>同上，但直接用在内存里的卡字节 —— 界面刚读完卡时**不要**再按路径读一遍。</summary>
        public static byte[] TextureBytes(byte[] b, object texId)
        {
            try
            {
                if (b == null || b.Length == 0) return null;
                int kpos = Card.FindKeyCached(b, "TextureDictionary");
                if (kpos < 0) return null;
                var bin = MP.Read(b, ref kpos) as BinRef;
                if (bin == null) return null;
                int tp = bin.Off;
                var dic = MP.Read(b, ref tp) as MMap;
                if (dic == null) return null;
                for (int i = 0; i < dic.Keys.Count; i++)
                {
                    object k = dic.Keys[i];
                    if (k == null || texId == null) continue;
                    if (k.ToString() != texId.ToString()) continue;
                    var v = dic.Vals[i] as BinRef;
                    return v == null ? null : v.Bytes();
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>给预览用：把贴图字节解成 Image（PNG/JPEG 走 GDI+，HDR 走自己的解码器 + 色调映射）。</summary>
        public static Image LoadTextureImage(byte[] raw, out string err)
        {
            err = "";
            if (raw == null || raw.Length == 0) { err = "取不到贴图字节"; return null; }
            var fmt = Sniff(raw);
            try
            {
                if (fmt == TexFmt.Hdr) return HdrToBitmap(Hdr.Decode(raw));
                if (fmt == TexFmt.Png || fmt == TexFmt.Jpeg)
                {
                    var ms = new MemoryStream(raw, false);
                    return Image.FromStream(ms);
                }
                err = "这个格式（" + FmtName(fmt) + "）本工具不预览";
                return null;
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        /// <summary>HDR 是线性浮点，不能当普通图看：x/(1+x) 色调映射后再转 sRGB 画成 8 位位图。</summary>
        public static Image HdrToBitmap(Hdr.Image h)
        {
            var bmp = new Bitmap(h.W, h.H, PixelFormat.Format24bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, h.W, h.H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h.H; y++)
                    {
                        byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                        for (int x = 0; x < h.W; x++)
                        {
                            int si = (y * h.W + x) * 3;
                            for (int c = 0; c < 3; c++)
                            {
                                float v = h.Rgb[si + c];
                                if (v < 0f) v = 0f;
                                float t = v / (1f + v);
                                int b8 = (int)(Math.Pow(t, 1.0 / 2.2) * 255f + 0.5f);
                                if (b8 < 0) b8 = 0;
                                if (b8 > 255) b8 = 255;
                                row[x * 3 + (2 - c)] = (byte)b8;      // GDI+ 位图是 BGR
                            }
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        public static ScanResult Scan(string path)
        {
            byte[] b = ReadAll(path);
            int end = Card.PngEnd(b);
            if (end < 0) return null;
            int kpos = Card.FindKeyCached(b, "TextureDictionary");
            if (kpos < 0) return new ScanResult { NTex = 0 };
            object holder = MP.Read(b, ref kpos);
            var br = holder as BinRef;
            if (br == null) return null;
            int p = br.Off;
            var dic = MP.Read(b, ref p) as MMap;
            if (dic == null) return null;
            var props = Card.TextureProperties(b);
            var res = new ScanResult();
            for (int i = 0; i < dic.Keys.Count; i++)
            {
                var v = dic.Vals[i] as BinRef;
                if (v == null) continue;
                List<string> pl;
                if (!props.TryGetValue(dic.Keys[i], out pl)) pl = new List<string>();
                string cls = Classes.Classify(pl);
                int dim = DimOf(v.Bytes());
                long[] a;
                if (!res.Classes.TryGetValue(cls, out a)) { a = new long[3]; res.Classes[cls] = a; }
                a[0]++; a[1] += v.Len; if (dim > a[2]) a[2] = dim;
                res.Bytes += v.Len;
                res.NTex++;
            }
            return res;
        }

        // ---------------------------------------------------------------- 格式识别
        // 衣服卡里的贴图不一定是 PNG：实测有 Radiance HDR（lilToon 的 ReflectionCubeTex，
        // 4096x2160 展开环境图）。GDI+ 解不了 HDR，照着"一定是 PNG/JPEG"去解码会
        // 直接抛 ArgumentException: Parameter is not valid. 并废掉整张卡。

        public enum TexFmt { Png, Jpeg, Hdr, Exr, Unknown }

        public static TexFmt Sniff(byte[] raw)
        {
            if (raw == null || raw.Length < 4) return TexFmt.Unknown;
            if (raw.Length > 8 && raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47) return TexFmt.Png;
            if (raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF) return TexFmt.Jpeg;
            if (Hdr.IsHdr(raw)) return TexFmt.Hdr;
            if (raw[0] == 0x76 && raw[1] == 0x2F && raw[2] == 0x31 && raw[3] == 0x01) return TexFmt.Exr;
            return TexFmt.Unknown;
        }

        public static string FmtName(TexFmt f)
        {
            switch (f)
            {
                case TexFmt.Png: return "PNG";
                case TexFmt.Jpeg: return "JPEG";
                case TexFmt.Hdr: return "HDR";
                case TexFmt.Exr: return "EXR";
            }
            return "未知格式";
        }

        /// <summary>最长边（拿不到返回 0）。HDR 读头部分辨率行，PNG/JPEG 用 GDI+。</summary>
        public static int DimOf(byte[] raw)
        {
            var f = Sniff(raw);
            if (f == TexFmt.Hdr)
            {
                try { int w, h; return Hdr.PeekSize(raw, out w, out h) ? Math.Max(w, h) : 0; }
                catch { return 0; }
            }
            if (f != TexFmt.Png && f != TexFmt.Jpeg) return 0;
            try { using (var im = Image.FromStream(new MemoryStream(raw, false), false, false)) return Math.Max(im.Width, im.Height); }
            catch { return 0; }
        }

        // ---------------------------------------------------------------- 按部位扫描
        // 给「按部位压缩」分页用：一张卡里每个部位有哪些贴图、每张贴图多大、
        // 以及每张贴图还被哪些别的部位共用（共用的只能有一种处理方式）。

        public sealed class PartTex
        {
            public object Id;
            public string Cls = "", Props = "-", Fmt = "";
            public int Dim;                 // 最长边
            public long Bytes;
            public List<int> SlotKeys = new List<int>();
            public string SlotNames = "";
            public List<string> Sigs = new List<string>();     // 绑定签名（预设匹配/导出用）
            public bool Shared { get { return SlotKeys.Count > 1; } }
        }

        public sealed class PartInfo
        {
            public int SlotKey;
            public string Name = "";
            public List<PartTex> Tex = new List<PartTex>();
            public long Bytes;              // 该部位涉及的全部贴图字节（含共享）
            public long OwnBytes;           // 只属于这个部位的
            public int OwnCount;
            public List<string> Materials = new List<string>();
            public string MatText { get { return Materials.Count == 0 ? "-" : string.Join(",", Materials.ToArray()); } }
        }

        public sealed class PartsScan
        {
            public List<PartInfo> Parts = new List<PartInfo>();
            public List<PartTex> All = new List<PartTex>();
            public List<PartTex> Orphans = new List<PartTex>();     // 没有任何部位引用
            public int NTex;
            public long Bytes;
            public string Err = "";
            /// <summary>这次扫描读进来的原始字节（= 整张卡）。调用方要用（读卡名/卡种/预览）
            /// 就直接用它，**别再 ReadAll 一遍** —— 人物卡 270MB，多读一次就是几百毫秒到几秒。</summary>
            public byte[] Raw;
            public PartInfo Find(int slotKey)
            {
                foreach (var p in Parts) if (p.SlotKey == slotKey) return p;
                return null;
            }
        }

        public static PartsScan ScanParts(string path)
        {
            var res = ScanPartsCore(path);
            ScanCachePut(path, res);
            return res;
        }

        // ---- 扫描结果缓存（v2.25）：同一张卡再读一次就不用重扫 ----------------------
        // 只留最近 2 张：够覆盖"两页之间来回切"，又不会把内存吃满。
        // 为了省内存，**只有最新那条保留 Raw**（Raw = 整张卡字节，270MB 级），
        // 旧条目仍可用（表格、规则都在），只是预览会退回按路径读一次。
        sealed class ScanEntry { public string Key; public PartsScan Scan; }
        static readonly List<ScanEntry> scanCache = new List<ScanEntry>();
        static readonly object scanCacheLock = new object();

        static string ScanKey(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
            }
            catch { return path; }
        }

        /// <summary>命中缓存就返回上次的扫描结果（按 路径+修改时间+大小 判有效）。</summary>
        public static PartsScan ScanPartsCached(string path)
        {
            string key = ScanKey(path);
            lock (scanCacheLock)
            {
                for (int i = 0; i < scanCache.Count; i++)
                    if (scanCache[i].Key == key && scanCache[i].Scan != null && scanCache[i].Scan.Err.Length == 0)
                    {
                        var e = scanCache[i];
                        scanCache.RemoveAt(i);
                        scanCache.Insert(0, e);
                        CacheHits++;
                        return e.Scan;
                    }
            }
            CacheMiss++;
            var s = ScanParts(path);                    // 内部会放进缓存
            return s;
        }

        static void ScanCachePut(string path, PartsScan s)
        {
            if (s == null || s.Err.Length > 0) return;
            string key = ScanKey(path);
            lock (scanCacheLock)
            {
                for (int i = 0; i < scanCache.Count; i++) if (scanCache[i].Key == key) scanCache.RemoveAt(i);
                scanCache.Insert(0, new ScanEntry { Key = key, Scan = s });
                while (scanCache.Count > 2) scanCache.RemoveAt(scanCache.Count - 1);
                for (int i = 1; i < scanCache.Count; i++) if (scanCache[i].Scan != null) scanCache[i].Scan.Raw = null;
            }
        }

        /// <summary>诊断：扫描缓存命中 / 未命中次数。</summary>
        public static int CacheHits, CacheMiss;

        static PartsScan ScanPartsCore(string path)
        {
            var res = new PartsScan();
            byte[] b;
            var swAll = Stopwatch.StartNew();          // 诊断：KOITEX_SCANTIME=1 时打印分阶段耗时
            int c0 = Card.KeyScanCount; long m0 = Card.KeyScanMs;
            try { b = ReadAll(path); }
            catch (Exception ex) { res.Err = ex.Message; return res; }
            long tRead = swAll.ElapsedMilliseconds;
            res.Raw = b;                       // 调用方要卡名/卡种时直接用它，别再读一遍
            res.Bytes = b.Length;
            int end = Card.PngEnd(b);
            if (end < 0) { res.Err = "不是服装卡（没有 PNG/IEND 结构）"; return res; }

            int kpos = Card.FindKeyCached(b, "TextureDictionary");
            if (kpos < 0) { res.Err = "卡里没有贴图（没有 MaterialEditor TextureDictionary）"; return res; }
            var dic = MP.Read(b, ref kpos) as BinRef;
            if (dic == null) { res.Err = "TextureDictionary 结构异常"; return res; }
            int dp = dic.Off;
            var texmap = MP.Read(b, ref dp) as MMap;
            if (texmap == null) { res.Err = "TextureDictionary 不是 map"; return res; }
            long tDict = swAll.ElapsedMilliseconds;

            bool chara = Chara.IsCharaCard(b);
            long tChara = swAll.ElapsedMilliseconds;
            var props = Card.TextureProperties(b);
            long tProps = swAll.ElapsedMilliseconds;
            var binds = chara ? null : Card.SlotBindings(b);
            long tBinds = swAll.ElapsedMilliseconds;
            var sigs = SigMap(b, chara);
            long tSigs = swAll.ElapsedMilliseconds;
            // 贴图 → 键；键 → 贴图 / 键 → 材质
            var texKeys = TexKeys(b, chara);
            long tKeys = swAll.ElapsedMilliseconds;
            var chBinds = chara ? Chara.Bindings(b) : null;
            long tChBinds = swAll.ElapsedMilliseconds;

            var texSlots = new Dictionary<object, List<int>>();
            var slotTex = new Dictionary<int, List<object>>();
            var slotMat = new Dictionary<int, List<string>>();
            if (chara)
            {
                foreach (var bd in chBinds)
                {
                    List<object> tl;
                    if (!slotTex.TryGetValue(bd.Key, out tl)) { tl = new List<object>(); slotTex[bd.Key] = tl; }
                    if (!tl.Contains(bd.TexId)) tl.Add(bd.TexId);
                    List<string> ml;
                    if (!slotMat.TryGetValue(bd.Key, out ml)) { ml = new List<string>(); slotMat[bd.Key] = ml; }
                    if (bd.Material.Length > 0 && !ml.Contains(bd.Material)) ml.Add(bd.Material);
                }
                texSlots = texKeys;
            }
            else
            {
                foreach (var bd in binds)
                {
                    List<int> sl;
                    if (!texSlots.TryGetValue(bd.TexId, out sl)) { sl = new List<int>(); texSlots[bd.TexId] = sl; }
                    if (!sl.Contains(bd.SlotKey)) sl.Add(bd.SlotKey);
                    List<object> tl;
                    if (!slotTex.TryGetValue(bd.SlotKey, out tl)) { tl = new List<object>(); slotTex[bd.SlotKey] = tl; }
                    if (!tl.Contains(bd.TexId)) tl.Add(bd.TexId);
                    List<string> ml;
                    if (!slotMat.TryGetValue(bd.SlotKey, out ml)) { ml = new List<string>(); slotMat[bd.SlotKey] = ml; }
                    if (bd.Material.Length > 0 && !ml.Contains(bd.Material)) ml.Add(bd.Material);
                }
            }

            // 每张贴图的公共信息
            var info = new Dictionary<object, PartTex>();
            for (int i = 0; i < texmap.Keys.Count; i++)
            {
                var v = texmap.Vals[i] as BinRef;
                if (v == null) continue;
                var tid = texmap.Keys[i];
                List<string> pl;
                if (!props.TryGetValue(tid, out pl)) pl = new List<string>();
                byte[] raw = v.Bytes();
                var t = new PartTex
                {
                    Id = tid, Cls = Classes.Classify(pl), Props = Card.PropsToString(pl),
                    Fmt = FmtName(Sniff(raw)), Dim = DimOf(raw), Bytes = v.Len
                };
                if (texSlots.ContainsKey(tid)) t.SlotKeys = texSlots[tid];
                List<string> sg;
                if (sigs.TryGetValue(tid, out sg)) t.Sigs = sg;
                var names = new List<string>();
                foreach (var sk in t.SlotKeys) names.Add(KName(sk));
                t.SlotNames = names.Count == 0 ? "（无部位引用）" : string.Join(" + ", names.ToArray());
                info[tid] = t;
                res.All.Add(t);
                res.NTex++;
                if (t.SlotKeys.Count == 0) res.Orphans.Add(t);
            }

            // 部位表：卡片里出现过的槽位（按槽位号排序）
            var keys = new List<int>(slotTex.Keys);
            keys.Sort();
            foreach (var sk in keys)
            {
                var pi = new PartInfo { SlotKey = sk, Name = KName(sk) };
                List<string> ml;
                if (slotMat.TryGetValue(sk, out ml)) { ml.Sort(StringComparer.Ordinal); pi.Materials = ml; }
                foreach (var tid in slotTex[sk])
                {
                    PartTex t;
                    if (!info.TryGetValue(tid, out t)) continue;
                    pi.Tex.Add(t);
                    pi.Bytes += t.Bytes;
                    if (!t.Shared) { pi.OwnBytes += t.Bytes; pi.OwnCount++; }
                }
                pi.Tex.Sort(delegate (PartTex x, PartTex y) { return y.Bytes.CompareTo(x.Bytes); });
                res.Parts.Add(pi);
            }
            // 没用到的部位也补一行：衣服卡补 9 个服装槽；人物卡补「角色本体 + 7 套换装」表头
            if (chara)
            {
                for (int g = 0; g <= Chara.CoordNames.Length; g++)
                {
                    int hk = Chara.PackKey(g, 0);
                    if (!slotTex.ContainsKey(hk))
                        res.Parts.Add(new PartInfo { SlotKey = hk, Name = Chara.GroupName(g) });
                }
            }
            else
            {
                for (int sn = 0; sn < Card.ClothesSlotNames.Length; sn++)
                {
                    int sk = Card.SlotKey(Card.ObjClothes, sn);
                    if (!slotTex.ContainsKey(sk))
                        res.Parts.Add(new PartInfo { SlotKey = sk, Name = Card.SlotName(sk) });
                }
            }
            res.Parts.Sort(delegate (PartInfo x, PartInfo y)
            {
                if (x.Tex.Count == 0 && y.Tex.Count > 0) return 1;
                if (y.Tex.Count == 0 && x.Tex.Count > 0) return -1;
                return x.SlotKey.CompareTo(y.SlotKey);
            });
            if (Environment.GetEnvironmentVariable("KOITEX_SCANTIME") == "1")
            {
                Console.WriteLine(L.F("  [读卡耗时] 文件 {0:F1} MB | 读盘 {1}ms | 解析字典 {2}ms | 判定卡种 {3}ms | " +
                    "属性表 {4}ms | 部位绑定 {5}ms | 签名 {6}ms | 贴图键 {7}ms | 人物卡绑定 {8}ms | " +
                    "收尾 {9}ms || 整文件键扫描 {10} 次 / {11}ms",
                    b.Length / 1048576.0, tRead, tDict - tRead, tChara - tDict,
                    tProps - tChara, tBinds - tProps, tSigs - tBinds, tKeys - tSigs, tChBinds - tKeys,
                    swAll.ElapsedMilliseconds - tChBinds,
                    Card.KeyScanCount - c0, Card.KeyScanMs - m0));
            }
            return res;
        }


        /// <summary>读卡耗时（诊断/自检用）：跑一遍 ScanParts 并回报各阶段毫秒。</summary>
        public static string ScanPartsTimed(string path)
        {
            string old = Environment.GetEnvironmentVariable("KOITEX_SCANTIME");
            int c0 = Card.KeyScanCount; long m0 = Card.KeyScanMs;
            var sw = Stopwatch.StartNew();
            var r = ScanParts(path);
            sw.Stop();
            return L.F("{0}：共 {1}ms（读盘+解析），整文件键扫描 {2} 次 / {3}ms，{4} 个部位 / {5} 张贴图{6}",
                System.IO.Path.GetFileName(path), sw.ElapsedMilliseconds,
                Card.KeyScanCount - c0, Card.KeyScanMs - m0, r.Parts.Count, r.NTex,
                r.Err.Length > 0 ? "  错误：" + r.Err : "");
        }

        /// <summary>诊断用：读一次卡并回报耗时（不打印到控制台）。</summary>
        public static string ScanPartsDetail(string path)
        {
            int c0 = Card.KeyScanCount; long m0 = Card.KeyScanMs;
            var sw = Stopwatch.StartNew();
            var r = ScanParts(path);
            sw.Stop();
            return L.F("总 {0}ms（其中整文件键扫描 {1} 次 / {2}ms）",
                sw.ElapsedMilliseconds, Card.KeyScanCount - c0, Card.KeyScanMs - m0);
        }

        /// <summary>按部位压缩时，把各部位对同一张贴图的要求合并成一条规则。
        /// 规则：拥有它的部位必须**全部**启用（有一个没启用/选了「原样不动」就整张不动），
        /// 合并时格式取最保守（PNG < 自动 < JPEG）、尺寸取最大（更保外观）。
        /// 返回 false 表示这张贴图不该动，why 说明原因。</summary>
        public static bool MergeSlotRules(List<int> owners, Dictionary<int, Rule> slotRules,
                                          out Rule merged, out string why)
        {
            merged = null; why = "";
            if (owners == null || owners.Count == 0) { why = "没有任何部位引用它"; return false; }
            var off = new List<string>();
            Rule best = null;
            foreach (var sk in owners)
            {
                Rule r;
                if (slotRules == null || !slotRules.TryGetValue(sk, out r) || r == null)
                { off.Add(KName(sk) + "(未启用)"); continue; }
                if (r.Format == "keep") { off.Add(KName(sk) + "(原样不动)"); continue; }
                if (best == null) best = r.Clone();
                else
                {
                    string f = Rank(r.Format) < Rank(best.Format) ? r.Format : best.Format;
                    int sz = Math.Max(best.Size, r.Size);
                    best = new Rule(f, sz)
                    {
                        // 锐化强度：取**最小**（最保守，谁也不被别人的强锐化带上）；
                        // 只要有一个部位没指定（-1），结果就是 -1（跟随全局）。
                        // 但"显式 0"是有意义的（这张不锐化），所以 -1 单独处理。
                        SharpenPct = MergePct(best.SharpenPct, r.SharpenPct),
                        SharpenMode = best.SharpenMode == r.SharpenMode ? best.SharpenMode : "",
                        GrayA = best.GrayA == r.GrayA ? best.GrayA : -1
                    };
                }
            }
            if (off.Count > 0) { why = "被 " + string.Join("、", off.ToArray()) + " 共用，那些部位选择不动它"; return false; }
            merged = best;
            return best != null;
        }

        /// <summary>锐化强度合并：任一为 -1（跟随）→ 跟随；否则取较小者（保守）。
        /// 0 是有效值（明确"这张不锐化"），不能被当成"没设"。</summary>
        static int MergePct(int a, int b)
        {
            if (a < 0 || b < 0) return -1;
            return Math.Min(a, b);
        }

        /// <summary>格式的"保守程度"，数字越小越保守（越不容易掉画质）。</summary>
        static int Rank(string fmt)
        {
            if (fmt == "keep") return 0;
            if (fmt == "png") return 1;
            if (fmt == "auto") return 2;
            if (fmt == "jpeg" || fmt == "jpg") return 3;
            return 2;
        }

        /// <summary>槽位 → 名字（日志用）。人物卡的键（≥100000）会带上组名。</summary>
        public static string KName(int key)
        {
            return Chara.IsCharaKey(key) ? Chara.KeyName(key) : Card.SlotName(key);
        }

        /// <summary>槽位 → 名字（日志用）。</summary>
        static List<string> OwnerNames(List<int> keys)
        {
            var l = new List<string>();
            if (keys == null) return l;
            foreach (var k in keys) l.Add(KName(k));
            return l;
        }

        /// <summary>贴图 → 拥有它的键列表。衣服卡用「槽位键」，人物卡用「组·部位键」。</summary>
        public static Dictionary<object, List<int>> TexKeys(byte[] b, bool chara)
        {
            var d = new Dictionary<object, List<int>>();
            if (chara)
            {
                foreach (var bd in Chara.Bindings(b))
                {
                    List<int> l;
                    if (!d.TryGetValue(bd.TexId, out l)) { l = new List<int>(); d[bd.TexId] = l; }
                    if (!l.Contains(bd.Key)) l.Add(bd.Key);
                }
                return d;
            }
            foreach (var bd in Card.SlotBindings(b))
            {
                List<int> l;
                if (!d.TryGetValue(bd.TexId, out l)) { l = new List<int>(); d[bd.TexId] = l; }
                if (!l.Contains(bd.SlotKey)) l.Add(bd.SlotKey);
            }
            return d;
        }

        /// <summary>每张贴图的绑定签名集合（人物卡按「组·部位|材质|属性」）。</summary>
        public static Dictionary<object, List<string>> SigMap(byte[] b, bool chara)
        {
            if (!chara) return TextureSigs(b);
            var d = new Dictionary<object, List<string>>();
            foreach (var bd in Chara.Bindings(b))
            {
                List<string> l;
                if (!d.TryGetValue(bd.TexId, out l)) { l = new List<string>(); d[bd.TexId] = l; }
                string s = Chara.KeyName(bd.Key) + "|" + NormMaterial(bd.Material) + "|" + (bd.Property ?? "");
                if (!l.Contains(s)) l.Add(s);
                if (bd.ObjType >= 3)                       // 本体贴图再加一条不带组名的签名，便于跨卡匹配
                {
                    string s2 = Chara.BodySig(bd);
                    if (!l.Contains(s2)) l.Add(s2);
                }
            }
            foreach (var k in new List<object>(d.Keys)) d[k].Sort(StringComparer.Ordinal);
            return d;
        }

        // ---------------------------------------------------------------- 预设（可导出、可套用到同款服装）
        // 「同款服装、只有贴图不同」的卡之间，TexID 会平移、材质会多出 .MECopy1 副本，
        // 但「哪个部位、哪个材质、哪个属性用哪张贴图」这套绑定关系是一致的 → 用它当跨卡匹配键。
        // 实测三张 KKCoordeF 参考卡：卡1↔卡3 有 78 个 TexID 绑定完全一致；
        // 卡1↔卡2（早期版本）TexID 集合都不同、53 个共有里 14 个绑定不同，
        // 但按「部位|材质|属性」签名匹配仍有 59 个能对上，且签名无重复。

        /// <summary>材质名归一化：去掉 MaterialEditor 的 ".MECopyN" 副本后缀（同款服装不同版本的主要差异）。</summary>
        public static string NormMaterial(string m)
        {
            if (string.IsNullOrEmpty(m)) return "";
            int i = m.LastIndexOf(".MECopy", StringComparison.OrdinalIgnoreCase);
            if (i <= 0) return m;
            for (int k = i + 7; k < m.Length; k++)
                if (m[k] < '0' || m[k] > '9') return m;          // .MECopy 后面不是纯数字 → 不是副本后缀
            return m.Substring(0, i);
        }

        /// <summary>每张贴图被哪些 shader 用到（读 MaterialShaderList + 贴图绑定关联起来）。
        /// key = TexID；值 = 用到它的所有 shader 名（去重）。缺数据时返回空表。</summary>
        public static Dictionary<object, List<string>> TexShaders(byte[] b)
        {
            var d = new Dictionary<object, List<string>>();
            var mats = Card.MaterialShaders(b);
            if (mats.Count == 0) return d;
            var byKey = new Dictionary<string, string>();
            foreach (var m in mats)
            {
                // 同一 (槽位,材质名) 可能有多条（不同 RenderQueue），取第一条即可
                string k = m.SlotKey + "|" + NormMaterial(m.Material);
                if (!byKey.ContainsKey(k)) byKey[k] = m.Shader;
            }
            foreach (var bd in Card.SlotBindings(b))
            {
                string sh;
                if (!byKey.TryGetValue(bd.SlotKey + "|" + NormMaterial(bd.Material), out sh)) continue;
                if (string.IsNullOrEmpty(sh)) continue;
                List<string> l;
                if (!d.TryGetValue(bd.TexId, out l)) { l = new List<string>(); d[bd.TexId] = l; }
                if (!l.Contains(sh)) l.Add(sh);
            }
            foreach (var k in new List<object>(d.Keys)) d[k].Sort(StringComparer.Ordinal);
            return d;
        }

        /// <summary>【测试用】按**贴图类型**强制转 JPEG 的类型集合（命令行 `forcejpg=matcap` 或 `forcejpg=matcap,reflection`）。
        ///
        /// 和上面那条 `NonalphaJpeg`（已被证伪的 shader 名规则）不同，这里用的是**类型**，
        /// 而且是从用户的游戏内实测出发的：matcap 转 JPEG 观感基本无影响（已实测）。
        ///
        /// ⚠ 依然要注意：**JPEG 没有 alpha 通道**。强制转 JPEG 会把 alpha 丢掉 ——
        /// 这正是上一轮 black Lunalice 凭空多出反光的原因（那条规则误伤了 5 张带真 alpha 的贴图）。
        /// 所以这里会额外统计"被强制转、但 alpha 非平坦"的张数（NForcedAlphaLost），
        /// 供判断风险面。</summary>
        public static HashSet<string> ForceJpgClasses = new HashSet<string>();

        /// <summary>【内建判据，v2.19 起默认开】matcap 类：只要"圆内几乎没有非不透明像素"，
        /// 就允许用 JPEG（命令行 `matcapjpg=0` 可关，仅供对照实验）。
        ///
        /// 依据是**游戏内实测**：matcap 转 JPEG 观感基本无影响（用户已验），
        /// 以及 alpha 的解剖 —— 实测 8 张可安全放行的 matcap，它们的非 255 像素
        /// 要么只有 JPEG 往返的 254/253 抖动，要么集中在**内切圆之外**（圆边掩罩，
        /// 而标准 matcap UV 只采样内切圆 → 那些像素永远取不到）。
        ///
        /// ⚠ 严格到"圆内一个非 255 都没有"是**做不到**的：实测 9 张里全部都在圆内
        /// 有 28~16909 个非 255 像素（圆边掩罩的抗锯齿环 + 抖动），收益直接归零。
        /// 所以保留 0.5% 的容差，阈值取 a&lt;250（比 a&lt;200 更严，能多拦住"整体偏暗的掩罩"）。
        ///
        /// ⚠⚠ **不要把这个例外推广到别的类**。实测教训：`KKCoordeF_20250607174302180`
        /// 的 maintex alpha **恒等于 230**（一个像素都不例外，230/255 = 0.902），
        /// 当时按"alpha 里没有 a&lt;200 的像素 = 没有数据"放行 → 游戏内**大片半透明布料变成不透明**。
        /// 结论：**非 255 的 alpha 只要落在会被采样到的区域，就可能是真的不透明度**。</summary>
        public static bool MatcapCircleJpeg = true;

        /// <summary>【仅测试】给"按类型强制转 JPEG"（`forcejpg=`）加的 alpha 闸门：
        /// 0=不加（只统计风险）、1=只转 alpha 恒为 255 的、2=matcap 圆掩罩安全规则。命令行 `forcejpgsafe=0|1|2`。
        ///
        /// 存在的意义是**分离收益来源**：`auto` 本来就是「alpha 平坦 → JPEG」，
        /// 所以 alpha 平坦的 matcap 在基线里**早就是 JPEG 了**，强制开关对它们毫无作用。
        /// 实测：加闸门 1 之后收益**归零**（1,770,042 → 0）→
        /// 「matcap 转 JPEG 省的这点空间，全部是丢掉在用 alpha 换来的」（那个 0.49% 的版本已废弃）。
        /// 闸门 2（圆掩罩）才是留下 0.42% 的那条，已提升为内建判据（见 MatcapCircleJpeg）。</summary>
        public static int ForceJpgAlphaLevel = 0;

        /// <summary>【测试用·已被证伪，留作反面教材】把"材质用的 shader 名里没有 alpha"的贴图强制转 JPEG。
        /// 默认关；命令行 `nonalphajpeg=1` 打开。
        ///
        /// ⚠ **这条规则已被实测证伪**（2.19 起加了第二道闸门）：
        ///   1) `Goo` / `KKUTS` 这些名字里没有 "alpha" 的 mod 着色器**照样会用 alpha 通道**；
        ///   2) 连游戏自带的 `Shader Forge/main_opaque` 也用 —— 它的定义里写着
        ///      `_Cutoff ("Alpha cutoff", Range(0,1)) = 0.5`、`QUEUE = AlphaTest`、
        ///      `Blend One OneMinusSrcAlpha`。"opaque" 只是"不做半透明排序"，**不是"不用 alpha"**；
        ///   3) 实测 black Lunalice 11 ME-PRO：被这条规则转成 JPEG 的 6 张贴图**全部**带真 alpha，
        ///      丢掉 alpha 之后材质上**凭空多出反光**（alpha 掩罩失效 → shader 读到 1.0）。
        ///
        /// 因此加了第二道闸门：**只对 alpha 全为 255 的贴图**强制 JPEG（那种情况没什么可丢）。
        /// 判定：这张贴图用到的所有 shader 名都不含 "alpha" **且** alpha 平坦。</summary>
        public static bool NonalphaJpeg = false;

        /// <summary>该贴图是否"完全不含 alpha 着色器"。texShaders 为空（读不到 shader）时返回 false（保守）。</summary>
        static bool AllShadersNonAlpha(Dictionary<object, List<string>> texShaders, object key)
        {
            List<string> names;
            if (texShaders == null || !texShaders.TryGetValue(key, out names) || names.Count == 0) return false;
            foreach (var n in names)
                if (n != null && n.ToLowerInvariant().Contains("alpha")) return false;
            return true;
        }

        /// <summary>一条绑定的签名：部位|材质(去副本后缀)|属性。</summary>
        public static string BindingSig(int slotKey, string material, string prop)
        {
            return Card.SlotKey2Text(slotKey) + "|" + NormMaterial(material) + "|" + (prop ?? "");
        }

        /// <summary>每张贴图的绑定签名集合（去重排序）。</summary>
        public static Dictionary<object, List<string>> TextureSigs(byte[] b)
        {
            var d = new Dictionary<object, List<string>>();
            foreach (var bd in Card.SlotBindings(b))
            {
                List<string> l;
                if (!d.TryGetValue(bd.TexId, out l)) { l = new List<string>(); d[bd.TexId] = l; }
                string s = BindingSig(bd.SlotKey, bd.Material, bd.Property);
                if (!l.Contains(s)) l.Add(s);
            }
            foreach (var k in new List<object>(d.Keys)) d[k].Sort(StringComparer.Ordinal);
            return d;
        }

        /// <summary>预设里的一张单贴图规则。</summary>
        public sealed class PresetTex
        {
            public string Id = "";
            public List<string> Sigs = new List<string>();
            public Rule Rule;
        }

        /// <summary>导出/套用的预设：部位规则 + 单张贴图规则 + 通用设置。</summary>
        public sealed class Preset
        {
            public string Source = "";
            public Dictionary<int, Rule> Parts = new Dictionary<int, Rule>();
            public List<PresetTex> Textures = new List<PresetTex>();

            // ---- 通用设置：**只用于不匹配的服装**（勾了强制时）----
            /// <summary>通用规则：与预设不是同款服装、又勾了「不匹配服装使用通用设置压缩」时，
            /// 整卡按它压。同款卡永远用部位/单张贴图规则，通用设置不参与。</summary>
            public Rule Uniform;
            /// <summary>运行时开关（不写进 JSON）：不匹配的服装是否用通用设置压。</summary>
            public bool ForceUniform;
            /// <summary>整卡的绑定签名指纹（同款检测用）。只写部位规则、没有单张贴图规则时，
            /// 单靠 Textures 里那几条签名算不出匹配度，必须单独存一份整卡指纹。</summary>
            public List<string> SigSet = new List<string>();
            /// <summary>预设来源是人物卡还是衣服卡。人物卡的「组·部位」规则与具体穿什么衣服无关，
            /// 所以人物卡预设套到任何人物卡上直接放行；只有种类不同（人物卡↔衣服卡）才判不匹配。</summary>
            public bool IsChara;
            /// <summary>判定"同款服装"的最低匹配度（写进 JSON，可手改）。</summary>
            public double MinMatch = 0.5;

            /// <summary>找出套用到某张贴图上的全部预设规则（可能多条 → 由调用方按保守规则合并）。</summary>
            public List<Rule> Match(object id, List<string> sigs)
            {
                var res = new List<Rule>();
                string ids = id == null ? "" : id.ToString();
                foreach (var t in Textures)
                {
                    if (t.Rule == null) continue;
                    bool hit = false;
                    if (t.Id == ids && (sigs == null || sigs.Count == 0 || t.Sigs.Count == 0))
                        hit = true;                                 // 没有签名信息时只能按编号认
                    if (!hit && sigs != null)
                        foreach (var s in t.Sigs)
                            if (sigs.Contains(s)) { hit = true; break; }   // 绑定级命中（跨版本也能对上）
                    if (hit) res.Add(t.Rule);
                }
                return res;
            }

            /// <summary>预设涉及的全部绑定签名：优先用整卡指纹，没有就退回单张贴图规则里的签名。</summary>
            public HashSet<string> AllSigs()
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                if (SigSet.Count > 0) foreach (var s in SigSet) set.Add(s);
                else foreach (var t in Textures) foreach (var s in t.Sigs) set.Add(s);
                return set;
            }

            /// <summary>目标卡片的全部绑定签名。</summary>
            public static HashSet<string> CardSigs(byte[] b)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in TextureSigs(b)) foreach (var s in kv.Value) set.Add(s);
                return set;
            }

            /// <summary>同款判定：预设签名里有多少比例能在目标卡上找到（0~1）。</summary>
            public double Score(HashSet<string> cardSigs)
            {
                var mine = AllSigs();
                if (mine.Count == 0) return 1.0;                 // 没记签名（老预设）→ 不拦
                if (cardSigs == null) return 0.0;
                int hit = 0;
                foreach (var s in mine) if (cardSigs.Contains(s)) hit++;
                return (double)hit / mine.Count;
            }
        }

        /// <summary>把预设 JSON 读进来（System.Text.Json，无外部依赖）。</summary>
        public static Preset PresetLoad(string path)
        {
            // 预设可能是「卡面 PNG + 内嵌 JSON」（PresetIo 存的），也可能是老的纯 .json
            string text = PresetIo.ReadJson(path);
            if (text == null) throw new IOException("读不出预设内容：" + path);
            return PresetLoadText(text);
        }

        /// <summary>从 JSON 文本读预设（PresetIo 也可以直接用）。</summary>
        public static Preset PresetLoadText(string text)
        {
            var pf = new Preset();
            using (var doc = System.Text.Json.JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                System.Text.Json.JsonElement e;
                if (root.TryGetProperty("source", out e) && e.ValueKind == System.Text.Json.JsonValueKind.String)
                    pf.Source = e.GetString();
                if (root.TryGetProperty("cardType", out e) && e.ValueKind == System.Text.Json.JsonValueKind.String)
                    pf.IsChara = string.Equals(e.GetString(), "chara", StringComparison.OrdinalIgnoreCase);
                if (root.TryGetProperty("minMatch", out e) && e.ValueKind == System.Text.Json.JsonValueKind.Number)
                    pf.MinMatch = e.GetDouble();
                if (root.TryGetProperty("uniform", out e) && e.ValueKind == System.Text.Json.JsonValueKind.Object)
                    pf.Uniform = ReadRule(e);
                if (root.TryGetProperty("sigs", out e) && e.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var s in e.EnumerateArray())
                        if (s.ValueKind == System.Text.Json.JsonValueKind.String) pf.SigSet.Add(s.GetString());
                if (root.TryGetProperty("parts", out e) && e.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject())
                    {
                        int k;
                        if (!int.TryParse(p.Name, out k)) continue;
                        pf.Parts[k] = ReadRule(p.Value);
                    }
                if (root.TryGetProperty("textures", out e) && e.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var t in e.EnumerateArray())
                    {
                        var pt = new PresetTex { Rule = ReadRule(t) };
                        if (t.TryGetProperty("id", out e) && e.ValueKind == System.Text.Json.JsonValueKind.String)
                            pt.Id = e.GetString();
                        if (t.TryGetProperty("sigs", out e) && e.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var s in e.EnumerateArray())
                                if (s.ValueKind == System.Text.Json.JsonValueKind.String) pt.Sigs.Add(s.GetString());
                        pf.Textures.Add(pt);
                    }
            }
            // 老预设 / 手改的预设可能没有 cardType：用「部位键是不是组·部位键（≥100000）」反推卡种，
            // 否则人物卡预设套到人物卡上会被当成「预设是衣服卡预设」直接跳过。
            if (!pf.IsChara)
                foreach (var k in pf.Parts.Keys)
                    if (Chara.IsCharaKey(k)) { pf.IsChara = true; break; }
            return pf;
        }

        public static Rule ReadRule(System.Text.Json.JsonElement o)
        {
            var r = new Rule("keep", 0);
            System.Text.Json.JsonElement v;
            if (o.TryGetProperty("format", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                r.Format = v.GetString();
            if (o.TryGetProperty("size", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                r.Size = v.GetInt32();
            // 可选字段：老文件没有 → 保持 -1/""（跟随全局），行为与以前一致
            if (o.TryGetProperty("sh", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                r.SharpenPct = v.GetInt32();
            if (o.TryGetProperty("shm", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                r.SharpenMode = v.GetString();
            if (o.TryGetProperty("ga", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                r.GrayA = v.GetInt32();
            if (o.TryGetProperty("up", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                r.Up = v.GetInt32();
            return r;
        }

        /// <summary>把四项"按贴图处理方式"追加进一段 JSON 对象（都是"跟随"时什么都不写，
        /// 这样生成的 plan/预设跟以前逐字节一样，老工具也读得懂）。</summary>
        public static string RuleExtraJson(Rule r)
        {
            if (r == null || r.AllFollow) return "";
            var sb = new StringBuilder();
            if (r.SharpenPct >= 0) sb.AppendFormat(", \"sh\": {0}", r.SharpenPct);
            if (!string.IsNullOrEmpty(r.SharpenMode)) sb.AppendFormat(", \"shm\": \"{0}\"", r.SharpenMode);
            if (r.GrayA >= 0) sb.AppendFormat(", \"ga\": {0}", r.GrayA);
            if (r.Up >= 0) sb.AppendFormat(", \"up\": {0}", r.Up);
            return sb.ToString();
        }

        /// <summary>导出预设 JSON（人可读、可手改）。</summary>
        public static void PresetSave(Preset pf, string path)
        {
            File.WriteAllText(path, PresetJson(pf), new UTF8Encoding(false));
        }

        /// <summary>预设 → JSON 文本（PresetIo 包成「卡面 PNG + 内嵌 JSON」时要用）。</summary>
        public static string PresetJson(Preset pf)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"tool\": \"KoiCardTexTool\",\n  \"version\": 1,\n");
            sb.Append("  \"note\": \"部位规则 + 单张贴图规则 + 通用设置 + 整卡签名指纹；可套用到同款服装（只有贴图不同）的其它卡\",\n");
            sb.AppendFormat("  \"source\": \"{0}\",\n", Escape(pf.Source));
            sb.AppendFormat("  \"cardType\": \"{0}\",\n", pf.IsChara ? "chara" : "clothes");
            sb.AppendFormat("  \"minMatch\": {0},\n", pf.MinMatch.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            if (pf.Uniform != null)
                sb.AppendFormat("  \"uniform\": {{ \"format\": \"{0}\", \"size\": {1}{2} }},\n",
                    pf.Uniform.Format, pf.Uniform.Size, RuleExtraJson(pf.Uniform));
            else sb.Append("  \"uniform\": null,\n");
            sb.AppendFormat("  \"sigCount\": {0},\n  \"sigs\": [", pf.SigSet.Count);
            for (int k = 0; k < pf.SigSet.Count; k++)
                sb.AppendFormat("{0}{1}\"{2}\"", k == 0 ? "" : ", ", k % 4 == 0 ? "\n    " : "", Escape(pf.SigSet[k]));
            sb.Append(pf.SigSet.Count > 0 ? "\n  ],\n" : "],\n");
            sb.Append("  \"parts\": {");
            int i = 0;
            foreach (var kv in pf.Parts)
                sb.AppendFormat("{0}\n    \"{1}\": {{ \"name\": \"{2}\", \"format\": \"{3}\", \"size\": {4}{5} }}",
                    i++ == 0 ? "" : ",", kv.Key, Escape(Card.SlotName(kv.Key)), kv.Value.Format, kv.Value.Size,
                    RuleExtraJson(kv.Value));
            sb.Append(pf.Parts.Count > 0 ? "\n  },\n" : "},\n");
            sb.Append("  \"textures\": [");
            for (int n = 0; n < pf.Textures.Count; n++)
            {
                var t = pf.Textures[n];
                sb.AppendFormat("{0}\n    {{ \"id\": \"{1}\", \"format\": \"{2}\", \"size\": {3}{4}, \"sigs\": [",
                    n == 0 ? "" : ",", Escape(t.Id), t.Rule == null ? "keep" : t.Rule.Format,
                    t.Rule == null ? 0 : t.Rule.Size, RuleExtraJson(t.Rule));
                for (int k = 0; k < t.Sigs.Count; k++)
                    sb.AppendFormat("{0}\"{1}\"", k == 0 ? "" : ", ", Escape(t.Sigs[k]));
                sb.Append("] }");
            }
            sb.Append(pf.Textures.Count > 0 ? "\n  ]\n}\n" : "]}\n");
            return sb.ToString();
        }

        static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>只读文件头/卡片信息（不解析贴图），供拖入时立即显示。</summary>
        public static byte[] Peek(string path)
        {
            try { return ReadAll(path); }
            catch { return new byte[0]; }
        }

        /// <summary>只读**卡头**（默认前 1 MB）—— 判断"是不是卡片/哪种卡/卡名"只需要这一段。
        ///
        /// 为什么重要：卡里那个 PNG 缩略图的 IEND 实测都在 140~152 KB 处，
        /// 而 Card.ReadInfo 需要的 tag/版本/卡名/`§version` 全在它后面一点点。
        /// 以前这些判断一律走 Peek()（整读 270 MB），
        /// 而「导入时自动展开人物卡组过滤」一次要看 8 张卡 —— 那就是 2 GB 的读盘。
        /// 找不到 IEND（异常格式）时返回 null，调用方自己退回整读。</summary>
        public static byte[] PeekHead(string path)
        {
            try
            {
                var b = ReadHead(path, 1 << 20);
                if (b.Length < 8) return null;
                return Card.PngEnd(b) < 0 ? null : b;
            }
            catch { return null; }
        }

        /// <summary>只要"是不是人物卡"：优先用卡头判断（读 1MB），判断不了才整读。</summary>
        public static bool IsCharaCardFast(string path)
        {
            var h = PeekHead(path);
            if (h != null) return Chara.IsCharaCard(h);
            return Chara.IsCharaCard(Peek(path));
        }

        /// <summary>卡信息 + 贴图分类统计，供拖入时"直接读取"或 info 命令打印。</summary>
        public static string Describe(string path, ScanResult scan, Card.Info info)
        {
            var sb = new StringBuilder();
            string fn = Path.GetFileName(path);
            if (info == null || !info.IsCard)
            {
                if (info != null && info.PngMissing.Length > 0) sb.AppendLine(fn + "：" + info.PngMissing);
                else if (info != null) sb.AppendLine(fn + "：像是卡片但标识是 " + info.Tag);
            }
            if (info != null && info.IsCard)
                sb.AppendLine(L.F("{0}  〖{1}〗 版本 {2} / 数据 {3}  名称：{4}  部件 {5} 个",
                    fn, info.Tag.Replace("【", "").Replace("】", ""), info.HeaderVersion, info.DataVersion,
                    info.Name, info.Parts));
            if (scan == null) { sb.Append(fn + "：无法读取"); return sb.ToString(); }
            if (scan.NTex == 0) { sb.AppendLine(fn + "：这张卡没有内嵌贴图（普通卡），无可压缩内容"); return sb.ToString(); }
            sb.AppendLine(L.F("  贴图 {0} 张 / {1}", scan.NTex, Human(scan.Bytes)));
            foreach (var c in Classes.Order)
            {
                long[] v;
                if (scan.Classes.TryGetValue(c, out v) && v[0] > 0)
                    sb.AppendLine(L.F("    {0,-10} {1,4} 张  {2,10}  最大 {3}px", c, v[0], Human(v[1]), v[2]));
            }
            return sb.ToString().TrimEnd();
        }

        // ---------- image helpers ----------
        static Bitmap Load(byte[] raw)        {
            using (var ms = new MemoryStream(raw, false))
            using (var im = Image.FromStream(ms, false, false))
                return new Bitmap(im);
        }

        static Bitmap ToArgb(Bitmap src)
        {
            if (src.PixelFormat == PixelFormat.Format32bppArgb) return src;
            var o = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(o))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return o;
        }

        /// <summary>扫一遍像素，判断三类事实：
        /// flatAlpha  = alpha 是否恒为 255（"平坦 alpha"）
        /// gray       = 是否每个像素 R==G==B（严格灰度，可无损降通道）
        /// alphaFlat05= alpha 是否**几乎恒为 127（≈0.5）的占位值**
        ///
        /// 第三项是为「alpha 也锐化」把关的：实测 37 张被锐化的法线里，多数 alpha 的
        /// p5 和 p95 都等于 127 —— 那是作者工具写进去的**占位常数**，不是掩罩。
        /// 锐化一条几乎恒定的通道只会放大那几个离群点（体积 +、画质无），所以这种要跳过。
        ///
        /// 第四项 alphaCircleOnly 是给 matcap 的例外规则把关的（见 MatcapCircleJpeg 注释）：
        /// "非不透明"（**a &lt; 250**，把 254/253 这种抖动也算上）的像素，是否**只出现在内切圆之外**。
        /// 判据是"圆内 a&lt;250 的像素占圆内面积的比例 ≤ 0.5%"。
        /// 5% 内不算：实测 9 张可放行的 matcap 圆内占比都在 0.17% 以下，而真形状掩罩是 2.1% / 23%。</summary>
        static void Analyze(Bitmap argb, out bool flatAlpha, out bool gray, out bool alphaFlat05,
                            out bool alphaCircleOnly)
        {
            flatAlpha = true; gray = true; alphaFlat05 = true; alphaCircleOnly = false;
            int w = argb.Width, h = argb.Height;
            long n = 0, nmid = 0, nlow = 0, nlowIn = 0;
            var bd = argb.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                        // 内切圆判据：把像素中心归一化到 [-1,1]，半径 > 1 即在圆外
                        double dy = (y + 0.5) / h * 2 - 1;
                        for (int x = 0; x < w; x++)
                        {
                            byte b0 = row[x * 4], g0 = row[x * 4 + 1], r0 = row[x * 4 + 2], a0 = row[x * 4 + 3];
                            if (a0 != 255) flatAlpha = false;
                            if (gray && (b0 != g0 || g0 != r0)) gray = false;
                            n++;
                            if (a0 >= 120 && a0 <= 136) nmid++;
                            if (a0 < 250)
                            {
                                nlow++;
                                double dx = (x + 0.5) / w * 2 - 1;
                                if (dx * dx + dy * dy <= 1.0) nlowIn++;
                            }
                            // 三项都已有结论才提前退出：flatAlpha 和 gray 已否，且中段像素已超 5%
                            if (!flatAlpha && !gray && nmid * 20 > n) return;
                        }
                    }
                }
            }
            finally { argb.UnlockBits(bd); }
            // 阈值 95%：实测"占位 127"的那些 ≥99% 落在 ±8 内；
            // 真有内容的（双峰掩罩 / 与亮度相关 / 宽分布）远远低于这个值。
            alphaFlat05 = n > 0 && (double)nmid / n > 0.95;
            // 分母用"圆内像素数"而不是总像素数：圆边掩罩的四个角本来就不该参与判定。
            alphaCircleOnly = n == 0 || (double)nlowIn / n <= 0.005;
        }

        static void Analyze(Bitmap argb, out bool flatAlpha, out bool gray, out bool alphaFlat05)
        {
            bool dummyCircle;
            Analyze(argb, out flatAlpha, out gray, out alphaFlat05, out dummyCircle);
        }

        static void Analyze(Bitmap argb, out bool flatAlpha, out bool gray)
        {
            bool dummy, dummy2;
            Analyze(argb, out flatAlpha, out gray, out dummy, out dummy2);
        }

        static Bitmap Resize(Bitmap src, int w, int h, bool gray, bool flatAlpha)
        {
            return Resize(src, w, h, gray, flatAlpha, false, false, false, false, 0, 0, null);
        }

        static Bitmap Resize(Bitmap src, int w, int h, bool gray, bool flatAlpha, bool detail)
        {
            return Resize(src, w, h, gray, flatAlpha, detail, false, false, false, 0, 0, null);
        }

        static Bitmap Resize(Bitmap src, int w, int h, bool gray, bool flatAlpha, bool detail, bool premul)
        {
            return Resize(src, w, h, gray, flatAlpha, detail, premul, false, false, 0, 0, null);
        }

        /// <summary>同尺寸下的**精确**像素拷贝（不做任何插值）。
        /// 为什么需要：GDI+ 的 DrawImage 即使 1:1 + HighQualityBicubic 也会让像素挪动 ±1
        /// （实测某张灰度+alpha 贴图有近一半像素的灰阶差 1），这会破坏"无损"承诺。</summary>
        static unsafe Bitmap CopyExact(Bitmap src, PixelFormat want)
        {
            int w = src.Width, h = src.Height;
            var dst = new Bitmap(w, h, want);
            var sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, want);
            try
            {
                bool rgb24 = want == PixelFormat.Format24bppRgb;
                for (int y = 0; y < h; y++)
                {
                    byte* s = (byte*)sd.Scan0 + y * sd.Stride;
                    byte* d = (byte*)dd.Scan0 + y * dd.Stride;
                    if (!rgb24) Buffer.MemoryCopy(s, d, (long)w * 4, (long)w * 4);
                    else for (int x = 0; x < w; x++) { d[x * 3] = s[x * 4]; d[x * 3 + 1] = s[x * 4 + 1]; d[x * 3 + 2] = s[x * 4 + 2]; }
                }
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        static Bitmap Resize(Bitmap src, int w, int h, bool gray, bool flatAlpha, bool detail, bool premul, bool denoise,
                             bool alphaSharp, double sharpK, double sharpKEdge, string sharpMode)
        {
            // GDI+ cannot create a Graphics from an indexed bitmap, so resample into a normal
            // format first and only then (optionally) pack into 8bpp gray.
            PixelFormat work = flatAlpha ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb;
            bool downscaled = (w < src.Width || h < src.Height);
            bool upscaled = (w > src.Width || h > src.Height);      // v2.33
            Bitmap tmp = null;
            bool areaPath = false;

            // ---- 第 1 步：降采样 ----
            // 「细节图保细节」只决定**用哪种降采样核**：勾上 = 数据贴图用面积平均（+彩色贴图看 colorArea），
            // 不勾 = 退回 GDI+ 双三次。它不再管锐化（锐化已独立成第 3 步）。
            if (w == src.Width && h == src.Height)
            {
                tmp = CopyExact(src, work);                  // 同尺寸：精确拷贝，绝不插值
                Resample.SameSize++;
            }
            // ---- 第 1 步之二：放大（v2.33；目标大于源时才有这一步）----
            // 放在降采样分支**之前**决定路径：两者互斥（一个放大一个缩小），
            // 但放大不依赖「细节图保细节」那个开关 —— 那个开关管的是"缩小用哪种核"。
            if (tmp == null && upscaled)
            {
                // 核名从**只读**静态取：没有"按贴图指定放大核"这种需求，所以不存在逐贴图写静态的串味问题
                // （锐化之所以要逐贴图传参，是因为它可以按贴图覆盖）。
                tmp = Resample.Upscale(src, w, h, Resample.UpKernel, premul);
                if (tmp != null) Resample.UpUsed++;
            }
            if (tmp == null && downscaled && detail && Resample.DetailMode)
            {
                // 【实验项 E】结构保留降采样：在"平均"这一步就保住细线/蕾丝。
                tmp = Resample.StructGamma > 0 ? Resample.AreaStruct(src, w, h, premul, Resample.StructGamma) : null;
                if (tmp == null) tmp = Resample.Area(src, w, h, premul);      // 整数倍：块平均（快）
                if (tmp == null) tmp = Resample.AreaF(src, w, h, premul);   // 非整数倍：分数权重面积平均
                if (tmp != null) { Resample.AreaUsed++; areaPath = true; }
            }
            if (tmp == null)
            {
                tmp = new Bitmap(w, h, work);
                using (var g = Graphics.FromImage(tmp))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
            }

            // ---- 第 2 步：降噪/去块（off / all / dirty）----
            // 只处理源自带 JPEG 伪影的（作者从 JPEG 导出的）。必须排在锐化**前面**：
            // 先锐化再降噪等于把刚放大的噪声又抹一遍，白费。
            if (areaPath && denoise && Resample.Denoise != "off")
            {
                bool dirty = false;
                if (Resample.Denoise == "dirty")
                {
                    dirty = Resample.Blockiness(src) > Resample.DirtyThreshold;
                    if (dirty) Resample.DirtSeen++;
                }
                if (Resample.Denoise == "all" || dirty)
                {
                    Resample.Guided(tmp, Resample.DenoiseRadius, Resample.DenoiseEps);
                    Resample.Denoised++;
                }
            }

            // ---- 第 3 步：锐化（**独立阶段**）----
            // 运行条件只有一条：**这张贴图的分辨率真的变过**（缩小或放大）。
            // 不再要求"数据类贴图"、不再要求"勾了细节图保细节" —— 彩色贴图走 GDI+ 双三次那条路
            // 也会被锐化。没变分辨率的贴图（同尺寸精确拷贝）一律不锐化。
            // v2.33：放大也算"变过" —— 放大后的画面天生偏软（重建插值必然低通），
            // 这时候的锐化是补回锐度的主力，不锐化等于白白放大。
            // 强度/方式由调用方算好传进来（可能是这张贴图自己的按贴图规则，
            // 也可能是全局值）—— 不能在这里读 Resample 的静态字段：贴图是**并行**处理的，
            // 按贴图改静态值会串味。
            if ((downscaled || upscaled) && sharpK > 0)
            {
                if (sharpMode == "cas") Resample.CasInPlace(tmp, sharpK, alphaSharp, premul);
                else Resample.UnsharpInPlace(tmp, sharpK, sharpKEdge, alphaSharp, premul);
                Resample.SharpUsed++;
            }

            // ---- 第 4 步：可选打包成 8bpp 灰度 ----
            if (!(gray && flatAlpha)) return tmp;

            var idx = new Bitmap(w, h, PixelFormat.Format8bppIndexed);
            var pal = idx.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(255, i, i, i);
            idx.Palette = pal;
            var sd = tmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, work);
            var dd = idx.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                unsafe
                {
                    int bpp = flatAlpha ? 3 : 4;
                    for (int y = 0; y < h; y++)
                    {
                        byte* s = (byte*)sd.Scan0 + y * sd.Stride;
                        byte* d = (byte*)dd.Scan0 + y * dd.Stride;
                        for (int x = 0; x < w; x++) d[x] = s[x * bpp + (flatAlpha ? 0 : 0)];
                    }
                }
            }
            finally { tmp.UnlockBits(sd); idx.UnlockBits(dd); }
            tmp.Dispose();
            return idx;
        }

        static byte[] EncodePng(Bitmap b)
        {
            using (var ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] EncodeJpeg(Bitmap b, int quality)
        {
            ImageCodecInfo jpg = null;
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) { jpg = c; break; }
            using (var ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, jpg, ps);
                    return ms.ToArray();
                }
            }
        }

        // ---------- main work ----------
        /// <summary>老签名：完全按原来的行为（类型表 + 全局 mode/max）。</summary>
        public static RepackResult Repack(string src, string dst,
                                          Dictionary<string, Rule> plan, int maxSize, string mode,
                                          int quality, int maskSize, List<string> only, bool protectAlpha,
                                          Action<string> log, Action<int, int> progress, Func<bool> cancel)
        {
            return Repack(src, dst, plan, maxSize, mode, quality, maskSize, only, protectAlpha,
                          null, log, progress, cancel);
        }

        /// <summary>按部位压缩（slotRules 非空时启用）：每张贴图的规则来自它所属的部位，
        /// 共用的贴图必须所有拥有者都启用才动，合并规则见 MergeSlotRules。</summary>
        public static RepackResult Repack(string src, string dst,
                                          Dictionary<string, Rule> plan, int maxSize, string mode,
                                          int quality, int maskSize, List<string> only, bool protectAlpha,
                                          Dictionary<int, Rule> slotRules,
                                          Action<string> log, Action<int, int> progress, Func<bool> cancel)
        {
            return Repack(src, dst, plan, maxSize, mode, quality, maskSize, only, protectAlpha,
                          slotRules, null, log, progress, cancel);
        }

        /// <summary>再带一个预设：预设的「单张贴图规则」优先级最高（用户明确点过的那一张），
        /// 部位规则仍由 slotRules 决定（预设里的 parts 只在 slotRules 为 null 时顶上）。</summary>
        public static RepackResult Repack(string src, string dst,
                                          Dictionary<string, Rule> plan, int maxSize, string mode,
                                          int quality, int maskSize, List<string> only, bool protectAlpha,
                                          Dictionary<int, Rule> slotRules, Preset preset,
                                          Action<string> log, Action<int, int> progress, Func<bool> cancel)
        {
            return Repack(src, dst, plan, maxSize, mode, quality, maskSize, only, protectAlpha,
                          slotRules, preset, 0, log, progress, cancel);
        }

        /// <summary>charaGroupMask：人物卡专用 —— 位 g 置 1 表示「第 g 组（0=角色本体，1..7=换装）」参与压缩，
        /// 0 = 不限制。一张贴图**所有拥有者所在组**都在掩码里才会动，与"共用贴图所有者全勾才动"一致。</summary>
        /// <summary>贴图级并行度：KOITEX_TEXJOBS 覆盖；默认 min(8, 核数)。
        /// 比卡级并行保守，是因为同一张卡里的贴图要共用一批大缓冲区，开太多反而互相抢内存带宽。</summary>
        public static int TexJobs(int total)
        {
            int n = 0;
            string e = Environment.GetEnvironmentVariable("KOITEX_TEXJOBS");
            if (!string.IsNullOrEmpty(e)) { int v; if (int.TryParse(e, out v)) n = v; }
            if (n <= 0) n = Math.Min(8, Environment.ProcessorCount);
            if (n < 1) n = 1;
            if (n > total) n = Math.Max(1, total);
            return n;
        }

        /// <summary>卡级并行度（GUI 批量用）：KOITEX_JOBS 覆盖；默认 min(4, 核数)。
        /// 不按核数开是因为真正限制并发的是**内存**（一张 200 MB 的卡峰值约 1.1 GB）。
        /// 命令行 batch 另有 jobs=N 参数，走 Program.ParseJobs。</summary>
        public static int CardJobs(int total)
        {
            int n = 0;
            string e = Environment.GetEnvironmentVariable("KOITEX_JOBS");
            if (!string.IsNullOrEmpty(e)) { int v; if (int.TryParse(e, out v)) n = v; }
            if (n <= 0) n = Math.Min(4, Environment.ProcessorCount);
            if (n < 1) n = 1;
            if (n > total) n = Math.Max(1, total);
            return n;
        }

        public static RepackResult Repack(string src, string dst,
                                          Dictionary<string, Rule> plan, int maxSize, string mode,
                                          int quality, int maskSize, List<string> only, bool protectAlpha,
                                          Dictionary<int, Rule> slotRules, Preset preset, int charaGroupMask,
                                          Action<string> log, Action<int, int> progress, Func<bool> cancel)
        {
            if (log == null) log = delegate (string s) { };
            if (slotRules == null && preset != null && preset.Parts.Count > 0)
                slotRules = preset.Parts;
            var res = new RepackResult();
            // 统计计数的起点快照：这些计数器是 [ThreadStatic] 的，取差值就能拿到"这一张卡"的准确数字，
            // 并行处理多张卡时不会互相串台。
            int cAreaU = Resample.AreaUsed, cSharpU = Resample.SharpUsed;
            int cDen = Resample.Denoised, cDirt = Resample.DirtSeen;
            int cJpU = JpegOpt.Used; long cJpS = JpegOpt.Saved;
            byte[] b = ReadAll(src);
            res.CardBefore = b.Length;

            int end = Card.PngEnd(b);
            if (end < 0)
            {
                if (SkipCopy.TryCopy(src, dst, res, log, "不是卡片（没有 PNG/IEND 结构）")) return res;
                log("不是卡片（没有 PNG/IEND 结构）: " + src);
                res.Rc = 2; res.Reason = RepackResult.RcText(2); return res;
            }

            // ---- 同款检测 + 通用设置 ----
            // 预设是给「同款服装（只有贴图不同）」用的：拿绑定签名算匹配度，不像同款就别硬压
            // （部位规则套到别的衣服上只会误伤）。匹配度低于 minMatch 时默认跳过；
            // 勾了「不匹配服装使用通用设置压缩」则改用通用设置整卡统一压
            // —— 通用设置**只在这一种情况下生效**，绝不参与同款卡的部位规则。
            bool uniformMode = false, forced = false;
            if (preset != null)
            {
                bool cardIsChara = Chara.IsCharaCard(b);
                if (preset.IsChara != cardIsChara)
                {
                    // 种类都不同（人物卡预设套衣服卡 / 反之）→ 直接跳过，别硬套
                    res.Reason = L.F("预设是{0}，本卡是{1}",
                        preset.IsChara ? "人物卡预设" : "衣服卡预设", cardIsChara ? "人物卡" : "衣服卡");
                    if (SkipCopy.TryCopy(src, dst, res, log, res.Reason)) return res;
                    res.Rc = 6;
                    log("  [跳过] " + res.Reason);
                    return res;
                }
                if (cardIsChara)
                {
                    // 人物卡：预设的「组·部位」规则是通用的（哪套衣服、哪个部位），跟具体穿什么无关，
                    // 不再做同款检测 —— 之前拿签名重叠度去比，两张不同角色卡只有 28.6%，会被误跳过。
                    log("  [人物卡] 按预设的「组·部位」规则处理（人物卡之间不做同款检测）");
                }
                else
                {
                    double sc = preset.Score(Preset.CardSigs(b));
                    res.OutfitScore = sc;
                    if (sc < preset.MinMatch)
                    {
                        if (preset.ForceUniform && preset.Uniform != null && preset.Uniform.Format != "keep")
                        {
                            uniformMode = true; forced = true;
                            log(L.F("  [不匹配服装] 与预设匹配度 {0:0.0}%（低于 {1:0.0}%）→ 按通用设置 {2}{3} 整卡压",
                                sc * 100, preset.MinMatch * 100, preset.Uniform.Format,
                                preset.Uniform.Size > 0 ? "/" + preset.Uniform.Size : "/不缩放"));
                        }
                        else
                        {
                            res.Reason = preset.ForceUniform
                                ? L.F("与预设不是同款服装（匹配度 {0:0.0}%），且没有可用的通用设置", sc * 100)
                                : L.F("与预设不是同款服装（匹配度 {0:0.0}%，要求 ≥{1:0.0}%）",
                                                sc * 100, preset.MinMatch * 100);
                            if (SkipCopy.TryCopy(src, dst, res, log, res.Reason)) return res;
                            res.Rc = 6;
                            log("  [跳过] " + res.Reason
                                + (preset.ForceUniform ? " —— 如要压，命令行加 force=1（改用预设里的通用设置）"
                                                       : " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」"));
                            return res;
                        }
                    }
                    else
                    {
                        log(L.F("  [同款] 与预设匹配度 {0:0.0}% ✓ 按预设的部位/单张贴图规则处理", sc * 100));
                    }
                }
            }

            int kpos = Card.FindKeyCached(b, "TextureDictionary");
            if (kpos < 0)
            {
                File.WriteAllBytes(dst, b);
                log("无贴图（卡里没有 MaterialEditor TextureDictionary），已原样复制到 " + dst);
                res.Rc = 3; res.Reason = RepackResult.RcText(3); res.CardAfter = res.CardBefore; return res;
            }
            object holder = MP.Read(b, ref kpos);
            var bin = holder as BinRef;
            if (bin == null) { log("TextureDictionary 结构异常（不是 bin），未改动卡片"); res.Rc = 4; res.Reason = "TextureDictionary 不是 bin"; return res; }

            int tp = bin.Off;
            var dic = MP.Read(b, ref tp) as MMap;
            if (dic == null) { log("TextureDictionary 不是 map，未改动卡片"); res.Rc = 4; res.Reason = "TextureDictionary 不是 map"; return res; }

            var props = Card.TextureProperties(b);
            res.PayloadBefore = bin.Len;

            // 按部位/按组模式才需要：贴图 → 拥有它的键（衣服卡=槽位键，人物卡=组·部位键）
            // 「独占贴图保护」也需要它（要数一张贴图被几个部位引用），所以把它加进条件
            bool charaCard = Chara.IsCharaCard(b);
            Dictionary<object, List<int>> texSlots = null;
            Dictionary<object, List<string>> texSigs = null;
            if (slotRules != null || charaGroupMask != 0 || (preset != null && preset.Textures.Count > 0) || ProtectOwn)
            {
                texSlots = TexKeys(b, charaCard);
                if (charaCard && (slotRules != null || charaGroupMask != 0))
                    log(L.F("  [人物卡] 角色本体 + {0} 套换装：{1} 张贴图 / {2} 个「组·部位」组合",
                        Chara.CoordNames.Length,
                        (texSlots.Count), CountKeys(texSlots)));
            }
            if (preset != null && preset.Textures.Count > 0)
                texSigs = SigMap(b, charaCard);
            Dictionary<object, List<string>> texShaders = NonalphaJpeg ? TexShaders(b) : null;   // 仅测试开关打开时才建
            var usedPresetTex = new List<string>();

            foreach (var kk in dic.Keys)
            {
                if (!(kk is long))
                {
                    log("贴图键不是整数（" + kk + "），本工具不支持，未改动卡片");
                    res.Rc = 4;
                    res.Reason = "贴图键不是整数（" + kk + "）";
                    return res;
                }
            }

            var inner = new MemoryStream();
            var hdr = MP.MapHeader(dic.Keys.Count);
            inner.Write(hdr, 0, hdr.Length);

            int total = dic.Keys.Count;
            // ---- 贴图级并行 ----
            // 一张贴图与另一张完全独立（各读各的字节、各算各的结果），只有**写 msgpack** 必须按原顺序。
            // 所以分两遍：并行把每张贴图算成 j.Out，再串行按顺序写入。日志也按原顺序缓存在 job 里一起打。
            int texN = TexJobs(total);
            if (texN > 1) log(L.F("  贴图级并行：{0} 路（KOITEX_TEXJOBS / texjobs=N 可改）", texN));
            var jobs = new TexJob[total];
            bool cancelled = false;

            TexJob ProcessTex(int i)
            {
                object key = dic.Keys[i];
                var j = new TexJob { Key = key };
                Action<string> lg = delegate (string s) { if (s != null) j.Lines.Add(s); };


                var v = dic.Vals[i] as BinRef;
                if (v == null) return null;
                byte[] raw = v.Bytes();
                List<string> pl;
                if (!props.TryGetValue(key, out pl)) pl = new List<string>();
                string cls = Classes.Classify(pl);
                string pstr = Card.PropsToString(pl);

                Rule rule = null;
                if (plan != null && plan.ContainsKey(cls)) rule = plan[cls];

                bool keep = false;
                if (rule != null) keep = rule.Format == "keep";
                else if (only != null)
                {
                    keep = true;
                    foreach (var sub in only)
                        foreach (var p0 in pl)
                            if ((p0 ?? "").ToLowerInvariant().Contains(sub)) { keep = false; break; }
                }

                // 人物卡组过滤：拥有它的组必须**全部**在掩码里，否则原样不动
                if (charaGroupMask != 0 && charaCard)
                {
                    List<int> own = null;
                    if (texSlots != null) texSlots.TryGetValue(key, out own);
                    var off = new List<string>();
                    if (own != null)
                        foreach (var sk in own)
                        {
                            int g = Chara.KeyGroup(sk);
                            if (g < 0 || g > 30 || (charaGroupMask & (1 << g)) == 0) off.Add(KName(sk));
                        }
                    if (off.Count > 0)
                    {
                        keep = true;
                        lg(L.F("  [组过滤] TexID {0}: 属于未选中的 {1} —— 原样保留", key, string.Join("、", off.ToArray())));
                        j.Skipped.Add(L.F("TexID {0} ({1}, {2}): 属于未选中的组", key, FmtName(Sniff(raw)), Human(raw.Length)));
                    }
                }

                // 通用设置（整卡统一 / 不像同款强制）：先给整卡一个默认规则，再让部位/单张规则覆盖
                if (uniformMode)
                {
                    keep = preset.Uniform.Format == "keep";
                    rule = new Rule(preset.Uniform.Format, preset.Uniform.Size);
                }

                // 按部位模式：这张贴图的规则改由「拥有它的部位」决定（整卡统一时部位表不参与）
                if (slotRules != null && !uniformMode)
                {
                    List<int> owners = null;
                    if (texSlots != null) texSlots.TryGetValue(key, out owners);
                    Rule merged; string why;
                    if (MergeSlotRules(owners, slotRules, out merged, out why))
                    {
                        keep = false;
                        rule = merged;                      // 覆盖类型表来的规则
                        if (owners != null && owners.Count > 1)
                            lg(L.F("  [共用] TexID {0}: {1} 张部位共享 → 按 {2}{3} 处理",
                                key, owners.Count, merged.Format,
                                merged.Size > 0 ? "/" + merged.Size : ""));
                    }
                    else
                    {
                        keep = true;
                        string ownerTxt = owners == null || owners.Count == 0
                            ? "没有任何部位引用" : string.Join(" + ", OwnerNames(owners).ToArray());
                        lg(L.F("  [保留] TexID {0}: {1} —— {2}", key, ownerTxt, why));
                        j.Skipped.Add(string.Format("TexID {0} ({1}, {2}): {3}", key, FmtName(Sniff(raw)),
                            Human(raw.Length), why));
                    }
                }

                // 预设里的「单张贴图规则」优先级最高：用户明确点过这一张，就不受部位/共用规则约束
                // （强制套用通用设置时不用预设的单张规则 —— 那是给同款服装的，套到别的衣服上没意义）
                if (preset != null && preset.Textures.Count > 0 && !forced)
                {
                    List<string> sg = null;
                    if (texSigs != null) texSigs.TryGetValue(key, out sg);
                    var hits = preset.Match(key, sg);
                    if (hits.Count > 0)
                    {
                        Rule best = null;
                        foreach (var h in hits)
                        {
                            if (best == null) best = new Rule(h.Format, h.Size);
                            else best = new Rule(Rank(h.Format) < Rank(best.Format) ? h.Format : best.Format,
                                                 Math.Max(best.Size, h.Size));
                        }
                        rule = best;
                        keep = best.Format == "keep";
                        j.PresetMatched++;
                        if (j.UsedTex != null) j.UsedTex.Add(key.ToString());
                        lg(L.F("  [预设] TexID {0}: 单张指定 {1}{2}", key, best.Format,
                            best.Size > 0 ? "/" + best.Size : "/不缩放"));
                    }
                }

                j.St = new TexStat { Id = key, Cls = cls, InBytes = raw.Length, Props = pstr };
                var st = j.St;
                TexFmt fmt = Sniff(raw);
                byte[] outp;
                // v2.33：这一张到底放大了没有。至少要活到下面那条"结果不比原图小就退回原字节"
                // 的判断处（那个作用域比 up 的声明处浅），所以在外层声明。
                bool upApplied = false;
                string targetSize = null;      // 目标尺寸字符串（tw x th）；实际尺寸要在兜底判断之后才能定
                if (keep)
                {
                    outp = raw; st.Fmt = "keep"; st.Mode = cls + "/原样";
                }
                else if (fmt == TexFmt.Hdr)
                {
                    // HDR 不是 Raster 图，走不了 GDI+：只能线性空间缩放后按 RGBE 重新编码
                    // （格式保持 HDR，加载路径与原图完全一致，不会变成 JPEG/PNG）。
                    st.Mode = "HDR";
                    outp = ReduceHdr(raw, rule, mode, maxSize, maskSize, lg, ref st);
                    if (outp == null) { outp = raw; st.Fmt = "orig"; st.Mode = "HDR 原样"; j.Skipped.Add(L.F("TexID {0} (HDR): 没有缩小（已在下限内或编码后更大），原样保留", key)); }
                    else st.Fmt = "HDR";
                }
                else if (fmt != TexFmt.Png && fmt != TexFmt.Jpeg)
                {
                    outp = raw; st.Fmt = "orig"; st.Mode = FmtName(fmt) + " 原样";
                    lg(L.F("  [跳过] TexID {0}: {1} 格式本工具不解码，原样保留（{2} 字节）",
                        key, FmtName(fmt), raw.Length));
                    j.Skipped.Add(L.F("TexID {0} ({1}): 本工具不认识这种格式，原样保留", key, FmtName(fmt)));
                }
                else
                {
                    try
                    {
                        int w = 0, h = 0;
                        bool flatAlpha = true, gray = false;
                        string origMode = "";
                        byte[] pngNew = null, jpgNew = null;
                        bool useJpg, autoMode;
                        int cap;

                        using (var src0 = Load(raw))
                        {
                            var swDec = Stopwatch.StartNew();
                            w = src0.Width; h = src0.Height;
                            origMode = Desc(src0.PixelFormat);
                            using (var argb = ToArgb(src0))
                            {
                                bool alphaFlat05, alphaCircleOnly;
                                Analyze(argb, out flatAlpha, out gray, out alphaFlat05, out alphaCircleOnly);
                                swDec.Stop();
                                j.TDecode += swDec.Elapsed.TotalMilliseconds;
                                // ---- 内建 alpha 判据（v2.19）----
                                // 工具**自己**决定格式时（auto）必须保证不丢有用的 alpha，判据只有两条：
                                //   ① alpha 逐像素全为 255（JPEG 无 alpha 通道，没什么可丢）
                                //   ② matcap 且"圆内几乎没有非不透明像素"（圆边掩罩，见 MatcapCircleJpeg 注释）
                                // 非 255 的 alpha 只要落在会被采样到的区域，就可能是真的不透明度 ——
                                // 实测教训：maintex 的 alpha 恒等于 230 时，那是**大片半透明布料**。
                                bool alphaOk = flatAlpha || (MatcapCircleJpeg && cls == "matcap" && alphaCircleOnly);
                                if (rule != null)
                                {
                                    string pref = string.IsNullOrEmpty(rule.Format) ? "auto" : rule.Format;
                                    cap = rule.Size > 0 ? rule.Size : Math.Max(w, h);
                                    // 显式选了 JPEG 就**真的**是 JPEG：不受上面那条内建判据约束。
                                    // （以前这里挂着一个「保护 alpha」开关，勾着的时候「全部 JPEG」其实不全 JPEG，
                                    //   名不副实；v2.19 起把开关删掉，显式选择 = 显式承担后果。）
                                    useJpg = pref == "jpeg" ? true
                                           : (pref == "png" ? false : (alphaOk && !Risky(pl)));
                                    autoMode = pref == "auto";
                                }
                                else
                                {
                                    useJpg = mode == "jpg" ? true : (mode == "auto" ? (alphaOk && !Risky(pl)) : false);
                                    autoMode = mode == "auto";
                                    cap = useJpg ? maxSize : maskSize;
                                }

                                // ---- 【测试开关】按类型强制转 JPEG（例如 forcejpg=matcap）----
                                // 依据是**游戏内实测**：matcap 转 JPEG 观感基本无影响。
                                // 注意 JPEG 没有 alpha → 会丢 alpha；这里统计"被强制转但 alpha 非平坦"的张数，
                                // 让风险面可见（上一轮凭空多出反光就是丢了在用着的 alpha）。
                                if (ForceJpgClasses.Count > 0 && !keep && ForceJpgClasses.Contains(cls)
                                    && (ForceJpgAlphaLevel == 0
                                        || (ForceJpgAlphaLevel == 1 && flatAlpha)
                                        || (ForceJpgAlphaLevel == 2 && (flatAlpha || alphaCircleOnly))))
                                {
                                    if (!useJpg)
                                    {
                                        useJpg = true; autoMode = false;
                                        j.NShaderJpeg++;
                                        if (!flatAlpha) j.NForcedAlphaLost++;
                                        if (j.NShaderJpeg <= 12)
                                            lg(L.F("  [强制JPEG] TexID {0}（{1}，{2}）alpha{3}",
                                                key, cls, w + "x" + h, flatAlpha ? "平坦"
                                                    : (alphaCircleOnly ? "非平坦但只在圆外（安全）" : "**非平坦→会丢**")));
                                        if (cap <= 0) cap = maxSize;
                                    }
                                }

                                // ---- 【测试开关】非 alpha 着色器的贴图强制转 JPEG ----
                                // 两道闸门都要过：① 材质用的 shader 名都不含 "alpha"
                                //                ② **alpha 全为 255**（JPEG 没有 alpha 通道，先确认没东西可丢）
                                // 只靠 ① 会踩坑：KKUTS/Goo 名字里没 alpha 却用 alpha；连 main_opaque 也用
                                // （AlphaTest 队列 + _Cutoff）。实测丢 alpha 会让材质凭空多出满值反光。
                                if (NonalphaJpeg && !keep && flatAlpha && AllShadersNonAlpha(texShaders, key))
                                {
                                    if (!useJpg)
                                    {
                                        useJpg = true; autoMode = false;
                                        j.NShaderJpeg++;
                                        if (j.NShaderJpeg <= 12)
                                        {
                                            List<string> sn = null; texShaders.TryGetValue(key, out sn);
                                            lg(L.F("  [非alpha转JPEG] TexID {0}（{1}，{2}）shader={3}",
                                                key, cls, w + "x" + h, sn == null ? "?" : string.Join("/", sn.ToArray())));
                                        }
                                        if (cap <= 0) cap = maxSize;
                                    }
                                }

                                // ---- 独占贴图保护：只被一个部位引用的贴图，放宽上限 ----
                                // 依据：这类贴图体积占比小（实测全卡 8.4%），却承载该部位 100% 的自身细节；
                                // 按字节加权的质量评估和统一上限都会低估它。放宽后仍受"不放大"约束。
                                if (ProtectOwn && !keep)
                                {
                                    List<int> ownSlots = null;
                                    if (texSlots != null) texSlots.TryGetValue(key, out ownSlots);
                                    if (ownSlots != null && ownSlots.Count == 1)
                                    {
                                        int pc = ProtectOwnMax > 0 ? ProtectOwnMax : Math.Max(w, h);
                                        if (pc > cap)
                                        {
                                            cap = pc;
                                            j.NOwnProtected++;
                                            if (j.NOwnProtected <= 12)
                                                lg(L.F("  [独占保护] TexID {0}（{1}）只属于「{2}」→ 最大边放宽到 {3}",
                                                    key, cls, KName(ownSlots[0]), pc));
                                        }
                                    }
                                }

                                // ---- 按贴图/按部位覆盖：锐化强度、锐化方式、灰度 2 通道 ----
                                // rule 可能是类型表规则、部位合并规则或单张贴图规则，这三项要么"跟随全局"(-1/空)
                                // 要么是个明确值。算成**局部变量**往下传 —— 贴图是并行处理的，
                                // 绝不能为了让某张贴图用别的强度去改 Resample 的静态字段（会串味）。
                                int shPct = (rule != null && rule.SharpenPct >= 0) ? rule.SharpenPct : Resample.SharpenPct;
                                string shMode = (rule != null && !string.IsNullOrEmpty(rule.SharpenMode))
                                                ? rule.SharpenMode : Resample.SharpMode;
                                double shK = Resample.Sharpen * shPct / 100.0;
                                double shKEdge = Math.Max(Resample.SharpenEdgeMax, Resample.Sharpen) * shPct / 100.0;
                                bool grayA = (rule != null && rule.GrayA >= 0) ? rule.GrayA == 1 : PngEnc.UseGrayAlpha;
                                // ---- v2.33：放大（目标尺寸大于源尺寸）----
                                // 规则链选出来的 rule（单张贴图 > 按部位 > 类型表）里 Up 明确就听它的，
                                // 否则跟随全局 Resample.UpGlobal。默认全局是 0（不放大），所以不开这个功能时
                                // 下面的运算结果与 2.32 完全一致。
                                int up = (rule != null && rule.Up >= 0) ? rule.Up : Resample.UpGlobal;
                                bool upBlockedData = false, upBlockedCap = false;
                                if (up > 1)
                                {
                                    if (Resample.IsDataClass(cls))
                                    {
                                        // 法线/掩罩/高度不放大的理由不是"不好看"，而是**没意义还很贵**：
                                        // 它们的采样方式决定了更大尺寸不会带来新信息，而插值会改动语义
                                        // （法线要保单位长度与方向、掩罩是权重）。白白 3 倍体积。
                                        up = 0; upBlockedData = true;
                                    }
                                    else if (cap <= 0)
                                    {
                                        // 「最大边 = 不缩放」是明确表态：那就连放大也不做。
                                        up = 0; upBlockedCap = true;
                                    }
                                    else
                                    {
                                        int ceiling = cap < Resample.UpCeiling ? cap : Resample.UpCeiling;
                                        int longEdge = Math.Max(w, h);
                                        while (up > 1 && (longEdge * up) > ceiling) up = (up >= 4) ? 2 : 0;
                                        if (up <= 1) { up = 0; upBlockedCap = true; }
                                    }
                                }
                                double sc = (up > 1)
                                            ? up
                                            : ((cap > 0 && Math.Max(w, h) > cap) ? (double)cap / Math.Max(w, h) : 1.0);
                                int tw = Math.Max(1, (int)Math.Round(w * sc));
                                int th = Math.Max(1, (int)Math.Round(h * sc));
                                // 诊断（KOITEX_UPDEBUG=1）：把放大的决策输入与结果逐张打出来。
                                // 排查"日志说放大了、产物却没变"这类问题时，没有这条只能靠猜。
                                if (Environment.GetEnvironmentVariable("KOITEX_UPDEBUG") == "1")
                                    lg(L.F("  [UPDBG] TexID {0} cls={1} 源={2}x{3} cap={4} up={5} sc={6:0.###} "
                                        + "目标={7}x{8} useJpg={9} fmt={10} grayA={11}",
                                        key, cls, w, h, cap, up, sc, tw, th, useJpg, fmt, grayA));
                                if (up > 1)
                                {
                                    upApplied = true;
                                    j.NUp++;
                                    j.UpPixelsBefore += (long)w * h;
                                    j.UpPixelsAfter += (long)tw * th;
                                    if (j.NUp <= 20) j.UpIds.Add(key.ToString());
                                }
                                else if (upBlockedData) j.NUpBlockedData++;
                                else if (upBlockedCap) j.NUpBlockedCap++;
                                // ---- 纯无损快路径：分辨率不变 + 结果是 PNG 时**完全绕开 GDI+** ----
                                // 为什么必须绕：GDI+ 内部用预乘 alpha，解码→绘制→编码这一圈会让
                                //   · alpha=0 的像素 RGB 被清成 0（实测 198→0）
                                //   · 半透明像素的 RGB 产生 ±1~4 的舍入
                                // 也就是"保持原分辨率 + 重编码"其实**不是无损**。而 PngEnc 自己解 IDAT、
                                // 自己做自适应滤波与 zlib-ng、自己降通道（严格校验 R==G==B），全程不碰 GDI+，
                                // 实测像素逐位不变，还能省 44~49%。所以这里直接对**原字节**做。
                                // v2.33：这里必须是 sc == 1.0（**正好不变**），不能写 sc >= 1.0 ——
                                // 放大时 sc > 1，走这条快路径会把放大请求整个吞掉（尺寸根本不改）。
                                if (!useJpg && fmt == TexFmt.Png && sc == 1.0)
                                {
                                    string lkind;
                                    var swRe = Stopwatch.StartNew();
                                    byte[] lossless = PngEnc.Recompress(raw, grayA, out lkind);
                                    j.TReopt += swRe.Elapsed.TotalMilliseconds;
                                    if (lossless != null && lossless.Length < raw.Length)
                                    {
                                        j.PngPacked++;
                                        j.PngSaved += raw.Length - lossless.Length;
                                        j.NKeepOpt++;
                                        j.KeepOptSaved += raw.Length - lossless.Length;
                                        if (!string.IsNullOrEmpty(lkind))
                                        {
                                            j.NGrayAlpha++;
                                            j.GrayAlphaSaved += raw.Length - lossless.Length;
                                            j.GrayAlphaIds.Add(key.ToString());
                                        }
                                        outp = lossless; st.Fmt = "PNG*";
                                        st.Mode = origMode + (flatAlpha ? "" : " 有alpha") + "（无损优化）";
                                    }
                                    else
                                    {
                                        outp = raw; st.Fmt = "orig"; j.NSame++;
                                        st.Mode = origMode + "（无损优化后没更小，原样）";
                                    }
                                    st.OutBytes = outp.Length;
                                    j.St.OutBytes = outp.Length; j.Out = outp; return j;
                                }
                                bool detail = Resample.Applies(cls);
                                bool premul = Resample.Premul && !Resample.IsDataClass(cls);
                                bool den = !Resample.IsDataClass(cls);      // 降噪只对彩色贴图（数据贴图的"噪声"是内容）
                                // alpha 也锐化：只对数据类贴图，且**alpha 必须是真的数据**。
                                // 实测多数法线的 alpha 是作者工具写的占位常数 127（p5=p95=127），
                                // 锐化它只会放大离群点 —— 体积 +0.37% 而画质无收益，所以那种跳过。
                                bool alphaSharp = Resample.SharpAlphaData && Resample.IsDataClass(cls) && !alphaFlat05
                                                  && Environment.GetEnvironmentVariable("KOITEX_NOALPHASHARP") != "1";
                                Bitmap small = null;
                                var swRs = Stopwatch.StartNew();
                                try
                                {
                                    // v2.33：只有 sc **正好等于 1** 才用原尺寸（走 CopyExact 精确拷贝）。
                                    // 原来的写法是 (sc < 1.0) ? 目标 : 原尺寸 —— 放大时 sc > 1 会落到
                                    // 原尺寸那一支，放大被静默吞掉（尺寸根本没变，还查不出原因）。
                                    small = (sc == 1.0) ? Resize(argb, w, h, gray, flatAlpha, detail, premul, den, alphaSharp,
                                                                shK, shKEdge, shMode)
                                                       : Resize(argb, tw, th, gray, flatAlpha, detail, premul, den, alphaSharp,
                                                                shK, shKEdge, shMode);
                                }
                                finally { j.TResize += swRs.Elapsed.TotalMilliseconds; }
                                using (small)
                                {
                                    var swP = Stopwatch.StartNew();
                                    pngNew = EncodePng(small);
                                    j.TPng += swP.Elapsed.TotalMilliseconds;
                                    var swJ = Stopwatch.StartNew();
                                    if (useJpg)
                                    {
                                        using (var rgb = (small.PixelFormat == PixelFormat.Format24bppRgb || small.PixelFormat == PixelFormat.Format32bppArgb)
                                                         ? small : To24(small))
                                            jpgNew = EncodeJpeg(rgb, quality);
                                    }
                                    j.TJpg += swJ.Elapsed.TotalMilliseconds;
                                }
                                st.Before = w + "x" + h;
                                targetSize = tw + "x" + th;
                                st.After = targetSize;
                            }
                        }

                        // 这里的"无损再优化"跑在降采样那一段之外，所以 grayA 要重新算一遍
                        // （同名变量不能在同一方法的外层再声明，故叫 grayAKeep）。
                        bool grayAKeep = (rule != null && rule.GrayA >= 0) ? rule.GrayA == 1 : PngEnc.UseGrayAlpha;
                        string fmtName = "PNG";
                        byte[] use = pngNew;
                        if (useJpg)
                        {
                            if (autoMode && jpgNew.Length >= pngNew.Length) { use = pngNew; fmtName = "PNG"; }
                            else { use = jpgNew; fmtName = "JPEG"; }
                        }
                        // v2.33：**放大过的贴图不走这条兜底**。
                        // 这条规则的本意是"压缩不该让贴图比原来还大"，对缩小/重编码是正确的；
                        // 但放大后必然比原图大 —— 照这条退回原字节，等于把放大结果原地丢掉，
                        // 而且日志里还写着目标尺寸（st.After 是 tw x th），看起来像"放大了"。
                        // 实测就是这样骗过去的：日志报 2048x2048，产物里其实是原始 1024x1024。
                        if (!upApplied && use.Length >= raw.Length) { use = raw; fmtName = "orig"; j.NSame++; }
                        // 无损再优化 JPEG：最优哈夫曼（像素逐位不变）。同样只在"本来就用我们
                        // 重编码出来的 JPEG"时替换 —— 不改变哪些贴图走 JPEG。
                        if (ReferenceEquals(use, jpgNew) && JpegOpt.Enabled)
                        {
                            var swR2 = Stopwatch.StartNew();
                            byte[] optJ = JpegOpt.Optimize(use);
                            j.TReopt += swR2.Elapsed.TotalMilliseconds;
                            if (optJ != null && optJ.Length < use.Length) use = optJ;
                        }
                        // 无损再压缩 PNG：解 GDI+ 产物的扫描线 → 自适应滤波 + zlib-ng。
                        if (ReferenceEquals(use, pngNew))
                        {
                            string gtag;
                            var swR3 = Stopwatch.StartNew();
                            byte[] altPng = PngEnc.Recompress(pngNew, grayAKeep, out gtag);
                            j.TReopt += swR3.Elapsed.TotalMilliseconds;
                            if (altPng != null && altPng.Length < use.Length)
                            {
                                j.PngPacked++;
                                j.PngSaved += use.Length - altPng.Length;
                                if (gtag.Length > 0) { j.NGrayAlpha++; j.GrayAlphaSaved += use.Length - altPng.Length; j.GrayAlphaIds.Add(key.ToString()); }
                                use = altPng;
                            }
                        }
                        // ---- 关键补漏：**原图本身就比我们重编码更小**的那些贴图，
                        // 以前是逐字节沿用（fmtName="orig"），于是无损重压缩/无损 JPEG 优化**从没跑过**。
                        // 实测这些原图里能再省 6%~48%（个别反而更大 → 所以仍然"只在更小时替换"）。
                        // 两条路都是**像素逐位不变**，所以这一步不引入任何精度损失。
                        if (ReferenceEquals(use, raw))
                        {
                            byte[] opt = null; string kind = null;
                            var swR4 = Stopwatch.StartNew();
                            if (fmt == TexFmt.Png) opt = PngEnc.Recompress(raw, grayAKeep, out kind);
                            else if (fmt == TexFmt.Jpeg && JpegOpt.Enabled) opt = JpegOpt.Optimize(raw);
                            j.TReopt += swR4.Elapsed.TotalMilliseconds;
                            if (opt != null && opt.Length < use.Length)
                            {
                                j.NKeepOpt++;
                                j.KeepOptSaved += use.Length - opt.Length;
                                if (fmt == TexFmt.Png)
                                {
                                    j.PngPacked++;
                                    j.PngSaved += use.Length - opt.Length;
                                    if (kind != null && kind.Length > 0) { j.NGrayAlpha++; j.GrayAlphaSaved += use.Length - opt.Length; j.GrayAlphaIds.Add(key.ToString()); }
                                }
                                use = opt;
                                fmtName = fmt == TexFmt.Png ? "PNG*" : "JPEG*";
                                j.NSame--;                       // 不再是"原样"，字节变了（但像素没变）
                            }
                        }
                        outp = use; st.Fmt = fmtName;
                        st.Mode = origMode + (flatAlpha ? "" : " 有alpha");
                        // 日志的「结果尺寸」必须报**实际写出去**的尺寸：退回原字节时（包括下面那条
                        // "结果不比原图小就退回"的兜底）装进卡里的就是源尺寸。
                        // 不修的话日志会写着目标尺寸（例如 2048x2048），产物里却是 1024x1024 —— 会被骗很久。
                        if (targetSize != null)
                            st.After = ReferenceEquals(use, raw) ? (w + "x" + h) : targetSize;
                    }
                    catch (Exception ex)
                    {
                        // 一张贴图解码失败不能废掉整张卡：原样保留这一张，其余照常压。
                        outp = raw; st.Fmt = "orig"; st.Mode = FmtName(fmt) + " 解码失败，原样";
                        lg(L.F("  [跳过] TexID {0} ({1}, {2} 字节): {3} —— 这一张原样保留，其余继续",
                            key, FmtName(fmt), raw.Length, ex.Message));
                        j.Skipped.Add(L.F("TexID {0} ({1}, {2} 字节): 解码失败 —— {3}", key, FmtName(fmt), raw.Length, ex.Message));
                    }
                }

                j.St.OutBytes = outp.Length;
                j.Out = outp;
                return j;
            }

            if (texN > 1)
                System.Threading.Tasks.Parallel.For(0, total,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = texN },
                    delegate (int i) { jobs[i] = ProcessTex(i); });
            else
                for (int i = 0; i < total; i++) jobs[i] = ProcessTex(i);

            if (cancelled) { log("已取消，未写出文件。"); res.Rc = 5; res.Reason = RepackResult.RcText(5); return res; }

            // ---- 串行收尾：按原顺序写 msgpack、合并计数、按原顺序打日志 ----
            int done = 0;
            for (int i = 0; i < total; i++)
            {
                var j = jobs[i];
                done++;
                if (progress != null) progress(done, total);
                if (j == null) continue;
                foreach (var ln in j.Lines) log(ln);
                var kb = MP.Int((long)j.Key);
                inner.Write(kb, 0, kb.Length);
                var bb = MP.Bin32(j.Out);
                inner.Write(bb, 0, bb.Length);
                res.Stats.Add(j.St);
                if (j.St.Fmt == "JPEG") res.NJpg++;
                else if (j.St.Fmt == "PNG") res.NPng++;
                else if (j.St.Fmt == "HDR") res.NHdr++;
                else if (j.St.Fmt == "keep") res.NKeep++;
                res.NSame += j.NSame; res.PngPacked += j.PngPacked; res.PngSaved += j.PngSaved;
                res.NKeepOpt += j.NKeepOpt; res.KeepOptSaved += j.KeepOptSaved;
                res.NOwnProtected += j.NOwnProtected;
    res.NShaderJpeg += j.NShaderJpeg;
    res.NForcedAlphaLost += j.NForcedAlphaLost;
                res.NGrayAlpha += j.NGrayAlpha; res.GrayAlphaSaved += j.GrayAlphaSaved;
                foreach (var g in j.GrayAlphaIds) res.GrayAlphaIds.Add(g);
                // v2.33 放大
                res.NUp += j.NUp; res.NUpBlockedData += j.NUpBlockedData; res.NUpBlockedCap += j.NUpBlockedCap;
                res.UpPixelsBefore += j.UpPixelsBefore; res.UpPixelsAfter += j.UpPixelsAfter;
                foreach (var u in j.UpIds) res.UpIds.Add(u);
                foreach (var s in j.Skipped) res.Skipped.Add(s);
                res.PresetMatched += j.PresetMatched;
                if (usedPresetTex != null) foreach (var u in j.UsedTex) usedPresetTex.Add(u);
                res.TDecode += j.TDecode; res.TResize += j.TResize; res.TPng += j.TPng;
                res.TJpg += j.TJpg; res.TReopt += j.TReopt;
            }

            var swPack = Stopwatch.StartNew();
            byte[] newBin = inner.ToArray();
            var outBytes = new byte[end + (bin.Off - 5 - end) + 5 + newBin.Length + (b.Length - (bin.Off + bin.Len))];
            int w0 = 0;
            Buffer.BlockCopy(b, 0, outBytes, w0, end); w0 += end;                       // thumbnail PNG
            Buffer.BlockCopy(b, end, outBytes, w0, bin.Off - 5 - end); w0 += bin.Off - 5 - end;
            var bh = MP.Bin32Header(newBin.Length);            Buffer.BlockCopy(bh, 0, outBytes, w0, 5); w0 += 5;
            Buffer.BlockCopy(newBin, 0, outBytes, w0, newBin.Length); w0 += newBin.Length;
            Buffer.BlockCopy(b, bin.Off + bin.Len, outBytes, w0, b.Length - (bin.Off + bin.Len));
            File.WriteAllBytes(dst, outBytes);
            res.TPack += swPack.Elapsed.TotalMilliseconds;

            log(string.Format("{0,-10} {1,-9} {2,-11} {3,-11} {4,11} {5,11}  {6,-6} {7,-20} {8}",
                "id", "class", "before", "after", "bytes(in)", "bytes(out)", "fmt", "mode", "bound properties"));
            foreach (var s in res.Stats)
                log(string.Format("{0,-10} {1,-9} {2,-11} {3,-11} {4,11} {5,11}  {6,-6} {7,-20} {8}",
                    s.Id, s.Cls, s.Before, s.After, s.InBytes, s.OutBytes, s.Fmt, s.Mode,
                    s.Props.Length > 56 ? s.Props.Substring(0, 56) : s.Props));
            res.NTex = res.Stats.Count;
            res.PayloadAfter = newBin.Length;
            res.CardAfter = outBytes.Length;
            if (preset != null && preset.Textures.Count > 0)
            {
                // 预设里哪些单贴图规则在本卡没落地（换版本/换材质时会有）——不静默吞掉
                foreach (var pt in preset.Textures)
                {
                    bool hit = usedPresetTex.Contains(pt.Id);
                    if (!hit && texSigs != null)
                        foreach (var s in pt.Sigs)
                            foreach (var kv in texSigs)
                                if (kv.Value.Contains(s)) { hit = true; break; }
                    if (!hit) res.PresetMissed.Add("TexID " + pt.Id + "（" + string.Join("；", pt.Sigs.ToArray()) + "）");
                }
                log(L.F("预设：命中 {0} 张单贴图规则；未匹配 {1} 张{2}",
                    res.PresetMatched, res.PresetMissed.Count,
                    res.PresetMissed.Count > 0 ? " —— " + string.Join(" / ", res.PresetMissed.ToArray()) : ""));
            }
            log(L.F("贴图 {0} 张：JPEG {1} / PNG {2} / HDR {3} / 原样 {4}（q{5}）",
                res.NTex, res.NJpg, res.NPng, res.NHdr, res.NKeep, quality));
            if (res.NSame > 0)
                log(L.F("  （另有 {0} 张按规则重编码了，但没比原字节更小 → 保留原字节，卡片不会变大）", res.NSame));
            if (res.PngPacked > 0)
                log(L.F("  （其中 {0} 张 PNG 换用自写编码器：自适应滤波 + zlib-ng，无损再省 {1}）",
                    res.PngPacked, Human(res.PngSaved)));
            if (res.NOwnProtected > 0)
                log(L.F("  （其中 {0} 张是「独占贴图」（只被一个部位引用），已按 {1} 的放宽上限处理）",
                    res.NOwnProtected, ProtectOwnMax > 0 ? ProtectOwnMax + "px" : "原尺寸"));
            if (res.NKeepOpt > 0)
                log(L.F("  （另有 {0} 张走「不经 GDI+ 的纯无损路径」：直接对原字节做无损再压缩+降通道，省 {1}，像素逐位不变）",
                    res.NKeepOpt, Human(res.KeepOptSaved)));
            int dAreaU = Resample.AreaUsed - cAreaU, dSharpU = Resample.SharpUsed - cSharpU;
            int dDen = Resample.Denoised - cDen, dDirt = Resample.DirtSeen - cDirt;
            int dJpU = JpegOpt.Used - cJpU; long dJpS = JpegOpt.Saved - cJpS;
            if (Resample.Denoise != "off")
                log(L.F("  （降噪模式 {0}：命中脏源 {1} 张，实际降噪 {2} 张）",
                    Resample.Denoise, dDirt, dDen));
            if (dAreaU > 0)
                log(L.F("  （其中 {0} 张数据贴图走了面积平均{1}）",
                    dAreaU, dSharpU > 0 ? " + 低分辨率域轻度锐化" : ""));
            if (dJpU > 0)
                log(L.F("  （其中 {0} 张 JPEG 做了无损哈夫曼再优化，省 {1}，像素逐位不变）",
                    dJpU, Human(dJpS)));
            if (res.NGrayAlpha > 0)
                log(L.F("  （另有 {0} 张是灰度/灰度+alpha，改成 2/1 通道无损存储，省 {1}：TexID {2}）",
                    res.NGrayAlpha, Human(res.GrayAlphaSaved), string.Join(", ", res.GrayAlphaIds.ToArray())));
            if (res.NUp > 0)
                log(L.F("  【放大】{0} 张真放大了（{1} 核，像素 {2} -> {3}，{4:0.0} 倍）：TexID {5}",
                    res.NUp, Resample.UpKernel, res.UpPixelsBefore, res.UpPixelsAfter,
                    (double)res.UpPixelsAfter / Math.Max(1, res.UpPixelsBefore),
                    string.Join(", ", res.UpIds.ToArray())));
            if (res.NUpBlockedData > 0 || res.NUpBlockedCap > 0)
                log(L.F("  【放大】被挡下 {0} 张（数据贴图 {1} 张——法线/掩罩/高度放大没有意义且体积 ×3；"
                    + "上限装不下 {2} 张——{3}）",
                    res.NUpBlockedData + res.NUpBlockedCap, res.NUpBlockedData, res.NUpBlockedCap,
                    Resample.UpCeiling > 0 ? "放大会越过「最大边」或 " + Resample.UpCeiling + "px 硬上限"
                                          : "放大会越过「最大边」"));
            log(L.F("贴图载荷: {0} -> {1} 字节（{2:0.00} MB -> {3:0.00} MB）",
                res.PayloadBefore, res.PayloadAfter, res.PayloadBefore / 1048576.0, res.PayloadAfter / 1048576.0));
            log(L.F("卡片: {0} -> {1} 字节（{2:0.0}%）", res.CardBefore, res.CardAfter,
                100.0 * res.CardAfter / Math.Max(1, res.CardBefore)));
            if (res.NShaderJpeg > 0)
                log(L.F("  [测试] 强制转 JPEG {0} 张，其中 alpha 非平坦（**丢了 alpha**）{1} 张",
                    res.NShaderJpeg, res.NForcedAlphaLost));
            if (Environment.GetEnvironmentVariable("KOITEX_TIME") == "1")
            {
                double tot = res.TDecode + res.TResize + res.TPng + res.TJpg + res.TReopt;
                log("  [耗时] 解码+分析 " + res.TDecode.ToString("0") + "ms / 重采样 " + res.TResize.ToString("0")
                    + "ms / PNG编码 " + res.TPng.ToString("0") + "ms / JPEG编码 " + res.TJpg.ToString("0")
                    + "ms / 无损再优化 " + res.TReopt.ToString("0") + "ms / 打包 " + res.TPack.ToString("0")
                    + "ms ｜ 合计 " + tot.ToString("0") + "ms（贴图 " + res.NTex + " 张）");
                log("  [耗时·PNG内部] 解压 " + PngEnc.TInflate.ToString("0") + "ms / 反滤波+自适应滤波 "
                    + PngEnc.TFilter.ToString("0") + "ms / deflate " + PngEnc.TDeflate.ToString("0")
                    + "ms / 其余 " + (res.TReopt - PngEnc.TInflate - PngEnc.TFilter - PngEnc.TDeflate).ToString("0")
                    + "ms ｜ Recompress " + PngEnc.NRecomp + " 次，Pack " + PngEnc.NPack + " 次");
                log("  [耗时·PNG量] 送 deflate 扫描线 " + (PngEnc.TDeflateBytes / 1048576.0).ToString("0.0")
                    + " MB；灰度优先救回 " + PngEnc.GraySkippedColor + " 次（省掉整轮彩色版滤波+deflate）");
            }
            res.Rc = 0;
            return res;
        }

        /// <summary>把 HDR 贴图按线性空间缩到上限内并重新编码成 RGBE。
        /// 返回 null 表示不改（尺寸已经够小 / 编出来反而更大 / 头部异常），调用方原样保留。</summary>
        static byte[] ReduceHdr(byte[] raw, Rule rule, string mode, int maxSize, int maskSize, Action<string> log, ref TexStat st)
        {
            int pw, ph;
            if (!Hdr.PeekSize(raw, out pw, out ph))
            {
                log("  [跳过] TexID " + st.Id + ": HDR 头部异常，原样保留");
                return null;
            }
            st.Before = pw + "x" + ph;
            st.After = pw + "x" + ph;
            int cap = rule != null ? rule.Size : (mode == "png" ? maskSize : maxSize);
            if (cap <= 0 || Math.Max(pw, ph) <= cap)
            {
                log(L.F("  [HDR] TexID {0}: {1}x{2} 未超过上限 {3}，原样保留（{4} 字节）",
                    st.Id, pw, ph, cap <= 0 ? "不限" : cap.ToString(), raw.Length));
                return null;
            }
            double sc = (double)cap / Math.Max(pw, ph);
            int nw = Math.Max(1, (int)Math.Round(pw * sc)), nh = Math.Max(1, (int)Math.Round(ph * sc));
            try
            {
                var im = Hdr.Decode(raw);
                var sm = Hdr.ResizeBox(im, nw, nh);
                var enc = Hdr.Encode(sm);
                st.After = nw + "x" + nh;
                if (enc.Length >= raw.Length)
                {
                    st.After = pw + "x" + ph;
                    log(L.F("  [HDR] TexID {0}: 重新编码后没有更小（{1} → {2} 字节），原样保留",
                        st.Id, raw.Length, enc.Length));
                    return null;
                }
                log(L.F("  [HDR] TexID {0}: {1}x{2} → {3}x{4}，{5} → {6} 字节（线性空间盒式缩放，格式仍是 HDR）",
                    st.Id, pw, ph, nw, nh, raw.Length, enc.Length));
                return enc;
            }
            catch (Exception ex)
            {
                st.After = pw + "x" + ph;
                log(L.F("  [跳过] TexID {0}: HDR 处理失败（{1}），原样保留", st.Id, ex.Message));
                return null;
            }
        }

        static Bitmap To24(Bitmap src)
        {
            var o = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(o))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return o;
        }

        static bool Risky(List<string> pl)
        {
            foreach (var p in pl)
            {
                if (p == null) continue;
                if (p.Contains("Bump") || p.Contains("Mask") || p.Contains("Normal")) return true;
            }
            return false;
        }

        static string Desc(PixelFormat pf)
        {
            if ((pf & PixelFormat.Alpha) != 0) return "ARGB";
            if (pf == PixelFormat.Format8bppIndexed || pf == PixelFormat.Format4bppIndexed || pf == PixelFormat.Format1bppIndexed) return "P";
            if (pf == PixelFormat.Format24bppRgb) return "RGB";
            return pf.ToString().Replace("Format", "");
        }

        /// <summary>一张待处理卡片：Src = 完整路径，Rel = 相对输入根的子目录（"" = 顶层）。
        /// Rel 用来把输出按同样的目录结构摆回去。</summary>
        public class CardFile
        {
            public string Src;
            public string Rel;
        }

        /// <summary>输出目录标记文件名：递归读取时，见到这个文件就认定"这是我上次的输出目录"，跳过。
        /// 光靠目录名（[zip]）只能认出默认命名；用户自定义的 out_dir 名字千奇百怪，必须留标记。</summary>
        public const string OutMarker = ".koicardtex-outdir";

        /// <summary>在输出目录里放标记（失败就算了，不影响压缩）。</summary>
        public static void MarkOut(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, OutMarker);
                if (!File.Exists(p))
                    File.WriteAllText(p, "这个目录是 KoiCardTexTool 的输出目录。\r\n"
                        + "递归读取时会跳过它，避免把压缩过的卡再压一遍。\r\n", new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>收集 root 下的 .png（递归）。会跳过两类目录，防止把自己的产出再压一遍：
        /// ① outDir 子树（默认输出就在输入文件夹里面，不排除会无限套娃）；
        /// ② 带输出标记的目录，以及任何以 [zip] 结尾的目录（本工具的输出目录命名约定）。</summary>
        public static List<CardFile> Enumerate(string root, string outDir, bool recurse)
        {
            var list = new List<CardFile>();
            if (!Directory.Exists(root)) return list;
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string ex = string.IsNullOrEmpty(outDir)
                ? null : Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WalkDir(rootFull, rootFull, ex, recurse, list);
            list.Sort(delegate (CardFile x, CardFile y)
            {
                return string.Compare(Path.Combine(x.Rel, Path.GetFileName(x.Src)),
                                      Path.Combine(y.Rel, Path.GetFileName(y.Src)),
                                      StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static void WalkDir(string rootFull, string dir, string exclude, bool recurse, List<CardFile> list)
        {
            string[] fs;
            try { fs = Directory.GetFiles(dir, "*.png"); } catch { fs = new string[0]; }
            foreach (var f in fs)
            {
                string d = Path.GetDirectoryName(f) ?? rootFull;
                string rel = d.Length > rootFull.Length
                    ? d.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : "";
                list.Add(new CardFile { Src = f, Rel = rel });
            }
            if (!recurse) return;
            string[] ds;
            try { ds = Directory.GetDirectories(dir); } catch { return; }
            foreach (var d in ds)
            {
                if (Path.GetFileName(d).EndsWith("[zip]", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(d, OutMarker))) continue;      // 上次跑出来的输出目录
                string full = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (exclude != null &&
                    (string.Equals(full, exclude, StringComparison.OrdinalIgnoreCase) ||
                     full.StartsWith(exclude + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    continue;
                WalkDir(rootFull, d, exclude, recurse, list);
            }
        }

        /// <summary>输入文件夹 X → 输出目录 ``X\X[zip]``（结果留在源文件夹内部，源文件夹本身不乱）。
        /// 输入是文件时退回 ``父目录\压缩输出``。CLI 与 GUI 共用，保证两边默认值一致。</summary>
        public static string DefaultOut(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string name = Path.GetFileName(inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.Length == 0) name = "cards";       // 输入是 D:\ 这种根
                return Path.Combine(inputPath, name + "[zip]");
            }
            string parent = Path.GetDirectoryName(inputPath);
            return Path.Combine(parent ?? ".", "compressed");   // v1.0：默认输出目录改成英文
        }

        /// <summary>统计出现了多少个不同的键（组·部位）。</summary>
        static int CountKeys(Dictionary<object, List<int>> d)
        {
            var s = new HashSet<int>();
            foreach (var kv in d) foreach (var k in kv.Value) s.Add(k);
            return s.Count;
        }

        /// <summary>汇总块：失败/跳过清单交给 CLI 与 GUI 共用，保证两边日志措辞一致。
        /// failed / skipped / partial 里的每一条都是「文件名 —— 原因」或「文件名: 贴图原因」。</summary>
        public static void PrintSummary(Action<string> log, int nOk, int nSkip, int nFail,
                                        long bytesBefore, long bytesAfter,
                                        List<string> skipped, List<string> failed, List<string> partial)
        {
            if (log == null) log = delegate (string s) { };
            log("");
            log("================ 汇总 ================");
            log(L.F("成功 {0} 张，跳过 {1} 张，失败 {2} 张", nOk, nSkip, nFail));
            if (nOk > 0)
                log(L.F("成功部分的体积：{0} -> {1} 字节（{2:0.0}%）",
                    bytesBefore, bytesAfter, 100.0 * bytesAfter / Math.Max(1, bytesBefore)));
            if (failed != null && failed.Count > 0)
            {
                log(L.F("失败清单（{0} 张）：", failed.Count));
                foreach (var s in failed) log("  [X] " + s);
            }
            if (skipped != null && skipped.Count > 0)
            {
                log(L.F("跳过清单（{0} 张，卡里本来就没有可压的贴图，不算失败）：", skipped.Count));
                foreach (var s in skipped) log("  [跳过] " + s);
            }
            if (partial != null && partial.Count > 0)
            {
                log(L.F("贴图级跳过（{0} 处：这些贴图原样保留，所属卡片本身压缩成功）：", partial.Count));
                foreach (var s in partial) log("  [跳过] " + s);
            }
            if ((failed == null || failed.Count == 0) && (skipped == null || skipped.Count == 0)
                && (partial == null || partial.Count == 0))
                log("没有失败，也没有跳过。");
            log("=====================================");
        }

        /// <summary>Structural check of a written card (mirrors the Python quick_verify).</summary>
        public static bool QuickVerify(string path, out string msg)
        {
            msg = "";
            byte[] b = ReadAll(path);
            if (Card.PngEnd(b) < 0) { msg = "没有 IEND"; return false; }
            int kpos = Card.FindKeyCached(b, "TextureDictionary");
            if (kpos < 0) { msg = "无贴图字典"; return true; }
            object holder = MP.Read(b, ref kpos);
            var br = holder as BinRef;
            if (br == null) { msg = "字典结构异常"; return false; }
            int p = br.Off;
            var dic = MP.Read(b, ref p) as MMap;
            if (dic == null) { msg = "字典不是 map"; return false; }
            int n = 0, nHdr = 0, nOther = 0;
            for (int i = 0; i < dic.Keys.Count; i++)
            {
                var v = dic.Vals[i] as BinRef;
                if (v == null) continue;
                var raw = v.Bytes();
                var fmt = Sniff(raw);
                if (fmt == TexFmt.Png || fmt == TexFmt.Jpeg)
                {
                    try { using (var im = Image.FromStream(new MemoryStream(raw, false), false, false)) { var _ = im.Width; } }
                    catch (Exception ex) { msg = "TexID " + dic.Keys[i] + " 无法解码: " + ex.Message; return false; }
                }
                else if (fmt == TexFmt.Hdr)
                {
                    // HDR 走自己的解码器整张解一遍，能解通才算这一步通过
                    try { var im = Hdr.Decode(raw); if (im.W <= 0 || im.H <= 0) { msg = "TexID " + dic.Keys[i] + " HDR 尺寸异常"; return false; } }
                    catch (Exception ex) { msg = "TexID " + dic.Keys[i] + " HDR 解码失败: " + ex.Message; return false; }
                    nHdr++;
                }
                else nOther++;                       // EXR/其它：不认识的格式只做存在性检查，不动它
                n++;
            }
            msg = n + " 张贴图可解码";
            if (nHdr > 0) msg += "（HDR " + nHdr + " 张按 RGBE 整张解码校验）";
            if (nOther > 0) msg += "；另有 " + nOther + " 张为不认识的格式（未改动）";
            return true;
        }
    }
}

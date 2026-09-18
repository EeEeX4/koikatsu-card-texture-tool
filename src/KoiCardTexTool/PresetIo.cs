using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace KoiCardTexTool
{
    /// <summary>一条预设（预设框里的一行）。</summary>
    sealed class PresetEntry
    {
        public string Path;          // 文件全路径；null = 「不使用预设」占位项
        public string Name;          // 自定义名（文件名里的那一段）
        public string Source = "";   // 存的时候用的原卡文件名
        public string Kind = "";     // batch / coordinate / chara
        public string Saved = "";
        public Image Cover;          // 卡面缩略图（24~48px），可能为 null
        public bool HasPayload;      // .png 且带内嵌 JSON

        public override string ToString() { return Name; }
    }

    /// <summary>预设文件的读写。一个预设 = **原服装卡的卡面 PNG** + **IEND 之后内嵌的 JSON**：
    ///
    ///   ┌─ PNG（原卡缩略图，IEND 结束）─┬─ "KoiCardTexToolPreset1" ─┬─ int32 长度 ─┬─ UTF-8 JSON ─┐
    ///
    /// 这样在资源管理器里看到的就是原卡卡面（不用记文件名），工具又能把整份设置读回来；
    /// 也仍然兼容老的 .json 预设（纯文本 JSON）。
    /// 目录：&lt;exe 所在文件夹&gt;\preset\{batch|coordinate|chara}\</summary>
    static class PresetIo
    {
        public const string Marker = "KoiCardTexToolPreset1";
        public const string KindBatch = "batch";
        public const string KindCoord = "coordinate";
        public const string KindChara = "chara";

        /// <summary>预设目录（按用途分子目录）。</summary>
        public static string Dir(string kind)
        {
            string b = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(b))
            {
                try { b = Path.GetDirectoryName(typeof(PresetIo).Assembly.Location); } catch { }
            }
            if (string.IsNullOrEmpty(b)) b = ".";
            return Path.Combine(Path.Combine(b, "preset"), kind);
        }

        public static string EnsureDir(string kind)
        {
            string d = Dir(kind);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        // ---- v2.23：「把当前预设设为默认」----------------------------------------
        // 标记就是一个文件：preset\<kind>\.default，内容是预设的**文件名**（不含路径）。
        // 不写注册表、不写用户目录 —— 预设目录拷到哪里，默认设置就跟到哪里。
        const string DefaultMarker = ".default";

        static string DefaultMarkerPath(string kind) { return Path.Combine(Dir(kind), DefaultMarker); }

        /// <summary>读「默认预设」标记：返回预设文件名（不是全路径）；没有/读不到返回 null。</summary>
        public static string GetDefaultPreset(string kind)
        {
            try
            {
                string p = DefaultMarkerPath(kind);
                if (!File.Exists(p)) return null;
                string s = File.ReadAllText(p, Encoding.UTF8).Trim();
                if (s.Length == 0) return null;
                // 标记里存的是文件名；对应文件已经不在就当作没设
                if (!File.Exists(Path.Combine(Dir(kind), s))) return null;
                return s;
            }
            catch { return null; }
        }

        /// <summary>设/清「默认预设」。pathOrFile 传 null/空 = 清除。</summary>
        public static void SetDefaultPreset(string kind, string pathOrFile)
        {
            try
            {
                string p = DefaultMarkerPath(kind);
                if (string.IsNullOrEmpty(pathOrFile)) { if (File.Exists(p)) File.Delete(p); return; }
                EnsureDir(kind);
                File.WriteAllText(p, Path.GetFileName(pathOrFile), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>第一次运行时给 preset\batch 放三个内置基础预设：
        ///   「仅优化」= 全部类 自动 + 原尺寸（**不降分辨率**，只吃无损优化）→ **默认预设**
        ///   「质量」  = 主贴图 自动/2048，其余 自动/1024；独占贴图放宽到 4096
        ///   「性能」  = 主贴图 自动/1024，其余 PNG/1024；彩色贴图也走面积平均+边缘感知锐化；独占贴图放宽到 2048
        /// 只在目录里一个预设都没有的时候写；删掉就不会再生成。批量页用纯 JSON。</summary>
        public static void EnsureDefaults()
        {
            string d = EnsureDir(KindBatch);
            try
            {
                if (Directory.GetFiles(d, "*.png").Length > 0 || Directory.GetFiles(d, "*.json").Length > 0) return;
            }
            catch { return; }

            // 仅优化：全部类 自动 + 原尺寸（size=0 → 不设上限，不减分辨率；无损优化照常生效）
            var pOpt = new Dictionary<string, Rule>();
            foreach (var c in Classes.Order) pOpt[c] = new Rule("auto", 0);

            // 质量：主贴图 2048，其余 1024（都自动）
            var pQual = new Dictionary<string, Rule>();
            foreach (var c in Classes.Order) pQual[c] = new Rule("auto", 1024);
            pQual["maintex"] = new Rule("auto", 2048);


            // v2.32：三个内置预设的 colorArea 全部改成 true，跟新的代码默认值一致。
            // 依据：全语料单变量 A/B（配置就是「质量」这一档：maintex 2048 / 其余 1024 / 90）
            // 彩色类改面积平均后，15 张被改的贴图 SSIM 全部上升、体积合计 −4.1%（整卡 −0.30%）。
            // 「性能」本来就是 true；「仅优化」全是原尺寸、根本不降分辨率，这个字段对它是空转，
            // 但保持三个预设一致，免得以后有人改了尺寸才发现它写的是旧值。

            WriteJson(Path.Combine(d, FileName("仅优化", "内置默认.png", "json")),
                      Envelope(KindBatch, "仅优化", "(内置默认)", "clothes", 0.5, null, null, null, null, pOpt, 90, true,
                               true, "", true, 0));
            WriteJson(Path.Combine(d, FileName("质量", "内置默认.png", "json")),
                      Envelope(KindBatch, "质量", "(内置默认)", "clothes", 0.5, null, null, null, null, pQual, 90, true,
                               true, "", true, 4096));
            // v2.38：性能 / 性能（激进）按用户实测的那两套值写（auto 格式、主贴图 1024 / 其余 512）
            var pFast = new Dictionary<string, Rule>();
            foreach (var c in Classes.Order) pFast[c] = new Rule("auto", 512);
            pFast["maintex"] = new Rule("auto", 1024);

            WriteJson(Path.Combine(d, FileName("性能", "内置默认.png", "json")),
                      Envelope(KindBatch, "性能", "(内置默认)", "clothes", 0.5, null, null, null, null, pFast, 90, true,
                               true, "edge", true, 2048, 150, "cas"));
            WriteJson(Path.Combine(d, FileName("性能（激进）", "内置默认.png", "json")),
                      Envelope(KindBatch, "性能（激进）", "(内置默认)", "clothes", 0.5, null, null, null, null, pFast, 90, true,
                               true, "edge", false, 2048, 200, "cas"));

            // v2.39：新装的默认预设 = 「质量」（用户指定）。写的是「把当前预设设为默认」用的那个标记文件，
            // 所以用户之后在界面上改默认时会照常覆盖它。
            SetDefaultPreset(KindBatch, FileName("质量", "内置默认.png", "json"));
        }

        /// <summary>纯 JSON 预设（批量页用）：UTF-8 无 BOM，人可读可手改。</summary>
        public static void WriteJson(string path, string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json ?? "{}", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                throw new IOException("预设存不下来：\n" + path + "\n" + ex.Message +
                    "\n\n（预设固定放在 EXE 同级的 preset 目录里；如果 EXE 放在只读目录/压缩包里，" +
                    "把整个文件夹拷到可写的位置再试。）");
            }
        }

        /// <summary>预设文件名 = [preset] + 自定义名 + _ + 原卡名（批量页是 .json，其余是卡面 .png）。</summary>
        public static string FileName(string custom, string cardPathOrName, string ext = "png")
        {
            string card = Path.GetFileName(cardPathOrName ?? "");
            if (card.Length == 0) card = "card.png";
            string stem = Path.GetFileNameWithoutExtension(card);
            string nm = Sanitize(custom);
            if (nm.Length == 0) nm = "preset";
            return "[preset]" + nm + "_" + stem + "." + ext;
        }

        public static string Sanitize(string s)
        {
            string nm = (s ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) nm = nm.Replace(c, '_');
            return nm;
        }

        // ---- 读 ----

        /// <summary>预设文件里的 JSON 文本（.png 走内嵌载荷；.json 直接读）。读不出返回 null。</summary>
        public static string ReadJson(string path)
        {
            try
            {
                byte[] head = ReadHead(path, 32 * 1024 * 1024);
                int json = PayloadOffset(head);
                if (json > 0)
                {
                    int len = BitConverter.ToInt32(head, json - 4);
                    if (len <= 0) return null;
                    if (json + len <= head.Length) return Encoding.UTF8.GetString(head, json, len);
                    // 载荷比读到的头还长：按长度重新整份读
                    var all = File.ReadAllBytes(path);
                    int off = PayloadOffset(all);
                    if (off <= 0) return null;
                    len = BitConverter.ToInt32(all, off - 4);
                    if (len <= 0 || off + len > all.Length) return null;
                    return Encoding.UTF8.GetString(all, off, len);
                }
                return File.ReadAllText(path, Encoding.UTF8);      // 老式 .json 预设
            }
            catch { return null; }
        }

        /// <summary>文件里内嵌 JSON 的起始偏移；没有则 -1。只在头部里找标记，够用且不慢。</summary>
        static int PayloadOffset(byte[] b)
        {
            int end = 0;
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50) end = Card.PngEnd(b);
            int from = end > 0 ? end : 0;
            var mk = Encoding.ASCII.GetBytes(Marker);
            for (int i = from; i + mk.Length + 4 <= b.Length; i++)
            {
                if (b[i] != mk[0]) continue;
                bool ok = true;
                for (int k = 1; k < mk.Length; k++) if (b[i + k] != mk[k]) { ok = false; break; }
                if (!ok) continue;
                int off = i + mk.Length + 4;                 // 跳过标记 + int32 长度
                int len = BitConverter.ToInt32(b, i + mk.Length);
                if (len > 0 && off + len <= b.Length) return off;   // 长度对得上才认
            }
            return -1;
        }

        static byte[] ReadHead(string path, int max)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n = (int)Math.Min(max, fs.Length);
                var buf = new byte[n];
                int got = 0;
                while (got < n)
                {
                    int r = fs.Read(buf, got, n - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got == n) return buf;
                var small = new byte[got];
                Array.Copy(buf, small, got);
                return small;
            }
        }

        /// <summary>卡面缩略图（等比缩到 maxPx）；没有就返回 null。</summary>
        public static Image Cover(string path, int maxPx)
        {
            try
            {
                byte[] head = ReadHead(path, 16 * 1024 * 1024);
                int end = (head.Length > 8 && head[0] == 0x89 && head[1] == 0x50) ? Card.PngEnd(head) : -1;
                if (end <= 0)
                {
                    var all = File.ReadAllBytes(path);
                    end = (all.Length > 8 && all[0] == 0x89 && all[1] == 0x50) ? Card.PngEnd(all) : -1;
                    if (end <= 0) return null;
                    head = all;
                }
                using (var ms = new MemoryStream(head, 0, end))
                using (var img = Image.FromStream(ms))
                {
                    double sc = Math.Min(1.0, (double)maxPx / Math.Max(img.Width, img.Height));
                    int w = Math.Max(1, (int)Math.Round(img.Width * sc)), h = Math.Max(1, (int)Math.Round(img.Height * sc));
                    var small = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, w, h);
                    }
                    return small;
                }
            }
            catch { return null; }
        }

        /// <summary>列出一个预设目录里的所有预设（.png 带内嵌 JSON 的 + 老的 .json），按名字排序。</summary>
        public static List<PresetEntry> List(string kind, int coverPx)
        {
            var list = new List<PresetEntry>();
            string dir = Dir(kind);
            if (!Directory.Exists(dir)) return list;
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(dir, "*.png"));
                files.AddRange(Directory.GetFiles(dir, "*.json"));
            }
            catch { return list; }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                bool png = f.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                string json = ReadJson(f);                       // .png 读内嵌载荷；.json 直接读文本
                if (png && json == null) continue;               // png 但没有内嵌载荷：不是预设，跳过
                var e = new PresetEntry { Path = f, HasPayload = png };
                e.Name = Path.GetFileNameWithoutExtension(f);
                if (e.Name.StartsWith("[preset]", StringComparison.OrdinalIgnoreCase)) e.Name = e.Name.Substring(8);
                e.Kind = kind;
                if (json != null)
                {
                    e.Source = JsonField(json, "source");
                    e.Saved = JsonField(json, "saved");
                    string nm = JsonField(json, "name");
                    if (!string.IsNullOrEmpty(nm)) e.Name = nm;    // 文件被改名也不影响显示名
                }
                if (png) e.Cover = Cover(f, coverPx);
                list.Add(e);
            }
            return list;
        }

        /// <summary>从 JSON 文本里取一个顶层字符串字段（不引第三方库，够用即可）。</summary>
        public static string JsonField(string json, string key)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    System.Text.Json.JsonElement v;
                    if (doc.RootElement.TryGetProperty(key, out v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                        return v.GetString();
                }
            }
            catch { }
            return "";
        }

        // ---- 写 ----

        /// <summary>把 JSON 存成预设：卡面取自 coverCardPath 的 PNG 段（取不到就写一张占位图）。</summary>
        public static void Save(string path, string json, string coverCardPath)
        {
            byte[] png = CoverPngBytes(coverCardPath);
            byte[] body = Encoding.UTF8.GetBytes(json ?? "{}");
            var mk = Encoding.ASCII.GetBytes(Marker);
            var len = BitConverter.GetBytes(body.Length);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(png, 0, png.Length);
                    fs.Write(mk, 0, mk.Length);
                    fs.Write(len, 0, len.Length);
                    fs.Write(body, 0, body.Length);
                }
            }
            catch (Exception ex)
            {
                throw new IOException("预设存不下来：\n" + path + "\n" + ex.Message +
                    "\n\n（预设固定放在 EXE 同级的 preset 目录里；如果 EXE 放在只读目录/压缩包里，" +
                    "把整个文件夹拷到可写的位置再试。）");
            }
        }

        /// <summary>卡片的 PNG 段（缩略图），失败则给一张 1x1 透明 PNG。</summary>
        static byte[] CoverPngBytes(string cardPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(cardPath) && File.Exists(cardPath))
                {
                    byte[] head = ReadHead(cardPath, 16 * 1024 * 1024);
                    int end = (head.Length > 8 && head[0] == 0x89 && head[1] == 0x50) ? Card.PngEnd(head) : -1;
                    if (end > 0)
                    {
                        var png = new byte[end];
                        Array.Copy(head, png, end);
                        return png;
                    }
                }
            }
            catch { }
            return BlankPng();
        }

        static byte[] BlankPng()
        {
            using (var bm = new Bitmap(64, 64))
            using (var ms = new MemoryStream())
            {
                using (var g = Graphics.FromImage(bm)) { g.Clear(Color.FromArgb(235, 235, 240)); }
                bm.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>预设 JSON 的信封：既带 Preset 的字段（TexTool.PresetLoad 能读），
        /// 又带批量页需要的类型表 / 质量 / 保护 alpha。</summary>
        public static string Envelope(string kind, string name, string source, string cardType,
                                      double minMatch, Rule uniform,
                                      IEnumerable<string> sigs,
                                      IDictionary<int, Rule> parts,
                                      IEnumerable<TexTool.PresetTex> textures,
                                      Dictionary<string, Rule> plan,
                                      int quality, bool protectAlpha)
        {
            return Envelope(kind, name, source, cardType, minMatch, uniform, sigs, parts, textures, plan,
                            quality, protectAlpha, false, "");
        }

        /// <summary>带处理开关的版本：colorArea=彩色贴图也走面积平均+边缘感知锐化；sharpen 见 Resample（"edge" 或数值）。
        /// ownProtect/ownMax = 「独占贴图保护」的开关与放宽上限（0 = 完全不降分辨率）。</summary>
        public static string Envelope(string kind, string name, string source, string cardType,
                                      double minMatch, Rule uniform,
                                      IEnumerable<string> sigs,
                                      IDictionary<int, Rule> parts,
                                      IEnumerable<TexTool.PresetTex> textures,
                                      Dictionary<string, Rule> plan,
                                      int quality, bool protectAlpha,
                                      bool colorArea, string sharpen)
        {
            return Envelope(kind, name, source, cardType, minMatch, uniform, sigs, parts, textures, plan,
                            quality, protectAlpha, colorArea, sharpen, true, TexTool.ProtectOwnMax);
        }

        /// <summary>再带「独占贴图保护」设置的版本。</summary>
        public static string Envelope(string kind, string name, string source, string cardType,
                                      double minMatch, Rule uniform,
                                      IEnumerable<string> sigs,
                                      IDictionary<int, Rule> parts,
                                      IEnumerable<TexTool.PresetTex> textures,
                                      Dictionary<string, Rule> plan,
                                      int quality, bool protectAlpha,
                                      bool colorArea, string sharpen,
                                      bool ownProtect, int ownMax)
        {
            return Envelope(kind, name, source, cardType, minMatch, uniform, sigs, parts, textures, plan,
                            quality, protectAlpha, colorArea, sharpen, ownProtect, ownMax,
                            Resample.SharpenPct, Resample.SharpMode);
        }

        /// <summary>再带「锐化强度 / 锐化方式」的版本。</summary>
        public static string Envelope(string kind, string name, string source, string cardType,
                                      double minMatch, Rule uniform,
                                      IEnumerable<string> sigs,
                                      IDictionary<int, Rule> parts,
                                      IEnumerable<TexTool.PresetTex> textures,
                                      Dictionary<string, Rule> plan,
                                      int quality, bool protectAlpha,
                                      bool colorArea, string sharpen,
                                      bool ownProtect, int ownMax,
                                      int sharpenPct, string sharpenMode)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"tool\": \"KoiCardTexTool\",\n  \"version\": 2,\n");
            sb.AppendFormat("  \"presetKind\": \"{0}\",\n", kind);
            sb.AppendFormat("  \"name\": \"{0}\",\n", Esc(name));
            sb.AppendFormat("  \"source\": \"{0}\",\n", Esc(source));
            sb.AppendFormat("  \"cardType\": \"{0}\",\n", string.IsNullOrEmpty(cardType) ? "clothes" : cardType);
            sb.AppendFormat("  \"saved\": \"{0}\",\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendFormat("  \"quality\": {0},\n  \"protectAlpha\": {1},\n", quality, protectAlpha ? "true" : "false");
            sb.AppendFormat("  \"colorArea\": {0},\n  \"sharpen\": \"{1}\",\n", colorArea ? "true" : "false", Esc(sharpen ?? ""));
            sb.AppendFormat("  \"sharpenPct\": {0},\n  \"sharpenMode\": \"{1}\",\n", sharpenPct, Esc(sharpenMode ?? "edge"));
            sb.AppendFormat("  \"sharpenModeExplicit\": {0},\n", Resample.SharpModeExplicit ? "true" : "false");
            sb.AppendFormat("  \"ownProtect\": {0},\n  \"ownMax\": {1},\n", ownProtect ? "true" : "false", ownMax);
            sb.AppendFormat("  \"minMatch\": {0},\n", minMatch.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            if (uniform != null)
                sb.AppendFormat("  \"uniform\": {{ \"format\": \"{0}\", \"size\": {1}{2} }},\n", uniform.Format, uniform.Size,
                    TexTool.RuleExtraJson(uniform));
            else sb.Append("  \"uniform\": null,\n");
            // 类型表（第 1 页批量用）
            sb.Append("  \"plan\": {");
            if (plan != null)
            {
                int i = 0;
                foreach (var kv in plan)
                    sb.AppendFormat("{0}\n    \"{1}\": {{ \"format\": \"{2}\", \"size\": {3}{4} }}",
                        i++ == 0 ? "" : ",", Esc(kv.Key), kv.Value.Format, kv.Value.Size,
                        TexTool.RuleExtraJson(kv.Value));
            }
            sb.Append("\n  },\n");
            // 签名指纹
            var sl = new List<string>();
            if (sigs != null) foreach (var s in sigs) sl.Add(s);
            sb.AppendFormat("  \"sigCount\": {0},\n  \"sigs\": [", sl.Count);
            for (int k = 0; k < sl.Count; k++)
                sb.AppendFormat("{0}{1}\"{2}\"", k == 0 ? "" : ", ", k % 4 == 0 ? "\n    " : "", Esc(sl[k]));
            sb.Append(sl.Count > 0 ? "\n  ],\n" : "],\n");
            // 部位规则
            sb.Append("  \"parts\": {");
            if (parts != null)
            {
                int i = 0;
                foreach (var kv in parts)
                    sb.AppendFormat("{0}\n    \"{1}\": {{ \"name\": \"{2}\", \"format\": \"{3}\", \"size\": {4}{5} }}",
                        i++ == 0 ? "" : ",", kv.Key, Esc(KeyText(kv.Key)), kv.Value.Format, kv.Value.Size,
                        TexTool.RuleExtraJson(kv.Value));
            }
            sb.Append(parts != null && parts.Count > 0 ? "\n  },\n" : "},\n");
            // 单张贴图规则
            sb.Append("  \"textures\": [");
            if (textures != null)
            {
                int n = 0;
                foreach (var t in textures)
                {
                    sb.AppendFormat("{0}\n    {{ \"id\": \"{1}\", \"format\": \"{2}\", \"size\": {3}{4}, \"sigs\": [",
                        n++ == 0 ? "" : ",", Esc(t.Id), t.Rule == null ? "keep" : t.Rule.Format,
                        t.Rule == null ? 0 : t.Rule.Size, TexTool.RuleExtraJson(t.Rule));
                    for (int k = 0; k < t.Sigs.Count; k++)
                        sb.AppendFormat("{0}\"{1}\"", k == 0 ? "" : ", ", Esc(t.Sigs[k]));
                    sb.Append("] }");
                }
            }
            sb.Append(textures != null && Count(textures) > 0 ? "\n  ]\n}\n" : "]}\n");
            return sb.ToString();
        }

        static int Count(IEnumerable<TexTool.PresetTex> t)
        {
            int n = 0;
            foreach (var x in t) n++;
            return n;
        }

        /// <summary>「组·部位」键或衣服槽位键 → 人看得懂的名字（只为 JSON 里的 name 字段好看）。</summary>
        static string KeyText(int key)
        {
            return Chara.IsCharaKey(key) ? Chara.KeyName(key) : Card.SlotKey2Text(key);
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>从预设 JSON 里读出批量页需要的类型表 + 质量 + 保护 alpha。</summary>
        public static bool TryReadPlan(string json, out Dictionary<string, Rule> plan, out int quality, out bool protect)
        {
            bool ca; string sh;
            return TryReadPlanEx(json, out plan, out quality, out protect, out ca, out sh);
        }

        /// <summary>带"处理开关"的版本：colorArea / sharpen 决定彩色贴图是否也走面积平均+边缘感知锐化
        /// （「性能」预设靠这两个字段与「质量/仅优化」区分开；老预设没有这两个字段 → 取默认 = 不开）。
        /// v2.32：colorArea 的默认值已经从 false 改成 true（见 Resample.ColorMode），
        /// 需要区分"预设里写着 false"和"老预设根本没这个字段"的调用方请用下面那个带
        /// colorAreaSeen 的重载。</summary>
        public static bool TryReadPlanEx(string json, out Dictionary<string, Rule> plan, out int quality,
                                         out bool protect, out bool colorArea, out string sharpen)
        {
            bool seen;
            return TryReadPlanEx(json, out plan, out quality, out protect, out colorArea, out sharpen, out seen);
        }

        /// <summary>v2.32：额外报出「colorArea 字段到底在不在预设里」。
        ///
        /// 为什么需要：2.32 把这条的默认值从 false 翻成 true。老预设**根本没有这个字段**，
        /// 如果调用方照单全收 out 出来的默认值 false，「选中一个老预设」就会把新默认悄悄关掉。
        /// 命令行那条路本来就是"字段不在就不覆盖"（Program.cs 里看 caSeen），
        /// 界面必须和命令行一致，所以把"在不在"报出来。
        /// 和 sharpenModeExplicit 是同一套思路：老预设自动迁移到当前默认，用户明确写过的才照收。</summary>
        public static bool TryReadPlanEx(string json, out Dictionary<string, Rule> plan, out int quality,
                                         out bool protect, out bool colorArea, out string sharpen,
                                         out bool colorAreaSeen)
        {
            plan = new Dictionary<string, Rule>();
            quality = 90; protect = true; colorArea = false; sharpen = "";
            colorAreaSeen = false;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    System.Text.Json.JsonElement v;
                    if (root.TryGetProperty("quality", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                        quality = v.GetInt32();
                    if (root.TryGetProperty("protectAlpha", out v) &&
                        (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False))
                        protect = v.GetBoolean();
                    if (root.TryGetProperty("colorArea", out v) &&
                        (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False))
                    { colorArea = v.GetBoolean(); colorAreaSeen = true; }
                    if (root.TryGetProperty("sharpen", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                        sharpen = v.GetString();
                    if (root.TryGetProperty("plan", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var p in v.EnumerateObject())
                            plan[p.Name] = TexTool.ReadRule(p.Value);
                    return plan.Count > 0;
                }
            }
            catch { return false; }
        }

        /// <summary>读预设里的「锐化强度 / 锐化方式」。
        ///
        /// **关于"谁说了算"**：`sharpenMode` 从 2.15 起才写进预设，而 2.15/2.16 的默认是 `edge` ——
        /// 所以那时候存下来的预设里写着 `edge` 的，多半只是"当时默认值"，不是用户真的挑过。
        /// 2.17 把默认换成 `cas` 之后，如果照单全收就会把这些老预设永远钉在 `edge` 上。
        ///
        /// 因此 2.17 起额外写一个 `sharpenModeExplicit` 标记：
        ///   · 标记为 true（用户在界面上动过方式下拉框 / 命令行写过 sharpcas=）→ 按预设里的值走；
        ///   · 没有标记、或标记为 false（老预设）→ **不覆盖**当前值（= 跟随当前默认）。
        /// 这样老预设就自动"迁移"到新默认，而用户明确选过的不会被悄悄改掉。</summary>
        public static bool TryReadSharpen(string json, out int pct, out string mode)
        {
            bool exp;
            return TryReadSharpenEx(json, out pct, out mode, out exp);
        }

        /// <summary>带「方式是否为用户明确指定」的版本。modeExplicit 表示调用方应不应该应用 mode。</summary>
        public static bool TryReadSharpenEx(string json, out int pct, out string mode, out bool modeExplicit)
        {
            pct = 100; mode = "edge"; modeExplicit = false;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    System.Text.Json.JsonElement v;
                    bool has = false;
                    if (root.TryGetProperty("sharpenPct", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                    { pct = v.GetInt32(); has = true; }
                    if (root.TryGetProperty("sharpenMode", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    { mode = v.GetString(); has = true; }
                    if (root.TryGetProperty("sharpenModeExplicit", out v) &&
                        (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False))
                        modeExplicit = v.GetBoolean();
                    return has;
                }
            }
            catch { return false; }
        }

        /// <summary>读预设里的「独占贴图保护」设置。老预设没有这两个字段 → 返回 false（调用方保留界面上的当前值）。</summary>
        public static bool TryReadOwnProtect(string json, out bool ownProtect, out int ownMax)
        {
            ownProtect = true; ownMax = 4096;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    System.Text.Json.JsonElement v;
                    bool has = false;
                    if (root.TryGetProperty("ownProtect", out v) &&
                        (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False))
                    { ownProtect = v.GetBoolean(); has = true; }
                    if (root.TryGetProperty("ownMax", out v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                    { ownMax = v.GetInt32(); has = true; }
                    return has;
                }
            }
            catch { return false; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace KoiCardTexTool
{
    /// <summary>界面语言。</summary>
    public enum Lang { En = 0, Ja = 1, Zh = 2, Ko = 3 }

    /// <summary>
    /// 界面多语言（v1.0）。
    ///
    /// 做法：**以中文原文当键**。代码里 200 多处 `Text = "…"` 一个都不用改 ——
    /// 建完界面后走一遍控件树（<see cref="Apply"/>），把已知文案换成当前语言。
    /// 好处是老代码零侵入、中文本身就是"原文语言"（切回中文永远是对的）；
    /// 代价是**没收录的字符串原样显示**（不会变成空白），所以可以分批补翻译。
    ///
    /// 覆盖范围：控件文案（标签/按钮/勾选框/分组框/页签）、表格列头、下拉项、
    /// 窗口标题、产品名。**日志/状态栏里的动态句子暂不覆盖**（它们是拼出来的，
    /// 要逐条改成 L.F 才行，见 <see cref="F"/>）。
    /// </summary>
    /// <summary>
    /// 会自翻译的 Label：状态栏文字是运行时拼的，直接给 <c>Text</c> 赋值也能翻。
    /// 这样几百处 <c>lblStatus.Text = "…" + x</c> 一个都不用改。
    /// </summary>
    public class LocLabel : System.Windows.Forms.Label
    {
        public override string Text
        {
            get { return base.Text; }
            set
            {
                // **非 UI 线程一律不碰翻译**：直接把原值交给基类，行为与普通 Label 完全一致。
                // （跨线程设置控件文字本来就是 WinForms 的雷区；翻译只应发生在 UI 线程上。）
                if (InvokeRequired) { base.Text = value; return; }
                try { base.Text = L.Tr(value); }
                catch { base.Text = value; }        // 翻译出错也绝不能让状态栏更新失败
            }
        }
    }

    public static class L
    {
        /// <summary>当前语言。**默认英语**。</summary>
        public static Lang Cur = Lang.En;

        public static readonly Lang[] All = { Lang.En, Lang.Ja, Lang.Zh, Lang.Ko };

        /// <summary>语言自身的名字（用各自语言写，不翻译）。</summary>
        public static string LangName(Lang l)
        {
            switch (l)
            {
                case Lang.Ja: return "日本語";
                case Lang.Zh: return "中文";
                case Lang.Ko: return "한국어";
                default: return "English";
            }
        }

        /// <summary>产品名（三种语言都一样）。</summary>
        public const string Product = "Koikatsu Coordinate/Chara Texture Optimization Tool";

        /// <summary>换行符常量。多行文案用它拼，避免在 C# 字面量里写转义。</summary>
        static readonly string NL = Environment.NewLine;

        static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>();

        /// <summary>韩语表（单独一张）：键同样是中文原文。这样加一种语言不用把已有三种语言抄一遍。</summary>
        static readonly Dictionary<string, string> KoMap = new Dictionary<string, string>();
        internal static void K(string zh, string ko) { KoMap[zh] = ko; }
        static void Add(string zh, string en, string ja) { Map[zh] = new[] { en, ja }; }

        /// <summary>取当前语言的译文；没收录就原样返回（中文就是原文）。</summary>
        public static string T(string zh)
        {
            if (zh == null || Cur == Lang.Zh) return zh;
            if (Cur == Lang.Ko)
            {
                string ko;
                if (KoMap.TryGetValue(zh, out ko)) return ko;
                if (zh.StartsWith("换装") && zh.Length > 2 && char.IsDigit(zh[2]))
                    return "코디 " + zh.Substring(2);
                return zh;                                     // 没收录就原样（可见即可补）
            }
            string[] v;
            if (Map.TryGetValue(zh, out v)) return Cur == Lang.En ? v[0] : v[1];
            // 前缀规则：「换装3 Gym」这类是运行时按卡拼的，没法逐条进表 —— 只换前缀。
            if (zh.StartsWith("换装") && zh.Length > 2 && char.IsDigit(zh[2]))
                return (Cur == Lang.En ? "Outfit " : (Cur == Lang.Ko ? "코디 " : "衣装")) + zh.Substring(2);
            // 第 1 页组过滤上的短标签「换3」这种
            if (zh.Length > 1 && zh[0] == '换' && char.IsDigit(zh[1]))
                return (Cur == Lang.En ? "Outfit " : (Cur == Lang.Ko ? "코디 " : "衣装")) + zh.Substring(1);
            // 部位名里的「饰品槽 3」这种（没有英文 token，只能按词换）
            if (zh.StartsWith("饰品槽"))
                return (Cur == Lang.En ? "Accessory " : (Cur == Lang.Ko ? "액세서리 " : "アクセサリ ")) + zh.Substring(3);
            return zh;
        }

        // ---- 显示时翻译（日志 / 状态栏那些"拼出来的句子"）-------------------------
        //
        // 这些句子是运行时拼的（"已保存预设：" + 路径、"已记住列宽：{0} 个表格"…），
        // 没法在构造界面时翻译，也没必要把几百处赋值逐个改写。做法：
        //   1) 整串能对上字典 → 直接换；
        //   2) 否则找**最长的、是句子前缀的**字典键（"已保存预设："），换掉这一段，其余原样；
        //   3) 都没有 → 原样返回（中文照旧，不会变空）。
        // 结果缓存起来：日志一次压缩要写几百行，不能每次都线性查表。
        // **必须并发安全**：日志是从压缩的多个 worker 线程写的，
        // 普通 Dictionary 被并发写会写坏内部结构，症状就是**整个程序卡死**（哈希表里死循环）。
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TrCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        public static string Tr(string s)
        {
            if (string.IsNullOrEmpty(s) || Cur == Lang.Zh) return s;
            string hit;
            if (TrCache.TryGetValue(s, out hit)) return hit;
            string outp = StripParts(TranslateSlow(s));
            if (TrCache.Count < 20000) TrCache.TryAdd(s, outp);
            return outp;
        }

        // 按首字索引，避免每次都把几百条词条扫一遍
        static readonly Dictionary<char, List<string>> ByFirst = new Dictionary<char, List<string>>();
        // 句中碎片的最小长度。英文/韩文界面下放到 2：那时界面句子已经是英文，
        // 出现汉字基本只剩"数据"（身体/脸/头发 这类部位词），不会误伤句子；
        // 中文/日文界面保持 4（汉字是正文，短词容易误伤）。
        static int MinFrag { get { return (Cur == Lang.En || Cur == Lang.Ko) ? 2 : 4; } }

        static void IndexKeys()
        {
            if (ByFirst.Count > 0) return;
            foreach (var k in Map.Keys)
            {
                if (k.Length < MinFrag) continue;
                char c = k[0];
                List<string> l;
                if (!ByFirst.TryGetValue(c, out l)) { l = new List<string>(); ByFirst[c] = l; }
                l.Add(k);
            }
            foreach (var k in KoMap.Keys)
            {
                if (k.Length < MinFrag) continue;
                char c = k[0];
                List<string> l;
                if (!ByFirst.TryGetValue(c, out l)) { l = new List<string>(); ByFirst[c] = l; }
                l.Add(k);
            }
            foreach (var l in ByFirst.Values) l.Sort(delegate (string x, string y) { return y.Length - x.Length; });
        }

        // ---- 部位名只留英文（v1.0.4）---------------------------------------------
        //
        // 卡片里的部位名是"中文 + 英文 token"（"上衣 top" / "鞋(外层) shoes_outer"），
        // 这是卡里的数据，不是界面文案。英文/韩文界面下只保留英文那半截更好读；
        // **中文/日文界面不处理** —— 中日读者本来就看懂汉字（日文里汉字是正常写法）。
        static readonly System.Text.RegularExpressions.Regex PartTail =
            new System.Text.RegularExpressions.Regex(
                @"^[\u3000-\u303f\u4e00-\u9fff()（）\s]*\s([A-Za-z][A-Za-z0-9_\-]*(?:\s[A-Za-z0-9_\-]+)*)$");
        static readonly System.Text.RegularExpressions.Regex PartFrag =
            new System.Text.RegularExpressions.Regex(
                @"[\u4e00-\u9fff][\u4e00-\u9fff\u3000()（）/\u00b7\s]*?\s([A-Za-z][A-Za-z0-9_\-]{1,})");

        public static string StripParts(string s)
        {
            if (string.IsNullOrEmpty(s) || Cur == Lang.Zh || Cur == Lang.Ja) return s;
            var m = PartTail.Match(s);
            if (m.Success) return m.Groups[1].Value;                 // 整串就是部位名
            if (!HasAnyCjk(s)) return s;
            string outp = PartFrag.Replace(s, "$1");                 // 句子里夹着的部位名
            return outp;
        }

        static bool HasAnyCjk(string s)
        {
            foreach (char ch in s) if (ch >= '一' && ch <= '鿿') return true;
            return false;
        }

        /// <summary>在位置 i 处匹配"模式规则"，返回源串消耗长度并给出译文（0 = 不匹配）。</summary>
        static int RuleAt(string s, int i, out string translated)
        {
            translated = null;
            // 换装3 / 换3 → Outfit 3 / 衣装3
            if (s[i] == '换' && i + 1 < s.Length && (s[i + 1] == '装' || char.IsDigit(s[i + 1])))
            {
                int j = i + (s[i + 1] == '装' ? 2 : 1);
                int st = j;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j > st)
                {
                    translated = (Cur == Lang.En ? "Outfit " : (Cur == Lang.Ko ? "코디 " : "衣装"))
                                 + s.Substring(st, j - st);
                    return j - i;
                }
            }
            // 饰品槽 → Accessory（后面跟数字，数字留给词典/原样）
            if (i + 3 <= s.Length && string.CompareOrdinal(s, i, "饰品槽", 0, 3) == 0)
            {
                translated = Cur == Lang.En ? "Accessory " : (Cur == Lang.Ko ? "액세서리 " : "アクセサリ ");
                return 3;
            }
            return 0;
        }

        static string TranslateSlow(string s)
        {
            // ① 整串命中
            if (Map.ContainsKey(s) || KoMap.ContainsKey(s)) return T(s);
            IndexKeys();
            // ② 从头到尾扫一遍，**哪一段能对上词条就翻哪一段**
            //    （日志里很多句子是拼出来的：翻好模板之后，中间还夹着没进词条的片段）
            var sb = new StringBuilder(s.Length);
            int i = 0, hits = 0;
            while (i < s.Length)
            {
                // ① 模式规则先判：运行时拼出来的名字（"换装3 Gym"/"饰品槽 12"），
                //    它们在**句子中间**也要能换（"换装1 School01 · 上衣 top"）。
                string seg;
                int segLen = RuleAt(s, i, out seg);
                if (segLen > 0) { sb.Append(seg); i += segLen; hits++; continue; }
                List<string> cands;
                string best = null;
                if (ByFirst.TryGetValue(s[i], out cands))
                {
                    foreach (var k in cands)
                    {
                        if (k.Length <= s.Length - i && string.CompareOrdinal(s, i, k, 0, k.Length) == 0)
                        { best = k; break; }      // 已按长度降序，第一个命中就是最长
                    }
                }
                if (best != null)
                {
                    sb.Append(T(best));
                    i += best.Length;
                    hits++;
                }
                else
                {
                    sb.Append(s[i]);
                    i++;
                }
            }
            return hits > 0 ? sb.ToString() : s;
        }

        /// <summary>带占位符的模板翻译 + 格式化（动态句子用这个）。</summary>
        public static string F(string zhTemplate, params object[] args)
        {
            return string.Format(T(zhTemplate), args);
        }

        // ---- 控件树翻译 ----------------------------------------------------------

        /// <summary>切换语言时把翻译缓存清掉（缓存是按语言算出来的）。</summary>
        public static void ClearCache() { TrCache.Clear(); ByFirst.Clear(); }   // 只在 UI 线程切语言时调

        // ---- 气泡提示（tooltip）也要多语言 ----------------------------------------
        //
        // 气泡不在控件树里，遍历控件翻不到，所以这里登记一份"控件 → 中文原文"，
        // 切语言时统一重设。用法：把 `new ToolTip().SetToolTip(控件, "…")` 换成 `L.Tip(控件, "…")`。
        static readonly ToolTip TipHost = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300, ReshowDelay = 100 };
        // 登记的是"怎么把译文设回去"的动作，这样控件和表格列都能登记
        static readonly List<KeyValuePair<Action<string>, string>> TipReg =
            new List<KeyValuePair<Action<string>, string>>();

        public static void Tip(Control c, string zh)
        {
            if (c == null || zh == null) return;
            TipReg.Add(new KeyValuePair<Action<string>, string>(
                delegate (string s) { if (!c.IsDisposed) TipHost.SetToolTip(c, s); }, zh));
            TipHost.SetToolTip(c, T(zh));
        }

        /// <summary>表格列的提示（列不是控件，走它自己的 ToolTipText）。</summary>
        public static void Tip(DataGridViewColumn col, string zh)
        {
            if (col == null || zh == null) return;
            TipReg.Add(new KeyValuePair<Action<string>, string>(
                delegate (string s) { col.ToolTipText = s; }, zh));
            col.ToolTipText = T(zh);
        }

        /// <summary>切语言后把所有气泡重设一遍。</summary>
        public static void RetipAll()
        {
            for (int i = 0; i < TipReg.Count; i++)
            {
                var kv = TipReg[i];
                try { if (kv.Key != null) kv.Key(T(kv.Value)); } catch { }
            }
        }

        /// <summary>自检用：还没翻译的气泡（当前语言下译文 == 中文原文的）。</summary>
        public static string MissingTips()
        {
            var miss = new List<string>();
            var seen = new HashSet<string>();
            for (int i = 0; i < TipReg.Count; i++)
            {
                string zh = TipReg[i].Value;
                if (zh == null || !seen.Add(zh)) continue;
                if (T(zh) == zh && Cur != Lang.Zh) miss.Add(zh);
            }
            return string.Format("气泡共 {0} 条（去重 {1}），本语言未翻译 {2} 条：{3}",
                TipReg.Count, seen.Count, miss.Count,
                miss.Count == 0 ? "（全部已翻译）" : string.Join(" | ", miss.ToArray()));
        }

        /// <summary>自检用：某个控件当前的气泡文字。</summary>
        public static string TipOf(Control c) { return c == null ? "-" : TipHost.GetToolTip(c); }

        /// <summary>
        /// 给"当前可能已经是译文"的文字重新翻译：先反查出中文原文再翻。
        /// **表头之类的必须用它，不能用 T()** —— `T()` 只认中文原文，
        /// 传英文进去会原样返回，于是第一次切能翻、之后再切就卡住（用户反馈的表头问题）。
        /// </summary>
        public static string Retranslate(string shown)
        {
            if (shown == null) return null;
            string zh = ZhOf(shown);
            return zh != null ? T(zh) : shown;
        }

        /// <summary>把一棵控件树（含下拉项与子控件）翻成当前语言。</summary>
        public static void Apply(Control root)
        {
            if (root == null) return;
            ApplyOne(root);
            foreach (Control c in root.Controls) Apply(c);
        }

        static void ApplyOne(Control c)
        {
            var cb = c as ComboBox;
            if (cb != null)
            {
                // 下拉项：第一次翻译时把原文留一份在 Tag 里，之后每次切换都从原文重译
                // （不然"中文→英文→中文"会越翻越偏）。
                string[] orig = cb.Tag as string[];
                if (orig == null)
                {
                    orig = new string[cb.Items.Count];
                    for (int i = 0; i < cb.Items.Count; i++) orig[i] = cb.Items[i] as string;
                    cb.Tag = orig;
                }
                int sel = cb.SelectedIndex;
                for (int i = 0; i < orig.Length && i < cb.Items.Count; i++)
                    if (orig[i] != null) cb.Items[i] = T(orig[i]);
                if (sel >= 0 && sel < cb.Items.Count) cb.SelectedIndex = sel;
                return;
            }
            var g = c as DataGridView;
            if (g != null)
            {
                foreach (DataGridViewColumn col in g.Columns) col.HeaderText = Retranslate(col.HeaderText);
                foreach (DataGridViewColumn col in g.Columns)
                {
                    var cc = col as DataGridViewComboBoxColumn;
                    if (cc == null) continue;
                    string[] orig = cc.Tag as string[];
                    if (orig == null)
                    {
                        orig = new string[cc.Items.Count];
                        for (int i = 0; i < cc.Items.Count; i++) orig[i] = cc.Items[i] as string;
                        cc.Tag = orig;
                    }
                    for (int i = 0; i < orig.Length && i < cc.Items.Count; i++)
                        if (orig[i] != null) cc.Items[i] = T(orig[i]);
                }
                return;
            }
            // 注意要把 Form 也算进来：窗体自己的标题也是 Text（第一版漏了它，
            // 结果界面全英文了、标题栏还是中文）。
            if (c is Form || c is TabControl || c is TabPage || c is GroupBox || c is Label || c is Button ||
                c is CheckBox || c is RadioButton || c is LinkLabel || c is ToolStripItem)
            {
                // 英文/日文比中文宽：按钮、勾选框、单选按钮在换文字之前必须先打开 AutoSize，
                // 否则宽度会停在中文时的值 → 文案被切掉（实测单选 112 需要 / 104 实际、勾选框 135/104）。
                // 这三种类型在本项目里从没故意关过 AutoSize（关掉的只有 Label/GroupBox/Panel），所以这里可以放心打开。
                if (c is Button || c is CheckBox || c is RadioButton) c.AutoSize = true;
                // 只翻"译文变了"的，避免把用户运行时拼出来的状态文字也当模板处理
                string now = c.Text;
                string zh = ZhOf(now);
                if (zh != null) c.Text = T(zh);
            }
        }

        /// <summary>反向查表：给当前显示的文字，找回它的中文原文。
        /// （界面建好之后 Text 里是中文原文，切语言时又可能是英文/日文 —— 两种都要认。）</summary>
        static string ZhOf(string shown)
        {
            if (shown == null) return null;
            if (Map.ContainsKey(shown)) return shown;              // 显示的就是中文原文
            foreach (var kv in Map)
            {
                if (kv.Value[0] == shown || kv.Value[1] == shown) return kv.Key;
            }
            foreach (var kv in KoMap)
            {
                if (kv.Value == shown) return kv.Key;              // 韩语→原文也要认
            }
            return null;                                           // 未收录：不动它
        }

        /// <summary>翻译之后**自底向上**重排一遍。
        ///
        /// 为什么必须做：英文/日文比中文宽，换完文字后嵌套的 AutoSize 容器（尤其是
        /// FlowLayoutPanel）会多折一行、需要更高的高度，而 WinForms 不会自动把这串
        /// 变更一路传上去 —— 实测结果是「显示放大选项」那个按钮被挤到可视区外
        /// （Bounds 306x0），界面上直接看不见了。后序（先子后父）PerformLayout
        /// 才能把最外层的高度重新算对。</summary>
        static bool relayoutBusy;

        // 自检用：重排的累计耗时/次数（排查切页卡顿）
        public static long RelayoutMs;
        public static int RelayoutCalls;

        public static void Relayout(Control root)
        {
            // **重入保护，必须有**：Relayout 里改控件尺寸会触发 Resize，
            // 而 Resize/Shown/SelectedIndexChanged 上都挂了 Relayout —— 不给保护就是无限递归，
            // 实测直接 **段错误**（栈溢出），点一下语言下拉就崩。
            if (relayoutBusy) return;
            relayoutBusy = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                RelayoutOnce(root);
                RelayoutOnce(root);   // 嵌套 AutoSize 常要两遍才稳定
            }
            finally
            {
                relayoutBusy = false;
                sw.Stop();
                RelayoutMs += sw.ElapsedMilliseconds;
                RelayoutCalls++;
            }
        }

        static void RelayoutOnce(Control root)
        {
            if (root == null) return;
            // **没显示的东西一律不碰**：未选中的标签页里子控件 Visible=false，
            // 量出来的高度是垃圾（默认 100），照它写回去等于把那一页的布局毁掉。
            if (root.Parent != null && !root.Visible) return;
            // 只在**根**上做一次 PerformLayout：WinForms 会自己级联到子控件。
            // 原来对每个控件逐个 PerformLayout + Invalidate，实测一次切页要 440ms
            // （整次切页 404~483ms 里 9 成是它），是"切页卡顿"的主因。
            root.PerformLayout();
        }

        // ---- 文案表（中文原文 → 英语 / 日语）-------------------------------------

        static L()
        {
            // 页签与产品
            Add("Koikatsu 衣服卡贴图压缩工具（EXE 版）", Product, Product);
            Add("① 批量压缩（整卡 / 文件夹）", "① Batch (cards / folder)", "① 一括（カード / フォルダ）");
            Add("② 单服装卡细分压缩", "② Outfit card (per-part)", "② 衣装カード（部位ごと）");
            Add("③ 单张人物卡压缩", "③ Chara card", "③ キャラカード");
            Add("显示日志", "Show log", "ログを表示");
            Add("贴图类型", "Texture type", "テクスチャ種別");
            Add("浅色", "Light", "ライト");
            Add("深色", "Dark", "ダーク");
            Add("无法处理目标一同不压缩并复制进输出目录",
                "Copy unprocessed cards",
                "未処理カードはコピー");
            Add("读取部位", "Read parts", "部位を読み込む");
            Add("一键改所有类型", "apply to all types", "全種別に適用");
            Add("应用到所有类型", "Apply to all types", "全種別に適用");

            // 第 1 页
            Add("1) 选择一张衣服卡（.png）", "1) Pick an outfit card (.png)", "1) 衣装カードを選ぶ（.png）");
            Add("2) 部位一览", "2) Parts", "2) 部位一覧");
            Add("3) 选中部位的贴图明细", "3) Textures of the selected part", "3) 選択部位のテクスチャ詳細");
            Add("4) 输出 / 选项", "4) Output / Options", "4) 出力 / オプション");
            Add("输入 / 输出", "Input / Output", "入力 / 出力");
            Add("卡片路径", "Card path", "カードパス");
            Add("输出目录", "Output folder", "出力フォルダ");
            Add("文件名后缀", "Filename suffix", "ファイル名の接尾辞");
            Add("覆盖同名", "Overwrite existing", "同名を上書き");
            Add("单个卡片文件", "Single card file", "単一カードファイル");
            Add("整个文件夹", "Whole folder", "フォルダ全体");
            Add("包含子文件夹", "Include subfolders", "サブフォルダを含む");
            Add("① 选择一张衣服卡（.png）", "1) Pick an outfit card (.png)", "1) 衣装カードを選ぶ（.png）");
            Add("② 读取部位", "2) Read parts", "2) 部位を読み込む");
            Add("▶  开始压缩", "▶  Start", "▶  実行");
            Add("取消", "Cancel", "キャンセル");
            Add("退出", "Exit", "終了");
            Add("浏览…", "Browse…", "参照…");
            Add("读取部位", "Read parts", "部位を読み込む");
            Add("另存为计划 JSON（CLI 批处理可用）", "Save plan JSON (for CLI batch)", "プラン JSON を保存（CLI 一括用）");
            Add("打开输出目录", "Open output folder", "出力フォルダを開く");
            Add("打开预设目录", "Open preset folder", "プリセットフォルダを開く");
            Add("隐藏日志", "Hide log", "ログを隠す");
            Add("日志", "Log", "ログ");
            Add("预设", "Preset", "プリセット");
            Add("刷新", "Refresh", "更新");
            Add("保存为预设…", "Save as preset…", "プリセットとして保存…");
            Add("保存预设…", "Save preset…", "プリセット保存…");
            Add("读取预设…", "Load preset…", "プリセット読込…");
            Add("保存", "Save", "保存");
            Add("将当前预设设为默认", "Set as the default preset", "選択中のプリセットを既定にする");
            Add("将压缩应用到目录下的所有相似服装",
                "Apply to similar outfits",
                "類似衣装すべてに適用");
            Add("用当前设置批量处理文件夹…", "Batch-process a folder with current settings…",
                "現在の設定でフォルダを一括処理…");
            Add("不匹配的服装直接复制到输出目录（不压缩）",
                "Copy non-matching outfits",
                "一致しない衣装はコピー");
            Add("批量含子文件夹", "Batch: include subfolders", "一括: サブフォルダを含む");
            Add("拖目录时含子文件夹", "Include subfolders when a folder is dropped",
                "フォルダをドロップしたときサブフォルダを含む");
            Add("JPEG 质量", "JPEG quality", "JPEG 品質");
            Add("锐化", "Sharpen", "シャープ");
            Add("锐化算法", "Sharpen mode", "シャープ方式");
            Add("灰度2通道", "Grayscale 2ch", "グレースケール2ch");
            Add("灰度用 2 通道", "Grayscale 2ch", "グレースケール2ch");
            Add("细节图保细节", "Preserve detail maps", "ディテールマップを保持");
            Add("贴图细节保护", "Texture detail protection", "テクスチャ詳細保護");
            Add("独占贴图保护", "Protect exclusive", "専有テクスチャを保護");
            Add("彩色贴图也走面积平均", "Area-average colors", "カラーも面積平均");
            Add("贴图类型：分别设置 处理方式 与 最大边",
                "Texture types: set format and max edge per type",
                "テクスチャ種別: 形式と最大辺を個別に設定");
            Add("处理方式（格式）", "Format", "形式");
            Add("处理方式", "Format", "形式");
            Add("最大边", "Max edge", "最大辺");
            Add("放大", "Upscale", "アップスケール");
            Add("放大算法", "Upscale kernel", "アップスケール方式");
            Add("应用放大设置", "Apply upscale setting", "アップスケール設定を適用");
            Add("显示放大选项 ▾", "Show upscale options ▾", "アップスケール設定を表示 ▾");
            Add("隐藏放大选项 ▴", "Hide upscale options ▴", "アップスケール設定を隠す ▴");
            Add("扫描结果", "Scan result", "スキャン結果");
            Add("统一设置（一键改所有类型）：", "Unified setting:", "一括設定（全種別に適用）:");
            Add("应用到所有类型", "Apply to all types", "全種別に適用");
            Add("主贴图 MainTex（衣服主体颜色）", "MainTex (body color)", "メインテクスチャ MainTex");
            Add("法线贴图 Normal/Bump", "Normal / Bump", "法線マップ Normal/Bump");
            Add("遮罩 Mask", "Mask", "マスク Mask");
            Add("高度/视差 Parallax/Height", "Parallax / Height", "パララックス/ハイト");
            Add("发光 Emission", "Emission", "発光 Emission");
            Add("反射 Reflection", "Reflection", "反射 Reflection");
            Add("材质球 MatCap", "MatCap", "マテリアルキャップ MatCap");
            Add("高光色 HighColor", "HighColor", "ハイカラー HighColor");
            Add("其它 / 未分类", "Other / unclassified", "その他 / 未分類");
            Add("一键统一", "Unify", "一括統一");
            Add("按贴图类型统一 ▾", "Unify by texture type ▾", "テクスチャ種別ごとに統一 ▾");
            Add("按贴图类型统一 ▴", "Unify by texture type ▴", "テクスチャ種別ごとに統一 ▴");
            Add("应用到所有该类型贴图", "Apply to all of this type",
                "この種別の全テクスチャに適用");
            Add("应用到选中部位", "Apply to selected part", "選択部位に適用");
            Add("应用到所有部位", "Apply to all parts", "全部位に適用");
            Add("应用到本部位全部贴图", "Apply to this part's textures",
                "この部位の全テクスチャに適用");
            Add("记住列宽", "Remember column widths", "列幅を記憶");
            Add("恢复默认列宽", "Reset column widths", "列幅を既定に戻す");
            Add("点上面一行贴图即可预览", "Click a texture row above to preview",
                "上のテクスチャ行をクリックするとプレビュー");
            Add("点一行贴图即可预览（上方灰条可拖动调高度）",
                "Click a texture row to preview (drag the grey bar to resize)",
                "行をクリックでプレビュー（灰色バーをドラッグで高さ調整）");
            Add("就绪。选好输入卡（拖进来或点浏览…），然后点「▶ 开始压缩」。",
                "Ready. Choose an input card (drop it here or Browse…), then click ▶ Start.",
                "準備完了。カードを選び（ドロップまたは参照…）、▶ 実行 を押してください。");
            Add("扫描中…", "Scanning…", "スキャン中…");
            Add("正在取消…", "Cancelling…", "キャンセル中…");
            Add("把衣服卡（.png）或文件夹直接拖到这里读取　—　也可以拖到 EXE 图标上，或点「浏览…」",
                "Drop an outfit card (.png) or a folder here — or drop it on the EXE, or click Browse…",
                "衣装カード（.png）やフォルダをここにドロップ — EXE にドロップするか参照…でも可");
            Add("（批量时每个 .png 都会处理，没贴图的自动跳过）",
                "(in batch mode every .png is processed; cards without textures are skipped)",
                "（一括では全 .png を処理、テクスチャ無しは自動スキップ）");
            Add("（衣服卡不受影响）", "(outfit cards are unaffected)", "（衣装カードには影響しません）");
            Add("▸ 人物卡组过滤（只对人物卡生效，点这里展开）",
                "▸ Chara group filter (chara cards only; click to expand)",
                "▸ キャラのグループ絞り込み（キャラカードのみ・クリックで展開）");
            Add("人物卡组过滤：", "Chara group filter:", "キャラのグループ絞り込み:");
            Add("组过滤", "Group filter", "グループ絞り込み");
            Add("全选", "All", "全選択");
            Add("全不选", "None", "全解除");
            Add("只压本体", "Body only", "本体のみ");
            Add("显示：全部组", "Show: all groups", "表示: 全グループ");
            Add("不用所选预设：完全按上面的类型表", "No preset: use the type table above",
                "プリセット不使用: 上の種別表に従う");
            Add("不使用预设：完全按上面的类型表", "No preset: use the type table above",
                "プリセット不使用: 上の種別表に従う");
            Add("无", "None", "無し");
            Add("部位", "Part", "部位");
            Add("数量", "Count", "数");
            Add("大小", "Size", "サイズ");
            Add("尺寸", "Dimensions", "寸法");
            Add("启用", "On", "有効");
            Add("类型", "Type", "種別");
            Add("材质", "Material", "マテリアル");
            Add("原格式", "Original format", "元の形式");
            Add("共用情况", "Shared with", "共有状況");
            Add("最终处理", "Final processing", "最終処理");
            Add("独占贴图", "Exclusive tex", "専有テクスチャ");
            Add("路径", "Path", "パス");

            // 第 2 页
            Add("单服装卡细分压缩", "Outfit card (per-part)", "衣装カード（部位ごと）");
            Add("批量压缩（整卡 / 文件夹）", "Batch (cards / folder)", "一括（カード / フォルダ）");
            Add("单人物卡压缩", "Chara card", "キャラカード");
            Add("先选一张衣服卡（或把卡拖到这一页），点「读取部位」。",
                "Pick an outfit card (or drop it on this page) and click Read parts.",
                "衣装カードを選び（このページにドロップ可）、部位を読み込む を押してください。");
            Add("正在读取部位…（大卡要几秒；界面不会卡住）",
                "Reading parts… (large cards take a few seconds; the UI stays responsive)",
                "部位を読み込み中…（大きなカードは数秒・UI は固まりません）");
            Add("读取后中间列出每个部位有哪些贴图；勾选想压的部位、\n给每个部位选格式与最大边；右边可以给单张贴图\n单独指定；没勾/没指定的部位原样不动。",
                "After reading, the middle column lists" + NL +
                "each part's textures. Tick the parts" + NL +
                "to compress, pick format + max edge;" + NL +
                "the right side overrides one texture.",
                "読み込み後、中央に部位ごとの" + NL +
                "テクスチャが並びます。圧縮する" + NL +
                "部位にチェックし、形式と最大辺を" + NL +
                "選びます。右側は単体の個別指定。");
            Add("左边几项由两个按钮套到部位/贴图\n（贴图里的「跟随」= 用这里的值）\n右边两个开关立刻生效（整卡）",
                "The buttons apply the left values" + NL + "to parts / textures (Follow = these)." + NL + "The switches apply to the whole card.",
                "左の値はボタンで部位／テクスチャへ" + NL + "（「部位に従う」= ここの値）" + NL + "右の2つはカード全体に反映。");
            Add("已读取 {0} 张贴图，{1} 个部位。勾选要压的部位后点「单服装卡细分压缩」开始。",
                "Loaded {0} textures in {1} parts. Tick the parts to compress, then click Start.",
                "{0} テクスチャ / {1} 部位を読み込みました。圧縮する部位にチェックして実行してください。");
            Add("请先读取一张服装卡", "Load an outfit card first", "先に衣装カードを読み込んでください");
            Add("请先在「部位一览」里选中一个部位", "Select a part in the parts list first",
                "先に部位一覧で部位を選択してください");
            Add("贴图明细", "Texture details", "テクスチャ詳細");

            // 第 3 页
            Add("1) 选一张人物卡（Koikatu_F_*.png）", "1) Pick a chara card (Koikatu_F_*.png)",
                "1) キャラカードを選ぶ（Koikatu_F_*.png）");
            Add("2) 角色本体 / 换装 × 部位", "2) Body / outfit × part", "2) 本体 / 衣装 × 部位");
            Add("3) 选中组·部位的贴图", "3) Textures of the selected group · part",
                "3) 選択した組・部位のテクスチャ");
            Add("② 读取", "2) Read", "2) 読み込む");
            Add("清空", "Clear", "クリア");
            Add("先选一张或多张人物卡（Koikatu_F_*.png），点「读取」。",
                "Pick one or more chara cards (Koikatu_F_*.png) and click Read.",
                "キャラカード（Koikatu_F_*.png）を選び、読み込む を押してください。");
            Add("正在读取…（大卡要几秒；界面不会卡住）",
                "Reading… (large cards take a few seconds; the UI stays responsive)",
                "読み込み中…（大きなカードは数秒・UI は固まりません）");
            Add("读取后中间按「角色本体 / 换装1~7」列出，\n每组里再分部位；右边可以给单张贴图单独指定。\n角色的身体/脸/头发/眼睛贴图属于「角色本体」，\n和换装是分开的。",
                "After reading: Body / Outfit 1-7," + NL +
                "each split into parts. The right" + NL +
                "side overrides a single texture." + NL +
                "Body, face, hair, eyes belong to Body.",
                "読み込み後は本体 / 衣装1〜7、" + NL +
                "その中で部位に分かれます。右側は" + NL +
                "単体テクスチャの個別指定です。" + NL +
                "体・顔・髪・目は「本体」に属します。");
            Add("当前组全选", "Select all in group", "この組を全選択");
            Add("当前组全不选", "Deselect all in group", "この組を全解除");
            Add("组 · 部位", "Group · Part", "組 · 部位");
            Add("设置到目前浏览的部位", "Apply to viewed part", "表示中の部位に適用");
            Add("一键统一只改「范围」那个组，其它组不动\n右边两个开关立刻生效（整卡）",
                "Unify changes only the group in Scope." + NL + "The two switches apply to the whole card.",
                "一括統一は「範囲」の組のみ変更。" + NL + "右の2つはカード全体に即時反映。");
            Add("当前没有任何条目的「启用」是勾上的 —— 先把要压的勾上再用这个范围。",
                "No entry is ticked as enabled — tick the ones to compress before using this scope.",
                "「有効」が1つもチェックされていません。圧縮する項目にチェックしてから使ってください。");
            Add("一键统一只改「范围」那个组，其它组不动", "Unify only changes the group in Scope",
                "一括統一は「範囲」の組だけを変更");
            Add("批量：用当前设置处理选中的卡", "Batch: process the selected cards", "一括: 選択カードを処理");
            Add("范围", "Scope", "範囲");
            Add("全部组", "All groups", "全グループ");
            Add("角色本体", "Body", "本体");
            Add("仅已启用的部位", "Enabled parts only", "有効な部位のみ");
            Add("仅当前选中组", "Selected group only", "選択中の組のみ");
            Add("上限", "Max", "上限");
            Add("放宽到 4096", "Up to 4096", "4096 まで");
            Add("放宽到 2048", "Up to 2048", "2048 まで");
            Add("放宽到 1024", "Up to 1024", "1024 まで");
            Add("原尺寸不降", "Keep original size", "元サイズ維持");
            Add("请先读取一张人物卡", "Load a chara card first", "先にキャラカードを読み込んでください");
            Add("请先在「组·部位」表里选中一行", "Select a row in the group · part table first",
                "先に組・部位テーブルで行を選択してください");
            Add("这张不是人物卡（没有「角色本体 / 换装」结构），请用第 2 页压衣服卡。",
                "This is not a chara card (no Body / Outfit structure). Use page 2 for outfit cards.",
                "これはキャラカードではありません（本体/衣装の構造なし）。衣装カードは 2 ページで。");
            Add("（且角色本体/装备的贴图不会被处理）",
                "(body / equipment textures are not processed)",
                "（本体・装備のテクスチャは処理されません）");
            Add("（只有「同时处理角色本体」勾上时才会处理本体贴图）",
                "(body textures are processed only when Body is included)",
                "（本体テクスチャは「本体を含める」時のみ処理）");

            // 预览窗口
            Add("Koikatsu Coordinate/Chara Texture Optimization Tool — Preview",
                "Koikatsu Coordinate/Chara Texture Optimization Tool — Preview",
                "Koikatsu Coordinate/Chara Texture Optimization Tool — プレビュー");
            Add("贴图放大预览", "Texture preview", "テクスチャ拡大プレビュー");
            Add("⛶ 独立窗口", "⛶ Separate window", "⛶ 別ウィンドウ");
            Add("打开时这里会显示当前贴图信息", "Texture info appears here when opened",
                "開くとここにテクスチャ情報が表示されます");
            Add("滚轮缩放 · 左键拖动平移 · 双击适应窗口 · 1=100%",
                "Wheel = zoom · drag = pan · double-click = fit · 1 = 100%",
                "ホイール=拡大縮小 · ドラッグ=移動 · ダブルクリック=フィット · 1=100%");
            Add("适应窗口", "Fit", "フィット");
            Add("100%", "100%", "100%");

            // 下拉项
            Add("原样不动", "Keep", "そのまま");
            Add("自动", "Auto", "自動");
            Add("全部 JPEG（有损，最小）", "All JPEG (lossy, smallest)", "すべて JPEG（非可逆・最小）");
            Add("全部 PNG（无损，最大）", "All PNG (lossless, largest)", "すべて PNG（可逆・最大）");
            Add("自动（安全且更小才用 JPEG）", "Auto (JPEG only when safe and smaller)",
                "自動（安全かつ小さい時のみ JPEG）");
            Add("原样不动（不重编码、不缩放）", "Keep (no re-encode, no resize)",
                "そのまま（再エンコード・縮小なし）");
            Add("不缩放", "No resize", "縮小しない");
            Add("原尺寸", "Original size", "元サイズ");
            Add("跟随", "Follow", "従う");
            Add("跟随部位", "Follow part", "部位に従う");
            Add("不放大", "No upscale", "拡大しない");
            Add("×2", "×2", "×2");
            Add("×4", "×4", "×4");
            Add("不锐化", "No sharpen", "シャープなし");
            Add("对比度自适应", "Contrast adaptive", "コントラスト適応");
            Add("边缘感知", "Edge aware", "エッジ感知");
            Add("开", "On", "オン");
            Add("关", "Off", "オフ");
            Add("（不使用预设：按上面的类型表）", "(No preset: use the type table above)",
                "（プリセット不使用: 上の種別表に従う）");

            // 类名短标签（贴图类型下拉用；Classes.Short 的返回值）
            Add("主贴图", "MainTex", "メインテクスチャ");
            Add("法线贴图", "Normal", "法線");
            Add("遮罩", "Mask", "マスク");
            Add("高度/视差", "Parallax/Height", "パララックス/ハイト");
            Add("发光", "Emission", "発光");
            Add("反射", "Reflection", "反射");
            Add("材质球", "MatCap", "マテリアルキャップ");
            Add("高光色", "HighColor", "ハイカラー");
            Add("其它/未分类", "Other", "その他");
            Add("▶  单服装卡细分压缩", "▶  Compress this outfit card", "▶  衣装カードを圧縮");
            Add("▶  压缩人物卡", "▶  Compress chara card", "▶  キャラカードを圧縮");
            Add("▶  开始压缩", "▶  Start", "▶  実行");

            // ---- v1.0.2：日志 / 状态栏 / 对话框里的动态句子 ----
            Add("        {0,-34} {1,3} 张 {2,10}  材质 {3}", "        {0,-34} {1,3} tex {2,10}  material {3}",
                "        {0,-34} {1,3} 枚 {2,10}  マテリアル {3}");
            Add("      而锐化只在「降了分辨率」的贴图上跑，所以现在不会有任何效果。", "      Sharpen only runs on textures whose resolution was reduced, so it has no effect right now.",
                "      シャープは解像度を下げたテクスチャにのみ適用されるため、現状では効果がありません。");
            Add("      要用锐化请把某些类的最大边设成 2048/1024，或直接用「质量」「性能」预设。", "      To use sharpen, set the max edge of some classes to 2048/1024, or just use the \"Quality\" / \"Performance\" presets.",
                "      シャープを使うには、いずれかのクラスの最大辺を 2048/1024 に設定するか、「品質」「性能」プリセットをそのまま使ってください。");
            Add("    {0,-10} {1,4} 张  {2,10}  最大 {3}px", "    {0,-10} {1,4} tex  {2,10}  max {3}px",
                "    {0,-10} {1,4} 枚  {2,10}  最大 {3}px");
            Add("   单贴图规则落地 {0} 张；本卡没找到对应贴图 {1} 张{2}", "   per-texture rules applied to {0} tex; {1} tex had no matching entry in this card{2}",
                "   単体テクスチャ規則を {0} 枚に適用；このカードで対応テクスチャが見つからなかったもの {1} 枚{2}");
            Add("   贴图 TexID {0,-6} {1}{2}  签名 {3}", "   texture TexID {0,-6} {1}{2}  signature {3}",
                "   テクスチャ TexID {0,-6} {1}{2}  シグネチャ {3}");
            Add("   部位 {0,-24} {1}{2}", "   part {0,-24} {1}{2}",
                "   部位 {0,-24} {1}{2}");
            Add("  [HDR] TexID {0}: {1}x{2} → {3}x{4}，{5} → {6} 字节（线性空间盒式缩放，格式仍是 HDR）", "  [HDR] TexID {0}: {1}x{2} → {3}x{4}, {5} → {6} bytes (linear-space box scale, format stays HDR)",
                "  [HDR] TexID {0}: {1}x{2} → {3}x{4}、{5} → {6} バイト（リニア空間のボックス縮小、形式は HDR のまま）");
            Add("  [HDR] TexID {0}: {1}x{2} 未超过上限 {3}，原样保留（{4} 字节）", "  [HDR] TexID {0}: {1}x{2} within limit {3}, kept as-is ({4} bytes)",
                "  [HDR] TexID {0}: {1}x{2} は上限 {3} 以内のため、そのまま保持（{4} バイト）");
            Add("  [HDR] TexID {0}: 重新编码后没有更小（{1} → {2} 字节），原样保留", "  [HDR] TexID {0}: re-encode was not smaller ({1} → {2} bytes), kept as-is",
                "  [HDR] TexID {0}: 再エンコードしても小さくならず（{1} → {2} バイト）、そのまま保持");
            Add("  [UPDBG] TexID {0} cls={1} 源={2}x{3} cap={4} up={5} sc={6:0.###} ", "  [UPDBG] TexID {0} cls={1} src={2}x{3} cap={4} up={5} sc={6:0.###} ",
                "  [UPDBG] TexID {0} cls={1} 元={2}x{3} cap={4} up={5} sc={6:0.###} ");
            Add("  [不匹配服装] 与预设匹配度 {0:0.0}%（低于 {1:0.0}%）→ 按通用设置 {2}{3} 整卡压", "  [Outfit Mismatch] preset match {0:0.0}% (below {1:0.0}%) → compress whole card with generic settings {2}{3}",
                "  [コーデ不一致] プリセット一致度 {0:0.0}%（{1:0.0}% 未満）→ 汎用設定 {2}{3} でカード全体を圧縮");
            Add("  [人物卡] 角色本体 + {0} 套换装：{1} 张贴图 / {2} 个「组·部位」组合", "  [Chara Card] character body + {0} outfits: {1} textures / {2} \"group·part\" combos",
                "  [キャラカード] 本体 + 衣装 {0} 着：{1} 枚のテクスチャ / {2} 個の「グループ·部位」組み合わせ");
            Add("  [保留] TexID {0}: {1} —— {2}", "  [Keep] TexID {0}: {1} —— {2}",
                "  [保持] TexID {0}: {1} —— {2}");
            Add("  [共用] TexID {0}: {1} 张部位共享 → 按 {2}{3} 处理", "  [Shared] TexID {0}: shared by {1} parts → handled as {2}{3}",
                "  [共用] TexID {0}: {1} 部位で共有 → {2}{3} として処理");
            Add("  [同款] 与预设匹配度 {0:0.0}% ✓ 按预设的部位/单张贴图规则处理", "  [Same Outfit] preset match {0:0.0}% ✓ handled by the preset's part / per-texture rules",
                "  [同コーデ] プリセット一致度 {0:0.0}% ✓ プリセットの部位／単体テクスチャ規則で処理");
            Add("  [强制JPEG] TexID {0}（{1}，{2}）alpha{3}", "  [Force JPEG] TexID {0} ({1}, {2}) alpha{3}",
                "  [JPEG 強制] TexID {0}（{1}、{2}）alpha{3}");
            Add("  [测试] 强制转 JPEG {0} 张，其中 alpha 非平坦（**丢了 alpha**）{1} 张", "  [Test] forced {0} textures to JPEG, of which {1} had non-flat alpha (**alpha lost**)",
                "  [テスト] JPEG へ強制変換 {0} 枚、うち alpha が非フラット（**alpha を喪失**）{1} 枚");
            Add("  [独占保护] TexID {0}（{1}）只属于「{2}」→ 最大边放宽到 {3}", "  [Exclusive Protection] TexID {0} ({1}) belongs only to \"{2}\" → max edge relaxed to {3}",
                "  [独占保護] TexID {0}（{1}）は「{2}」専用 → 最大辺を {3} に緩和");
            Add("  [直接复制] {0} —— 原样复制到输出目录（不压缩）", "  [Direct Copy] {0} —— copied as-is to the output folder (no compression)",
                "  [直接コピー] {0} —— そのまま出力フォルダへコピー（圧縮なし）");
            Add("  [组过滤] TexID {0}: 属于未选中的 {1} —— 原样保留", "  [Group Filter] TexID {0}: belongs to unselected {1} —— kept as-is",
                "  [グループ絞り込み] TexID {0}: 未選択の {1} に属する —— そのまま保持");
            Add("  [读卡耗时] 文件 {0:F1} MB | 读盘 {1}ms | 解析字典 {2}ms | 判定卡种 {3}ms | ", "  [Card Read Time] file {0:F1} MB | disk read {1}ms | dict parse {2}ms | card type {3}ms | ",
                "  [カード読込時間] ファイル {0:F1} MB | 読み込み {1}ms | 辞書解析 {2}ms | カード種別判定 {3}ms | ");
            Add("  [跳过] TexID {0} ({1}, {2} 字节): {3} —— 这一张原样保留，其余继续", "  [Skip] TexID {0} ({1}, {2} bytes): {3} —— this one is kept as-is, the rest continues",
                "  [スキップ] TexID {0} ({1}、{2} バイト): {3} —— これはそのまま保持し、残りは続行します");
            Add("  [跳过] TexID {0}: HDR 处理失败（{1}），原样保留", "  [Skip] TexID {0}: HDR processing failed ({1}), kept as-is",
                "  [スキップ] TexID {0}: HDR 処理に失敗（{1}）、そのまま保持");
            Add("  [跳过] TexID {0}: {1} 格式本工具不解码，原样保留（{2} 字节）", "  [Skip] TexID {0}: {1} format is not decoded by this tool, kept as-is ({2} bytes)",
                "  [スキップ] TexID {0}: {1} 形式は本ツールではデコードしないため、そのまま保持（{2} バイト）");
            Add("  [非alpha转JPEG] TexID {0}（{1}，{2}）shader={3}", "  [Non-alpha to JPEG] TexID {0} ({1}, {2}) shader={3}",
                "  [非 alpha の JPEG 化] TexID {0}（{1}、{2}）shader={3}");
            Add("  [预览耗时] TexID {0} 解码+缩放 {1}ms", "  [Preview Time] TexID {0} decode+scale {1}ms",
                "  [プレビュー時間] TexID {0} デコード+縮小 {1}ms");
            Add("  [预设] TexID {0}: 单张指定 {1}{2}", "  [Preset] TexID {0}: per-texture override {1}{2}",
                "  [プリセット] TexID {0}: 単体指定 {1}{2}");
            Add("  {0,-10} -> {1,-5} 最大边 {2}", "  {0,-10} -> {1,-5} max edge {2}",
                "  {0,-10} -> {1,-5} 最大辺 {2}");
            Add("  {0,-24} {1,3} 张 {2,11}  独享 {3} 张/{4,10}  材质 {5}", "  {0,-24} {1,3} tex {2,11}  exclusive {3} tex/{4,10}  material {5}",
                "  {0,-24} {1,3} 枚 {2,11}  専有 {3} 枚/{4,10}  マテリアル {5}");
            Add("  {0,-24} （这张卡没用到）", "  {0,-24} (not used by this card)",
                "  {0,-24}（このカードでは未使用）");
            Add("  {0} 张贴图 / {1}；卡片 {2}", "  {0} textures / {1}; card {2}",
                "  {0} 枚のテクスチャ / {1}；カード {2}");
            Add("  …已写出，正在校验结构", "  …written, validating structure",
                "  …書き出し完了、構造を検証中");
            Add("  …开始压缩（读取中，大卡会比较久）", "  …compression started (reading; large cards take longer)",
                "  …圧縮を開始（読み込み中、大きいカードは時間がかかります）");
            Add("  …校验完成", "  …validation complete",
                "  …検証完了");
            Add("  【{0}】{1} 个部位 / {2} 张 / {3}", "  [{0}] {1} parts / {2} tex / {3}",
                "  【{0}】{1} 部位 / {2} 枚 / {3}");
            Add("  【放大】{0} 张真放大了（{1} 核，像素 {2} -> {3}，{4:0.0} 倍）：TexID {5}", "  [Upscale] {0} textures actually upscaled ({1} cores, pixels {2} -> {3}, {4:0.0}x): TexID {5}",
                "  【アップスケール】{0} 枚を実際に拡大（{1} コア、ピクセル {2} -> {3}、{4:0.0} 倍）：TexID {5}");
            Add("  【放大】被挡下 {0} 张（数据贴图 {1} 张——法线/掩罩/高度放大没有意义且体积 ×3；", "  [Upscale] {0} blocked ({1} data textures —— upscaling normal/mask/height is meaningless and triples the size;",
                "  【アップスケール】{0} 枚を却下（データテクスチャ {1} 枚——法線/マスク/ハイトの拡大は無意味でサイズが ×3；");
            Add("  提示：放大只在**彩色类**生效（法线/掩罩/高度会被忽略）；且受「最大边」与 4096px 硬上限约束，装不下会自动降级。", "  Note: upscale only applies to **color classes** (normal/mask/height are ignored); it is also bounded by \"max edge\" and the hard 4096px limit, and is downgraded automatically when it does not fit.",
                "  ヒント：アップスケールは**カラー系**でのみ有効です（法線/マスク/ハイトは無視されます）；また「最大辺」と 4096px のハード上限に従い、収まらない場合は自動的に降格します。");
            Add("  没被任何材质引用的贴图 {0} 张 / {1}", "  textures not referenced by any material: {0} / {1}",
                "  どのマテリアルからも参照されていないテクスチャ {0} 枚 / {1}");
            Add("  没被任何部位引用的贴图 {0} 张：", "  textures not referenced by any part: {0}:",
                "  どの部位からも参照されていないテクスチャ {0} 枚：");
            Add("  贴图 {0} 张 / {1}", "  textures {0} / {1}",
                "  テクスチャ {0} 枚 / {1}");
            Add("  贴图级并行：{0} 路（KOITEX_TEXJOBS / texjobs=N 可改）", "  texture-level parallelism: {0} workers (change with KOITEX_TEXJOBS / texjobs=N)",
                "  テクスチャ単位の並列：{0} 並列（KOITEX_TEXJOBS / texjobs=N で変更可）");
            Add("  （其中 {0} 张 JPEG 做了无损哈夫曼再优化，省 {1}，像素逐位不变）", "  (of which {0} JPEGs got lossless Huffman re-optimization, saving {1}, pixels bit-identical)",
                "  （うち {0} 枚の JPEG は可逆ハフマン再最適化を実施、{1} 削減、ピクセルはビット単位で不変）");
            Add("  （其中 {0} 张 PNG 换用自写编码器：自适应滤波 + zlib-ng，无损再省 {1}）", "  (of which {0} PNGs switched to a custom encoder: adaptive filtering + zlib-ng, losslessly saving {1} more)",
                "  （うち {0} 枚の PNG は自作エンコーダに変更：適応フィルタ + zlib-ng、可逆でさらに {1} 削減）");
            Add("  （其中 {0} 张数据贴图走了面积平均{1}）", "  (of which {0} data textures used area averaging{1})",
                "  （うち {0} 枚のデータテクスチャは面積平均{1}）");
            Add("  （其中 {0} 张是「独占贴图」（只被一个部位引用），已按 {1} 的放宽上限处理）", "  (of which {0} are \"exclusive textures\" (referenced by only one part), handled with the relaxed limit of {1})",
                "  （うち {0} 枚は「独占テクスチャ」（1 部位のみ参照）、{1} の緩和上限で処理）");
            Add("  （另有 {0} 张按规则重编码了，但没比原字节更小 → 保留原字节，卡片不会变大）", "  (another {0} were re-encoded per the rules but are not smaller than the original bytes → original bytes kept, the card does not grow)",
                "  （別に {0} 枚は規則どおり再エンコードしましたが元のバイト数より小さくならず → 元バイトを保持、カードは大きくなりません）");
            Add("  （另有 {0} 张是灰度/灰度+alpha，改成 2/1 通道无损存储，省 {1}：TexID {2}）", "  (another {0} were grayscale / grayscale+alpha and were stored losslessly as 2/1 channels, saving {1}: TexID {2})",
                "  （別に {0} 枚はグレースケール／グレースケール+alpha のため、2/1 チャンネルの可逆保存に変更、{1} 削減：TexID {2}）");
            Add("  （另有 {0} 张走「不经 GDI+ 的纯无损路径」：直接对原字节做无损再压缩+降通道，省 {1}，像素逐位不变）", "  (another {0} took the \"pure lossless path that skips GDI+\": lossless recompression + channel reduction on the original bytes, saving {1}, pixels bit-identical)",
                "  （別に {0} 枚は「GDI+ を通さない純可逆パス」を適用：元バイトに直接可逆再圧縮+チャンネル削減、{1} 削減、ピクセルはビット単位で不変）");
            Add("  （降噪模式 {0}：命中脏源 {1} 张，实际降噪 {2} 张）", "  (denoise mode {0}: {1} dirty sources matched, {2} actually denoised)",
                "  （ノイズ除去モード {0}：ダーティソース検出 {1} 枚、実際に除去 {2} 枚）");
            Add(" / 界面语言已切换", " / UI language switched",
                " / 表示言語を切り替えました");
            Add(" —— 不匹配，已原样复制到输出目录", " —— no match, copied as-is to the output folder",
                " —— 不一致のため、そのまま出力フォルダへコピーしました");
            Add(" —— 产出校验失败: ", " —— output validation failed: ",
                " —— 出力の検証に失敗: ");
            Add(" —— 处理不了的卡，已原样复制到输出目录", " —— card cannot be processed, copied as-is to the output folder",
                " —— 処理できないカードのため、そのまま出力フォルダへコピーしました");
            Add(" —— 处理不了，已原样复制到输出目录", " —— cannot be processed, copied as-is to the output folder",
                " —— 処理できないため、そのまま出力フォルダへコピーしました");
            Add(" 放得下", " fits",
                " 収まります");
            Add(" 溢出!", " overflow!",
                " オーバーフロー!");
            Add(" 读取失败: ", " read failed: ",
                " 読み込みに失敗: ");
            Add(" 跳过 —— ", " skip —— ",
                " スキップ —— ");
            Add(" 跳过（不是同款服装）—— ", " skip (not the same outfit) —— ",
                " スキップ（同じコーデではない）—— ");
            Add(" 跳过（无贴图）—— ", " skip (no texture) —— ",
                " スキップ（テクスチャなし）—— ");
            Add("AC 表码字数({0})≠ 符号数({1})", "AC table codeword count ({0}) ≠ symbol count ({1})",
                "AC 表のコード語数({0})≠ シンボル数({1})");
            Add("DC 表码字数({0})≠ 符号数({1})", "DC table codeword count ({0}) ≠ symbol count ({1})",
                "DC 表のコード語数({0})≠ シンボル数({1})");
            Add("DRAG1  : 日志列宽 {0} → {1}（一次拖动总位移 -80，应为 {2}）", "DRAG1  : log column width {0} → {1} (single drag total offset -80, expected {2})",
                "DRAG1  : ログ列幅 {0} → {1}（1 回のドラッグの総移動量 -80、期待値 {2}）");
            Add("G3ONLY : 只勾「{0}」→ {1} 行", "G3ONLY : only \"{0}\" checked → {1} lines",
                "G3ONLY : 「{0}」のみチェック → {1} 行");
            Add("G3PART : 第 {0} 行 → {1}", "G3PART : line {0} → {1}",
                "G3PART : {0} 行目 → {1}");
            Add("G3TICK : {0}s 日志 {1} 行；末行: {2}", "G3TICK : {0}s log {1} lines; last line: {2}",
                "G3TICK : {0}s ログ {1} 行；最終行: {2}");
            Add("HDR 原样", "HDR as-is",
                "HDR そのまま");
            Add("JPEG 质量 q{0}", "JPEG quality q{0}",
                "JPEG 品質 q{0}");
            Add("LOADWAIT3: 读人物卡用了 {0}ms（后台线程）", "LOADWAIT3: reading chara card took {0}ms (background thread)",
                "LOADWAIT3: キャラカードの読み込みに {0}ms（バックグラウンドスレッド）");
            Add("LOADWAIT: 读部位用了 {0}ms（后台线程）", "LOADWAIT: reading part took {0}ms (background thread)",
                "LOADWAIT: 部位の読み込みに {0}ms（バックグラウンドスレッド）");
            Add("PREVH   : 初始 {0}", "PREVH   : Initial {0}",
                "PREVH   : 初期 {0}");
            Add("PREVH   : 实际预览框 {0}", "PREVH   : Actual preview box {0}",
                "PREVH   : 実際のプレビュー枠 {0}");
            Add("PREVH   : 往上拖 60 → 行高 {0}", "PREVH   : Drag up 60 → row height {0}",
                "PREVH   : 上へ 60 ドラッグ → 行高 {0}");
            Add("PREVH   : 往下拖 100 → 行高 {0}", "PREVH   : Drag down 100 → row height {0}",
                "PREVH   : 下へ 100 ドラッグ → 行高 {0}");
            Add("PREVH3  : 第 3 页往上拖 50 → 行高 {0}", "PREVH3  : Page 3 drag up 50 → row height {0}",
                "PREVH3  : 3 ページ目を上へ 50 ドラッグ → 行高 {0}");
            Add("TexID {0,-4} 原 {1,9}  GDI+ {2,9}  自写 {3,9}  ({4:+#0.0%;-#0.0%}）  {5}", "TexID {0,-4} orig {1,9}  GDI+ {2,9}  own {3,9}  ({4:+#0.0%;-#0.0%})  {5}",
                "TexID {0,-4} 元 {1,9}  GDI+ {2,9}  自前 {3,9}  （{4:+#0.0%;-#0.0%}）  {5}");
            Add("TexID {0,-5} {1,-10} {2}x{3}  块状度 {4:F3} {5}", "TexID {0,-5} {1,-10} {2}x{3}  blockiness {4:F3} {5}",
                "TexID {0,-5} {1,-10} {2}x{3}  ブロック度 {4:F3} {5}");
            Add("TexID {0}   {1}×{2}（缓存）", "TexID {0}   {1}×{2} (cached)",
                "TexID {0}   {1}×{2}（キャッシュ）");
            Add("TexID {0} (HDR): 没有缩小（已在下限内或编码后更大），原样保留", "TexID {0} (HDR): not downscaled (already within the lower limit, or larger after encoding), kept as is",
                "TexID {0} (HDR): 縮小なし（下限内、またはエンコード後の方が大きいため）、そのまま保持");
            Add("TexID {0} ({1}): 本工具不认识这种格式，原样保留", "TexID {0} ({1}): this tool does not recognize this format, kept as is",
                "TexID {0} ({1}): 本ツールが認識しない形式のため、そのまま保持");
            Add("TexID {0} ({1}, {2} 字节): 解码失败 —— {3}", "TexID {0} ({1}, {2} bytes): decode failed —— {3}",
                "TexID {0} ({1}, {2} バイト): デコード失敗 —— {3}");
            Add("TexID {0} ({1}, {2}): 属于未选中的组", "TexID {0} ({1}, {2}): belongs to an unselected group",
                "TexID {0} ({1}, {2}): 未選択のグループに属します");
            Add("UiSetAllParts({0},{1},{2},{3},{4}) 命中 {5} 行，行数 {6}", "UiSetAllParts({0},{1},{2},{3},{4}) matched {5} rows, row count {6}",
                "UiSetAllParts({0},{1},{2},{3},{4}) 命中 {5} 行、行数 {6}");
            Add("[OK] 校验通过：", "[OK] verification passed:",
                "[OK] 検証に合格しました：");
            Add("[X] 失败 —— ", "[X] failed —— ",
                "[X] 失敗 —— ");
            Add("[X] 失败: ", "[X] failed: ",
                "[X] 失敗: ");
            Add("[X] 校验失败: ", "[X] verification failed: ",
                "[X] 検証失敗: ");
            Add("[X] 校验失败：", "[X] verification failed:",
                "[X] 検証失敗：");
            Add("[X] 这不是人物卡：卡里没有 组·部位 结构。人物卡文件名一般是 Koikatu_F_*.png", "[X] not a chara card: the card has no group·part structure. Chara card filenames are usually Koikatu_F_*.png",
                "[X] キャラカードではありません：カードに グループ·部位 構造がありません。キャラカードのファイル名は通常 Koikatu_F_*.png です");
            Add("[复制] ", "[Copy] ",
                "[コピー] ");
            Add("[复制] 这张卡处理不了，已原样复制到输出目录（未压缩，体积不变）。", "[Copy] this card cannot be processed; copied as is to the output folder (uncompressed, size unchanged).",
                "[コピー] このカードは処理できないため、そのまま出力フォルダへコピーしました（未圧縮、サイズは変わりません）。");
            Add("[就近] 「{0}」→ {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}（顺带清掉 {6} 条单张贴图指定）", "[Nearest] 「{0}」→ {1} / {2} / sharpen {3} {4} / grayscale 2ch {5} (also clears {6} per-texture overrides)",
                "[最近傍] 「{0}」→ {1} / {2} / シャープ {3} {4} / グレースケール2ch {5}（併せて単体テクスチャ指定 {6} 件を消去）");
            Add("[组过滤] 全部 {0} 行 {1}", "[Group filter] all {0} rows {1}",
                "[グループ絞り込み] 全 {0} 行 {1}");
            Add("[统一] {0} → {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}（{7} 个条目，顺带清掉 {8} 条单张贴图指定）", "[Unify] {0} → {1} / {2} / sharpen {3} {4} / grayscale 2ch {5}{6} ({7} entries, also clears {8} per-texture overrides)",
                "[一括統一] {0} → {1} / {2} / シャープ {3} {4} / グレースケール2ch {5}{6}（{7} 件のエントリ、併せて単体テクスチャ指定 {8} 件を消去）");
            Add("[统一·仅已启用] {0} 个已勾上的条目 → {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}", "[Unify·enabled only] {0} checked entries → {1} / {2} / sharpen {3} {4} / grayscale 2ch {5}{6}",
                "[一括統一·有効のみ] チェック済み {0} 件 → {1} / {2} / シャープ {3} {4} / グレースケール2ch {5}{6}");
            Add("\n\n批处理用法：\nKoiCardTexTool.exe batch <输入目录> <输出目录> 1024 plan=", "\n\nBatch usage:\nKoiCardTexTool.exe batch <input folder> <output folder> 1024 plan=",
                "\n\nバッチの使い方：\nKoiCardTexTool.exe batch <入力フォルダ> <出力フォルダ> 1024 plan=");
            Add("\r\n    明细：{0} 行，首行 TexID={1}", "\r\n    Details: {0} rows, first row TexID={1}",
                "\r\n    明細：{0} 行、先頭行 TexID={1}");
            Add("preset\\batch：{0} 个预设{1}", "preset\\batch: {0} presets{1}",
                "preset\\batch：{0} 件のプリセット{1}");
            Add("resize -> {0}x{1}  {2} 字节（再解码 {3}x{4} ok，sum={5:0.0000}）", "resize -> {0}x{1}  {2} bytes (re-decode {3}x{4} ok, sum={5:0.0000})",
                "resize -> {0}x{1}  {2} バイト（再デコード {3}x{4} ok、sum={5:0.0000}）");
            Add("roundtrip 同尺寸重编码: {0}（差异 {1} 个分量）编码后 {2} 字节（原 {3}）", "roundtrip same-size re-encode: {0} ({1} components differ) encoded {2} bytes (orig {3})",
                "roundtrip 同サイズ再エンコード: {0}（差異 {1} 成分）エンコード後 {2} バイト（元 {3}）");
            Add("{0,-52} 原样复制（处理不了的卡，体积不变）", "{0,-52} copied as is (card cannot be processed, size unchanged)",
                "{0,-52} そのままコピー（処理できないカード、サイズは変わりません）");
            Add("{0,-52} 失败 —— {1}", "{0,-52} failed —— {1}",
                "{0,-52} 失敗 —— {1}");
            Add("{0,-52} 跳过（{1}）", "{0,-52} skipped ({1})",
                "{0,-52} スキップ（{1}）");
            Add("{0}  〖{1}〗  名称：{2}", "{0}  〖{1}〗  name: {2}",
                "{0}  〖{1}〗  名称：{2}");
            Add("{0}  〖{1}〗 版本 {2} / 数据 {3}  名称：{4}  部件 {5} 个", "{0}  〖{1}〗 version {2} / data {3}  name: {4}  {5} parts",
                "{0}  〖{1}〗 バージョン {2} / データ {3}  名称：{4}  パーツ {5} 個");
            Add("{0} {1}x{2} 通道{3} 滤波{4} 原始{5}B→压缩{6}B", "{0} {1}x{2} channels {3} filter {4} orig {5}B→compressed {6}B",
                "{0} {1}x{2} チャンネル{3} フィルタ{4} 元{5}B→圧縮{6}B");
            Add("{0} {1}{2}{3}{2}{2}—— 把这一段发给开发者即可定位问题。{2}EXE: {4}{2}OS: {5}{2}CLR: {6}{2}区域: {7} / {8}", "{0} {1}{2}{3}{2}{2}—— send this section to the developer to locate the problem.{2}EXE: {4}{2}OS: {5}{2}CLR: {6}{2}locale: {7} / {8}",
                "{0} {1}{2}{3}{2}{2}—— この部分を開発者に送ると問題を特定できます。{2}EXE: {4}{2}OS: {5}{2}CLR: {6}{2}ロケール: {7} / {8}");
            Add("{0} → {1}   {2} → {3} 字节  ({4:+#0.0%;-#0.0%}）", "{0} → {1}   {2} → {3} bytes  ({4:+#0.0%;-#0.0%})",
                "{0} → {1}   {2} → {3} バイト  （{4:+#0.0%;-#0.0%}）");
            Add("{0} → {1}   {2} → {3} 字节（{4:+#0.0%;-#0.0%}）", "{0} → {1}   {2} → {3} bytes ({4:+#0.0%;-#0.0%})",
                "{0} → {1}   {2} → {3} バイト（{4:+#0.0%;-#0.0%}）");
            Add("{0} → 图={1} 尺寸={2} 说明={3}", "{0} → img={1} size={2} note={3}",
                "{0} → 画像={1} サイズ={2} 説明={3}");
            Add("{0} 张 / {1}", "{0} images / {1}",
                "{0} 枚 / {1}");
            Add("{0} 张 / {1} / 最大 {2}px", "{0} images / {1} / max edge {2}px",
                "{0} 枚 / {1} / 最大辺 {2}px");
            Add("{0} 张人物卡；勾选的「组·部位」{1} 个，单张贴图指定 {2} 张", "{0} chara cards; {1} 「group·part」 checked, {2} per-texture overrides",
                "{0} 枚のキャラカード；チェック済みの「グループ·部位」{1} 件、単体テクスチャ指定 {2} 件");
            Add("{0} 张卡；部位规则 {1} 条，单贴图规则 {2} 条", "{0} cards; {1} part rules, {2} per-texture rules",
                "{0} 枚のカード；部位ルール {1} 件、単体テクスチャルール {2} 件");
            Add("{0}.{1}: {2} → {3}（要求 +{4}，实际 +{5}）模式={6} 总宽={7} 可视={8}", "{0}.{1}: {2} → {3} (requested +{4}, actual +{5}) mode={6} total width={7} visible={8}",
                "{0}.{1}: {2} → {3}（要求 +{4}、実際 +{5}）モード={6} 総幅={7} 可視={8}");
            Add("{0}: {1} 张贴图 / {2}", "{0}: {1} textures / {2}",
                "{0}: {1} 枚のテクスチャ / {2}");
            Add("{0}: 共 {1} 个 .png（{2}子文件夹）", "{0}: {1} .png in total ({2} subfolders)",
                "{0}: 合計 {1} 個の .png（サブフォルダ {2} 個）");
            Add("{0}：{1} 个「组·部位」统一为 {2} / {3} / 锐化 {4} {5} / 灰度2通道 {6}{7}", "{0}: unified {1} 「group·part」 to {2} / {3} / sharpen {4} {5} / grayscale 2ch {6}{7}",
                "{0}：{1} 件の「グループ·部位」を {2} / {3} / シャープ {4} {5} / グレースケール2ch {6}{7} に統一");
            Add("{0}：{1} 张贴图 / {2}，有贴图的部位 {3} 个", "{0}: {1} textures / {2}, {3} parts with textures",
                "{0}：{1} 枚のテクスチャ / {2}、テクスチャのある部位 {3} 個");
            Add("{0}：共 {1}ms（读盘+解析），整文件键扫描 {2} 次 / {3}ms，{4} 个部位 / {5} 张贴图{6}", "{0}: {1}ms total (read+parse), whole-file key scan {2} times / {3}ms, {4} parts / {5} textures{6}",
                "{0}：合計 {1}ms（読み込み+解析）、ファイル全体のキー走査 {2} 回 / {3}ms、部位 {4} 個 / テクスチャ {5} 枚{6}");
            Add("▾ 人物卡组过滤（点这里收起）", "▾ Chara card group filter (click here to collapse)",
                "▾ キャラカードグループ絞り込み（ここをクリックで折りたたみ）");
            Add("　独占贴图放宽到 {0}", "　Exclusive texture relaxed to {0}",
                "　独占テクスチャを {0} まで緩和");
            Add("　锐化 {0}%{1}", "　Sharpen {0}%{1}",
                "　シャープ {0}%{1}");
            Add("一个「组·部位」都没勾。", "No 「group·part」 is checked.",
                "「グループ·部位」が 1 つもチェックされていません。");
            Add("一个部位都没勾。请至少勾一个要压的部位。", "No part is checked. Check at least one part to compress.",
                "部位が 1 つもチェックされていません。圧縮する部位を 1 つ以上チェックしてください。");
            Add("不含", "Not containing",
                "含まない");
            Add("不是同款服装", "Not the same outfit",
                "同じコーデではありません");
            Add("与预设不是同款服装（匹配度 {0:0.0}%），且没有可用的通用设置", "Not the same outfit as the preset (match {0:0.0}%), and no general settings available",
                "プリセットと同じコーデではありません（一致度 {0:0.0}%）、利用できる共通設定もありません");
            Add("与预设不是同款服装（匹配度 {0:0.0}%，要求 ≥{1:0.0}%）", "Not the same outfit as the preset (match {0:0.0}%, requires ≥{1:0.0}%)",
                "プリセットと同じコーデではありません（一致度 {0:0.0}%、要求 ≥{1:0.0}%）");
            Add("产出 {0} vs 原 {1}（{2}）", "Output {0} vs orig {1} ({2})",
                "出力 {0} vs 元 {1}（{2}）");
            Add("人物卡完成：成功 {0} 张，跳过 {1} 张，原样复制 {2} 张，失败 {3} 张；{4} -> {5}（{6:0.0}%）", "Chara cards done: {0} succeeded, {1} skipped, {2} copied as is, {3} failed; {4} -> {5} ({6:0.0}%)",
                "キャラカード完了：成功 {0} 枚、スキップ {1} 枚、そのままコピー {2} 枚、失敗 {3} 枚；{4} -> {5}（{6:0.0}%）");
            Add("人物卡：{0}（{1}）；本次 {2} 张卡", "Chara cards: {0} ({1}); {2} cards this run",
                "キャラカード：{0}（{1}）；今回 {2} 枚のカード");
            Add("什么都没选", "Nothing selected",
                "何も選択されていません");
            Add("使用预设里的类型表（{0} 个类；未显式给 plan=）", "Using the type table from the preset ({0} classes; no explicit plan=)",
                "プリセット内のタイプ表を使用（{0} クラス；plan= の明示指定なし）");
            Add("先在左边的下拉框里选一个预设，再勾这个。\n（「不使用预设」不能设为默认。）", "First pick a preset in the dropdown on the left, then check this.\n(「Do not use preset」 cannot be set as the default.)",
                "先に左のドロップダウンでプリセットを選んでから、これをチェックしてください。\n（「プリセットを使用しない」は既定にできません。）");
            Add("全部 {0} 个「组·部位」{1}", "All {0} 「group·part」{1}",
                "全 {0} 件の「グループ·部位」{1}");
            Add("共 {0} 张贴图 / {1}；有贴图的部位 {2} 个", "{0} textures in total / {1}; {2} parts with textures",
                "合計 {0} 枚のテクスチャ / {1}；テクスチャのある部位 {2} 個");
            Add("共 {0} 张贴图 / {1}；角色本体 + {2} 套换装", "{0} textures in total / {1}; base character + {2} outfits",
                "合計 {0} 枚のテクスチャ / {1}；キャラ本体 + 着替え {2} セット");
            Add("共检查 {0} 张，块状度 > {1:F2} 的 {2} 张", "Checked {0} images in total, blockiness > {1:F2}: {2}",
                "合計 {0} 枚を検査、ブロック度 > {1:F2} は {2} 枚");
            Add("其中 {0} 张是「处理不了的卡 → 原样复制」（未压缩，不计入上面的体积）。", "Of these, {0} are 「unprocessable card → copied as is」 (uncompressed, not counted in the size above).",
                "うち {0} 枚は「処理できないカード → そのままコピー」（未圧縮、上記のサイズには含みません）。");
            Add("勾上", "Check",
                "チェック");
            Add("单服装卡细分压缩：{0} 个部位参与，质量 q{1}", "Single outfit card fine compression: {0} parts involved, quality q{1}",
                "単一コーデカードの細分圧縮：{0} 部位が対象、品質 q{1}");
            Add("卡片: {0} -> {1} 字节（{2:0.0}%）", "Card: {0} -> {1} bytes ({2:0.0}%)",
                "カード: {0} -> {1} バイト（{2:0.0}%）");
            Add("卡片文件不存在。", "Card file not found.",
                "カードファイルが存在しません。");
            Add("卡片：{0}（{1}）", "Card: {0} ({1})",
                "カード：{0}（{1}）");
            Add("卡路径无效，无法预览", "Invalid card path, cannot preview",
                "カードパスが無効のためプレビューできません");
            Add("压缩 {0}/{1}: {2}", "Compress {0}/{1}: {2}",
                "圧縮 {0}/{1}: {2}");
            Add("压缩中… {0}/{1}", "Compressing… {0}/{1}",
                "圧縮中… {0}/{1}");
            Add("另有 {0} 张不匹配的卡原样复制到输出目录（未压缩，体积不变）。", "{0} non-matching cards copied as-is to the output folder (not compressed, size unchanged).",
                "ほかに不一致のカード {0} 枚をそのまま出力フォルダへコピーしました（未圧縮、サイズは変わりません）。");
            Add("另有 {0} 张处理不了的卡原样复制到输出目录（未压缩，体积不变）。", "{0} unprocessable cards copied as-is to the output folder (not compressed, size unchanged).",
                "ほかに処理できないカード {0} 枚をそのまま出力フォルダへコピーしました（未圧縮、サイズは変わりません）。");
            Add("只压缩当前这一件。", "Compress only this one.",
                "これ 1 件だけを圧縮します。");
            Add("可见={0} 图={1} 缩放={2:0}% 说明={3}", "Visible={0} Img={1} Scale={2:0}% Note={3}",
                "可視={0} 画像={1} 縮小率={2:0}% 説明={3}");
            Add("合计 {0} 张贴图。", "Total {0} textures.",
                "合計 {0} 枚のテクスチャ。");
            Add("合计：GDI+ {0}  自写 {1}  → {2:+#0.0%;-#0.0%}", "Total: GDI+ {0}  custom {1}  → {2:+#0.0%;-#0.0%}",
                "合計：GDI+ {0}  自作 {1}  → {2:+#0.0%;-#0.0%}");
            Add("合计：成功 {0} / 失败 {1}；{2} -> {3}（{4:0.0}%）", "Total: OK {0} / failed {1}; {2} -> {3} ({4:0.0}%)",
                "合計：成功 {0} / 失敗 {1}；{2} -> {3}（{4:0.0}%）");
            Add("含", "Incl.",
                "含む");
            Add("失败清单（{0} 张）：", "Failure list ({0}):",
                "失敗リスト（{0} 枚）：");
            Add("失败：", "Failed:",
                "失敗：");
            Add("套用预设：", "Apply preset:",
                "プリセット適用：");
            Add("套用预设：{0}（部位规则 {1} 条，单贴图规则 {2} 条，来自 {3}）", "Apply preset: {0} ({1} part rules, {2} per-texture rules, from {3})",
                "プリセット適用：{0}（部位ルール {1} 件、単体テクスチャルール {2} 件、取得元 {3}）");
            Add("完成：{0} -> {1}（{2:0.0}%）→ {3}", "Done: {0} -> {1} ({2:0.0}%) → {3}",
                "完了：{0} -> {1}（{2:0.0}%）→ {3}");
            Add("完成：成功 {0} 张，跳过（无贴图）{1} 张，原样复制 {2} 张，失败 {3} 张；{4} -> {5}（{6:0.0}%）", "Done: OK {0}, skipped (no texture) {1}, copied as-is {2}, failed {3}; {4} -> {5} ({6:0.0}%)",
                "完了：成功 {0} 枚、スキップ（テクスチャなし）{1} 枚、そのままコピー {2} 枚、失敗 {3} 枚；{4} -> {5}（{6:0.0}%）");
            Add("将对 {0} 张卡套用当前设置（{1}子文件夹），输出到\n{2}\n\n不匹配的服装：{3}", "Current settings will be applied to {0} cards ({1} subfolders), output to\n{2}\n\nNon-matching outfits: {3}",
                "{0} 枚のカードに現在の設定を適用します（{1} サブフォルダ）、出力先\n{2}\n\n不一致の服装：{3}");
            Add("将对 {0} 张卡套用当前设置（{1}子文件夹），输出到\n{2}\n{3}[zip]\\\n\n不匹配的服装：{4}", "Current settings will be applied to {0} cards ({1} subfolders), output to\n{2}\n{3}[zip]\\\n\nNon-matching outfits: {4}",
                "{0} 枚のカードに現在の設定を適用します（{1} サブフォルダ）、出力先\n{2}\n{3}[zip]\\\n\n不一致の服装：{4}");
            Add("展开={0} 第1页 {1}/{2} 可见 第2页 {3}/{4} 第3页 {5}/{6} 表格列 {7}/{8} 列宽={9}", "Expanded={0} Page1 {1}/{2} visible Page2 {3}/{4} Page3 {5}/{6} table cols {7}/{8} col width={9}",
                "展開={0} 1ページ {1}/{2} 可視 2ページ {3}/{4} 3ページ {5}/{6} テーブル列 {7}/{8} 列幅={9}");
            Add("已保存预设 ", "Saved preset ",
                "プリセットを保存しました ");
            Add("已保存预设 {0}：部位规则 {1} 条，单贴图规则 {2} 条", "Saved preset {0}: {1} part rules, {2} per-texture rules",
                "プリセット {0} を保存しました：部位ルール {1} 件、単体テクスチャルール {2} 件");
            Add("已保存预设：", "Saved preset:",
                "保存したプリセット：");
            Add("已保存：", "Saved:",
                "保存しました：");
            Add("已保存：\n", "Saved:\n",
                "保存しました：\n");
            Add("已写出预设 {0}：部位规则 {1} 条，单贴图规则 {2} 条，整卡签名指纹 {3} 个，通用设置 {4}", "Wrote preset {0}: {1} part rules, {2} per-texture rules, {3} whole-card signature fingerprints, {4} general settings",
                "プリセット {0} を書き出しました：部位ルール {1} 件、単体テクスチャルール {2} 件、カード全体シグネチャ指紋 {3} 件、共通設定 {4}");
            Add("已勾上", "Checked",
                "チェックしました");
            Add("已取消", "Unchecked",
                "チェックを外しました");
            Add("已取消。完成 {0} 张，失败 {1} 张。", "Cancelled. {0} done, {1} failed.",
                "キャンセルしました。完了 {0} 枚、失敗 {1} 枚。");
            Add("已取消第 {0}/{1} 行；现在启用 {2} 行", "Unchecked row {0}/{1}; {2} rows now enabled",
                "{0}/{1} 行目のチェックを外しました。現在 {2} 行が有効です");
            Add("已取消默认预设（下次启动回到「仅优化」）", "Default preset cleared (next launch returns to \"Optimize only\")",
                "既定のプリセットを解除しました（次回起動時は「最適化のみ」に戻ります）");
            Add("已启用的 {0} 个「组·部位」统一为 {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}", "Enabled {0} \"group·part\" entries unified to {1} / {2} / sharpen {3} {4} / grayscale 2ch {5}{6}",
                "有効な {0} 件の「グループ・部位」を {1} / {2} / シャープ {3} {4} / グレースケール2ch {5}{6} に統一しました");
            Add("已套用预设「{0}」（原卡 {1}，存于 {2}）{3}{4}{5}", "Applied preset \"{0}\" (source card {1}, stored in {2}){3}{4}{5}",
                "プリセット「{0}」を適用しました（元カード {1}、保存先 {2}）{3}{4}{5}");
            Add("已开启：会连同目录下的同款服装一起压缩。", "Enabled: same-type outfits in the folder will also be compressed.",
                "有効：フォルダ内の同型の服装もまとめて圧縮します。");
            Add("已恢复默认列宽（{0} 个表格）", "Default column widths restored ({0} tables)",
                "既定の列幅に戻しました（{0} 個のテーブル）");
            Add("已把{0}的「{1}」类型 {2} 张贴图设为：{3} / {4} / 锐化 {5} {6} / 灰度2通道 {7}{8}", "Set {0}'s \"{1}\" type {2} textures to: {3} / {4} / sharpen {5} {6} / grayscale 2ch {7}{8}",
                "{0} の「{1}」タイプ {2} 枚のテクスチャを設定しました：{3} / {4} / シャープ {5} {6} / グレースケール2ch {7}{8}");
            Add("已把「放大」设为 {0}：写进 {1} 个类{2}", "Set \"upscale\" to {0}: written to {1} classes{2}",
                "「アップスケール」を {0} に設定しました：{1} 個のクラスに書き込み{2}");
            Add("已把所有类型统一设为：{0} / {1}", "Unified all types to: {0} / {1}",
                "すべてのタイプを {0} / {1} に一括統一しました");
            Add("已把所有部位统一设为：{0} / {1} / 锐化 {2} {3} / 灰度2通道 {4}{5}", "Unified all parts to: {0} / {1} / sharpen {2} {3} / grayscale 2ch {4}{5}",
                "すべての部位を {0} / {1} / シャープ {2} {3} / グレースケール2ch {4}{5} に一括統一しました");
            Add("已把本部位 {0} 张贴图统一设为：{1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}", "Unified this part's {0} textures to: {1} / {2} / sharpen {3} {4} / grayscale 2ch {5}{6}",
                "この部位の {0} 枚のテクスチャを {1} / {2} / シャープ {3} {4} / グレースケール2ch {5}{6} に一括統一しました");
            Add("已拖入 {0} 张卡（{1} …）　—　点「② 开始压缩」批量处理", "Dropped {0} cards ({1} …)　—　click \"② Start compression\" for batch processing",
                "{0} 枚のカードをドロップしました（{1} …）　—　「② 圧縮開始」で一括処理");
            Add("已记住列宽：{0} 个表格 / {1} 列 → {2}", "Column widths remembered: {0} tables / {1} cols → {2}",
                "列幅を記憶しました：{0} 個のテーブル / {1} 列 → {2}");
            Add("已设到「{0}」：{1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}", "Set to \"{0}\": {1} / {2} / sharpen {3} {4} / grayscale 2ch {5}",
                "「{0}」に設定しました：{1} / {2} / シャープ {3} {4} / グレースケール2ch {5}");
            Add("已读取 {0} 张贴图，{1} 个部位。勾选要压的部位后点「单服装卡细分压缩」。", "Loaded {0} textures, {1} parts. Check the parts to compress, then click \"Per-outfit card fine compression\".",
                "{0} 枚のテクスチャ、{1} 個の部位を読み込みました。圧縮する部位にチェックを入れて「コーデカード個別圧縮」をクリックしてください。");
            Add("已读取预设 {0}：部位规则 {1} 条，单贴图规则落地 {2} 张，未匹配 {3} 张", "Loaded preset {0}: {1} part rules, {2} per-texture rules applied, {3} unmatched",
                "プリセット {0} を読み込みました：部位ルール {1} 件、単体テクスチャルール適用 {2} 枚、未一致 {3} 枚");
            Add("已读取预设：", "Loaded preset:",
                "プリセットを読み込みました：");
            Add("已读取：", "Loaded:",
                "読み込みました：");
            Add("已读取：{0} 张贴图 / {1} 个「组·部位」条目", "Loaded: {0} textures / {1} \"group·part\" entries",
                "読み込みました：{0} 枚のテクスチャ / {1} 件の「グループ・部位」");
            Add("并行处理 {0} 张卡：{1} 路（内存不够可设环境变量 KOITEX_JOBS=2）", "Processing {0} cards in parallel: {1} workers (if memory is short, set env var KOITEX_JOBS=2)",
                "{0} 枚のカードを並列処理：{1} 並列（メモリが足りない場合は環境変数 KOITEX_JOBS=2 を設定）");
            Add("并行处理：{0} 路（jobs=N 可改；每路约需 0.5~1.5 GB 内存）", "Parallel: {0} workers (change with jobs=N; about 0.5~1.5 GB memory each)",
                "並列処理：{0} 並列（jobs=N で変更可、1 並列あたり約 0.5~1.5 GB のメモリが必要）");
            Add("彩色核={0} 细节保护={1} 勾选={2}/{3}/{4} 可用={5}/{6}/{7}", "Color kernel={0} Detail protect={1} Checked={2}/{3}/{4} Available={5}/{6}/{7}",
                "カラーカーネル={0} ディテール保護={1} チェック={2}/{3}/{4} 使用可={5}/{6}/{7}");
            Add("总 {0}ms（其中整文件键扫描 {1} 次 / {2}ms）", "Total {0}ms (whole-file key scans {1} / {2}ms)",
                "合計 {0}ms（うちファイル全体キースキャン {1} 回 / {2}ms）");
            Add("成功 {0} 张，跳过 {1} 张，失败 {2} 张", "OK {0}, skipped {1}, failed {2}",
                "成功 {0} 枚、スキップ {1} 枚、失敗 {2} 枚");
            Add("成功部分的体积：{0} -> {1} 字节（{2:0.0}%）", "Size of successful items: {0} -> {1} bytes ({2:0.0}%)",
                "成功分のサイズ：{0} -> {1} バイト（{2:0.0}%）");
            Add("所有类型都是「原样不动」，输出会与原卡一样。仍要继续吗？", "All types are set to \"leave untouched\", so the output will be identical to the original card. Continue anyway?",
                "すべてのタイプが「そのまま」のため、出力は元のカードと同じになります。続行しますか？");
            Add("打不开目录：\n", "Cannot open folder:\n",
                "フォルダを開けません：\n");
            Add("扫描 {0} 张卡（并行）…", "Scanning {0} cards (parallel)…",
                "{0} 枚のカードをスキャン中（並列）…");
            Add("扫描出错", "Scan error",
                "スキャンエラー");
            Add("扫描完成：{0} 张卡含贴图，共 {1} 张贴图 / {2}", "Scan done: {0} cards with textures, {1} textures total / {2}",
                "スキャン完了：テクスチャありのカード {0} 枚、合計 {1} 枚のテクスチャ / {2}");
            Add("扫描完成：{0} 张卡，{1} 张贴图，{2}", "Scan done: {0} cards, {1} textures, {2}",
                "スキャン完了：{0} 枚のカード、{1} 枚のテクスチャ、{2}");
            Add("批量 {0}/{1}: {2}", "Batch {0}/{1}: {2}",
                "一括 {0}/{1}: {2}");
            Add("批量套用预设 → ", "Batch apply preset → ",
                "プリセットを一括適用 → ");
            Add("批量完成：成功 {0} 张，原样复制 {1} 张，失败 {2} 张；{3} -> {4}（{5:0.0}%）→ {6}", "Batch done: OK {0}, copied as-is {1}, failed {2}; {3} -> {4} ({5:0.0}%) → {6}",
                "一括完了：成功 {0} 枚、そのままコピー {1} 枚、失敗 {2} 枚；{3} -> {4}（{5:0.0}%）→ {6}");
            Add("找到 {0} 个 .png；输出名后缀 \"{1}\"", "Found {0} .png files; output name suffix \"{1}\"",
                "{0} 個の .png を検出；出力名サフィックス \"{1}\"");
            Add("拖进来的东西里没有卡片文件（.png）。", "No card files (.png) among the dropped items.",
                "ドロップされたものにカードファイル（.png）がありません。");
            Add("按类型展开={0} 第2页={1}/{2} 第3页={3}/{4} 下拉2={5} 下拉3={6} | 宽度2={7} 宽度3={8}", "Expand by type={0} Page2={1}/{2} Page3={3}/{4} Combo2={5} Combo3={6} | Width2={7} Width3={8}",
                "タイプ別展開={0} 2ページ={1}/{2} 3ページ={3}/{4} コンボ2={5} コンボ3={6} | 幅2={7} 幅3={8}");
            Add("提示：当前类型表**没有任何类设置「最大边」**（全是原尺寸/原样不动）→ 不会降分辨率，", "Note: no class in the current type table sets \"max edge\" (all original size / untouched) → resolution will not be reduced,",
                "ヒント：現在のタイプ表で「最大辺」を設定しているクラスが**1 つもありません**（すべて原寸/そのまま）→ 解像度は下がりません、");
            Add("提示：这批卡里有第 {0} 套以上的换装（组号超过 {1}）。压缩器用 32 位掩码，", "Note: this batch has more than {0} outfits (group number over {1}). The compressor uses a 32-bit mask,",
                "ヒント：このバッチには {0} セット以上の衣装替えがあります（グループ番号が {1} 超）。圧縮器は 32 ビットマスクを使うため、");
            Add("整文件读取 {0} 次 / {1:F1} MB；整文件键扫描 {2} 次 / {3}ms", "Whole-file reads {0} / {1:F1} MB; whole-file key scans {2} / {3}ms",
                "ファイル全体読み込み {0} 回 / {1:F1} MB；ファイル全体キースキャン {2} 回 / {3}ms");
            Add("整组勾上", "Check all in group",
                "グループをすべてチェック");
            Add("整组取消", "Uncheck all in group",
                "グループをすべて解除");
            Add("文件={0} 存在={1}{2} | {3}", "File={0} Exists={1}{2} | {3}",
                "ファイル={0} 存在={1}{2} | {3}");
            Add("文件不存在：\n", "File not found:\n",
                "ファイルが存在しません：\n");
            Add("新 AC 表缺符号 {0:X2}(表{1}) 频次{2}", "New AC table missing symbol {0:X2} (table {1}) freq {2}",
                "新 AC テーブルに記号 {0:X2}（表{1}）が欠落 頻度{2}");
            Add("新 DC 表缺符号 {0}(表{1}) 频次{2}", "New DC table missing symbol {0} (table {1}) freq {2}",
                "新 DC テーブルに記号 {0}（表{1}）が欠落 頻度{2}");
            Add("无贴图", "No texture",
                "テクスチャなし");
            Add("更小", "Smaller",
                "より小さい");
            Add("未布局(需要{0})", "Not laid out (needs {0})",
                "未レイアウト（{0} が必要）");
            Add("本页一次只处理一张人物卡，已只保留第一张（其余 {0} 张忽略）。", "This page handles only one chara card at a time; kept the first one only (ignoring the other {0}).",
                "このページでは一度にキャラカード1枚のみ処理します。1枚目だけを残しました（残り {0} 枚は無視）。");
            Add("本页一次只处理一张人物卡，已只读取第一张：{0}", "This page handles only one chara card at a time; read the first one only: {0}",
                "このページでは一度にキャラカード1枚のみ処理します。1枚目だけを読み込みました：{0}");
            Add("标记={0} 重载后选中={1} 勾选框={2}", "Mark={0} Selected after reload={1} Checkbox={2}",
                "マーク={0} 再読込後の選択={1} チェックボックス={2}");
            Add("正在读取：{0}", "Reading: {0}",
                "読み込み中：{0}");
            Add("汇总 {0}/{1}: {2}", "Summary {0}/{1}: {2}",
                "集計 {0}/{1}: {2}");
            Add("没更小", "Not smaller",
                "小さくならない");
            Add("没有可套用的规则（先勾一个部位或给某张贴图指定规则）。", "No applicable rules (check a part or assign a rule to a texture first).",
                "適用できるルールがありません（先に部位をチェックするか、テクスチャにルールを指定してください）。");
            Add("没有要处理的卡。", "No cards to process.",
                "処理するカードがありません。");
            Add("消息框测试", "Message box test",
                "メッセージボックスのテスト");
            Add("现在所有部位都是「原样不动」，没有可保存的规则。", "All parts are currently \"Keep as is\", so there are no rules to save.",
                "現在すべての部位が「そのまま」のため、保存できるルールがありません。");
            Add("相似服装={0} 复制不匹配={1}（可选={2}）", "Similar outfits={0} Copy unmatched={1} (optional={2})",
                "類似コーデ={0} 不一致をコピー={1}（任意={2}）");
            Add("第3页开关：细节保护={0} 独占保护={1}（上限 {2}）锐化={3}% 方式={4} 灰度2通道={5}", "Page 3 toggles: Detail guard={0} Exclusive guard={1} (limit {2}) Sharpen={3}% Format={4} Grayscale 2ch={5}",
                "3ページ目のスイッチ：ディテール保護={0} 独占保護={1}（上限 {2}）シャープ={3}% 方式={4} グレースケール2ch={5}");
            Add("类型={0} → {1} ｜ 贴图覆盖：{2}", "Type={0} → {1} | Texture overrides: {2}",
                "タイプ={0} → {1} ｜ テクスチャ上書き：{2}");
            Add("行 {0} TexID={1} → 图={2} 说明={3}", "Row {0} TexID={1} → Image={2} Note={3}",
                "行 {0} TexID={1} → 画像={2} 説明={3}");
            Add("表选中={0}（共 {1} 行）{2} | 预览路径={3} | 小预览说明={4} | {5}", "Table selected={0} ({1} rows total) {2} | Preview path={3} | Thumb note={4} | {5}",
                "表の選択={0}（全 {1} 行）{2} | プレビュー経路={3} | サムネイル説明={4} | {5}");
            Add("解码失败 @MCU({0},{1}) 分量{2}：{3}；结构 ncomp={4} hmax={5} vmax={6} mcus={7}x{8}", "Decode failed @MCU({0},{1}) component {2}: {3}; structure ncomp={4} hmax={5} vmax={6} mcus={7}x{8}",
                "デコード失敗 @MCU({0},{1}) 成分{2}：{3}；構造 ncomp={4} hmax={5} vmax={6} mcus={7}x{8}");
            Add("记住列宽失败：", "Failed to remember column width:",
                "列幅の記憶に失敗：");
            Add("请先「② 读取」一张人物卡。", "Please click \"② Read\" for a chara card first.",
                "先に「② 読み込み」でキャラカードを読み込んでください。");
            Add("请先「读取部位」。", "Please click \"Read parts\" first.",
                "先に「部位を読み込み」を実行してください。");
            Add("请先在一张卡上调好设置（「读取部位」）。", "Please set up the settings on a card first (\"Read parts\").",
                "先にカード上で設定を整えてください（「部位を読み込み」）。");
            Add("请先点「读取部位」。", "Please click \"Read parts\" first.",
                "先に「部位を読み込み」をクリックしてください。");
            Add("请先读取一张人物卡。", "Please read a chara card first.",
                "先にキャラカードを読み込んでください。");
            Add("请先读取一张人物卡，并在「组·部位」表里选中一行。", "Please read a chara card and select a row in the \"Group · Part\" table first.",
                "先にキャラカードを読み込み、「グループ・部位」表で1行選択してください。");
            Add("请先选一张人物卡（Koikatu_F_*.png）。", "Please select a chara card first (Koikatu_F_*.png).",
                "先にキャラカード（Koikatu_F_*.png）を選んでください。");
            Add("请先选择一张衣服卡。", "Please select an outfit card first.",
                "先にコーデカードを選んでください。");
            Add("请先选择卡片文件或文件夹。", "Please select a card file or folder first.",
                "先にカードファイルまたはフォルダを選んでください。");
            Add("读不出来：", "Could not read:",
                "読み出せません：");
            Add("读不出这张卡所在的目录，没法「应用到目录下的所有相似服装」。", "Could not read this card's folder, so \"Apply to all similar outfits in the folder\" is unavailable.",
                "このカードのフォルダを読み出せないため、「フォルダ内のすべての類似コーデに適用」はできません。");
            Add("读取失败", "Read failed",
                "読み込み失敗");
            Add("读取失败：", "Read failed:",
                "読み込み失敗：");
            Add("读取预设：", "Reading preset:",
                "プリセットを読み込み中：");
            Add("贴图 {0} 张：JPEG {1} / PNG {2} / HDR {3} / 原样 {4}（q{5}）", "Textures {0}: JPEG {1} / PNG {2} / HDR {3} / as-is {4} (q{5})",
                "テクスチャ {0} 枚：JPEG {1} / PNG {2} / HDR {3} / そのまま {4}（q{5}）");
            Add("贴图级跳过（{0} 处：这些贴图原样保留，所属卡片本身压缩成功）：", "Texture-level skips ({0}: these textures are kept as-is; their cards compressed successfully):",
                "テクスチャ単位のスキップ（{0} 件：これらのテクスチャはそのまま保持。所属カード自体は圧縮成功）：");
            Add("贴图载荷: {0} -> {1} 字节（{2:0.00} MB -> {3:0.00} MB）", "Texture payload: {0} -> {1} bytes ({2:0.00} MB -> {3:0.00} MB)",
                "テクスチャ容量: {0} -> {1} バイト（{2:0.00} MB -> {3:0.00} MB）");
            Add("路径不存在：\n", "Path not found:\n",
                "パスが存在しません：\n");
            Add("跳过清单（{0} 张，卡里本来就没有可压的贴图，不算失败）：", "Skip list ({0}: these cards had no compressible textures to begin with, not counted as failures):",
                "スキップ一覧（{0} 枚：元から圧縮できるテクスチャが無いカードで、失敗には数えません）：");
            Add("输入 {0}（{1}子文件夹）→ 输出 {2}", "Input {0} ({1} subfolders) → Output {2}",
                "入力 {0}（サブフォルダ {1}）→ 出力 {2}");
            Add("输出目录: ", "Output folder: ",
                "出力フォルダ: ");
            Add("输出目录建不出来：\n", "Could not create the output folder:\n",
                "出力フォルダを作成できません：\n");
            Add("运行出错，见日志", "Runtime error, see the log",
                "実行エラー。ログを参照してください");
            Add("这个文件夹里没有 .png。", "There is no .png in this folder.",
                "このフォルダに .png がありません。");
            Add("这个文件夹（含子文件夹）里没有 .png。", "There is no .png in this folder (including subfolders).",
                "このフォルダ（サブフォルダを含む）に .png がありません。");
            Add("这个文件读不出预设内容。\n", "Could not read preset content from this file.\n",
                "このファイルからプリセット内容を読み出せません。\n");
            Add("这个预设读不出来：", "Could not read this preset:",
                "このプリセットを読み出せません：");
            Add("这个预设里没有批量用的类型表（可能是按部位/人物卡预设）：", "This preset has no type table for batch use (it may be a per-part/chara card preset):",
                "このプリセットにはバッチ用のタイプ表がありません（部位別／キャラカード用プリセットの可能性）：");
            Add("这个预设里没有批量用的类型表（可能是按部位/人物卡预设）：\n", "This preset has no type table for batch use (it may be a per-part/chara card preset):\n",
                "このプリセットにはバッチ用のタイプ表がありません（部位別／キャラカード用プリセットの可能性）：\n");
            Add("需要{0}/表格宽{1}{2}", "Needs {0}/table width {1}{2}",
                "{0} が必要／表の幅 {1}{2}");
            Add("预览失败：", "Preview failed:",
                "プレビュー失敗：");
            Add("预设存不下来：\n", "Could not save the preset:\n",
                "プリセットを保存できません：\n");
            Add("预设已保存：", "Preset saved:",
                "プリセットを保存しました：");
            Add("预设已套到界面：单贴图命中 {0} 张，未匹配 {1} 张", "Preset applied to the UI: {0} single-texture rules hit, {1} unmatched",
                "プリセットを画面に適用しました：単体テクスチャ一致 {0} 件、不一致 {1} 件");
            Add("预设文件：", "Preset file:",
                "プリセットファイル：");
            Add("预设是{0}，本卡是{1}", "The preset is {0}, this card is {1}",
                "プリセットは{0}、このカードは{1}");
            Add("预设落地：命中 {0} 张单贴图规则；未匹配 {1} 张（同款不同版本的卡会少一些）", "Preset applied: {0} single-texture rules hit; {1} unmatched (slightly fewer for same-design cards of a different version)",
                "プリセット適用：単体テクスチャルール一致 {0} 件；不一致 {1} 件（同型で別バージョンのカードは少なめになります）");
            Add("预设落地：命中 {0} 张单贴图规则；未匹配 {1} 张（换版本/换材质的卡会少一些，属正常）", "Preset applied: {0} single-texture rules hit; {1} unmatched (slightly fewer for cards with a different version/material, which is normal)",
                "プリセット適用：単体テクスチャルール一致 {0} 件；不一致 {1} 件（バージョン違い／素材違いのカードは少なめになりますが正常です）");
            Add("预设读不了：\n", "Could not read the preset:\n",
                "プリセットを読み込めません：\n");
            Add("预设：命中 {0} 张单贴图规则；未匹配 {1} 张{2}", "Preset: {0} single-texture rules hit; {1} unmatched {2}",
                "プリセット：単体テクスチャルール一致 {0} 件；不一致 {1} 件{2}");
            Add("默认预设：", "Default preset:",
                "既定のプリセット：");
            Add("（preset\\batch 里还没有预设）", "(No presets in preset\\batch yet)",
                "（preset\\batch にはまだプリセットがありません）");
            Add("（下次启动自动套用它）", "(It will be applied on next start)",
                "（次回起動時に自動で適用されます）");
            Add("（另有 {0} 个类是「原样不动」，跳过）", "(Another {0} classes are \"Keep as is\" and skipped)",
                "（ほかに {0} クラスが「そのまま」のためスキップ）");
            Add("（已拖入 {0} 张卡：{1} …）", "({0} cards dropped: {1} …)",
                "（{0} 枚のカードをドロップ済み：{1} …）");
            Add("（已选 {0} 张）", "({0} selected)",
                "（{0} 枚選択中）");
            // ---- v1.0.2 补：日志里剩下没进词条的碎片（用整句扫描能认出来） ----
            Add("================ 汇总 ================",
                "================ Summary ================",
                "================ 集計 ================");
            Add("没有失败，也没有跳过。", "No failures and nothing skipped.",
                "失敗もスキップもありません。");
            Add("张贴图可解码", "textures decodable", "枚のテクスチャをデコード確認");
            Add("低分辨率域轻度锐化", "mild sharpening in the downscaled domain",
                "縮小後の領域に軽いシャープ");
            Add("有alpha", "with alpha", "alpha あり");
            Add("无alpha", "no alpha", "alpha なし");
            // ---- v1.0.3：类名说明（扫描结果那一列）+ 预设状态行碎片 ----
            Add("JPEG 安全（无 alpha 时），压缩收益最大",
                "JPEG is safe (when there is no alpha); best size win",
                "JPEG は安全（alpha 無し）。削減効果が最大");
            Add("JPEG 会产生明暗色带，建议原样或只降分辨率",
                "JPEG may band; keep as-is or only downscale",
                "JPEG は階調段差が出ます。そのまま or 縮小のみ推奨");
            Add("JPEG 噪声变成阴影条带，建议原样",
                "JPEG turns noise into shadow banding; keep as-is",
                "JPEG はノイズが縞になります。そのまま推奨");
            Add("凹凸细节，建议原样", "Bump detail; keep as-is", "凹凸ディテール。そのまま推奨");
            Add("发光图，JPEG 一般可行", "Emission map; JPEG usually OK", "発光マップ。JPEG は概ね可");
            Add("反射图，通常很小", "Reflection map; usually small", "反射マップ。通常は小さい");
            Add("球面高光图，JPEG 一般可行", "Sphere highlight map; JPEG usually OK",
                "球面ハイライト。JPEG は概ね可");
            Add("高光颜色图，JPEG 一般可行", "Highlight color map; JPEG usually OK",
                "ハイライト色マップ。JPEG は概ね可");
            Add("无法判定的贴图，默认不动最安全", "Unknown texture; safest to leave untouched",
                "判別不能。触らないのが最も安全");
            Add("内置默认", "built-in default", "組み込み既定");
            Add("未知", "unknown", "不明");
            Add("未知时间", "unknown time", "不明な日時");
            Add("原尺寸", "original size", "元サイズ");
            Add("　彩色贴图也走面积平均+锐化", " | area-average + sharpen for color",
                " | カラーも面積平均+シャープ");
            Add("本体", "Body", "本体");
            Add("身体", "Body", "体");
            Add("脸", "Face", "顔");
            Add("头发", "Hair", "髪");
            Add("眼睛", "Eyes", "目");
            Add("身体/脸/头发/眼睛", "Body/Face/Hair/Eyes", "体/顔/髪/目");
            Add("独享", "Exclusive", "専有");
            Add("灰度", "Grayscale", "グレースケール");
            Add("灰度（按共用规则）", "Grayscale (shared rule)", "グレースケール（共有ルール）");
            Add("（无部位引用）", "(no part reference)", "（部位参照なし）");
            // ---- v1.0.3：内置预设名（下拉按显示名翻，文件/逻辑仍用原名） ----
            Add("仅优化", "Optimize only", "最適化のみ");
            Add("质量", "Quality", "高画質");
            Add("性能", "Performance", "軽量");
            Add("性能（激进）", "Performance (aggressive)", "軽量（強）");
            Add("（colorArea=1", "（colorArea=1", "（colorArea=1");
            // ---- v1.1.5：压缩选项的气泡说明（简短版）----
            Add("降分辨率时用面积平均，细节更清楚。不勾 = 用普通缩放（更快）。",
                "Area averaging when downscaling — keeps detail clearer. Unchecked: plain resize (faster).",
                "縮小時に面積平均を使います（細部がきれい）。オフ: 通常の縮小（速い）。");
            Add("彩色贴图也一起用面积平均 + 边缘锐化，观感更好。",
                "Color textures also get area averaging + edge sharpening (looks better).",
                "カラーテクスチャにも面積平均＋エッジシャープ（見た目が良い）。");
            Add("只被一个部位用到的贴图不跟着降 —— 它往往是那个部位唯一的高清来源。",
                "Textures used by only one part are not downscaled — often that part's only hi-res source.",
                "1 つの部位だけが使うテクスチャは縮小しません（その部位唯一の高解像度元が多い）。");
            Add("三通道相同的灰阶图只存一个通道：体积更小，像素完全不变。",
                "Grayscale images store one channel only — smaller, pixels unchanged.",
                "グレースケール画像は 1 チャンネルだけ保存（小さくなり、画素は不変）。");
            // ---- v1.1.6：气泡说明（白话版）----
            Add("界面语言（默认 English，选择会记住）。",
                "UI language (default English; the choice is remembered).",
                "表示言語（既定は English、選択は記憶されます）。");
            Add("浅色 / 深色。深色连标题栏一起变暗；系统弹窗和「打开文件」仍是系统样式。",
                "Light / dark. Dark also dims the title bar. System dialogs keep the system look.",
                "ライト / ダーク。ダークはタイトルバーも暗くします。システムダイアログは変わりません。");
            Add("勾上 = 处理这一套换装；不勾 = 跳过。",
                "Checked: this outfit is processed. Unchecked: skipped.",
                "オン: この衣装を処理。オフ: スキップ。");
            Add("拖动 = 调整日志宽度（双击回到默认）。",
                "Drag to resize the log panel (double-click to reset).",
                "ドラッグでログ幅を変更（ダブルクリックで既定に戻す）。");
            Add("勾上 = 处理不了的卡原样复制到输出目录；不勾 = 直接跳过。复制过去的不会被压缩。",
                "Checked: cards that cannot be processed are copied as-is. Unchecked: skipped.",
                "オン: 処理できないカードはそのままコピー。オフ: スキップ。");
            Add("把小于「最大边」的贴图整数倍放大（×2 / ×4）。只在彩色类生效。代价大，通常只放大主贴图就够。",
                "Upscales textures smaller than Max edge (x2 / x4). Color classes only. Costly — usually MainTex is enough.",
                "「最大辺」未満のテクスチャを整数倍に拡大（×2 / ×4）。カラー種別のみ。負担が大きいので通常はメインテクスチャだけで十分。");
            Add("放大用哪种算法。默认 lanczos3 最清晰，mitchell 最柔和，nearest 只适合像素画。",
                "Upscale algorithm. Default lanczos3 is sharpest; mitchell is softest; nearest is for pixel art only.",
                "拡大アルゴリズム。既定 lanczos3 が最もシャープ、mitchell は最も柔らか、nearest はドット絵専用。");
            Add("降分辨率后补一点锐化，找回面积平均糊掉的细节。0~200%，数字框可手打（上限 500%）。只对真的缩过的贴图生效。",
                "Sharpens after downscaling to recover detail lost to area averaging. 0-200% (type up to 500%).",
                "縮小後に軽くシャープをかけ、面積平均で失った細部を取り戻します。0〜200%（数値入力は 500% まで）。");
            Add("可直接输入数值（0~500）。超过 200 时滑块顶到最右。",
                "Type a value directly (0-500). Above 200 the slider stays at the right end.",
                "数値を直接入力できます（0〜500）。200 を超えるとスライダーは右端のままです。");
            Add("对比度自适应（默认）= 强边附近收着锐，更少白边；边缘感知 = 边缘锐得更狠，也更容易出白边。",
                "Contrast adaptive (default): gentler near strong edges, fewer halos. Edge aware: sharper edges, more halos.",
                "コントラスト適応（既定）: 強いエッジ付近で抑え、ハローが少ない。エッジ感知: より強く、ハローが出やすい。");
            Add("独占贴图的最大边上限：4096 = 画质优先（默认）；2048 = 折中；原尺寸不降 = 完全不缩。",
                "Max edge for exclusive textures: 4096 = quality (default); 2048 = middle; original = never downscale.",
                "専有テクスチャの最大辺: 4096 = 画質優先（既定）、2048 = 折衷、元サイズ = 縮小しない。");
            Add("选好倍率后点右边的「应用放大设置」，会写进类型表里所有已勾选的类；之后仍可逐类改。",
                "Pick a factor, then click Apply upscale setting — it is written to all checked types. Per-type edits still possible.",
                "倍率を選んで右の「アップスケール設定を適用」を押すと、チェックした全種別に書き込まれます。後から個別変更も可。");
            Add("放大（×2 / ×4）代价大、多数人用不上，所以默认收起；点这里展开（三个页面一起）。",
                "Upscale (x2 / x4) is costly and rarely needed, so it is hidden by default. Click to show it (all three pages).",
                "アップスケール（×2 / ×4）は負担が大きく通常不要なため既定で非表示。ここで表示（3 ページ同時）。");
            Add("把左边的放大倍率写进类型表里所有已勾选的类。放大只在彩色类生效，且受「最大边」与 4096px 上限约束。",
                "Writes the upscale factor to all checked types. Upscaling applies to color classes only, capped by Max edge and 4096px.",
                "左の倍率をチェック済みの全種別に書き込みます。拡大はカラー種別のみ、最大辺と 4096px の制限を受けます。");
            Add("勾上 = 每次启动自动套用这个预设；取消 = 回到内置的「仅优化」。",
                "Checked: this preset is applied automatically on every start. Unchecked: back to the built-in Optimize only.",
                "オン: 起動時にこのプリセットを自動適用。オフ: 組み込みの「最適化のみ」に戻ります。");
            Add("勾上 = 开始压缩时，把当前设置套到同目录下同款的其它卡上；不勾 = 只压这一件。",
                "Checked: also applies these settings to matching cards in the same folder. Unchecked: this card only.",
                "オン: 同じフォルダ内の同型カードにも設定を適用。オフ: この 1 枚のみ。");
            Add("与预设不是同款服装时：不勾 = 跳过；勾上 = 原样复制到输出目录。（配合上一项使用）",
                "When a card does not match the preset: unchecked = skip; checked = copy as-is to the output folder.",
                "プリセットと同型でない場合: オフ = スキップ、オン = そのまま出力へコピー。");
            Add("放大用哪种算法（三页共用）。默认 lanczos3 最清晰，mitchell 最柔和，nearest 只适合像素画。",
                "Upscale algorithm (shared by all three pages). Default lanczos3 is sharpest; mitchell softest; nearest for pixel art.",
                "拡大アルゴリズム（3 ページ共通）。既定 lanczos3 が最もシャープ、mitchell は柔らか、nearest はドット絵向け。");
            Add("放大（×2 / ×4）默认收起：代价大、多数人用不上。点这里展开（三个页面一起）。",
                "Upscale (x2 / x4) is hidden by default — costly and rarely needed. Click to show (all three pages).",
                "アップスケール（×2 / ×4）は既定で非表示（負担大・通常不要）。ここで表示（3 ページ同時）。");
            Add("想按贴图类型批量改（比如把所有主贴图设成同一个值）时点开这里。",
                "Open this to change one texture type in bulk (e.g. set all MainTex to the same value).",
                "テクスチャ種別ごとにまとめて変更したいとき（例: 全メインテクスチャを同じ値に）に開きます。");
            Add("把这张贴图放进独立窗口放大看：滚轮缩放、按住左键拖动。主界面换贴图时窗口跟着换。",
                "Opens this texture in a separate window: wheel to zoom, drag to pan. It follows your selection.",
                "このテクスチャを別ウィンドウで拡大表示: ホイールで拡大、ドラッグで移動。選択に追従します。");
            Add("把四个表格的当前列宽记下来，下次启动自动套用。",
                "Remembers the current column widths of the four tables and applies them next time.",
                "4 つの表の現在の列幅を記憶し、次回起動時に適用します。");
            Add("忘掉记住的列宽，四个表格回到默认比例。",
                "Forgets the saved column widths; the four tables return to the default proportions.",
                "記憶した列幅を破棄し、4 つの表を既定の比率に戻します。");
            Add("把「组·部位」表里所有组的每一行都勾上（不受「组过滤」显示范围影响）。",
                "Checks every row of every group in the Group · Part table (ignores the group filter).",
                "「組・部位」表の全組・全行をチェックします（グループ絞り込みの影響を受けません）。");
            Add("把「组·部位」表里所有组的每一行都取消勾选。",
                "Unchecks every row of every group in the Group · Part table.",
                "「組・部位」表の全組・全行のチェックを外します。");
            Add("把「一键统一」那套值套到整张卡里这一类型的全部贴图（含单张指定，优先级最高）。",
                "Applies the Unify values to every texture of this type in the whole card.",
                "「一括統一」の値をカード全体のこの種別すべてに適用します。");
            Add("只改当前选中部位里这一类型的贴图。先在「部位一览」里选中一个部位。",
                "Changes only this type in the currently selected part. Select a part in the list first.",
                "選択中の部位のこの種別だけを変更します。先に部位一覧で部位を選んでください。");
            Add("把「一键统一」那套值套到整张人物卡里这一类型的全部贴图（含单张指定，优先级最高）。",
                "Applies the Unify values to every texture of this type in the whole chara card.",
                "「一括統一」の値をキャラカード全体のこの種別すべてに適用します。");
            Add("只改当前选中的「组·部位」里这一类型的贴图。先在上面那张表里选中一行。",
                "Changes only this type in the selected Group · Part. Select a row in the table above first.",
                "選択中の「組・部位」のこの種別だけを変更します。上の表で行を選んでください。");
            Add("把左边这套设置套到「范围」命中的那些「组·部位」（默认全部组）。",
                "Applies the settings on the left to the Group · Part rows matched by Scope (all groups by default).",
                "左の設定を「範囲」に一致する「組・部位」に適用します（既定は全グループ）。");
            Add("把左边这几个值只设到当前正在浏览的那一组（同组其它部位不动），并清掉该处的单张指定。",
                "Applies the values to the group · part you are currently viewing only, and clears its per-texture overrides.",
                "現在表示中の組・部位にだけ適用し、その個別指定を消します。");
            Add("降分辨率时用面积平均，细节更清楚。不勾 = 用普通缩放（更快）。",
                "Area averaging when downscaling — keeps detail clearer. Unchecked: plain resize (faster).",
                "縮小時に面積平均を使います（細部がきれい）。オフ: 通常の縮小（速い）。");
            Add("只被一个部位用到的贴图不跟着降 —— 它往往是那个部位唯一的高清来源。",
                "Textures used by only one part are not downscaled — often that part's only hi-res source.",
                "1 つの部位だけが使うテクスチャは縮小しません（その部位唯一の高解像度元が多い）。");
            LKo.Fill();          // v1.0.1：韩语表（单独文件，便于再加语言）
        }
    }
}

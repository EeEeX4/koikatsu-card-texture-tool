using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;                 // ListSortDirection（表头排序用）
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;                  // 解析 "14.58 MB" 里的数字（排序用）
using System.IO;
using System.Text;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;

namespace KoiCardTexTool
{
    public sealed partial class MainForm : Form
    {
        static readonly string[] FmtText =
        {
            "原样不动（不重编码、不缩放）", "自动（安全且更小才用 JPEG）", "全部 JPEG（有损，最小）", "全部 PNG（无损，最大）"
        };
        static readonly string[] FmtKey = { "keep", "auto", "jpeg", "png" };
        static readonly string[] Sizes = { "原尺寸", "4096", "2048", "1024", "512", "256" };
        // v2.33 放大：界面文案 ↔ 倍率。0 = 不放大（默认）。
        static readonly string[] Ups = { "不放大", "×2", "×4" };
        static readonly int[] UpKey = { 0, 2, 4 };

        readonly TextBox txtIn = new TextBox();
        readonly TextBox txtOut = new TextBox();
        readonly TextBox txtSuffix = new TextBox();
        readonly RadioButton rbFile = new RadioButton { Text = "单个卡片文件", Checked = true };
        readonly RadioButton rbDir = new RadioButton { Text = "整个文件夹" };
        readonly CheckBox chkOverwrite = new CheckBox { Text = "覆盖同名" };
        readonly CheckBox chkRecurse = new CheckBox { Text = "包含子文件夹", Checked = true };
        readonly CheckBox chkGrayA = new CheckBox();
        readonly CheckBox chkKernel = new CheckBox();
        readonly CheckBox chkColor = new CheckBox();       // v2.32：彩色贴图也用面积平均（Resample.ColorMode）
        readonly CheckBox chkOwn = new CheckBox();
        readonly ComboBox cbOwnMax = new ThemedCombo();
        readonly ThemedSlider trkSharpen = new ThemedSlider { Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 25, Width = 160 };
        readonly NumericUpDown numSharpen = new NumericUpDown { Minimum = 0, Maximum = 500, Value = 100, Width = 64 };
        readonly Label lblSharpen = new Label { Text = "%", AutoSize = true };
        readonly ComboBox cbSharpMode = new ThemedCombo();
        readonly ComboBox cbUpKernel = new ThemedCombo();      // v2.33 放大算法
        readonly ComboBox cbUpAll = new ThemedCombo();         // v2.36 全局「放大」倍率（配合「应用放大设置」）
        Button bApplyUp;                                    // v2.36 「应用放大设置」
        readonly Label lblGrpToggle = new Label();
        FlowLayoutPanel pGrp;                       // 人物卡组过滤那一行（默认收起）
        // v2.19：原「保护 alpha」复选框已删除（alpha 保护改成内建判据，见 TexTool.alphaOk）。
        const bool protectAlphaCompat = true;   // 旧预设里的 protectAlpha 字段仍然读，但不再影响判定
        readonly ThemedSlider trkQuality = new ThemedSlider { Minimum = 50, Maximum = 100, Value = 90, TickFrequency = 5, Width = 220 };
        readonly Label lblQuality = new Label { Text = "90", AutoSize = true };
        readonly RichTextBox log = new RichTextBox { ReadOnly = true, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, Font = new Font("Consolas", 8.5f), BackColor = Color.White };
        readonly ProgressBar pb = new ProgressBar();
        readonly LocLabel lblStatus = new LocLabel { Text = "就绪。选好输入卡（拖进来或点浏览…），然后点「▶ 开始压缩」。", AutoSize = true };
        readonly Dictionary<string, CheckBox> on = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, ComboBox> fmt = new Dictionary<string, ComboBox>();
        readonly Dictionary<string, ComboBox> size = new Dictionary<string, ComboBox>();
        readonly Dictionary<string, ComboBox> up = new Dictionary<string, ComboBox>();     // v2.33
        readonly Dictionary<string, Label> info = new Dictionary<string, Label>();

       readonly Button btnRun = new Button { Text = "▶  开始压缩", AutoSize = true };
        readonly Button btnCancel = new Button { Text = "取消", Enabled = false, AutoSize = true };

        readonly ConcurrentQueue<object[]> q = new ConcurrentQueue<object[]>();
        readonly Timer pump;
        volatile bool cancelFlag;

        readonly List<string> dropped = new List<string>();      // 拖入的多张卡
        bool settingText;

        // ---- 布局：日志整块可折叠 + 自绘分隔条（SplitContainer 在零尺寸布局时会内部抛异常，不用它）----
        const int LogWidth = 340;
        const int SplitW = 6;
        const int LogMin = 180;          // 日志列的最小宽度（再窄就没法看了）
        TableLayoutPanel rootT1, rootT2, rootT3;                  // 三页的主表格
        Panel logWrap1, logWrap2, logWrap3, spLog1, spLog2, spLog3; // 日志块 + 它的分隔条
        // 日志开关做成按下去就显示、再按就隐藏"的按钮式开关（勾上=日志显示中，外观是按下状态）
        readonly CheckBox btnLog1 = new CheckBox { Text = "隐藏日志", Appearance = Appearance.Button, AutoSize = true, Checked = true };
        readonly CheckBox btnLog2 = new CheckBox { Text = "隐藏日志", Appearance = Appearance.Button, AutoSize = true, Checked = true };
        readonly CheckBox btnLog3 = new CheckBox { Text = "隐藏日志", Appearance = Appearance.Button, AutoSize = true, Checked = true };
        bool syncingLogBtn;

        // ---- 第1 页的预设（卡面下拉框）----
        readonly ComboBox cbPre1 = new ThemedCombo();
        // v1.0.3：预设下拉只翻**显示名**（PresetEntry 还是原对象，路径/名字等逻辑一律不受影响）
        void HookPresetDisplay(ComboBox cb)
        {
            cb.FormattingEnabled = true;
            cb.Format += delegate (object se, ListControlConvertEventArgs fe)
            {
                var pe = fe.ListItem as PresetEntry;
                if (pe != null) fe.Value = L.T(pe.Name);
            };
        }
        // AutoSize=false → 太长时**换行**而不是被右边切掉（预设状态行在英文下会比较长）
        readonly LocLabel lblPre1 = new LocLabel { AutoSize = false };
        readonly List<PresetEntry> pre1 = new List<PresetEntry>();
        bool pre1Loading;
        readonly CheckBox chkPreDefault = new CheckBox { AutoSize = true };
        bool syncingPreDefault;

        /// <summary>重新列 preset\batch 里的预设，填进下拉框（第 0 项永远是「不使用预设」）。</summary>
        void PreReload()
        {
            // 记住当前选的是哪个预设，刷新后尽量选回去
            string keepName = (cbPre1.SelectedIndex > 0 && cbPre1.SelectedIndex < pre1.Count) ? pre1[cbPre1.SelectedIndex].Name : null;
            pre1Loading = true;
            try
            {
                foreach (var e in pre1) if (e.Cover != null) { try { e.Cover.Dispose(); } catch { } }
                pre1.Clear();
                cbPre1.Items.Clear();
                PresetIo.EnsureDefaults();                   // 第一次运行时种三个内置基础预设
                var loaded = PresetIo.List(PresetIo.KindBatch, 0);
                // 第0 项恒为「不使用预设」；接着三个基础预设固定顺序（仅优化 → 质量 → 性能）；其余用户预设跟在后面
                var ordered = new List<PresetEntry>();
                ordered.Add(new PresetEntry { Path = null, Name = "（不使用预设：按上面的类型表）" });
                foreach (var bn in new[] { "仅优化", "质量", "性能" })
                    foreach (var e in loaded)
                        if (e.Name == bn && !ordered.Contains(e)) ordered.Add(e);
                foreach (var e in loaded) if (!ordered.Contains(e)) ordered.Add(e);
                pre1.AddRange(ordered);
                foreach (var e in pre1) cbPre1.Items.Add(e);
                HookPresetDisplay(cbPre1);
                cbPre1.SelectedIndex = 0;
                int want = 0;
                // v2.23：优先选"将当前预设设为默认"的那个预设；没有默认标记才退回「仅优化」
                string defFile = PresetIo.GetDefaultPreset(PresetIo.KindBatch);
                for (int i = 0; i < pre1.Count; i++)
                    if (pre1[i].Path != null && (keepName != null ? pre1[i].Name == keepName
                                                                  : (defFile != null && string.Equals(Path.GetFileName(pre1[i].Path), defFile, StringComparison.OrdinalIgnoreCase))))
                    { want = i; break; }
                // v2.39：没有「默认预设」标记时的回退 —— 先找「质量」，再退「仅优化」。
                // （老版本是直接退「仅优化」，而那个预设是"全原尺寸"，新装的用户一压缩会发现什么都没变。）
                if (want == 0 && keepName == null && defFile == null)
                {
                    foreach (var wantName in new[] { "质量", "仅优化" })
                    {
                        for (int i = 0; i < pre1.Count; i++)
                            if (pre1[i].Path != null && pre1[i].Name == wantName) { want = i; break; }
                        if (want != 0) break;
                    }
                }
                cbPre1.SelectedIndex = want;
                SyncPreDefaultCheck();
                lblPre1.Text = pre1.Count > 1
                    ? L.F("preset\\batch：{0} 个预设{1}", pre1.Count - 1,
                        defFile != null ? "（默认：" + Path.GetFileNameWithoutExtension(defFile) + "）" : "")
                    : "preset\\batch 里还没有预设（点「保存为预设…」存一个）";
            }
            finally { pre1Loading = false; }
            // 关键：选中 ≠ 套用。pre1Loading 期间 SelectedIndexChanged 被挡掉了，
            // 所以这里补一次，让启动时的默认预设（仅优化）真正生效。
            if (cbPre1.SelectedIndex > 0) PreApply(cbPre1.SelectedIndex);
        }

        /// <summary>「将当前预设设为默认」勾选框跟着下拉框走：选中的就是默认那个才勾上。</summary>
        void SyncPreDefaultCheck()
        {
            if (chkPreDefault == null) return;
            string def = PresetIo.GetDefaultPreset(PresetIo.KindBatch);
            int i = cbPre1.SelectedIndex;
            bool cur = i > 0 && i < pre1.Count && !string.IsNullOrEmpty(pre1[i].Path)
                       && def != null && string.Equals(Path.GetFileName(pre1[i].Path), def, StringComparison.OrdinalIgnoreCase);
            syncingPreDefault = true;
            try { chkPreDefault.Checked = cur; }
            finally { syncingPreDefault = false; }
        }

        /// <summary>预设下拉框自绘：左边 48px 卡面，右边预设名（批量页预设是纯 JSON，没有卡面就只画名字）。</summary>
        void PreItemDraw(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= pre1.Count) return;
            var it = pre1[e.Index];
            e.DrawBackground();
            int pad = 3, box = Math.Max(16, e.Bounds.Height - pad * 2);
            if (it.Cover != null)
            {
                var rc = new Rectangle(e.Bounds.Left + pad, e.Bounds.Top + pad, box, box);
                try { e.Graphics.DrawImage(it.Cover, rc); } catch { }
                using (var p = new Pen(Color.FromArgb(200, 200, 205))) e.Graphics.DrawRectangle(p, rc.Left, rc.Top, rc.Width - 1, rc.Height - 1);
                using (var br = new SolidBrush(e.ForeColor))
                    e.Graphics.DrawString(it.Name, e.Font, br, new PointF(rc.Right + 6, e.Bounds.Top + 2));
            }
            else
            {
                using (var br = new SolidBrush(e.ForeColor))
                    e.Graphics.DrawString(it.Name, e.Font, br, new PointF(e.Bounds.Left + 4, e.Bounds.Top + 2));
            }
            e.DrawFocusRectangle();
        }

        /// <summary>选中某个预设 →把它的类型表 / 质量套到界面上。</summary>
        /// <summary>套用预设里的"处理开关：彩色贴图是否也走面积平均+锐化、锐化强度。</summary>
        void ApplyProcessFlags(bool colorArea, string sharpen)
        {
            Resample.ColorMode = colorArea;
            // v2.32：预设有 colorArea 字段（老预设没有 → 走 PresetIo 的默认值 false），
            // 套用后要把三个页面的勾选框一起刷新，否则界面显示的和实际生效的不一致。
            chkColor.Checked = colorArea;
            chkColor2.Checked = colorArea;
            chkColor3.Checked = colorArea;
            if (!string.IsNullOrEmpty(sharpen))
            {
                if (sharpen == "edge") { Resample.Sharpen = 0.6; Resample.SharpenEdgeMax = 2.5; }
                else
                {
                    double v;
                    if (double.TryParse(sharpen, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out v))
                    { Resample.Sharpen = v; Resample.SharpenEdgeMax = 0; }
                }
            }
        }

        /// <summary>手输/浏览路径之后自动扫描一次（只在路径有效且与上次不同时）。
        /// 这是"扫描"从按钮变成自动行为的地方。</summary>
        string lastAutoScanned = null;
        bool autoScanning = false;
        void AutoScanIfNew()
        {
            if (autoScanning) return;
            if (dropped.Count > 0) return;                       // 拖入的卡已经扫过了
            string p = txtIn.Text.Trim();
            if (p.Length == 0) return;
            if (!File.Exists(p) && !Directory.Exists(p)) return;  // 路径还不对就不要弹框打扰
            if (p == lastAutoScanned) return;
            lastAutoScanned = p;
            autoScanning = true;
            try { DoScan(); } finally { autoScanning = false; }
        }

        /// <summary>展开/收起「人物卡组过滤」那一行。默认收起（衣服卡用不到）；
        /// 导入的卡里发现人物卡时自动展开（见 AutoExpandGroupFilter）。</summary>
        void SetGroupFilterVisible(bool on)
        {
            if (pGrp == null) return;
            pGrp.Visible = on;
            lblGrpToggle.Text = on ? "▾ 人物卡组过滤（点这里收起）" : "▸ 人物卡组过滤（只对人物卡生效，点这里展开）";
        }

        /// <summary>看输入路径里有没有人物卡（Koikatu_F_*.png）；有就自动展开组过滤那一行。
        /// 扫描时会调用，所以拖入 / 浏览 / 手输路径之后都会自动处理。</summary>
        void AutoExpandGroupFilter(IEnumerable<string> cardPaths)
        {
            if (pGrp == null || pGrp.Visible) return;
            foreach (var p in cardPaths)
            {
                try
                {
                    if (TexTool.IsCharaCardFast(p)) { SetGroupFilterVisible(true); return; }
                }
                catch { }
            }
        }

        /// <summary>把预设里的「锐化强度 / 方式」套到界面与静态设置上。
        /// 方式只在预设**明确标记为用户选过**时才应用；老预设（无标记）不覆盖当前值 ——
        /// 于是 2.15/2.16 存下的 `edge` 会自动跟随新默认 `cas`。</summary>
        void ApplySharpen(string json)
        {
            int pct; string mode; bool modeExplicit;
            if (!PresetIo.TryReadSharpenEx(json, out pct, out mode, out modeExplicit)) return;   // 老预设没这两个字段 → 保留当前值
            Resample.SharpenPct = pct < 0 ? 0 : (pct > 500 ? 500 : pct);
            trkSharpen.Value = Math.Max(trkSharpen.Minimum, Math.Min(trkSharpen.Maximum, Resample.SharpenPct));
            numSharpen.Value = Math.Max(numSharpen.Minimum, Math.Min(numSharpen.Maximum, Resample.SharpenPct));
            if (!modeExplicit) return;                       // 预设没明确选过方式 → 不动（跟随当前默认）
            Resample.SharpMode = mode == "cas" ? "cas" : "edge";
            Resample.SharpModeExplicit = true;
            cbSharpMode.SelectedIndex = Resample.SharpMode == "cas" ? 1 : 0;
        }

        /// <summary>锐化现在**独立成一步**：只要这张贴图真的被降了分辨率就会跑（对所有类生效），
        /// 不再受「细节图保细节」约束。所以这里只剩一条要提示的：当前类型表根本没降分辨率。</summary>
        void WarnIfSharpenInert()
        {
            if (Resample.SharpenPct <= 0) return;
            var p = BuildPlan();
            bool anyCap = false;
            foreach (var cls in Classes.Order)
            {
                var r = p[cls];
                if (r.Format != "keep" && r.Size > 0) { anyCap = true; break; }
            }
            if (anyCap) return;
            Log("提示：当前类型表**没有任何类设置「最大边」**（全是原尺寸/原样不动）→ 不会降分辨率，");
            Log("      而锐化只在「降了分辨率」的贴图上跑，所以现在不会有任何效果。");
            Log("      要用锐化请把某些类的最大边设成 2048/1024，或直接用「质量」「性能」预设。");
        }

        /// <summary>v2.32：「彩色贴图也走面积平均」依赖上面那个「贴图细节保护」——
        /// 细节保护不勾时所有贴图都退回 GDI+ 双三次，彩色那项就没有意义了，所以跟着灰掉。
        /// 三个页面各有一份勾选框，这里一次性对齐。</summary>
        void SyncColorEnable()
        {
            chkColor.Enabled = chkKernel.Checked;
            chkColor2.Enabled = chkKernel2.Checked;
            chkColor3.Enabled = chkKernel3.Checked;
        }

        /// <summary>v2.37：让表格列宽**真的能被拖动**。
        ///
        /// 原来四个表格都是 AutoSizeColumnsMode.Fill：Fill 每次布局都会把列宽重新摊平到
        /// 可视宽度，于是"往右拖宽"会被立刻还原、最后一列更是无处可扩（总宽必须正好等于可视宽）。
        /// 这就是"拖不动表头"的根因。
        ///
        /// 做法：先让 Fill 按 FillWeight 铺一次，把**那一瞬间的实际宽度**记下来（初始观感不变），
        /// 然后把模式切成 None —— 之后列宽就是我们自己的值，用户可以自由拖动。
        /// 等到可视宽度够大（>160px）再固化，避免在布局早期把列宽定成很小的一坨。
        /// </summary>
        static void MakeColumnsResizable(DataGridView g, string key)
        {
            // v1.0.3：表格里显示的"取值"（跟随部位/自动/不缩放/开/关/不放大…）以及应用自己
            // 生成的说明文字，都是运行时填进去的 —— 用 CellFormatting 统一翻**显示值**，
            // 底层数据保持中文原文（逻辑判断、预设读写完全不受影响）。
            g.CellFormatting += delegate (object se, DataGridViewCellFormattingEventArgs fe)
            {
                CellFmtCount++;                     // 自检用：单元格重绘（=重新翻译）次数
                string v = fe.Value as string;
                if (v != null) { string t = L.Tr(v); if (t != v) fe.Value = t; }
            };
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            LayoutEventHandler once = null;
            once = delegate
            {
                if (g.IsDisposed || g.ClientSize.Width <= 160) return;      // 等一次像样的布局
                g.Layout -= once;
                // **先切模式再设宽度**：Fill 模式下设好的宽度会在下一次布局被重新摊平，
                // 所以必须先把模式切成 None，之后设置的宽度才留得住。
                // （第一版写成"有保存的列宽就提前 return"——那样模式还留在 Fill，
                //   记住的列宽既没生效、拖动又会被还原，等于两个 bug 叠在一起。）
                g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
                // 有「记住」过的就用记住的（按列名匹配，改列顺序/增删列也不会错位）；
                // 否则按 FillWeight × 当前可视宽度算一遍默认值（与 Fill 的意图一致：
                // 按权重分满可视宽度；各列 MinimumWidth 之和更大时自然溢出成横向滚动条）。
                if (!ApplySavedColWidths(g, key)) ApplyDefaultColWidths(g);
            };
            g.Layout += once;
        }

        // ===================== v1.0：界面语言 =====================
        ComboBox cbLang;
        FlowLayoutPanel pLang;                 // 语言面板（自检要读它的坐标）
        ComboBox cbTheme;                      // 外观（浅色/深色）
        bool settingTheme;

        /// <summary>切换外观：套色 + 记住 + 系统部分（标题栏/滚动条）也重来一次。</summary>
        public void SetTheme(ThemeKind k)
        {
            Theme.Set(k);
            Theme.Save(k);
            settingTheme = true;
            try
            {
                Theme.Apply(this);
                if (prevBig != null && !prevBig.IsDisposed) Theme.Apply(prevBig);
                Theme.ApplyNative(this, this);
                if (cbTheme != null && cbTheme.SelectedIndex != (k == ThemeKind.Dark ? 1 : 0))
                    cbTheme.SelectedIndex = k == ThemeKind.Dark ? 1 : 0;
            }
            finally { settingTheme = false; }
            Log("Theme: " + Theme.Name(k));
        }

        /// <summary>切语言时"浅色/深色"两项也要跟着变。</summary>
        void RefreshThemeCombo()
        {
            if (cbTheme == null) return;
            int keep = cbTheme.SelectedIndex;
            cbTheme.Items.Clear();
            cbTheme.Items.Add(L.T("浅色"));
            cbTheme.Items.Add(L.T("深色"));
            cbTheme.SelectedIndex = keep;
        }
        bool settingLang;                     // 正在程序内切换语言（防重入 / 防事件自触发）

        static string LangPath()
        {
            string b = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(b)) b = ".";
            return Path.Combine(b, "ui-lang.txt");
        }
        /// <summary>自检/命令行要把界面语言钉死时用（null = 按保存的语言）。
        /// uitest 固定中文：回归脚本是靠中文关键字 grep 自检输出的，语言一变脚本就失效。</summary>
        public static Lang? LangOverride;

        static Lang LoadLang()
        {
            try
            {
                string p = LangPath();
                if (!File.Exists(p)) return Lang.En;                  // 默认英语
                string t = File.ReadAllText(p).Trim().ToLowerInvariant();
                if (t.StartsWith("ja")) return Lang.Ja;
                if (t.StartsWith("zh")) return Lang.Zh;
                if (t.StartsWith("ko")) return Lang.Ko;
                return Lang.En;
            }
            catch { return Lang.En; }
        }
        static void SaveLang(Lang l)
        {
            try
            {
                File.WriteAllText(LangPath(), l == Lang.Ja ? "ja" : (l == Lang.Zh ? "zh" : (l == Lang.Ko ? "ko" : "en")),
                                  new UTF8Encoding(false));
            }
            catch { }
        }

        void BuildLangPicker()
        {
            pLang = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                              WrapContents = false, Margin = new Padding(0) };
            var lb = new Label { Text = "Language", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            cbLang = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 104 };
            foreach (var l in L.All) cbLang.Items.Add(L.LangName(l));
            cbLang.SelectedIndex = Array.IndexOf(L.All, L.Cur);
            cbLang.SelectedIndexChanged += delegate
            {
                if (settingLang) return;                      // 程序自己改的，不当作"用户切换"
                if (cbLang.SelectedIndex < 0 || cbLang.SelectedIndex >= L.All.Length) return;
                SetLang(L.All[cbLang.SelectedIndex]);
            };
            pLang.Controls.Add(lb);
            pLang.Controls.Add(cbLang);
            L.Tip(cbLang, "界面语言（默认 English，选择会记住）。");
            // GUI 全局异常兜底：WinForms 偶发（比如切语言时的嵌套重绘）会抛到这里，
            // 默认行为是弹"未处理异常"对话框 —— 对用户来说就是"程序出错"。改成记进日志继续跑。
            Application.ThreadException += delegate (object se, System.Threading.ThreadExceptionEventArgs te)
            {
                try { Log("【界面异常】" + te.Exception.Message); } catch { }
            };

            // v1.0.5：双缓冲 —— 切页/重排时少闪。WinForms 的 Form 默认是双缓冲的，
            // 但嵌在里面的 Panel / TableLayoutPanel / DataGridView 不是，切页时会闪一下，
            // 观感上就是"卡"。这里统一打开。
            EnableDoubleBuffer(this);
            EnableDoubleBuffer(tabs);
            foreach (TabPage pg in tabs.TabPages) EnableDoubleBuffer(pg);

            // 外观（浅色 / 深色）—— 和语言放一起，都在页签行右侧
            var lbTheme = new Label { Text = "Theme", AutoSize = true, Margin = new Padding(14, 6, 4, 0) };
            cbTheme = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92 };
            cbTheme.Items.Add(L.T("浅色"));
            cbTheme.Items.Add(L.T("深色"));
            cbTheme.SelectedIndex = Theme.Cur == ThemeKind.Dark ? 1 : 0;
            cbTheme.SelectedIndexChanged += delegate
            {
                if (settingTheme) return;
                SetTheme(cbTheme.SelectedIndex == 1 ? ThemeKind.Dark : ThemeKind.Light);
            };
            L.Tip(cbTheme, "浅色 / 深色。深色连标题栏一起变暗；系统弹窗和「打开文件」仍是系统样式。");
            pLang.Controls.Add(lbTheme);
            pLang.Controls.Add(cbTheme);
            Controls.Add(pLang);
            pLang.BringToFront();
            Action place = delegate
            {
                // 与**页签那一行**垂直对齐（用户反馈：原来贴在标题栏下面太突兀）。
                // 全部用表单坐标：页签条 = [tabs.Top, tabs.Top + 页签高]。
                // 构造时控件尚未布局（读到的都是 0），所以 Shown/Layout/Resize 时都要重摆。
                int top = tabs.Top;
                if (top <= 0) top = (lblDrop.Visible && lblDrop.Bottom > 0) ? lblDrop.Bottom : 26;
                int stripH = tabs.ItemSize.Height + 8;
                if (stripH < 24) stripH = 30;
                int y = top + Math.Max(0, (stripH - pLang.Height) / 2);
                pLang.Location = new Point(Math.Max(0, ClientSize.Width - pLang.Width - 18), Math.Max(0, y));
            };
            Resize += delegate { place(); };
            Shown += delegate { place(); };
            tabs.Layout += delegate { place(); };
            place();
        }

        /// <summary>切语言：重新翻译**所有**已建界面（含悬浮预览窗）。</summary>
        public void SetLang(Lang l)
        {
            // 界面更新**延迟到消息空闲**做：一次切语言涉及几百个控件重排+重绘，
            // 若正巧处在事件/绘制中间，WinForms 会偶发 Control.WmPaint 空 DC 异常。
            // **重入保护，必须有**：下面会把 cbLang.SelectedIndex 设回当前语言，
            // 那会再次触发 SelectedIndexChanged → 又进 SetLang → 无限递归，
            // 实测直接**段错误**（栈溢出）：点一下语言下拉程序就没了。
            if (settingLang) return;
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke((MethodInvoker)delegate { ApplyLangNow(l); });
                return;
            }
            ApplyLangNow(l);
        }

        void ApplyLangNow(Lang l)
        {
            if (settingLang) return;
            settingLang = true;
            try
            {
                if (L.Cur != l)
                {
                    L.Cur = l;
                    L.ClearCache();                    // 缓存是按语言算的，切语言必须清
                    relaidPages.Clear();                // 语言变了 → 各页要重新量一次高度
                    SaveLang(l);
                    L.Apply(this);
                    L.Relayout(this);
                    RefreshRuntimeCombos();
            RefreshThemeCombo();
            Theme.Apply(this);
            // v1.1.7：**表格必须显式重绘**。单元格里的文字是"绘制时"才翻译的（CellFormatting），
            // 切语言只改控件属性不会让表格重画 —— 于是旁边的标签/按钮变了、单元格还是旧语言
            // （用户反馈的就是这个）。这里主动让四个表格失效重画一次。
            InvalidateGrids();
            L.RetipAll();                      // v1.1.5：气泡提示跟着语言走
                    ApplyLangToDialogs();
                }
                else
                {
                    L.Apply(this);
                    ApplyLangToDialogs();
                }
                if (cbLang != null && cbLang.SelectedIndex != Array.IndexOf(L.All, l))
                    cbLang.SelectedIndex = Array.IndexOf(L.All, l);
                Log(L.F("Language: {0} / 界面语言已切换", L.LangName(l)));
            }
            finally { settingLang = false; }
        }

        /// <summary>运行时填的下拉项（贴图类型 / 统一范围）在切语言时要重建一次。</summary>
        void RefreshRuntimeCombos()
        {
            foreach (var cb in new[] { cbTypeUni2, cbTypeUni3 })
            {
                if (cb == null) continue;
                int keep = cb.SelectedIndex;
                cb.Items.Clear();
                foreach (var c in Classes.Order) cb.Items.Add(L.T(Classes.Short(c)));
                cb.SelectedIndex = keep >= 0 && keep < cb.Items.Count ? keep : 0;
            }
            try { G3FillUniScope(); } catch { }        // 第3页还没读卡时会抛，忽略
        }

        void ApplyLangToDialogs()
        {
            if (prevBig != null && !prevBig.IsDisposed) L.Apply(prevBig);
        }
        //
        // 存在 exe 旁边的 ui-layout.json 里（按列名存，不按序号 —— 以后加/删列也不会错位）。
        // 只记"已经真正布局过"的表格（ClientSize 太小说明还没显示过，那时的宽度是占位值）。

        const string ColWidthFile = "ui-layout.json";

        static string ColWidthPath()
        {
            string b = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(b)) b = ".";
            return Path.Combine(b, ColWidthFile);
        }

        /// <summary>按 FillWeight × 当前可视宽度算一遍默认列宽（Fill 的意图）。</summary>
        static void ApplyDefaultColWidths(DataGridView g)
        {
            int avail = g.ClientSize.Width;
            if (g.RowHeadersVisible) avail -= g.RowHeadersWidth;
            double sum = 0;
            foreach (DataGridViewColumn c in g.Columns) if (c.Visible) sum += c.FillWeight;
            foreach (DataGridViewColumn c in g.Columns)
            {
                if (sum <= 0) { c.Width = Math.Max(c.MinimumWidth, c.Width); continue; }
                c.Width = Math.Max(c.MinimumWidth, (int)Math.Round(avail * c.FillWeight / sum));
            }
        }

        /// <summary>读 ui-layout.json 里这个表格记住的列宽并应用；没有就返回 false（走默认）。</summary>
        static bool ApplySavedColWidths(DataGridView g, string key)
        {
            try
            {
                string p = ColWidthPath();
                if (!File.Exists(p)) return false;
                using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p)))
                {
                    System.Text.Json.JsonElement grids, one;
                    if (!doc.RootElement.TryGetProperty("grids", out grids)) return false;
                    if (!grids.TryGetProperty(key, out one)) return false;
                    int n = 0;
                    foreach (var prop in one.EnumerateObject())
                    {
                        var c = g.Columns[prop.Name];
                        if (c == null || prop.Value.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                        c.Width = Math.Max(c.MinimumWidth, prop.Value.GetInt32());
                        n++;
                    }
                    return n > 0;
                }
            }
            catch { return false; }        // 文件坏了就当没记住，不要让界面起不来
        }

        /// <summary>「记住列宽」：把四个表格当前的列宽写进 ui-layout.json。</summary>
        string SaveColWidths()
        {
            // 用 JsonSerializer 生成，不手拼字符串 —— 手拼就得到处写转义，
            // 而在这台机器上编辑代码时反斜杠转义很容易被中间层吃掉（吃过好几次亏）。
            var grids = new Dictionary<string, Dictionary<string, int>>();
            var names = new[] { new object[] { "gridParts", gridParts }, new object[] { "gridTex", gridTex },
                                new object[] { "gridG", gridG }, new object[] { "gridGT", gridGT } };
            int nc = 0;
            foreach (var kv in names)
            {
                var g = (DataGridView)kv[1];
                if (g.ClientSize.Width <= 160) continue;      // 还没显示过 → 宽度是占位值，不记
                var per = new Dictionary<string, int>();
                foreach (DataGridViewColumn c in g.Columns)
                {
                    if (!c.Visible) continue;
                    per[c.Name] = c.Width;
                    nc++;
                }
                if (per.Count > 0) grids[(string)kv[0]] = per;
            }
            var data = new Dictionary<string, object>();
            data["version"] = 1;
            data["grids"] = grids;
            try
            {
                var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ColWidthPath(), System.Text.Json.JsonSerializer.Serialize(data, opt), new UTF8Encoding(false));
                string msg = L.F("已记住列宽：{0} 个表格 / {1} 列 → {2}", grids.Count, nc, Path.GetFileName(ColWidthPath()));
                Log1(msg);
                return msg;
            }
            catch (Exception ex)
            {
                Log1("记住列宽失败：" + ex.Message);
                return "失败：" + ex.Message;
            }
        }

        /// <summary>「恢复默认列宽」：删掉记住的文件，并把四个表格按默认比例重排。</summary>
        string ResetColWidths()
        {
            try { if (File.Exists(ColWidthPath())) File.Delete(ColWidthPath()); } catch { }
            var gs = new[] { gridParts, gridTex, gridG, gridGT };
            int n = 0;
            foreach (var g in gs)
            {
                if (g.ClientSize.Width <= 160) continue;
                ApplyDefaultColWidths(g);
                n++;
            }
            string msg = L.F("已恢复默认列宽（{0} 个表格）", n);
            Log1(msg);
            return msg;
        }

        // ===================== v2.38：按贴图类型统一（展开栏） =====================
        // 默认收起；点开后是「贴图类型 [下拉] [应用到该类型贴图]」这两个必须挨在一起的控件
        // （包在一个 WrapContents=false 的子面板里，换行也不会被拆开）。
        bool typeUniVisible;
        /// <summary>自检用：命令行 showtype= 在窗体出现前就定下展开状态。</summary>
        public static bool TypeUniWanted;
        ComboBox cbTypeUni2, cbTypeUni3;
        Button bTypeShow2, bTypeShow3;
        Button bApplyType2, bApplyType2Part, bApplyType3, bApplyType3Part;
        Control pTypePair2, pTypePair3;
        FlowLayoutPanel pUni2, gUni;      // v1.0：自检要读它们的尺寸

        void SetTypeUniVisible(bool on)
        {
            typeUniVisible = on;
            if (pTypePair2 != null) pTypePair2.Visible = on;
            if (pTypePair3 != null) pTypePair3.Visible = on;
            // 展开/收起后高度要重算。**延迟到本次消息处理完**再做：
            // 在事件处理里同步重排+重绘容易触发嵌套重绘，实测偶发 Control.WmPaint 空 DC 异常
            // （WinForms 会当成未处理异常弹框）。句柄还没建好时（构造期）就直接做。
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) L.Relayout(this); });
            else
                L.Relayout(this);
            string t = L.T(on ? "按贴图类型统一 ▴" : "按贴图类型统一 ▾");
            if (bTypeShow2 != null) bTypeShow2.Text = t;
            if (bTypeShow3 != null) bTypeShow3.Text = t;
        }

        /// <summary>建一组「贴图类型 + 应用到该类型贴图」（第 2/3 页共用外观）。</summary>
        ComboBox MakeTypeCombo()
        {
            var cb = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 108 };
            foreach (var c in Classes.Order) cb.Items.Add(Classes.Short(c));
            cb.SelectedIndex = 0;                        // 主贴图
            return cb;
        }
        static string TypeComboCls(ComboBox cb)
        {
            int i = cb.SelectedIndex;
            return (i >= 0 && i < Classes.Order.Length) ? Classes.Order[i] : Classes.Order[0];
        }
        /// <summary>「贴图类型 + 按钮们」这一个不可拆单元。
        /// v2.39：两个按钮（应用到所有该类型贴图 / 应用到选中部位）**放不下同一行** ——
        /// 中间那张表只有约 370px，而「贴图类型」标签+下拉+两个按钮约 400px，
        /// 硬挤会把最后一个按钮裁掉（而且这类"按钮被切"不会被 CLIP 检查发现：它只查标签文字）。
        /// 所以排成两行：第一行「贴图类型 [下拉]」，第二行两个按钮 —— 仍是同一个单元，
        /// 别的控件插不进来。</summary>
        static Control MakeTypePair(ComboBox cb, params Button[] btns)
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                          FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            var line1 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
            line1.Controls.Add(new Label { Text = "贴图类型", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            line1.Controls.Add(cb);
            // 每个按钮**各占一行**：英文/日文比中文长很多（实测中文两个按钮同排 253px、
            // 英文同排 363px），而这一栏窄的时候只有约 306px —— 同排必然溢出。
            // 允许换行：英文两个按钮并排要 313px，而这一栏窄的时候只有 306px；
            // 换行后各占一行放得下，中文仍然是并排一行（观感不变）。
            var line2 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = true };
            foreach (var b in btns) line2.Controls.Add(b);
            p.Controls.Add(line1);
            p.Controls.Add(line2);
            return p;
        }
        static void HookTypeToggle(Button b, Action flip)
        {
            b.AutoSize = true;
            b.Click += delegate { flip(); };
        }

        /// <summary>把「一键统一」那套值套到**当前卡里某一类型**的全部贴图（按 TexID 记单张指定）。
        /// 与「应用到本部位全部贴图」同一套取值约定。</summary>

        /// <summary>自检用：设「按贴图类型统一」的展开状态（两页一起），并回读可见性。</summary>
        public string UiTypeVisible(bool? on)
        {
            if (on.HasValue) SetTypeUniVisible(on.Value);
            // 顺带一个几何断言：这一单元有没有超出父面板（"按钮被裁掉"用肉眼看容易漏，CLIP 也只查标签文字）
            return L.F("按类型展开={0} 第2页={1}/{2} 第3页={3}/{4} 下拉2={5} 下拉3={6} | 宽度2={7} 宽度3={8}",
                typeUniVisible,
                pTypePair2 != null && pTypePair2.Visible, pTypePair2 != null,
                pTypePair3 != null && pTypePair3.Visible, pTypePair3 != null,
                cbTypeUni2 != null && cbTypeUni2.Visible, cbTypeUni3 != null && cbTypeUni3.Visible,
                FitText(pTypePair2, gridParts), FitText(pTypePair3, gridG));
        }

        /// <summary>「这一单元有没有超出父面板」的可读结论。</summary>
        /// <summary>拿"表格的可视宽度"当参照（那才是这一栏真正能用的宽度）。
        /// 不能用父面板宽度：它是 AutoSize 的，宽度等于内容宽度，比出来永远"放得下"。</summary>
        static string FitText(Control c, DataGridView refGrid)
        {
            if (c == null) return "-";
            // 没显示的页不判：那时控件还没布局过，量到的是旧值，会误报"溢出"
            // （真正显示时 CLIP 检查会管，这里只当提示用）
            if (!c.Visible) return "未显示";
            int need = c.PreferredSize.Width;
            int have = (refGrid != null && refGrid.ClientSize.Width > 200) ? refGrid.ClientSize.Width : 0;
            if (have == 0) return L.F("未布局(需要{0})", need);
            return L.F("需要{0}/表格宽{1}{2}", need, have, need > have ? " 溢出!" : " 放得下");
        }

        /// <summary>自检用：走真实按钮「应用到该类型贴图」（charaPage=true 走第 3 页）。</summary>
        public string UiTypeUni(bool charaPage, string cls)
        {
            // cls 可以写成 "maintex" 或 "maintex:part"（后者点「应用到选中部位」）
            bool onlyPart = false;
            if (cls != null && cls.EndsWith(":part")) { onlyPart = true; cls = cls.Substring(0, cls.Length - 5); }
            var cb = charaPage ? cbTypeUni3 : cbTypeUni2;
            var btn = charaPage ? (onlyPart ? bApplyType3Part : bApplyType3) : (onlyPart ? bApplyType2Part : bApplyType2);
            if (cb == null || btn == null) return "控件没建（先打开对应页）";
            int i = Array.IndexOf(Classes.Order, cls);
            if (i >= 0) cb.SelectedIndex = i;
            btn.PerformClick();
            Application.DoEvents();
            string dump = charaPage ? UiTexOvDump3() : UiTexOvDump();
            string st = charaPage ? lblG3Status.Text : lblP2Status.Text;
            return L.F("类型={0} → {1} ｜ 贴图覆盖：{2}", cls, st,
                dump.Length > 300 ? dump.Substring(0, 300) + "…" : dump);
        }

        /// <summary>自检用：列宽的 记住/恢复/查看。</summary>
        public string UiColWidths(string action)
        {
            if (action == "save") return SaveColWidths();
            if (action == "reset") return ResetColWidths();
            string p = ColWidthPath();
            return L.F("文件={0} 存在={1}{2} | {3}", Path.GetFileName(p), File.Exists(p),
                File.Exists(p) ? "（" + new FileInfo(p).Length + " 字节）" : "", UiGridWidths());
        }

        /// <summary>自检用：统一设置面板（pUni2 / gUni）的尺寸读数。</summary>
        public string UiUniPanelInfo()
        {
            var sb = new StringBuilder();
            foreach (var kv in new object[][] { new object[] { "pUni2", pUni2 }, new object[] { "gUni", gUni } })
            {
                var p = kv[1] as Control;
                if (p == null) { sb.AppendFormat("{0}=null  ", kv[0]); continue; }
                sb.AppendFormat("{0}: H={1} 需要H={2} ClientH={3} 子控件={4} 最后一个={5}",
                    kv[0], p.Height, p.PreferredSize.Height, p.ClientSize.Height, p.Controls.Count,
                    p.Controls.Count > 0 ? p.Controls[p.Controls.Count - 1].Bounds.ToString() : "-");
                sb.Append("  ||  明细：");
                foreach (Control c in p.Controls)
                    sb.AppendFormat("[{0}:{1} V={2} {3}] ", c.GetType().Name, c.Text.Length > 0 ? c.Text : "-", c.Visible, c.Bounds);
            }
            return sb.ToString();
        }

        /// <summary>自检用：按文字找控件并打印它的父链尺寸（排查"控件看不见"用）。</summary>
        public string UiFindTrace(string text)
        {
            var hits = new List<Control>();
            FindAllByText(this, text, hits);
            if (hits.Count == 0) return "没找到：" + text;
            var sb = new StringBuilder();
            foreach (var hit in hits)
            {
                sb.AppendFormat("{0} \"{1}\" Bounds={2} 需要={3}", hit.GetType().Name, hit.Text, hit.Bounds, hit.PreferredSize);
                for (var p = hit.Parent; p != null; p = p.Parent)
                    sb.AppendFormat("  ← {0} Client={1} AutoSize={2} Dock={3}",
                        p.GetType().Name, p.ClientSize, p.AutoSize, p.Dock);
                sb.Append("  ||  ");
            }
            return sb.ToString();
        }
        static void FindAllByText(Control root, string text, List<Control> hits)
        {
            foreach (Control c in root.Controls)
            {
                if (c.Text == text) hits.Add(c);
                FindAllByText(c, text, hits);
            }
        }

        /// <summary>自检用：切完语言后抽几个控件看看是不是真翻过去了（返回"某控件=译文"）。</summary>
        public string UiLangSwitchProbe()
        {
            var sb = new StringBuilder();
            sb.Append("语言下拉=").Append(cbLang != null && cbLang.SelectedIndex >= 0
                ? cbLang.Items[cbLang.SelectedIndex].ToString() : "?");
            var b = FindByTextAny(btnP2Run);
            if (b != null) sb.Append(" 主按钮=").Append(b.Text);
            if (cbTypeUni2 != null && cbTypeUni2.Items.Count > 0)
                sb.Append(" 类型下拉0=").Append(cbTypeUni2.Items[0]);
            return sb.ToString();
        }
        static Button FindByTextAny(Button b) { return b; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

        /// <summary>自检用：用 PrintWindow 抓**真实窗口**（含我们自己画的内容）。
        /// 外部截图工具要么抓不到前台窗口、要么抓到别的进程，不可靠；进程内抓最稳。</summary>
        public string UiSnapReal(string path)
        {
            try
            {
                // 先把消息泵空：切语言/切主题都是**延迟执行**的（BeginInvoke），
                // 不泵一遍就会拍到"还没切"的画面（我第一版就拍错了）。
                Application.DoEvents();
                System.Threading.Thread.Sleep(400);
                Application.DoEvents();
                var r = new Rectangle(Location, Size);
                using (var bmp = new Bitmap(Math.Max(200, Width), Math.Max(200, Height)))
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok = PrintWindow(Handle, hdc, 2);      // 2 = PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return string.Format("{0} {1}x{2} ok={3}", path, bmp.Width, bmp.Height, ok);
                }
            }
            catch (Exception ex) { return "抓图失败：" + ex.Message; }
        }

        /// <summary>窗口图标：直接从 exe 的图标取（csproj 里 ApplicationIcon 已设），
        /// 不用再往资源里塞一份 .ico。</summary>
        internal static void SetAppIcon(Form f)
        {
            try { f.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
        }

        /// <summary>让四个表格重绘（单元格文字是绘制时翻译的，切语言/切主题后必须重画）。</summary>
        void InvalidateGrids()
        {
            var gs = new DataGridView[] { gridParts, gridTex, gridG, gridGT };
            foreach (var g in gs)
            {
                if (g == null || g.IsDisposed) continue;
                g.Invalidate();
                // 下拉格还要让"当前单元格"重画（它由编辑控件绘制，缓存更久）
                try { g.InvalidateColumn(0); } catch { }
            }
        }

        /// <summary>自检用：几个表头当前显示的文字（用来验证切语言后表头有没有跟着变）。</summary>
        public string UiHeaderProbe()
        {
            var sb = new StringBuilder();
            foreach (var g in new[] { gridParts, gridTex, gridG, gridGT })
            {
                if (g == null || g.Columns.Count == 0) continue;
                sb.Append("[").Append(g.Columns[0].HeaderText).Append("|");
                if (g.Columns.Count > 1) sb.Append(g.Columns[1].HeaderText);
                sb.Append("] ");
            }
            return sb.ToString();
        }

        /// <summary>自检用：单元格格式化（=绘制时翻译）被调用的次数。
        /// 切语言后它必须增加 —— 否则说明表格没有重绘，单元格会停在旧语言。</summary>
        public static long CellFmtCount;

        /// <summary>自检用：四个压缩选项的气泡文字（按当前语言）。</summary>
        public string UiTipProbe()
        {
            return "细节=" + L.TipOf(chkKernel2) + " ｜ 彩色=" + L.TipOf(chkColor2)
                 + " ｜ 独占=" + L.TipOf(chkOwn2) + " ｜ 灰度=" + L.TipOf(chkGrayA)
                 + "  ／  " + L.MissingTips();
        }

        /// <summary>自检用：主题是否真的套上了（几个代表性控件的实际颜色）。</summary>
        public string UiThemeProbe()
        {
            return string.Format("主题={0} 窗体={1} txtIn={2} log={3} grid表头={4} 页签={5} 主按钮={6}",
                Theme.Name(Theme.Cur), BackColor.ToArgb().ToString("X6"),
                txtIn.BackColor.ToArgb().ToString("X6"), log.BackColor.ToArgb().ToString("X6"),
                gridParts.ColumnHeadersDefaultCellStyle.BackColor.ToArgb().ToString("X6"),
                tabs.TabPages.Count > 0 ? tabs.TabPages[0].BackColor.ToArgb().ToString("X6") : "-",
                btnRun.BackColor.ToArgb().ToString("X6"))
                + " 标题栏API=" + Theme.TitleBarRc + " 边框/标题栏颜色API=" + Theme.FrameRc + "（0=系统已接受）"
                + " 页签=" + tabs.TabPages.Count + "页/" + tabs.DrawMode;
        }

        /// <summary>自检用：语言面板与页签条的实际坐标。</summary>
        public string UiLangPanelRect()
        {
            return string.Format("面板={0} 可见={1} / dropBottom={2} tabs.Top={3} tabs.DisplayRect.Top={4} ItemH={5} 表单={6}x{7}",
                pLang.Location, pLang.Visible, lblDrop.Bottom, tabs.Top, tabs.DisplayRectangle.Top,
                tabs.ItemSize.Height, ClientSize.Width, ClientSize.Height);
        }

        /// <summary>递归打开双缓冲（切页时不闪）。</summary>
        static void EnableDoubleBuffer(Control root)
        {
            if (root == null) return;
            try
            {
                var pi = typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(root, true, null);
            }
            catch { }
            foreach (Control c in root.Controls) EnableDoubleBuffer(c);
        }

        /// <summary>自检用：上一次切页耗时（毫秒）。</summary>
        public long LastTabMs;
        readonly HashSet<object> relaidPages = new HashSet<object>();   // 已按当前语言量过的页

        /// <summary>自检用：来回切页 N 次，返回每趟耗时（含重排累计）。</summary>
        public string UiTabBench(int n)
        {
            var sb = new StringBuilder();
            L.RelayoutMs = 0; L.RelayoutCalls = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                int which = i % Math.Max(1, tabs.TabPages.Count);
                tabs.SelectedIndex = which;
                Application.DoEvents();
                sb.AppendFormat("{0}ms ", LastTabMs);
            }
            sw.Stop();
            sb.AppendFormat("| 共 {0}ms（平均 {1:0.0}ms/次），其中重排累计 {2}ms / {3} 次",
                sw.ElapsedMilliseconds, (double)sw.ElapsedMilliseconds / Math.Max(1, n),
                L.RelayoutMs, L.RelayoutCalls);
            return sb.ToString();
        }

        /// <summary>自检用：当前是否在忙（**与语言无关** —— 自检脚本原来靠状态栏的中文
        /// "完成："前缀判断跑完没，界面切成英文后就永远匹配不上，会一直等到 15 分钟超时）。</summary>
        public bool UiBusyNow { get { return btnCancel.Enabled; } }
        /// <summary>自检用：第 2 页是否在处理中（同样与语言无关）。</summary>
        public bool UiP2Busy { get { return btnP2Cancel.Enabled; } }

        /// <summary>自检用：列出**界面上还剩哪些中文没翻译**（控件文字 + 表格单元格 + 下拉项）。
        /// 用来收口多语言，比对着截图找靠谱。</summary>
        public string UiCjkLeft()
        {
            var seen = new HashSet<string>();
            var ctrl = new List<string>();
            var cell = new List<string>();
            WalkCjk(this, seen, ctrl, cell);
            var sb = new StringBuilder();
            sb.AppendFormat("控件里剩 {0} 条：{1}", ctrl.Count, string.Join(" | ", ctrl.ToArray()));
            sb.AppendFormat("  ／ 表格/下拉项里剩 {0} 条：{1}", cell.Count, string.Join(" | ", cell.ToArray()));
            return sb.ToString();
        }

        static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s) if (ch >= '一' && ch <= '鿿') return true;
            return false;
        }

        static void WalkCjk(Control root, HashSet<string> seen, List<string> ctrl, List<string> cell)
        {
            string shown = L.Tr(root.Text);      // 看**实际显示**的文字，不是底层原文
            if (shown != null && HasCjk(shown) && seen.Add(shown))
            {
                bool isCtrl = root is Label || root is Button || root is CheckBox || root is RadioButton
                              || root is GroupBox || root is TabPage || root is Form;
                (isCtrl ? ctrl : cell).Add(shown);
            }
            var cb = root as ComboBox;
            if (cb != null)
                foreach (var it in cb.Items)
                {
                    string t = it as string;
                    string ts = L.Tr(t);
                    if (ts != null && HasCjk(ts) && seen.Add(ts)) cell.Add("[下拉]" + ts);
                }
            var g = root as DataGridView;
            if (g != null)
            {
                foreach (DataGridViewColumn c2 in g.Columns)
                    if (HasCjk(c2.HeaderText) && seen.Add(c2.HeaderText)) cell.Add("[表头]" + c2.HeaderText);
                foreach (DataGridViewRow r in g.Rows)
                    if (r.Visible)
                        foreach (DataGridViewCell c2 in r.Cells)
                        {
                            string t = L.Tr(c2.Value as string);
                            if (t != null && HasCjk(t) && seen.Add(t)) cell.Add(t);
                        }
            }
            foreach (Control c in root.Controls) WalkCjk(c, seen, ctrl, cell);
        }

        /// <summary>自检用：四个表格的列宽与模式一览。</summary>
        public string UiGridWidths()
        {
            var sb = new StringBuilder();
            foreach (var kv in new[] { new object[] { "parts", gridParts }, new object[] { "tex", gridTex },
                                       new object[] { "g", gridG }, new object[] { "gt", gridGT } })
            {
                var g = (DataGridView)kv[1];
                sb.AppendFormat("{0}[模式={1} 可视={2} 总宽={3}] ", kv[0], g.AutoSizeColumnsMode, g.ClientSize.Width, GridTotalWidth(g));
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>自检用：模拟拖动某列右边界（往右 dx 像素）并回读，验证拖得动、不会被还原。</summary>
        public string UiDragCol(string which, string colName, int dx)
        {
            DataGridView g = which == "tex" ? gridTex : (which == "g" ? gridG : (which == "gt" ? gridGT : gridParts));
            DataGridViewColumn c = g.Columns[colName];
            if (c == null) return "没有这一列：" + colName;
            int before = c.Width;
            c.Width = before + dx;
            Application.DoEvents();
            g.PerformLayout();
            Application.DoEvents();
            return L.F("{0}.{1}: {2} → {3}（要求 +{4}，实际 +{5}）模式={6} 总宽={7} 可视={8}",
                which, colName, before, c.Width, dx, c.Width - before, g.AutoSizeColumnsMode,
                GridTotalWidth(g), g.ClientSize.Width);
        }
        static int GridTotalWidth(DataGridView g)
        {
            int t = 0;
            foreach (DataGridViewColumn c in g.Columns) if (c.Visible) t += c.Width;
            return t;
        }

        /// <summary>v2.36：把「放大」下拉选的倍率写进类型表里**所有已勾选**的类。
        /// 没勾的类是「原样不动」（plan 里直接是 keep），改它没有意义，跳过并计数。</summary>
        void ApplyUpToClasses()
        {
            string item = cbUpAll.SelectedItem as string;
            if (string.IsNullOrEmpty(item)) item = Ups[0];
            int idx = Array.IndexOf(Ups, item);
            if (idx < 0) idx = 0;
            int n = 0, skipped = 0;
            foreach (var cls in Classes.Order)
            {
                if (!on.ContainsKey(cls) || !up.ContainsKey(cls)) continue;
                if (!on[cls].Checked) { skipped++; continue; }
                up[cls].SelectedIndex = idx;
                n++;
            }
            string msg = L.F("已把「放大」设为 {0}：写进 {1} 个类{2}",
                item, n, skipped > 0 ? L.F("（另有 {0} 个类是「原样不动」，跳过）", skipped) : "");
            lblStatus.Text = msg;
            Log1(msg);
            if (idx > 0)
                Log1("  提示：放大只在**彩色类**生效（法线/掩罩/高度会被忽略）；且受「最大边」与 4096px 硬上限约束，装不下会自动降级。");
        }

        /// <summary>自检用：设全局「放大」并点「应用放大设置」，返回类型表的结果。</summary>
        public string UiApplyUp(string item)
        {
            if (!string.IsNullOrEmpty(item))
            {
                int i = Array.IndexOf(Ups, item);
                if (i >= 0) cbUpAll.SelectedIndex = i;
            }
            ApplyUpToClasses();
            return UiPlanSummary();
        }

        // ===================== v2.34：放大选项默认藏起来 =====================
        //
        // 放大是"代价很大、多数人用不上"的功能（×2 会让整卡 +36%），所以它的所有设置
        // 默认**不显示**：三个页面各有一个「显示放大选项」按钮，按下去才展开。
        // 展开状态是**全局共享**的（和「贴图细节保护」那几个开关一路：改一处三页同步），
        // 但它只是界面状态，**不进预设**（预设里只存 up 的值本身）。
        bool upUiVisible;
        /// <summary>自检用：命令行 showup= 想在窗体出现前就定下展开状态（截图/布局 dump 更早）。</summary>
        public static bool UpUiWanted;
        readonly List<Control> upUiCtrls = new List<Control>();    // 第 1 页：类型表「放大」列 + 放大算法
        readonly List<Control> upUiCtrls2 = new List<Control>();   // 第 2 页：一键统一里的两项
        readonly List<Control> upUiCtrls3 = new List<Control>();   // 第 3 页：同上
        readonly List<DataGridViewColumn> upUiCols = new List<DataGridViewColumn>();   // 第 2/3 页表格里的「放大」列
        Button bShowUp1, bShowUp2, bShowUp3;
        ColumnStyle t2UpColStyle;                                  // 第 1 页类型表「放大」那一列的宽度

        void SetUpUiVisible(bool on)
        {
            upUiVisible = on;
            if (t2UpColStyle != null) t2UpColStyle.Width = on ? 92 : 0;
            foreach (var c in upUiCtrls) if (c != null) c.Visible = on;
            foreach (var c in upUiCtrls2) if (c != null) c.Visible = on;
            foreach (var c in upUiCtrls3) if (c != null) c.Visible = on;
            foreach (var c in upUiCols) if (c != null) c.Visible = on;
            string txt = (on ? "隐藏放大选项 ▴" : "显示放大选项 ▾");
            if (bShowUp1 != null) bShowUp1.Text = txt;
            if (bShowUp2 != null) bShowUp2.Text = txt;
            if (bShowUp3 != null) bShowUp3.Text = txt;
        }

        void HookUpToggle(Button b)
        {
            if (b == null) return;
            b.AutoSize = true;
            b.Click += delegate { SetUpUiVisible(!upUiVisible); };
        }

        /// <summary>表格里「放大」列的可选项。**没有「跟随」**（v2.35 用户要求）：
        /// 选「不放大」就表示"这一级不表态"，效果与跟随类型表一致 —— 见 <see cref="UpTableToVal"/>。</summary>
        static readonly string[] UpItems = Ups;         // 不放大 / ×2 / ×4
        static readonly string[] TexUpItems = Ups;

        /// <summary>表格里「放大」列的取值：只有 ×2/×4 才算"表了态"，返回 2/4；
        /// 「不放大」= 不指定（跟随上一级），返回 -1。
        /// 为什么这么定：类型表那一列也是"不放大 = 不写 up 字段"，两边约定一致；
        /// 而且部位行不会因为默认值就把类级设的放大顶掉（那类问题已经踩过好几次）。</summary>
        static int UpTableToVal(string item)
        {
            for (int i = 1; i < UpKey.Length; i++) if (Ups[i] == item) return UpKey[i];
            return -1;
        }
        static string UpValToItem(int v)
        {
            for (int i = 0; i < UpKey.Length; i++) if (UpKey[i] == v) return Ups[i];
            return "不放大";
        }

        /// <summary>「独占贴图保护」上限下拉框的档位→ 数值（0 = 完全不降分辨率）。</summary>
        // 「独占贴图保护」的放宽上限：4096（画质优先）/ 2048（折中）/ 1024 / 原尺寸不降
        static readonly string[] OwnMaxItems = { "放宽到 4096", "放宽到 2048", "放宽到 1024", "原尺寸不降" };
        static readonly int[] OwnMaxVals = { 4096, 2048, 1024, 0 };
        static int OwnMaxIndex(int max)
        {
            for (int i = 0; i < OwnMaxVals.Length; i++) if (OwnMaxVals[i] == max) return i;
            return max <= 0 ? 3 : (max >= 4096 ? 0 : (max >= 2048 ? 1 : 2));
        }
        static int OwnMaxValue(int idx) { return (idx >= 0 && idx < OwnMaxVals.Length) ? OwnMaxVals[idx] : 4096; }

        /// <summary>同时设置开关与上限（预设套用/ 初始化都用这个，避免下拉框事件互相覆盖）。</summary>
        void SetOwnProtect(bool on, int max)
        {
            TexTool.ProtectOwnMax = max;
            TexTool.ProtectOwn = on;
            chkOwn.Checked = on;
            cbOwnMax.SelectedIndex = OwnMaxIndex(max);
            cbOwnMax.Enabled = on;
        }

        void PreApply(int idx)
        {
            if (pre1Loading || idx < 0 || idx >= pre1.Count) return;
            var it = pre1[idx];
            if (it.Path == null)
            {
                lblPre1.Text = "不使用预设：完全按上面的类型表";
                return;
            }
            string json = PresetIo.ReadJson(it.Path);
            if (json == null) { lblPre1.Text = "这个预设读不出来：" + Path.GetFileName(it.Path); return; }
            Dictionary<string, Rule> plan; int q; bool prot, ca; string sh; bool caSeen;
            if (!PresetIo.TryReadPlanEx(json, out plan, out q, out prot, out ca, out sh, out caSeen))
            {
                lblPre1.Text = "这个预设里没有批量用的类型表（可能是按部位/人物卡预设）：" + it.Name;
                return;
            }
            ApplyPlan(plan);
            // v2.32：老预设没有 colorArea 字段 → **不覆盖**当前值（跟随当前默认），
            // 而不是当成 false 把它关掉。命令行那条路本来就是这么做的。
            ApplyProcessFlags(caSeen ? ca : Resample.ColorMode, sh);
            ApplySharpen(json);
            bool own; int ownMax;
            if (PresetIo.TryReadOwnProtect(json, out own, out ownMax))
                SetOwnProtect(own, ownMax);
            trkQuality.Value = Math.Max(trkQuality.Minimum, Math.Min(trkQuality.Maximum, q));
            lblQuality.Text = trkQuality.Value.ToString();
            _ = prot;   // 旧预设里的 protectAlpha 字段不再使用
            SyncPreDefaultCheck();     // 下拉框换了 → 默认预设勾选框跟着刷新
            lblPre1.Text = L.F("已套用预设「{0}」（原卡 {1}，存于 {2}）{3}{4}{5}",
                L.T(it.Name), it.Source.Length > 0 ? L.T(it.Source) : L.T("未知"),
                it.Saved.Length > 0 ? it.Saved : L.T("未知时间"),
                ca ? L.T("　彩色贴图也走面积平均+锐化") : "",
                TexTool.ProtectOwn ? L.F("　独占贴图放宽到 {0}",
                    TexTool.ProtectOwnMax > 0 ? TexTool.ProtectOwnMax + "px" : L.T("原尺寸")) : "",
                L.F("　锐化 {0}%{1}", Resample.SharpenPct, Resample.SharpMode == "cas" ? "(CAS)" : ""));
            Log1(L.T("套用预设：") + it.Path
                + (ca ? L.F("（colorArea=1{0}）", sh.Length > 0 ? " sharpen=" + sh : "") : ""));
        }

        /// <summary>把当前类型表 + 质量 存成预设（批量页用纯 JSON：
        /// preset\batch\[preset]名字_原卡名json，人可读可手改，也方便丢进版本管理）。</summary>
        void PreSaveDialog()
        {
            string card = PreCurrentCard();
            string nm = AskName("保存为预设（批量）",
                "预设名称（会存成 " + PresetIo.FileName("名称", card ?? "card.png", "json") + "）", "我的设置");
            if (nm == null) return;
            string dir = PresetIo.EnsureDir(PresetIo.KindBatch);
            string path = Path.Combine(dir, PresetIo.FileName(nm, card ?? "card.png", "json"));
            try
            {
                string json = PresetIo.Envelope(PresetIo.KindBatch, PresetIo.Sanitize(nm),
                    card == null ? "" : Path.GetFileName(card),
                    "clothes", 0.5, null, null, null, null, BuildPlan(), (int)trkQuality.Value, protectAlphaCompat,
                    Resample.ColorMode, Resample.SharpenEdgeMax > 0 ? "edge" :
                        (Math.Abs(Resample.Sharpen) < 1e-9 ? "0" : Resample.Sharpen.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    chkOwn.Checked, TexTool.ProtectOwnMax,
                    Resample.SharpenPct, Resample.SharpMode);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                PreReload();
                for (int i = 0; i < pre1.Count; i++)
                    if (pre1[i].Path != null && string.Equals(pre1[i].Path, path, StringComparison.OrdinalIgnoreCase)) { cbPre1.SelectedIndex = i; break; }
                Log1("已保存预设：" + path);
                lblPre1.Text = "已保存：" + Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L.Tr("预设存不下来：\n") + path + "\n" + ex.Message +
                    "\n\n（预设固定放在 EXE 同级的 preset 目录里；如果 EXE 放在只读目录/压缩包里，把整个文件夹拷到可写的位置再试。）");
            }
        }

        /// <summary>从文件夹里挑一个预设读进来（批量页：默认开在preset\batch）。</summary>
        void PreLoadDialog()
        {
            var d = new OpenFileDialog
            {
                Filter = "预设 JSON|*.json|所有文件|*.*",
                InitialDirectory = PresetIo.EnsureDir(PresetIo.KindBatch),
                Title = "读取预设（默认在 preset\\batch 里）"
            };
            if (d.ShowDialog() != DialogResult.OK) return;
            string json = PresetIo.ReadJson(d.FileName);
            if (json == null) { MessageBox.Show(L.Tr("这个文件读不出预设内容。\n") + d.FileName); return; }
            Dictionary<string, Rule> plan; int q; bool prot;
            if (!PresetIo.TryReadPlan(json, out plan, out q, out prot))
            {
                MessageBox.Show(L.Tr("这个预设里没有批量用的类型表（可能是按部位/人物卡预设）：\n") + Path.GetFileName(d.FileName));
                return;
            }
            ApplyPlan(plan);
            trkQuality.Value = Math.Max(trkQuality.Minimum, Math.Min(trkQuality.Maximum, q));
            lblQuality.Text = trkQuality.Value.ToString();
            _ = prot;   // 旧预设里的 protectAlpha 字段不再使用
            lblPre1.Text = "已读取预设：" + Path.GetFileName(d.FileName);
            Log1("读取预设：" + d.FileName);
        }

        /// <summary>当前要当"原卡"的那张卡（拖入路径里的第一张；目录就取里面第一张卡）。</summary>
        string PreCurrentCard()
        {
            string src = dropped.Count > 0 ? dropped[0] : txtIn.Text.Trim();
            if (src.Length == 0) return null;
            try
            {
                if (File.Exists(src)) return src;
                if (Directory.Exists(src))
                {
                    foreach (var f in TexTool.Enumerate(src, TexTool.DefaultOut(src), false)) return f.Src;
                }
            }
            catch { }
            return null;
        }

        /// <summary>打开某个预设目录（batch / coordinate / chara）。</summary>
        void PreOpenDir(string kind)
        {
            string d = PresetIo.EnsureDir(kind);
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\""); }
            catch (Exception ex) { MessageBox.Show(L.Tr("打不开目录：\n") + d + "\n" + ex.Message); }
        }

        /// <summary>要一个名字（自己画的小输入框，不依赖 Microsoft.VisualBasic）。</summary>
        internal static string AskName(string title, string hint, string initial)
        {
            using (var f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent; f.MinimizeBox = false; f.MaximizeBox = false;
                f.ClientSize = new Size(460, 132); f.Font = SystemFonts.MessageBoxFont;
                var lb = new Label { Text = hint, AutoSize = false, Left = 12, Top = 12, Width = 436, Height = 32 };
                var tb = new TextBox { Left = 12, Top = 48, Width = 436, Text = initial ?? "" };
                tb.SelectAll();
                var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Left = 292, Top = 84, Width = 74 };
                var no = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 374, Top = 84, Width = 74 };
                f.Controls.Add(lb); f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(no);
                f.AcceptButton = ok; f.CancelButton = no;
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }


        // ---- 第1 页的「人物卡组过滤」：只对人物卡生效（位 g = 第 g 组：0=角色本体，1..N=第 N 套换装）----
        // v2.30：**不再写死 8 个复选框**。原版游戏是"本体 + 7 套换装"，但有些人物卡带的换装超过 7 套
        // （插件/改卡会往 CoordinateIndex 后面加）。以前写死 8 个 → 第 8 套往后的组永远勾不上 →
        // 压缩器那边 `g<0 || g>30 || (mask & (1<<g))==0` 就把它们的贴图**静默跳过**。
        // 现在按"扫描到的卡里实际出现的组"动态生成；位掩码上限 30（压缩器用 int，g>30 会被忽略）。
        const int MaxGroupBit = 30;
        readonly List<CheckBox> chkGrp = new List<CheckBox>();   // 每项的 Tag = 组号
        bool[] grpOn = new bool[1];                              // 每组的勾选状态（重建复选框时保留）
        int grpMax = 7;                                          // 界面上当前显示到第几组（默认游戏那 7 套）
        FlowLayoutPanel pGrpChk;                                 // 只装复选框的子面板（换卡重扫时就重建它）

        int CharaMask()
        {
            int m = 0;
            foreach (var c in chkGrp)
                if (c != null && c.Checked && c.Tag is int && (int)c.Tag <= MaxGroupBit) m |= (1 << (int)c.Tag);
            return m;
        }

        void CharaMaskAll(bool all, bool onlyBody)
        {
            foreach (var c in chkGrp)
                if (c != null) c.Checked = onlyBody ? (c.Tag is int && (int)c.Tag == 0) : all;
        }

        /// <summary>按"实际出现的最大组号"重建那一排复选框（勾选状态按组号保留）。</summary>
        void BuildGrpFilter(int maxGroup)
        {
            if (maxGroup < 0) maxGroup = 0;
            if (maxGroup > MaxGroupBit) maxGroup = MaxGroupBit;
            if (pGrpChk == null) return;
            // 先把当前勾选状态记下来（按组号，重建后原样恢复）
            int oldMax = grpOn.Length - 1;
            if (maxGroup > oldMax)
            {
                var bigger = new bool[maxGroup + 1];
                for (int g = 0; g <= oldMax; g++) bigger[g] = grpOn[g];
                for (int g = oldMax + 1; g <= maxGroup; g++) bigger[g] = true;   // 新出现的组默认勾上
                grpOn = bigger;
            }
            foreach (var c in chkGrp)
                if (c != null && c.Tag is int)
                {
                    int g0 = (int)c.Tag;
                    if (g0 >= 0 && g0 < grpOn.Length) grpOn[g0] = c.Checked;
                }
            grpMax = maxGroup;
            pGrpChk.SuspendLayout();
            while (pGrpChk.Controls.Count > 0)
            {
                var old = pGrpChk.Controls[0];
                pGrpChk.Controls.RemoveAt(0);
                old.Dispose();
            }
            chkGrp.Clear();
            for (int g = 0; g <= maxGroup; g++)
            {
                var cb = new CheckBox { Text = L.T(g == 0 ? "本体" : ("换" + g)), Checked = g < grpOn.Length ? grpOn[g] : true, AutoSize = true, Tag = g };
                L.Tip(cb, Chara.GroupName(g) + (g == 0 ? "" : "（第 " + g + " 套换装）"));
                pGrpChk.Controls.Add(cb);
                chkGrp.Add(cb);
            }
            pGrpChk.ResumeLayout();
        }

        // ---- 无头自检钩子（第 1 页预设框）---
        public string UiPreDump
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendFormat("dir={0}\r\n", PresetIo.Dir(PresetIo.KindBatch));
                sb.AppendFormat("坐标={0}\r\n", PresetIo.Dir(PresetIo.KindCoord));
                sb.AppendFormat("计数={0}  选中={1}  提示={2}\r\n", pre1.Count, cbPre1.SelectedIndex, lblPre1.Text);
                for (int i = 0; i < pre1.Count; i++)
                    sb.AppendFormat("  [{0}] {1} | 卡面={2} | 原卡={3} | 文件={4}\r\n",
                        i, pre1[i].Name, pre1[i].Cover == null ? "无" : (pre1[i].Cover.Width + "x" + pre1[i].Cover.Height),
                        pre1[i].Source, pre1[i].Path == null ? "(不使用预设)" : Path.GetFileName(pre1[i].Path));
                return sb.ToString();
            }
        }

        /// <summary>走真实路径：选中下拉框第 i 项（触发 SelectedIndexChanged →套用）。</summary>
        public void UiPreSelect(int i)
        {
            if (i >= 0 && i < pre1.Count) cbPre1.SelectedIndex = i;
        }

        public void UiPreReload() { PreReload(); }

        /// <summary>自检用：走界面同一条路勾/取消「将当前预设设为默认」，然后重载预设看选中项。</summary>
        public string UiSetDefaultPreset(bool on)
        {
            chkPreDefault.Checked = on;
            Application.DoEvents();
            PreReload();
            Application.DoEvents();
            string marker = PresetIo.GetDefaultPreset(PresetIo.KindBatch);
            return L.F("标记={0} 重载后选中={1} 勾选框={2}",
                marker ?? "(无)",
                (cbPre1.SelectedIndex >= 0 && cbPre1.SelectedIndex < pre1.Count) ? pre1[cbPre1.SelectedIndex].Name : "?",
                chkPreDefault.Checked);
        }

        /// <summary>自检用：按 "maintex:jpeg:512,normal:png:512" 改类型表。</summary>
        public void UiPreSetPlan(string spec)
        {
            var plan = new Dictionary<string, Rule>();
            foreach (var c in Classes.Order) plan[c] = new Rule("keep", 0);
            foreach (var part in (spec ?? "").Split(','))
            {
                var f = part.Split(':');
                if (f.Length < 3 || !plan.ContainsKey(f[0].Trim())) continue;
                plan[f[0].Trim()] = new Rule(f[1].Trim().ToLowerInvariant(), int.Parse(f[2].Trim()));
            }
            ApplyPlan(plan);
        }

        /// <summary>自检用：把当前类型表存成预设（不弹输入框）。批量页 = 纯JSON。</summary>
        public string UiPreSave(string name, string cardPath)
        {
            string card = cardPath;
            if (string.IsNullOrEmpty(card)) card = PreCurrentCard();
            if (string.IsNullOrEmpty(card)) card = "card.png";
            string path = Path.Combine(PresetIo.EnsureDir(PresetIo.KindBatch), PresetIo.FileName(name, card, "json"));
            string json = PresetIo.Envelope(PresetIo.KindBatch, PresetIo.Sanitize(name), Path.GetFileName(card),
                "clothes", 0.5, null, null, null, null, BuildPlan(), (int)trkQuality.Value, protectAlphaCompat);
            PresetIo.WriteJson(path, json);
            PreReload();
            return path;
        }

        /// <summary>自检用：读回一个预设并把类型表抖出来（验证"能被读取预设读取"）。</summary>
        public string UiPrePeek(string path)
        {
            string json = PresetIo.ReadJson(path);
            if (json == null) return "(读不出 JSON)";
            Dictionary<string, Rule> plan; int q; bool prot;
            bool hasPlan = PresetIo.TryReadPlan(json, out plan, out q, out prot);
            var sb = new StringBuilder();
            sb.AppendFormat("JSON {0} 字节；plan={1} quality={2} protectAlpha={3}\r\n", json.Length, hasPlan, q, prot);
            foreach (var kv in plan) sb.AppendFormat("  {0} = {1}/{2}\r\n", kv.Key, kv.Value.Format, kv.Value.Size);
            try
            {
                var pf = TexTool.PresetLoad(path);       // 走"读取预设（默认在 preset\\coordinate 里）"的同一条路径
                sb.AppendFormat("PresetLoad OK：卡种={0} 部位规则={1} 单贴图规则={2} 指纹={3} source={4}\r\n",
                    pf.IsChara ? "chara" : "clothes", pf.Parts.Count, pf.Textures.Count, pf.SigSet.Count, pf.Source);
            }
            catch (Exception ex) { sb.AppendLine("PresetLoad 失败：" + ex.Message); }
            var img = PresetIo.Cover(path, 48);
            sb.AppendFormat("卡面={0}", img == null ? "无" : img.Width + "x" + img.Height);
            return sb.ToString();
        }

        public string UiPrePlanSummary
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var kv in BuildPlan()) sb.AppendFormat("{0}={1}/{2} ", kv.Key, kv.Value.Format, kv.Value.Size);
                sb.AppendFormat("q={0}", (int)trkQuality.Value);
                return sb.ToString();
            }
        }

        /// <summary>自检用：第2 页把当前设置存成 preset\coordinate 里的预设。</summary>
        public string UiP2SavePreset(string name)
        {
            if (ps == null) return "(先读卡)";
            var pf = BuildPresetUi();
            ApplyUniformToPreset(pf);
            string card = txtP2Card.Text.Trim();
            string path = Path.Combine(PresetIo.EnsureDir(PresetIo.KindCoord), PresetIo.FileName(name, card));
            string json = PresetIo.Envelope(PresetIo.KindCoord, PresetIo.Sanitize(name), Path.GetFileName(card),
                "clothes", pf.MinMatch, pf.Uniform, pf.SigSet, pf.Parts, pf.Textures, null, (int)numP2Quality.Value, protectAlphaCompat);
            PresetIo.Save(path, json, card);
            return path;
        }

        /// <summary>自检用：第3 页把当前设置存成 preset\chara 里的预设。</summary>
        public string UiG3SavePreset(string name)
        {
            if (ps3 == null) return "(先读卡)";
            var pf = G3BuildPreset();
            string card = g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim();
            string path = Path.Combine(PresetIo.EnsureDir(PresetIo.KindChara), PresetIo.FileName(name, card));
            string json = PresetIo.Envelope(PresetIo.KindChara, PresetIo.Sanitize(name), Path.GetFileName(card),
                "chara", pf.MinMatch, pf.Uniform, pf.SigSet, pf.Parts, pf.Textures, null, (int)numG3Quality.Value, protectAlphaCompat);
            PresetIo.Save(path, json, card);
            return path;
        }

        /// <summary>自检用：把某个目录里的预设列出来（名字/ 卡面 / 原卡）。</summary>
        public static string UiPresetList(string kind, int coverPx)
        {
            var list = PresetIo.List(kind, coverPx);
            var sb = new StringBuilder();
            sb.AppendFormat("{0}：{1} 个\r\n", PresetIo.Dir(kind), list.Count);
            foreach (var e in list)
                sb.AppendFormat("  {0} | 卡面={1} | 原卡={2} | {3}\r\n", e.Name,
                    e.Cover == null ? "无" : (e.Cover.Width + "x" + e.Cover.Height), e.Source, Path.GetFileName(e.Path));
            return sb.ToString();
        }

        // ---- 无头自检钩子（第 1 页组过滤）---
        public string UiGrpDump
        {
            get
            {
                var l = new List<string>();
                foreach (var c in chkGrp) if (c != null) l.Add(c.Text + "=" + (c.Checked ? "1" : "0"));
                return string.Join(" ", l.ToArray()) + "  掩码=" + CharaMask()
                     + "  最小组界面到换" + grpMax;
            }
        }

        /// <summary>自检用：grp=all / grp=body / grp=0,3 ——勾选第 1 页的组过滤（组号越界会被忽略）。</summary>
        public void UiGrpSet(string spec)
        {
            string s = (spec ?? "").Trim().ToLowerInvariant();
            if (s == "all") { CharaMaskAll(true, false); return; }
            if (s == "body") { CharaMaskAll(false, true); return; }
            if (s == "none") { CharaMaskAll(false, false); return; }
            CharaMaskAll(false, false);
            foreach (var tk in s.Split(','))
            {
                int g;
                if (!int.TryParse(tk.Trim(), out g)) continue;
                foreach (var c in chkGrp)
                    if (c != null && c.Tag is int && (int)c.Tag == g) c.Checked = true;
            }
        }

        /// <summary>自检用：展开/收起第 1 页的组过滤那一行（截图核对用）。</summary>
        public void UiGrpShow(bool show) { SetGroupFilterVisible(show); }

        /// <summary>自检用：按指定套数重建组过滤（验证"超过 7 套"的卡）。</summary>
        public string UiGrpRebuild(int maxGroup)
        {
            BuildGrpFilter(maxGroup);
            return UiGrpDump;
        }

        // ---- 贴图缩略图预览（第2、3 页共用）----
        readonly PictureBox picP2 = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(245, 245, 248) };
        readonly LocLabel lblP2Prev = new LocLabel { Dock = DockStyle.Bottom, Height = 34, AutoSize = false, ForeColor = Color.FromArgb(60, 60, 70) };
        readonly PictureBox picG3 = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(245, 245, 248) };
        readonly LocLabel lblG3Prev = new LocLabel { Dock = DockStyle.Bottom, Height = 34, AutoSize = false, ForeColor = Color.FromArgb(60, 60, 70) };
        // v2.27：独立悬浮预览窗（一个就够，两页共用；跟着当前选中的贴图换）
        PreviewForm prevBig;
        readonly Button btnBigP2 = new Button { Text = "⛶ 独立窗口", AutoSize = true };
        readonly Button btnBigG3 = new Button { Text = "⛶ 独立窗口", AutoSize = true };
        /// <summary>上一次真正喂给悬浮窗的图（同 hash 不重复解码）</summary>
        string bigKey = "";
        bool previewBusy;

        // ---- 预览缩略图缓存（v2.25）------------------------------------------------
        // 起因（实测）：读 22MB 衣服卡只要 34ms，但"选中卡"这一步整体要 28 秒 ——
        // 因为填表过程中表格会反复触发 SelectionChanged，每次都把选中的那张贴图**整张解码**一遍
        // （096² 的 PNG 一次 100~300ms，几十次就是几十秒）。这就是"读卡很慢"的真正来源。
        // 两道修：① 网格批量刷新期间不预览，刷新完只预览一次；② 同一张图解码结果缓存复用。
        bool suppressPreview;
        readonly Dictionary<string, Bitmap> thumbCache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        readonly List<string> thumbOrder = new List<string>();

        void CacheThumb(string key, Bitmap bmp)
        {
            Bitmap prev;
            if (thumbCache.TryGetValue(key, out prev) && !ReferenceEquals(prev, bmp))
            { try { prev.Dispose(); } catch { } }             // 换新前先把旧的副本释放掉
            thumbCache[key] = bmp;
            thumbOrder.Remove(key);
            thumbOrder.Add(key);
            while (thumbOrder.Count > 16)                     // 只留最近 16 张（都是缩略图，很小）
            {
                string old = thumbOrder[0];
                thumbOrder.RemoveAt(0);
                Bitmap ob;
                if (thumbCache.TryGetValue(old, out ob)) { thumbCache.Remove(old); try { ob.Dispose(); } catch { } }
            }
        }

        /// <summary>卡换了/重读了 → 这张卡的缩略图缓存作废。</summary>
        void ClearThumbCache()
        {
            foreach (var kv in thumbCache) { try { kv.Value.Dispose(); } catch { } }
            thumbCache.Clear(); thumbOrder.Clear();
        }

        /// <summary>把选中那行的贴图做一张缩略图显示在预览框里。
        /// 取图优先用*已经读在内存里的那份卡字节**（LoadParts/G3Load 刚读的 ps.Raw），
        /// 实在没有才回落到 TexTool 的按路径缓存 ——以前这里必然再整读一遍卡（人物卡 270MB）。</summary>
        public int texSelFired;                        // 自检：明细表 SelectionChanged 触发了几次
        public string UiLastPreview = "(未调用)";      // 自检：上一次 UpdatePreview 走了哪条路
        readonly List<string> uiPrevTrace = new List<string>();   // 自检：最近几次预览调用
        public string UiPreviewTrace { get { return string.Join(" →", uiPrevTrace.ToArray()); } }
        void UpdatePreview(PictureBox pic, Label info, DataGridView grid, string cardPath)
        {
            uiPrevTrace.Add("enter(" + (grid.CurrentRow == null ? "?" : (grid.CurrentRow.Tag as string ?? "?"))
                          + ",sup=" + suppressPreview + ",busy=" + previewBusy + ")");
            while (uiPrevTrace.Count > 8) uiPrevTrace.RemoveAt(0);
            UiLastPreview = "enter(id=" + (grid.CurrentRow == null ? "?" : (grid.CurrentRow.Tag as string ?? "?"))
                          + ",suppress=" + suppressPreview + ",busy=" + previewBusy + ")";
            if (suppressPreview) { UiLastPreview = "suppress"; return; }                 // 网格批量刷新期间不预览
            if (Environment.GetEnvironmentVariable("KOITEX_NOPREVIEW") == "1") { UiLastPreview = "nopreview"; return; }
            if (previewBusy) { UiLastPreview = "busy"; return; }
            previewBusy = true;
            var swPrev = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string id = (grid.CurrentRow != null) ? grid.CurrentRow.Tag as string : null;
                var old = pic.Image;
                pic.Image = null;
                if (old != null) { try { old.Dispose(); } catch { } }
                if (id == null)
                {
                    info.Text = "点上面一行贴图即可预览";
                    if (BigOpen) { bigKey = ""; prevBig.SetImage(null, ""); }
                    return;
                }
                if (string.IsNullOrEmpty(cardPath) || !File.Exists(cardPath)) { UiLastPreview = "noCard"; info.Text = "卡路径无效，无法预览"; if (BigOpen) { bigKey = ""; prevBig.SetImage(null, ""); } return; }
                // ② 同一张图直接复用上次的缩略图（绝大多数重复调用都是同一行）。
                // 但**悬浮窗开着的时候要跳过这条捷径** —— 它要的是整图解出来的大图，
                // 缓存里只有缩略图；此时照常解码（那份大图顺手也喂给悬浮窗，不浪费）。
                string ck = cardPath + "|" + id;
                Bitmap hit;
                if (!BigOpen && thumbCache.TryGetValue(ck, out hit) && hit != null && !hit.Size.IsEmpty)
                {
                    pic.Image = (Bitmap)hit.Clone();
                    info.Text = L.F("TexID {0}   {1}×{2}（缓存）", id, hit.Width, hit.Height);
                    return;
                }
                byte[] cardBytes = (grid == gridTex && ps != null && ps.Raw != null) ? ps.Raw
                                 : (grid == gridGT && ps3 != null && ps3.Raw != null) ? ps3.Raw : null;
                byte[] raw = cardBytes != null ? TexTool.TextureBytes(cardBytes, id) : TexTool.TextureBytes(cardPath, id);
                string err;
                var img = TexTool.LoadTextureImage(raw, out err);
                if (img == null)
                {
                    info.Text = string.Format("TexID {0}：{1}", id, err);
                    UiLastPreview = "decodeFail " + id + "：" + err + "（raw=" + (raw == null ? -1 : raw.Length) + "字节）";
                    if (BigOpen) { bigKey = ""; prevBig.SetImage(null, ""); }   // 别把上一张留在悬浮窗里冒充这一张
                    return;
                }
                // 缩到预览框大小再显示，别把 4096² 原图留在内存里
                int maxW = Math.Max(120, pic.ClientSize.Width - 8), maxH = Math.Max(90, pic.ClientSize.Height - 8);
                double sc = Math.Min(1.0, Math.Min((double)maxW / img.Width, (double)maxH / img.Height));
                int tw = Math.Max(1, (int)Math.Round(img.Width * sc)), th = Math.Max(1, (int)Math.Round(img.Height * sc));
                var small = new Bitmap(tw, th);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, tw, th);
                }
                // v2.27：悬浮窗开着就把**整图**交给它（所有权转移，由它负责释放），
                // 这样切贴图时不多解码一次；否则照旧立即释放。
                string cap = string.Format("TexID {0}   {1}×{2}   {3}   {4}",
                    id, img.Width, img.Height, TexTool.Human(raw.Length), TexTool.FmtName(TexTool.Sniff(raw)));
                if (BigOpen)
                {
                    prevBig.SetImage(img, BigCaption(grid, cardPath, cap));
                    bigKey = ck; UiLastPreview = "big " + id;
                }
                else img.Dispose();
                pic.Image = small;
                // ⚠ 缓存里必须放**独立副本**：上面 old.Dispose() 释放的是 pic.Image，
                // 如果缓存和界面共用同一个Bitmap，下一次预览就会先把它释放掉，
                // 再 Clone 一个已释放的位图 → GDI+ 抛「Parameter is not valid」。
                // （.25 引入缓存时踩过这个坑：同一张贴图预览第二次才炸，看起来像"部分贴图"坏。）
                CacheThumb(ck, (Bitmap)small.Clone());
                if (!BigOpen) UiLastPreview = "thumb " + id;
                if (swPrev.ElapsedMilliseconds > 400) Console.WriteLine(L.F("  [预览耗时] TexID {0} 解码+缩放 {1}ms", id, swPrev.ElapsedMilliseconds));
                info.Text = string.Format("TexID {0}   {1}×{2}   {3}   {4}", id, tw, th,
                    TexTool.Human(raw.Length), TexTool.FmtName(TexTool.Sniff(raw)));
            }
            catch (Exception ex) { info.Text = "预览失败：" + ex.Message; }
            finally { previewBusy = false; }
        }

        /// <summary>「⛶ 独立窗口」按钮：打开（或前置）悬浮预览窗，并立刻把当前选中那张喂进去。</summary>
        void OpenBigPreview(PictureBox pic, DataGridView grid, string cardPath)
        {
            if (prevBig == null || prevBig.IsDisposed)
            {
                prevBig = new PreviewForm { Owner = this };
                L.Apply(prevBig);                    // v1.0：预览窗也要跟着当前语言
                // 摆到主窗口右侧外面一点，别盖住它
                try
                {
                    prevBig.Location = new Point(Bounds.Right + 8, Bounds.Top + 40);
                    var wa = Screen.FromControl(this).WorkingArea;
                    if (prevBig.Right > wa.Right) prevBig.Location = new Point(Math.Max(wa.Left, Bounds.Right - prevBig.Width - 8), Bounds.Top + 40);
                    if (prevBig.Bottom > wa.Bottom) prevBig.Top = Math.Max(wa.Top, wa.Bottom - prevBig.Height);
                }
                catch { }
                prevBig.FormClosed += delegate { bigKey = ""; };
            }
            if (!prevBig.Visible) prevBig.Show(this);
            prevBig.BringToFront();
            bigKey = "";                                    // 强制重新喂一次
            UpdatePreview(pic, pic == picP2 ? lblP2Prev : lblG3Prev, grid, cardPath);
        }

        /// <summary>悬浮窗底部那行说明：比小预览的多带类型共用情况和卡文件名，
        /// 因为独立窗口离开了主界面的表格上下文，光一个 TexID 看不出是哪张。
        /// 尺寸/字节/格式那几项baseCap 里已经有了，这里不重复。</summary>
        string BigCaption(DataGridView grid, string cardPath, string baseCap)
        {
            try
            {
                var row = grid.CurrentRow;
                var sb = new System.Text.StringBuilder(baseCap);
                if (row != null)
                {
                    string cls = CellText(row, "tcls"), shr = CellText(row, "tshare");
                    if (cls.Length > 0) sb.Append("　类型=").Append(cls);
                    if (shr.Length > 0)
                    {
                        if (shr.Length > 44) shr = shr.Substring(0, 44) + "…";
                        sb.Append("　共用：").Append(shr);
                    }
                }
                if (!string.IsNullOrEmpty(cardPath))
                {
                    try { sb.Append("　卡=").Append(System.IO.Path.GetFileName(cardPath)); } catch { }
                }
                return sb.ToString();
            }
            catch { return baseCap; }
        }

        static string CellText(DataGridViewRow row, string col)
        {
            try
            {
                if (!row.DataGridView.Columns.Contains(col)) return "";
                var c = row.Cells[col];
                return c == null || c.Value == null ? "" : c.Value.ToString();
            }
            catch { return ""; }
        }

        /// <summary>悬浮窗是否开着（开着时预览要走整图"那条路）。</summary>
        bool BigOpen { get { return prevBig != null && !prevBig.IsDisposed && prevBig.Visible; } }

        // ---------------------------------------------------------------- 表头点击排序（v2.29）
        //
        // 规则（用户定的，两页一致）：
        //   · 「部位/ 组 · 部位」→ 只按**固定顺序**排：
        //       上衣 · 下衣 · 胸罩 · 内裤 · 手套 · 连裤袜· 袜子 · 鞋(内层) · 鞋(外层) ·
        //       饰品槽0、1、2 …（人物卡先按「组」：角色本体 → 换装1 … 换装7）
        //     这个顺序不是编出来的：衣服槽位编号本来就是0上衣 1下衣 2胸罩 3内裤 4手套
        //     5连裤袜6袜子 7鞋(内层) 8鞋(外层)、饰品 1000+n、本体部件 2000+n，
        //     所以直接拿 SlotKey 当序号就是用户要的顺序。
        //     点它**不反向*，而且"没有贴图的排最后这条对它不生效（该在哪还在哪）。
        //   · 「数量 / 大小 / 独占贴图」→ **第一次点从大到小**，同一列再点反向。
        //     这三列显示的是"14.58 MB"、"1 张 / 2.19 MB" 这种给人看的字符串，字典序是错的
        //     （8.92 MB" 会排到 "18.16 MB" 后面、"11 张  会排到 "3 张  前面），所以按解析出的数字比。
        //   · 这三列里**没有贴图的部位永远排在最后**（不管升序还是降序）。
        //
        // 实现上三件事要一起做对：
        //   ① 这四列改成 Programmatic，点击由 ColumnHeaderMouseClick 自己接管 ——否则
        //      网格默认"第一次点=升序"，跟"第一次点从大到小"冲突。
        //   ② SortCompare 返回的是**升序语义**的比较结果，网格在降序时会自己取反；
        //      只有"无贴图（卡里没有 MaterialEditor TextureDictionary），已原样复制到 "
        //   ③ 排序状态记在 GridSort 里，重填表以后回放（勾选框/换卡重读都会重建整张表）。
        sealed class GridSort
        {
            public string Col;
            public ListSortDirection Dir = ListSortDirection.Descending;   // 数字列默认从大到小
        }
        readonly GridSort st2 = new GridSort(), st3 = new GridSort();
        // 贴图明细表（第 2 页 gridTex / 第 3 页 gridGT）自己的排序状态：只按数值排 TexID/尺寸/大小
        readonly GridSort stTex2 = new GridSort(), stTex3 = new GridSort();
        bool texFilling;                      // FillTexListCore 期间为 true（排序回放时别再刷一次）
        bool sortCurDesc;                     // 本次 Sort 的方向（SortCompare 里要用）

        /// <summary>把这四列设成"自己接管点击"，并挂上比较逻辑。</summary>
        void HookHeaderSort(DataGridView g, GridSort st, Action afterSort)
        {
            foreach (DataGridViewColumn c in g.Columns)
                if (c.Name == "name" || IsNumSortCol(c.Name)) c.SortMode = DataGridViewColumnSortMode.Programmatic;
            g.ColumnHeaderMouseClick += delegate (object s, DataGridViewCellMouseEventArgs e)
            {
                if (e.ColumnIndex < 0 || e.ColumnIndex >= g.Columns.Count) return;
                HeaderSort(g, st, g.Columns[e.ColumnIndex].Name, afterSort);
            };
            g.SortCompare += delegate (object s, DataGridViewSortCompareEventArgs e)
            {
                int r;
                if (e.Column.Name == "name")
                {
                    r = PartOrderOf(g, e.RowIndex1).CompareTo(PartOrderOf(g, e.RowIndex2));
                    if (r == 0) r = string.Compare(e.CellValue1 as string, e.CellValue2 as string, StringComparison.Ordinal);
                }
                else if (IsNumSortCol(e.Column.Name))
                {
                    bool no1 = IsNoTex(e.CellValue1), no2 = IsNoTex(e.CellValue2);
                    if (no1 != no2)
                    {
                        // 「没有贴图的永远在后」：升序时r 直接表达；降序时网格会取反，所以这里先反过来写
                        int after = no1 ? 1 : -1;
                        r = sortCurDesc ? -after : after;
                    }
                    else if (no1) r = PartOrderOf(g, e.RowIndex1).CompareTo(PartOrderOf(g, e.RowIndex2));
                    else
                    {
                        r = SortNum(e.Column.Name, e.CellValue1).CompareTo(SortNum(e.Column.Name, e.CellValue2));
                        if (r == 0) r = PartOrderOf(g, e.RowIndex1).CompareTo(PartOrderOf(g, e.RowIndex2));  // 同值按部位序，顺序才确定
                    }
                }
                else return;                       // 其它列（材质等）保持原来的字符串比法
                e.SortResult = r;
                e.Handled = true;
            };
        }

        /// <summary>点表头（真实点击与自检走同一条路）。clicks=0 表示只报状态不排。</summary>
        void HeaderSort(DataGridView g, GridSort st, string colName, Action afterSort)
        {
            if (string.IsNullOrEmpty(colName) || !g.Columns.Contains(colName)) return;
            if (colName == "name")
            {
                st.Col = "name";
                st.Dir = ListSortDirection.Ascending;              // 固定顺序：不反向
            }
            else if (IsNumSortCol(colName))
            {
                // 第一次点这一列：数字量（大小/张数/尺寸）从大到小，编号类（TexID）从小到大；
                // 同一列再点 → 反向
                st.Dir = (st.Col == colName)
                       ? (st.Dir == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending)
                       : (FirstClickDesc(colName) ? ListSortDirection.Descending : ListSortDirection.Ascending);
                st.Col = colName;
            }
            else return;
            ApplySort(g, st);
            if (afterSort != null) afterSort();
        }

        /// <summary>把排序落到表格上（重填表后回放也走这里）。</summary>
        void ApplySort(DataGridView g, GridSort st)
        {
            if (string.IsNullOrEmpty(st.Col) || !g.Columns.Contains(st.Col)) return;
            sortCurDesc = st.Dir == ListSortDirection.Descending;   // ⚠必须在 Sort 之前设好
            try { g.Sort(g.Columns[st.Col], st.Dir); } catch { }
            // Programmatic 列网格不自己画箭头，得自己给（NotSortable 列不能设，会抛）
            foreach (DataGridViewColumn c in g.Columns)
                if (c.SortMode != DataGridViewColumnSortMode.NotSortable) c.HeaderCell.SortGlyphDirection = SortOrder.None;
            g.Columns[st.Col].HeaderCell.SortGlyphDirection =
                st.Dir == ListSortDirection.Descending ? SortOrder.Descending : SortOrder.Ascending;
        }

        /// <summary>按数值排序的列。
        /// 部位表：数量 / 大小 / 独占贴图；
        /// 贴图明细表（第 2、3 页）：TexID / 尺寸 / 大小（v2.30 加，以前这三列是字典序 → "1024px" 会排在 "512px" 前面）。</summary>
        static bool IsNumSortCol(string n)
        {
            return n == "cnt" || n == "size" || n == "own"
                || n == "tid" || n == "tdim" || n == "tbytes";
        }

        /// <summary>第一次点这一列时，要不要"从大到小"。数字量（大小/张数/尺寸）从大到小；
        /// 编号类（TexID）从小到大 —— 点它多半是想找某个 ID。</summary>
        static bool FirstClickDesc(string n) { return n != "tid"; }

        /// <summary>「没贴图」的行：这三列都显示「—」。</summary>
        static bool IsNoTex(object v)
        {
            string s = v as string;
            return string.IsNullOrEmpty(s) || s == "—";
        }

        /// <summary>固定部位顺序的序号：拿行的Tag（= SlotKey）算。
        /// 衣服槽位 0..8 原样 →正好是「上衣 下衣 胸罩 内裤 手套 连裤袜 袜子 鞋(内层) 鞋(外层)」；
        /// 饰品 1000+n 排到鞋子后面（按 n 升序）；本体部件 2000+n 排最后。
        /// 人物卡的键是 (组1)*100000+部位键 → 先组、后部位。</summary>
        static long PartOrderOf(DataGridView g, int rowIndex)
        {
            try
            {
                if (rowIndex < 0 || rowIndex >= g.Rows.Count) return long.MaxValue;
                object t = g.Rows[rowIndex].Tag;
                if (!(t is int)) return long.MaxValue;
                int key = (int)t;
                if (Chara.IsCharaKey(key))
                    return (long)(key / 100000 - 1) * 100000 + SlotOrder(key % 100000);
                return SlotOrder(key);
            }
            catch { return long.MaxValue; }
        }

        static int SlotOrder(int slotKey)
        {
            if (slotKey >= 2000) return 100000 + (slotKey - 2000);   // 角色本体部件
            if (slotKey >= 1000) return 100 + (slotKey - 1000);      // 饰品槽0、1、2 …
            return slotKey;                                          // 0..8 服装槽
        }

        /// <summary>把表里的显示字符串还原成可比的数字（字节数 / 张数 / 像素边长 / 编号）。</summary>
        static long SortNum(string col, object v)
        {
            string s = v as string;
            if (IsNoTex(s)) return 0;
            if (col == "cnt" || col == "tid")
            {
                int n;
                return int.TryParse(s, out n) ? n : 0;
            }
            if (col == "tdim")
            {
                // 明细表的「尺寸」列形如 "4096px" / "?"；只取数字部分
                int e = s.IndexOf("px", StringComparison.OrdinalIgnoreCase);
                if (e > 0) s = s.Substring(0, e);
                double px;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out px) ? (long)Math.Round(px) : 0;
            }
            if (col == "own")
            {
                // 形如 "1 张/ 2.19 MB"：要的是**独占的那部分字节数**（这才是"按大小排"的意思）
                int k = s.LastIndexOf('/');
                if (k < 0) return 0;
                s = s.Substring(k + 1);
            }
            s = s.Trim();
            int sp = s.IndexOf(' ');
            double d;
            string unit = "B";
            if (sp < 0)
            {
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
            }
            else
            {
                if (!double.TryParse(s.Substring(0, sp), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
                unit = s.Substring(sp + 1).Trim().ToUpperInvariant();
            }
            double mul = unit == "KB" ? 1024.0 : unit == "MB" ? 1048576.0 : unit == "GB" ? 1073741824.0 : 1.0;
            return (long)Math.Round(d * mul);
        }

        /// <summary>日志开关：按下显示、再按隐藏。</summary>
        void ToggleLog(int which) { SetLogVisible(which, !LogBtn(which).Checked); }

        CheckBox LogBtn(int which) { return which == 1 ? btnLog1 : (which == 2 ? btnLog2 : btnLog3); }

        void SetLogVisible(int which, bool show)
        {
            var t = which == 1 ? rootT1 : (which == 2 ? rootT2 : rootT3);
            var w = which == 1 ? logWrap1 : (which == 2 ? logWrap2 : logWrap3);
            var sp = which == 1 ? spLog1 : (which == 2 ? spLog2 : spLog3);
            var b = LogBtn(which);
            syncingLogBtn = true;
            b.Checked = show;
            b.Text = show ? L.T("隐藏日志") : L.T("显示日志");   // v1.0：运行时赋值也要跟着语言
            syncingLogBtn = false;
            if (t == null) return;
            int last = t.ColumnStyles.Count - 1;                   // 日志永远是最后一列
            t.ColumnStyles[last].SizeType = SizeType.Absolute;
            t.ColumnStyles[last].Width = show ? LogWidth : 0;
            if (w != null) w.Visible = show;
            if (sp != null) sp.Visible = show;                     // 收起时连分隔条一起收
        }

        void LogBtnClicked(int which)
        {
            if (syncingLogBtn) return;
            SetLogVisible(which, LogBtn(which).Checked);
        }

        /// <summary>造一条可拖动的竖直分隔条。splitterCol = 这条分隔条自己占的列，
        /// leftCol/rightCol = 它左边/右边那两个**内容**列（拖动就是改这两列）。
        /// 两边都是 Percent 时按比例分；有一边是 Absolute（固定宽）就改那一边。</summary>
        Panel MakeSplitter(TableLayoutPanel t, int splitterCol, int leftCol, int rightCol, bool ratio)
        {
            var sp = new Panel
            {
                Dock = DockStyle.Fill, Margin = new Padding(0),
                BackColor = Color.FromArgb(214, 219, 226), Cursor = Cursors.VSplit
            };
            int startX = 0, startL = 0, startR = 0;
            // 起始宽度必须**在按下时量一次**，之后整段拖动都基于它 + 位移；
            // 如果每次 MouseMove 都重新量当前宽度再位移，就会越拖越飞（累积误差）。
            splitBegin[sp] = delegate
            {
                startX = Cursor.Position.X;
                startL = ColStylePx(t, leftCol);
                startR = ColStylePx(t, rightCol);
            };
            splitDrag[sp] = delegate (int dx)
            {
                bool lFix = t.ColumnStyles[leftCol].SizeType == SizeType.Absolute;
                bool rFix = t.ColumnStyles[rightCol].SizeType == SizeType.Absolute;
                if (ratio || (!lFix && !rFix))
                {
                    int sum = startL + startR;
                    if (sum < 120) return;
                    int nl = Math.Max(80, Math.Min(startL + dx, sum - 80));
                    double pct = 100.0 * nl / sum;
                    t.ColumnStyles[leftCol].SizeType = SizeType.Percent;
                    t.ColumnStyles[leftCol].Width = (float)pct;
                    t.ColumnStyles[rightCol].SizeType = SizeType.Percent;
                    t.ColumnStyles[rightCol].Width = (float)(100.0 - pct);
                }
                else if (lFix) t.ColumnStyles[leftCol].Width = Math.Max(160, startL + dx);
                else t.ColumnStyles[rightCol].Width = Math.Max(140, startR - dx);
            };
            sp.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                splitBegin[sp]();
                sp.Capture = true;
            };
            sp.MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!sp.Capture) return;
                splitDrag[sp](Cursor.Position.X - startX);
            };
            sp.MouseUp += delegate { sp.Capture = false; };
            t.Controls.Add(sp, splitterCol, 0);
            return sp;
        }

        /// <summary>日志分隔条：包一层，把用户拖出来的宽度记进 logPref，
        /// 这样之后窗口放大时"多出来的宽度给日志"是从**你拖到的那一框**继续长，不会被弹回去。
        /// 顺便支持双击恢复默认宽度。</summary>
        void HookLogSplitter(Panel sp, TableLayoutPanel t, int logCol)
        {
            if (sp == null || t == null) return;
            System.Action<int> orig;
            if (splitDrag.TryGetValue(sp, out orig))
            {
                splitDrag[sp] = delegate (int dx)
                {
                    orig(dx);
                    int w = ColPixels(t, logCol);
                    foreach (var lc in logCols) if (lc.T == t) lc.LogPref = Math.Max(LogMin, w);
                };
            }
            sp.DoubleClick += delegate
            {
                var st = t.ColumnStyles[logCol];
                if (st.SizeType == SizeType.Absolute && st.Width <= 1) return;   // 日志收起了，别展开
                foreach (var lc in logCols) if (lc.T == t) lc.LogPref = LogWidth;
                st.SizeType = SizeType.Absolute;
                st.Width = LogWidth;
            };
            L.Tip(sp, "拖动 = 调整日志宽度（双击回到默认）。");
        }

        /// <summary>上下拖动的分隔条（用来调整预览框占的高度）。topRow 是表格行，bottomRow 是预览行，
        /// 都是绝对高度；拖动时按位移同时改两行，并把底部行限制在60~整个面板高度-120 之间。</summary>
        Panel MakeRowSplitter(TableLayoutPanel t, int splitterRow, int topRow, int bottomRow, int minTop, int minBottom)
        {
            var sp = new Panel
            {
                Dock = DockStyle.Fill, Margin = new Padding(0),
                BackColor = Color.FromArgb(214, 219, 226), Cursor = Cursors.HSplit
            };
            int startY = 0, startTop = 0, startBottom = 0;
            splitBegin[sp] = delegate
            {
                startY = Cursor.Position.Y;
                startTop = RowPixels(t, topRow);
                startBottom = RowPixels(t, bottomRow);
            };
            splitDrag[sp] = delegate (int dy)
            {
                int sum = startTop + startBottom;
                if (sum < 120) return;
                int nb = Math.Max(minBottom, Math.Min(startBottom - dy, sum - minTop));   // 往下拖 = 预览变矮
                t.RowStyles[bottomRow].SizeType = SizeType.Absolute;
                t.RowStyles[bottomRow].Height = nb;
                t.RowStyles[topRow].SizeType = SizeType.Absolute;
                t.RowStyles[topRow].Height = Math.Max(minTop, sum - nb);
            };
            sp.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                splitBegin[sp]();
                sp.Capture = true;
            };
            sp.MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!sp.Capture) return;
                splitDrag[sp](Cursor.Position.Y - startY);
            };
            sp.MouseUp += delegate { sp.Capture = false; };
            t.Controls.Add(sp, 0, splitterRow);
            return sp;
        }

        static int RowPixels(TableLayoutPanel t, int row)
        {
            var c = t.GetControlFromPosition(0, row);
            if (c != null) return c.Height;
            if (row >= 0 && row < t.RowStyles.Count) return (int)t.RowStyles[row].Height;
            return 0;
        }

        static int ColPixels(TableLayoutPanel t, int col)
        {
            var c = t.GetControlFromPosition(col, 0);
            return c == null ? 0 : c.Width;
        }

        // ================= 日志列："放大窗口时优先把多出来的宽度给日志" =================
        // 背景：日志列原来固定 320~340px，窗口一放大，多出来的宽度全被中间的操作区吃掉，
        // 日志还是那么窄。而操作区其实只需要"刚好把所有按钮放下"的宽度。
        //
        // 做法：每页记两个数 —…
        //   logPref   用户拖出来的日志宽度（拖动时更新；默认 LogWidth）
        //   mainWant  左边那块**至少**要留多少（不够时先压日志，压到 minLog 为止）
        // 窗口一变就重算：日志宽 = 总宽 - mainWant，但不小于 logPref、不超过 总宽 - minMain。
        // 这样"放大 → 全部给日志；缩小 → 先拿日志的、日志到下限了再压主区"。
        sealed class LogCol
        {
            public TableLayoutPanel T;
            public int LogIdx;
            public int[] MainCols;    // 主区那几列（窗口变大时**不动它们**，多出来的宽度给日志）
            public int MainWant;      // 首次布局时主区实际占的宽度 = "够用宽度"
            public int MinMain;       // 主区硬下限（再小主要按钮就摆不下了）
            public int LogPref;       // 用户拖出来的日志宽（拖动会更新）
            public bool Applied;
        }
        readonly List<LogCol> logCols = new List<LogCol>();
        bool layingOut;
        bool logArmed;            // 窗体显示之前不测量主区宽度（那时布局还没定，量出来的值会偏小）

        /// <summary>登记一页的"日志列优先级"规则：窗口变大时，多出来的宽度**优先给日志列**，
        /// 主区保持它首次布局时的宽度（那时所有主要按钮都显示得下）。
        /// 窗口变小时反过来：先压日志（到 LogMin 为止），再压主区（到 MinMain 为止）。</summary>
        void RegisterLogCol(TableLayoutPanel t, int logCol, int minMain, params int[] mainCols)
        {
            if (t == null) return;
            var lc = new LogCol { T = t, LogIdx = logCol, MainCols = mainCols, MinMain = minMain, LogPref = LogWidth };
            logCols.Add(lc);
            t.SizeChanged += delegate { ApplyLogCol(lc); };
        }

        int MainNow(LogCol lc)
        {
            int s = 0;
            foreach (var c in lc.MainCols) s += ColPixels(lc.T, c);
            return s;
        }

        void ApplyLogCol(LogCol lc)
        {
            var t = lc.T;
            if (t == null || layingOut || lc.LogIdx >= t.ColumnStyles.Count) return;
            // 首次布局期间布局还没稳定（主区可能只有几百像素宽），这时量出来的"够用宽度"会明显偏小，
            // 结果就是日志列一上来就把宽度抢光了。所以**等窗体显示之后**才开始接管。
            if (!logArmed) return;
            var st = t.ColumnStyles[lc.LogIdx];
            if (st.SizeType == SizeType.Absolute && st.Width <= 1) return;        // 日志被折叠了，别动

            if (!lc.Applied)
            {
                int cur = MainNow(lc);
                if (cur <= 0) return;
                lc.MainWant = Math.Max(lc.MinMain, cur); lc.Applied = true;
                return;                                                          // 只记录，不改布局
            }

            int used = 0;                                                        // 除主区 日志以外的固定列（分隔条）
            for (int i = 0; i < t.ColumnStyles.Count; i++)
            {
                bool isMain = false;
                foreach (var c in lc.MainCols) if (c == i) { isMain = true; break; }
                if (isMain || i == lc.LogIdx) continue;
                used += ColPixels(t, i);
            }
            int avail = t.ClientSize.Width - used;
            if (avail < 240) return;

            int log = Math.Max(lc.LogPref, avail - lc.MainWant);                 // 放大：多出来的都给日志
            int hardCap = Math.Max(LogMin, avail - lc.MinMain);                  // 主区不能小于硬下限
            if (log > hardCap) log = hardCap;
            if (log < LogMin) log = LogMin;
            if (Math.Abs(ColPixels(t, lc.LogIdx) - log) < 2) return;

            layingOut = true;
            try { st.SizeType = SizeType.Absolute; st.Width = log; }
            finally { layingOut = false; }
        }

        /// <summary>拖动的基准宽度：列是 Absolute 时用**列宽**（拖动改的就是它），
        /// 否则退回控件宽度。混用这两个数会让每次拖动都偏掉一个 Margin（实测偏 5~9px）。</summary>
        static int ColStylePx(TableLayoutPanel t, int col)
        {
            if (col >= 0 && col < t.ColumnStyles.Count && t.ColumnStyles[col].SizeType == SizeType.Absolute)
                return (int)t.ColumnStyles[col].Width;
            return ColPixels(t, col);
        }

        public string UiLogVisible
        {
            get
            {
                return string.Format("log1={0} log2={1} w1={2} w2={3}", btnLog1.Checked, btnLog2.Checked,
                    rootT1 != null ? (int)rootT1.ColumnStyles[rootT1.ColumnStyles.Count - 1].Width : -1,
                    rootT2 != null ? (int)rootT2.ColumnStyles[rootT2.ColumnStyles.Count - 1].Width : -1);
            }
        }
        public void UiToggleLog(int which) { ToggleLog(which); }
        /// <summary>模拟真实点按钮（自检用）：和用户点击一样先把Checked 翻过去，再走 Click 处理。</summary>
        public void UiClickLogButton(int which)
        {
            var b = which == 1 ? btnLog1 : btnLog2;
            b.Checked = !b.Checked;
            LogBtnClicked(which);
        }
        /// <summary>布局自检：各块坐标尺寸+ 分隔条，确认重排/拖动真的生效。</summary>
        public string UiLayoutDump
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendFormat("tab1[{0}x{1}] 日志={2} 日志列宽={3} | ", rootT1.Width, rootT1.Height, logWrap1.Bounds,
                    (int)rootT1.ColumnStyles[rootT1.ColumnStyles.Count - 1].Width);
                sb.AppendFormat("tab2 左列={0} 部位表={1} 明细={2} 日志={3} 列宽=[{4},{5},{6}]",
                    scLeftDump(), gridParts.Parent.Bounds, gridTex.Parent.Bounds, logWrap2.Bounds,
                    (int)rootT2.ColumnStyles[0].Width, (int)rootT2.ColumnStyles[2].Width, (int)rootT2.ColumnStyles[rootT2.ColumnStyles.Count - 1].Width);
                return sb.ToString();
            }
        }
        Control leftColCtl;
        string scLeftDump() { return leftColCtl == null ? "-" : leftColCtl.Bounds.ToString(); }
        readonly Dictionary<Panel, Action<int>> splitDrag = new Dictionary<Panel, Action<int>>();
        readonly Dictionary<Panel, Action> splitBegin = new Dictionary<Panel, Action>();
        Panel spTab2Left, spTab2Mid, spPrev2, spPrev3;
        GroupBox gbIn2, gbOut2, gbIn3, gbOut3;
        TableLayoutPanel left2, left3, actRow2, actRow3;
        FlowLayoutPanel pUni1;                       // 统一设置那一行（v2.23 起放进全局选项块的最上面）
        /// <summary>文字截断自检：列出所有「显示宽度< 文字需要宽度」的控件 —— 界面上看起来就像"多了个括号"。</summary>
        public string UiClippedText()
        {
            var sb = new StringBuilder();
            CheckClipped(sb, this);
            return sb.Length == 0 ? "（没有文字被截断）" : sb.ToString().TrimEnd();
        }

        /// <summary>自检用：把整个窗口画成PNG（画图到文件），用来人眼复核布局。</summary>
        public string UiSnap(string path, int tab)
        {
            if (tab >= 0) UiSelectTab(tab);
            Application.DoEvents();
            using (var bmp = new Bitmap(Math.Max(200, Width), Math.Max(200, Height)))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return string.Format("{0}  {1}x{2}", path, Width, Height);
        }

        /// <summary>自检用：把某个标签页的控件树（名字位置/尺寸/文字）抖出来。</summary>
        public string UiRectDump(int tab)
        {
            var pages = UiTabPageList();
            if (tab < 0 || tab >= pages.Count) return "(页号越界)";
            var sb = new StringBuilder();
            DumpRects(sb, pages[tab], 0);
            return sb.ToString();
        }

        static void DumpRects(StringBuilder sb, Control c, int depth)
        {
            string ind = new string(' ', depth * 2);
            sb.AppendFormat("{0}{1} @{2},{3} {4}x{5} 需要{6}x{7} dock={8}{9}\r\n", ind, c.GetType().Name, c.Left, c.Top, c.Width, c.Height,
                c.PreferredSize.Width, c.PreferredSize.Height,
                c.Dock, string.IsNullOrEmpty(c.Text) ? "" : " 「" + c.Text.Replace("\r\n", " ").Replace("\n", " ") + "」");
            if (depth >= 8) return;
            foreach (Control k in c.Controls) DumpRects(sb, k, depth + 1);
        }

        /// <summary>读某条分隔条右边的列宽（自检用）：==第1页日志 1/2/3 同 UiDragSplitter。</summary>
        public float UiLogWidth(int which)
        {
            var t = which == 0 ? rootT1 : rootT2;
            if (t == null) return -1;
            int col = which == 0 ? t.ColumnStyles.Count - 1 : (which == 1 ? 0 : (which == 2 ? 2 : t.ColumnStyles.Count - 1));
            return t.ColumnStyles[col].Width;
        }

        /// <summary>拖动"上下"分隔条（自检用）：which 2=第页预览 3=第3页预览。返回预览框高度。</summary>
        public int UiDragRowSplitter(int which, int dy)
        {
            Panel sp = which == 2 ? spPrev2 : spPrev3;
            if (sp == null) return -1;
            Action<int> f; Action beg;
            if (splitDrag.TryGetValue(sp, out f))
            {
                if (splitBegin.TryGetValue(sp, out beg)) beg();
                f(dy);
            }
            var box = sp.Parent as TableLayoutPanel;
            if (box == null) return -1;
            return (int)box.RowStyles[box.RowStyles.Count - 1].Height;
        }

        /// <summary>当前预览框高度（自检用）。</summary>
        public int UiPreviewHeight(int which)
        {
            var p = which == 2 ? picP2 : picG3;
            return p == null || p.Parent == null ? -1 : p.Parent.Height;
        }

        static void CheckClipped(StringBuilder sb, Control root)
        {
            // v1.0：跳过**当前没显示的页** —— 那些页从没布局过（高度 0），
            // 会把「同名控件在另一页」误报成被切掉（实测「显示放大选项」两页各一个，报的是没打开的那页）。
            var owner = root as TabControl;
            foreach (Control c in root.Controls)
            {
                if (owner != null && c is TabPage && owner.SelectedTab != c) continue;
                // 用控件自己的 PreferredSize 判（它已包含内边距），比自己 MeasureText 再加余量准；
                // 放宽 2px 容忍字体测量误差
                string t = c.Text;
                // AutoSize=false 的 Label 是**故意让它自动换行**的（状态条、预览提示），不该按单行宽度判截断
                bool wraps = (c is Label) && !c.AutoSize;
                if (!wraps && !string.IsNullOrEmpty(t) && t.Length > 1 && c.Width > 0 && c.Visible
                    && (c is Label || c is CheckBox || c is RadioButton || c is Button || c is GroupBox))
                {
                    // GroupBox 的 PreferredSize 不可信（会返回当前尺寸），它的"文字"其实只有标题 → 单独量标题
                    int need = (c is GroupBox)
                        ? TextRenderer.MeasureText(t, c.Font).Width + 14
                        : c.PreferredSize.Width;
                    if (need > c.Width + 2)
                        sb.AppendFormat("  [截断] {0} 需要 {1} > 实际 {2} AutoSize={4} Parent={5} : {3}\r\n", c.GetType().Name, need, c.Width, t, c.AutoSize, c.Parent == null ? "null" : c.Parent.GetType().Name);
                    // 自身宽度够，但**被父容器切掉**（流式布局 + AutoSize 的经典坑）：这种在界面上
                    // 看起来就是文字少了半截（或多了个括号）"，光比 PreferredSize 是查不出来的
                    var par = c.Parent;
                    if (par != null && c.Width > 0)
                    {
                        int visW = par.ClientSize.Width - c.Left, visH = par.ClientSize.Height - c.Top;
                        bool scrolls = par is ScrollableControl && ((ScrollableControl)par).AutoScroll;
                        if (!scrolls && (visW < c.Width - 2 || visH < c.Height - 2))
                            sb.AppendFormat("  [切掉] {0} 在 {1} 里放不下：需要 {2}x{3}，可见 {4}x{5} —— {6}\r\n",
                                c.GetType().Name, par.GetType().Name, c.Width, c.Height, Math.Max(0, visW), Math.Max(0, visH), t);
                    }
                }
                if (c.HasChildren) CheckClipped(sb, c);
            }
        }

        /// <summary>拖动分隔条（自检用，走与鼠标拖动同一条逻辑）：which 0=第页日志 1=左|部位表 2=部位表|明细 3=明细|日志。</summary>
        public void UiDragSplitter(int which, int dx)
        {
            Panel p = which == 0 ? spLog1 : (which == 1 ? spTab2Left : (which == 2 ? spTab2Mid : spLog2));
            Action<int> f; Action beg;
            if (p != null && splitDrag.TryGetValue(p, out f))
            {
                if (splitBegin.TryGetValue(p, out beg)) beg();
                f(dx);
            }
        }
        /// <summary>连续拖动（自检用）：按 d1,d2,d3…逐步位移，模拟真实鼠标一路拖过去。</summary>
        public void UiDragSteps(int which, params int[] steps)
        {
            Panel p = which == 0 ? spLog1 : (which == 1 ? spTab2Left : (which == 2 ? spTab2Mid : spLog2));
            Action<int> f; Action beg;
            if (p == null || !splitDrag.TryGetValue(p, out f)) return;
            if (splitBegin.TryGetValue(p, out beg)) beg();
            foreach (var d in steps) f(d);
        }

        // ---- 「按部位压缩」分页----
        readonly ThemedTabControl tabs = new ThemedTabControl { Dock = DockStyle.Fill };
        readonly TextBox txtP2Card = new TextBox();
        readonly TextBox txtP2Out = new TextBox();
        readonly TextBox txtP2Suffix = new TextBox();
        readonly NumericUpDown numP2Quality = new NumericUpDown { Minimum = 50, Maximum = 100, Value = 90, Width = 60 };
        // v2.19：第 2 页原「保护 alpha」复选框已删除（alpha 保护改成内建判据，见 TexTool.alphaOk）。
        readonly CheckBox chkP2Overwrite = new CheckBox { Text = "覆盖同名", AutoSize = true };
        // 通用设置：**只用于不匹配的服装**（勾了下面那个勾时），不参与同款卡的部位规则
        // v2.21：cbUniformFmt / cbUniformSize / chkForce（「通用设置（不匹配的服装用）」）已删除。
        // 不匹配的服装现在默认**既不压缩也不复制**（跳过），要复制就勾下面这个。
        // v2.20：不匹配的卡不再只能"跳过"——也可以原样复制到输出目录，让输出目录保持完整
        readonly CheckBox chkCopySkip = new CheckBox { Text = "不匹配的服装直接复制到输出目录（不压缩）", AutoSize = true };
        // v2.22：默认只压这一件；勾上才把设置套到同目录下的同款服装上
        readonly CheckBox chkAllSimilar = new CheckBox { Text = "将压缩应用到目录下的所有相似服装", AutoSize = true };
        CheckBox chkCopySkip1 = new CheckBox { AutoSize = true };   // 第1 页（并进「覆盖同名」那一行）
        CheckBox chkCopySkip3 = new CheckBox { AutoSize = true };   // 第 3 页
        bool g3Loading;                   // 第3 页后台读取中
        bool syncingCopy;
        bool syncGlobals;                 // SyncGlobalControls 期间挡住滑条/数字框的回环
        // v2.21：第 2/3 页的「细节图保细节 / 独占贴图保护」（与第 1 页那两项是同一批设置）
        // v2.32：彩色贴图的降采样核（面积平均 vs GDI+ 双三次）。和「贴图细节保护」一样，
        // 三个页面各有一份勾选框，共用 Resample.ColorMode 这一个静态设置。
        const string ColorKernelText = "彩色贴图也走面积平均";
        const string ColorKernelTip =
            "彩色贴图（主贴图/发光/反射/材质球等）降分辨率时也用面积平均，而不是 GDI+ 双三次。\n" +
            "2.32 全语料实测（14 张卡，1024/auto/90，唯一变量就是这个开关）：\n" +
            "  被改的 15 张贴图 SSIM 全部上升（均值 +0.0033）、振铃/过冲下降；\n" +
            "  这 15 张体积合计 −4.1%，整卡合计 −0.30%，耗时 28s → 25s；\n" +
            "  其余 196 张贴图逐字节不变（含全部法线/掩罩/高度）。\n" +
            "这条路径同时启用按覆盖率加权的预乘 alpha 平均 —— 透明区不会把垃圾色混进边缘。\n" +
            "不勾 = 彩色贴图退回 GDI+ 双三次（2.31 及以前的行为）。\n" +
            "「贴图细节保护」不勾时这项无意义（那时所有贴图都走 GDI+ 双三次）。";
        readonly CheckBox chkColor2 = new CheckBox { Text = ColorKernelText, AutoSize = true };
        readonly CheckBox chkKernel2 = new CheckBox { Text = "贴图细节保护", AutoSize = true };
        readonly CheckBox chkOwn2 = new CheckBox { Text = "独占贴图保护", AutoSize = true };
        readonly ComboBox cbOwnMax2 = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckBox chkColor3 = new CheckBox { Text = ColorKernelText, AutoSize = true };
        readonly CheckBox chkKernel3 = new CheckBox { Text = "贴图细节保护", AutoSize = true };
        readonly CheckBox chkOwn3 = new CheckBox { Text = "独占贴图保护", AutoSize = true };
        readonly ComboBox cbOwnMax3 = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckBox chkSub = new CheckBox { Text = "批量含子文件夹", Checked = true, AutoSize = true };
        readonly DataGridView gridParts = new DataGridView();
        readonly DataGridView gridTex = new DataGridView();
        readonly Dictionary<string, Rule> texOv = new Dictionary<string, Rule>();   // 单张贴图覆盖规则（键=TexID 文本）
        // 样式与第 1 页「▶ 开始压缩」完全一致（在 BuildPartsPage 里用 MakeMainButton 统一设）
        readonly Button btnP2Run = new Button { AutoSize = true };
        readonly Button btnP2Cancel = new Button { Text = "取消", Enabled = false, AutoSize = true };
        readonly LocLabel lblP2Status = new LocLabel { Text = "先选一张衣服卡（或把卡拖到这一页），点「读取部位」。", AutoSize = true };
        readonly RichTextBox logP2 = new RichTextBox
        {
            ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.FromArgb(250, 250, 252),
            Font = new Font("Consolas", 8.5f), WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical
        };
        TexTool.PartsScan ps;                                     // 当前读出来的部位数据（第 2 页）

        // ---- 第3 页：人物卡压缩（角色本体 + 换装）----
        readonly TextBox txtG3Card = new TextBox();
        readonly TextBox txtG3Out = new TextBox();
        readonly TextBox txtG3Suffix = new TextBox();
        readonly NumericUpDown numG3Quality = new NumericUpDown { Minimum = 50, Maximum = 100, Value = 90, Width = 60 };
        // v2.19：第 3 页原「保护 alpha」复选框已删除（alpha 保护改成内建判据，见 TexTool.alphaOk）。
        readonly CheckBox chkG3Overwrite = new CheckBox { Text = "覆盖同名", AutoSize = true };
        readonly CheckBox chkG3Sub = new CheckBox { Text = "拖目录时含子文件夹", Checked = true, AutoSize = true };
        readonly ComboBox cbG3Filter = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly ComboBox cbG3UniScope = new ThemedCombo();     // 一键统一的作用范围（全部组本体/换装N/当前组）
        readonly ComboBox cbG3UniFmt = new ThemedCombo();
        readonly ComboBox cbG3UniSize = new ThemedCombo();
        readonly ComboBox cbG3UniSh = new ThemedCombo();        // v2.20：一键统一的锐化强度
        readonly ComboBox cbG3UniShm = new ThemedCombo();       // v2.22：一键统一的锐化算法
        readonly ComboBox cbG3UniGa = new ThemedCombo();        // v2.20：一键统一的灰度 2 通道
        // v2.20：第 3 页的「通用设置」（放在一键统一下面）——和批量页那几行是同一批设置
        readonly NumericUpDown numG3Sharpen = new NumericUpDown { Minimum = 0, Maximum = 500, Value = 100, Width = 60 };
        readonly ComboBox cbG3SharpMode = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 108 };
        readonly CheckBox chkG3GrayA = new CheckBox { AutoSize = true };
        readonly CheckBox chkG3Kernel = new CheckBox { AutoSize = true };
        readonly CheckBox chkG3Own = new CheckBox { AutoSize = true };
        Label bUniTip;
        readonly DataGridView gridG = new DataGridView();          // 组·部位
        readonly DataGridView gridGT = new DataGridView();         // 选中组·部位的贴图
        readonly Dictionary<string, Rule> texOv3 = new Dictionary<string, Rule>();
        // 样式与第 1 页「▶ 开始压缩」完全一致（在 BuildCharaPage 里用 MakeMainButton 统一设）
        readonly Button btnG3Run = new Button { AutoSize = true };
        readonly Button btnG3Cancel = new Button { Text = "取消", Enabled = false, AutoSize = true };
        readonly LocLabel lblG3Status = new LocLabel { Text = "先选一张或多张人物卡（Koikatu_F_*.png），点「读取」。", AutoSize = true };
        readonly RichTextBox logG3 = new RichTextBox
        {
            ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.FromArgb(250, 250, 252),
            Font = new Font("Consolas", 8.5f), WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical
        };
        TexTool.PartsScan ps3;                                    // 人物卡扫描结果
        bool g3Filling;                                           // 防止 DataGridView 重入（清行→事件→再清行 →栈溢出）
        readonly List<string> g3Cards = new List<string>();       // 本次要处理的人物卡（可多张）
        readonly LocLabel lblDrop = new LocLabel
        {
            Text = "把衣服卡（.png）或文件夹直接拖到这里读取　—　也可以拖到 EXE 图标上，或点「浏览…」",
            Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(232, 242, 255), ForeColor = Color.FromArgb(20, 70, 140),
            BorderStyle = BorderStyle.FixedSingle
        };

        public MainForm() : this(null) { }

        public MainForm(string[] startup)
        {
            Text = "Koikatsu 衣服卡贴图压缩工具（EXE 版）";
            ClientSize = new Size(1360, 942);
            MinimumSize = new Size(1080, 800);
            Font = new Font("Microsoft YaHei UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;

            // ---- 拖入支持 ----
            AllowDrop = true;
            DragEnter += OnDragEnterAny;
            DragDrop += OnDragDropAny;
            lblDrop.AllowDrop = true;
            lblDrop.DragEnter += OnDragEnterAny;
            lblDrop.DragDrop += OnDragDropAny;
            txtIn.AllowDrop = true;
            txtIn.DragEnter += OnDragEnterAny;
            txtIn.DragDrop += OnDragDropAny;
            log.AllowDrop = true;
            log.DragEnter += OnDragEnterAny;
            log.DragDrop += OnDragDropAny;

            // 主布局：3 列 = [0]操作区  [1]分隔条  [2]日志整块（按钮可折叠、拖动可改宽）
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 8, Padding = new Padding(4) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitW));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogWidth));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 输入/输出
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 类型表（吸收多余高度，别让底部留白）
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 质量
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 预设（卡面下拉框 + 保存 / 打开目录）
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 统一设置（单独一行）
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 按钮
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));// 人物卡组过滤
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 进度/状态

            // ---- 输入 / 输出 ----
            gb1 = new GroupBox { Text = "输入 / 输出", Dock = DockStyle.Top, AutoSize = false };
            var t1 = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
            t1Ref = t1;
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // GrowAndShrink 必须有：默认 GrowOnly 时面板不会长到"内容需要的宽度"，
            // 英文文案比中文宽，单选按钮就被切掉一截（CLIP 检查实测 112 > 104）。
            var pMode = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            // 英文比中文宽：显式打开 AutoSize，否则宽度停在中文时的值（实测 112 需要 / 104 实际）
            rbFile.AutoSize = true; rbDir.AutoSize = true;
            pMode.Controls.Add(rbFile); pMode.Controls.Add(rbDir);
            t1.Controls.Add(pMode, 0, 0); t1.SetColumnSpan(pMode, 2);
            t1.Controls.Add(new Label { Text = "（批量时每个 .png 都会处理，没贴图的自动跳过）", ForeColor = Color.Gray, AutoSize = true }, 2, 0);
            t1.Controls.Add(new Label { Text = "路径", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            txtIn.Dock = DockStyle.Fill; t1.Controls.Add(txtIn, 1, 1);
            var bIn = new Button { Text = "浏览…", AutoSize = true };
            bIn.Click += delegate { PickInput(); };
            t1.Controls.Add(bIn, 2, 1);
            t1.Controls.Add(new Label { Text = "输出目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            txtOut.Dock = DockStyle.Fill; t1.Controls.Add(txtOut, 1, 2);
            var bOut = new Button { Text = "浏览…", AutoSize = true };
            bOut.Click += delegate
            {
                var d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) txtOut.Text = d.SelectedPath;
            };
            t1.Controls.Add(bOut, 2, 2);
            var pSuf = new FlowLayoutPanel { AutoSize = true , };
            pSuf.Controls.Add(new Label { Text = "文件名后缀", AutoSize = true, Anchor = AnchorStyles.Left });
            txtSuffix.Text = "[zip]"; txtSuffix.Width = 100;
            pSuf.Controls.Add(txtSuffix); pSuf.Controls.Add(chkRecurse); pSuf.Controls.Add(chkOverwrite);
            // v2.23：这一项并到「覆盖同名」后面，不再单独占一行；文案也按用户要求改过
            chkCopySkip1.Text = "无法处理目标一同不压缩并复制进输出目录";
            chkCopySkip1.Checked = SkipCopy.On;
            chkCopySkip1.CheckedChanged += delegate { if (!syncingCopy) SetCopySkip(chkCopySkip1.Checked); };
            L.Tip(chkCopySkip1, "勾上 = 处理不了的卡原样复制到输出目录；不勾 = 直接跳过。复制过去的不会被压缩。");
            pSuf.Controls.Add(chkCopySkip1);
            t1.Controls.Add(pSuf, 1, 3); t1.SetColumnSpan(pSuf, 3);
            gb1.Controls.Add(t1);
            root.Controls.Add(gb1, 0, 0);

            // ---- 类型表----
            var gb2 = new GroupBox { Text = "贴图类型：分别设置 处理方式 与 最大边", Dock = DockStyle.Fill, AutoSize = false };
            var t2 = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 5, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));      // v2.33：放大
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t2.Controls.Add(new Label { Text = "贴图类型", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            t2.Controls.Add(new Label { Text = "处理方式（格式）", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 1, 0);
            t2.Controls.Add(new Label { Text = "最大边", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 2, 0);
            var lbUpHead = new Label { Text = "放大", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            t2.Controls.Add(lbUpHead, 3, 0);
            t2.Controls.Add(new Label { Text = "扫描结果", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 4, 0);
            upUiCtrls.Add(lbUpHead);
            int row = 1;
            foreach (var cls in Classes.Order)
            {
                var cb = new CheckBox { Text = Classes.Label(cls), AutoSize = true, Tag = cls, Margin = new Padding(3, 1, 3, 1) };
                cb.CheckedChanged += delegate { RowToggled((string)cb.Tag); };
                var cf = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 216, Margin = new Padding(3, 1, 3, 1) };
                cf.Items.AddRange(FmtText); cf.SelectedIndex = 0;
                var cs = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 88, Margin = new Padding(3, 1, 3, 1) };
                cs.Items.AddRange(Sizes); cs.SelectedIndex = 0;
                // v2.33 放大：×2/×4。装不下就自动降级（×4→×2→不放大），受「最大边」与 4096 硬上限双重约束。
                var cu = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 84, Margin = new Padding(3, 1, 3, 1) };
                cu.Items.AddRange(Ups); cu.SelectedIndex = 0;
                var tipU = new ToolTip();
                L.Tip(cu, "把小于「最大边」的贴图整数倍放大（×2 / ×4）。只在彩色类生效。代价大，通常只放大主贴图就够。");
                var lb = new Label { Text = L.T(Classes.Note(cls)), AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(3, 4, 3, 1) };
                t2.Controls.Add(cb, 0, row); t2.Controls.Add(cf, 1, row);
                t2.Controls.Add(cs, 2, row); t2.Controls.Add(cu, 3, row);
                t2.Controls.Add(lb, 4, row);
                on[cls] = cb; fmt[cls] = cf; size[cls] = cs; up[cls] = cu; info[cls] = lb;
                upUiCtrls.Add(cu);                       // v2.34：默认藏起来，按按钮才显示
                row++;
            }
            t2UpColStyle = t2.ColumnStyles[3];           // 「放大」那一列的宽度（隐藏 = 0）
            gb2.Controls.Add(t2);
            root.Controls.Add(gb2, 0, 1);

            // ---- 全局选项（三行，每行独占一行）----
            // 用FlowLayoutPanel + FlowDirection.TopDown + WrapContents=false **强制每个子面板独占一行**。
            // 踩过的坑：TableLayoutPanel 嵌 TableLayoutPanel 时子面板的 PreferredSize 会被算大
            // （实测第三行只要 31px 却占了 192px），把上面的类型表」那一行挤到只剩 3 行可见。
            // TopDown 的 FlowLayoutPanel 只做垂直堆叠，不参与那种"抢高度"的协商。
            var pOpt = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };

            // 第1 行：JPEG 质量 / 灰度 2 通道
            var pRow1 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            pRow1.Controls.Add(new Label { Text = "JPEG 质量", AutoSize = true, Anchor = AnchorStyles.Left });
            trkQuality.ValueChanged += delegate { lblQuality.Text = trkQuality.Value.ToString(); };
            pRow1.Controls.Add(trkQuality); pRow1.Controls.Add(lblQuality);
            // v2.19：删掉「保护 alpha」复选框。
            // 它原来是"只对显式 JPEG 生效"的可选保护，在 auto 路径上根本不参与判定，
            // 而三个内置预设里一样 jpeg 都没有 → 默认流程下勾不勾完全没区别（空转控件）。
            // 现在 alpha 保护是**内建判据**（auto 自己保证不丢有用的 alpha，见 TexTool.alphaOk），
            // 而显式「全部 JPEG」不再被它挡住 ——名字说什么就做什么。
            chkGrayA.Text = "灰度用 2 通道";
            L.Tip(chkGrayA, TipGray);          // v1.1.5：这一项也要有说明
            chkGrayA.AutoSize = true;
            chkGrayA.Checked = PngEnc.UseGrayAlpha;
            chkGrayA.CheckedChanged += delegate { PngEnc.UseGrayAlpha = chkGrayA.Checked; };
            pRow1.Controls.Add(chkGrayA);
            pOpt.Controls.Add(pRow1);

            // 第2 行：锐化（+ 紧跟在右边的「细节图保细节」）
            var pRow2 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            pRow2.Controls.Add(new Label { Text = "锐化", AutoSize = true, Anchor = AnchorStyles.Left });
            trkSharpen.Value = Math.Max(trkSharpen.Minimum, Math.Min(trkSharpen.Maximum, Resample.SharpenPct));
            numSharpen.Value = Math.Max(numSharpen.Minimum, Math.Min(numSharpen.Maximum, Resample.SharpenPct));
            // 滑条 ↔ 数字框 双向同步（syncing 防回环）。
            // 滑条固定 0~200；*数字框可以填到 500%** —— 超过 200 时滑条顶到最右，表示"已超出滑条范围。
            bool syncing = false;
            trkSharpen.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                try { numSharpen.Value = trkSharpen.Value; } finally { syncing = false; }
                Resample.SharpenPct = trkSharpen.Value;
                WarnIfSharpenInert();
            };
            numSharpen.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                try { trkSharpen.Value = Math.Max(trkSharpen.Minimum, Math.Min(trkSharpen.Maximum, (int)numSharpen.Value)); }
                finally { syncing = false; }
                Resample.SharpenPct = (int)numSharpen.Value;
                WarnIfSharpenInert();
            };
            pRow2.Controls.Add(trkSharpen);
            pRow2.Controls.Add(numSharpen);
            pRow2.Controls.Add(lblSharpen);
            cbSharpMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cbSharpMode.Width = 128;
            cbSharpMode.Items.AddRange(new object[] { "边缘感知", "对比度自适应" });
            cbSharpMode.SelectedIndex = Resample.SharpMode == "cas" ? 1 : 0;
            cbSharpMode.SelectedIndexChanged += delegate
            {
                Resample.SharpMode = cbSharpMode.SelectedIndex == 1 ? "cas" : "edge";
                Resample.SharpModeExplicit = true;          // 用户动过这个下拉框 → 存预设时要"钉住"这个选择
                // 「边缘感知」得名副其实：SharpenEdgeMax 默认是 0，那样 USM 会走**均匀**锐化分支
                // （不再是"边缘更狠"）。选了这一项就把边缘区强度抬起来。
                if (Resample.SharpMode == "edge")
                    Resample.SharpenEdgeMax = Math.Max(2.5, Resample.Sharpen);
            };
            pRow2.Controls.Add(cbSharpMode);
            // v2.33：放大算法。只在某类「放大」选了 ×2/×4 时才起作用。
            cbUpKernel.DropDownStyle = ComboBoxStyle.DropDownList;
            cbUpKernel.Width = 104;
            cbUpKernel.Items.AddRange(Resample.UpKernels);
            cbUpKernel.SelectedIndex = Math.Max(0, Array.IndexOf(Resample.UpKernels, Resample.UpKernel));
            cbUpKernel.SelectedIndexChanged += delegate
            {
                if (cbUpKernel.SelectedIndex >= 0) Resample.UpKernel = Resample.UpKernels[cbUpKernel.SelectedIndex];
            };
            var tipUpK = new ToolTip();
            L.Tip(cbUpKernel, "放大用哪种算法。默认 lanczos3 最清晰，mitchell 最柔和，nearest 只适合像素画。");
            var tipS = new ToolTip();
            L.Tip(trkSharpen, "降分辨率后补一点锐化，找回面积平均糊掉的细节。0~200%，数字框可手打（上限 500%）。只对真的缩过的贴图生效。");
            L.Tip(numSharpen, "可直接输入数值（0~500）。超过 200 时滑块顶到最右。");
            L.Tip(cbSharpMode, "对比度自适应（默认）= 强边附近收着锐，更少白边；边缘感知 = 边缘锐得更狠，也更容易出白边。");
            pRow2.Controls.Add(chkKernel);
            chkKernel.Text = "细节图保细节";
            chkKernel.AutoSize = true;
            chkKernel.Checked = Resample.DetailMode;
            chkKernel.CheckedChanged += delegate { Resample.DetailMode = chkKernel.Checked; WarnIfSharpenInert(); SyncColorEnable(); };
            var tipK = new ToolTip();
            L.Tip(chkKernel, TipKernel);
            // v2.32：彩色贴图的核（面积平均 vs GDI+ 双三次），紧跟在上一个勾选框右边。
            chkColor.Text = ColorKernelText;
            chkColor.AutoSize = true;
            chkColor.Checked = Resample.ColorMode;
            chkColor.CheckedChanged += delegate { Resample.ColorMode = chkColor.Checked; };
            L.Tip(chkColor, TipColor);
            pRow2.Controls.Add(chkColor);
            pOpt.Controls.Add(pRow2);

            // 第3 行：独占贴图保护（单独一行）
            var pRow3 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            chkOwn.Text = "独占贴图保护";
            chkOwn.AutoSize = true;
            chkOwn.Checked = TexTool.ProtectOwn;
            cbOwnMax.DropDownStyle = ComboBoxStyle.DropDownList;
            cbOwnMax.Width = 92;
            cbOwnMax.Items.AddRange(OwnMaxItems);
            cbOwnMax.SelectedIndex = OwnMaxIndex(TexTool.ProtectOwnMax);
            cbOwnMax.Enabled = chkOwn.Checked;
            chkOwn.CheckedChanged += delegate
            {
                TexTool.ProtectOwn = chkOwn.Checked;
                cbOwnMax.Enabled = chkOwn.Checked;
            };
            cbOwnMax.SelectedIndexChanged += delegate
            {
                TexTool.ProtectOwnMax = OwnMaxValue(cbOwnMax.SelectedIndex);
            };
            var tipO = new ToolTip();
            L.Tip(chkOwn, TipOwn);
            L.Tip(cbOwnMax, "独占贴图的最大边上限：4096 = 画质优先（默认）；2048 = 折中；原尺寸不降 = 完全不缩。");
            pRow3.Controls.Add(chkOwn);
            pRow3.Controls.Add(cbOwnMax);
            // v2.36：全局「放大」倍率下拉（给「应用放大设置」当输入）
            cbUpAll.DropDownStyle = ComboBoxStyle.DropDownList;
            cbUpAll.Width = 84;
            cbUpAll.Items.AddRange(Ups);
            cbUpAll.SelectedIndex = 0;                            // 不放大
            L.Tip(cbUpAll, "选好倍率后点右边的「应用放大设置」，会写进类型表里所有已勾选的类；之后仍可逐类改。");
            // v2.36：「显示放大选项」按钮放在「独占贴图保护（+上限）」后面，放大那两项紧跟其后
            bShowUp1 = new Button { Text = "显示放大选项 ▾", Margin = new Padding(10, 1, 2, 1) };
            HookUpToggle(bShowUp1);
            var tipShowUp = new ToolTip();
            L.Tip(bShowUp1, "放大（×2 / ×4）代价大、多数人用不上，所以默认收起；点这里展开（三个页面一起）。");
            pRow3.Controls.Add(bShowUp1);
            // 全局「放大」+「放大算法」：展开后才出现，且紧跟在按钮后面
            var pUpAll = Pair("放大", cbUpAll);
            var pUpKAll = Pair("放大算法", cbUpKernel);
            pRow3.Controls.Add(pUpAll);
            pRow3.Controls.Add(pUpKAll);
            upUiCtrls.Add(pUpAll);
            upUiCtrls.Add(pUpKAll);
            // 「应用放大设置」：把上面选的倍率写到类型表里**所有已勾选的类**那一列
            bApplyUp = new Button { Text = "应用放大设置", AutoSize = true, Margin = new Padding(6, 1, 2, 1) };
            bApplyUp.Click += delegate { ApplyUpToClasses(); };
            L.Tip(bApplyUp, "把左边的放大倍率写进类型表里所有已勾选的类。放大只在彩色类生效，且受「最大边」与 4096px 上限约束。");
            pRow3.Controls.Add(bApplyUp);
            upUiCtrls.Add(bApplyUp);
            pOpt.Controls.Add(pRow3);
            root.Controls.Add(pOpt, 0, 2);

            // ---- 预设：下拉框（原来两个固定预设按钮改成选哪个预设"）----
            var pPre1 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            pPre1.Controls.Add(new Label { Text = "预设", AutoSize = true, Anchor = AnchorStyles.Left });
            cbPre1.DropDownStyle = ComboBoxStyle.DropDownList;
            cbPre1.Width = 300;
            cbPre1.DropDownWidth = 520;
            cbPre1.SelectedIndexChanged += delegate { PreApply(cbPre1.SelectedIndex); };
            pPre1.Controls.Add(cbPre1);
            var bPreSave = new Button { Text = "保存为预设…", AutoSize = true };
            bPreSave.Click += delegate { PreSaveDialog(); };
            var bPreLoad = new Button { Text = "读取预设…", AutoSize = true };
            bPreLoad.Click += delegate { PreLoadDialog(); };
            var bPreOpen = new Button { Text = "打开预设目录", AutoSize = true };
            bPreOpen.Click += delegate { PreOpenDir(PresetIo.KindBatch); };
            var bPreRef = new Button { Text = "刷新", AutoSize = true };
            bPreRef.Click += delegate { PreReload(); };
            // v2.23：把当前选中的预设设成"启动默认"（写一个标记文件，不依赖注册表）
            chkPreDefault.Text = "将当前预设设为默认";
            chkPreDefault.AutoSize = true;
            chkPreDefault.CheckedChanged += delegate
            {
                if (syncingPreDefault) return;
                int i = cbPre1.SelectedIndex;
                if (chkPreDefault.Checked)
                {
                    if (i <= 0 || i >= pre1.Count || string.IsNullOrEmpty(pre1[i].Path))
                    {
                        MessageBox.Show(L.Tr("先在左边的下拉框里选一个预设，再勾这个。\n（「不使用预设」不能设为默认。）"));
                        syncingPreDefault = true; chkPreDefault.Checked = false; syncingPreDefault = false;
                        return;
                    }
                    PresetIo.SetDefaultPreset(PresetIo.KindBatch, pre1[i].Path);
                    lblPre1.Text = "默认预设：" + pre1[i].Name + "（下次启动自动套用它）";
                }
                else
                {
                    PresetIo.SetDefaultPreset(PresetIo.KindBatch, null);
                    lblPre1.Text = "已取消默认预设（下次启动回到「仅优化」）";
                }
            };
            L.Tip(chkPreDefault, "勾上 = 每次启动自动套用这个预设；取消 = 回到内置的「仅优化」。");
            pPre1.Controls.Add(bPreSave); pPre1.Controls.Add(bPreLoad); pPre1.Controls.Add(bPreOpen); pPre1.Controls.Add(bPreRef);
            pPre1.Controls.Add(chkPreDefault);
            lblPre1.Text = "（preset\\batch 里还没有预设）";
            // v1.0.5：不再强制 AutoSize=true —— 预设状态行在英文下比较长，
            // 单行会被右边切掉；保持 AutoSize=false 让它自动换行显示完整。
            lblPre1.ForeColor = Color.Gray; lblPre1.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            pPre1.Controls.Add(lblPre1);
            root.Controls.Add(pPre1, 0, 3);
            // ---- 一键统一设置（所有类型一起改）---
            var cbAllFmt = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            cbAllFmt.Items.AddRange(FmtText);
            cbAllFmt.SelectedIndex = 1;                       // 自动
            var cbAllSize = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            cbAllSize.Items.AddRange(Sizes);
            cbAllSize.SelectedIndex = 3;                      // 1024
            var bAll = new Button { Text = "应用到所有类型", AutoSize = true };
            bAll.Click += delegate
            {
                int fi = cbAllFmt.SelectedIndex, si = cbAllSize.SelectedIndex;
                foreach (var c in Classes.Order)
                {
                    fmt[c].SelectedIndex = fi;
                    size[c].SelectedIndex = si;
                    on[c].Checked = FmtKey[fi] != "keep";
                }
                lblStatus.Text = L.F("已把所有类型统一设为：{0} / {1}", FmtText[fi], Sizes[si]);
            };
            // ---- 统一设置（一键改所有类型）：v2.23 起放进「全局选项」块的最上面 ----
            // 也就是排在「JPEG 质量」那一行**之上**（原来它是独立一行、在预设行下面）。
            // v1.0：去掉 Dock=Fill —— 配 AutoSize 时高度由父布局给，英文文案变高后被压成 0，
            // 这一行里的「应用到所有类型」按钮就整个看不见了。
            pUni1 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            pUni1.Controls.Add(new Label { Text = "统一设置（一键改所有类型）：", AutoSize = true, Anchor = AnchorStyles.Left });
            pUni1.Controls.Add(cbAllFmt); pUni1.Controls.Add(cbAllSize); pUni1.Controls.Add(bAll);
            pOpt.Controls.Add(pUni1);
            pOpt.Controls.SetChildIndex(pUni1, 0);                 // 插到最前面（在「JPEG 质量」那行之上）

            // ---- 人物卡组过滤（只对Koikatu_F_*.png 人物卡生效）----
            // 默认**收起**（衣服卡用不到这一行）；导入的卡里有人物卡时自动展开。
            // 也可以点标题手动展开/收起。
            var pGrpRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            lblGrpToggle.Text = "▸ 人物卡组过滤（只对人物卡生效，点这里展开）";
            lblGrpToggle.AutoSize = true;
            lblGrpToggle.ForeColor = Color.Gray;
            lblGrpToggle.Cursor = Cursors.Hand;
            lblGrpToggle.Click += delegate { SetGroupFilterVisible(!pGrp.Visible); };
            pGrpRow.Controls.Add(lblGrpToggle);
            pGrp = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), Visible = false };
            pGrp.Controls.Add(new Label { Text = "人物卡组过滤：", AutoSize = true, Anchor = AnchorStyles.Left });
            // v2.30：复选框放在这个子面板里，按"卡里实际有几套换装"动态重建（见 BuildGrpFilter）
            pGrpChk = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = true };
            pGrp.Controls.Add(pGrpChk);
            BuildGrpFilter(7);                       // 初始先按游戏默认的 7 套显示；扫描完会按实际套数重建
            var bGAll = new Button { Text = "全选", AutoSize = true };
            bGAll.Click += delegate { CharaMaskAll(true, false); };
            var bGBody = new Button { Text = "只压本体", AutoSize = true };
            bGBody.Click += delegate { CharaMaskAll(false, true); };
            var bGNone = new Button { Text = "全不选", AutoSize = true };
            bGNone.Click += delegate { CharaMaskAll(false, false); };
            pGrp.Controls.Add(bGAll); pGrp.Controls.Add(bGBody); pGrp.Controls.Add(bGNone);
            pGrp.Controls.Add(new Label { Text = "（衣服卡不受影响）", AutoSize = true, ForeColor = Color.Gray, Anchor = AnchorStyles.Left });
            pGrpRow.Controls.Add(pGrp);
            root.Controls.Add(pGrpRow, 0, 5);

            // ---- 按钮（日志开关单独摆在最右边）---
            var pActRow = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                              Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 2 };
            pActRow1 = pActRow;
            pActRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pActRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // v1.1.3：不要 Dock=Fill —— 它配 AutoSize 时高度由父布局给、不会因换行而变高，
            // 于是按钮换到第二行就被裁掉（用户反馈：记住列宽/恢复默认列宽被挤到主按钮下面看不见）。
            var pAct = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                             Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            // 2.17：移除「① 扫描」按钮 —— 它存在的唯一理由原来是"浏览/手输路径后不会自动扫描"，
            // 现在这几个入口都会自动扫描（览 PickInput / txtIn 的 Enter 与失焦），按钮就多余了。
            // 「② 开始压缩」做成大按钮（这是这一页唯一的主操作）。
            MakeMainButton(btnRun, "▶  开始压缩", Font);
            btnRun.Click += delegate { DoRun(); };
            btnCancel.Click += delegate { cancelFlag = true; lblStatus.Text = "正在取消…"; };
            var bOpen = new Button { Text = "打开输出目录", AutoSize = true };
            bOpen.Click += delegate
            {
                if (Directory.Exists(txtOut.Text.Trim())) System.Diagnostics.Process.Start("explorer.exe", txtOut.Text.Trim());
            };
            var bPlan = new Button { Text = "另存为计划 JSON（CLI 批处理可用）", AutoSize = true };
            bPlan.Click += delegate
            {
                var d = new SaveFileDialog { FileName = "plan.json", Filter = "JSON|*.json" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    PlanIo.Save(BuildPlan(), d.FileName);
                    MessageBox.Show(L.Tr("已保存：\n") + d.FileName + "\n\n批处理用法：\nKoiCardTexTool.exe batch <输入目录> <输出目录> 1024 plan=" + d.FileName);
                }
            };
            var bQuit = new Button { Text = "退出", AutoSize = true };
            bQuit.Click += delegate { Close(); };
            pAct.Controls.Add(btnRun); pAct.Controls.Add(btnCancel);
            pAct.Controls.Add(bOpen); pAct.Controls.Add(bPlan); pAct.Controls.Add(bQuit);
            btnLog1.Click += delegate { LogBtnClicked(1); };
            pActRow.Controls.Add(pAct, 0, 0);
            pActRow.Controls.Add(btnLog1, 1, 0);                  // 日志开关单独摆在最右边
            root.Controls.Add(pActRow, 0, 6);

            // ---- 进度 + 状态（底部）----
            pStat1 = new Panel { Dock = DockStyle.Top, AutoSize = false };
            pb.Dock = DockStyle.Top; pb.Height = 18;
            lblStatus.Dock = DockStyle.Top;
            pStat1.Controls.Add(lblStatus); pStat1.Controls.Add(pb);
            root.Controls.Add(pStat1, 0, 7);

            // ---- 日志：右侧独立整块（分隔条可拖动，按钮可折叠）----
            log.Dock = DockStyle.Fill;
            logWrap1 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };
            var pLogHead = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            pLogHead.Controls.Add(new Label { Text = "日志", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left });
            logWrap1.Controls.Add(log); logWrap1.Controls.Add(pLogHead);

            rootT1 = root;
            spLog1 = MakeSplitter(root, 1, 0, 2, false);          // 操作区 | 日志
            HookLogSplitter(spLog1, root, 2);
            RegisterLogCol(root, 2, 620, 0);                      // 放大窗口 → 多出来的宽度优先给日志
            root.Controls.Add(logWrap1, 2, 0);
            root.SetRowSpan(logWrap1, 8);                          // 日志占满整高

            var page1 = new TabPage("① 批量压缩（整卡 / 文件夹）") { Padding = new Padding(4) };
            page1.Controls.Add(root);
            tabs.TabPages.Add(page1);
            tabs.TabPages.Add(BuildPartsPage());
            tabs.TabPages.Add(BuildCharaPage());

            Controls.Add(tabs);                    // 现有页面整体放进第 1 个标签页（布局未动）
            Controls.Add(lblDrop);                 // Dock=Top 的提示条（先加 Fill，再加 Top）

            // v1.0：语言选择放在"压缩模式"那一行（页签条）的最右边。
            // 做法：把一个小面板浮在页签条右侧（TabControl 的页签条区域是空着的，子控件能盖上去）。
            BuildLangPicker();
            ApplyPlan(Classes.DefaultPlan());
            PreReload();                           // 预设\batch 里的预设填进下拉框
            SetUpUiVisible(UpUiWanted);            // v2.34：放大选项默认收起（三个页面一起；showup= 可提前打开）
            SetTypeUniVisible(TypeUniWanted);      // v2.38：按类型统一那一栏默认也收起
            // v1.0：界面多语言。字典以**中文原文为键**，所以只要在这里设好当前语言、
            // 然后把整棵控件树翻一遍即可 —— 200 多处构造代码一处都不用改。
            L.Cur = LangOverride ?? LoadLang();
            // v1.1.0：外观主题。放在多语言之后套色（Apply 会改 AutoSize/文字，颜色随后覆盖）
            SetAppIcon(this);                   // v1.1.9：窗口图标用 exe 自带的图标
            Theme.Set(Theme.Load());
            Theme.Apply(this);
            L.Apply(this);
            // 翻译完必须重排一遍：英文文案比中文宽，AutoSize 控件要是没重新测量，
            // 宽度会停在中文时的值 → 英文被切掉一截（实测单选按钮 112 需要 / 104 实际）。
            L.Relayout(this);
            // v1.1.5：**必须重新设一遍气泡**。构造界面时 L.Cur 还是静态默认值（英语），
            // 而"设置语言"的语句在构造之后 —— 不补这行的话，气泡在任何语言下都显示英文。
            L.RetipAll();
            // 窗口尺寸/分栏位置定型之后还要再算一次：上面那次是在"窗口还没摆好"时算的，
            // 之后分栏一变窄，换行数就变了（英文文案下尤其明显），高度会再次不够。
            Shown += delegate { L.Relayout(this); Theme.ApplyNative(this, this); };
            // v1.1.3：窗口尺寸变化后让会换行的容器重新量一次高度。
            // 现在重排只对根做一次 PerformLayout、且只在该页首次显示时跑，开销可忽略（实测 ~0ms）；
            // 不重排的话，窗口变窄时多折一行会把最后一个控件挤出面板（窄窗口下"显示放大选项"被裁）。
            Resize += delegate { L.Relayout(this); };
            Resize += delegate { L.Relayout(this); };
            // 切页时也必须重排：**没被选中的页根本没布局过**（子控件 Visible=false），
            // 那时量出来的高度是垃圾值，切过去就会看到控件被切掉。
            tabs.SelectedIndexChanged += delegate
            {
                // 只在**这一页第一次显示**时重排：切页重排是为了让英文/日文下多折一行的容器
                // 拿到正确高度，这不是每次切页都要做的事 —— 实测每次切页 400ms+ 里 9 成是它。
                var swt = System.Diagnostics.Stopwatch.StartNew();
                var page = tabs.SelectedTab;
                if (page != null && !relaidPages.Contains(page))
                {
                    relaidPages.Add(page);
                    L.Relayout(tabs);
                }
                Application.DoEvents();                   // 先把这一页画出来，再算下一次
                swt.Stop();
                LastTabMs = swt.ElapsedMilliseconds;      // 自检用：这次切页花了多少
            };

            txtIn.TextChanged += delegate
            {
                if (!settingText) { dropped.Clear(); UpdateDropHint(); }
            };
            // 2.17：手输路径后自动扫描（Enter 或离开焦点），免得还要专门点一次「扫描」按钮。
            // 只在路径真的存在、且跟上次扫描过的不同时才扫，避免打字过程中反复触发。
            txtIn.KeyDown += delegate (object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; AutoScanIfNew(); }
            };
            txtIn.Leave += delegate { AutoScanIfNew(); };

            tabs.SelectedIndexChanged += delegate
            {
                SyncGlobalControls();                  // 第2/3 页改过的全局设置，回第 1 页要能看见
                BeginInvoke((Action)delegate { FitTab1(); FitTabs23(); });
            };
            pump = new Timer { Interval = 100 };
            pump.Tick += delegate { Drain(); };
            pump.Start();

            if (startup != null && startup.Length > 0)
                pendingStartup = startup;      // 真正加载放到 OnShown（构造期间句柄还没建好）
        }

        string[] pendingStartup;

        /// <summary>把「标签 + 控件」包成一个小面板 —— 换行时整对一起换，
        /// 不会出现"文字在上一行、选择框掉到下一行（用户明确要求）。</summary>
        static FlowLayoutPanel Pair(string label, Control ctl)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(2, 3, 8, 3) };
            p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 3, 0) });
            p.Controls.Add(ctl);
            return p;
        }

        /// <summary>单个控件也包一层，好让它在换行时作为一个整体。</summary>
        static FlowLayoutPanel Pair2(Control ctl)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(2, 3, 8, 3) };
            p.Controls.Add(ctl);
            return p;
        }

        /// <summary>把一个外部控件搬进 tab（WinForms 里同一控件只能有一个父）。</summary>
        static T Reparent<T>(T ctl, Control newParent) where T : Control
        {
            if (ctl.Parent != null) ctl.Parent.Controls.Remove(ctl);
            newParent.Controls.Add(ctl);
            return ctl;
        }

        /// <summary>把按钮做成"主操作"样式（三页统一走这一个方法，免得各写一套又慢慢走偏）。</summary>
        static void MakeMainButton(Button b, string text, Font baseFont)
        {
            b.Tag = "primary";   // v1.1.0：主按钮用强调色
            b.Text = text;
            b.Font = new Font(baseFont.FontFamily, baseFont.Size + 3.5f, FontStyle.Bold);
            b.Padding = new Padding(26, 10, 26, 10);
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        /// <summary>第 2 个标签页：单服装卡细分压缩（按部位细分）。第 1 页的布局与压缩方式完全没动。</summary>
        TabPage BuildPartsPage()
        {
            var page = new TabPage("② 单服装卡细分压缩") { Padding = new Padding(6) };
            // 四块 + 三条可拖动分隔条：
            //  [0]左列(360) [1]条[2]部位一览(50%) [3]条 [4]贴图明细(50%) [5]条 [6]日志(340，可折叠)
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
            // 单列也要显式给Percent：不给的话隐含列是 AutoSize，会按最宽子控件的"需要宽度"撑开
            // （表现：GroupBox/表格比容器宽、被右边切掉一截）
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Padding = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));     // 左列（选卡+输出/选项）
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitW));  // 分隔条
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));       // 部位一览
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitW));  // 分隔条
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));       // 贴图明细
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitW));  // 分隔条
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));// 日志
            rootT2 = t;
            outer.Controls.Add(t, 0, 0);

            // ---- 左列：选卡（上）+ 输出/选项（下）----
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            // 单列也要显式给Percent：不给的话隐含列是 AutoSize，会按最宽子控件的"需要宽度"撑开
            // （表现：GroupBox/表格比容器宽、被右边切掉一截）
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            gbIn2 = new GroupBox { Text = "1) 选择一张衣服卡（.png）", Dock = DockStyle.Fill, AutoSize = false };
            var pIn = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(6) };
            txtP2Card.Width = 330;
            var bPick = new Button { Text = "浏览…", AutoSize = true };
            bPick.Click += delegate
            {
                var d = new OpenFileDialog { Filter = "Koikatu 衣服卡|*.png|所有文件|*.*" };
                if (d.ShowDialog() == DialogResult.OK) { txtP2Card.Text = d.FileName; LoadParts(); }
            };
            var bLoad = new Button { Text = "② 读取部位", AutoSize = true };
            bLoad.Click += delegate { LoadParts(); };
            var pInRow = new FlowLayoutPanel { AutoSize = true };
            pInRow.Controls.Add(bPick); pInRow.Controls.Add(bLoad);
            pIn.Controls.Add(new Label { Text = "卡片路径", AutoSize = true });
            pIn.Controls.Add(txtP2Card);
            pIn.Controls.Add(pInRow);
            pIn.Controls.Add(new Label
            {
                Text = "读取后中间列出每个部位有哪些贴图；勾选想压的部位、\n给每个部位选格式与最大边；右边可以给单张贴图\n单独指定；没勾/没指定的部位原样不动。",
                ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(320, 0)
            });
            gbIn2.Controls.Add(pIn);
            left.Controls.Add(gbIn2, 0, 0);

            // ---- 输出 / 选项（竖排，塞进左列）---
            gbOut2 = new GroupBox { Text = "4) 输出 / 选项", Dock = DockStyle.Fill, AutoSize = false };
            var pOut = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(6) };

            var rOut = new FlowLayoutPanel { AutoSize = true };
            rOut.Controls.Add(new Label { Text = "输出目录", AutoSize = true, Anchor = AnchorStyles.Left });
            txtP2Out.Width = 200;
            var bOut = new Button { Text = "浏览…", AutoSize = true };
            bOut.Click += delegate
            {
                var d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) txtP2Out.Text = d.SelectedPath;
            };
            rOut.Controls.Add(txtP2Out); rOut.Controls.Add(bOut);

            var rSuf = new FlowLayoutPanel { AutoSize = true };
            rSuf.Controls.Add(new Label { Text = "文件名后缀", AutoSize = true, Anchor = AnchorStyles.Left });
            txtP2Suffix.Text = "[zip]"; txtP2Suffix.Width = 80;
            rSuf.Controls.Add(txtP2Suffix);

            // 勾选框各自单独一行（历史教训：挤在一行里会被容器截断成半截文字）
            var rChk = new FlowLayoutPanel { AutoSize = true };
            rChk.Controls.Add(chkP2Overwrite);

            var rQ = new FlowLayoutPanel { AutoSize = true };
            rQ.Controls.Add(new Label { Text = "JPEG 质量", AutoSize = true, Anchor = AnchorStyles.Left });
            rQ.Controls.Add(numP2Quality);

            var rSub = new FlowLayoutPanel { AutoSize = true };
            rSub.Controls.Add(chkSub);

            // v2.22：默认只压"这一件"；勾上才把设置套到同目录下的**同款**服装上。
            // 只有勾了它，「不匹配的服装直接复制到输出目录」才有意义（否则根本没有"不匹配的卡"）。
            chkAllSimilar.AutoSize = true;
            chkAllSimilar.Checked = false;
            chkAllSimilar.CheckedChanged += delegate { SyncAllSimilar(); };
            L.Tip(chkAllSimilar, "勾上 = 开始压缩时，把当前设置套到同目录下同款的其它卡上；不勾 = 只压这一件。");
            var rAllSim = new FlowLayoutPanel { AutoSize = true };
            rAllSim.Controls.Add(chkAllSimilar);

            var rCopy = new FlowLayoutPanel { AutoSize = true };
            chkCopySkip.AutoSize = true;
            chkCopySkip.Checked = SkipCopy.On;
            chkCopySkip.CheckedChanged += delegate { if (!syncingCopy) SetCopySkip(chkCopySkip.Checked); };
            rCopy.Controls.Add(chkCopySkip);
            L.Tip(chkCopySkip, "与预设不是同款服装时：不勾 = 跳过；勾上 = 原样复制到输出目录。（配合上一项使用）");
            SyncAllSimilar();                       // 初始状态：没勾"相似服装"→ 复制开关置灰

            // ================= 一键统一（v2.21：整块搬到中间列，放在「部位一览」上方）=================
            // 里面每一项都是"标签 + 选择框"成对包成一个小面板 —— 换行时整对一起换，
            // 不会出现"文字在上一行、选择框掉到下一行。
            var cbPartFmt = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92 };
            cbPartFmt.Items.AddRange(TexFmtItems);             // 含「跟随部位」
            cbPartFmt.SelectedIndex = 4;                       // 自动
            var cbPartSize = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            cbPartSize.Items.AddRange(TexSizeItems);
            cbPartSize.SelectedIndex = 4;                      // 1024
            var cbPartSh = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 78 };
            cbPartSh.Items.AddRange(SharpenItems);
            cbPartSh.SelectedIndex = 0;                        // 跟随
            // 锐化算法：和强度一起放进一键统一（v2.22）。「跟随」  用第 1 页那套全局方式。
            var cbPartShMode = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 108 };
            cbPartShMode.Items.AddRange(ShModeItems);
            cbPartShMode.SelectedIndex = 0;                    // 跟随
            var cbPartGa = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 62 };
            cbPartGa.Items.AddRange(UniGrayItems);             // 一键统一只有「开 / 关」（v2.23）
            cbPartGa.SelectedIndex = 0;                        // 开（与全局默认一致）
            // v2.34：放大（默认收起）。「跟随」= 不表态 → 用第 1 页类型表里该类设的值。
            var cbPartUp = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 78 };
            cbPartUp.Items.AddRange(UpItems);
            cbPartUp.SelectedIndex = 0;                        // 跟随
            var cbPartUpK = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 104 };
            cbPartUpK.Items.AddRange(Resample.UpKernels);
            cbPartUpK.SelectedIndex = Math.Max(0, Array.IndexOf(Resample.UpKernels, Resample.UpKernel));
            cbPartUpK.SelectedIndexChanged += delegate
            {
                if (cbPartUpK.SelectedIndex >= 0) Resample.UpKernel = Resample.UpKernels[cbPartUpK.SelectedIndex];
            };
            L.Tip(cbPartUpK, "放大用哪种算法（三页共用）。默认 lanczos3 最清晰，mitchell 最柔和，nearest 只适合像素画。");
            var bAllParts = new Button { Text = "应用到所有部位", AutoSize = true };
            bAllParts.Click += delegate
            {
                string f = cbPartFmt.SelectedItem as string, s = cbPartSize.SelectedItem as string;
                string sh = cbPartSh.SelectedItem as string, ga = cbPartGa.SelectedItem as string;
                string shm = cbPartShMode.SelectedItem as string;
                string upIt = cbPartUp.SelectedItem as string;
                foreach (DataGridViewRow row in gridParts.Rows)
                {
                    if (row.Tag == null) continue;
                    bool hasTex = (row.Cells["cnt"].Value as string) != "—";
                    row.Cells["on"].Value = hasTex && f != "原样不动";
                    row.Cells["fmt"].Value = f;
                    row.Cells["max"].Value = s;
                    row.Cells["sh"].Value = sh;
                    row.Cells["shm"].Value = shm;
                    row.Cells["ga"].Value = ga;
                    row.Cells["up"].Value = upIt;
                }
                FillTexList();
                lblP2Status.Text = L.F("已把所有部位统一设为：{0} / {1} / 锐化 {2} {3} / 灰度2通道 {4}{5}",
                    f, s, sh, shm, ga, upIt != "跟随" ? " / 放大 " + upIt : "");
            };
            var bAllTex = new Button { Text = "应用到本部位全部贴图", AutoSize = true };
            bAllTex.Click += delegate
            {
                string f = cbPartFmt.SelectedItem as string, s = cbPartSize.SelectedItem as string;
                string sh = cbPartSh.SelectedItem as string, ga = cbPartGa.SelectedItem as string;
                string shm = cbPartShMode.SelectedItem as string;
                string upIt = cbPartUp.SelectedItem as string;
                for (int i = 0; i < gridTex.Rows.Count; i++)
                {
                    UiTexSetRow(i, f, s);
                    gridTex.Rows[i].Cells["tsh"].Value = sh;
                    gridTex.Rows[i].Cells["tshm"].Value = shm;
                    gridTex.Rows[i].Cells["tga"].Value = ga;
                    gridTex.Rows[i].Cells["tup"].Value = upIt;
                }
                TexOvChanged();
                lblP2Status.Text = L.F("已把本部位 {0} 张贴图统一设为：{1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}",
                    gridTex.Rows.Count, f, s, sh, shm, ga, upIt != "跟随" ? " / 放大 " + upIt : "");
            };

            // v1.0：Dock 用 Top 而不是 Fill —— Fill + AutoSize 时高度由外层 AutoSize 行决定，
            // 而那一行不会因为面板多折一行而自己重算，英文文案下最后一个控件（显示放大选项）
            // 正好落在面板底边外（实测 Bounds 306x0，界面上看不见）。Top + AutoSize 高度跟着内容走。
            // v1.0：**不用 Dock**，用 Anchor 左右 + AutoSize ——
            // Dock(Fill/Top) + AutoSize 时高度由父布局决定，而父布局用的是"只有一行"的
            // PreferredSize（实测 37），英文多折一行后最后一个控件就被切掉（按钮 Y=292 / 面板高 283）。
            // Anchor 左右 + AutoSize：宽度跟着单元格，高度跟着内容。
            // v1.0.2：不用 Dock=Fill —— 它配 AutoSize 时高度由父布局给，英文文案多折一行后
            // 最后一个控件（「显示放大选项」）会落在面板外看不见。Anchor 左右 + AutoSize：宽度跟单元格，高度跟内容。
            pUni2 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                          Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                                          Padding = new Padding(0), WrapContents = true, Margin = new Padding(0) };
            pUni2.Controls.Add(new Label { Text = "一键统一", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 6, 3) });
            pUni2.Controls.Add(Pair("处理方式", cbPartFmt));
            pUni2.Controls.Add(Pair("最大边", cbPartSize));
            pUni2.Controls.Add(Pair("锐化", cbPartSh));
            pUni2.Controls.Add(Pair("锐化算法", cbPartShMode));
            pUni2.Controls.Add(Pair("灰度2通道", cbPartGa));
            // 「贴图细节保护」（原「细节图保细节」）与「独占贴图保护」：从第 1 页搬进来一份，
            // 和批量页共用同一组静态设置（改一处两边同步）。它们立刻生效，不需要点应用按钮。
            chkKernel2.Checked = Resample.DetailMode;
            chkKernel2.CheckedChanged += delegate { Resample.DetailMode = chkKernel2.Checked; WarnIfSharpenInert(); SyncColorEnable(); };
            L.Tip(chkKernel2, TipKernel);
            // v2.32：彩色贴图的核（和第 1 页那一项是同一个设置）
            chkColor2.Checked = Resample.ColorMode;
            chkColor2.CheckedChanged += delegate { Resample.ColorMode = chkColor2.Checked; };
            L.Tip(chkColor2, TipColor);
            chkOwn2.Checked = TexTool.ProtectOwn;
            cbOwnMax2.DropDownStyle = ComboBoxStyle.DropDownList;
            cbOwnMax2.Width = 104;
            cbOwnMax2.Items.AddRange(OwnMaxItems);
            cbOwnMax2.SelectedIndex = OwnMaxIndex(TexTool.ProtectOwnMax);
            cbOwnMax2.Enabled = chkOwn2.Checked;
            chkOwn2.CheckedChanged += delegate
            {
                TexTool.ProtectOwn = chkOwn2.Checked;
                cbOwnMax2.Enabled = chkOwn2.Checked;
            };
            cbOwnMax2.SelectedIndexChanged += delegate
            {
                TexTool.ProtectOwnMax = OwnMaxValue(cbOwnMax2.SelectedIndex);
            };
            L.Tip(chkOwn2, TipOwn);
            pUni2.Controls.Add(Pair2(chkKernel2));
            pUni2.Controls.Add(Pair2(chkColor2));
            // v2.35：「独占贴图保护」与「上限」绑成一个不可拆的单元，保证它们永远在同一行
            // （之前是两个独立子控件，FlowLayoutPanel 换行时会把它俩拆到两行）。
            var pOwn2 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
            pOwn2.Controls.Add(Pair2(chkOwn2));
            pOwn2.Controls.Add(Pair("上限", cbOwnMax2));
            pUni2.Controls.Add(pOwn2);
            // v2.35：「显示放大选项」按钮放在「独占贴图保护」后面，展开出来的两项紧跟在按钮后面。
            bShowUp2 = new Button { Text = "显示放大选项 ▾", Margin = new Padding(6, 4, 4, 1) };
            HookUpToggle(bShowUp2);
            L.Tip(bShowUp2, "放大（×2 / ×4）默认收起：代价大、多数人用不上。点这里展开（三个页面一起）。");
            pUni2.Controls.Add(bShowUp2);
            var pUp2 = Pair("放大", cbPartUp);
            var pUpK2 = Pair("放大算法", cbPartUpK);
            pUni2.Controls.Add(pUp2);
            pUni2.Controls.Add(pUpK2);
            upUiCtrls2.Add(pUp2);
            upUiCtrls2.Add(pUpK2);
            // v2.38：按贴图类型统一（展开栏）。默认收起；展开后是
            // 「贴图类型 [下拉] [应用到该类型贴图]」—— 这两件必须挨在一起，所以包成一个不可拆的子面板。
            cbTypeUni2 = MakeTypeCombo();
            bApplyType2 = new Button { Text = "应用到所有该类型贴图", AutoSize = true, Margin = new Padding(6, 1, 2, 1) };
            bApplyType2Part = new Button { Text = "应用到选中部位", AutoSize = true, Margin = new Padding(3, 1, 2, 1) };
            pTypePair2 = MakeTypePair(cbTypeUni2, bApplyType2, bApplyType2Part);
            L.Tip(bApplyType2, "把「一键统一」那套值套到整张卡里这一类型的全部贴图（含单张指定，优先级最高）。");
            // onlyThisPart=true 时只改**当前选中部位**里该类型的贴图；false 时整张卡
            Action<bool> applyTypeUni2 = delegate (bool onlyThisPart)
            {
                string cls = TypeComboCls(cbTypeUni2);
                string uf = cbPartFmt.SelectedItem as string, usz = cbPartSize.SelectedItem as string;
                string ush = cbPartSh.SelectedItem as string, ushm = cbPartShMode.SelectedItem as string;
                string uga = cbPartGa.SelectedItem as string, uup = cbPartUp.SelectedItem as string;
                if (ps == null || ps.Parts == null || ps.Parts.Count == 0)
                {
                    lblP2Status.Text = "请先读取一张服装卡";
                    return;
                }
                int only = -1;
                if (onlyThisPart)
                {
                    if (gridParts.CurrentRow == null || gridParts.CurrentRow.Tag == null)
                    {
                        lblP2Status.Text = "请先在「部位一览」里选中一个部位";
                        return;
                    }
                    only = (int)gridParts.CurrentRow.Tag;
                }
                int n = 0;
                foreach (var pi in ps.Parts)
                {
                    if (onlyThisPart && pi.SlotKey != only) continue;
                    foreach (var tx in pi.Tex)
                    {
                        if (tx.Id == null || tx.Cls != cls) continue;
                        var r = new Rule(uf == "跟随部位" ? "auto" : FmtItemToKey(uf),
                                         usz == "跟随部位" ? 1024 : SizeItemToNum(usz));
                        SetRuleExtra(r, SharpenItemToVal(ush), ShModeItemToVal(ushm), GrayItemToVal(uga), UpTableToVal(uup));
                        texOv[tx.Id.ToString()] = r;
                        n++;
                    }
                }
                FillTexList();
                lblP2Status.Text = L.F("已把{0}的「{1}」类型 {2} 张贴图设为：{3} / {4} / 锐化 {5} {6} / 灰度2通道 {7}{8}",
                    onlyThisPart ? "选中部位" : "整张卡", Classes.Short(cls), n, uf, usz, ush, ushm, uga,
                    uup != "不放大" ? " / 放大 " + uup : "");
            };
            bApplyType2.Click += delegate { applyTypeUni2(false); };
            bApplyType2Part.Click += delegate { applyTypeUni2(true); };
            L.Tip(bApplyType2Part, "只改当前选中部位里这一类型的贴图。先在「部位一览」里选中一个部位。");
            bTypeShow2 = new Button { Text = L.T("按贴图类型统一 ▾"), Margin = new Padding(0, 2, 4, 0) };
            HookTypeToggle(bTypeShow2, delegate { SetTypeUniVisible(!typeUniVisible); });
            L.Tip(bTypeShow2, "想按贴图类型批量改（比如把所有主贴图设成同一个值）时点开这里。");
            pTypePair2.Visible = false;
            // 展开按钮自己占一行：它约 120px，和"贴图类型+两个按钮"那一单元（253px）并排会超出
            // 中间栏的可用宽度（实测 314px）—— 实测右边界 373 被裁掉一截。
            var pTypeRow2 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                                FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            pTypeRow2.Controls.Add(bTypeShow2);
            pTypeRow2.Controls.Add(pTypePair2);

            // v2.35：两个「应用」按钮单独占一行（用户要求），不再和上面的设置项挤同一行
            // v1.0：允许换行 —— 英文文案比中文宽一倍，两个按钮并排会超出中间栏，
            // 而且被挤出去的还包括后面的控件（实测「显示放大选项」直接看不见了）。
            var pActs2 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 0), WrapContents = false };
            pActs2.Controls.Add(bAllParts);
            pActs2.Controls.Add(bAllTex);

            var pUniTip2 = new Label
            {
                // 手动断行：AutoSize 的Label 不会自动折行，太长会被容器**直接截掉**
                // （实测这句长 602px，而中间列只有 301px）。
                Text = "左边几项由两个按钮套到部位/贴图\n（贴图里的「跟随」= 用这里的值）\n右边两个开关立刻生效（整卡）",
                AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(3, 0, 3, 3)
            };

            // 顺序：输出 选项（细项）→ 不匹配的服装怎么处理
            pOut.Controls.Add(rOut);
            pOut.Controls.Add(rSuf);
            pOut.Controls.Add(rChk);
            pOut.Controls.Add(rQ);
            pOut.Controls.Add(rSub);
            pOut.Controls.Add(rAllSim);
            pOut.Controls.Add(rCopy);
            gbOut2.Controls.Add(pOut);
            left.Controls.Add(gbOut2, 0, 1);
            var pLeftStat2 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 3, 3, 0) };
            lblP2Status.Dock = DockStyle.Fill;
            lblP2Status.AutoSize = false;
            lblP2Status.TextAlign = ContentAlignment.TopLeft;
            pLeftStat2.Controls.Add(lblP2Status);
            left.Controls.Add(pLeftStat2, 0, 2);
            t.Controls.Add(left, 0, 0);
            leftColCtl = left;
            left2 = left;

            // ---- 中列：部位表 ----
            var gbGrid = new GroupBox { Text = "2) 部位一览", Dock = DockStyle.Fill };
            gridParts.Dock = DockStyle.Fill;
            gridParts.AllowUserToAddRows = false;
            gridParts.AllowUserToDeleteRows = false;
            gridParts.RowHeadersVisible = false;
            gridParts.BackgroundColor = Color.White;
            gridParts.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            gridParts.AllowUserToResizeRows = false;                   // v2.24：行高不许拖（容易误触）
            gridParts.ScrollBars = ScrollBars.Both;
            gridParts.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            gridParts.MultiSelect = false;
            gridParts.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "启用", FillWeight = 4, MinimumWidth = 42, SortMode = DataGridViewColumnSortMode.NotSortable });
            gridParts.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "部位", ReadOnly = true, FillWeight = 20, MinimumWidth = 88 });
            // v2.28：列头「数量」是 2 个汉字≈ 40px，MinimumWidth 不能只给 30（表格被挤到最小宽度时
            // 会把表头裁成「数…」）；「独占贴图」4 个汉字同理给 78。
            gridParts.Columns.Add(new DataGridViewTextBoxColumn { Name = "cnt", HeaderText = "数量", ReadOnly = true, FillWeight = 4, MinimumWidth = 46 });
            gridParts.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大小", ReadOnly = true, FillWeight = 9, MinimumWidth = 50 });
            gridParts.Columns.Add(new DataGridViewTextBoxColumn { Name = "own", HeaderText = "独占贴图", ReadOnly = true, FillWeight = 10, MinimumWidth = 78 });
            gridParts.Columns.Add(new DataGridViewTextBoxColumn { Name = "mat", HeaderText = "材质", ReadOnly = true, FillWeight = 20, MinimumWidth = 48 });
            var cbFmt = new DataGridViewComboBoxColumn { Name = "fmt", HeaderText = "处理方式", FillWeight = 12, MinimumWidth = 68, FlatStyle = FlatStyle.Flat };
            cbFmt.Items.AddRange("原样不动", "PNG", "JPEG", "自动");
            var cbSize = new DataGridViewComboBoxColumn { Name = "max", HeaderText = "最大边", FillWeight = 9, MinimumWidth = 58, FlatStyle = FlatStyle.Flat };
            cbSize.Items.AddRange("不缩放", "256", "512", "1024", "2048", "4096");   // v2.39：与 P2Sizes 同序
            gridParts.Columns.Add(cbFmt);
            gridParts.Columns.Add(cbSize);
            // 按部位单独指定「锐化强度 / 灰度 2 通道」（默认「跟随」= 用一键统一里那套值）。
            // 列头写全「灰度2通道」：它和一键统一里的「灰度2通道」、第 1 页的「灰度用 2 通道」
            // 是**同一件事**（PngEnc.UseGrayAlpha），只是作用范围不同。
            var cbSh = new DataGridViewComboBoxColumn { Name = "sh", HeaderText = "锐化", FillWeight = 9, MinimumWidth = 54, FlatStyle = FlatStyle.Flat };
            cbSh.Items.AddRange(SharpenItems);
            var cbShm = new DataGridViewComboBoxColumn { Name = "shm", HeaderText = "锐化算法", FillWeight = 10, MinimumWidth = 76, FlatStyle = FlatStyle.Flat };
            cbShm.Items.AddRange(ShModeItems);
            cbShm.ToolTipText = "这张贴图用哪种锐化算法；「跟随」= 用一键统一里设的值，再跟随第 1 页的全局方式。";
            var cbGa = new DataGridViewComboBoxColumn { Name = "ga", HeaderText = "灰度2通道", FillWeight = 8, MinimumWidth = 62, FlatStyle = FlatStyle.Flat };
            cbGa.Items.AddRange(GrayItems);
            L.Tip(cbGa, TipGray);
            // v2.34：放大列。**默认「跟随」**（不表态）—— 否则这一列会把第 1 页类型表里设的 up 顶掉。
            var cbUp = new DataGridViewComboBoxColumn { Name = "up", HeaderText = "放大", FillWeight = 7, MinimumWidth = 58, FlatStyle = FlatStyle.Flat };
            cbUp.Items.AddRange(UpItems);
            cbUp.ToolTipText = "这一部位的贴图整数倍放大（×2/×4）。「跟随」= 用第 1 页类型表里该类设的值。只在彩色类生效；受「最大边」与 4096px 硬上限约束，装不下自动降级。代价很大：实测全彩色类 ×2 → 整卡 +36%（单卡 0%~+142%）。";
            cbSh.ToolTipText = "这张贴图的锐化强度；「跟随」= 用一键统一里设的值。";
            gridParts.Columns.Add(cbSh);
            gridParts.Columns.Add(cbShm);
            gridParts.Columns.Add(cbGa);
            gridParts.Columns.Add(cbUp);
            upUiCols.Add(cbUp);          // v2.34：默认隐藏
            MakeColumnsResizable(gridParts, "gridParts");   // v2.37：列宽可拖动
            gridParts.SelectionChanged += delegate { FillTexList(); };
            // v2.29：点表头排序（部位固定顺序；数量/大小/独占贴图=第一次点从大到小、无贴图的永远在后）
            HookHeaderSort(gridParts, st2, delegate { FillTexList(); });
            gridParts.CurrentCellDirtyStateChanged += delegate
            {
                if (gridParts.IsCurrentCellDirty) gridParts.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            gridParts.CellValueChanged += delegate { FillTexList(); };
            gridParts.DataError += delegate { };                   // 别让下拉框取值异常弹框
            // 一键统一放在部位行**上方**（和人物卡页一样）：位置够宽，标签和选择框能成对排下来
            var gbWrap2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            gbWrap2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            gbWrap2.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 一键统一（设置项）
            gbWrap2.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // v2.35：两个「应用」按钮单独一行
            gbWrap2.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // v2.38：按贴图类型统一（展开栏）
            gbWrap2.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 说明
            gbWrap2.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 部位表
            gbWrap2.Controls.Add(pUni2, 0, 0);
            gbWrap2.Controls.Add(pActs2, 0, 1);
            gbWrap2.Controls.Add(pTypeRow2, 0, 2);
            gbWrap2.Controls.Add(pUniTip2, 0, 3);
            gbWrap2.Controls.Add(gridParts, 0, 4);
            gbGrid.Controls.Add(gbWrap2);
            t.Controls.Add(gbGrid, 2, 0);

            // ---- 贴图明细（可直接改单张贴图的规则）---
            var gbTex = new GroupBox { Text = "3) 选中部位的贴图明细", Dock = DockStyle.Fill };
            gridTex.Dock = DockStyle.Fill;
            gridTex.AllowUserToAddRows = false;
            gridTex.AllowUserToDeleteRows = false;
            gridTex.RowHeadersVisible = false;
            gridTex.BackgroundColor = Color.White;
            gridTex.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            gridTex.AllowUserToResizeRows = false;                     // v2.24：行高不许拖（容易误触）
            gridTex.ScrollBars = ScrollBars.Both;
            gridTex.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            gridTex.MultiSelect = false;
            gridTex.Columns.Add(new DataGridViewTextBoxColumn { Name = "tid", HeaderText = "TexID", ReadOnly = true, FillWeight = 6, MinimumWidth = 48 });
            gridTex.Columns.Add(new DataGridViewTextBoxColumn { Name = "tcls", HeaderText = "类型", ReadOnly = true, FillWeight = 8, MinimumWidth = 54 });
            gridTex.Columns.Add(new DataGridViewTextBoxColumn { Name = "tdim", HeaderText = "尺寸", ReadOnly = true, FillWeight = 6, MinimumWidth = 56 });
            // v2.30：列头「字节」→「大小」（和部位表同名）；点 TexID / 尺寸 / 大小 都按**数值**排
            gridTex.Columns.Add(new DataGridViewTextBoxColumn { Name = "tbytes", HeaderText = "大小", ReadOnly = true, FillWeight = 8, MinimumWidth = 62 });
            gridTex.Columns.Add(new DataGridViewTextBoxColumn { Name = "tfmt", HeaderText = "原格式", ReadOnly = true, FillWeight = 6, MinimumWidth = 54 });
            gridTex.Columns.Add(new DataGridViewTextBoxColumn { Name = "tshare", HeaderText = "共用情况", ReadOnly = true, FillWeight = 20, MinimumWidth = 70 });
            var cbTF = new DataGridViewComboBoxColumn { Name = "trule", HeaderText = "处理方式", FillWeight = 12, MinimumWidth = 74, FlatStyle = FlatStyle.Flat };
            cbTF.Items.AddRange(TexFmtItems);
            var cbTS = new DataGridViewComboBoxColumn { Name = "tsize", HeaderText = "最大边", FillWeight = 10, MinimumWidth = 62, FlatStyle = FlatStyle.Flat };
            cbTS.Items.AddRange(TexSizeItems);
            var cbTSh = new DataGridViewComboBoxColumn { Name = "tsh", HeaderText = "锐化", FillWeight = 9, MinimumWidth = 54, FlatStyle = FlatStyle.Flat };
            cbTSh.Items.AddRange(TexSharpenItems);
            var cbTShm = new DataGridViewComboBoxColumn { Name = "tshm", HeaderText = "锐化算法", FillWeight = 10, MinimumWidth = 76, FlatStyle = FlatStyle.Flat };
            cbTShm.Items.AddRange(TexShModeItems);
            var cbTGa = new DataGridViewComboBoxColumn { Name = "tga", HeaderText = "灰度2通道", FillWeight = 8, MinimumWidth = 62, FlatStyle = FlatStyle.Flat };
            cbTGa.Items.AddRange(TexGrayItems);
            L.Tip(cbTGa, TipGray);
            gridTex.Columns.Add(cbTF);
            gridTex.Columns.Add(cbTS);
            gridTex.Columns.Add(cbTSh);
            gridTex.Columns.Add(cbTShm);
            gridTex.Columns.Add(cbTGa);
            var cbTUp = new DataGridViewComboBoxColumn { Name = "tup", HeaderText = "放大", FillWeight = 7, MinimumWidth = 64, FlatStyle = FlatStyle.Flat };
            cbTUp.Items.AddRange(TexUpItems);
            cbTUp.ToolTipText = "这一张贴图自己的放大倍率。「跟随部位」= 用所属部位的设置。";
            gridTex.Columns.Add(cbTUp);
            upUiCols.Add(cbTUp);         // v2.34：默认隐藏
            gridTex.Columns.Add(new DataGridViewTextBoxColumn { Name = "tfinal", HeaderText = "最终处理", ReadOnly = true, FillWeight = 16, MinimumWidth = 80 });
            MakeColumnsResizable(gridTex, "gridTex");   // v2.37：列宽可拖动（放在最后一列加完之后）
            gridTex.CurrentCellDirtyStateChanged += delegate
            {
                if (gridTex.IsCurrentCellDirty) gridTex.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            gridTex.CellValueChanged += delegate { TexOvChanged(); };
            gridTex.DataError += delegate { };
            var texBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            // 单列也要显式给Percent：不给的话隐含列是 AutoSize，会按最宽子控件的"需要宽度"撑开
            // （表现：GroupBox/表格比容器宽、被右边切掉一截）
            texBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            texBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            texBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
            texBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));      // 预览框默认高度（可上下拖）
            gridTex.Dock = DockStyle.Fill;
            texBox.Controls.Add(gridTex, 0, 0);
            var prevP2 = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
            prevP2.Controls.Add(picP2); prevP2.Controls.Add(lblP2Prev);
            // v2.27：「独立窗口」按钮钉在预览框右上角（滚轮缩放 / 拖动平移 / 跟着换图）
            btnBigP2.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            L.Tip(btnBigP2, "把这张贴图放进独立窗口放大看：滚轮缩放、按住左键拖动。主界面换贴图时窗口跟着换。");
            btnBigP2.Click += delegate { OpenBigPreview(picP2, gridTex, txtP2Card.Text.Trim()); };
            prevP2.Controls.Add(btnBigP2);
            // ⚠ 必须显式 BringToFront：picP2 是 Dock=Fill 的兄弟控件，WinForms 里后 Add 的控件
            //    z 序在后面，会被 Fill 的 PictureBox 盖住（首次布局的 Resize 不一定来得及调 z 序）。
            btnBigP2.Location = new Point(Math.Max(0, prevP2.ClientSize.Width - btnBigP2.Width - 6), 6);
            btnBigP2.BringToFront();
            prevP2.Resize += delegate
            {
                btnBigP2.Location = new Point(Math.Max(0, prevP2.ClientSize.Width - btnBigP2.Width - 6), 6);
                btnBigP2.BringToFront();
            };
            texBox.Controls.Add(prevP2, 0, 2);
            gbTex.Controls.Add(texBox);
            spPrev2 = MakeRowSplitter(texBox, 1, 0, 2, 140, 70);              // 明细 | 预览：上下拖
            gridTex.SelectionChanged += delegate
            {
                if (texFilling) return;                 // v2.30：重填/排序期间不重复刷
                texSelFired++;
                UiLastPreview = "fired row=" + (gridTex.CurrentRow == null ? "?" : (gridTex.CurrentRow.Tag as string ?? "?"));
                UpdatePreview(picP2, lblP2Prev, gridTex, txtP2Card.Text.Trim());
            };
            // v2.30：明细表也接管表头点击（TexID / 尺寸 / 大小 按数值排；排序本身不需要联动别的表）
            HookHeaderSort(gridTex, stTex2, null);
            lblP2Prev.Text = "点一行贴图即可预览（上方灰条可拖动调高度）";
            t.Controls.Add(gbTex, 4, 0);

            // ---- 右列：日志（独立整块，可折叠）---
            logP2.Dock = DockStyle.Fill;
            logWrap2 = new Panel { Dock = DockStyle.Fill };
            var pLogHead2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            pLogHead2.Controls.Add(new Label { Text = "日志", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left });
            logWrap2.Controls.Add(logP2); logWrap2.Controls.Add(pLogHead2);
            t.Controls.Add(logWrap2, 6, 0);
            MakeSplitter(t, 1, 0, 2, false);                       // 左列 | 部位一览
            MakeSplitter(t, 3, 2, 4, true);                        // 部位一览 | 贴图明细（按比例）
            spLog2 = MakeSplitter(t, 5, 4, 6, false);              // 贴图明细 | 日志
            HookLogSplitter(spLog2, t, 6);                          // 仍然可以左右拖动（只是不再"放大优先给日志"）
            spTab2Left = (Panel)t.GetControlFromPosition(1, 0);    // 取回引用（自检要用同一条拖动逻辑）
            spTab2Mid = (Panel)t.GetControlFromPosition(3, 0);

            // ---- 底部动作条（日志开关摆在最右边）---
            var actRow = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 2 };
            actRow2 = actRow;
            actRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // v1.1.3：不要 Dock=Fill —— 它配 AutoSize 时高度由父布局给、不会因换行而变高，
            // 于是按钮换到第二行就被裁掉（用户反馈：记住列宽/恢复默认列宽被挤到主按钮下面看不见）。
            var pAct = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                             Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            MakeMainButton(btnP2Run, "▶  单服装卡细分压缩", Font);       // 与第 1 页「开始压缩」同一套样式
            btnP2Run.Click += delegate { DoPartsRun(); };
            btnP2Cancel.Click += delegate { cancelFlag = true; lblP2Status.Text = "正在取消…"; };
            var bSavePf = new Button { Text = "保存预设…", AutoSize = true };
            bSavePf.Click += delegate { SavePresetUi(); };
            var bLoadPf = new Button { Text = "读取预设…", AutoSize = true };
            bLoadPf.Click += delegate { LoadPresetUi(); };
            var bPfDir = new Button { Text = "打开预设目录", AutoSize = true };
            bPfDir.Click += delegate { PreOpenDir(PresetIo.KindCoord); };
            var bOpen2 = new Button { Text = "打开输出目录", AutoSize = true };
            bOpen2.Click += delegate
            {
                if (Directory.Exists(txtP2Out.Text.Trim()))
                    System.Diagnostics.Process.Start("explorer.exe", txtP2Out.Text.Trim());
            };
            pAct.Controls.Add(btnP2Run); pAct.Controls.Add(btnP2Cancel);
            pAct.Controls.Add(bSavePf); pAct.Controls.Add(bLoadPf); pAct.Controls.Add(bPfDir);
            // v2.38：记住/恢复列宽，放在「打开输出目录」旁边
            var bColSave = new Button { Text = "记住列宽", AutoSize = true };
            bColSave.Click += delegate { SaveColWidths(); };
            var bColReset = new Button { Text = "恢复默认列宽", AutoSize = true };
            bColReset.Click += delegate { ResetColWidths(); };
            L.Tip(bColSave, "把四个表格的当前列宽记下来，下次启动自动套用。");
            L.Tip(bColReset, "忘掉记住的列宽，四个表格回到默认比例。");
            pAct.Controls.Add(bOpen2);
            pAct.Controls.Add(bColSave);
            pAct.Controls.Add(bColReset);
            btnLog2.Click += delegate { LogBtnClicked(2); };
            actRow.Controls.Add(pAct, 0, 0);
            actRow.Controls.Add(btnLog2, 1, 0);
            outer.Controls.Add(actRow, 0, 1);
            page.Controls.Add(outer);

            // 拖到这一页的任何地方 →当成"选这张卡"
            foreach (Control c in new Control[] { page, txtP2Card, gridParts, gridTex, logP2 })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnterAny;
                c.DragDrop += delegate (object s, DragEventArgs e)
                {
                    if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                    var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (paths == null || paths.Length == 0) return;
                    var f = paths[0];
                    if (Directory.Exists(f))
                    {
                        var lst = TexTool.Enumerate(f, null, false);
                        if (lst.Count == 0) { MessageBox.Show(L.Tr("这个文件夹里没有 .png。")); return; }
                        f = lst[0].Src;
                    }
                    txtP2Card.Text = f;
                    LoadParts();
                };
            }
            return page;
        }


        /// <summary>第1 页两块"内容定高"的容器：GroupBox / Panel 的AutoSize 在 Dock 组合下会返回
        /// 当前尺寸（等于永不收缩），所以高度改成从内容算一次，避免整页被撑高、把底部几行挤掉。</summary>
        GroupBox gb1;
        TableLayoutPanel t1Ref;
        Panel pStat1;
        TableLayoutPanel pActRow1;

        void FitTab1()
        {
            try
            {
                if (gb1 != null && t1Ref != null)
                {
                    int h = ContentBottom(t1Ref) + 24;                  // 24 ≈ 边框 + 组标题
                    gb1.Height = h;
                    rootT1.RowStyles[0].SizeType = SizeType.Absolute;   // 行高写死，别让 AutoSize 行去问
                    rootT1.RowStyles[0].Height = h + 6;                 // GroupBox 的PreferredSize（它会返回当前尺寸）
                }
                if (pStat1 != null)
                {
                    int h = ContentBottom(pStat1) + 4;
                    pStat1.Height = h;
                    rootT1.RowStyles[7].SizeType = SizeType.Absolute;
                    rootT1.RowStyles[7].Height = h + 6;
                }
                if (pActRow1 != null)
                {
                    // 按钮行同样是 Dock=Fill + AutoSize 容器 →会虚高（实测 171px），必须写死
                    int h = 0;
                    foreach (Control k in pActRow1.Controls)
                        h = Math.Max(h, k.PreferredSize.Height + k.Margin.Vertical);
                    h = Math.Max(34, h);
                    pActRow1.Height = h;
                    rootT1.RowStyles[6].SizeType = SizeType.Absolute;
                    rootT1.RowStyles[6].Height = h + 4;
                }
            }
            catch { }
        }

        /// <summary>容器里最下面那个子控件的底边（用它算"内容真正需要多高，比 PreferredSize 准）。</summary>
        static int ContentBottom(Control c)
        {
            int b = 0;
            foreach (Control k in c.Controls) b = Math.Max(b, k.Bottom + k.Margin.Bottom);
            return b;
        }

        /// <summary>第 2/3 页左列：两个 GroupBox 的 AutoSize 会撑宽（盖过 Dock）→ 宽度交给 Dock，
        /// 高度用「内容底边」写进行高。</summary>
        static void FitLeftColumn(TableLayoutPanel left, GroupBox a, GroupBox b)
        {
            try
            {
                if (left == null || a == null || b == null) return;
                Apply(left, 0, a);
                Apply(left, 1, b);
            }
            catch { }
        }

        static void Apply(TableLayoutPanel left, int row, GroupBox gb)
        {
            Control inner = gb.Controls.Count > 0 ? gb.Controls[0] : null;
            if (inner != null) inner.PerformLayout();          // 先让内层流式布局把子控件摆好，再量高度
            int h = (inner == null ? 80 : ContentBottom(inner)) + 24;
            gb.Height = h;
            gb.MinimumSize = new Size(0, h);
            left.RowStyles[row].SizeType = SizeType.Absolute;
            left.RowStyles[row].Height = h + 6;
        }

        /// <summary>底部动作条：TableLayoutPanel 的AutoSize+Dock 会虚高（实测 89px 里只有 33px 是内容）
        /// → 高度按内容写死，并把它所在那一行也写死（AutoSize 行问子控件的 PreferredSize 问不出真值）。</summary>
        static void FitActionRow(TableLayoutPanel actRow)
        {
            if (actRow == null) return;
            int h = 0;
            foreach (Control k in actRow.Controls) h = Math.Max(h, k.PreferredSize.Height + k.Margin.Vertical + 6);
            h = Math.Max(34, h);
            actRow.Height = h;
            var par = actRow.Parent as TableLayoutPanel;
            if (par != null)
            {
                int r = par.GetRow(actRow);
                if (r >= 0 && r < par.RowStyles.Count)
                {
                    par.RowStyles[r].SizeType = SizeType.Absolute;
                    par.RowStyles[r].Height = h + 4;
                }
            }
        }

        void FitTabs23()
        {
            // 两遍：第一遍改高之后子控件会重新排版（标签可能换行变高），再量一次才准
            for (int pass = 0; pass < 2; pass++)
            {
                FitLeftColumn(left2, gbIn2, gbOut2);
                FitLeftColumn(left3, gbIn3, gbOut3);
                if (left2 != null) left2.PerformLayout();
                if (left3 != null) left3.PerformLayout();
            }
            FitActionRow(actRow2);
            FitActionRow(actRow3);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitTab1();
            // 布局稳定了，才让"日志列优先变宽的逻辑开始接管（并在这时记下主区的够用宽度）
            logArmed = true;
            foreach (var lc in logCols) ApplyLogCol(lc);
            if (pendingStartup != null)
            {
                var p = pendingStartup;
                pendingStartup = null;
                AcceptPaths(p, true);          // 拖到 EXE 图标上/ 命令行带路径 → 直接读取
            }
        }


        // ---------------- 拖入 ----------------
        void OnDragEnterAny(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        void OnDragDropAny(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null || paths.Length == 0) return;
            AcceptPaths(paths, true);
        }

        /// <summary>拖入/启动参数统一入口：读卡（可选立即扫描）并显示卡片信息。</summary>
        public void AcceptPaths(string[] paths, bool autoScan)
        {
            var files = new List<string>();
            var dirs = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) dirs.Add(p);
                else if (File.Exists(p) && p.ToLowerInvariant().EndsWith(".png")) files.Add(p);
                else if (File.Exists(p)) files.Add(p);
            }
            if (files.Count == 0 && dirs.Count == 0)
            {
                MessageBox.Show(L.Tr("拖进来的东西里没有卡片文件（.png）。"));
                return;
            }
            if (files.Count == 0)
            {
                dropped.Clear();
                rbDir.Checked = true;
                settingText = true; txtIn.Text = dirs[0]; settingText = false;
            }
            else if (files.Count == 1)
            {
                dropped.Clear();
                rbFile.Checked = true;
                settingText = true; txtIn.Text = files[0]; settingText = false;
            }
            else
            {
                dropped.Clear();
                dropped.AddRange(files);
                rbFile.Checked = true;
                settingText = true; txtIn.Text = L.F("（已拖入 {0} 张卡：{1} …）", files.Count, Path.GetFileName(files[0]));
                settingText = false;
            }
            AutoOut();
            UpdateDropHint();
            if (autoScan) DoScan();
        }

        void UpdateDropHint()
        {
            if (dropped.Count > 0)
                lblDrop.Text = L.F("已拖入 {0} 张卡（{1} …）　—　点「② 开始压缩」批量处理", dropped.Count, Path.GetFileName(dropped[0]));
            else if (txtIn.Text.Trim().Length > 0)
                lblDrop.Text = "已读取：" + txtIn.Text.Trim();
            else
                lblDrop.Text = "把衣服卡（.png）或文件夹直接拖到这里读取　—　也可以拖到 EXE 图标上，或点「浏览…」";
        }

        // ---------------- UI helpers ----------------
        void PickInput()
        {
            if (rbFile.Checked)
            {
                var d = new OpenFileDialog { Filter = "Koikatu 卡片|*.png|所有文件|*.*" };
                if (d.ShowDialog() == DialogResult.OK) { dropped.Clear(); settingText = true; txtIn.Text = d.FileName; settingText = false; UpdateDropHint(); AutoOut(); lastAutoScanned = null; AutoScanIfNew(); }
            }
            else
            {
                var d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) { dropped.Clear(); settingText = true; txtIn.Text = d.SelectedPath; settingText = false; UpdateDropHint(); AutoOut(); lastAutoScanned = null; AutoScanIfNew(); }
            }
        }

        void AutoOut()
        {
            if (txtOut.Text.Trim().Length > 0) return;
            string p;
            if (dropped.Count > 0) p = dropped[0];
            else
            {
                p = txtIn.Text.Trim();
                if (p.StartsWith("（已拖入")) { if (dropped.Count == 0) return; p = dropped[0]; }
            }
            if (p.Length == 0) return;
            txtOut.Text = TexTool.DefaultOut(p);
        }

        void RowToggled(string cls)
        {
            if (on[cls].Checked)
            {
                if (fmt[cls].SelectedIndex == 0) fmt[cls].SelectedIndex = 1;
                if (size[cls].SelectedIndex == 0) size[cls].SelectedIndex = 3;
            }
            else { fmt[cls].SelectedIndex = 0; size[cls].SelectedIndex = 0; up[cls].SelectedIndex = 0; }
        }

        void ApplyPlan(Dictionary<string, Rule> plan)
        {
            foreach (var cls in Classes.Order)
            {
                Rule r;
                if (!plan.TryGetValue(cls, out r)) r = new Rule("keep", 0);
                // 顺序很重要：先勾选（勾选会触发 RowToggled 把格式/尺寸设成默认值），
                // 再用预设里的值覆盖 —— 否则"原尺寸不降"会被 RowToggled 改成默认的 1024。
                on[cls].Checked = r.Format != "keep";
                int fi = Array.IndexOf(FmtKey, r.Format); if (fi < 0) fi = 0;
                fmt[cls].SelectedIndex = fi;
                size[cls].SelectedIndex = r.Size <= 0 ? 0 : Math.Max(0, Array.IndexOf(Sizes, r.Size.ToString()));
                // 放大：-1（跟随全局）在界面上按"不放大"显示 —— 界面这一列就是权威值，
                // 存回预设时会写成明确的 0/2/4。
                up[cls].SelectedIndex = Math.Max(0, Array.IndexOf(UpKey, r.Up < 0 ? 0 : r.Up));
            }
        }

        Dictionary<string, Rule> BuildPlan()
        {
            var plan = new Dictionary<string, Rule>();
            foreach (var cls in Classes.Order)
            {
                if (!on[cls].Checked) { plan[cls] = new Rule("keep", 0); continue; }
                int sz = size[cls].SelectedIndex <= 0 ? 0 : int.Parse(Sizes[size[cls].SelectedIndex]);
                var oneRule = new Rule(FmtKey[fmt[cls].SelectedIndex], sz);
                int uv = UpKey[Math.Max(0, Math.Min(UpKey.Length - 1, up[cls].SelectedIndex))];
                // 只在真开了放大时才写 Up；选"不放大"就保持 -1（跟随全局 = 默认 0 不放大）。
                // 这样不开这个功能时，生成的 plan/预设与 2.32 **逐字节相同**（老工具也读得懂）。
                if (uv > 0) oneRule.Up = uv;
                plan[cls] = oneRule;
            }
            return plan;
        }

        List<TexTool.CardFile> Inputs(out string outDir)
        {
            outDir = txtOut.Text.Trim();
            var items = new List<TexTool.CardFile>();
            string src = dropped.Count > 0 ? dropped[0] : txtIn.Text.Trim();
            if (dropped.Count == 0)
            {
                if (src.Length == 0) { MessageBox.Show(L.Tr("请先选择卡片文件或文件夹。")); return null; }
                if (!File.Exists(src) && !Directory.Exists(src)) { MessageBox.Show(L.Tr("路径不存在：\n") + src); return null; }
            }
            bool recurse = chkRecurse.Checked;
            // 输出目录默认值必须先定下来：默认输出就在输入文件夹里面，枚举时要把它排除掉
            if (outDir.Length == 0)
            {
                outDir = TexTool.DefaultOut(src);
                txtOut.Text = outDir;
            }
            if (dropped.Count > 0)
            {
                foreach (var p in dropped)
                {
                    if (Directory.Exists(p)) items.AddRange(TexTool.Enumerate(p, outDir, recurse));
                    else if (File.Exists(p)) items.Add(new TexTool.CardFile { Src = p, Rel = "" });
                }
            }
            else if (Directory.Exists(src)) items.AddRange(TexTool.Enumerate(src, outDir, recurse));
            else items.Add(new TexTool.CardFile { Src = src, Rel = "" });

            if (items.Count == 0)
            {
                MessageBox.Show(recurse ? "该目录及其子文件夹里没有 .png 文件。\n（以 [zip] 结尾的目录会被跳过）"
                                        : "该目录下没有 .png 文件。");
                return null;
            }
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { MessageBox.Show(L.Tr("输出目录建不出来：\n") + outDir + "\n" + ex.Message); return null; }
            TexTool.MarkOut(outDir);
            return items;
        }

        /// <summary>列表里显示用的相对路径（含子文件夹时更清楚）。</summary>
        static string Rel(TexTool.CardFile cf)
        {
            return cf.Rel.Length > 0 ? Path.Combine(cf.Rel, Path.GetFileName(cf.Src)) : Path.GetFileName(cf.Src);
        }

        void Post(string kind, params object[] args)
        {
            var a = new object[args.Length + 1];
            a[0] = kind;
            Array.Copy(args, 0, a, 1, args.Length);
            q.Enqueue(a);
        }

        void Log(string s)
        {
            s = L.Tr(s);                       // v1.0.2：日志里的句子跟着语言走
            log.AppendText(s + "\r\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        void Drain()
        {
            object[] o;
            while (q.TryDequeue(out o))
            {
                switch ((string)o[0])
                {
                    case "log": Log((string)o[1]); break;
                    case "status": lblStatus.Text = (string)o[1]; break;
                    case "progress":
                        pb.Maximum = Math.Max(1, (int)o[2]);
                        pb.Value = Math.Min(pb.Maximum, (int)o[1]);
                        break;
                    case "scandata":
                        var map = (Dictionary<string, long[]>)o[1];
                        foreach (var cls in Classes.Order)
                        {
                            long[] v;
                            if (map.TryGetValue(cls, out v) && v[0] > 0)
                                info[cls].Text = L.F("{0} 张 / {1} / 最大 {2}px", v[0], TexTool.Human(v[1]), v[2]);
                            else info[cls].Text = "无";
                        }
                        break;
                    case "groupmax":
                        // v2.30：按扫描到的实际换装套数重建组过滤（超过 7 套的卡也能勾到）
                        BuildGrpFilter((int)o[1]);
                        break;
                    case "done":
                        btnRun.Enabled = true; btnCancel.Enabled = false;
                        lblStatus.Text = (string)o[1];
                        break;
                    case "log2": Log2((string)o[1]); break;
                    case "status2": lblP2Status.Text = (string)o[1]; break;
                    case "progress2":
                        pb.Maximum = Math.Max(1, (int)o[2]);
                        pb.Value = Math.Min(pb.Maximum, (int)o[1]);
                        break;
                    case "partsdata":
                        ApplyPartsScan((string)o[1], o[2] as TexTool.PartsScan, o[3] as string);
                        break;
                    case "g3data":
                        ApplyG3Scan((string)o[1], o[2] as TexTool.PartsScan, o[3] as string);
                        break;
                    case "done2":
                        Busy2(false);
                        lblP2Status.Text = (string)o[1];
                        break;
                    // 第 3 页（人物卡）：少了这几个分支的话，worker 的日志与"完成"消息会被静默丢掉，
                    // 表现就是「点了压缩按钮 → 日志不刷新 → 按钮一直灰着不可交互」
                    case "log3": Log3((string)o[1]); break;
                    case "status3": lblG3Status.Text = (string)o[1]; break;
                    case "progress3":
                        pb.Maximum = Math.Max(1, (int)o[2]);
                        pb.Value = Math.Min(pb.Maximum, (int)o[1]);
                        break;
                    case "done3":
                        Busy3(false);
                        lblG3Status.Text = (string)o[1];
                        break;
                }
            }
        }

        void Busy(bool b)
        {
            btnRun.Enabled = !b; btnCancel.Enabled = b;
        }

        // ---------------- work ----------------
        void DoScan()
        {
            string od;
            var files = Inputs(out od);
            if (files == null) return;
            // 顺手判断有没有人物卡 → 有就自动展开「人物卡组过滤」那一行
            try
            {
                var ps = new List<string>();
                foreach (var f in files) { ps.Add(f.Src); if (ps.Count >= 8) break; }
                AutoExpandGroupFilter(ps);
            }
            catch { }
            Busy(true);
            cancelFlag = false;
            lblStatus.Text = "扫描中…";
            var t = new Thread(delegate ()
            {
                try
                {
                    var tot = new Dictionary<string, long[]>();
                    int ncard = 0, ntex = 0; long big = 0;
                    // v2.25：扫描阶段也并行（压缩早就并行了，只有扫描这一段还是串行）。
                    // 14 张卡 × 0.3s 串行就是 4 秒；按核数并行能压到 1 秒内。
                    // 日志仍然**按原顺序**串行回报，所以输出顺序与旧版一致。
                    Post("status", L.F("扫描 {0} 张卡（并行）…", files.Count));
                    var scans = new ScanResult[files.Count];
                    var errs = new string[files.Count];
                    int scanJobs = 4;
                    {
                        int cores = Math.Max(2, Environment.ProcessorCount);
                        scanJobs = Math.Min(4, cores);
                        string sj = Environment.GetEnvironmentVariable("KOITEX_SCANJOBS");
                        int ov;
                        if (!string.IsNullOrEmpty(sj) && int.TryParse(sj, out ov) && ov > 0) scanJobs = ov;
                        if (scanJobs > files.Count) scanJobs = Math.Max(1, files.Count);
                    }
                    System.Threading.Tasks.Parallel.For(0, files.Count,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = scanJobs },
                        delegate (int i)
                        {
                            try { scans[i] = TexTool.Scan(files[i].Src); }
                            catch (Exception ex) { errs[i] = ex.Message; }
                        });
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (cancelFlag) break;
                        string fn = Rel(files[i]);
                        Post("progress", i + 1, files.Count);
                        Post("status", L.F("汇总 {0}/{1}: {2}", i + 1, files.Count, fn));
                        if (errs[i] != null) { Post("log", "[!] " + fn + " 读取失败: " + errs[i]); continue; }
                        ScanResult s = scans[i];
                        if (s == null || s.NTex == 0)
                        {
                            string d0 = "";
                            try { d0 = TexTool.Describe(files[i].Src, s, Card.ReadInfo(TexTool.PeekHead(files[i].Src) ?? TexTool.Peek(files[i].Src))); } catch { }
                            if (d0.Length > 0) Post("log", d0);
                            continue;
                        }
                        ncard++; ntex += s.NTex; big += s.Bytes;
                        try { Post("log", TexTool.Describe(files[i].Src, s, Card.ReadInfo(TexTool.PeekHead(files[i].Src) ?? TexTool.Peek(files[i].Src)))); }
                        catch (Exception ex) { Post("log", fn + ": " + ex.Message); }
                        foreach (var kv in s.Classes)
                        {
                            long[] a;
                            if (!tot.TryGetValue(kv.Key, out a)) { a = new long[3]; tot[kv.Key] = a; }
                            a[0] += kv.Value[0]; a[1] += kv.Value[1];
                            if (kv.Value[2] > a[2]) a[2] = kv.Value[2];
                        }
                    }
                    Post("scandata", tot);
                    // v2.30：先探测"这批卡里最多有几套换装"，让第 1 页的组过滤按实际套数重建。
                    // 有些人物卡带的换装超过原版那 7 套；以前写死 8 个复选框 → 第 8 套往后勾不上 →
                    // 压缩器那边按掩码把它们当"不参与"，那些贴图被**静默跳过**。
                    int maxGrp = 7, grpBeyond = 0;
                    {
                        int probed = 0;
                        for (int i = 0; i < files.Count && probed < 6; i++)
                        {
                            if (cancelFlag) break;
                            try
                            {
                                var psc = TexTool.ScanPartsCached(files[i].Src);
                                if (psc == null) continue;
                                bool any = false;
                                foreach (var pi in psc.Parts)
                                {
                                    if (!Chara.IsCharaKey(pi.SlotKey)) continue;
                                    any = true;
                                    int g = Chara.KeyGroup(pi.SlotKey);
                                    if (g > maxGrp) maxGrp = g;
                                    if (g > MaxGroupBit) grpBeyond++;
                                }
                                if (any) probed++;
                            }
                            catch { }
                        }
                    }
                    Post("groupmax", maxGrp);
                    if (grpBeyond > 0)
                        Post("log", L.F("提示：这批卡里有第 {0} 套以上的换装（组号超过 {1}）。压缩器用 32 位掩码，"
                            + "第 {1} 套往后没法单独开关，那些贴图会按「不参与」处理。", grpBeyond, MaxGroupBit));
                    Post("log", L.F("扫描完成：{0} 张卡含贴图，共 {1} 张贴图 / {2}", ncard, ntex, TexTool.Human(big)));
                    Post("done", L.F("扫描完成：{0} 张卡，{1} 张贴图，{2}", ncard, ntex, TexTool.Human(big)));
                }
                catch (Exception ex) { Post("log", ex.ToString()); Post("done", "扫描出错"); }
            });            t.IsBackground = true; t.Start();
        }

        /// <summary>GUI 批量的一张卡（并行算完、串行回报，保证日志顺序与旧版一致）。</summary>
        sealed class GuiRow
        {
            public string Fn, Stem, Rd, Dst, Err;
            public RepackResult R;
            public bool Cancel;
            public readonly List<string> Lines = new List<string>();
        }

        void DoRun()
        {
            string od;
            var files = Inputs(out od);
            if (files == null) return;
            var plan = BuildPlan();
            if (Classes.IsNoop(plan) &&
                MessageBox.Show(L.Tr("所有类型都是「原样不动」，输出会与原卡一样。仍要继续吗？"), "什么都没选",
                                MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            Busy(true);
            cancelFlag = false;
            log.Clear();
            Log("输出目录: " + od);
            Log(L.F("JPEG 质量 q{0}", trkQuality.Value));
            foreach (var cls in Classes.Order)
                if (plan[cls].Format != "keep")
                    Log(L.F("  {0,-10} -> {1,-5} 最大边 {2}", cls, plan[cls].Format,
                        plan[cls].Size > 0 ? plan[cls].Size.ToString() : "原尺寸"));
            Log("");

            int quality = trkQuality.Value;
            const bool protect = protectAlphaCompat;
            string suffix = txtSuffix.Text;
            bool overwrite = chkOverwrite.Checked;

            var t = new Thread(delegate ()
            {
                long before = 0, after = 0;
                int nOk = 0, nSkip = 0, nFail = 0, nCopy = 0;
                var failList = new List<string>();      // 文件名——原因
                var skipList = new List<string>();
                var partList = new List<string>();      // 文件名: 贴图级跳过
                try
                {
                    // ---- 卡级并行 ----
                    // 先把每张卡的输出路径定下来（含"覆盖同名"的改名逻辑，必须串行，保证命名确定），
                    // 再并行跑重活，最后**按原顺序**回报日志与状态 ——所以并发度不影响任何输出。
                    var rows = new GuiRow[files.Count];
                    for (int i = 0; i < files.Count; i++)
                    {
                        var row = new GuiRow();
                        row.Fn = Rel(files[i]);
                        row.Stem = Path.GetFileNameWithoutExtension(files[i].Src);
                        row.Rd = files[i].Rel.Length > 0 ? Path.Combine(od, files[i].Rel) : od;
                        rows[i] = row;
                        try { Directory.CreateDirectory(row.Rd); }
                        catch (Exception ex) { row.Err = "子目录建不出来: " + ex.Message; continue; }
                        row.Dst = Path.Combine(row.Rd, row.Stem + suffix + ".png");
                        if (File.Exists(row.Dst) && !overwrite)
                        {
                            int k = 2;
                            while (File.Exists(Path.Combine(row.Rd, string.Format("{0}{1}_{2}.png", row.Stem, suffix, k)))) k++;
                            row.Dst = Path.Combine(row.Rd, string.Format("{0}{1}_{2}.png", row.Stem, suffix, k));
                        }
                    }
                    int jobs = TexTool.CardJobs(files.Count);
                    if (jobs > 1 && files.Count > 1)
                        Log(L.F("并行处理 {0} 张卡：{1} 路（内存不够可设环境变量 KOITEX_JOBS=2）", files.Count, jobs));
                    System.Threading.Tasks.Parallel.For(0, files.Count,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = jobs },
                        delegate (int i)
                        {
                            var row = rows[i];
                            if (row.Err != null) return;
                            if (cancelFlag) { row.Cancel = true; return; }
                            try
                            {
                                row.R = TexTool.Repack(files[i].Src, row.Dst, plan, 1024, "auto", quality, 1024, null, protect, null, null, CharaMask(),
                                                       delegate (string s) { row.Lines.Add(s); },
                                                       null,
                                                       delegate { return cancelFlag; });
                            }
                            catch (Exception ex) { row.Err = ex.Message; }
                        });

                    for (int i = 0; i < files.Count; i++)
                    {
                        var row = rows[i];
                        string fn = row.Fn;
                        if (row.Cancel) break;
                        Post("status", L.F("压缩 {0}/{1}: {2}", i + 1, files.Count, fn));
                        Post("log", string.Format("===== [{0}/{1}] {2}", i + 1, files.Count, fn));
                        if (row.Err != null)
                        {
                            nFail++;
                            Post("log", "[X] 失败: " + row.Err);
                            failList.Add(fn + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + row.Err);
                            continue;
                        }
                        var r = row.R;
                        foreach (var ln in row.Lines) Post("log", ln);
                        foreach (var sk in r.Skipped) partList.Add(fn + ": " + sk);
                        if (r.Rc == 5) break;
                        if (r.Rc == 3)
                        {
                            nSkip++;
                            skipList.Add(fn + " —— " + (string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(3) : r.Reason));
                            continue;
                        }
                        if (r.Rc != 0)
                        {
                            nFail++;
                            string why = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                            Post("log", "[X] 失败: " + why);
                            failList.Add(fn + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + why);
                            continue;
                        }
                        string msg;
                        bool ok = TexTool.QuickVerify(row.Dst, out msg);
                        Post("log", ok ? "[OK] 校验通过：" + msg : "[X] 校验失败：" + msg);
                        if (!ok) { nFail++; failList.Add(fn + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + msg); continue; }
                        if (r.Copied) { nCopy++; Post("log", "[复制] " + fn + " —— 处理不了的卡，已原样复制到输出目录"); }
                        else { nOk++; before += r.CardBefore; after += r.CardAfter; }
                        Post("log", "");
                    }
                    TexTool.PrintSummary(delegate (string s) { Post("log", s); },
                        nOk, nSkip, nFail, before, after, skipList, failList, partList);
                    if (nCopy > 0) Post("log", L.F("另有 {0} 张处理不了的卡原样复制到输出目录（未压缩，体积不变）。", nCopy));
                    string sum = cancelFlag
                        ? L.F("已取消。完成 {0} 张，失败 {1} 张。", nOk, nFail)
                        : L.F("完成：成功 {0} 张，跳过（无贴图）{1} 张，原样复制 {2} 张，失败 {3} 张；{4} -> {5}（{6:0.0}%）",
                                        nOk, nSkip, nCopy, nFail, TexTool.Human(before), TexTool.Human(after),
                                        before > 0 ? 100.0 * after / before : 0);
                    Post("log", sum);
                    Post("done", sum);
                }
                catch (Exception ex) { Post("log", ex.ToString()); Post("done", "运行出错，见日志"); }
            });
            t.IsBackground = true; t.Start();
        }

        // ---- 三套选项（v2.25 起明确分成三类，别再混用）----
        //
        // ① 「一键统一」用：**不带任何"跟随"**。一键统一是"设成什么"，不是"不设"。
        // ① 「部位一览 / 组·部位表」用：也不带"跟随"（它是一键统一的结果，本身要有明确值）。
        // ① 「选中部位的贴图明细」用：**第一项必须是「跟随部位」** —— 这张贴图没单独指定时，
        //    就该跟它所属部位的设置走。只有这一层需要"没指定"这个状态。
        //
        // 引擎侧的 Rule 仍然支持"跟随"（SharpPct=-1 / SharpMode="" / GrayA=-1），
        // 老预设里这些值读进来还是"跟随"，只是新界面不再产生它们。
        static readonly string[] P2FmtKeys = { "keep", "png", "jpeg", "auto" };
        static readonly int[] P2Sizes = { 0, 256, 512, 1024, 2048, 4096 };   // v2.39：加 4096

        // ① 一键统一（格式 最大边沿用第 3 页那套，锐化/算法/灰度用下面 Uni* 那套）
        static readonly string[] G3FmtItems = { "原样不动", "PNG", "JPEG", "自动" };
        static readonly string[] G3SizeItems = { "不缩放", "256", "512", "1024", "2048", "4096" };   // v2.39

        // ②③ 表格用（部位表 / 贴图明细共用"取值"部分，明细在前面多一项「跟随部位」）
        static readonly string[] SharpenValsItems = { "不锐化", "25%", "50%", "100%", "150%", "200%", "300%" };
        static readonly int[] SharpenVals = { 0, 25, 50, 100, 150, 200, 300 };
        static readonly string[] GrayValsItems = { "开", "关" };
        static readonly int[] GrayVals = { 1, 0 };
        static readonly string[] ShModeValsItems = { "对比度自适应", "边缘感知" };
        static readonly string[] ShModeVals = { "cas", "edge" };

        // 一键统一（①）：没有「跟随」
        static readonly string[] SharpenItems = SharpenValsItems;
        static readonly string[] GrayItems = GrayValsItems;              // 表格 / 一键统一共用两态
        static readonly string[] UniGrayItems = GrayValsItems;
        static readonly string[] ShModeItems = ShModeValsItems;

        // 贴图明细（③）：第一项「跟随部位」
        static readonly string[] TexFmtItems = { "跟随部位", "原样不动", "PNG", "JPEG", "自动" };
        // v1.1.5：压缩选项的气泡说明（三页共用，尽量白话、少术语）
        internal const string TipKernel = "降分辨率时用面积平均，细节更清楚。不勾 = 用普通缩放（更快）。";
        internal const string TipColor = "彩色贴图也一起用面积平均 + 边缘锐化，观感更好。";
        internal const string TipOwn = "只被一个部位用到的贴图不跟着降 —— 它往往是那个部位唯一的高清来源。";
        internal const string TipGray = "三通道相同的灰阶图只存一个通道：体积更小，像素完全不变。";

        static readonly string[] TexSizeItems = { "跟随部位", "不缩放", "256", "512", "1024", "2048", "4096" };   // v2.39
        static readonly string[] TexSharpenItems = { "跟随部位", "不锐化", "25%", "50%", "100%", "150%", "200%", "300%" };
        static readonly string[] TexGrayItems = { "跟随部位", "开", "关" };
        static readonly string[] TexShModeItems = { "跟随部位", "对比度自适应", "边缘感知" };


        static string ShModeItemToVal(string item)
        {
            for (int i = 0; i < ShModeValsItems.Length; i++) if (ShModeValsItems[i] == item) return ShModeVals[i];
            return "";
        }
        static string ShModeValToItem(string v)
        {
            for (int i = 0; i < ShModeVals.Length; i++) if (ShModeVals[i] == (v ?? "")) return ShModeValsItems[i];
            return "对比度自适应";
        }

        // 取值↔选项：一键统一/部位表里没有「跟随」这一项，所以没选取不到时回落到具体默认值
        // （锐化 100% = 全局默认强度、算法对比度自适应 = 全局默认、灰度开 = 全局默认）。
        // 贴图明细那三个下拉框**第一项就是「跟随部位」**，它用Tex* 那套单独处理。
        static int SharpenItemToVal(string item)
        {
            for (int i = 0; i < SharpenValsItems.Length; i++) if (SharpenValsItems[i] == item) return SharpenVals[i];
            return -1;                                    // 传进来的是「跟随部位」等"不指定"→ 保持跟随
        }
        /// <summary>数值→ 部位行一键统一用的选项文案（-1（跟随）显示成默认 100%。</summary>
        static string SharpenValToItem(int v)
        {
            for (int i = 0; i < SharpenVals.Length; i++) if (SharpenVals[i] == v) return SharpenValsItems[i];
            return "100%";
        }
        static int GrayItemToVal(string item)
        {
            for (int i = 0; i < GrayValsItems.Length; i++) if (GrayValsItems[i] == item) return GrayVals[i];
            return -1;
        }
        static string GrayValToItem(int v)
        {
            for (int i = 0; i < GrayVals.Length; i++) if (GrayVals[i] == v) return GrayValsItems[i];
            return "开";
        }
        /// <summary>把三态覆盖值写进规则（-1 = 跟随 → 不动，保持默认）。v2.34 起多一项放大。</summary>
        static void SetRuleExtra(Rule r, int sh, string shMode, int ga, int up)
        {
            r.SharpenPct = sh;
            if (!string.IsNullOrEmpty(shMode)) r.SharpenMode = shMode;
            r.GrayA = ga;
            r.Up = up;
        }

        static string FmtItemToKey(string item)
        {
            if (item == "PNG") return "png";
            if (item == "JPEG") return "jpeg";
            if (item == "原样不动") return "keep";
            return "auto";
        }

        static string KeyToFmtItem(string k)
        {
            if (k == "png") return "PNG";
            if (k == "jpeg") return "JPEG";
            if (k == "keep") return "原样不动";
            return "自动";
        }

        static int SizeItemToNum(string item)
        {
            int n;
            return int.TryParse(item, out n) ? n : 0;
        }

        /// <summary>第1 页日志（追加到底部）。</summary>
        void Log1(string s)
        {
            Post("log", L.Tr(s));             // v1.0.2
        }

        void Log2(string s)
        {
            s = L.Tr(s);                       // v1.0.2
            logP2.AppendText(s + "\r\n");
            logP2.SelectionStart = logP2.TextLength;
            logP2.ScrollToCaret();
        }

        /// <summary>读卡 + 解析部位，填表。</summary>
        /// <summary>读部位：**放到后台线程**（v2.25）。
        /// 以前是同步调用，人物卡 270MB 那几百毫秒到几秒里窗口完全冻住、连"正在读取…（大卡要几秒；界面不会卡住）"
        /// 现在先刷状态、再丢给后台线程，读完经 Post/Drain 回主线程填表。</summary>
        void LoadParts()
        {
            string card = txtP2Card.Text.Trim();
            if (card.Length == 0) { MessageBox.Show(L.Tr("请先选择一张衣服卡。")); return; }
            if (!File.Exists(card)) { MessageBox.Show(L.Tr("文件不存在：\n") + card); return; }
            logP2.Clear();
            Busy2(true);
            p2Loading = true;
            lblP2Status.Text = "正在读取部位…（大卡要几秒；界面不会卡住）";
            Log2(L.F("正在读取：{0}", Path.GetFileName(card)));
            var t = new Thread(delegate ()
            {
                TexTool.PartsScan r = null; string err = null;
                try { r = TexTool.ScanPartsCached(card); }
                catch (Exception ex) { err = ex.Message; }
                Post("partsdata", card, r, err);
            });
            t.IsBackground = true; t.Start();
        }

        /// <summary>后台读完 →主线程填表（由 Drain 调用）。</summary>
        void ApplyPartsScan(string card, TexTool.PartsScan r, string err)
        {
            p2Loading = false;
            Busy2(false);
            if (err != null) { lblP2Status.Text = "读取失败：" + err; Log2("[X] " + err); return; }
            ps = r;
            if (ps == null) { lblP2Status.Text = "读取失败"; return; }
            if (ps.Err.Length > 0)
            {
                lblP2Status.Text = "读不出来：" + ps.Err;
                Log2("[X] " + ps.Err);
                gridParts.Rows.Clear(); gridTex.Rows.Clear();
                return;
            }
            if (txtP2Out.Text.Trim().Length == 0) txtP2Out.Text = TexTool.DefaultOut(card);
            // 卡名/卡种直接用刚才读进来的字节（ps.Raw）——以前是 Peek(card)，等于**再整读一遍**
            // （人物卡 270MB，多这一遍就是实打实的一次全文件读 + 一次分配）。
            var info = Card.ReadInfo(ps.Raw != null && ps.Raw.Length > 0 ? ps.Raw : TexTool.Peek(card));
            Log2(L.F("卡片：{0}（{1}）", Path.GetFileName(card), info.Name));
            Log2(L.F("共 {0} 张贴图 / {1}；有贴图的部位 {2} 个",
                ps.NTex, TexTool.Human(ps.Bytes), CountParts(ps)));
            FillPartsGrid();
            lblP2Status.Text = L.F("已读取 {0} 张贴图，{1} 个部位。勾选要压的部位后点「单服装卡细分压缩」。",
                ps.NTex, CountParts(ps));
        }

        static int CountParts(TexTool.PartsScan s)
        {
            int n = 0;
            foreach (var p in s.Parts) if (p.Tex.Count > 0) n++;
            return n;
        }

        void FillPartsGrid()
        {
            suppressPreview = true;                     // v2.25：填表期间不预览，避免几十次重复解码
            try { FillPartsGridCore(); }
            finally { suppressPreview = false; }
            UpdatePreview(picP2, lblP2Prev, gridTex, txtP2Card.Text.Trim());   // 只解一次
        }

        void FillPartsGridCore()
        {
            ClearThumbCache();                          // 换卡了 → 缩略图缓存作废
            gridParts.Rows.Clear();
            if (ps == null) return;
            foreach (var pi in ps.Parts)
            {
                int idx = gridParts.Rows.Add();
                var row = gridParts.Rows[idx];
                row.Tag = pi.SlotKey;
                bool has = pi.Tex.Count > 0;
                row.Cells["on"].Value = has;                          // 有贴图的默认勾上
                row.Cells["name"].Value = pi.Name;
                row.Cells["cnt"].Value = has ? pi.Tex.Count.ToString() : "—";
                row.Cells["size"].Value = has ? TexTool.Human(pi.Bytes) : "—";
                row.Cells["own"].Value = has ? L.F("{0} 张 / {1}", pi.OwnCount, TexTool.Human(pi.OwnBytes)) : "—";
                row.Cells["mat"].Value = pi.MatText;
                row.Cells["fmt"].Value = "自动";
                row.Cells["max"].Value = "1024";
                row.Cells["sh"].Value = "100%";               // v2.25：部位表不再有「跟随」→ 给明确默认值
                row.Cells["shm"].Value = "对比度自适应";
                row.Cells["ga"].Value = "开";
                row.Cells["up"].Value = "不放大";       // v2.35：不放大 = 不表态（跟类型表同一约定）
                if (!has)
                {
                    foreach (DataGridViewCell c in row.Cells)
                    {
                        if (c is DataGridViewCheckBoxCell || c.OwningColumn.Name == "fmt" || c.OwningColumn.Name == "max")
                            c.ReadOnly = true;
                    }
                    row.DefaultCellStyle.ForeColor = Color.Silver;
                }
            }
            if (gridParts.Rows.Count > 0) gridParts.CurrentCell = gridParts.Rows[0].Cells["name"];
            ApplySort(gridParts, st2);                          // v2.29：把上次的排序回放一遍
            FillTexList();
        }

        /// <summary>把表格里勾选的部位整理成slotRules。</summary>
        Dictionary<int, Rule> BuildSlotRules()
        {
            var d = new Dictionary<int, Rule>();
            foreach (DataGridViewRow row in gridParts.Rows)
            {
                if (row.Tag == null) continue;
                bool on = row.Cells["on"].Value is bool && (bool)row.Cells["on"].Value;
                if (!on) continue;
                int fi = P2FmtKeys.Length - 1;
                var fv = row.Cells["fmt"].Value as string;
                for (int i = 0; i < P2FmtKeys.Length; i++)
                    if (P2FmtKeys[i] == (fv == "PNG" ? "png" : fv == "JPEG" ? "jpeg" : fv == "原样不动" ? "keep" : "auto")) fi = i;
                int si = 3;
                var sv = row.Cells["max"].Value as string;
                for (int i = 0; i < P2Sizes.Length; i++) if (P2Sizes[i].ToString() == sv) si = i;
                var r = new Rule(P2FmtKeys[fi], P2Sizes[si]);
                SetRuleExtra(r, SharpenItemToVal(row.Cells["sh"].Value as string),
                             ShModeItemToVal(row.Cells["shm"].Value as string),
                             GrayItemToVal(row.Cells["ga"].Value as string),
                             UpTableToVal(row.Cells["up"].Value as string));
                d[(int)row.Tag] = r;
            }
            return d;
        }

        /// <summary>选中部位的贴图明细+ 每张贴图的最终处理（单张指定 > 部位规则 > 原样）。</summary>
        void FillTexList()
        {
            if (texFilling) return;                     // v2.30：排序回放会触发 SelectionChanged，别递归
            texFilling = true;
            suppressPreview = true;                     // 同上：明细表重建期间不预览
            try { FillTexListCore(); }
            finally { texFilling = false; suppressPreview = false; }
            UpdatePreview(picP2, lblP2Prev, gridTex, txtP2Card.Text.Trim());
        }

        void FillTexListCore()
        {
            gridTex.Rows.Clear();
            if (ps == null || gridParts.CurrentRow == null || gridParts.CurrentRow.Tag == null) return;
            var pi = ps.Find((int)gridParts.CurrentRow.Tag);
            if (pi == null) return;
            var rules = BuildSlotRules();
            foreach (var tx in pi.Tex)
            {
                Rule ov = null;
                bool hasOv;
                if (tx.Id != null && texOv.TryGetValue(tx.Id.ToString(), out ov) && ov != null) hasOv = true;
                else hasOv = false;
                int idx = gridTex.Rows.Add();
                var row = gridTex.Rows[idx];
                row.Tag = tx.Id == null ? null : tx.Id.ToString();
                row.Cells["tid"].Value = tx.Id;
                row.Cells["tcls"].Value = tx.Cls;
                row.Cells["tdim"].Value = tx.Dim > 0 ? tx.Dim + "px" : "?";
                row.Cells["tbytes"].Value = TexTool.Human(tx.Bytes);
                row.Cells["tfmt"].Value = tx.Fmt;
                row.Cells["tshare"].Value = tx.Shared ? ("与 " + tx.SlotNames + " 共用") : "独享";
                row.Cells["trule"].Value = hasOv ? KeyToFmtItem(ov.Format) : "跟随部位";
                row.Cells["tsize"].Value = hasOv ? (ov.Size > 0 ? ov.Size.ToString() : "不缩放") : "跟随部位";
                row.Cells["tsh"].Value = hasOv ? SharpenValToItem(ov.SharpenPct) : "跟随部位";
                row.Cells["tshm"].Value = hasOv
                    ? (string.IsNullOrEmpty(ov.SharpenMode) ? "跟随部位" : ShModeValToItem(ov.SharpenMode))
                    : "跟随部位";
                row.Cells["tga"].Value = hasOv ? GrayValToItem(ov.GrayA) : "跟随部位";
                // ov 为 null = 这张贴图没有单张指定 → 显示「不放大」（= 这一级不表态）。
                // 注意不能直接写 ov.Up：原来的写法有 hasOv 短路，去掉保护就会 NRE。
                row.Cells["tup"].Value = (ov == null) ? "不放大" : UpValToItem(ov.Up);
                row.Cells["tfinal"].Value = FinalText(tx, rules, hasOv, hasOv ? ov : null);
                if (hasOv) row.DefaultCellStyle.BackColor = Color.FromArgb(255, 250, 225);   // 单张指定的标黄
            }
            ApplySort(gridTex, stTex2);         // v2.30：明细表的表头排序在重填后回放
        }

        /// <summary>v2.21：界面上已经没有「通用设置」了，所以这里**什么都不塞** ——
        /// 不匹配的服装既不用通用设置压、也不会被复制（除非用户勾了「直接复制到输出目录」）。
        /// 方法保留是为了不动调用点；预设里的uniform/forceUniform 字段仍然可读可写（老预设兼容）。</summary>
        void ApplyUniformToPreset(TexTool.Preset pf)
        {
            pf.Uniform = null;
            pf.ForceUniform = false;
        }

        /// <summary>「最终处理」文案：单张指定 > 部位合并规则 > 原样（附原因）。
        /// 通用设置不参与同款卡的部位规则，所以这里不算它。</summary>
        string FinalText(TexTool.PartTex tx, Dictionary<int, Rule> rules, bool hasOv, Rule ov)
        {
            if (hasOv) return "单张指定 " + RuleText(ov);
            Rule merged; string why;
            if (TexTool.MergeSlotRules(tx.SlotKeys, rules, out merged, out why))
                return RuleText(merged) + (tx.Shared ? "（按共用规则）" : "");
            return "原样不动 —— " + why;
        }

        /// <summary>规则的简短说明：格式/边长 + 只在按贴图指定过时才写的锐化/灰度。</summary>
        static string RuleText(Rule r)
        {
            var sb = new StringBuilder(r.Format == "keep" ? "原样不动" : r.Format);
            sb.Append(r.Size > 0 ? "/" + r.Size : "/不缩放");
            if (r.SharpenPct >= 0) sb.Append(" 锐化" + (r.SharpenPct == 0 ? "关" : r.SharpenPct + "%"));
            if (r.GrayA >= 0) sb.Append(r.GrayA == 1 ? " 灰度" : " 不灰度");
            return sb.ToString();
        }

        /// <summary>明细表里改了某一行的规则 →记进覆盖表并刷新「最终处理」。</summary>
        void TexOvChanged()
        {
            if (ps == null || gridTex.Rows.Count == 0) return;
            var rules = BuildSlotRules();
            foreach (DataGridViewRow row in gridTex.Rows)
            {
                string id = row.Tag as string;
                if (id == null) continue;
                string fi = row.Cells["trule"].Value as string;
                string si = row.Cells["tsize"].Value as string;
                string sh = row.Cells["tsh"].Value as string;
                string shm = row.Cells["tshm"].Value as string;
                string ga = row.Cells["tga"].Value as string;
                string up = row.Cells["tup"].Value as string;
                var pi = ps.Find((int)gridParts.CurrentRow.Tag);
                TexTool.PartTex tx = null;
                if (pi != null) foreach (var z in pi.Tex) if (z.Id != null && z.Id.ToString() == id) { tx = z; break; }
                bool hasOv = (fi ?? "跟随部位") != "跟随部位" || (si ?? "跟随部位") != "跟随部位"
                             || (sh ?? "跟随部位") != "跟随部位" || (shm ?? "跟随部位") != "跟随部位"
                             || UpTableToVal(up) >= 0
                             || (ga ?? "跟随部位") != "跟随部位";
                Rule ov = null;
                if (hasOv)
                {
                    string f = (fi ?? "跟随部位") == "跟随部位" ? "auto" : FmtItemToKey(fi);
                    int sz = (si ?? "跟随部位") == "跟随部位" ? 1024 : SizeItemToNum(si);
                    ov = new Rule(f, sz);
                    SetRuleExtra(ov, SharpenItemToVal(sh), ShModeItemToVal(shm), GrayItemToVal(ga), UpTableToVal(up));
                    texOv[id] = ov;
                }
                else texOv.Remove(id);
                if (tx != null && row.Cells["tfinal"].Value as string != FinalText(tx, rules, hasOv, ov))
                    row.Cells["tfinal"].Value = FinalText(tx, rules, hasOv, ov);
            }
        }

        /// <summary>把当前界面状态（部位表+ 单张覆盖）收拾成一个预设对象。</summary>
        TexTool.Preset BuildPresetUi()
        {
            var pf = new TexTool.Preset { Source = Path.GetFileName(txtP2Card.Text.Trim()), IsChara = false };
            pf.Parts = BuildSlotRules();
            if (ps != null)
            {
                var sigAll = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tx in ps.All) foreach (var sg in tx.Sigs) sigAll.Add(sg);
                pf.SigSet = new List<string>(sigAll);
                pf.SigSet.Sort(StringComparer.Ordinal);
            }
            foreach (var kv in texOv)
            {
                var pt = new TexTool.PresetTex { Id = kv.Key, Rule = kv.Value };
                if (ps != null)
                    foreach (var tx in ps.All)
                        if (tx.Id != null && tx.Id.ToString() == kv.Key) pt.Sigs = new List<string>(tx.Sigs);
                pf.Textures.Add(pt);
            }
            return pf;
        }

        void SavePresetUi()
        {
            if (ps == null) { MessageBox.Show(L.Tr("请先「读取部位」。")); return; }
            var pf = BuildPresetUi();
            if (pf.Parts.Count == 0 && pf.Textures.Count == 0)
            {
                MessageBox.Show(L.Tr("现在所有部位都是「原样不动」，没有可保存的规则。"));
                return;
            }
            string card = txtP2Card.Text.Trim();
            string nm = AskName("保存为预设（预设\\coordinate）",
                "预设名称（会存成 [preset]名称_" + Path.GetFileNameWithoutExtension(card) + ".png，封面就是这张卡的卡面）", "我的部位设置");
            if (nm == null) return;
            string path = Path.Combine(PresetIo.EnsureDir(PresetIo.KindCoord), PresetIo.FileName(nm, card));
            try
            {
                ApplyUniformToPreset(pf);                       // 通用设置也一起存进去
                string json = PresetIo.Envelope(PresetIo.KindCoord, PresetIo.Sanitize(nm), Path.GetFileName(card),
                    "clothes", pf.MinMatch, pf.Uniform, pf.SigSet, pf.Parts, pf.Textures, null, (int)trkQuality.Value, protectAlphaCompat);
                PresetIo.Save(path, json, card);
                Log2(L.F("已保存预设 {0}：部位规则 {1} 条，单贴图规则 {2} 条",
                    path, pf.Parts.Count, pf.Textures.Count));
                lblP2Status.Text = "预设已保存：" + Path.GetFileName(path);
            }
            catch (Exception ex) { MessageBox.Show(L.Tr("预设存不下来：\n") + ex.Message); }
        }

        void LoadPresetUi()
        {
            if (ps == null) { MessageBox.Show(L.Tr("请先「读取部位」。")); return; }
            string dir = PresetIo.EnsureDir(PresetIo.KindCoord);
            var d = new OpenFileDialog
            {
                Filter = "预设（卡面 PNG / JSON）|*.png;*.json|卡面预设 PNG|*.png|老的 JSON 预设|*.json|所有文件|*.*",
                InitialDirectory = dir,
                Title = "读取预设（默认在 preset\\coordinate 里）"
            };
            if (d.ShowDialog() != DialogResult.OK) return;
            TexTool.Preset pf;
            try { pf = TexTool.PresetLoad(d.FileName); }
            catch (Exception ex) { MessageBox.Show(L.Tr("预设读不了：\n") + ex.Message); return; }
            ApplyPresetToUi(pf, true);
            Log2("预设文件：" + d.FileName);
        }

        /// <summary>把预设套到当前界面（部位表+ 单张贴图覆盖），并报告匹配情况。</summary>
        void ApplyPresetToUi(TexTool.Preset pf, bool logIt)
        {
            // 部位：预设里有写到的部位按预设设；没写到的按「原样不动」
            foreach (DataGridViewRow row in gridParts.Rows)
            {
                if (row.Tag == null) continue;
                int k = (int)row.Tag;
                Rule r;
                bool has = pf.Parts.TryGetValue(k, out r) && r != null;
                bool hasTex = false;
                if (ps != null) { var pi = ps.Find(k); hasTex = pi != null && pi.Tex.Count > 0; }
                if (!hasTex) continue;
                row.Cells["on"].Value = has && r.Format != "keep";
                row.Cells["fmt"].Value = has ? KeyToFmtItem(r.Format) : "原样不动";
                row.Cells["max"].Value = has && r.Size > 0 ? r.Size.ToString() : "不缩放";
            }
            // 单张贴图：按编号 + 绑定签名匹配
            texOv.Clear();
            int hit = 0;
            var missed = new List<string>();
            if (ps != null)
                foreach (var pt in pf.Textures)
                {
                    var targets = new List<TexTool.PartTex>();
                    foreach (var tx in ps.All)
                    {
                        if (tx.Id == null) continue;
                        if (tx.Id.ToString() == pt.Id) { targets.Add(tx); continue; }
                        foreach (var s in pt.Sigs) if (tx.Sigs.Contains(s)) { targets.Add(tx); break; }
                    }
                    if (targets.Count == 0) { missed.Add("TexID " + pt.Id); continue; }
                    foreach (var tx in targets) { texOv[tx.Id.ToString()] = pt.Rule; hit++; }
                }
            FillTexList();
            if (logIt)
            {
                Log2(L.F("已读取预设 {0}：部位规则 {1} 条，单贴图规则落地 {2} 张，未匹配 {3} 张",
                    pf.Source.Length > 0 ? pf.Source : "(未署名)", pf.Parts.Count, pf.Textures.Count));
                Log2(L.F("   单贴图规则落地 {0} 张；本卡没找到对应贴图 {1} 张{2}", hit, missed.Count,
                    missed.Count > 0 ? " —— " + string.Join(" / ", missed.ToArray()) : ""));
                lblP2Status.Text = L.F("预设已套到界面：单贴图命中 {0} 张，未匹配 {1} 张", hit, missed.Count);
            }
        }

        /// <summary>用当前设置批量处理一个文件夹（同款服装只有贴图不同的那些卡）。</summary>
        void BatchPresetUi()
        {
            if (ps == null) { MessageBox.Show(L.Tr("请先在一张卡上调好设置（「读取部位」）。")); return; }
            var pf = BuildPresetUi();
            if (pf.Parts.Count == 0 && pf.Textures.Count == 0)
            { MessageBox.Show(L.Tr("没有可套用的规则（先勾一个部位或给某张贴图指定规则）。")); return; }
            var fd = new FolderBrowserDialog { Description = "选一个文件夹：里面是同款服装的其它卡（只有贴图不同）" };
            if (fd.ShowDialog() != DialogResult.OK) return;
            string inDir = fd.SelectedPath;
            var cards = TexTool.Enumerate(inDir, null, chkSub.Checked);
            if (cards.Count == 0) { MessageBox.Show(L.Tr("这个文件夹（含子文件夹）里没有 .png。")); return; }
            if (MessageBox.Show(L.F("将对 {0} 张卡套用当前设置（{1}子文件夹），输出到\n{2}\n{3}[zip]\\\n\n不匹配的服装：{4}",
                    cards.Count, chkSub.Checked ? "含" : "不含", inDir, Path.GetFileName(inDir.TrimEnd('\\')),
                    chkCopySkip.Checked ? "原样复制到输出目录（不压缩）" : "跳过，不压缩也不复制"),
                    "批量套用", MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            string od = Path.Combine(inDir, Path.GetFileName(inDir.TrimEnd('\\', '/')) + "[zip]");
            BatchPresetTo(pf, inDir, od);
        }

        /// <summary>「将压缩应用到目录下的所有相似服装」走的就是这条路：不弹文件夹选择框，
        /// 目录取当前卡所在目录，输出目录用界面上填的那个（没填才退回 &lt;目录&gt;[zip]）。
        /// confirm=false 供无头自检用（不弹确认框）。</summary>
        void DoBatchPresetTo(string inDir)
        {
            DoBatchPresetTo(inDir, true);
        }
        void DoBatchPresetTo(string inDir, bool confirm)
        {
            if (ps == null) { if (confirm) MessageBox.Show(L.Tr("请先点「读取部位」。")); return; }
            var pf = BuildPresetUi();
            if (pf.Parts.Count == 0 && pf.Textures.Count == 0)
            { if (confirm) MessageBox.Show(L.Tr("没有可套用的规则（先勾一个部位或给某张贴图指定规则）。")); return; }
            var cards = TexTool.Enumerate(inDir, null, chkSub.Checked);
            if (cards.Count == 0) { if (confirm) MessageBox.Show(L.Tr("这个文件夹（含子文件夹）里没有 .png。")); return; }
            string od = txtP2Out.Text.Trim();
            if (od.Length == 0) od = Path.Combine(inDir, Path.GetFileName(inDir.TrimEnd('\\', '/')) + "[zip]");
            if (confirm && MessageBox.Show(L.F("将对 {0} 张卡套用当前设置（{1}子文件夹），输出到\n{2}\n\n不匹配的服装：{3}",
                    cards.Count, chkSub.Checked ? "含" : "不含", od,
                    chkCopySkip.Checked ? "原样复制到输出目录（不压缩）" : "跳过，不压缩也不复制"),
                    "应用到目录下的所有相似服装", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            BatchPresetTo(pf, inDir, od);
        }

        /// <summary>实际干活的部分（两条入口共用）。</summary>
        void BatchPresetTo(TexTool.Preset pf, string inDir, string od)
        {
            var cards = TexTool.Enumerate(inDir, null, chkSub.Checked);
            if (cards.Count == 0) return;
            bool overwrite = chkP2Overwrite.Checked;
            Busy2(true);
            cancelFlag = false;
            logP2.Clear();
            Log2("批量套用预设 → " + od);
            Log2(L.F("{0} 张卡；部位规则 {1} 条，单贴图规则 {2} 条",
                cards.Count, pf.Parts.Count, pf.Textures.Count));
            var rules = BuildSlotRules();
            int quality = (int)numP2Quality.Value;
            const bool protect = protectAlphaCompat;
            string suffix = txtP2Suffix.Text;
            var t = new Thread(delegate ()
            {
                long before = 0, after = 0;
                int nOk = 0, nFail = 0, hit = 0, miss = 0, nCopy = 0;   // nCopy：原样复制的单独记（不压缩）
                var failList = new List<string>();
                var skipList = new List<string>();
                try
                {
                    Directory.CreateDirectory(od);
                    TexTool.MarkOut(od);
                    for (int i = 0; i < cards.Count; i++)
                    {
                        if (cancelFlag) break;
                        var cf = cards[i];
                        string fn = cf.Rel.Length > 0 ? Path.Combine(cf.Rel, Path.GetFileName(cf.Src)) : Path.GetFileName(cf.Src);
                        string rd = cf.Rel.Length > 0 ? Path.Combine(od, cf.Rel) : od;
                        Directory.CreateDirectory(rd);
                        string stem = Path.GetFileNameWithoutExtension(cf.Src);
                        string dst = Path.Combine(rd, stem + suffix + ".png");
                        if (File.Exists(dst) && !overwrite)
                        {
                            int k = 2;
                            while (File.Exists(Path.Combine(rd, string.Format("{0}{1}_{2}.png", stem, suffix, k)))) k++;
                            dst = Path.Combine(rd, string.Format("{0}{1}_{2}.png", stem, suffix, k));
                        }
                        Post("status2", L.F("批量 {0}/{1}: {2}", i + 1, cards.Count, fn));
                        Post("log2", "===== [" + (i + 1) + "/" + cards.Count + "] " + fn);
                        RepackResult r;
                        try
                        {
                            r = TexTool.Repack(cf.Src, dst, null, 1024, "auto", quality, 1024, null, protect,
                                               rules, pf,
                                               delegate (string s) { Post("log2", s); },
                                               delegate (int d, int tt) { Post("progress2", d, tt); },
                                               delegate { return cancelFlag; });
                        }
                        catch (Exception ex)
                        {
                            nFail++; Post("log2", "[X] 失败: " + ex.Message);
                            failList.Add(fn + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + ex.Message);
                            continue;
                        }
                        hit += r.PresetMatched; miss += r.PresetMissed.Count;
                        // Rc=3（无贴图）与 Rc=6（不是同款服装）都算"跳过"，不算失败——
                        // 批量套用到一整个目录时，"不同款是**预期内**的结果，不是错误。
                        if (r.Rc == 3 || r.Rc == 6)
                        {
                            string why0 = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                            Post("log2", fn + (r.Rc == 6 ? " 跳过（不是同款服装）—— " : " 跳过（无贴图）—— ") + why0);
                            skipList.Add(fn + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + why0);
                            continue;
                        }
                        if (r.Rc != 0)
                        {
                            nFail++;
                            string why = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                            Post("log2", "[X] 失败: " + why);
                            failList.Add(fn + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + why);
                            continue;
                        }
                        string msg;
                        bool ok = TexTool.QuickVerify(dst, out msg);
                        Post("log2", ok ? "[OK] " + TexTool.Human(r.CardBefore) + " -> " + TexTool.Human(r.CardAfter)
                                        : "[X] 校验失败: " + msg);
                        if (!ok) { nFail++; failList.Add(fn + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + msg); continue; }
                        if (r.Copied) { nCopy++; Post("log2", "[复制] " + fn + " —— 不匹配，已原样复制到输出目录"); continue; }
                        nOk++; before += r.CardBefore; after += r.CardAfter;
                    }
                    TexTool.PrintSummary(delegate (string s) { Post("log2", s); }, nOk, skipList.Count, nFail,
                        before, after, null, failList, null);
                    if (nCopy > 0) Post("log2", L.F("另有 {0} 张不匹配的卡原样复制到输出目录（未压缩，体积不变）。", nCopy));
                    Post("log2", L.F("预设落地：命中 {0} 张单贴图规则；未匹配 {1} 张（同款不同版本的卡会少一些）", hit, miss));
                    string sum = L.F("批量完成：成功 {0} 张，原样复制 {1} 张，失败 {2} 张；{3} -> {4}（{5:0.0}%）→ {6}",
                        nOk, nCopy, nFail, TexTool.Human(before), TexTool.Human(after),
                        before > 0 ? 100.0 * after / before : 0, od);
                    Post("log2", sum);
                    Post("done2", sum);
                }
                catch (Exception ex) { Post("log2", ex.ToString()); Post("done2", "运行出错，见日志"); }
            });
            t.IsBackground = true; t.Start();
        }

        void DoPartsRun()
        {
            if (ps == null || ps.NTex == 0) { MessageBox.Show(L.Tr("请先点「读取部位」。")); return; }
            var rules = BuildSlotRules();
            if (rules.Count == 0)
            {
                MessageBox.Show(L.Tr("一个部位都没勾。请至少勾一个要压的部位。"));
                return;
            }
            // v2.22：勾了「将压缩应用到目录下的所有相似服装」→ 走批量套用那条路（用当前设置当预设）！
            // 同款才压、不同款按「直接复制」开关处理；不勾就只压这一件。
            if (chkAllSimilar.Checked)
            {
                string src0 = txtP2Card.Text.Trim();
                string dir = null;
                try { dir = Path.GetDirectoryName(src0); } catch { }
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show(L.Tr("读不出这张卡所在的目录，没法「应用到目录下的所有相似服装」。"));
                    return;
                }
                DoBatchPresetTo(dir, !uiNoConfirm);
                return;
            }
            string src = txtP2Card.Text.Trim();
            if (!File.Exists(src)) { MessageBox.Show(L.Tr("卡片文件不存在。")); return; }
            string od = txtP2Out.Text.Trim();
            if (od.Length == 0) { od = TexTool.DefaultOut(src); txtP2Out.Text = od; }
            try { Directory.CreateDirectory(od); TexTool.MarkOut(od); }
            catch (Exception ex) { MessageBox.Show(L.Tr("输出目录建不出来：\n") + od + "\n" + ex.Message); return; }

            string stem = Path.GetFileNameWithoutExtension(src);
            string suffix = txtP2Suffix.Text;
            string dst = Path.Combine(od, stem + suffix + ".png");
            bool overwrite = chkP2Overwrite.Checked;
            if (File.Exists(dst) && !overwrite)
            {
                int k = 2;
                while (File.Exists(Path.Combine(od, string.Format("{0}{1}_{2}.png", stem, suffix, k)))) k++;
                dst = Path.Combine(od, string.Format("{0}{1}_{2}.png", stem, suffix, k));
            }

            Busy2(true);
            cancelFlag = false;
            logP2.Clear();
            Log2("输出目录: " + od);
            Log2(L.F("单服装卡细分压缩：{0} 个部位参与，质量 q{1}",
                rules.Count, (int)numP2Quality.Value));
            foreach (var kv in SortedRules(rules))
                Log2(string.Format("   {0} → {1}{2}", Card.SlotName(kv.Key), kv.Value.Format,
                    kv.Value.Size > 0 ? "/" + kv.Value.Size : "/不缩放"));

            string card = src;
            int quality = (int)numP2Quality.Value;
            const bool protect = protectAlphaCompat;
            var pfRun = BuildPresetUi();          // 单张贴图覆盖规则走预设通道（优先级最高）
            ApplyUniformToPreset(pfRun);
            var t = new Thread(delegate ()
            {
                try
                {
                    var r = TexTool.Repack(card, dst, null, 1024, "auto", quality, 1024, null, protect, rules, pfRun,
                                           delegate (string s) { Post("log2", s); },
                                           delegate (int d, int tt) { Post("progress2", d, tt); Post("status2", L.F("压缩中… {0}/{1}", d, tt)); },
                                           delegate { return cancelFlag; });
                    var partList = new List<string>();
                    foreach (var sk in r.Skipped) partList.Add(Path.GetFileName(card) + ": " + sk);
                    if (r.Rc == 0)
                    {
                        string msg;
                        bool ok = TexTool.QuickVerify(dst, out msg);
                        Post("log2", ok ? "[OK] 校验通过：" + msg : "[X] 校验失败：" + msg);
                        if (r.Copied)
                        {
                            // 处理不了的卡 → 原样复制：不算压缩成功（体积没变），单独报一只
                            Post("log2", "[复制] 这张卡处理不了，已原样复制到输出目录（未压缩，体积不变）。");
                            TexTool.PrintSummary(delegate (string s) { Post("log2", s); }, 0, 0, 0, 0, 0, null, null, partList);
                            string sumc = "完成：原样复制（未压缩）→ " + dst;
                            Post("log2", sumc);
                            Post("done2", sumc);
                            return;
                        }
                        TexTool.PrintSummary(delegate (string s) { Post("log2", s); },
                            ok ? 1 : 0, 0, ok ? 0 : 1, r.CardBefore, r.CardAfter, null,
                            ok ? null : new List<string> { Path.GetFileName(dst) + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + msg },
                            partList);
                        string sum = ok
                            ? L.F("完成：{0} -> {1}（{2:0.0}%）→ {3}",
                                TexTool.Human(r.CardBefore), TexTool.Human(r.CardAfter),
                                100.0 * r.CardAfter / Math.Max(1, r.CardBefore), dst)
                            : "产出校验失败，别用这个文件。";
                        Post("log2", sum);
                        Post("done2", sum);
                        return;
                    }
                    string why = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                    Post("log2", "[X] 失败 —— " + why);
                    TexTool.PrintSummary(delegate (string s) { Post("log2", s); }, 0, r.Rc == 3 ? 1 : 0, r.Rc == 3 ? 0 : 1,
                        0, 0, r.Rc == 3 ? new List<string> { Path.GetFileName(card) + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + why } : null,
                        r.Rc == 3 ? null : new List<string> { Path.GetFileName(card) + " —— 如要原样保留这张卡，勾上「不匹配的服装直接复制到输出目录」" + why }, partList);
                    Post("done2", "失败：" + why);
                }
                catch (Exception ex) { Post("log2", ex.ToString()); Post("done2", "运行出错，见日志"); }
            });
            t.IsBackground = true; t.Start();
        }

        static List<KeyValuePair<int, Rule>> SortedRules(Dictionary<int, Rule> d)
        {
            var l = new List<KeyValuePair<int, Rule>>(d);
            l.Sort(delegate (KeyValuePair<int, Rule> a, KeyValuePair<int, Rule> b) { return a.Key.CompareTo(b.Key); });
            return l;
        }

        void Busy2(bool b)
        {
            btnP2Run.Enabled = !b; btnP2Cancel.Enabled = b;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            pump.Stop();
            base.OnFormClosed(e);
        }

        // ---- 供uitest 自检读取的内部状态 ----
        /// <summary>uitest 用：无头触发界面上的「开始压缩」按钮走的同一条路径（DoRun）</summary>
        public void UiRun() { DoRun(); }
        public string UiStatus { get { return lblStatus.Text; } }
        public string UiInputText { get { return txtIn.Text; } }
        public string UiOutDir { get { return txtOut.Text; } }
        public string UiDropText { get { return lblDrop.Text; } }
        public string UiLogText { get { return log.Text; } }
        public string UiDropRect { get { return lblDrop.Bounds.ToString(); } }
        public string UiContentRect
        {
            get { return Controls.Count > 1 ? Controls[0].Bounds.ToString() : "-"; }
        }
        /// <summary>自检用：放大选项的展开/收起（三个页面一起），并回读各页面控件的可见性。</summary>
        public string UiShowUp(bool? on)
        {
            if (on.HasValue) SetUpUiVisible(on.Value);
            int n1 = 0, v1 = 0, n2 = 0, v2 = 0, n3 = 0, v3 = 0, nc = 0, vc = 0;
            foreach (var c in upUiCtrls) { n1++; if (c.Visible) v1++; }
            foreach (var c in upUiCtrls2) { n2++; if (c.Visible) v2++; }
            foreach (var c in upUiCtrls3) { n3++; if (c.Visible) v3++; }
            foreach (var c in upUiCols) { nc++; if (c.Visible) vc++; }
            return L.F("展开={0} 第1页 {1}/{2} 可见 第2页 {3}/{4} 第3页 {5}/{6} 表格列 {7}/{8} 列宽={9}",
                upUiVisible, v1, n1, v2, n2, v3, n3, vc, nc,
                t2UpColStyle == null ? -1 : (int)t2UpColStyle.Width);
        }

        public string UiPlanSummary()
        {
            var sb = new StringBuilder();
            foreach (var kv in BuildPlan())
                sb.AppendFormat("{0}={1}/{2}{3} ", kv.Key, kv.Value.Format, kv.Value.Size,
                    kv.Value.Up > 0 ? "/up" + kv.Value.Up : "");
            return sb.ToString().TrimEnd();
        }

        /// <summary>自检用：设类型表的「放大」列（spec = "maintex=2,matcap=4"，0 = 不放大）。</summary>
        public string UiUpSet(string spec)
        {
            if (!string.IsNullOrEmpty(spec))
                foreach (var part in spec.Split(','))
                {
                    var kv = part.Split('=');
                    if (kv.Length != 2) continue;
                    string cls = kv[0].Trim();
                    int f;
                    if (!int.TryParse(kv[1].Trim(), out f)) continue;
                    if (!up.ContainsKey(cls)) continue;
                    up[cls].SelectedIndex = Math.Max(0, Array.IndexOf(UpKey, f));
                }
            return UiPlanSummary();
        }

        // ---- 按部位分页的无头自检入口 ----
        public int UiTabCount { get { return tabs.TabPages.Count; } }
        public List<TabPage> UiTabPageList() { var l = new List<TabPage>(); foreach (TabPage p in tabs.TabPages) l.Add(p); return l; }
        public string UiTabTitle(int i) { return i < tabs.TabPages.Count ? tabs.TabPages[i].Text : "-"; }
        public void UiSelectTab(int i) { if (i >= 0 && i < tabs.TabPages.Count) tabs.SelectedIndex = i; }

        /// <summary>自检用：改窗口大小后回报各页的日志列宽与主区宽度（验证放大优先扩日志）。
        /// 会逐页切换后再量——没显示的页不会重新布局，直接量到的值是旧的。</summary>
        public string UiResize(int w, int h)
        {
            int keep = tabs.SelectedIndex;
            ClientSize = new Size(w, h);
            Application.DoEvents();
            var sb = new StringBuilder();
            sb.AppendFormat("窗口 {0}x{1}", w, h);
            for (int p = 0; p < tabs.TabPages.Count; p++)
            {
                tabs.SelectedIndex = p;
                Application.DoEvents();
                var lc = null as LogCol;
                foreach (var z in logCols) if (z.T != null && IsAncestor(tabs.TabPages[p], z.T)) { lc = z; break; }
                if (lc == null)
                {
                    // 没有"日志优先"规则的页（第 2/3 页）：报表宽 + 日志列宽
                    var tt = p == 1 ? rootT2 : (p == 2 ? rootT3 : null);
                    if (tt == null) { sb.AppendFormat(" | 第{0}页[部位表={1} 日志={2}（不参与优先）]", p + 1, "-", "-"); continue; }
                    int lst = tt.ColumnStyles.Count - 1;
                    sb.AppendFormat(" | 第{0}页[部位表={1} 日志={2}（不参与优先）]", p + 1,
                        ColPixels(tt, 2), (int)ColPixels(tt, lst));
                    continue;
                }
                int last = lc.T.ColumnStyles.Count - 1;
                sb.AppendFormat(" | 第{0}页[日志={1} 主区={2}]", p + 1, (int)ColPixels(lc.T, last), MainNow(lc));
            }
            tabs.SelectedIndex = keep;
            Application.DoEvents();
            return sb.ToString();
        }

        static bool IsAncestor(Control parent, Control child)
        {
            for (var c = child; c != null; c = c.Parent) if (c == parent) return true;
            return false;
        }

        /// <summary>三页共用同一批全局设置"静态字段（锐化/灰度/保细节/独占保护/质量）。
        /// 第2、3 页也有这些控件，在第 1 页显示时要按**当前真实值**刷新一遍，
        /// 否则会出现在第 2 页改了锐化，回第 1 页滑条还是老数值这种假象。</summary>
        void SyncGlobalControls()
        {
            syncGlobals = true;
            try
            {
                int pct = Math.Max(0, Math.Min(200, Resample.SharpenPct));
                trkSharpen.Value = pct;
                numSharpen.Value = Math.Max(numSharpen.Minimum, Math.Min(numSharpen.Maximum, Resample.SharpenPct));
                chkGrayA.Checked = PngEnc.UseGrayAlpha;
                chkKernel.Checked = Resample.DetailMode;
                chkColor.Checked = Resample.ColorMode;      // v2.32
                chkColor2.Checked = Resample.ColorMode;
                chkColor3.Checked = Resample.ColorMode;
                chkOwn.Checked = TexTool.ProtectOwn;
                cbOwnMax.Enabled = chkOwn.Checked;
                SyncColorEnable();
            }
            finally { syncGlobals = false; }
        }
        public void UiPartsLoad(string card) { txtP2Card.Text = card; LoadParts(); }

        /// <summary>自检用：整文件读取的次数/字节数（定位"读卡慢到底读了几遍）。</summary>
        public string UiReadStat
        {
            get
            {
                return L.F("整文件读取 {0} 次 / {1:F1} MB；整文件键扫描 {2} 次 / {3}ms",
                    TexTool.FullReads, TexTool.FullReadBytes / 1048576.0, Card.KeyScanCount, Card.KeyScanMs);
            }
        }
        public void UiReadStatReset() { TexTool.FullReads = 0; TexTool.FullReadBytes = 0; Card.KeyScanCount = 0; Card.KeyScanMs = 0; }
        /// <summary>自检用：开悬浮预览窗（走界面按钮同一条路），回报它的状态。</summary>
        public string UiBigOpen(bool charaPage, int row)
        {
            if (charaPage)
            {
                if (row >= 0 && row < gridGT.Rows.Count) gridGT.CurrentCell = gridGT.Rows[row].Cells["tid"];
                Application.DoEvents();
                OpenBigPreview(picG3, gridGT, g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim());
            }
            else
            {
                if (row >= 0 && row < gridTex.Rows.Count) gridTex.CurrentCell = gridTex.Rows[row].Cells["tid"];
                Application.DoEvents();
                OpenBigPreview(picP2, gridTex, txtP2Card.Text.Trim());
            }
            Application.DoEvents();
            return prevBig == null ? "（没开起来）" : prevBig.State;
        }
        /// <summary>自检用：选中明细表第 row 行。
        /// ⚠ 这里**显式再调一次 UpdatePreview**，和真实 SelectionChanged 的处理体完全一样 ——
        /// 无头自检里程序化改 CurrentCell 不一定触发 SelectionChanged（控件不可见时就不触发），
        /// 老的自检钩子 UiP2PreviewAt 也是这么做的。</summary>
        public string UiSelectTexRow(bool charaPage, int row)
        {
            var grid = charaPage ? gridGT : gridTex;
            if (row >= 0 && row < grid.Rows.Count) grid.CurrentCell = grid.Rows[row].Cells["tid"];
            Application.DoEvents();
            if (charaPage) UpdatePreview(picG3, lblG3Prev, gridGT, g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim());
            else UpdatePreview(picP2, lblP2Prev, gridTex, txtP2Card.Text.Trim());
            Application.DoEvents();
            string cur = grid.CurrentRow == null ? "?" : (grid.CurrentRow.Tag as string ?? "?");
            string n = grid.Rows.Count.ToString();
            return L.F("表选中={0}（共 {1} 行）{2} | 预览路径={3} | 小预览说明={4} | {5}", cur, n,
                grid.CurrentCell == null ? "" : " cell=" + grid.CurrentCell.RowIndex,
                UiLastPreview + " 事件次数=" + texSelFired, charaPage ? lblG3Prev.Text : lblP2Prev.Text,
                prevBig == null ? "（悬浮窗没开）" : prevBig.State);
        }
        /// <summary>自检用：模拟滚轮（delta&gt;0 放大），回报缩放。</summary>
        public string UiBigWheel(int delta)
        {
            if (prevBig == null || prevBig.IsDisposed) return "（悬浮窗没开）";
            prevBig.ZoomBy(delta, null);
            return prevBig.State;
        }
        public string UiBigClose()
        {
            if (prevBig == null || prevBig.IsDisposed) return "（悬浮窗没开）";
            string s = prevBig.State;
            prevBig.Close();
            Application.DoEvents();
            return "已关闭；关闭前：" + s;
        }
        /// <summary>自检用：把悬浮预览窗画成 PNG（人眼复核它的布局）。</summary>
        public string UiSnapBig(string path)
        {
            if (prevBig == null || prevBig.IsDisposed) return "（悬浮窗没开）";
            Application.DoEvents();
            using (var bmp = new Bitmap(Math.Max(200, prevBig.Width), Math.Max(200, prevBig.Height)))
            {
                prevBig.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return string.Format("{0}  {1}x{2}", path, prevBig.Width, prevBig.Height);
        }

        /// <summary>逐字读取是否还在后台进行（自检要等它结束）。</summary>
        public bool UiPartsBusy { get { return p2Loading; } }

        public int UiQueueLen { get { return q.Count; } }
        bool p2Loading;
        public void UiPartsRun() { DoPartsRun(); }
        public string UiPartsStatus { get { return lblP2Status.Text; } }
        public string UiPartsLog { get { return logP2.Text; } }
        public string UiPartsOutDir { get { return txtP2Out.Text; } }
        /// <summary>自检用：改「按部位压缩」的输出目录（默认是 &lt;卡所在目录gt;\压缩输出，会污染输入目录）。</summary>
        public void UiPartsOutSet(string dir) { txtP2Out.Text = dir; }
        /// <summary>部位表的文本快照（自检断言用）：每行"启用|部位|贴图数|大小|独享|处理方式|最大边"。</summary>
        public string UiPartsGridDump()
        {
            var sb = new StringBuilder();
            foreach (DataGridViewRow row in gridParts.Rows)
            {
                if (row.Tag == null) continue;
                sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}\r\n",
                    row.Cells["on"].Value, row.Cells["name"].Value, row.Cells["cnt"].Value,
                    row.Cells["size"].Value, row.Cells["own"].Value, row.Cells["fmt"].Value, row.Cells["max"].Value,
                    row.Cells["sh"].Value, row.Cells["shm"].Value, row.Cells["ga"].Value, row.Cells["up"].Value);
            }
            return sb.ToString();
        }
        /// <summary>明细表快照：每行 "TexID"|类型|尺寸|字节|原格式|共用|处理方式|最大边|锐化|灰度|最终处理。</summary>
        public string UiPartsTexDump()
        {
            var sb = new StringBuilder();
            foreach (DataGridViewRow row in gridTex.Rows)
            {
                sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}\r\n",
                    row.Cells["tid"].Value, row.Cells["tcls"].Value, row.Cells["tdim"].Value,
                    row.Cells["tbytes"].Value, row.Cells["tfmt"].Value, row.Cells["tshare"].Value,
                    row.Cells["trule"].Value, row.Cells["tsize"].Value,
                    row.Cells["tsh"].Value, row.Cells["tshm"].Value, row.Cells["tga"].Value, row.Cells["tfinal"].Value);
            }
            return sb.ToString();
        }
        /// <summary>改明细表某一行的单张贴图规则（自检用）。fmt/size 传"跟随部位" 表示恢复跟随。</summary>
        public void UiTexSetRow(int i, string fmt, string size)
        {
            UiTexSetRow(i, fmt, size, null, null);
        }
        public void UiTexSetRow(int i, string fmt, string size, string sh, string ga)
        {
            UiTexSetRow(i, fmt, size, sh, null, ga);
        }
        public void UiTexSetRow(int i, string fmt, string size, string sh, string shm, string ga, string up = null)
        {
            if (i < 0 || i >= gridTex.Rows.Count) return;
            gridTex.Rows[i].Cells["trule"].Value = fmt;
            gridTex.Rows[i].Cells["tsize"].Value = size;
            if (sh != null) gridTex.Rows[i].Cells["tsh"].Value = sh;
            if (shm != null) gridTex.Rows[i].Cells["tshm"].Value = shm;
            if (ga != null) gridTex.Rows[i].Cells["tga"].Value = ga;
            if (up != null) gridTex.Rows[i].Cells["tup"].Value = up;
            TexOvChanged();
        }
        public void UiPresetSave(string path) { TexTool.PresetSave(BuildPresetUi(), path); }
        public void UiPresetLoad(string path) { ApplyPresetToUi(TexTool.PresetLoad(path), true); }
        // ---- 一键统一 / 批量选项（自检用）----
        public void UiCopySkip(bool on) { SetCopySkip(on); }
        /// <summary>「处理不了的卡直接复制到输出目录」：第 1、2 页共用同一个开关（勾一处两处一起变）。</summary>
        void SetCopySkip(bool on)
        {
            SkipCopy.On = on;
            syncingCopy = true;
            chkCopySkip.Checked = on;
            if (chkCopySkip1 != null) chkCopySkip1.Checked = on;
            syncingCopy = false;
        }
        public void UiSubfolders(bool on) { chkSub.Checked = on; }
        /// <summary>「不匹配的服装直接复制」只在勾了「应用到目录下的所有相似服装」时才可用。</summary>
        void SyncAllSimilar()
        {
            bool many = chkAllSimilar.Checked;
            chkCopySkip.Enabled = many;
            if (!many)
            {
                // 只压这一件时不存在不匹配，强制关掉复制开关，免得看起来像在起作用
                if (chkCopySkip.Checked) SetCopySkip(false);
            }
            if (lblP2Status != null)
                lblP2Status.Text = many ? "已开启：会连同目录下的同款服装一起压缩。" : "只压缩当前这一件。";
        }
        public void UiAllSimilar(bool on) { chkAllSimilar.Checked = on; }
        /// <summary>无头自检时把"要不要确认的弹框关掉（否则会卡住）。</summary>
        public void UiNoConfirm(bool on) { uiNoConfirm = on; }
        bool uiNoConfirm;
        public string UiAllSimilarDump
        {
            get
            {
                return L.F("相似服装={0} 复制不匹配={1}（可选={2}）",
                    chkAllSimilar.Checked, chkCopySkip.Checked, chkCopySkip.Enabled);
            }
        }
        public string UiOptionDump()
        {
            return string.Format("copySkip={0} sub={1} partsGridEnabled={2} detailProtect={3} ownProtect={4}",
                chkCopySkip.Checked, chkSub.Checked, gridParts.Enabled, Resample.DetailMode, TexTool.ProtectOwn);
        }
        /// <summary>一键统一：应用到所有部位（fmt 用「跟随部位/原样不动/PNG/JPEG/自动」）。
        /// sh/ga 传 null 表示"这两项不动（老调用点只改格式/边长时的兼容写法）。</summary>
        public void UiSetAllParts(string fmt, string size)
        {
            UiSetAllParts(fmt, size, null, null);
        }
        public string UiLastAllParts = "(未调用)";
        public void UiSetAllParts(string fmt, string size, string sh, string ga)
        {
            UiSetAllParts(fmt, size, sh, null, ga);
        }
        public void UiSetAllParts(string fmt, string size, string sh, string shm, string ga, string up = null)
        {
            int n = 0;
            foreach (DataGridViewRow row in gridParts.Rows)
            {
                if (row.Tag == null) continue;
                bool hasTex = (row.Cells["cnt"].Value as string) != "—";
                row.Cells["on"].Value = hasTex && fmt != "原样不动";
                row.Cells["fmt"].Value = fmt;
                row.Cells["max"].Value = size;
                if (sh != null) row.Cells["sh"].Value = sh;
                if (shm != null) row.Cells["shm"].Value = shm;
                if (ga != null) row.Cells["ga"].Value = ga;
                if (up != null) row.Cells["up"].Value = up;
                n++;
            }
            UiLastAllParts = L.F("UiSetAllParts({0},{1},{2},{3},{4}) 命中 {5} 行，行数 {6}",
                fmt, size, sh, shm, ga, n, gridParts.Rows.Count);
            FillTexList();
        }
        /// <summary>一键统一：应用到当前部位的全部贴图。</summary>
        public void UiSetAllTex(string fmt, string size)
        {
            UiSetAllTex(fmt, size, null, null);
        }
        public void UiSetAllTex(string fmt, string size, string sh, string ga)
        {
            UiSetAllTex(fmt, size, sh, null, ga);
        }
        public void UiSetAllTex(string fmt, string size, string sh, string shm, string ga, string up = null)
        {
            for (int i = 0; i < gridTex.Rows.Count; i++)
            {
                UiTexSetRow(i, fmt, size);
                if (sh != null) gridTex.Rows[i].Cells["tsh"].Value = sh;
                if (shm != null) gridTex.Rows[i].Cells["tshm"].Value = shm;
                if (ga != null) gridTex.Rows[i].Cells["tga"].Value = ga;
                if (up != null) gridTex.Rows[i].Cells["tup"].Value = up;
            }
            TexOvChanged();
        }
        public string UiTexOvDump()
        {
            var l = new List<string>();
            foreach (var kv in texOv)
                l.Add(kv.Key + "=" + kv.Value.Format + "/" + kv.Value.Size + RuleExtraDump(kv.Value));
            l.Sort();
            return string.Join(" ", l.ToArray());
        }
        static string RuleExtraDump(Rule r)
        {
            if (r.AllFollow) return "";
            return "[sh=" + r.SharpenPct + (string.IsNullOrEmpty(r.SharpenMode) ? "" : "," + r.SharpenMode)
                 + ",ga=" + r.GrayA + (r.Up >= 0 ? ",up=" + r.Up : "") + "]";
        }
        public void UiBatchPreset(string dir) { DoBatchPresetTo(dir, false); }

        /// <summary>选中部位表的第几行（自检用）。</summary>
        public void UiPartsSelectRow(int i)
        {
            if (i >= 0 && i < gridParts.Rows.Count) gridParts.CurrentCell = gridParts.Rows[i].Cells["name"];
        }

        // ---- 无头自检钩子（第 2 页：选中贴图 →缩略图预览）----
        public int UiTexRows { get { return gridTex.Rows.Count; } }

        public string UiP2PreviewAt(int i)
        {
            if (i < 0 || i >= gridTex.Rows.Count) return "(越界或该部位没贴图)";
            gridTex.CurrentCell = gridTex.Rows[i].Cells["tid"];
            Application.DoEvents();
            UpdatePreview(picP2, lblP2Prev, gridTex, txtP2Card.Text.Trim());
            Application.DoEvents();
            return L.F("行 {0} TexID={1} → 图={2} 说明={3}", i, gridTex.Rows[i].Cells["tid"].Value,
                picP2.Image == null ? "无" : picP2.Image.Width + "×" + picP2.Image.Height, lblP2Prev.Text);
        }

        // ---- 无头自检钩子（v2.29：表头点击排序）----
        /// <summary>自检用：模拟点第 2 页部位表的某个表头clicks 次（走的是真实点击同一条路）。</summary>
        public string UiSortParts(string col, int clicks)
        {
            for (int i = 0; i < clicks && col != null; i++) HeaderSort(gridParts, st2, col, null);
            Application.DoEvents();
            return DumpSortState(gridParts, st2, "第2页部位表") + DumpRows(gridParts) +
                   L.F("\r\n    明细：{0} 行，首行 TexID={1}", gridTex.Rows.Count,
                       gridTex.Rows.Count == 0 ? "-" : (gridTex.Rows[0].Cells["tid"].Value ?? "-"));
        }

        /// <summary>自检用：点「贴图明细」表的表头 clicks 次（真实点击同一条路）。
        /// charaPage=false → 第 2 页 gridTex；true → 第 3 页 gridGT。</summary>
        public string UiSortTex(bool charaPage, string col, int clicks)
        {
            var g = charaPage ? gridGT : gridTex;
            var st = charaPage ? stTex3 : stTex2;
            for (int i = 0; i < clicks && col != null; i++) HeaderSort(g, st, col, null);
            Application.DoEvents();
            var sb = new StringBuilder();
            sb.AppendFormat("{0}贴图明细：我记的排序 = {1}；网格 = {2}",
                charaPage ? "第3页" : "第2页",
                string.IsNullOrEmpty(st.Col) ? "无" : (st.Col + "/" + st.Dir),
                g.SortedColumn == null ? "无" : (g.SortedColumn.Name + "/" + g.SortOrder));
            int n = 0;
            foreach (DataGridViewRow r in g.Rows)
            {
                if (n++ >= 10) { sb.Append("\r\n    …"); break; }
                sb.AppendFormat("\r\n    [{0}] TexID={1} | 尺寸={2} | 大小={3}",
                    n - 1, r.Cells["tid"].Value, r.Cells["tdim"].Value, r.Cells["tbytes"].Value);
            }
            return sb.ToString();
        }

        /// <summary>自检用：模拟点第 3 页「组·部位」表的某个表头clicks 次。</summary>
        public string UiSortG3(string col, int clicks)
        {
            for (int i = 0; i < clicks && col != null; i++) HeaderSort(gridG, st3, col, null);
            Application.DoEvents();
            return DumpSortState(gridG, st3, "第3页组·部位表") + DumpRows(gridG) +
                   L.F("\r\n    明细：{0} 行，首行 TexID={1}", gridGT.Rows.Count,
                       gridGT.Rows.Count == 0 ? "-" : (gridGT.Rows[0].Cells["tid"].Value ?? "-"));
        }

        /// <summary>自检用：重填第2 页部位表（验证"排序在重填后还在不在"）。</summary>
        public string UiRefillParts()
        {
            FillPartsGrid();
            Application.DoEvents();
            return DumpSortState(gridParts, st2, "重填后") + DumpRows(gridParts);
        }

        static string DumpSortState(DataGridView g, GridSort st, string who)
        {
            var sb = new StringBuilder(who + "：");
            foreach (DataGridViewColumn c in g.Columns)
                if (c.Name == "cnt" || c.Name == "size" || c.Name == "own" || c.Name == "name")
                    sb.AppendFormat(" {0}={1}", c.HeaderText,
                        c.SortMode == DataGridViewColumnSortMode.Programmatic ? "自管" :
                        (c.SortMode == DataGridViewColumnSortMode.NotSortable ? "不可排" : "网格管"));
            // ⚠ 以**我们自己记的**为准：重填表以后 DataGridView.SortedColumn 会被清成 null，
            // 但排序其实已经回放过了（顺序是对的），拿它当判据会误报"无排序"。
            sb.AppendFormat("；我记的排序 = {0}；网格SortedColumn = {1}",
                string.IsNullOrEmpty(st.Col) ? "无" : (st.Col + "/" + st.Dir),
                g.SortedColumn == null ? "无" : (g.SortedColumn.Name + "/" + g.SortOrder));
            return sb.ToString();
        }

        static string DumpRows(DataGridView g)
        {
            var sb = new StringBuilder();
            int n = g.Rows.Count;
            for (int i = 0; i < n; i++)
            {
                // 头 6 行 + 尾 3 行：排序验证要看两头（"11/13 张有没有排到最后"）
                if (n > 10 && i >= 6 && i < n - 3) { if (i == 6) sb.Append("\r\n    —"); continue; }
                var r = g.Rows[i];
                sb.AppendFormat("\r\n    [{0}] {1} | 数量={2} | 大小={3} | 独占={4}", i,
                    r.Cells["name"].Value, r.Cells["cnt"].Value, r.Cells["size"].Value, r.Cells["own"].Value);
            }
            return sb.ToString();
        }
        /// <summary>改某一行的勾选格式/最大边（自检用）。</summary>
        public void UiPartsSetRow(int i, bool on, string fmt, string max)
        {
            if (i < 0 || i >= gridParts.Rows.Count) return;
            var row = gridParts.Rows[i];
            row.Cells["on"].Value = on;
            row.Cells["fmt"].Value = fmt;
            row.Cells["max"].Value = max;
            FillTexList();
        }
    }
}

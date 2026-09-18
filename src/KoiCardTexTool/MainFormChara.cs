using System;
using System.Collections.Generic;
using System.ComponentModel;                 // ListSortDirection（表头排序用）
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KoiCardTexTool
{
    /// <summary>第 3 页：人物卡压缩（角色本体 + 7 套换装）。
    ///
    /// 人物卡（Koikatu_F_*.png）和衣服卡不是一回事：
    ///   · 卡里自带**角色本体**的贴图/材质（身体 cf_m_body、眼线、眼白、白目…）
    ///   · 还带 7 套换装，每套各自有服装槽与饰品
    ///   · 所有贴图放在**一个** TextureDictionary 里，同张贴图会被多套换装共用
    /// 所以这一页按「组（角色本体 / 换装1..7）× 部位」列出，可以只压某一套的某些贴图。</summary>
    public partial class MainForm
    {
        TabPage BuildCharaPage()
        {
            var page = new TabPage("③ 单张人物卡压缩") { Padding = new Padding(6) };
            // 四块：左＝选卡+输出/选项，中＝组·部位，右＝贴图明细，最右＝日志（可折叠）
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
            // 单列也要显式给 Percent：不给的话隐含列是 AutoSize，会按最宽子控件的"需要宽度"撑开
            // （表现：GroupBox/表格比容器宽、被右边切掉一截）
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Padding = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 336));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitW));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitW));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SplitW));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            rootT3 = t;
            outer.Controls.Add(t, 0, 0);

            // ---- 左列：选卡 + 输出/选项 ----
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            // 单列也要显式给 Percent：不给的话隐含列是 AutoSize，会按最宽子控件的"需要宽度"撑开
            // （表现：GroupBox/表格比容器宽、被右边切掉一截）
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.AutoScroll = true;
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            gbIn3 = new GroupBox { Text = "1) 选一张人物卡（Koikatu_F_*.png）", Dock = DockStyle.Fill, AutoSize = false };
            var pIn = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(6) };
            txtG3Card.Width = 330;
            var rowB = new FlowLayoutPanel { AutoSize = true };
            var bPick = new Button { Text = "浏览…", AutoSize = true };
            bPick.Click += delegate
            {
                var d = new OpenFileDialog { Filter = "Koikatu 人物卡|Koikatu_F_*.png|所有 png|*.png" };
                if (d.ShowDialog() != DialogResult.OK) return;
                g3Cards.Clear();
                g3Cards.AddRange(d.FileNames);
                txtG3Card.Text = g3Cards.Count == 1 ? g3Cards[0] : L.F("（已选 {0} 张）", g3Cards.Count);
                G3Load();
            };
            var bLoad = new Button { Text = "② 读取", AutoSize = true };
            bLoad.Click += delegate { G3Load(); };
            rowB.Controls.Add(bPick); rowB.Controls.Add(bLoad);
            var bClear = new Button { Text = "清空", AutoSize = true };
            bClear.Click += delegate { g3Cards.Clear(); txtG3Card.Text = ""; gridG.Rows.Clear(); gridGT.Rows.Clear(); ps3 = null; lblG3Status.Text = ""; };
            rowB.Controls.Add(bClear);
            pIn.Controls.Add(new Label { Text = "卡片路径", AutoSize = true });
            pIn.Controls.Add(txtG3Card);
            pIn.Controls.Add(rowB);
            pIn.Controls.Add(new Label
            {
                Text = "读取后中间按「角色本体 / 换装1~7」列出，\n每组里再分部位；右边可以给单张贴图单独指定。\n角色的身体/脸/头发/眼睛贴图属于「角色本体」，\n和换装是分开的。",
                ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(320, 0)
            });
            gbIn3.Controls.Add(pIn);
            left.Controls.Add(gbIn3, 0, 0);

            gbOut3 = new GroupBox { Text = "4) 输出 / 选项", Dock = DockStyle.Fill, AutoSize = false };
            var pOut = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(6) };
            var rOut = new FlowLayoutPanel { AutoSize = true };
            rOut.Controls.Add(new Label { Text = "输出目录", AutoSize = true, Anchor = AnchorStyles.Left });
            txtG3Out.Width = 200;
            var bOut = new Button { Text = "浏览…", AutoSize = true };
            bOut.Click += delegate
            {
                var d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) txtG3Out.Text = d.SelectedPath;
            };
            rOut.Controls.Add(txtG3Out); rOut.Controls.Add(bOut);
            var rSuf = new FlowLayoutPanel { AutoSize = true };
            rSuf.Controls.Add(new Label { Text = "文件名后缀", AutoSize = true, Anchor = AnchorStyles.Left });
            txtG3Suffix.Text = "[zip]"; txtG3Suffix.Width = 80;
            rSuf.Controls.Add(txtG3Suffix);
            var rChk = new FlowLayoutPanel { AutoSize = true };
            rChk.Controls.Add(chkG3Overwrite);
            var rQ = new FlowLayoutPanel { AutoSize = true };
            rQ.Controls.Add(new Label { Text = "JPEG 质量", AutoSize = true, Anchor = AnchorStyles.Left });
            rQ.Controls.Add(numG3Quality);
            // v2.23：这一页只处理**一张**人物卡（要多张请用第 1 页批量压缩），
            // 所以「拖入目录时含子文件夹」没有意义了，整项删掉。
            var rFil = new FlowLayoutPanel { AutoSize = true };
            cbG3Filter.Items.Add("显示：全部组");
            cbG3Filter.SelectedIndex = 0;
            cbG3Filter.SelectedIndexChanged += delegate { G3Fill(); };
            rFil.Controls.Add(new Label { Text = "组过滤", AutoSize = true, Anchor = AnchorStyles.Left });
            rFil.Controls.Add(cbG3Filter);
            // 全选/全不选（v2.22）：除了「当前组」，再加两个管**所有组**的按钮
            var rAll = new FlowLayoutPanel { AutoSize = true };
            var bAllOn = new Button { Text = "当前组全选", AutoSize = true };
            bAllOn.Click += delegate { G3GroupAll(true); };
            var bAllOff = new Button { Text = "当前组全不选", AutoSize = true };
            bAllOff.Click += delegate { G3GroupAll(false); };
            var bEvOn = new Button { Text = "全选", AutoSize = true };
            bEvOn.Click += delegate { G3GroupAllEvery(true); };
            var bEvOff = new Button { Text = "全不选", AutoSize = true };
            bEvOff.Click += delegate { G3GroupAllEvery(false); };
            L.Tip(bEvOn, "把「组·部位」表里所有组的每一行都勾上（不受「组过滤」显示范围影响）。");
            L.Tip(bEvOff, "把「组·部位」表里所有组的每一行都取消勾选。");
            rAll.Controls.Add(bAllOn); rAll.Controls.Add(bAllOff);
            rAll.Controls.Add(bEvOn); rAll.Controls.Add(bEvOff);
            pOut.Controls.Add(rOut); pOut.Controls.Add(rSuf); pOut.Controls.Add(rChk);
            pOut.Controls.Add(rQ); pOut.Controls.Add(rFil); pOut.Controls.Add(rAll);
            gbOut3.Controls.Add(pOut);
            left.Controls.Add(gbOut3, 0, 1);
            // 左列剩下来的那块地方给状态文字用（否则底部是一条没法缩小的空白）
            var pLeftStat3 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 3, 3, 0) };
            lblG3Status.Dock = DockStyle.Fill;
            lblG3Status.AutoSize = false;
            lblG3Status.TextAlign = ContentAlignment.TopLeft;
            pLeftStat3.Controls.Add(lblG3Status);
            left.Controls.Add(pLeftStat3, 0, 2);
            t.Controls.Add(left, 0, 0);
            left3 = left;

            // ---- 中列：组·部位 ----
            var gbG = new GroupBox { Text = "2) 角色本体 / 换装 × 部位", Dock = DockStyle.Fill };
            gridG.Dock = DockStyle.Fill;
            gridG.AllowUserToAddRows = false; gridG.AllowUserToDeleteRows = false;
            gridG.RowHeadersVisible = false; gridG.BackgroundColor = Color.White;
            gridG.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;   // 按权重铺满，低于最小宽度才横向滚动
            gridG.AllowUserToResizeRows = false;              // v2.24：行高不许拖（容易误触）
            gridG.ScrollBars = ScrollBars.Both;
            gridG.SelectionMode = DataGridViewSelectionMode.FullRowSelect; gridG.MultiSelect = false;
            gridG.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "启用", FillWeight = 5, MinimumWidth = 42, SortMode = DataGridViewColumnSortMode.NotSortable });
            gridG.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "组 · 部位", ReadOnly = true, FillWeight = 24, MinimumWidth = 120 });
            // v2.28：同第 2 页 —— 表头「数量」2 个汉字要 46px，「独占贴图」4 个汉字要 78px，
            // 否则这一页列多、被挤到最小宽度时表头会被裁成「数…」「独占贴…」。
            gridG.Columns.Add(new DataGridViewTextBoxColumn { Name = "cnt", HeaderText = "数量", ReadOnly = true, FillWeight = 4, MinimumWidth = 46 });
            gridG.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大小", ReadOnly = true, FillWeight = 9, MinimumWidth = 50 });
            gridG.Columns.Add(new DataGridViewTextBoxColumn { Name = "own", HeaderText = "独占贴图", ReadOnly = true, FillWeight = 10, MinimumWidth = 78 });
            var cbF = new DataGridViewComboBoxColumn { Name = "fmt", HeaderText = "处理方式", FillWeight = 12, MinimumWidth = 68, FlatStyle = FlatStyle.Flat };
            cbF.Items.AddRange("原样不动", "PNG", "JPEG", "自动");
            var cbS = new DataGridViewComboBoxColumn { Name = "max", HeaderText = "最大边", FillWeight = 9, MinimumWidth = 58, FlatStyle = FlatStyle.Flat };
            cbS.Items.AddRange("不缩放", "256", "512", "1024", "2048", "4096");   // v2.39：加 4096
            gridG.Columns.Add(cbF); gridG.Columns.Add(cbS);
            // v2.20：按组·部位单独指定「锐化 / 灰度」
            var cbSh = new DataGridViewComboBoxColumn { Name = "sh", HeaderText = "锐化", FillWeight = 9, MinimumWidth = 54, FlatStyle = FlatStyle.Flat };
            cbSh.Items.AddRange(SharpenItems);
            var cbShm = new DataGridViewComboBoxColumn { Name = "shm", HeaderText = "锐化算法", FillWeight = 10, MinimumWidth = 76, FlatStyle = FlatStyle.Flat };
            cbShm.Items.AddRange(ShModeItems);
            var cbGa = new DataGridViewComboBoxColumn { Name = "ga", HeaderText = "灰度2通道", FillWeight = 8, MinimumWidth = 62, FlatStyle = FlatStyle.Flat };
            cbGa.Items.AddRange(GrayItems);
            cbGa.ToolTipText = "灰度用 2 通道（R=G=B）";
            // v2.34：放大列，默认「跟随」（不表态）—— 否则会顶掉第 1 页类型表里设的 up
            var cbUp = new DataGridViewComboBoxColumn { Name = "up", HeaderText = "放大", FillWeight = 7, MinimumWidth = 58, FlatStyle = FlatStyle.Flat };
            cbUp.Items.AddRange(UpItems);
            cbUp.ToolTipText = "这一「组·部位」的贴图整数倍放大（×2/×4）。「跟随」= 用第 1 页类型表里该类设的值。只在彩色类生效，受「最大边」与 4096px 硬上限约束。";
            gridG.Columns.Add(cbSh); gridG.Columns.Add(cbShm); gridG.Columns.Add(cbGa); gridG.Columns.Add(cbUp);
            upUiCols.Add(cbUp);
            MakeColumnsResizable(gridG, "gridG");       // v2.37：列宽可拖动
            gridG.SelectionChanged += delegate { G3FillTex(); };
            // v2.29：点表头排序（组·部位=固定顺序：先组后部位；数量/大小/独占贴图=第一次点从大到小）
            HookHeaderSort(gridG, st3, delegate { G3FillTex(); });
            gridG.CurrentCellDirtyStateChanged += delegate { if (gridG.IsCurrentCellDirty) gridG.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            gridG.CellValueChanged += delegate { G3FillTex(); };
            gridG.DataError += delegate { };
            // 一键统一：按「组」过滤后统一设置处理方式（不影响其它组）
            // 每一项都是"标签 + 选择框"成对包成小面板 —— 换行时整对一起换，
            // 不会出现"文字在上一行、选择框掉到下一行"。
            // v1.0：同第 2 页 —— Fill + AutoSize 的高度不跟着内容长，英文下会把最后一个控件挤出可视区
            // v1.0：不用 Dock（理由同第 2 页），Anchor 左右 + AutoSize
            // v1.0.2：同第 2 页 —— 不用 Dock=Fill，Anchor 左右 + AutoSize
            gUni = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                          Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                                          Padding = new Padding(0), WrapContents = true, Margin = new Padding(0) };
            gUni.Controls.Add(new Label { Text = "一键统一", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 6, 3) });
            cbG3UniScope.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniScope.Width = 112;
            gUni.Controls.Add(Pair("范围", cbG3UniScope));
            cbG3UniFmt.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniFmt.Width = 88;
            cbG3UniFmt.Items.AddRange(G3FmtItems);
            cbG3UniFmt.SelectedIndex = 3;                        // 自动
            gUni.Controls.Add(Pair("处理方式", cbG3UniFmt));
            cbG3UniSize.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniSize.Width = 78;
            cbG3UniSize.Items.AddRange(G3SizeItems);
            cbG3UniSize.SelectedIndex = 3;                       // 1024
            gUni.Controls.Add(Pair("最大边", cbG3UniSize));
            cbG3UniSh.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniSh.Width = 78;
            cbG3UniSh.Items.AddRange(SharpenItems);
            cbG3UniSh.SelectedIndex = 0;                         // 跟随
            gUni.Controls.Add(Pair("锐化", cbG3UniSh));
            cbG3UniShm.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniShm.Width = 108;
            cbG3UniShm.Items.AddRange(ShModeItems);
            cbG3UniShm.SelectedIndex = 0;                        // 跟随
            gUni.Controls.Add(Pair("锐化算法", cbG3UniShm));
            cbG3UniGa.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniGa.Width = 62;
            cbG3UniGa.Items.AddRange(UniGrayItems);
            cbG3UniGa.SelectedIndex = 0;                         // 开（与全局默认一致）
            gUni.Controls.Add(Pair("灰度2通道", cbG3UniGa));
            // v2.35：放大那一对默认收起（三个页面共用一个展开状态）
            cbG3UniUp.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniUp.Width = 78;
            cbG3UniUp.Items.AddRange(UpItems);
            cbG3UniUp.SelectedIndex = 0;                         // 跟随
            cbG3UniUpK.DropDownStyle = ComboBoxStyle.DropDownList;
            cbG3UniUpK.Width = 104;
            cbG3UniUpK.Items.AddRange(Resample.UpKernels);
            cbG3UniUpK.SelectedIndex = Math.Max(0, Array.IndexOf(Resample.UpKernels, Resample.UpKernel));
            cbG3UniUpK.SelectedIndexChanged += delegate
            {
                if (cbG3UniUpK.SelectedIndex >= 0) Resample.UpKernel = Resample.UpKernels[cbG3UniUpK.SelectedIndex];
            };
            L.Tip(cbG3UniUpK, "放大用哪种算法（三页共用）。默认 lanczos3 最清晰，mitchell 最柔和，nearest 只适合像素画。");
            // 「贴图细节保护」与「独占贴图保护」：整卡开关，立刻生效（不需要点一键统一）
            chkKernel3.Checked = Resample.DetailMode;
            chkKernel3.CheckedChanged += delegate { Resample.DetailMode = chkKernel3.Checked; WarnIfSharpenInert(); SyncColorEnable(); };
            L.Tip(chkKernel3, MainForm.TipKernel);
            // v2.32：彩色贴图的核（和第 1 页那一项是同一个设置）
            chkColor3.Checked = Resample.ColorMode;
            chkColor3.CheckedChanged += delegate { Resample.ColorMode = chkColor3.Checked; };
            L.Tip(chkColor3, MainForm.TipColor);
            chkOwn3.Checked = TexTool.ProtectOwn;
            cbOwnMax3.DropDownStyle = ComboBoxStyle.DropDownList;
            cbOwnMax3.Width = 104;
            cbOwnMax3.Items.AddRange(OwnMaxItems);
            cbOwnMax3.SelectedIndex = OwnMaxIndex(TexTool.ProtectOwnMax);
            cbOwnMax3.Enabled = chkOwn3.Checked;
            chkOwn3.CheckedChanged += delegate
            {
                TexTool.ProtectOwn = chkOwn3.Checked;
                cbOwnMax3.Enabled = chkOwn3.Checked;
            };
            cbOwnMax3.SelectedIndexChanged += delegate
            {
                TexTool.ProtectOwnMax = OwnMaxValue(cbOwnMax3.SelectedIndex);
            };
            L.Tip(chkOwn3, MainForm.TipOwn);
            gUni.Controls.Add(Pair2(chkKernel3));
            gUni.Controls.Add(Pair2(chkColor3));
            // v2.35：「独占贴图保护」与「上限」绑成一个不可拆的单元（保证同一行，不换行）
            var pOwn3 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
            pOwn3.Controls.Add(Pair2(chkOwn3));
            pOwn3.Controls.Add(Pair("上限", cbOwnMax3));
            gUni.Controls.Add(pOwn3);
            // v2.35：展开按钮放在「独占贴图保护」后面，展开出来的两项紧跟在按钮后面
            bShowUp3 = new Button { Text = "显示放大选项 ▾", Margin = new Padding(6, 4, 4, 1) };
            HookUpToggle(bShowUp3);
            L.Tip(bShowUp3, "放大（×2 / ×4）默认收起：代价大、多数人用不上。点这里展开（三个页面一起）。");
            gUni.Controls.Add(bShowUp3);
            var pUp3 = Pair("放大", cbG3UniUp);
            var pUpK3 = Pair("放大算法", cbG3UniUpK);
            gUni.Controls.Add(pUp3);
            gUni.Controls.Add(pUpK3);
            upUiCtrls3.Add(pUp3);
            upUiCtrls3.Add(pUpK3);
            // v2.38：按贴图类型统一（展开栏），与第 2 页一致
            cbTypeUni3 = MakeTypeCombo();
            bApplyType3 = new Button { Text = "应用到所有该类型贴图", AutoSize = true, Margin = new Padding(6, 1, 2, 1) };
            bApplyType3Part = new Button { Text = "应用到选中部位", AutoSize = true, Margin = new Padding(3, 1, 2, 1) };
            pTypePair3 = MakeTypePair(cbTypeUni3, bApplyType3, bApplyType3Part);
            L.Tip(bApplyType3, "把「一键统一」那套值套到整张人物卡里这一类型的全部贴图（含单张指定，优先级最高）。");
            Action<bool> applyTypeUni3 = delegate (bool onlyThisPart)
            {
                string cls = TypeComboCls(cbTypeUni3);
                string uf = cbG3UniFmt.SelectedItem as string, usz = cbG3UniSize.SelectedItem as string;
                string ush = cbG3UniSh.SelectedItem as string, ushm = cbG3UniShm.SelectedItem as string;
                string uga = cbG3UniGa.SelectedItem as string, uup = cbG3UniUp.SelectedItem as string;
                if (ps3 == null || ps3.Parts == null || ps3.Parts.Count == 0)
                {
                    lblG3Status.Text = "请先读取一张人物卡";
                    return;
                }
                int only = -1;
                if (onlyThisPart)
                {
                    if (gridG.CurrentRow == null || gridG.CurrentRow.Tag == null)
                    {
                        lblG3Status.Text = "请先在「组·部位」表里选中一行";
                        return;
                    }
                    only = (int)gridG.CurrentRow.Tag;
                }
                int n = 0;
                foreach (var pi in ps3.Parts)
                {
                    if (onlyThisPart && pi.SlotKey != only) continue;
                    foreach (var tx in pi.Tex)
                    {
                        if (tx.Id == null || tx.Cls != cls) continue;
                        var r = new Rule(uf == "跟随部位" ? "auto" : FmtItemToKey(uf),
                                         usz == "跟随部位" ? 1024 : SizeItemToNum(usz));
                        SetRuleExtra(r, SharpenItemToVal(ush), ShModeItemToVal(ushm), GrayItemToVal(uga), UpTableToVal(uup));
                        texOv3[tx.Id.ToString()] = r;
                        n++;
                    }
                }
                G3FillTex();
                lblG3Status.Text = L.F("已把{0}的「{1}」类型 {2} 张贴图设为：{3} / {4} / 锐化 {5} {6} / 灰度2通道 {7}{8}",
                    onlyThisPart ? "选中部位" : "整张卡", Classes.Short(cls), n, uf, usz, ush, ushm, uga,
                    uup != "不放大" ? " / 放大 " + uup : "");
            };
            bApplyType3.Click += delegate { applyTypeUni3(false); };
            bApplyType3Part.Click += delegate { applyTypeUni3(true); };
            L.Tip(bApplyType3Part, "只改当前选中的「组·部位」里这一类型的贴图。先在上面那张表里选中一行。");
            bTypeShow3 = new Button { Text = L.T("按贴图类型统一 ▾"), Margin = new Padding(0, 2, 4, 0) };
            HookTypeToggle(bTypeShow3, delegate { SetTypeUniVisible(!typeUniVisible); });
            L.Tip(bTypeShow3, "想按贴图类型批量改（比如把所有主贴图设成同一个值）时点开这里。");
            pTypePair3.Visible = false;
            // 展开按钮自己占一行（同第 2 页的理由：并排会超出可用宽度）
            var pTypeRow3 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                                FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            pTypeRow3.Controls.Add(bTypeShow3);
            pTypeRow3.Controls.Add(pTypePair3);

            // v2.35：「一键统一」改名「应用到所有部位」，并与「设置到目前浏览的部位」单独占一行
            // v1.0：允许换行（同第 2 页的理由）
            var pActs3 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 0), WrapContents = false };
            var bUni = new Button { Text = "应用到所有部位", AutoSize = true };
            bUni.Click += delegate { G3UniApply(); };
            L.Tip(bUni, "把左边这套设置套到「范围」命中的那些「组·部位」（默认全部组）。");
            pActs3.Controls.Add(bUni);
            // v2.30：再加一个"就近应用"按钮 —— 一键改目前正在浏览的那个组·部位（不用去动上面的「范围」）
            var btnUniToPart = new Button { Text = "设置到目前浏览的部位", AutoSize = true };
            btnUniToPart.Click += delegate { G3UniToPart(); };
            L.Tip(btnUniToPart, "把左边这几个值只设到当前正在浏览的那一组（同组其它部位不动），并清掉该处的单张指定。");
            pActs3.Controls.Add(btnUniToPart);

            // v2.35：两个动作按钮单独占一行
            // 说明用 Label 直接当一行（FlowLayoutPanel 在 AutoSize 行里会算不准高度，把字压扁）
            // 手动断行：AutoSize 的 Label 不折行，太长会被容器**直接截掉**（实测 440px vs 容器 308px）。
            bUniTip = new Label { Text = "一键统一只改「范围」那个组，其它组不动\n右边两个开关立刻生效（整卡）",
                                  AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(3, 0, 3, 2) };

            var gWrap = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            // 单列也要显式给 Percent：不给的话隐含列是 AutoSize，会按最宽子控件的"需要宽度"撑开
            // （表现：GroupBox/表格比容器宽、被右边切掉一截）
            gWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            gWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 一键统一（设置项）
            gWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // v2.35：两个动作按钮单独一行
            gWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // v2.38：按贴图类型统一（展开栏）
            gWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            gWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            gridG.Dock = DockStyle.Fill;
            gWrap.Controls.Add(gUni, 0, 0);
            gWrap.Controls.Add(pActs3, 0, 1);
            gWrap.Controls.Add(pTypeRow3, 0, 2);
            gWrap.Controls.Add(bUniTip, 0, 3);
            gWrap.Controls.Add(gridG, 0, 4);
            gbG.Controls.Add(gWrap);
            t.Controls.Add(gbG, 2, 0);

            // ---- 右列：贴图明细 ----
            var gbGT = new GroupBox { Text = "3) 选中组·部位的贴图", Dock = DockStyle.Fill };
            gridGT.Dock = DockStyle.Fill;
            gridGT.AllowUserToAddRows = false; gridGT.AllowUserToDeleteRows = false;
            gridGT.RowHeadersVisible = false; gridGT.BackgroundColor = Color.White;
            gridGT.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            gridGT.AllowUserToResizeRows = false;             // v2.24：行高不许拖（容易误触）
            gridGT.ScrollBars = ScrollBars.Both;
            gridGT.SelectionMode = DataGridViewSelectionMode.FullRowSelect; gridGT.MultiSelect = false;
            gridGT.Columns.Add(new DataGridViewTextBoxColumn { Name = "tid", HeaderText = "TexID", ReadOnly = true, FillWeight = 6, MinimumWidth = 48 });
            gridGT.Columns.Add(new DataGridViewTextBoxColumn { Name = "tcls", HeaderText = "类型", ReadOnly = true, FillWeight = 8, MinimumWidth = 54 });
            gridGT.Columns.Add(new DataGridViewTextBoxColumn { Name = "tdim", HeaderText = "尺寸", ReadOnly = true, FillWeight = 6, MinimumWidth = 56 });
            // v2.30：列头「字节」→「大小」（和部位表同名）；点 TexID / 尺寸 / 大小 都按**数值**排
            gridGT.Columns.Add(new DataGridViewTextBoxColumn { Name = "tbytes", HeaderText = "大小", ReadOnly = true, FillWeight = 8, MinimumWidth = 62 });
            gridGT.Columns.Add(new DataGridViewTextBoxColumn { Name = "tfmt", HeaderText = "原格式", ReadOnly = true, FillWeight = 6, MinimumWidth = 54 });
            gridGT.Columns.Add(new DataGridViewTextBoxColumn { Name = "tshare", HeaderText = "共用情况", ReadOnly = true, FillWeight = 20, MinimumWidth = 70 });
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
            cbTGa.ToolTipText = "灰度用 2 通道（R=G=B）";
            gridGT.Columns.Add(cbTF); gridGT.Columns.Add(cbTS);
            var cbTUp = new DataGridViewComboBoxColumn { Name = "tup", HeaderText = "放大", FillWeight = 7, MinimumWidth = 64, FlatStyle = FlatStyle.Flat };
            cbTUp.Items.AddRange(TexUpItems);
            cbTUp.ToolTipText = "这一张贴图自己的放大倍率。「跟随部位」= 用所属「组·部位」的设置。";
            gridGT.Columns.Add(cbTSh); gridGT.Columns.Add(cbTShm); gridGT.Columns.Add(cbTGa); gridGT.Columns.Add(cbTUp);
            upUiCols.Add(cbTUp);
            gridGT.Columns.Add(new DataGridViewTextBoxColumn { Name = "tfinal", HeaderText = "最终处理", ReadOnly = true, FillWeight = 16, MinimumWidth = 80 });
            MakeColumnsResizable(gridGT, "gridGT");  // v2.37：列宽可拖动（放在最后一列加完之后）
            gridGT.CurrentCellDirtyStateChanged += delegate { if (gridGT.IsCurrentCellDirty) gridGT.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            gridGT.CellValueChanged += delegate { G3TexOvChanged(); };
            gridGT.DataError += delegate { };
            var texBox3 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            // 单列也要显式给 Percent：不给的话隐含列是 AutoSize，会按最宽子控件的"需要宽度"撑开
            // （表现：GroupBox/表格比容器宽、被右边切掉一截）
            texBox3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            texBox3.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            texBox3.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
            texBox3.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));     // 预览框默认高度（可上下拖）
            gridGT.Dock = DockStyle.Fill;
            texBox3.Controls.Add(gridGT, 0, 0);
            var prevG3 = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
            prevG3.Controls.Add(picG3); prevG3.Controls.Add(lblG3Prev);
            // v2.27：「独立窗口」按钮钉在预览框右上角（与第 2 页同一个悬浮窗，跟着换图）
            btnBigG3.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            L.Tip(btnBigG3, "把这张贴图放进独立窗口放大看：滚轮缩放、按住左键拖动。主界面换贴图时窗口跟着换。");
            btnBigG3.Click += delegate
            {
                OpenBigPreview(picG3, gridGT, g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim());
            };
            prevG3.Controls.Add(btnBigG3);
            // ⚠ 同第 2 页：不 BringToFront 会被 Dock=Fill 的 picG3 盖住
            btnBigG3.Location = new Point(Math.Max(0, prevG3.ClientSize.Width - btnBigG3.Width - 6), 6);
            btnBigG3.BringToFront();
            prevG3.Resize += delegate
            {
                btnBigG3.Location = new Point(Math.Max(0, prevG3.ClientSize.Width - btnBigG3.Width - 6), 6);
                btnBigG3.BringToFront();
            };
            texBox3.Controls.Add(prevG3, 0, 2);
            spPrev3 = MakeRowSplitter(texBox3, 1, 0, 2, 140, 70);            // 明细 | 预览：上下拖
            gbGT.Controls.Add(texBox3);
            gridGT.SelectionChanged += delegate
            {
                if (g3Filling) return;                  // v2.30：重填/排序期间不重复刷
                UpdatePreview(picG3, lblG3Prev, gridGT, g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim());
            };
            // v2.30：明细表也接管表头点击（TexID / 尺寸 / 大小 按数值排）
            HookHeaderSort(gridGT, stTex3, null);
            lblG3Prev.Text = "点一行贴图即可预览（上方灰条可拖动调高度）";
            t.Controls.Add(gbGT, 4, 0);

            // ---- 最右：日志 ----
            logG3.Dock = DockStyle.Fill;
            logWrap3 = new Panel { Dock = DockStyle.Fill };
            var pLogHead3 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            pLogHead3.Controls.Add(new Label { Text = "日志", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left });
            logWrap3.Controls.Add(logG3); logWrap3.Controls.Add(pLogHead3);
            t.Controls.Add(logWrap3, 6, 0);

            // ---- 底部动作条 ----
            var actRow = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 2 };
            actRow3 = actRow;
            actRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var pAct = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                             Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };   // v1.1.3：同第 2 页
            MakeMainButton(btnG3Run, "▶  压缩人物卡", Font);        // 与第 1 页「开始压缩」同一套样式
            btnG3Run.Click += delegate { G3Run(); };
            btnG3Cancel.Click += delegate { cancelFlag = true; lblG3Status.Text = "正在取消…"; };
            var bSave = new Button { Text = "保存预设…", AutoSize = true };
            bSave.Click += delegate { G3SavePreset(); };
            var bLoadP = new Button { Text = "读取预设…", AutoSize = true };
            bLoadP.Click += delegate { G3LoadPreset(); };
            var bPfDir = new Button { Text = "打开预设目录", AutoSize = true };
            bPfDir.Click += delegate { PreOpenDir(PresetIo.KindChara); };
            var bOpen = new Button { Text = "打开输出目录", AutoSize = true };
            bOpen.Click += delegate
            {
                if (Directory.Exists(txtG3Out.Text.Trim()))
                    System.Diagnostics.Process.Start("explorer.exe", txtG3Out.Text.Trim());
            };
            pAct.Controls.Add(btnG3Run); pAct.Controls.Add(btnG3Cancel);
            pAct.Controls.Add(bSave); pAct.Controls.Add(bLoadP); pAct.Controls.Add(bPfDir);
            // v2.38：记住/恢复列宽，放在「打开输出目录」旁边
            var bColSave3 = new Button { Text = "记住列宽", AutoSize = true };
            bColSave3.Click += delegate { SaveColWidths(); };
            var bColReset3 = new Button { Text = "恢复默认列宽", AutoSize = true };
            bColReset3.Click += delegate { ResetColWidths(); };
            L.Tip(bColSave3, "把四个表格的当前列宽记下来，下次启动自动套用。");
            L.Tip(bColReset3, "忘掉记住的列宽，四个表格回到默认比例。");
            pAct.Controls.Add(bOpen);
            pAct.Controls.Add(bColSave3);
            pAct.Controls.Add(bColReset3);
            btnLog3.Click += delegate { LogBtnClicked(3); };
            actRow.Controls.Add(pAct, 0, 0);
            actRow.Controls.Add(btnLog3, 1, 0);
            outer.Controls.Add(actRow, 0, 1);

            spTab3Left = MakeSplitter(t, 1, 0, 2, false);
            spTab3Mid = MakeSplitter(t, 3, 2, 4, true);
            spLog3 = MakeSplitter(t, 5, 4, 6, false);
            HookLogSplitter(spLog3, t, 6);              // 仍可左右拖动（人物卡页不参与"放大优先给日志"）

            // 拖到这一页也能读卡
            foreach (Control c in new Control[] { page, txtG3Card, gridG, gridGT, logG3 })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnterAny;
                c.DragDrop += delegate (object s, DragEventArgs e)
                {
                    if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                    var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (paths == null || paths.Length == 0) return;
                    // v2.23：这一页只读**一张**人物卡；拖进来多个或整个目录时只取第一张。
                    g3Cards.Clear();
                    foreach (var f in paths)
                    {
                        if (Directory.Exists(f))
                        {
                            var l = TexTool.Enumerate(f, null, false);      // 只看这一层，不递归
                            if (l.Count > 0) g3Cards.Add(l[0].Src);
                        }
                        else g3Cards.Add(f);
                        if (g3Cards.Count >= 1) break;
                    }
                    if (paths.Length > 1 || (paths.Length == 1 && Directory.Exists(paths[0])))
                        Log3(L.F("本页一次只处理一张人物卡，已只读取第一张：{0}", Path.GetFileName(g3Cards.Count > 0 ? g3Cards[0] : "")));
                    txtG3Card.Text = g3Cards.Count == 1 ? g3Cards[0] : "";
                    G3Load();
                };
            }
            page.Controls.Add(outer);
            return page;
        }

        Panel spTab3Left, spTab3Mid;

        void Log3(string s)
        {
            s = L.Tr(s);                       // v1.0.2
            logG3.AppendText(s + "\r\n");
            logG3.SelectionStart = logG3.TextLength;
            logG3.ScrollToCaret();
        }

        /// <summary>读卡：v2.23 起这一页**只处理一张**人物卡（多余的直接丢掉）。
        /// 需要一次压很多张就用第 1 页的批量压缩。</summary>
        void G3Load()
        {
            if (g3Cards.Count == 0)
            {
                string p = txtG3Card.Text.Trim();
                if (p.Length > 0 && File.Exists(p)) g3Cards.Add(p);
            }
            if (g3Cards.Count == 0) { MessageBox.Show(L.Tr("请先选一张人物卡（Koikatu_F_*.png）。")); return; }
            if (g3Cards.Count > 1)
            {
                Log3(L.F("本页一次只处理一张人物卡，已只保留第一张（其余 {0} 张忽略）。", g3Cards.Count - 1));
                g3Cards.RemoveRange(1, g3Cards.Count - 1);
                txtG3Card.Text = g3Cards[0];
            }
            logG3.Clear();
            Busy3(true);
            g3Loading = true;
            lblG3Status.Text = "正在读取…（大卡要几秒；界面不会卡住）";
            Log3(L.F("正在读取：{0}", Path.GetFileName(g3Cards[0])));
            string firstCard = g3Cards[0];
            var th = new Thread(delegate ()
            {
                TexTool.PartsScan r = null; string err = null;
                try { r = TexTool.ScanPartsCached(firstCard); }
                catch (Exception ex) { err = ex.Message; }
                Post("g3data", firstCard, r, err);
            });
            th.IsBackground = true; th.Start();
        }

        /// <summary>后台读完 → 主线程填表（由 Drain 调用）。</summary>
        void ApplyG3Scan(string first, TexTool.PartsScan r, string err)
        {
            g3Loading = false;
            Busy3(false);
            if (err != null) { lblG3Status.Text = "读取失败：" + err; Log3("[X] " + err); return; }
            ps3 = r;
            if (ps3 == null) { lblG3Status.Text = "读取失败"; return; }
            if (ps3.Err.Length > 0)
            {
                lblG3Status.Text = "读不出来：" + ps3.Err;
                Log3("[X] " + ps3.Err);
                gridG.Rows.Clear(); gridGT.Rows.Clear();
                return;
            }
            if (!G3IsChara(ps3))
            {
                lblG3Status.Text = "这张不是人物卡（没有「角色本体 / 换装」结构），请用第 2 页压衣服卡。";
                Log3("[X] 这不是人物卡：卡里没有 组·部位 结构。人物卡文件名一般是 Koikatu_F_*.png");
                gridG.Rows.Clear(); gridGT.Rows.Clear();
                return;
            }
            if (txtG3Out.Text.Trim().Length == 0) txtG3Out.Text = Path.GetDirectoryName(first) ?? ".";   // 默认与卡片同目录
            // 用刚才 ScanParts 读进来的字节（ps3.Raw），不要再整读一遍
            var info = Card.ReadInfo(ps3.Raw != null && ps3.Raw.Length > 0 ? ps3.Raw : TexTool.Peek(first));
            Log3(L.F("人物卡：{0}（{1}）；本次 {2} 张卡", Path.GetFileName(first), info.Tag, g3Cards.Count));
            Log3(L.F("共 {0} 张贴图 / {1}；角色本体 + {2} 套换装",
                ps3.NTex, TexTool.Human(ps3.Bytes), Chara.CoordNames.Length));
            G3FillFilter();
            G3FillUniScope();
            G3Fill();
            lblG3Status.Text = L.F("已读取：{0} 张贴图 / {1} 个「组·部位」条目", ps3.NTex, CountG3Rows());
        }

        int CountG3Rows()
        {
            int n = 0;
            foreach (var pi in ps3.Parts) if (pi.Tex.Count > 0) n++;
            return n;
        }

        /// <summary>扫描结果里只要出现「组·部位」键（≥100000）就是人物卡结构。</summary>
        static bool G3IsChara(TexTool.PartsScan s)
        {
            if (s == null) return false;
            foreach (var pi in s.Parts) if (Chara.IsCharaKey(pi.SlotKey)) return true;
            return false;
        }

        void G3Fill()
        {
            if (g3Filling) return;
            g3Filling = true;
            suppressPreview = true;                     // v2.25：填表期间不预览
            try { G3FillCore(); }
            finally { g3Filling = false; suppressPreview = false; }
            // ⚠ v2.30 修的界面 bug：G3FillCore 末尾那次 G3FillTex() 会被上面的 g3Filling 挡掉，
            // 于是刚读完卡时「3) 选中组·部位的贴图」是**空的**，必须手点一行「组·部位」才填。
            // 这里在清掉 g3Filling 之后补一次（G3FillTex 内部自己会刷预览，不再重复调）。
            G3FillTex();
        }

        void G3FillCore()
        {
            string filter = cbG3Filter.SelectedIndex <= 0 ? null : cbG3Filter.SelectedItem as string;
            ClearThumbCache();                          // 换卡了 → 缩略图缓存作废
            gridG.Rows.Clear();
            if (ps3 == null) return;
            foreach (var pi in ps3.Parts)
            {
                if (pi.Tex.Count == 0) continue;
                string gname = Chara.IsCharaKey(pi.SlotKey) ? Chara.GroupName(Chara.KeyGroup(pi.SlotKey)) : "?";
                if (filter != null && filter != "显示：全部组" && filter != gname) continue;
                int idx = gridG.Rows.Add();
                var row = gridG.Rows[idx];
                row.Tag = pi.SlotKey;
                row.Cells["on"].Value = true;
                row.Cells["name"].Value = pi.Name;
                row.Cells["cnt"].Value = pi.Tex.Count.ToString();
                row.Cells["size"].Value = TexTool.Human(pi.Bytes);
                row.Cells["own"].Value = L.F("{0} 张 / {1}", pi.OwnCount, TexTool.Human(pi.OwnBytes));
                row.Cells["fmt"].Value = "自动";
                row.Cells["max"].Value = "1024";
                row.Cells["sh"].Value = "100%";              // v2.25：组·部位表不再有「跟随」→ 明确默认值
                row.Cells["shm"].Value = "对比度自适应";
                row.Cells["ga"].Value = "开";
                row.Cells["up"].Value = "不放大";       // v2.35：不放大 = 不表态
            }
            if (gridG.Rows.Count > 0) gridG.CurrentCell = gridG.Rows[0].Cells["name"];
            ApplySort(gridG, st3);                          // v2.29：把上次的表头排序回放一遍
            G3FillTex();
        }

        /// <summary>组过滤下拉：把卡里出现的组建出来（角色本体 / 换装1..7）。</summary>
        void G3FillFilter()
        {
            string cur = cbG3Filter.SelectedItem as string;
            cbG3Filter.Items.Clear();
            cbG3Filter.Items.Add("显示：全部组");
            if (ps3 != null)
            {
                var gs = new List<int>();
                foreach (var pi in ps3.Parts)
                    if (pi.Tex.Count > 0 && Chara.IsCharaKey(pi.SlotKey))
                    {
                        int g = Chara.KeyGroup(pi.SlotKey);
                        if (!gs.Contains(g)) gs.Add(g);
                    }
                gs.Sort();
                foreach (var g in gs) cbG3Filter.Items.Add(L.T(Chara.GroupName(g)));   // 组过滤下拉：运行时填的，要翻
            }
            int i = cbG3Filter.Items.IndexOf(cur);
            cbG3Filter.SelectedIndex = i >= 0 ? i : 0;
        }

        /// <summary>「一键统一」的作用范围下拉：全部组 / 角色本体 / 换装N / 仅当前选中组。</summary>
        void G3FillUniScope()
        {
            string cur = cbG3UniScope.SelectedItem as string;
            cbG3UniScope.Items.Clear();
            cbG3UniScope.Items.Add(L.T("全部组"));
            if (ps3 != null)
            {
                var gs = new List<int>();
                foreach (var pi in ps3.Parts)
                    if (pi.Tex.Count > 0 && Chara.IsCharaKey(pi.SlotKey))
                    {
                        int g = Chara.KeyGroup(pi.SlotKey);
                        if (!gs.Contains(g)) gs.Add(g);
                    }
                gs.Sort();
                foreach (var g in gs) cbG3UniScope.Items.Add(L.T(Chara.GroupName(g)));
            }
            // v2.35：去掉「仅当前选中组」（用户要求）—— 要只改一组就用下面那些「换装N / 角色本体」项
            // v2.31：这一项 = 只对「启用」勾上的部位生效（以前是"只改明细里选中那张贴图"，语义已改）
            cbG3UniScope.Items.Add(L.T(G3ScopeEnabled));
            int i = cbG3UniScope.Items.IndexOf(cur);
            cbG3UniScope.SelectedIndex = i >= 0 ? i : 0;
        }

        /// <summary>一键统一：只改「统一范围」命中的组，其它组原封不动。</summary>
        void G3UniApply()
        {
            if (ps3 == null || gridG.Rows.Count == 0)
            {
                MessageBox.Show(L.Tr("请先读取一张人物卡。"));
                return;
            }
            string scope = cbG3UniScope.SelectedItem as string;
            if (string.IsNullOrEmpty(scope)) scope = "全部组";
            int only = -1;
            // v2.35：「仅当前选中组」已从下拉里去掉（改一组请直接选「换装N / 角色本体」）。
            // 这里仍然留着兼容分支：老预设/命令行若传了这个词，行为不变。
            string fmt = cbG3UniFmt.SelectedItem as string;
            string max = cbG3UniSize.SelectedItem as string;
            string sh = cbG3UniSh.SelectedItem as string;
            string shm = cbG3UniShm.SelectedItem as string;
            string ga = cbG3UniGa.SelectedItem as string;
            string upIt = cbG3UniUp.SelectedItem as string;      // v2.34
            string upSuffix = upIt != "跟随" ? " / 放大 " + upIt : "";
            // v2.31：这个范围的语义改成「只对**当前被设为启用（勾上）**的部位生效」。
            // 也就是说：先在「组·部位」表里把不要的那些取消勾选，再用它统一 —— 只改勾上的那些行，
            // 没勾的一行都不动（也不清它们的单张指定）。
            if (scope == G3ScopeEnabled)
            {
                int nOn = 0, nOvOn = 0;
                var idsOn = new List<string>();
                foreach (DataGridViewRow row in gridG.Rows)
                {
                    if (row.Tag == null || !Chara.IsCharaKey((int)row.Tag)) continue;
                    bool on = row.Cells["on"].Value is bool && (bool)row.Cells["on"].Value;
                    if (!on) continue;
                    row.Cells["fmt"].Value = fmt;
                    row.Cells["max"].Value = max;
                    row.Cells["sh"].Value = sh;
                    row.Cells["shm"].Value = shm;
                    row.Cells["ga"].Value = ga;
                    row.Cells["up"].Value = upIt;
                    nOn++;
                    var piOn = ps3.Find((int)row.Tag);
                    if (piOn != null)
                        foreach (var tx in piOn.Tex) if (tx.Id != null) idsOn.Add(tx.Id.ToString());
                }
                foreach (var id in idsOn) if (texOv3.Remove(id)) nOvOn++;
                G3FillTex();
                lblG3Status.Text = L.F("已启用的 {0} 个「组·部位」统一为 {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}",
                    nOn, fmt, max, sh, shm, ga, upSuffix);
                Log3(L.F("[统一·仅已启用] {0} 个已勾上的条目 → {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}"
                    + "（顺带清掉 {7} 条单张贴图指定；没勾的条目一律不动）", nOn, fmt, max, sh, shm, ga, upSuffix, nOvOn));
                if (nOn == 0) lblG3Status.Text = "当前没有任何条目的「启用」是勾上的 —— 先把要压的勾上再用这个范围。";
                return;
            }
            int n = 0;
            foreach (DataGridViewRow row in gridG.Rows)
            {
                if (row.Tag == null) continue;
                if (!Chara.IsCharaKey((int)row.Tag)) continue;
                int g = Chara.KeyGroup((int)row.Tag);
                bool want = only >= 0 ? g == only : (scope == "全部组" || scope == Chara.GroupName(g));
                if (!want) continue;
                row.Cells["fmt"].Value = fmt;
                row.Cells["max"].Value = max;
                row.Cells["sh"].Value = sh;
                row.Cells["shm"].Value = shm;
                row.Cells["ga"].Value = ga;
                n++;
            }
            // 清掉作用范围内那些贴图的「单张指定」，范围外的保留
            var ids = new List<string>();
            foreach (var pi in ps3.Parts)
            {
                if (pi.Tex.Count == 0 || !Chara.IsCharaKey(pi.SlotKey)) continue;
                int g = Chara.KeyGroup(pi.SlotKey);
                bool inScope = only >= 0 ? g == only : (scope == "全部组" || scope == Chara.GroupName(g));
                if (!inScope) continue;
                foreach (var tx in pi.Tex) if (tx.Id != null) ids.Add(tx.Id.ToString());
            }
            int nOv = 0;
            foreach (var id in ids) if (texOv3.Remove(id)) nOv++;
            G3FillTex();
            lblG3Status.Text = L.F("{0}：{1} 个「组·部位」统一为 {2} / {3} / 锐化 {4} {5} / 灰度2通道 {6}{7}",
                scope, n, fmt, max, sh, shm, ga, upSuffix);
            Log3(L.F("[统一] {0} → {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}{6}（{7} 个条目，顺带清掉 {8} 条单张贴图指定）",
                scope, fmt, max, sh, shm, ga, upSuffix, n, nOv));
        }

        /// <summary>一键统一「范围」里的那一项：只对"当前被设为启用（勾上）"的部位生效。</summary>
        const string G3ScopeEnabled = "仅已启用的部位";

        // ---- v2.30/v2.31：一键统一的"就近/按启用"应用 ---------------------------------
        //  · 范围「仅已启用的部位」：只改那些「启用」勾上的行（见 G3UniApply）
        //  · G3UniToPart           ：把值设到「目前正在浏览的那个组·部位」（只改这一行，不动同组其它部位）
        // 单张贴图仍然可以直接在「贴图明细」里改那张自己的组合框（那是"单张指定"，优先级最高）。

        /// <summary>把一键统一的那套值，设到「目前正在浏览的那个组·部位」。
        /// 只改这一行（同组其它部位不动）；顺带清掉该部位贴图的单张指定 ——
        /// 不清的话单张指定优先级更高，用户会觉得"设了没生效"。</summary>
        void G3UniToPart()
        {
            if (ps3 == null || gridG.CurrentRow == null || gridG.CurrentRow.Tag == null)
            {
                MessageBox.Show(L.Tr("请先读取一张人物卡，并在「组·部位」表里选中一行。"));
                return;
            }
            var row = gridG.CurrentRow;
            string fmt = cbG3UniFmt.SelectedItem as string;
            string max = cbG3UniSize.SelectedItem as string;
            string sh = cbG3UniSh.SelectedItem as string;
            string shm = cbG3UniShm.SelectedItem as string;
            string ga = cbG3UniGa.SelectedItem as string;
            row.Cells["fmt"].Value = fmt;
            row.Cells["max"].Value = max;
            row.Cells["sh"].Value = sh;
            row.Cells["shm"].Value = shm;
            row.Cells["ga"].Value = ga;
            row.Cells["up"].Value = cbG3UniUp.SelectedItem as string;
            int nOv = 0;
            var pi = ps3.Find((int)row.Tag);
            if (pi != null)
                foreach (var tx in pi.Tex)
                    if (tx.Id != null && texOv3.Remove(tx.Id.ToString())) nOv++;
            G3FillTex();
            string where = row.Cells["name"].Value as string ?? "?";
            lblG3Status.Text = L.F("已设到「{0}」：{1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}",
                where, fmt, max, sh, shm, ga);
            Log3(L.F("[就近] 「{0}」→ {1} / {2} / 锐化 {3} {4} / 灰度2通道 {5}（顺带清掉 {6} 条单张贴图指定）",
                where, fmt, max, sh, shm, ga, nOv));
        }

        void G3GroupAll(bool on)
        {
            if (ps3 == null || gridG.CurrentRow == null) return;
            int g = Chara.IsCharaKey((int)gridG.CurrentRow.Tag) ? Chara.KeyGroup((int)gridG.CurrentRow.Tag) : 0;
            foreach (DataGridViewRow row in gridG.Rows)
            {
                if (row.Tag == null) continue;
                if (!Chara.IsCharaKey((int)row.Tag) || Chara.KeyGroup((int)row.Tag) != g) continue;
                row.Cells["on"].Value = on;
            }
            G3FillTex();
            lblG3Status.Text = string.Format("{0}：{1}", Chara.GroupName(g), on ? "整组勾上" : "整组取消");
        }

        /// <summary>所有组的所有行一起勾上/取消（不管「组过滤」当前显示哪一组）。</summary>
        void G3GroupAllEvery(bool on)
        {
            if (ps3 == null || gridG.Rows.Count == 0) return;
            int n = 0;
            foreach (DataGridViewRow row in gridG.Rows)
            {
                if (row.Tag == null) continue;
                if (!Chara.IsCharaKey((int)row.Tag)) continue;
                row.Cells["on"].Value = on;
                n++;
            }
            G3FillTex();
            lblG3Status.Text = L.F("全部 {0} 个「组·部位」{1}", n, on ? "已勾上" : "已取消");
            Log3(L.F("[组过滤] 全部 {0} 行 {1}", n, on ? "勾上" : "取消"));
        }

        Dictionary<int, Rule> G3Rules()
        {
            var d = new Dictionary<int, Rule>();
            foreach (DataGridViewRow row in gridG.Rows)
            {
                if (row.Tag == null) continue;
                bool on = row.Cells["on"].Value is bool && (bool)row.Cells["on"].Value;
                if (!on) continue;
                string f = row.Cells["fmt"].Value as string ?? "自动";
                string s = row.Cells["max"].Value as string ?? "1024";
                var r = new Rule(FmtItemToKey(f), SizeItemToNum(s));
                SetRuleExtra(r, SharpenItemToVal(row.Cells["sh"].Value as string),
                             ShModeItemToVal(row.Cells["shm"].Value as string),
                             GrayItemToVal(row.Cells["ga"].Value as string),
                             UpTableToVal(row.Cells["up"].Value as string));
                d[(int)row.Tag] = r;
            }
            return d;
        }

        void G3FillTex()
        {
            if (g3Filling) return;
            g3Filling = true;
            suppressPreview = true;                     // v2.25：明细表重建期间不预览
            try { G3FillTexCore(); }
            finally { g3Filling = false; suppressPreview = false; }
            UpdatePreview(picG3, lblG3Prev, gridGT, g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim());
        }

        void G3FillTexCore()
        {
            gridGT.Rows.Clear();
            if (ps3 == null || gridG.CurrentRow == null || gridG.CurrentRow.Tag == null) return;
            var pi = ps3.Find((int)gridG.CurrentRow.Tag);
            if (pi == null) return;
            var rules = G3Rules();
            foreach (var tx in pi.Tex)
            {
                Rule ov = null;
                bool hasOv = tx.Id != null && texOv3.TryGetValue(tx.Id.ToString(), out ov) && ov != null;
                int idx = gridGT.Rows.Add();
                var row = gridGT.Rows[idx];
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
                row.Cells["tup"].Value = (ov == null) ? "不放大" : UpValToItem(ov.Up);   // ov 可能是 null
                if (hasOv) { row.Cells["tfinal"].Value = "单张指定 " + RuleText(ov); row.DefaultCellStyle.BackColor = Color.FromArgb(255, 250, 225); }
                else
                {
                    Rule merged; string why;
                    row.Cells["tfinal"].Value = TexTool.MergeSlotRules(tx.SlotKeys, rules, out merged, out why)
                        ? RuleText(merged) + (tx.Shared ? "（按共用规则）" : "")
                        : "原样不动 —— " + why;
                }
            }
            ApplySort(gridGT, stTex3);          // v2.30：明细表的表头排序在重填后回放
        }

        void G3TexOvChanged()
        {
            if (g3Filling) return;
            if (ps3 == null || gridGT.Rows.Count == 0 || gridG.CurrentRow == null) return;
            var pi = ps3.Find((int)gridG.CurrentRow.Tag);
            foreach (DataGridViewRow row in gridGT.Rows)
            {
                string id = row.Tag as string;
                if (id == null) continue;
                string f = row.Cells["trule"].Value as string ?? "跟随部位";
                string s = row.Cells["tsize"].Value as string ?? "跟随部位";
                string sh = row.Cells["tsh"].Value as string ?? "跟随部位";
                string shm = row.Cells["tshm"].Value as string ?? "跟随部位";
                string ga = row.Cells["tga"].Value as string ?? "跟随部位";
                string up = row.Cells["tup"].Value as string ?? "跟随部位";
                if (f == "跟随部位" && s == "跟随部位" && sh == "跟随部位"
                    && shm == "跟随部位" && ga == "跟随部位" && UpTableToVal(up) < 0) texOv3.Remove(id);
                else
                {
                    var r = new Rule(f == "跟随部位" ? "auto" : FmtItemToKey(f),
                                     s == "跟随部位" ? 1024 : SizeItemToNum(s));
                    SetRuleExtra(r, SharpenItemToVal(sh), ShModeItemToVal(shm), GrayItemToVal(ga), UpTableToVal(up));
                    texOv3[id] = r;
                }
            }
            G3FillTex();
        }

        /// <summary>把当前界面设置打包成预设（键就是「组·部位」，同款预设跨卡可用）。</summary>
        TexTool.Preset G3BuildPreset()
        {
            var pf = new TexTool.Preset { Source = g3Cards.Count > 0 ? Path.GetFileName(g3Cards[0]) : "", IsChara = true };
            pf.Parts = G3Rules();
            if (ps3 != null)
            {
                // 人物卡预设的指纹用「本体」签名：换装因卡而异，用全量签名会让同款检测误判
                var all = Chara.BodySigs(TexTool.Peek(txtG3Card.Text.Trim().Length > 0 && File.Exists(txtG3Card.Text.Trim()) ? txtG3Card.Text.Trim() : (g3Cards.Count > 0 ? g3Cards[0] : "")));
                pf.SigSet = new List<string>(all);
                pf.SigSet.Sort(StringComparer.Ordinal);
            }
            foreach (var kv in texOv3)
            {
                var pt = new TexTool.PresetTex { Id = kv.Key, Rule = kv.Value };
                if (ps3 != null)
                    foreach (var tx in ps3.All)
                        if (tx.Id != null && tx.Id.ToString() == kv.Key) pt.Sigs = new List<string>(tx.Sigs);
                pf.Textures.Add(pt);
            }
            return pf;
        }

        void G3SavePreset()
        {
            if (ps3 == null) { MessageBox.Show(L.Tr("请先读取一张人物卡。")); return; }
            var pf = G3BuildPreset();
            string card = g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim();
            string nm = AskName("保存为预设（预设\\chara）",
                "预设名称（会存成 [preset]名称_" + Path.GetFileNameWithoutExtension(card) + ".png，封面就是这张人物卡的卡面）", "我的组·部位设置");
            if (nm == null) return;
            string path = Path.Combine(PresetIo.EnsureDir(PresetIo.KindChara), PresetIo.FileName(nm, card));
            try
            {
                string json = PresetIo.Envelope(PresetIo.KindChara, PresetIo.Sanitize(nm), Path.GetFileName(card),
                    "chara", pf.MinMatch, pf.Uniform, pf.SigSet, pf.Parts, pf.Textures, null, (int)numG3Quality.Value, protectAlphaCompat);
                PresetIo.Save(path, json, card);
                Log3("已保存预设 " + path);
                lblG3Status.Text = "预设已保存：" + Path.GetFileName(path);
            }
            catch (Exception ex) { MessageBox.Show(L.Tr("预设存不下来：\n") + ex.Message); }
        }

        void G3LoadPreset()
        {
            if (ps3 == null) { MessageBox.Show(L.Tr("请先读取一张人物卡。")); return; }
            var d = new OpenFileDialog
            {
                Filter = "预设（卡面 PNG / JSON）|*.png;*.json|卡面预设 PNG|*.png|老的 JSON 预设|*.json|所有文件|*.*",
                InitialDirectory = PresetIo.EnsureDir(PresetIo.KindChara),
                Title = "读取预设（默认在 preset\\chara 里）"
            };
            if (d.ShowDialog() != DialogResult.OK) return;
            TexTool.Preset pf;
            try { pf = TexTool.PresetLoad(d.FileName); }
            catch (Exception ex) { MessageBox.Show(L.Tr("预设读不了：\n") + ex.Message); return; }
            Log3("预设文件：" + d.FileName);
            foreach (DataGridViewRow row in gridG.Rows)
            {
                if (row.Tag == null) continue;
                Rule r;
                bool has = pf.Parts.TryGetValue((int)row.Tag, out r) && r != null;
                row.Cells["on"].Value = has && r.Format != "keep";
                row.Cells["fmt"].Value = has ? KeyToFmtItem(r.Format) : "原样不动";
                row.Cells["max"].Value = has && r.Size > 0 ? r.Size.ToString() : "不缩放";
                row.Cells["sh"].Value = has ? SharpenValToItem(r.SharpenPct) : "100%";
                row.Cells["shm"].Value = has ? ShModeValToItem(r.SharpenMode) : "对比度自适应";
                row.Cells["ga"].Value = has ? GrayValToItem(r.GrayA) : "开";
                row.Cells["up"].Value = (r == null) ? "不放大" : UpValToItem(r.Up);       // r 可能是 null
            }
            texOv3.Clear();
            int hit = 0; var missed = new List<string>();
            if (ps3 != null)
                foreach (var pt in pf.Textures)
                {
                    var targets = new List<TexTool.PartTex>();
                    foreach (var tx in ps3.All)
                    {
                        if (tx.Id == null) continue;
                        if (tx.Id.ToString() == pt.Id) { targets.Add(tx); continue; }
                        foreach (var s in pt.Sigs) if (tx.Sigs.Contains(s)) { targets.Add(tx); break; }
                    }
                    if (targets.Count == 0) { missed.Add("TexID " + pt.Id); continue; }
                    foreach (var tx in targets) { texOv3[tx.Id.ToString()] = pt.Rule; hit++; }
                }
            G3FillTex();
            Log3(L.F("已读取预设 {0}：部位规则 {1} 条，单贴图规则落地 {2} 张，未匹配 {3} 张",
                pf.Source, pf.Parts.Count, hit, missed.Count));
            lblG3Status.Text = L.F("预设已套到界面：单贴图命中 {0} 张，未匹配 {1} 张", hit, missed.Count);
        }

        /// <summary>压缩：对 g3Cards 里的每张卡套用当前设置。</summary>
        void G3Run()
        {
            if (ps3 == null) { MessageBox.Show(L.Tr("请先「② 读取」一张人物卡。")); return; }
            var rules = G3Rules();
            if (rules.Count == 0) { MessageBox.Show(L.Tr("一个「组·部位」都没勾。")); return; }
            if (g3Cards.Count == 0) { MessageBox.Show(L.Tr("没有要处理的卡。")); return; }
            string od = txtG3Out.Text.Trim();
            string baseDir = Path.GetDirectoryName(g3Cards[0]) ?? ".";
            if (od.Length == 0) { od = Path.GetDirectoryName(g3Cards[0]) ?? "."; txtG3Out.Text = od; }
            try { Directory.CreateDirectory(od); TexTool.MarkOut(od); }
            catch (Exception ex) { MessageBox.Show(L.Tr("输出目录建不出来：\n") + od + "\n" + ex.Message); return; }
            var cards = new List<string>(g3Cards);
            var pf = G3BuildPreset();
            bool overwrite = chkG3Overwrite.Checked; const bool protect = protectAlphaCompat;
            int quality = (int)numG3Quality.Value;
            string suffix = txtG3Suffix.Text;
            logG3.Clear();
            Log3("输出目录: " + od);
            Log3(L.F("{0} 张人物卡；勾选的「组·部位」{1} 个，单张贴图指定 {2} 张",
                cards.Count, rules.Count, texOv3.Count));
            Busy3(true);
            cancelFlag = false;
            var th = new System.Threading.Thread(delegate ()
            {
                long before = 0, after = 0;
                int nOk = 0, nFail = 0, nCopy = 0;
                var failList = new List<string>();
                var skipList = new List<string>();
                var partList = new List<string>();
                try
                {
                    for (int i = 0; i < cards.Count; i++)
                    {
                        if (cancelFlag) break;
                        string fn = Path.GetFileName(cards[i]);
                        string stem = Path.GetFileNameWithoutExtension(cards[i]);
                        string dst = Path.Combine(od, stem + suffix + ".png");
                        if (File.Exists(dst) && !overwrite)
                        {
                            int k = 2;
                            while (File.Exists(Path.Combine(od, string.Format("{0}{1}_{2}.png", stem, suffix, k)))) k++;
                            dst = Path.Combine(od, string.Format("{0}{1}_{2}.png", stem, suffix, k));
                        }
                        Post("status3", L.F("压缩 {0}/{1}: {2}", i + 1, cards.Count, fn));
                        Post("log3", string.Format("===== [{0}/{1}] {2}", i + 1, cards.Count, fn));
                        Post("log3", "  …开始压缩（读取中，大卡会比较久）");
                        RepackResult r;
                        try
                        {
                            r = TexTool.Repack(cards[i], dst, null, 1024, "auto", quality, 1024, null, protect,
                                               rules, pf,
                                               delegate (string s) { Post("log3", s); },
                                               delegate (int d, int tt) { Post("progress3", d, tt); },
                                               delegate { return cancelFlag; });
                        }
                        catch (Exception ex)
                        {
                            nFail++; Post("log3", "[X] 失败: " + ex.Message);
                            failList.Add(fn + " —— " + ex.Message);
                            continue;
                        }
                        foreach (var sk in r.Skipped) partList.Add(fn + ": " + sk);
                        if (r.Rc == 3 || r.Rc == 6)
                        {
                            string why0 = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                            Post("log3", fn + " 跳过 —— " + why0);
                            skipList.Add(fn + " —— " + why0);
                            continue;
                        }
                        if (r.Rc != 0)
                        {
                            nFail++;
                            string why = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                            Post("log3", "[X] 失败: " + why);
                            failList.Add(fn + " —— " + why);
                            continue;
                        }
                        Post("log3", "  …已写出，正在校验结构");
                        string msg;
                        bool ok = TexTool.QuickVerify(dst, out msg);
                        Post("log3", "  …校验完成");
                        if (!ok) { nFail++; failList.Add(fn + " —— 产出校验失败: " + msg); Post("log3", "[X] 校验失败: " + msg); continue; }
                        if (r.Copied) { nCopy++; Post("log3", "[复制] " + fn + " —— 处理不了，已原样复制到输出目录"); continue; }
                        nOk++; before += r.CardBefore; after += r.CardAfter;
                        Post("log3", string.Format("[OK] {0} -> {1}（{2:0.0}%）→ {3}",
                            TexTool.Human(r.CardBefore), TexTool.Human(r.CardAfter),
                            100.0 * r.CardAfter / Math.Max(1, r.CardBefore), dst));
                    }
                    TexTool.PrintSummary(delegate (string s) { Post("log3", s); }, nOk, skipList.Count, nFail,
                        before, after, skipList, failList, partList);
                    if (nCopy > 0) Post("log3", L.F("另有 {0} 张处理不了的卡原样复制到输出目录（未压缩，体积不变）。", nCopy));
                    string sum = L.F("人物卡完成：成功 {0} 张，跳过 {1} 张，原样复制 {2} 张，失败 {3} 张；{4} -> {5}（{6:0.0}%）",
                        nOk, skipList.Count, nCopy, nFail, TexTool.Human(before), TexTool.Human(after),
                        before > 0 ? 100.0 * after / before : 0);
                    Post("log3", sum);
                    Post("done3", sum);
                }
                catch (Exception ex) { Post("log3", ex.ToString()); Post("done3", "运行出错，见日志"); }
            });
            th.IsBackground = true; th.Start();
        }

        void Busy3(bool b) { btnG3Run.Enabled = !b; btnG3Cancel.Enabled = b; }

        // ---- 供 uitest 自检 ----
        public void UiG3Load(string card)
        {
            g3Cards.Clear();
            g3Cards.Add(card);
            txtG3Card.Text = card;
            G3Load();
        }
        /// <summary>一次读多张（批量自检）。</summary>
        public void UiG3LoadMany(string[] cards)
        {
            g3Cards.Clear();
            g3Cards.AddRange(cards);
            txtG3Card.Text = cards.Length == 1 ? cards[0] : L.F("（已选 {0} 张）", cards.Length);
            G3Load();
        }
        public void UiG3SelectGroup(int g)
        {
            foreach (DataGridViewRow row in gridG.Rows)
            {
                if (row.Tag == null) continue;
                if (Chara.IsCharaKey((int)row.Tag) && Chara.KeyGroup((int)row.Tag) == g)
                {
                    gridG.CurrentCell = row.Cells["name"];
                    return;
                }
            }
        }
        public void UiG3SetRow(int i, bool on, string fmt, string max)
        {
            UiG3SetRow(i, on, fmt, max, null, null, null);
        }
        public void UiG3SetRow(int i, bool on, string fmt, string max, string sh, string ga)
        {
            UiG3SetRow(i, on, fmt, max, sh, null, ga);
        }
        public void UiG3SetRow(int i, bool on, string fmt, string max, string sh, string shm, string ga)
        {
            UiG3SetRow(i, on, fmt, max, sh, shm, ga, null);
        }
        public void UiG3SetRow(int i, bool on, string fmt, string max, string sh, string shm, string ga, string up)
        {
            if (i < 0 || i >= gridG.Rows.Count) return;
            gridG.Rows[i].Cells["on"].Value = on;
            gridG.Rows[i].Cells["fmt"].Value = fmt;
            gridG.Rows[i].Cells["max"].Value = max;
            if (sh != null) gridG.Rows[i].Cells["sh"].Value = sh;
            if (shm != null) gridG.Rows[i].Cells["shm"].Value = shm;
            if (ga != null) gridG.Rows[i].Cells["ga"].Value = ga;
            if (up != null) gridG.Rows[i].Cells["up"].Value = up;
            G3FillTex();
        }
        public void UiG3SetTexRow(int i, string f, string s, string sh, string ga)
        {
            UiG3SetTexRow(i, f, s, sh, null, ga);
        }
        public void UiG3SetTexRow(int i, string f, string s, string sh, string shm, string ga)
        {
            if (i < 0 || i >= gridGT.Rows.Count) return;
            gridGT.Rows[i].Cells["trule"].Value = f;
            gridGT.Rows[i].Cells["tsize"].Value = s;
            if (sh != null) gridGT.Rows[i].Cells["tsh"].Value = sh;
            if (shm != null) gridGT.Rows[i].Cells["tshm"].Value = shm;
            if (ga != null) gridGT.Rows[i].Cells["tga"].Value = ga;
            if (up != null) gridGT.Rows[i].Cells["tup"].Value = up;
            G3TexOvChanged();
        }
        public string UiG3TexOvDump()
        {
            var l = new List<string>();
            foreach (var kv in texOv3)
                l.Add(kv.Key + "=" + kv.Value.Format + "/" + kv.Value.Size
                      + (kv.Value.AllFollow ? "" : "[sh=" + kv.Value.SharpenPct
                         + ",shm=" + (kv.Value.SharpenMode ?? "") + ",ga=" + kv.Value.GrayA + "]"));
            l.Sort();
            return string.Join(" ", l.ToArray());
        }
        public void UiG3GroupAll(bool on) { G3GroupAll(on); }
        /// <summary>这一页是否还在后台读取（自检要等它结束）。</summary>
        public bool UiG3Busy { get { return g3Loading; } }
        public void UiG3Run() { G3Run(); }
        public string UiG3Status { get { return lblG3Status.Text; } }
        public string UiG3Log { get { return logG3.Text; } }
        public string UiG3Out { get { return txtG3Out.Text; } }
        public string UiG3FilterItems
        {
            get
            {
                var l = new List<string>();
                foreach (var it in cbG3Filter.Items) l.Add(it.ToString());
                return string.Join(" / ", l.ToArray());
            }
        }
        public void UiG3Filter(int idx) { if (idx >= 0 && idx < cbG3Filter.Items.Count) cbG3Filter.SelectedIndex = idx; }

        /// <summary>自检用：把「组·部位」表里第 a/b 行的「启用」取消掉（验证"仅已启用的部位"范围）。</summary>
        public string UiG3Uncheck(int a, int b)
        {
            foreach (int i in new int[] { a, b })
                if (i >= 0 && i < gridG.Rows.Count) gridG.Rows[i].Cells["on"].Value = false;
            int on = 0;
            foreach (DataGridViewRow r in gridG.Rows)
                if (r.Cells["on"].Value is bool && (bool)r.Cells["on"].Value) on++;
            return L.F("已取消第 {0}/{1} 行；现在启用 {2} 行", a, b, on);
        }

        /// <summary>自检用：走按钮那条路 ——「设置到目前浏览的部位」。</summary>
        public string UiG3UniToPart() { G3UniToPart(); return UiG3TexDump(); }

        /// <summary>自检用：一键统一的作用范围候选（验证「仅当前选中项目」在不在）。</summary>
        public string UiG3UniScopeItems
        {
            get
            {
                var l = new List<string>();
                foreach (var it in cbG3UniScope.Items) l.Add(it as string);
                return string.Join(" / ", l.ToArray());
            }
        }
        /// <summary>组·部位表快照：启用|组·部位|贴图|大小|独享|处理方式|最大边。</summary>
        public string UiG3GridDump()
        {
            var sb = new StringBuilder();
            foreach (DataGridViewRow row in gridG.Rows)
            {
                if (row.Tag == null) continue;
                sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}\r\n", row.Cells["on"].Value, row.Cells["name"].Value,
                    row.Cells["cnt"].Value, row.Cells["size"].Value, row.Cells["own"].Value,
                    row.Cells["fmt"].Value, row.Cells["max"].Value,
                    row.Cells["sh"].Value, row.Cells["shm"].Value, row.Cells["ga"].Value);
            }
            return sb.ToString();
        }
        /// <summary>贴图明细快照：TexID|类型|尺寸|字节|原格式|共用|处理方式|最大边|最终处理。</summary>
        public string UiG3TexDump()
        {
            var sb = new StringBuilder();
            foreach (DataGridViewRow row in gridGT.Rows)
                sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}\r\n", row.Cells["tid"].Value, row.Cells["tcls"].Value,
                    row.Cells["tdim"].Value, row.Cells["tbytes"].Value, row.Cells["tfmt"].Value,
                    row.Cells["tshare"].Value, row.Cells["trule"].Value, row.Cells["tsize"].Value,
                    row.Cells["tsh"].Value, row.Cells["tshm"].Value, row.Cells["tfinal"].Value);
            return sb.ToString();
        }
        // ---- 无头自检钩子（第 3 页：一键统一 + 缩略图预览）----

        public string UiG3UniItems
        {
            get
            {
                var l = new List<string>();
                foreach (var it in cbG3UniScope.Items) l.Add(it as string);
                return string.Join(" | ", l.ToArray());
            }
        }

        public string UiG3LastLog
        {
            get
            {
                var ls = logG3.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = ls.Length - 1; i >= 0; i--) if (ls[i].StartsWith("[统一]")) return ls[i];
                return "(无)";
            }
        }

        /// <summary>走真实路径按「统一范围」设置下拉 + 点「一键统一」按钮。</summary>
        public void UiG3Uni(string scope, string fmt, string size)
        {
            UiG3Uni(scope, fmt, size, null, null);
        }
        public void UiG3Uni(string scope, string fmt, string size, string sh, string ga)
        {
            UiG3Uni(scope, fmt, size, sh, null, ga);
        }
        public void UiG3Uni(string scope, string fmt, string size, string sh, string shm, string ga)
        {
            UiG3Uni(scope, fmt, size, sh, shm, ga, null);
        }
        public void UiG3Uni(string scope, string fmt, string size, string sh, string shm, string ga, string up)
        {
            int i = cbG3UniScope.Items.IndexOf(scope);
            if (i >= 0) cbG3UniScope.SelectedIndex = i;
            int j = cbG3UniFmt.Items.IndexOf(fmt);
            if (j >= 0) cbG3UniFmt.SelectedIndex = j;
            int k = cbG3UniSize.Items.IndexOf(size);
            if (k >= 0) cbG3UniSize.SelectedIndex = k;
            if (sh != null) { int a = cbG3UniSh.Items.IndexOf(sh); if (a >= 0) cbG3UniSh.SelectedIndex = a; }
            if (shm != null) { int sm2 = cbG3UniShm.Items.IndexOf(shm); if (sm2 >= 0) cbG3UniShm.SelectedIndex = sm2; }
            if (ga != null) { int b = cbG3UniGa.Items.IndexOf(ga); if (b >= 0) cbG3UniGa.SelectedIndex = b; }
            if (up != null) { int u = cbG3UniUp.Items.IndexOf(up); if (u >= 0) cbG3UniUp.SelectedIndex = u; }
            G3UniApply();
        }
        /// <summary>第 3 页一键统一里的两个整卡开关（自检用）：贴图细节保护 / 独占贴图保护。</summary>
        public string UiG3Cfg(int? sharpen, string mode, bool? grayA, bool? kernel, bool? own)
        {
            if (kernel.HasValue) chkKernel3.Checked = kernel.Value;
            if (own.HasValue) chkOwn3.Checked = own.Value;
            return L.F("第3页开关：细节保护={0} 独占保护={1}（上限 {2}）锐化={3}% 方式={4} 灰度2通道={5}"
                + " 放大下拉={6} 放大算法={7} 放大选项可见={8}",
                Resample.DetailMode, TexTool.ProtectOwn, TexTool.ProtectOwnMax,
                Resample.SharpenPct, Resample.SharpMode, PngEnc.UseGrayAlpha,
                cbG3UniUp.SelectedItem as string, Resample.UpKernel, cbG3UniUp.Visible);
        }

        /// <summary>v2.32：三页共用的「彩色贴图也走面积平均」（自检用）。传值 = 走真实勾选框设置，返回状态。
        /// 同时把三个勾选框的勾选态与 Enabled 一起回读 —— Enabled 跟着「贴图细节保护」走，
        /// 用来验证 SyncColorEnable 真的接上了。</summary>
        readonly ComboBox cbG3UniUp = new ThemedCombo();        // v2.34 放大
        readonly ComboBox cbG3UniUpK = new ThemedCombo();       // v2.34 放大算法

        /// <summary>自检用：第 3 页单张贴图覆盖的快照。</summary>
        public string UiTexOvDump3()
        {
            var l = new List<string>();
            foreach (var kv in texOv3)
                l.Add(kv.Key + "=" + kv.Value.Format + "/" + kv.Value.Size + RuleExtraDump(kv.Value));
            l.Sort();
            return string.Join(" ", l.ToArray());
        }

        public string UiColorKernel(bool? on)
        {
            if (on.HasValue)
            {
                chkColor.Checked = on.Value;
                chkColor2.Checked = on.Value;
                chkColor3.Checked = on.Value;
            }
            return L.F("彩色核={0} 细节保护={1} 勾选={2}/{3}/{4} 可用={5}/{6}/{7}",
                Resample.ColorMode ? "面积平均" : "GDI+双三次", Resample.DetailMode,
                chkColor.Checked, chkColor2.Checked, chkColor3.Checked,
                chkColor.Enabled, chkColor2.Enabled, chkColor3.Enabled);
        }

        /// <summary>自检用：重填第 3 页「组·部位」表（验证排序在重填后还在不在）。</summary>
        public string UiRefillG3()
        {
            G3Fill();
            Application.DoEvents();
            return DumpSortState(gridG, st3, "重填后") + DumpRows(gridG);
        }

        public int UiG3GridRows { get { return gridG.Rows.Count; } }

        /// <summary>第 3 页「贴图明细」表（gridGT）的行数 —— 悬浮预览窗跟随的是这张表。</summary>
        public int UiGTexRows { get { return gridGT.Rows.Count; } }

        /// <summary>选中「组·部位」表第 row 行，然后按真实 SelectionChanged 路径出预览，回报结果。</summary>
        public string UiG3PreviewAt(int row)
        {
            if (row < 0 || row >= gridG.Rows.Count) return "(越界)";
            gridG.CurrentCell = gridG.Rows[row].Cells["name"];
            Application.DoEvents();
            G3FillTex();                                   // 换行后明细要跟上
            if (gridGT.Rows.Count == 0) return "该行没有贴图";
            gridGT.CurrentCell = gridGT.Rows[0].Cells["tid"];
            UpdatePreview(picG3, lblG3Prev, gridGT, g3Cards.Count > 0 ? g3Cards[0] : txtG3Card.Text.Trim());
            Application.DoEvents();
            return L.F("{0} → 图={1} 尺寸={2} 说明={3}",
                gridG.Rows[row].Cells["name"].Value,
                picG3.Image == null ? "无" : (picG3.Image.Width + "×" + picG3.Image.Height),
                picG3.Image == null ? "-" : "有", lblG3Prev.Text);
        }
    }
}

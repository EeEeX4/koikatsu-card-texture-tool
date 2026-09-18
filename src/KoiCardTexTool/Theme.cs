using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace KoiCardTexTool
{
    /// <summary>外观主题。</summary>
    public enum ThemeKind { Light = 0, Dark = 1 }

    /// <summary>
    /// 外观主题（v1.1.0）。
    ///
    /// 做法和界面多语言一样：**一套颜色 + 遍历控件树统一套**，所以不用逐个控件改代码。
    /// 加上三处"系统画的部分"的处理（自绘页签、扁平按钮、深色标题栏/滚动条），
    /// 这样换肤看起来是完整的 —— 但 MessageBox、打开文件对话框仍是系统浅色，改不了。
    /// </summary>
    public static class Theme
    {
        public static ThemeKind Cur = ThemeKind.Light;

        /// <summary>自检用：命令行指定主题（null = 按保存的设置）。</summary>
        public static ThemeKind? Override;

        public static string Name(ThemeKind k) { return k == ThemeKind.Dark ? "Dark" : "Light"; }

        // ---- 颜色表（浅色偏白；深色白字暗底）----
        public static Color WindowBg, PanelBg, GroupBg, Text, SubText, Border, Accent, AccentText;
        public static Color InputBg, InputText, ButtonBg, ButtonText, ButtonBorder, ButtonHover;
        public static Color GridBg, GridAltBg, GridHeaderBg, GridHeaderText, GridSelBg, GridSelText, GridLine;
        public static Color LogBg, LogText, TabActiveBg, TabInactiveBg;
        public static Color SliderTrack, SliderFill, SliderThumb, Separator, CellInputBg, CellInputBorder;
        public static Color ComboBorder;

        public static void Set(ThemeKind k)
        {
            Cur = k;
            if (k == ThemeKind.Dark)
            {
                WindowBg = Color.FromArgb(32, 32, 34);
                PanelBg = Color.FromArgb(38, 38, 41);
                GroupBg = Color.FromArgb(38, 38, 41);
                Text = Color.FromArgb(240, 240, 242);
                SubText = Color.FromArgb(170, 172, 178);
                Border = Color.FromArgb(64, 65, 70);
                Accent = Color.FromArgb(72, 132, 224);
                AccentText = Color.White;
                InputBg = Color.FromArgb(46, 46, 50);
                InputText = Text;
                ButtonBg = Color.FromArgb(54, 54, 58);
                ButtonText = Text;
                ButtonBorder = Color.FromArgb(78, 79, 85);
                ButtonHover = Color.FromArgb(66, 66, 72);
                GridBg = Color.FromArgb(38, 38, 41);
                GridAltBg = Color.FromArgb(43, 43, 47);
                GridHeaderBg = Color.FromArgb(52, 52, 57);
                GridHeaderText = Text;
                GridSelBg = Color.FromArgb(60, 104, 176);
                GridSelText = Color.White;
                GridLine = Color.FromArgb(58, 58, 63);
                LogBg = Color.FromArgb(26, 26, 28);
                LogText = Color.FromArgb(214, 216, 220);
                TabActiveBg = Color.FromArgb(38, 38, 41);
                TabInactiveBg = Color.FromArgb(28, 28, 30);
                SliderTrack = Color.FromArgb(70, 70, 76);
                SliderFill = Accent;
                SliderThumb = Color.FromArgb(228, 230, 234);
                Separator = Color.FromArgb(58, 58, 63);
                CellInputBg = Color.FromArgb(48, 48, 53);      // 比表格底色亮一档
                CellInputBorder = Color.FromArgb(92, 94, 100);
                ComboBorder = Color.FromArgb(96, 98, 104);
            }
            else
            {
                // 浅色：比 WinForms 默认的灰更白（用户要求"更偏白色"）
                WindowBg = Color.White;
                PanelBg = Color.White;
                GroupBg = Color.White;
                Text = Color.FromArgb(32, 33, 36);
                SubText = Color.FromArgb(120, 122, 128);
                Border = Color.FromArgb(214, 216, 220);
                Accent = Color.FromArgb(38, 108, 204);
                AccentText = Color.White;
                InputBg = Color.White;
                InputText = Text;
                ButtonBg = Color.FromArgb(247, 248, 250);
                ButtonText = Text;
                ButtonBorder = Color.FromArgb(206, 208, 212);
                ButtonHover = Color.FromArgb(236, 240, 246);
                GridBg = Color.White;
                GridAltBg = Color.FromArgb(250, 251, 253);
                GridHeaderBg = Color.FromArgb(243, 245, 248);
                GridHeaderText = Text;
                GridSelBg = Color.FromArgb(214, 230, 252);
                GridSelText = Text;
                GridLine = Color.FromArgb(228, 230, 234);
                LogBg = Color.FromArgb(252, 252, 253);
                LogText = Color.FromArgb(48, 50, 54);
                TabActiveBg = Color.White;
                TabInactiveBg = Color.FromArgb(240, 241, 244);
                SliderTrack = Color.FromArgb(216, 218, 222);
                SliderFill = Accent;
                SliderThumb = Color.White;
                Separator = Color.FromArgb(232, 234, 238);
                CellInputBg = Color.FromArgb(246, 248, 251);   // 比纯白略灰，能看出是个输入格
                CellInputBorder = Color.FromArgb(188, 192, 200);
                ComboBorder = Color.FromArgb(186, 190, 198);
            }
        }

        // ---- 统一套色 -----------------------------------------------------------

        /// <summary>把主题套到一棵控件树上（保留各自"特殊控件"的例外处理）。</summary>
        public static void Apply(Control root)
        {
            if (root == null) return;
            ApplyOne(root);
            foreach (Control c in root.Controls) Apply(c);
        }

        static void ApplyOne(Control c)
        {
            var form = c as Form;
            if (form != null) { form.BackColor = WindowBg; form.ForeColor = Text; return; }
            var tab = c as TabControl;
            if (tab != null) { tab.BackColor = TabInactiveBg; tab.ForeColor = Text; foreach (TabPage p in tab.TabPages) p.BackColor = WindowBg; return; }
            var tb = c as TextBoxBase;
            if (tb != null)
            {
                tb.BackColor = LogBg; tb.ForeColor = LogText; tb.BorderStyle = BorderStyle.FixedSingle;
                DarkScroll(tb); return;
            }
            var grid = c as DataGridView;
            if (grid != null)
            {
                grid.BackgroundColor = GridBg;
                grid.GridColor = GridLine;
                grid.BorderStyle = BorderStyle.FixedSingle;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = GridHeaderBg;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = GridHeaderText;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeaderBg;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = GridHeaderText;
                grid.DefaultCellStyle.BackColor = GridBg;
                grid.DefaultCellStyle.ForeColor = Text;
                grid.DefaultCellStyle.SelectionBackColor = GridSelBg;
                grid.DefaultCellStyle.SelectionForeColor = GridSelText;
                grid.AlternatingRowsDefaultCellStyle.BackColor = GridAltBg;
                grid.AlternatingRowsDefaultCellStyle.ForeColor = Text;
                grid.RowHeadersDefaultCellStyle.BackColor = GridHeaderBg;
                grid.RowHeadersDefaultCellStyle.ForeColor = GridHeaderText;
                // 下拉列在编辑时的控件配色
                foreach (DataGridViewColumn col in grid.Columns)
                {
                    var cc = col as DataGridViewComboBoxColumn;
                    if (cc != null)
                    {
                        cc.FlatStyle = FlatStyle.Flat;      // 不设它就会按系统主题画成白色
                        cc.DefaultCellStyle.BackColor = CellInputBg;
                        cc.DefaultCellStyle.ForeColor = Text;
                        cc.DefaultCellStyle.SelectionBackColor = GridSelBg;
                        cc.DefaultCellStyle.SelectionForeColor = GridSelText;
                    }
                }
                DarkScroll(grid);
                HookCellBorder(grid);       // Flat 下拉格没有边框 → 自己画一圈，否则和背景融在一起
                return;
            }
            var btn = c as Button;
            if (btn != null)
            {
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderSize = 1;
                bool primary = (btn.Tag as string) == "primary";
                btn.BackColor = primary ? Accent : ButtonBg;
                btn.ForeColor = primary ? AccentText : ButtonText;
                btn.FlatAppearance.BorderColor = primary ? Accent : ButtonBorder;
                btn.FlatAppearance.MouseOverBackColor = primary ? Accent : ButtonHover;
                btn.UseVisualStyleBackColor = false;
                return;
            }
            var chk = c as CheckBox;
            if (chk != null && chk.Appearance == Appearance.Button)
            {
                // 「隐藏日志」这类按钮样式的勾选框
                chk.FlatStyle = FlatStyle.Flat;
                chk.BackColor = chk.Checked ? Accent : ButtonBg;
                chk.ForeColor = chk.Checked ? AccentText : ButtonText;
                chk.FlatAppearance.BorderColor = chk.Checked ? Accent : ButtonBorder;
                chk.UseVisualStyleBackColor = false;
                return;
            }
            var combo = c as ComboBox;
            if (combo != null)
            {
                // DropDownList 默认由**系统主题**绘制、忽略 BackColor（深色下就是一块白的），
                // 所以要 FlatStyle=Flat 才认颜色 —— 但 Flat 也把边框去掉了，深色下要自己补（见 ThemedCombo）。
                // 浅色主题下用 Standard：系统自带边框，且不用逐个控件自绘（省 ~50ms/次切页）。
                combo.FlatStyle = (Cur == ThemeKind.Dark) ? FlatStyle.Flat : FlatStyle.Standard;
                combo.BackColor = InputBg;
                combo.ForeColor = InputText;
                DarkScroll(combo);
                return;
            }
            if (c is CheckBox || c is RadioButton)
            {
                c.BackColor = (c.Parent != null) ? c.Parent.BackColor : PanelBg;
                c.ForeColor = Text;
                return;
            }
            if (c is GroupBox)
            {
                c.BackColor = GroupBg;
                c.ForeColor = Text;
                return;
            }
            if (c is Label)
            {
                c.BackColor = Color.Transparent;
                if (c.ForeColor.ToArgb() != Color.Gray.ToArgb() && c.ForeColor.ToArgb() != Color.FromArgb(120, 122, 128).ToArgb())
                    c.ForeColor = Text;                       // 灰色说明文字保留灰
                else
                    c.ForeColor = SubText;
                return;
            }
            if (c is Panel || c is FlowLayoutPanel || c is TableLayoutPanel || c is TabPage)
            {
                c.BackColor = (c is TabPage) ? WindowBg : PanelBg;
                c.ForeColor = Text;
                return;
            }
            if (c is ThemedSlider)
            {
                c.BackColor = PanelBg;
                return;
            }
            var split = c as Splitter;
            if (split != null) { split.BackColor = Separator; return; }
        }

        /// <summary>把滚动条画成深色（Win10 1809+ 有效；无效时保持系统样式，不影响功能）。</summary>
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        /// <summary>
        /// 给表格里的**下拉格**画一圈边框。
        /// 原因：下拉列必须设 FlatStyle=Flat（否则系统主题会画成白底、深色下不搭），
        /// 但扁平样式同时把边框也去掉了 —— 结果是"边框和背景颜色融为一体"，看不出是个下拉框
        /// （用户反馈）。这里按主题色自己补一圈，两种主题下都看得清。
        /// </summary>
        static void HookCellBorder(DataGridView g)
        {
            if (g.Tag as string == "cellborder") return;
            g.Tag = "cellborder";
            g.CellPainting += delegate (object se, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (!(g.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)) return;
                e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border
                    | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.ContentForeground
                    | DataGridViewPaintParts.Focus);
                var r = new Rectangle(e.CellBounds.X + 1, e.CellBounds.Y + 1,
                                      e.CellBounds.Width - 3, e.CellBounds.Height - 3);
                if (r.Width > 2 && r.Height > 2)
                    using (var p = new Pen(CellInputBorder))
                        e.Graphics.DrawRectangle(p, r);
                e.Handled = true;
            };
        }

        static void DarkScroll(Control c)
        {
            try
            {
                if (!c.IsHandleCreated) return;
                SetWindowTheme(c.Handle, Cur == ThemeKind.Dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        /// <summary>标题栏深色（Win10 20H1 用属性 20，老版本用 19；失败就算了）。</summary>
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static int TitleBarRc = -999;     // 自检用
        public static int FrameRc = -999;        // 自检用：标题栏/边框颜色是否被接受

        /// <summary>.NET 颜色 → DWM 要的 COLORREF（0x00BBGGRR）。</summary>
        static int Bgr(Color c) { return (c.R) | (c.G << 8) | (c.B << 16); }

        public static void ApplyTitleBar(Form f)
        {
            try
            {
                if (f == null || !f.IsHandleCreated) return;
                int on = Cur == ThemeKind.Dark ? 1 : 0;
                int rc = DwmSetWindowAttribute(f.Handle, 20, ref on, 4);
                if (rc != 0) rc = DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                TitleBarRc = rc;                 // 自检用：0 = 系统接受深色标题栏

                // 标题栏那一条和**窗口边框**还要另外指定颜色（Win11 22000+ 支持 35/34）——
                // 只设 20/19 时标题栏会变深，但边框往往还是白的（用户反馈"边框还是白色"）。
                // DWM 要的是 COLORREF（0x00BBGGRR），不是 .NET 的 ARGB。
                int cap = Cur == ThemeKind.Dark ? Bgr(WindowBg) : unchecked((int)0xFFFFFFFF);
                int bd = Cur == ThemeKind.Dark ? Bgr(Border) : unchecked((int)0xFFFFFFFF);
                int rc2 = DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);      // DWMWA_CAPTION_COLOR
                int rc3 = DwmSetWindowAttribute(f.Handle, 34, ref bd, 4);       // DWMWA_BORDER_COLOR
                FrameRc = rc2;
            }
            catch { }
        }

        /// <summary>句柄建好之后再套一次（滚动条 / 标题栏需要句柄）。</summary>
        public static void ApplyNative(Control root, Form owner)
        {
            if (root == null) return;
            DarkScroll(root);
            foreach (Control c in root.Controls) ApplyNative(c, owner);
            ApplyTitleBar(owner);
        }

        // ---- 持久化 -------------------------------------------------------------

        static string Path_
        {
            get
            {
                string b = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(b)) b = ".";
                return System.IO.Path.Combine(b, "ui-theme.txt");
            }
        }

        public static ThemeKind Load()
        {
            try
            {
                string p = Path_;
                if (Override.HasValue) return Override.Value;
                if (!File.Exists(p)) return ThemeKind.Light;
                string t = File.ReadAllText(p).Trim().ToLowerInvariant();
                return t.StartsWith("dark") ? ThemeKind.Dark : ThemeKind.Light;
            }
            catch { return ThemeKind.Light; }
        }

        public static void Save(ThemeKind k)
        {
            try { File.WriteAllText(Path_, k == ThemeKind.Dark ? "dark" : "light", new UTF8Encoding(false)); }
            catch { }
        }
    }

    /// <summary>
    /// 自绘滑块（替代 TrackBar）：TrackBar 是系统主题画的，深色下没法配合。
    /// 只实现用得到的 API，和 TrackBar 用法保持一致。
    /// </summary>
    public class ThemedSlider : Control
    {
        int _min, _max = 100, _val = 50;

        public ThemedSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 26;
            Width = 200;
            TabStop = false;
        }

        public int Minimum { get { return _min; } set { _min = value; if (_val < _min) Value = _min; Invalidate(); } }
        public int Maximum { get { return _max; } set { _max = value; if (_val > _max) Value = _max; Invalidate(); } }
        public int TickFrequency { get; set; }                 // 兼容 TrackBar 的写法，绘制时忽略
        public event EventHandler ValueChanged;

        public int Value
        {
            get { return _val; }
            set
            {
                int v = Math.Max(_min, Math.Min(_max, value));
                if (v == _val) return;
                _val = v;
                Invalidate();
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        int PosFromX(int x)
        {
            int pad = 10;
            int w = Math.Max(1, Width - pad * 2);
            double t = (double)(x - pad) / w;
            t = Math.Max(0, Math.Min(1, t));
            return _min + (int)Math.Round(t * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { Value = PosFromX(e.X); Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Capture && e.Button == MouseButtons.Left) Value = PosFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value = _val + (e.Delta > 0 ? 1 : -1) * Math.Max(1, (_max - _min) / 50);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int pad = 10, cy = Height / 2, h = 6;
            var track = new Rectangle(pad, cy - h / 2, Math.Max(2, Width - pad * 2), h);
            using (var b = new SolidBrush(Theme.SliderTrack)) FillRound(g, b, track, h / 2);
            double t = _max > _min ? (double)(_val - _min) / (_max - _min) : 0;
            int fw = (int)Math.Round(track.Width * t);
            if (fw > 0)
                using (var b = new SolidBrush(Theme.SliderFill))
                    FillRound(g, b, new Rectangle(track.X, track.Y, fw, h), h / 2);
            int cx = track.X + fw;
            using (var b = new SolidBrush(Theme.SliderThumb))
            using (var p = new Pen(Theme.Accent, 2))
            {
                g.FillEllipse(b, cx - 7, cy - 7, 14, 14);
                g.DrawEllipse(p, cx - 7, cy - 7, 14, 14);
            }
        }

        static void FillRound(Graphics g, Brush b, Rectangle r, int rad)
        {
            if (rad <= 0) { g.FillRectangle(b, r); return; }
            using (var path = new GraphicsPath())
            {
                int d = rad * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }
    }

    /// <summary>
    /// 带边框的下拉框。
    /// ComboBox 必须用 FlatStyle.Flat 才认主题颜色（否则系统主题画成白底，深色下不搭），
    /// 但扁平样式会把**边框也去掉** —— 于是"边框和背景融为一体"，看不出是个下拉框。
    /// 这里在 WM_PAINT 之后自己补一圈边框。
    /// </summary>
    public class ThemedCombo : ComboBox
    {
        static Pen borderPen;

        public ThemedCombo() { FlatStyle = FlatStyle.Flat; }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F) return;                 // WM_PAINT
            // 浅色主题下 FlatStyle 是 Standard（系统自带边框），不用自己画 ——
            // 自己画要 Graphics.FromHwnd，一个页面几十个下拉框会让切页变慢（实测 40ms → 90ms）。
            if (FlatStyle != FlatStyle.Flat) return;
            if (!IsHandleCreated || Width <= 2 || Height <= 2) return;
            try
            {
                if (borderPen == null || borderPen.Color != Theme.ComboBorder)
                    borderPen = new Pen(Theme.ComboBorder);
                using (var g = Graphics.FromHwnd(Handle))
                    g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            }
            catch { }
        }
    }

    /// <summary>自绘页签（默认的页签是系统画的，深色下不搭）。</summary>
    public class ThemedTabControl : TabControl
    {
        public ThemedTabControl()
        {
            // **不要设 UserPaint**：设了之后整个控件由我们负责绘制，
            // 页签文字就不会被画出来（实测：页签条变成一条空白）。只开双缓冲即可，
            // OwnerDrawFixed 本身会让我们接管每个页签的绘制。
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.ResizeRedraw, true);
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(180, 26);
            Padding = new Point(12, 4);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = e.Index == SelectedIndex;
            var r = GetTabRect(e.Index);
            using (var b = new SolidBrush(sel ? Theme.TabActiveBg : Theme.TabInactiveBg))
                g.FillRectangle(b, r);
            // 选中页签加一条强调色下边线
            if (sel)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, new Rectangle(r.X, r.Bottom - 3, r.Width, 3));
            string t = e.Index >= 0 && e.Index < TabPages.Count ? TabPages[e.Index].Text : "";
            using (var b = new SolidBrush(sel ? Theme.Text : Theme.SubText))
            using (var f = new Font(Font, sel ? FontStyle.Bold : FontStyle.Regular))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(t, f, b, r, sf);
            }
        }
    }
}

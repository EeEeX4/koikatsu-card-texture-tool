using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace KoiCardTexTool
{
    /// <summary>独立悬浮预览窗（v2.27）。
    ///
    /// 用途：把「选中部位的贴图明细」里当前那张贴图放到一个独立的窗口里放大看。
    /// 行为：
    ///   · 鼠标滚轮 = 以**光标位置**为中心放大/缩小（0.05x ~ 16x）
    ///   · 按住左键拖动 = 平移
    ///   · 双击 = 适应窗口；按 1 = 100%；按 0 或 F = 适应窗口
    ///   · 主界面切换贴图时**自动跟着换**（由 MainForm 调 SetImage，所有权一并转过来）
    /// 本窗口自己负责释放上一张图（SetImage 里 Dispose 旧的），再也不与主界面共用 Bitmap ——
    /// 2.25 那次「Parameter is not valid」就是共用对象被提前 Dispose 引起的，这里刻意避免。
    /// </summary>
    public sealed class PreviewForm : Form
    {
        Image _img;
        string _cap = "";
        float _zoom = 1f;
        PointF _off;                       // 图像左上角在控件坐标里的位置
        bool _dragging;
        Point _dragFrom;
        PointF _offFrom;
        readonly Label _hud = new Label
        {
            Dock = DockStyle.Top, Height = 22, AutoSize = false, AutoEllipsis = true,
            ForeColor = Color.FromArgb(30, 30, 40), BackColor = Color.FromArgb(238, 240, 244),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 4, 0)
        };
        readonly Label _hint = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true,
            ForeColor = Color.FromArgb(112, 118, 130), BackColor = Color.FromArgb(238, 240, 244),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 4, 0),
            Text = "滚轮缩放 · 左键拖动平移 · 双击适应窗口 · 1=100%"
        };
        // 底部两块：上面一行说明、下面一行快捷键提示。分开两块是为了各自单行省略号，
        // 说明行很长（带类型/共用情况/卡名），挤成一行会把提示顶出去。
        readonly Panel _bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        readonly Panel _view;

        public PreviewForm()
        {
            MainForm.SetAppIcon(this);          // v1.1.9：预览窗也用同一个图标
            Text = "贴图放大预览";
            ClientSize = new Size(760, 620);
            MinimumSize = new Size(260, 200);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;

            _view = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(58, 60, 66) };
            // 自绘：双缓冲 + 尺寸变化就重画，避免拖动时闪烁
            _view.GetType().GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(_view, true, null);
            _view.Paint += ViewPaint;
            _view.MouseWheel += ViewWheel;
            _view.MouseDown += ViewDown;
            _view.MouseMove += ViewMove;
            _view.MouseUp += ViewUp;
            _view.DoubleClick += delegate { FitToWindow(); Invalidate(); };

            // ⚠ 停靠顺序：Fill 的先加，贴边的后加（WinForms 按 z 序倒着停靠）
            Controls.Add(_view);
            Controls.Add(_bottom);
            _bottom.Controls.Add(_hint);
            _bottom.Controls.Add(_hud);
            _hud.Text = "打开时这里会显示当前贴图信息";
        }

        /// <summary>换图（所有权转移：本窗口负责释放上一张）。cap 是底部那行说明。</summary>
        public void SetImage(Image img, string cap)
        {
            var old = _img;
            _img = img;
            _cap = cap ?? "";
            if (old != null && !ReferenceEquals(old, img)) { try { old.Dispose(); } catch { } }
            if (_img != null) CenterView(); else { _off = PointF.Empty; }
            UpdateHud();
            _view.Invalidate();
        }

        public string State
        {
            get
            {
                return L.F("可见={0} 图={1} 缩放={2:0}% 说明={3}",
                    Visible, _img == null ? "无" : _img.Width + "×" + _img.Height, _zoom * 100f, _cap);
            }
        }

        /// <summary>自检用：模拟滚轮（delta&gt;0 放大）。</summary>
        public void ZoomBy(int delta, Point? at)
        {
            ZoomAt(at ?? new Point(_view.ClientSize.Width / 2, _view.ClientSize.Height / 2), delta);
            UpdateHud();
            _view.Invalidate();
        }

        void UpdateHud()
        {
            _hud.Text = _cap.Length == 0 ? "" : _cap;
        }

        void ZoomAt(Point at, int delta)
        {
            if (_img == null) return;
            float oldZoom = _zoom;
            float f = delta > 0 ? 1.15f : 1f / 1.15f;
            float nz = Math.Max(0.05f, Math.Min(16f, _zoom * f));
            if (nz == oldZoom) return;
            // 让"光标下的那个像素"保持在原地
            float ix = (at.X - _off.X) / oldZoom, iy = (at.Y - _off.Y) / oldZoom;
            _zoom = nz;
            _off = new PointF(at.X - ix * _zoom, at.Y - iy * _zoom);
        }

        void ViewWheel(object s, MouseEventArgs e) { ZoomAt(e.Location, e.Delta); UpdateHud(); _view.Invalidate(); }

        void ViewDown(object s, MouseEventArgs e)
        {
            if (_img == null) return;
            _dragging = true; _dragFrom = e.Location; _offFrom = _off;
            _view.Cursor = Cursors.SizeAll;
        }

        void ViewMove(object s, MouseEventArgs e)
        {
            if (!_dragging) return;
            _off = new PointF(_offFrom.X + (e.X - _dragFrom.X), _offFrom.Y + (e.Y - _dragFrom.Y));
            _view.Invalidate();
        }

        void ViewUp(object s, MouseEventArgs e) { _dragging = false; _view.Cursor = Cursors.Default; }

        /// <summary>缩放到刚好放进窗口（不放大超过 100%）。</summary>
        public void FitToWindow()
        {
            if (_img == null) return;
            float sx = (float)(_view.ClientSize.Width - 12) / _img.Width;
            float sy = (float)(_view.ClientSize.Height - 12) / _img.Height;
            _zoom = Math.Max(0.05f, Math.Min(1f, Math.Min(sx, sy)));
            CenterView();
        }

        /// <summary>图片居中（比窗口小就居中，比窗口大就贴左上）。</summary>
        void CenterView()
        {
            if (_img == null) return;
            float w = _img.Width * _zoom, h = _img.Height * _zoom;
            _off = new PointF(w <= _view.ClientSize.Width ? (_view.ClientSize.Width - w) / 2f : 8f,
                              h <= _view.ClientSize.Height ? (_view.ClientSize.Height - h) / 2f : 8f);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_img == null) return;
            if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) { _zoom = 1f; CenterView(); }
            else if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0 || e.KeyCode == Keys.F) FitToWindow();
            else if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add) ZoomAt(Center(), 120);
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract) ZoomAt(Center(), -120);
            else return;
            UpdateHud();
            _view.Invalidate();
        }

        Point Center() { return new Point(_view.ClientSize.Width / 2, _view.ClientSize.Height / 2); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_img != null) { CenterView(); _view.Invalidate(); }
        }

        void ViewPaint(object s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_view.BackColor);
            if (_img == null)
            {
                using (var b = new SolidBrush(Color.FromArgb(200, 200, 210)))
                    g.DrawString("在主界面的「选中部位的贴图明细」里点一行，这里就会显示那张贴图",
                        Font, b, new PointF(16, 16));
                return;
            }
            var dest = new RectangleF(_off.X, _off.Y, _img.Width * _zoom, _img.Height * _zoom);
            // 放大到 2 倍以上用最近邻，看得清真实像素；缩小时用双三次，避免锯齿
            g.InterpolationMode = _zoom >= 2f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = _zoom >= 2f ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.None;
            try { g.DrawImage(_img, dest); } catch { }
            // 棋盘底：方便看半透明贴图的 alpha
            if (Image.IsAlphaPixelFormat(_img.PixelFormat))
                using (var p = new Pen(Color.FromArgb(120, 255, 255, 255)))
                    g.DrawRectangle(p, dest.X, dest.Y, dest.Width, dest.Height);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            var old = _img; _img = null;
            if (old != null) { try { old.Dispose(); } catch { } }
            base.OnFormClosed(e);
        }
    }
}

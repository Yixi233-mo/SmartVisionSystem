using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using SmartVisionSystem.Core;
using SmartVisionSystem.Infrastructure;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 图像坐标 ↔ 屏幕坐标映射（PictureBox Zoom 模式）。
    /// 初版 MVP 的框选偏移就是错在这里：Zoom 模式会等比缩放并居中，必须按实际缩放系数换算，
    /// 否则框出来的模板跟框选位置对不上，匹配必然失败。
    /// </summary>
    public sealed class ImageMapper
    {
        public double Scale { get; private init; } = 1;
        public double OffsetX { get; private init; }
        public double OffsetY { get; private init; }
        public int ImageWidth { get; private init; }
        public int ImageHeight { get; private init; }

        public static ImageMapper Contain(int imgW, int imgH, Rectangle client)
        {
            if (imgW <= 0 || imgH <= 0 || client.Width <= 0 || client.Height <= 0)
                return new ImageMapper { ImageWidth = imgW, ImageHeight = imgH };

            double s = Math.Min(client.Width / (double)imgW, client.Height / (double)imgH);
            return new ImageMapper
            {
                Scale = s,
                OffsetX = client.X + (client.Width - imgW * s) / 2.0,
                OffsetY = client.Y + (client.Height - imgH * s) / 2.0,
                ImageWidth = imgW,
                ImageHeight = imgH
            };
        }

        public PointF ToClient(double ix, double iy)
            => new((float)(OffsetX + ix * Scale), (float)(OffsetY + iy * Scale));

        public PointF ToImage(double cx, double cy)
            => new((float)((cx - OffsetX) / Scale), (float)((cy - OffsetY) / Scale));

        public RectangleF RectToClient(double ix, double iy, double iw, double ih)
        {
            var p = ToClient(ix, iy);
            return new RectangleF(p.X, p.Y, (float)(iw * Scale), (float)(ih * Scale));
        }

        public Rectangle DestRect => new(
            (int)Math.Round(OffsetX), (int)Math.Round(OffsetY),
            (int)Math.Round(ImageWidth * Scale), (int)Math.Round(ImageHeight * Scale));
    }

    /// <summary>
    /// 实时画面控件：显示相机图像 + GDI 叠加层（基准十字、匹配框、偏差矢量、OK/NG 大字、CT）。
    /// 叠加层用 GDI 而不是 OpenCV 绘制，好处是零额外图像拷贝、文字清晰、且不污染送进算法的原图。
    /// </summary>
    public sealed class VideoPanel : Control
    {
        private readonly object _sync = new();
        private Bitmap? _frame;
        private VisionResult? _result;
        private Recipe? _recipe;

            [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Placeholder { get; set; } = "等待图像…";
            [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowOverlay { get; set; } = true;
        /// <summary>是否显示检测结论大标（生产监控用）</summary>
            [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowVerdict { get; set; } = true;
        /// <summary>ROI 选择框（参数页用），图像坐标系</summary>
            [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public RectangleF? SelectionRect { get; set; }

        public ImageMapper Mapper { get; private set; } = ImageMapper.Contain(1280, 960, new Rectangle(0, 0, 100, 100));
        public Size ImageSize => _frame?.Size ?? Size.Empty;

        public VideoPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            DoubleBuffered = true;
        }

        public void SetFrame(Bitmap bmp, VisionResult? result, Recipe? recipe)
        {
            Bitmap? old;
            lock (_sync)
            {
                old = _frame;
                _frame = bmp;
                _result = result;
                _recipe = recipe;
            }
            try
            {
                old?.Dispose();
                Invalidate();
            }
            catch { }
        }

        public void ClearFrame()
        {
            Bitmap? old;
            lock (_sync) { old = _frame; _frame = null; _result = null; }
            old?.Dispose();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            Bitmap? frame;
            VisionResult? result;
            Recipe? recipe;
            lock (_sync)
            {
                frame = _frame;
                result = _result;
                recipe = _recipe;
            }

            g.Clear(Color.FromArgb(8, 9, 11));

            if (frame == null)
            {
                DrawCentered(g, Placeholder);
                return;
            }

            ImageMapper map;
            try { map = ImageMapper.Contain(frame.Width, frame.Height, ClientRectangle); }
            catch { return; }
            Mapper = map;

            g.InterpolationMode = frame.Width > ClientRectangle.Width
                ? InterpolationMode.HighQualityBilinear
                : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle dest;
            try
            {
                dest = map.DestRect;
                g.DrawImage(frame, dest);
            }
            catch
            {
                return;
            }

            if (!ShowOverlay) return;

            DrawFrameBorder(g, dest);
            if (recipe != null) DrawBaseCross(g, map, recipe);
            if (result != null) DrawResult(g, map, result);
            if (SelectionRect.HasValue) DrawSelection(g, map, SelectionRect.Value);
        }

        private static void DrawCentered(Graphics g, string text)
        {
            using var f = Theme.F(12F);
            using var b = new SolidBrush(Theme.Muted);
            var size = g.MeasureString(text, f);
            g.DrawString(text, f, b, new PointF((g.VisibleClipBounds.Width - size.Width) / 2, (g.VisibleClipBounds.Height - size.Height) / 2));
        }

        private static void DrawFrameBorder(Graphics g, Rectangle dest)
        {
            using var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1);
            g.DrawRectangle(pen, dest.X, dest.Y, dest.Width - 1, dest.Height - 1);

            using var tick = new Pen(Color.FromArgb(120, Theme.Cyan), 2);
            const int len = 18;
            g.DrawLine(tick, dest.Left, dest.Top, dest.Left + len, dest.Top);
            g.DrawLine(tick, dest.Left, dest.Top, dest.Left, dest.Top + len);
            g.DrawLine(tick, dest.Right, dest.Top, dest.Right - len, dest.Top);
            g.DrawLine(tick, dest.Right, dest.Top, dest.Right, dest.Top + len);
            g.DrawLine(tick, dest.Left, dest.Bottom, dest.Left + len, dest.Bottom);
            g.DrawLine(tick, dest.Left, dest.Bottom, dest.Left, dest.Bottom - len);
            g.DrawLine(tick, dest.Right, dest.Bottom, dest.Right - len, dest.Bottom);
            g.DrawLine(tick, dest.Right, dest.Bottom, dest.Right, dest.Bottom - len);
        }

        private void DrawBaseCross(Graphics g, ImageMapper map, Recipe recipe)
        {
            var p = map.ToClient(recipe.ImageBaseX, recipe.ImageBaseY);
            using var pen = new Pen(Color.FromArgb(150, Theme.Cyan), 1) { DashStyle = DashStyle.Dash };
            const int r = 14;
            g.DrawLine(pen, p.X - r, p.Y, p.X + r, p.Y);
            g.DrawLine(pen, p.X, p.Y - r, p.X, p.Y + r);
            g.DrawEllipse(pen, p.X - r - 6, p.Y - r - 6, (r + 6) * 2, (r + 6) * 2);

            using var f = Theme.F(7.5F);
            using var b = new SolidBrush(Color.FromArgb(200, Theme.Cyan));
            g.DrawString("视觉基准", f, b, p.X + r + 4, p.Y - r - 16);
        }

        private void DrawResult(Graphics g, ImageMapper map, VisionResult result)
        {
            var color = result.IsOk ? Theme.Ok : Theme.Ng;

            var box = map.RectToClient(result.MatchedRect.X, result.MatchedRect.Y, result.MatchedRect.Width, result.MatchedRect.Height);
            using (var pen = new Pen(color, 2))
                g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

            if (result.CenterX > 0 || result.CenterY > 0)
            {
                var c = map.ToClient(result.CenterX, result.CenterY);
                using var cpen = new Pen(color, 1);
                const int s = 9;
                g.DrawLine(cpen, c.X - s, c.Y, c.X + s, c.Y);
                g.DrawLine(cpen, c.X, c.Y - s, c.X, c.Y + s);

                // 偏差矢量：从基准指向实测中心
                if (Math.Abs(result.OffsetXPx) > 0.4 || Math.Abs(result.OffsetYPx) > 0.4)
                {
                    var b = map.ToClient(result.BaseImageX, result.BaseImageY);
                    using var vpen = new Pen(Color.FromArgb(220, Theme.Warn), 2) { CustomEndCap = new AdjustableArrowCap(4, 5) };
                    g.DrawLine(vpen, b, c);
                    using var vf = Theme.F(8F, FontStyle.Bold);
                    using var vb = new SolidBrush(Theme.Warn);
                    g.DrawString($"Δ({result.OffsetXmm:+0.00;-0.00},{result.OffsetYmm:+0.00;-0.00})mm", vf, vb, (b.X + c.X) / 2 + 6, (b.Y + c.Y) / 2 - 14);
                }
            }

            // 左上角信息条
            var lines = new List<string>
            {
                $"得分 {result.Score:F3}   角度 {result.AngleDeg:+0.0;-0.0}°",
                $"面积 {(result.MaterialAreaPx > 0 ? result.MaterialAreaPx.ToString("F0") : "--")}px²   表面 {result.SurfaceRatio * 100:F2}%",
                $"视觉CT {result.CtMs:F1}ms"
            };
            DrawInfoBox(g, 10, 10, lines, color);

            if (!result.IsOk && !string.IsNullOrWhiteSpace(result.NgReason))
                DrawInfoBox(g, 10, 10 + 78, new List<string> { "NG: " + result.NgReason }, Theme.Ng);

            if (ShowVerdict)
            {
                string verdict = result.IsOk ? "OK" : "NG";
                using var f = new Font("Arial", 26F, FontStyle.Bold);
                var size = g.MeasureString(verdict, f);
                float x = ClientRectangle.Right - size.Width - 22;
                float y = 14;
                using var bg = new SolidBrush(Color.FromArgb(210, color));
                g.FillRectangle(bg, x - 10, y - 2, size.Width + 20, size.Height + 4);
                using var fg = new SolidBrush(Color.White);
                g.DrawString(verdict, f, fg, x, y);
            }
        }

        private static void DrawInfoBox(Graphics g, int x, int y, List<string> lines, Color accent)
        {
            using var f = Theme.Mono(9F);
            float w = 0, h = 0;
            foreach (var s in lines)
            {
                var sz = g.MeasureString(s, f);
                w = Math.Max(w, sz.Width);
                h += sz.Height;
            }
            using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
            g.FillRectangle(bg, x - 6, y - 4, w + 12, h + 8);
            using var bar = new SolidBrush(accent);
            g.FillRectangle(bar, x - 6, y - 4, 3, h + 8);
            using var fg = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            float cy = y;
            foreach (var s in lines)
            {
                g.DrawString(s, f, fg, x, cy);
                cy += g.MeasureString(s, f).Height;
            }
        }

        private static void DrawSelection(Graphics g, ImageMapper map, RectangleF r)
        {
            var box = map.RectToClient(r.X, r.Y, r.Width, r.Height);
            using var pen = new Pen(Theme.Ok, 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            using var f = Theme.F(8.5F, FontStyle.Bold);
            using var b = new SolidBrush(Theme.Ok);
            g.DrawString($"ROI {r.Width:F0}×{r.Height:F0}", f, b, box.X, Math.Max(0, box.Y - 18));
        }
    }

    /// <summary>KPI 数字卡</summary>
    public sealed class KpiCard : Panel
    {
        private readonly Label _value;
        private readonly Label _title;

        public KpiCard(string title, Color? accent = null, float valueSize = 22F)
        {
            BackColor = Theme.Card;
            Padding = new Padding(12, 8, 12, 8);

            var titleFont = Theme.F(9F);
            _title = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = Theme.LineHeight(titleFont) + 2,
                ForeColor = Theme.SubText,
                Font = titleFont,
                BackColor = Color.Transparent
            };
            _value = new Label
            {
                Text = "--",
                Dock = DockStyle.Fill,
                ForeColor = accent ?? Theme.Text,
                Font = Theme.F(valueSize, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            };
            Controls.Add(_value);
            Controls.Add(_title);

            Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                using var bar = new SolidBrush(accent ?? Theme.Accent);
                e.Graphics.FillRectangle(bar, 0, 0, 3, Height);
            };
        }

        public void SetValue(string text, Color? color = null)
        {
            _value.Text = text;
            if (color.HasValue) _value.ForeColor = color.Value;
        }

        /// <summary>卡片推荐高度：标题行 + 数值行 + 上下内边距，全部由字体行高推导</summary>
        public static int PreferredHeight(float valueSize)
            => Theme.LineHeight(Theme.F(9F)) + 2 + Theme.LineHeight(Theme.F(valueSize, FontStyle.Bold)) + 10 + 16;
    }

    /// <summary>顶栏状态胶囊</summary>
    public sealed class StatusPill : Panel
    {
        private readonly Label _label = new();
        private Color _dot = Theme.Muted;

        public StatusPill(string caption, int width = 150)
        {
            var capFont = Theme.F(7.5F);
            var valFont = Theme.F(9.5F, FontStyle.Bold);
            int capH = Theme.LineHeight(capFont);
            int valH = Theme.LineHeight(valFont);

            Width = width;
            Height = capH + valH + 10;      // 标题行 + 数值行 + 上下各 5px 呼吸，不写死像素
            BackColor = Theme.Card;
            Margin = new Padding(0, 0, 8, 0);

            var cap = new Label
            {
                Text = caption,
                Dock = DockStyle.Top,
                Height = capH,
                ForeColor = Theme.Muted,
                Font = capFont,
                BackColor = Color.Transparent,
                Padding = new Padding(16, 0, 0, 0)
            };
            _label.Dock = DockStyle.Fill;
            _label.ForeColor = Theme.Text;
            _label.Font = valFont;
            _label.BackColor = Color.Transparent;
            _label.Padding = new Padding(16, 0, 0, 0);
            _label.TextAlign = ContentAlignment.MiddleLeft;

            Controls.Add(_label);
            Controls.Add(cap);

            Paint += (_, e) =>
            {
                using var b = new SolidBrush(_dot);
                e.Graphics.FillEllipse(b, 6, Height / 2 - 3, 7, 7);
            };
        }

        public void Set(string text, Color? dot = null)
        {
            _label.Text = text;
            if (dot.HasValue) _dot = dot.Value;
            Invalidate();
        }
    }

    /// <summary>小工具：统一风格的输入行</summary>

    /// <summary>
    /// 深色输入框。只读状态的 TextBox 会忽略 BackColor、始终绘制系统底色，
    /// 必须向 edit 控件本身发 EM_SETBKGNDCOLOR 才能变深色（这就是历史页筛选框"纯白"的原因）。
    /// </summary>
    public sealed class DarkTextBox : TextBox
    {
        private const int EM_SETBKGNDCOLOR = 0x0443;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public DarkTextBox()
        {
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Color.FromArgb(28, 31, 37);
            ForeColor = Theme.Text;
            Font = Theme.Mono(9.5F);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyBackground();
        }

        public override Color BackColor
        {
            get => base.BackColor;
            set { base.BackColor = value; ApplyBackground(); }
        }

        private void ApplyBackground()
        {
            if (!IsHandleCreated) return;
            try { SendMessage(Handle, EM_SETBKGNDCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(BackColor)); }
            catch { }
        }
    }

    /// <summary>
    /// 深色下拉框。ComboBox 的关闭态由系统绘制，FlatStyle 也压不住白底，
    /// 这里改成自绘（DropdownList + OwnerDrawFixed），保证与主题一致。
    /// </summary>
    public sealed class DarkCombo : ComboBox
    {
        public DarkCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.FromArgb(28, 31, 37);
            ForeColor = Theme.Text;
            Font = Theme.F(9F);
            ItemHeight = Theme.LineHeight(Theme.F(9F)) + 4;
        }

        /// <summary>
        /// 下拉按钮（右侧箭头区）由系统主题绘制，OwnerDraw 盖不到，会露出一块白底。
        /// 这里在 WM_PAINT 之后补一层自绘：深色底 + 自绘箭头。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            const int WM_PAINT = 0x000F;
            const int WM_PRINT = 0x0317;         // DrawToBitmap 给子窗口发的是 WM_PRINT
            const int WM_PRINTCLIENT = 0x0318;
            if ((m.Msg != WM_PAINT && m.Msg != WM_PRINT && m.Msg != WM_PRINTCLIENT) || !IsHandleCreated) return;

            try
            {
                if (m.Msg == WM_PAINT) PaintArrow(Graphics.FromHwnd(Handle));
                else PaintArrow(Graphics.FromHdc(m.WParam));
            }
            catch { }
        }

        /// <summary>自绘下拉按钮区（深色底 + 箭头），盖住系统绘制的白色块</summary>
        private void PaintArrow(Graphics g)
        {
            int btnW = SystemInformation.VerticalScrollBarWidth;
            var rect = new Rectangle(Width - btnW - 1, 1, btnW, Height - 3);
            if (rect.Width <= 2 || rect.Height <= 2) return;

            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, rect);

            int cx = rect.Left + rect.Width / 2;
            int cy = rect.Top + rect.Height / 2;
            using var pen = new Pen(Theme.SubText, 1.4F);
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 4, cy - 2),
                new PointF(cx, cy + 2),
                new PointF(cx + 4, cy - 2),
            });
            g.Dispose();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool editing = (e.State & DrawItemState.ComboBoxEdit) != 0;

            using (var b = new SolidBrush(selected && !editing ? Theme.CardHover : BackColor))
                g.FillRectangle(b, e.Bounds);

            if (e.Index >= 0 && e.Index < Items.Count)
            {
                var text = Items[e.Index]?.ToString() ?? "";
                var color = selected && !editing ? Theme.Text : ForeColor;

                // 关闭态（ComboBoxEdit）的 e.Bounds 不含下拉箭头区域，若再减 24 会把"全部"截成"全..."
                var box = editing ? ClientRectangle : e.Bounds;
                int arrowW = editing ? 20 : 8;
                var textRect = new Rectangle(box.X + 6, box.Y, Math.Max(10, box.Width - arrowW - 8), box.Height);

                TextRenderer.DrawText(g, text, Font, textRect, color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
        }
    }

    public static class Field
    {
        public static Label Label(string text)
        {
            var f = Theme.F(9F);
            return new Label
            {
                Text = text,
                AutoSize = false,
                Width = 150,
                Height = Theme.LineHeight(f) + 6,   // 行高 + 上下各 3px，避免文字被控件边界切掉
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.SubText,
                Font = f,
                BackColor = Color.Transparent
            };
        }

        public static TextBox Input(string value, int width = 110) => new DarkTextBox
        {
            Text = value,
            Width = width,
            TextAlign = HorizontalAlignment.Right
        };

        public static ComboBox Combo(string[] items, int width = 110)
        {
            var c = new DarkCombo { Width = width };
            c.Items.AddRange(items);
            if (items.Length > 0) c.SelectedIndex = 0;
            return c;
        }
    }
}

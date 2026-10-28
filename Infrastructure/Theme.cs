using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SmartVisionSystem.Infrastructure
{
    /// <summary>
    /// 工业 HMI 视觉规范：深色主题 + 高对比状态色（OK 绿 / NG 红 / 待机灰 / 报警琥珀）。
    /// 现场强光环境下深色底 + 高亮状态色最易辨识，同时避免操作员长时间盯屏疲劳。
    /// </summary>
    public static class Theme
    {
        // ── 基础色板 ──
        public static readonly Color Bg = Color.FromArgb(24, 26, 31);
        public static readonly Color Panel = Color.FromArgb(33, 36, 42);
        public static readonly Color Card = Color.FromArgb(42, 46, 54);
        public static readonly Color CardHover = Color.FromArgb(52, 57, 66);
        public static readonly Color Border = Color.FromArgb(58, 63, 74);
        public static readonly Color Text = Color.FromArgb(232, 235, 240);
        public static readonly Color SubText = Color.FromArgb(150, 158, 172);
        public static readonly Color Muted = Color.FromArgb(104, 112, 126);

        // ── 状态色 ──
        public static readonly Color Accent = Color.FromArgb(38, 145, 255);
        public static readonly Color AccentDim = Color.FromArgb(28, 92, 160);
        public static readonly Color Ok = Color.FromArgb(34, 197, 94);
        public static readonly Color Ng = Color.FromArgb(239, 68, 68);
        public static readonly Color Warn = Color.FromArgb(245, 158, 11);
        public static readonly Color Cyan = Color.FromArgb(34, 211, 238);
        public static readonly Color Purple = Color.FromArgb(167, 139, 250);

        // ── 字体 ──
        public static Font F(float size, FontStyle style = FontStyle.Regular) => new("Microsoft YaHei UI", size, style);
        public static Font Mono(float size, FontStyle style = FontStyle.Regular) => new("Consolas", size, style);

        /// <summary>
        /// 字体实际单行高度（含中文行距）。所有"给文字留空间"的地方都应该用它算，
        /// 而不是写死 12/18/22 —— 写死的值在高 DPI 或换字体后必然把文字切掉一截。
        /// </summary>
        public static int LineHeight(Font font) => TextRenderer.MeasureText("设备状态Ag", font).Height;

        /// <summary>一行文字所需的控件高度（行高 + 视觉余量）</summary>
        public static int TextBoxHeight(Font font, int extra = 6) => LineHeight(font) + extra;

        /// <summary>
        /// 把一组按钮统一到同一尺寸：宽度取"实测文字最大值 + 左右留白"，高度取字体行高 + 上下留白。
        /// 必须在句柄创建后调用（OnHandleCreated），否则 125% 缩放下量到的是 96DPI 的值，文字仍会被截。
        /// </summary>
        public static void Equalize(IEnumerable<Button> buttons, int minWidth = 128, int padX = 44, int padY = 20)
        {
            var list = buttons.Where(b => b != null && !b.IsDisposed).ToList();
            if (list.Count == 0) return;

            int textW = 0;
            foreach (var b in list)
                textW = Math.Max(textW, TextRenderer.MeasureText(b.Text, b.Font).Width);

            int w = Math.Max(minWidth, textW + padX);
            int h = LineHeight(list[0].Font) + padY;

            foreach (var b in list)
            {
                b.AutoSize = false;
                b.Size = new Size(w, h);
            }
        }

        public static Button Btn(string text, Color? back = null, Color? fore = null, int w = 120, int h = 38, float fontSize = 10F)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(w, h),
                BackColor = back ?? Card,
                ForeColor = fore ?? Text,
                FlatStyle = FlatStyle.Flat,
                Font = F(fontSize, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 88);
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(b.BackColor, 0.15f);
            b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(b.BackColor, 0.1f);
            return b;
        }

        public static Label Lbl(string text, Color? fore = null, float size = 10F, FontStyle style = FontStyle.Regular)
            => new()
            {
                Text = text,
                ForeColor = fore ?? Text,
                Font = F(size, style),
                AutoSize = true,
                BackColor = Color.Transparent
            };

        /// <summary>卡片容器：标题 + 内容区，统一内边距与描边</summary>
        public static Panel Card2(string title, int height = 0)
        {
            var card = new Panel
            {
                BackColor = Theme.Card,
                Padding = new Padding(12, 34, 12, 12),
                Dock = DockStyle.Top,
                Height = height > 0 ? height : 160
            };
            card.Controls.Add(new Label
            {
                Text = title,
                Location = new Point(12, 8),
                AutoSize = true,
                Font = F(10F, FontStyle.Bold),
                ForeColor = SubText,
                BackColor = Color.Transparent
            });
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(Border);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                using var bar = new SolidBrush(Accent);
                e.Graphics.FillRectangle(bar, 0, 0, 3, Math.Min(28, card.Height));
            };
            return card;
        }

        /// <summary>把控件裁成圆角（工业按钮更贴近触摸屏风格）</summary>
        public static void Rounded(Control c, int radius = 6)
        {
            void Apply()
            {
                if (c.Width <= 0 || c.Height <= 0) return;
                using var path = RoundPath(new Rectangle(0, 0, c.Width, c.Height), radius);
                c.Region?.Dispose();
                c.Region = new Region(path);
            }
            c.SizeChanged += (_, _) => Apply();
            Apply();
        }

        private static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void StyleGrid(DataGridView g)
        {
            g.BackgroundColor = Panel;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Color.FromArgb(52, 57, 66);
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Card;
            g.ColumnHeadersDefaultCellStyle.ForeColor = SubText;
            g.ColumnHeadersDefaultCellStyle.Font = F(9.5F, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Card;
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.ColumnHeadersHeight = LineHeight(g.ColumnHeadersDefaultCellStyle.Font) + 14;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.DefaultCellStyle.BackColor = Panel;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = AccentDim;
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.DefaultCellStyle.Font = F(9.5F);
            g.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            g.RowTemplate.Height = LineHeight(g.DefaultCellStyle.Font) + 14;
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(37, 41, 48);
        }
    }

    /// <summary>窗口外观辅助：把 Win10/11 的标题栏切成深色，避免白标题栏 + 深色界面割裂</summary>
    public static class WindowStyle
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

        // uxtheme 未公开导出序号：123/133/135 是社区通用的暗色模式入口
        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int appMode);      // 2 = ForceDark

        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        private static bool _darkAppModeTried;

        /// <summary>让进程进入系统暗色模式（滚动条、下拉列表等原生控件才会跟着变深色）</summary>
        public static void EnableProcessDarkMode()
        {
            if (_darkAppModeTried) return;
            _darkAppModeTried = true;
            try { SetPreferredAppMode(2); } catch { }
        }

        /// <summary>
        /// 把控件（含其子控件、滚动条、滑块）切到系统深色主题。
        /// WinForms 的滚动条 / TrackBar / 下拉列表都由系统主题绘制，不套深色主题就会出现"整条纯白"。
        /// </summary>
        public static void ApplyDarkScrollbars(Control root)
        {
            try
            {
                if (root.IsHandleCreated)
                {
                    AllowDarkModeForWindow(root.Handle, true);
                    SetWindowTheme(root.Handle, "DarkMode_Explorer", null);
                }
                foreach (Control child in root.Controls) ApplyDarkScrollbars(child);
            }
            catch { /* 旧系统不支持则忽略，不影响功能 */ }
        }

        public static void UseDarkTitleBar(IntPtr handle)
        {
            try
            {
                if (Environment.OSVersion.Version.Major < 10) return;
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 20H1+），19 为旧版本号
                if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
            }
            catch { /* 非支持系统直接忽略 */ }
        }
    }
}

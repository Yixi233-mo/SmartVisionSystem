using System.Drawing.Drawing2D;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 登录页。布局完全按"控件实测高度"串联（上一个控件 Bottom + 间距 = 下一个控件 Top），
    /// 不写死任何纵坐标 —— 这样在 125%/150% 缩放或换字体时都不会出现文字互相压住、被截断。
    /// </summary>
    public sealed class LoginForm : Form
    {
        private const int BrandWidth = 360;

        private readonly TextBox _cboUser = new DarkTextBox();
        private readonly Button _btnUserPick = new();
        private readonly TextBox _txtPwd = new();
        private readonly CheckBox _chkRemember = new();
        private readonly Label _lblError = new();
        private readonly Label _lblUserMeta = new();
        private readonly Label _lblCaps = new();
        private readonly Button _btnLogin = Theme.Btn("登  录", Theme.Accent, Color.White, 380, 46, 11.5F);

        private static readonly string LastUserFile = Path.Combine(AppPaths.DataDir, "lastuser.txt");

        public LoginForm()
        {
            UserStore.EnsureSeeded();

            Text = "Smart Vision System — 登录";
            ClientSize = new Size(880, 568);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            Font = Theme.F(9.5F);
            KeyPreview = true;
            AcceptButton = _btnLogin;

            // 先加 Fill 面板、后加 Left 面板：WinForms 按 z 逆序停靠
            BuildLoginPanel();
            BuildBrandPanel();

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
                _lblCaps.Visible = Control.IsKeyLocked(Keys.CapsLock);
            };
            Shown += (_, _) =>
            {
                LoadLastUser();
                if (_cboUser.Text.Length > 0) _txtPwd.Focus(); else _cboUser.Focus();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowStyle.UseDarkTitleBar(Handle);
            WindowStyle.ApplyDarkScrollbars(this);
        }

        // ─────────────────────────── 右侧登录区 ───────────────────────────

        private void BuildLoginPanel()
        {
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };

            var titleFont = Theme.F(17F, FontStyle.Bold);
            var subFont = Theme.F(9F);
            var labelFont = Theme.F(9F);
            var metaFont = Theme.F(8.5F);
            var smallFont = Theme.F(8.5F);

            const int x = 48;
            int y = 46;

            var title = new Label
            {
                Text = "账号登录",
                ForeColor = Theme.Text,
                Font = titleFont,
                AutoSize = false,
                Size = new Size(360, Theme.LineHeight(titleFont) + 8),
                Location = new Point(x, y),
                BackColor = Color.Transparent
            };
            y = title.Bottom + 2;

            var sub = new Label
            {
                Text = "请使用产线分配的账号登录，权限由管理员维护",
                ForeColor = Theme.SubText,
                Font = subFont,
                AutoSize = false,
                Size = new Size(400, Theme.LineHeight(subFont) + 2),
                Location = new Point(x + 2, y),
                BackColor = Color.Transparent
            };
            y = sub.Bottom + 20;

            // 账号
            var lblUser = MakeLabel("账号", labelFont, x, y, 380);
            y = lblUser.Bottom + 2;

            const int fieldW = 380;
            const int pickW = 34;

            _cboUser.Font = Theme.F(11F);
            _cboUser.TextAlign = HorizontalAlignment.Left;
            _cboUser.Location = new Point(x, y);
            _cboUser.Width = fieldW - pickW;

            _btnUserPick.Text = "▾";
            _btnUserPick.Size = new Size(pickW, _cboUser.Height);
            _btnUserPick.Location = new Point(x + fieldW - pickW, y);
            _btnUserPick.FlatStyle = FlatStyle.Flat;
            _btnUserPick.BackColor = Theme.Card;
            _btnUserPick.ForeColor = Theme.SubText;
            _btnUserPick.Font = Theme.F(9F);
            _btnUserPick.Cursor = Cursors.Hand;
            _btnUserPick.TabStop = false;
            _btnUserPick.FlatAppearance.BorderColor = Theme.Border;
            _btnUserPick.FlatAppearance.BorderSize = 1;
            _btnUserPick.Click += (_, _) => ShowUserPicker();

            _cboUser.TextChanged += (_, _) => UpdateUserMeta();
            _cboUser.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoLogin(); } };
            y = _cboUser.Bottom + 4;          // 高度由字体决定，必须读实际值

            _lblUserMeta.Location = new Point(x + 2, y);
            _lblUserMeta.Width = 380;
            _lblUserMeta.Height = Theme.LineHeight(metaFont) + 2;
            _lblUserMeta.AutoSize = false;
            _lblUserMeta.ForeColor = Theme.Muted;
            _lblUserMeta.Font = metaFont;
            _lblUserMeta.BackColor = Color.Transparent;
            y = _lblUserMeta.Bottom + 10;

            // 口令
            var lblPwd = MakeLabel("口令", labelFont, x, y, 380);
            y = lblPwd.Bottom + 2;

            _txtPwd.Location = new Point(x, y);
            _txtPwd.Width = 380;
            _txtPwd.UseSystemPasswordChar = true;
            _txtPwd.BackColor = Color.FromArgb(28, 31, 37);
            _txtPwd.ForeColor = Theme.Text;
            _txtPwd.BorderStyle = BorderStyle.FixedSingle;
            _txtPwd.Font = Theme.F(11F);
            _txtPwd.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoLogin(); } };
            y = _txtPwd.Bottom + 8;

            // 选项行
            var chkShow = new CheckBox
            {
                Text = "显示口令",
                Location = new Point(x + 2, y),
                Width = 96,
                Height = Theme.LineHeight(smallFont) + 6,
                ForeColor = Theme.SubText,
                Font = smallFont,
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            chkShow.CheckedChanged += (_, _) => _txtPwd.UseSystemPasswordChar = !chkShow.Checked;

            _chkRemember.Text = "记住账号";
            _chkRemember.Location = new Point(x + 104, y);
            _chkRemember.Width = 96;
            _chkRemember.Height = Theme.LineHeight(smallFont) + 6;
            _chkRemember.Checked = File.Exists(LastUserFile);
            _chkRemember.ForeColor = Theme.SubText;
            _chkRemember.Font = smallFont;
            _chkRemember.BackColor = Color.Transparent;
            _chkRemember.FlatStyle = FlatStyle.Flat;

            _lblCaps.Text = "⚠ 大写锁定已开启";
            _lblCaps.Location = new Point(x + 206, y + 2);
            _lblCaps.Width = 170;
            _lblCaps.Height = Theme.LineHeight(smallFont) + 4;
            _lblCaps.AutoSize = false;
            _lblCaps.ForeColor = Theme.Warn;
            _lblCaps.Font = smallFont;
            _lblCaps.BackColor = Color.Transparent;
            _lblCaps.Visible = false;
            y = chkShow.Bottom + 8;

            _lblError.Location = new Point(x + 2, y);
            _lblError.Width = 380;
            _lblError.Height = Theme.LineHeight(Theme.F(9F)) + 2;
            _lblError.AutoSize = false;
            _lblError.ForeColor = Theme.Ng;
            _lblError.Font = Theme.F(9F);
            _lblError.BackColor = Color.Transparent;
            y = _lblError.Bottom + 6;

            _btnLogin.Location = new Point(x, y);
            _btnLogin.Click += (_, _) => DoLogin();
            y = _btnLogin.Bottom + 14;

            var footerFont = Theme.F(8.5F);
            var footer = new Label
            {
                Text = "首次部署账号：admin / admin123\r\nengineer / eng123　　operator / 空口令",
                ForeColor = Theme.Muted,
                Font = footerFont,
                AutoSize = false,
                Location = new Point(x + 2, y),
                Width = 396,
                Height = 2 * Theme.LineHeight(footerFont) + 6,
                BackColor = Color.Transparent
            };
            y = footer.Bottom + 4;

            var env = new Label
            {
                Text = $"{Environment.MachineName}  ·  v2.2  ·  仿真模式（无需相机/PLC）",
                ForeColor = Color.FromArgb(90, 96, 108),
                Font = Theme.F(8F),
                AutoSize = false,
                Location = new Point(x + 2, y),
                Width = 396,
                Height = Theme.LineHeight(Theme.F(8F)) + 2,
                BackColor = Color.Transparent
            };

            UpdateUserMeta();

            right.Controls.AddRange(new Control[]
            {
                title, sub, lblUser, _cboUser, _btnUserPick, _lblUserMeta, lblPwd, _txtPwd,
                chkShow, _chkRemember, _lblCaps, _lblError, _btnLogin, footer, env
            });
            Controls.Add(right);
        }

        private static Label MakeLabel(string text, Font font, int x, int y, int width) => new()
        {
            Text = text,
            Font = font,
            ForeColor = Theme.SubText,
            AutoSize = false,
            Location = new Point(x, y),
            Width = width,
            Height = Theme.LineHeight(font) + 4,
            BackColor = Color.Transparent
        };

        private void UpdateUserMeta()
        {
            var name = _cboUser.Text.Trim();
            if (name.Length == 0)
            {
                _lblUserMeta.Text = "输入账号名，或点右侧箭头选择已启用账号";
                _lblUserMeta.ForeColor = Theme.Muted;
                return;
            }

            var acc = UserStore.Find(name);
            if (acc == null)
            {
                _lblUserMeta.Text = "账号不存在";
                _lblUserMeta.ForeColor = Theme.Ng;
                return;
            }

            _lblUserMeta.Text = acc.Enabled
                ? $"{acc.DisplayName} · {acc.RoleText} · 上次登录 {acc.LastLoginText}"
                : $"{acc.DisplayName} · 已停用";
            _lblUserMeta.ForeColor = acc.Enabled ? Theme.Muted : Theme.Warn;
        }

        // ─────────────────────────── 左侧品牌区 ───────────────────────────

        private void BuildBrandPanel()
        {
            var left = new Panel { Dock = DockStyle.Left, Width = BrandWidth, BackColor = Color.FromArgb(17, 19, 24) };
            left.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var accent = new SolidBrush(Theme.Accent);
                g.FillRectangle(accent, 0, 0, 4, left.Height);

                using var glow = new LinearGradientBrush(
                    new Rectangle(-180, left.Height - 320, 460, 460),
                    Color.FromArgb(46, Theme.Accent), Color.FromArgb(0, Theme.Accent), 45f);
                g.FillEllipse(glow, -180, left.Height - 320, 460, 460);

                using var grid = new Pen(Color.FromArgb(16, 255, 255, 255));
                for (int gx = 24; gx < left.Width; gx += 24) g.DrawLine(grid, gx, 0, gx, left.Height);
                for (int gy = 24; gy < left.Height; gy += 24) g.DrawLine(grid, 0, gy, left.Width, gy);
            };

            var bigFont = new Font("Segoe UI Semibold", 21F, FontStyle.Bold);
            int bigLine = Theme.LineHeight(bigFont);

            const int x = 38;
            int y = 52;

            var title = new Label
            {
                Text = "Smart Vision",
                ForeColor = Color.White,
                Font = bigFont,
                AutoSize = false,
                Size = new Size(300, bigLine + 4),
                Location = new Point(x, y),
                BackColor = Color.Transparent
            };
            y = title.Bottom + 2;

            var title2 = new Label
            {
                Text = "System",
                ForeColor = Theme.Accent,
                Font = bigFont,
                AutoSize = false,
                Size = new Size(300, bigLine + 4),
                Location = new Point(x, y),
                BackColor = Color.Transparent
            };
            y = title2.Bottom + 8;

            var rule = new Panel { Location = new Point(x + 2, y), Size = new Size(46, 3), BackColor = Theme.Accent };
            y += rule.Height + 8;

            var subFont = Theme.F(9.5F);
            var sub = new Label
            {
                Text = "工业视觉引导与质量检测上位机",
                ForeColor = Color.FromArgb(150, 158, 172),
                Font = subFont,
                AutoSize = false,
                Size = new Size(300, Theme.LineHeight(subFont) + 2),
                Location = new Point(x + 2, y),
                BackColor = Color.Transparent
            };
            y = sub.Bottom + 26;

            var features = new (string Title, string Desc)[]
            {
                ("视觉定位", "模板匹配 + 亚像素，毫秒级偏差输出"),
                ("运动纠偏", "三轴插补、软限位、Z 轴安全互锁"),
                ("质量追溯", "SN 级判定记录，偏差与阈值可回查"),
                ("纯软仿真", "无相机/PLC 亦可跑通产线闭环"),
            };

            var fTitleFont = Theme.F(9.5F, FontStyle.Bold);
            var fDescFont = Theme.F(8.5F);
            foreach (var (fTitle, fDesc) in features)
            {
                var dot = new Label
                {
                    Text = "◆",
                    ForeColor = Theme.Accent,
                    Font = Theme.F(7F),
                    AutoSize = true,
                    Location = new Point(x + 2, y + 5),
                    BackColor = Color.Transparent
                };
                var t = new Label
                {
                    Text = fTitle,
                    ForeColor = Color.FromArgb(226, 230, 236),
                    Font = fTitleFont,
                    AutoSize = false,
                    Size = new Size(260, Theme.LineHeight(fTitleFont) + 2),
                    Location = new Point(x + 30, y),
                    BackColor = Color.Transparent
                };
                var d = new Label
                {
                    Text = fDesc,
                    ForeColor = Color.FromArgb(128, 136, 150),
                    Font = fDescFont,
                    AutoSize = false,
                    Size = new Size(290, Theme.LineHeight(fDescFont) + 2),
                    Location = new Point(x + 30, t.Bottom + 1),
                    BackColor = Color.Transparent
                };
                left.Controls.AddRange(new Control[] { dot, t, d });
                y = d.Bottom + 14;
            }

            left.Controls.AddRange(new Control[] { title, title2, rule, sub });

            var ver = new Label
            {
                Text = "v2.2  ·  SVS 视觉平台  ·  仿真版",
                ForeColor = Color.FromArgb(96, 104, 118),
                Font = Theme.F(8F),
                AutoSize = false,
                Width = 300,
                Height = Theme.LineHeight(Theme.F(8F)) + 2,
                BackColor = Color.Transparent
            };
            // 版本号贴底：随面板高度变化重新定位，不被内容挤走
            void PlaceVer() => ver.Location = new Point(42, Math.Max(y, left.Height - ver.Height - 22));
            left.Resize += (_, _) => PlaceVer();
            PlaceVer();
            left.Controls.Add(ver);

            Controls.Add(left);
        }

        /// <summary>已启用账号列表（自绘深色菜单，替代 ComboBox 的白色下拉按钮）</summary>
        private void ShowUserPicker()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                Font = Theme.F(10F),
                ShowImageMargin = false
            };

            var users = UserStore.List().Where(u => u.Enabled).ToList();
            if (users.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("（暂无可用账号）") { Enabled = false });

            foreach (var u in users)
            {
                var item = new ToolStripMenuItem($"{u.UserName}    · {u.RoleText}") { ForeColor = Theme.Text };
                item.Click += (_, _) =>
                {
                    _cboUser.Text = u.UserName;
                    _txtPwd.Focus();
                };
                menu.Items.Add(item);
            }

            menu.Show(_btnUserPick, new Point(0, _btnUserPick.Height));
        }

        // ─────────────────────────── 登录逻辑 ───────────────────────────

        private void LoadLastUser()
        {
            try
            {
                if (!File.Exists(LastUserFile)) return;
                var name = File.ReadAllText(LastUserFile).Trim();
                if (name.Length == 0) return;
                _cboUser.Text = name;
                UpdateUserMeta();
            }
            catch { }
        }

        private void SaveLastUser(string name)
        {
            try
            {
                if (_chkRemember.Checked) File.WriteAllText(LastUserFile, name);
                else if (File.Exists(LastUserFile)) File.Delete(LastUserFile);
            }
            catch { }
        }

        private void DoLogin()
        {
            var user = _cboUser.Text.Trim();
            if (user.Length == 0)
            {
                ShowError("请输入账号");
                _cboUser.Focus();
                return;
            }

            _btnLogin.Enabled = false;
            _btnLogin.Text = "验证中…";
            Application.DoEvents();

            try
            {
                if (Auth.TryLogin(user, _txtPwd.Text, out _, out var error))
                {
                    SaveLastUser(user);
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                ShowError("登录失败：" + error);
                _txtPwd.SelectAll();
                _txtPwd.Focus();
            }
            finally
            {
                _btnLogin.Enabled = true;
                _btnLogin.Text = "登  录";
            }
        }

        private void ShowError(string message) => _lblError.Text = "✕  " + message;
    }
}

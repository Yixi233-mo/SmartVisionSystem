using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 用户管理（仅管理员可见）：账号的增删改、角色分配、启用/停用、口令重置。
    ///
    /// 内置三条防呆规则，避免"把自己锁在门外"：
    /// ① 不能删除当前登录账号；
    /// ② 不能删除/停用/降级最后一个启用状态的管理员；
    /// ③ 口令以 PBKDF2 派生存储，任何界面都不回显明文。
    /// </summary>
    public sealed class UserManageView : UserControl
    {
        private readonly DataGridView _grid = new();
        private readonly TextBox _txtUser = Field.Input("", 210);
        private readonly TextBox _txtName = Field.Input("", 210);
        private readonly ComboBox _cboRole = Field.Combo(new[] { "操作员", "设备工程师", "管理员" }, 210);
        private readonly CheckBox _chkEnabled = new();
        private readonly TextBox _txtRemark = Field.Input("", 210);
        private readonly TextBox _txtPwd = Field.Input("", 210);
        private readonly TextBox _txtPwd2 = Field.Input("", 210);
        private readonly Label _lblHint = new();
        private readonly Button _btnSave = Theme.Btn("保存", Theme.Ok, Color.White, 264, 34, 9.5F);
        private readonly Button _btnNew = Theme.Btn("新增账号", Theme.Card, Theme.Text, 100, 34, 9.5F);
        private readonly Button _btnDel = Theme.Btn("删除", Theme.Ng, Color.White, 100, 34, 9.5F);
        private readonly Button _btnReset = Theme.Btn("重置口令", Theme.Card, Theme.Cyan, 264, 34, 9.5F);

        private List<UserAccount> _users = new();
        private UserAccount? _editing;
        private bool _isNew;

        public UserManageView()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;

            // 二次闸门：即便被程序化创建，非管理员也拿不到任何账号数据
            if (!IsAdmin)
            {
                BuildDenied();
                return;
            }

            // Fill 面板必须最先加入，否则右侧编辑面板会被它覆盖
            BuildGrid();
            BuildEditor();
            BuildToolbar();

            _txtUser.TextAlign = HorizontalAlignment.Left;
            _txtName.TextAlign = HorizontalAlignment.Left;
            _txtRemark.TextAlign = HorizontalAlignment.Left;
            Reload();
        }

        private static bool IsAdmin => Auth.Current.Can(Permission.ManageUsers);

        // ─────────────────────────── 布局 ───────────────────────────

        private void BuildToolbar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Panel, Padding = new Padding(12, 9, 12, 9) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };

            var btnRefresh = Theme.Btn("刷新", Theme.Card, Theme.Text, 76, 34, 9F);
            btnRefresh.Click += (_, _) => Reload();
            _btnNew.Click += (_, _) => StartNew();
            _btnDel.Click += (_, _) => DeleteSelected();

            foreach (var b in new[] { btnRefresh, _btnNew, _btnDel })
            {
                b.Margin = new Padding(0, 0, 8, 0);
                flow.Controls.Add(b);
            }

            var lblStat = new Label
            {
                Text = $"当前登录：{Auth.Current.DisplayName}（{Auth.Current.UserName}）",
                ForeColor = Theme.Muted,
                Font = Theme.F(9F),
                AutoSize = true,
                Dock = DockStyle.Right,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 10, 6, 0)
            };

            bar.Controls.Add(flow);
            bar.Controls.Add(lblStat);
            Controls.Add(bar);
        }

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            Theme.StyleGrid(_grid);
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            // 列宽按内容自适应：不再写死像素，避免"设备工程师"被截成"设备工程..."
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            _grid.Columns.Add("账号", "账号");
            _grid.Columns.Add("姓名", "姓名");
            _grid.Columns.Add("角色", "角色");
            _grid.Columns.Add("状态", "状态");
            _grid.Columns.Add("创建时间", "创建时间");
            _grid.Columns.Add("最近登录", "最近登录");
            _grid.Columns.Add("次数", "登录次数");
            _grid.Columns.Add("备注", "备注");

            _grid.Columns["备注"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;   // 备注吃掉剩余宽度
            _grid.SelectionChanged += (_, _) => LoadSelected();
            Controls.Add(_grid);
        }

        private void BuildEditor()
        {
            var right = new Panel { Dock = DockStyle.Right, Width = 320, BackColor = Theme.Panel, Padding = new Padding(14, 12, 14, 12), AutoScroll = true };

            var card = Theme.Card2("账号信息", 0);
            card.Dock = DockStyle.None;
            card.Width = 292;
            card.Location = new Point(0, 0);

            int y = 46;
            AddRow(card, "账号", _txtUser, ref y);
            AddRow(card, "姓名", _txtName, ref y);
            AddRow(card, "角色", _cboRole, ref y);
            AddRow(card, "备注", _txtRemark, ref y);

            _chkEnabled.Text = "启用该账号";
            _chkEnabled.Checked = true;
            _chkEnabled.Location = new Point(14, y + 2);
            _chkEnabled.Width = 200;
            _chkEnabled.Height = Theme.LineHeight(_chkEnabled.Font) + 6;
            _chkEnabled.ForeColor = Theme.Text;
            _chkEnabled.Font = Theme.F(9F);
            _chkEnabled.BackColor = Color.Transparent;
            _chkEnabled.FlatStyle = FlatStyle.Flat;
            card.Controls.Add(_chkEnabled);
            y += _chkEnabled.Height + 10;

            AddRow(card, "新口令", _txtPwd, ref y);
            AddRow(card, "确认口令", _txtPwd2, ref y);

            // 两个按钮并排，省下一行高度（竖向空间本来就紧）
            int halfW = (292 - 14 * 2 - 8) / 2;
            _btnSave.Size = new Size(halfW, 34);
            _btnSave.Location = new Point(14, y + 8);
            _btnSave.Click += (_, _) => Save();
            _btnReset.Size = new Size(halfW, 34);
            _btnReset.Location = new Point(14 + halfW + 8, y + 8);
            _btnReset.Click += (_, _) => ResetPassword();
            card.Controls.Add(_btnSave);
            card.Controls.Add(_btnReset);
            card.Height = y + 56;      // 卡片高度由内容决定，不写死

            // 每行控制在单行宽度内：8.5pt 中文在 125% 缩放下每字约 14px，292px 只放得下 ~20 字
            var hintLines = new[]
            {
                "• 新增需填口令，编辑可留空",
                "• 重置口令需填两次新口令",
                "• 至少保留一个启用的管理员",
                "• 不能删除或停用当前账号",
                "• 变更写入 logs 系统日志",
            };
            _lblHint.Font = Theme.F(8.5F);
            _lblHint.Text = string.Join("\n", hintLines);      // 必须先赋文本再量高度，否则量的是空串
            _lblHint.Location = new Point(0, card.Height + 12);
            _lblHint.Width = 292;
            _lblHint.Height = TextRenderer.MeasureText(_lblHint.Text, _lblHint.Font,
                new Size(292, 0), TextFormatFlags.WordBreak).Height + 8;
            _lblHint.ForeColor = Theme.Muted;
            _lblHint.BackColor = Color.Transparent;

            right.Controls.Add(card);
            right.Controls.Add(_lblHint);
            Controls.Add(right);
        }

        private static void AddRow(Control parent, string label, Control input, ref int y)
        {
            var lbl = Field.Label(label);
            lbl.Width = 292;
            int lblH = lbl.Height;

            lbl.Location = new Point(14, y);
            input.Location = new Point(14, y + lblH);
            input.Width = 264;
            input.Height = Math.Max(input.Height, Theme.LineHeight(input.Font) + 10);

            parent.Controls.Add(lbl);
            parent.Controls.Add(input);
            y += lblH + input.Height + 8;
        }

        /// <summary>非管理员访问时的占位页（不含任何账号信息）</summary>
        private void BuildDenied()
        {
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var lbl = new Label
            {
                Text = "权限不足\r\n\r\n用户管理仅对管理员开放。\r\n如需调整账号，请联系产线管理员登录后操作。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.SubText,
                Font = Theme.F(11F),
                BackColor = Color.Transparent
            };
            box.Controls.Add(lbl);
            Controls.Add(box);
        }

        // ─────────────────────────── 数据 ───────────────────────────

        private void Reload()
        {
            _users = UserStore.List();
            _grid.Rows.Clear();
            foreach (var u in _users)
            {
                int idx = _grid.Rows.Add(u.UserName, u.DisplayName, u.RoleText, u.StatusText,
                                          u.CreatedAt.ToString("yy-MM-dd HH:mm"), u.ShortLastLogin, u.LoginCount, u.Remark);
                _grid.Rows[idx].DefaultCellStyle.ForeColor = u.Enabled ? Theme.Text : Theme.Muted;
                if (!u.Enabled) _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(40, 32, 32);
            }

            if (_grid.Rows.Count > 0)
            {
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
                _grid.Rows[0].Selected = true;
            }
            if (!_isNew) LoadSelected();
        }

        private void LoadSelected()
        {
            if (_isNew || _grid.CurrentRow == null) return;
            int i = _grid.CurrentRow.Index;
            if (i < 0 || i >= _users.Count) return;


            _editing = _users[i];
            _txtUser.Text = _editing.UserName;
            _txtUser.ReadOnly = true;
            _txtName.Text = _editing.DisplayName;
            _cboRole.SelectedIndex = (int)_editing.Role;
            _chkEnabled.Checked = _editing.Enabled;
            _txtRemark.Text = _editing.Remark;
            _txtPwd.Text = "";
            _txtPwd2.Text = "";
            _btnSave.Text = "保存";

        }

        private void StartNew()
        {
            if (!IsAdmin) return;
            _isNew = true;
            _editing = null;

            _txtUser.Text = "";
            _txtUser.ReadOnly = false;
            _txtName.Text = "";
            _cboRole.SelectedIndex = 0;
            _chkEnabled.Checked = true;
            _txtRemark.Text = "";
            _txtPwd.Text = "";
            _txtPwd2.Text = "";
            _btnSave.Text = "创建账号";

            _txtUser.Focus();
        }

        private void Save()
        {
            if (!IsAdmin) return;

            var account = new UserAccount
            {
                UserName = _txtUser.Text.Trim(),
                DisplayName = _txtName.Text.Trim(),
                Role = (UserRole)Math.Max(0, _cboRole.SelectedIndex),
                Enabled = _chkEnabled.Checked,
                Remark = _txtRemark.Text.Trim()
            };

            if (_isNew)
            {
                if (!CheckPasswordPair(out var pwd)) return;
                var (ok, err) = UserStore.Add(account, pwd);
                if (!ok) { Fail(err); return; }
                Log.Info("用户", $"管理员 {Auth.Current.UserName} 创建账号 {account.UserName}");
                _isNew = false;
                Reload();
                Toast($"账号 {account.UserName} 已创建");
                return;
            }

            if (_editing == null) { Fail("请先在左侧选择一个账号"); return; }
            account.Id = _editing.Id;
            account.UserName = _editing.UserName;

            var (uOk, uErr) = UserStore.Update(account);
            if (!uOk) { Fail(uErr); return; }

            if (_txtPwd.Text.Length > 0 || _txtPwd2.Text.Length > 0)
            {
                if (!CheckPasswordPair(out var pwd2)) return;
                var (pOk, pErr) = UserStore.ChangePassword(account.UserName, pwd2);
                if (!pOk) { Fail(pErr); return; }
            }

            Auth.RefreshCurrent();
            Reload();
            Toast($"账号 {account.UserName} 已保存");
        }

        private void ResetPassword()
        {
            if (!IsAdmin) return;
            var name = _isNew ? _txtUser.Text.Trim() : _editing?.UserName ?? "";
            if (name.Length == 0) { Fail("请先选择账号"); return; }
            if (!CheckPasswordPair(out var pwd)) return;

            var (ok, err) = UserStore.ChangePassword(name, pwd);
            if (!ok) { Fail(err); return; }

            _txtPwd.Text = "";
            _txtPwd2.Text = "";
            Log.Info("用户", $"管理员 {Auth.Current.UserName} 重置了 {name} 的口令");
            Toast($"账号 {name} 的口令已重置");
        }

        private void DeleteSelected()
        {
            if (!IsAdmin) return;
            if (_isNew || _editing == null) { Fail("请先在左侧选择一个账号"); return; }

            if (_editing.UserName.Equals(Auth.Current.UserName, StringComparison.OrdinalIgnoreCase))
            {
                Fail("不能删除当前登录的账号");
                return;
            }
            if (MessageBox.Show(FindForm(), $"确认删除账号【{_editing.UserName} · {_editing.DisplayName}】？\n删除后该账号将无法登录，历史日志仍保留。",
                    "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var (ok, err) = UserStore.Delete(_editing.UserName);
            if (!ok) { Fail(err); return; }
            Reload();
            Toast("账号已删除");
        }

        private bool CheckPasswordPair(out string password)
        {
            password = _txtPwd.Text;
            if (password.Length == 0)
            {
                Fail("请填写口令（留空表示不修改已有账号的口令）");
                return false;
            }
            if (password != _txtPwd2.Text)
            {
                Fail("两次输入的口令不一致");
                return false;
            }
            if (password.Length < 4)
            {
                Fail("口令至少 4 位（现场建议 6 位以上，含字母与数字）");
                return false;
            }
            return true;
        }

        private void Fail(string message)
        {
            MessageBox.Show(FindForm(), message, "用户管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Log.Warn("用户", "用户管理操作被拒绝：" + message);
        }

        private void Toast(string message)
        {
            MessageBox.Show(FindForm(), message, "用户管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

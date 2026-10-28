using SmartVisionSystem.Core;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.UI;

namespace SmartVisionSystem
{
    /// <summary>
    /// 主窗体：左侧导航 + 顶部设备状态带 + 底部运行日志，所有页面共享同一套设备实例。
    /// 导航项按登录角色裁剪（操作员看不到参数/运动/仿真，符合现场防呆要求）。
    /// </summary>
    public class MainForm : Form
    {
        private readonly Panel _content = new();
        private readonly Dictionary<Type, UserControl> _views = new();
        private readonly List<(Button Btn, Type View, Permission? Need)> _nav = new();

        private readonly StatusPill _pillState = new("设备状态", 150);
        private readonly StatusPill _pillLine = new("输送线", 130);
        private readonly StatusPill _pillRecipe = new("当前配方", 160);
        private readonly StatusPill _pillYield = new("良率", 130);
        private readonly StatusPill _pillTotal = new("累计 / NG", 130);
        private readonly StatusPill _pillCt = new("平均节拍", 140);
        private readonly StatusPill _pillUser = new("当前用户", 170);

        private readonly Label _lblClock = new();
        private readonly Label _lblLog = new();
        private readonly Panel _alarmBar = new();
        private readonly Label _lblAlarm = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
        private Panel? _navHost;
        private Label _lblUserName = new();
        private Label _lblUserRole = new();
        private Button? _activeNav;
        private long _lastLogTick;

        public MainForm()
        {
            Text = "Smart Vision System  —  工业视觉引导与质量检测平台";
            ClientSize = new Size(1500, 940);
            MinimumSize = new Size(1280, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.F(9.5F);
            KeyPreview = true;

            // Dock=Fill 的容器必须最先加入，停靠面板靠后加入（WinForms 按 z 逆序执行停靠）
            BuildContent();
            BuildNav();
            BuildAlarmBar();
            BuildTopBar();
            BuildTicker();

            Log.EntryWritten += OnLogEntry;
            FormClosing += OnClosing;
            _timer.Tick += (_, _) => RefreshStatus();
            _timer.Start();

            ShowView(typeof(MonitorView));
            Log.Info("系统", $"{Auth.Current.DisplayName} 进入系统，权限：{Auth.Current.Role}");
        }

        private DeviceHub Hub => DeviceHub.I;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowStyle.UseDarkTitleBar(Handle);
            WindowStyle.ApplyDarkScrollbars(this);
        }

        // ─────────────────────────── 布局 ───────────────────────────

        private void BuildNav()
        {
            var nav = new Panel { Dock = DockStyle.Left, Width = 196, BackColor = Color.FromArgb(18, 20, 25) };

            var logo = new Label
            {
                Text = "SVS",
                Dock = DockStyle.Top,
                Height = 64,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(18, 20, 25)
            };
            var logoSub = new Label
            {
                Text = "视觉引导平台",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Theme.Muted,
                Font = Theme.F(8.5F),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };

            _navHost = new Panel { Dock = DockStyle.Top, Height = 320, BackColor = Color.Transparent };
            nav.Controls.Add(_navHost);
            BuildNavItems();

            // 底部用户卡
            var userCard = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = Color.FromArgb(22, 24, 30) };
            _lblUserName = new Label
            {
                Text = Auth.Current.DisplayName,
                ForeColor = Theme.Text,
                Font = Theme.F(10F, FontStyle.Bold),
                Location = new Point(14, 12),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            _lblUserRole = new Label
            {
                Text = Auth.Current.Role switch
                {
                    UserRole.Admin => "管理员 · 全权限",
                    UserRole.Engineer => "设备工程师",
                    _ => "操作员"
                },
                ForeColor = Theme.Muted,
                Font = Theme.F(8.5F),
                Location = new Point(14, 34),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            var btnLogout = Theme.Btn("切换账号", Color.FromArgb(42, 46, 54), Theme.SubText, 166, 30, 9F);
            btnLogout.Location = new Point(14, 56);
            btnLogout.Click += (_, _) => Logout();

            userCard.Controls.AddRange(new Control[] { _lblUserName, _lblUserRole, btnLogout });

            nav.Controls.Add(logoSub);
            nav.Controls.Add(logo);
            nav.Controls.Add(userCard);
            Controls.Add(nav);
        }

        /// <summary>按当前登录角色重建左侧导航（切换账号后角色会变，必须重建）</summary>
        private void BuildNavItems()
        {
            if (_navHost == null) return;

            _navHost.Controls.Clear();
            _nav.Clear();

            var items = new (string Text, Type View, Permission? Need)[]
            {
                ("生产监控", typeof(MonitorView), null),
                ("运动控制", typeof(MotionView), Permission.JogMotion),
                ("参数与图纸", typeof(SettingsView), Permission.EditRecipe),
                ("历史数据", typeof(HistoryView), Permission.ViewHistory),
                ("产线仿真", typeof(SimulatorView), Permission.TuneSimulator),
                ("用户管理", typeof(UserManageView), Permission.ManageUsers),
            };

            int y = 8;
            foreach (var (text, view, need) in items)
            {
                if (need.HasValue && !Auth.Current.Can(need.Value)) continue;

                var btn = new Button
                {
                    Text = "   " + text,
                    Location = new Point(10, y),
                    Size = new Size(176, 42),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Theme.SubText,
                    Font = Theme.F(10.5F),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(32, 36, 44);
                var captured = view;
                btn.Click += (_, _) => ShowView(captured);
                _navHost.Controls.Add(btn);
                _nav.Add((btn, view, need));
                y += 48;
            }

            _navHost.Height = y + 8;

            // 无权限的页面若已被创建过，移除并释放，避免残留
            foreach (var viewType in _views.Keys.ToList())
            {
                bool allowed = items.Any(i => i.View == viewType && (!i.Need.HasValue || Auth.Current.Can(i.Need.Value)));
                if (allowed) continue;
                _content.Controls.Remove(_views[viewType]);
                _views[viewType].Dispose();
                _views.Remove(viewType);
            }
        }

        /// <summary>按当前登录会话重建导航（供切换账号与自动化截图复用）</summary>
        public void ReloadNavForCurrentUser()
        {
            BuildNavItems();
            ShowView(typeof(MonitorView));
        }

        /// <summary>当前导航文本（自检用）</summary>
        public IReadOnlyList<string> NavTexts => _nav.Select(n => n.Btn.Text.Trim()).ToList();

        private void BuildTopBar()
        {
            // 顶栏高度由胶囊实际高度决定，避免写死 56 把标题行压掉
            var bar = new Panel { Dock = DockStyle.Top, Height = _pillState.Height + 20, BackColor = Theme.Panel, Padding = new Padding(12, 10, 12, 10) };
            bar.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                WrapContents = false,
                BackColor = Color.Transparent
            };
            foreach (var p in new[] { _pillState, _pillLine, _pillRecipe, _pillYield, _pillTotal, _pillCt, _pillUser })
            {
                p.Margin = new Padding(0, 0, 8, 0);
                flow.Controls.Add(p);
            }

            _lblClock.AutoSize = true;
            _lblClock.ForeColor = Theme.SubText;
            _lblClock.Font = Theme.Mono(10F);
            _lblClock.Dock = DockStyle.Right;
            _lblClock.TextAlign = ContentAlignment.MiddleRight;
            _lblClock.Padding = new Padding(0, 10, 4, 0);

            bar.Controls.Add(flow);
            bar.Controls.Add(_lblClock);
            Controls.Add(bar);
        }

        private void BuildAlarmBar()
        {
            _alarmBar.Dock = DockStyle.Top;
            _alarmBar.Height = 0;
            _alarmBar.BackColor = Color.FromArgb(70, 24, 24);
            _alarmBar.Visible = false;

            _lblAlarm.Dock = DockStyle.Fill;
            _lblAlarm.ForeColor = Color.FromArgb(255, 200, 200);
            _lblAlarm.Font = Theme.F(10F, FontStyle.Bold);
            _lblAlarm.TextAlign = ContentAlignment.MiddleLeft;
            _lblAlarm.Padding = new Padding(14, 0, 0, 0);

            var btnReset = Theme.Btn("复位并重连", Theme.Ng, Color.White, 130, 28, 9F);
            btnReset.Dock = DockStyle.Right;
            btnReset.Click += (_, _) =>
            {
                if (Hub.Station.ResetAlarm(out var err)) HideAlarm();
                else MessageBox.Show(this, "复位失败：" + err, "报警复位", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            _alarmBar.Controls.Add(_lblAlarm);
            _alarmBar.Controls.Add(btnReset);
            Controls.Add(_alarmBar);
        }

        private void BuildTicker()
        {
            var ticker = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Color.FromArgb(20, 22, 27) };
            _lblLog.Dock = DockStyle.Fill;
            _lblLog.ForeColor = Theme.Muted;
            _lblLog.Font = Theme.Mono(8.5F);
            _lblLog.TextAlign = ContentAlignment.MiddleLeft;
            _lblLog.Padding = new Padding(12, 0, 0, 0);
            _lblLog.Text = "系统日志：—";
            ticker.Controls.Add(_lblLog);
            Controls.Add(ticker);
        }

        private void BuildContent()
        {
            _content.Dock = DockStyle.Fill;
            _content.BackColor = Theme.Bg;
            _content.Padding = new Padding(10);
            Controls.Add(_content);
        }

        // ─────────────────────────── 导航 ───────────────────────────

        public void ShowView(Type viewType)
        {
            if (!_views.TryGetValue(viewType, out var view))
            {
                try
                {
                    view = (UserControl)Activator.CreateInstance(viewType)!;
                }
                catch (Exception ex)
                {
                    Log.Error("界面", $"创建页面失败：{viewType.Name}", ex);
                    MessageBox.Show(this, $"页面加载失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                view.Dock = DockStyle.Fill;
                _content.Controls.Add(view);
                _views[viewType] = view;
            }

            foreach (Control c in _content.Controls) c.Visible = false;
            view.Visible = true;
            view.BringToFront();

            // 系统绘制的滚动条/下拉列表/滑块默认是浅色，切页时统一套深色主题；
            // 滚动条往往在布局完成后才创建，所以再延迟补一次
            try { WindowStyle.ApplyDarkScrollbars(view); } catch { }
            var darkTimer = new System.Windows.Forms.Timer { Interval = 150 };
            darkTimer.Tick += (_, _) =>
            {
                darkTimer.Stop();
                darkTimer.Dispose();
                try { WindowStyle.ApplyDarkScrollbars(view); } catch { }
            };
            darkTimer.Start();

            foreach (var (btn, type, _) in _nav)
            {
                bool active = type == viewType;
                btn.BackColor = active ? Color.FromArgb(38, 44, 56) : Color.Transparent;
                btn.ForeColor = active ? Theme.Text : Theme.SubText;
                if (active)
                {
                    btn.Font = Theme.F(10.5F, FontStyle.Bold);
                    _activeNav = btn;
                }
                else btn.Font = Theme.F(10.5F);
            }
        }

        // ─────────────────────────── 状态刷新 ───────────────────────────

        private void RefreshStatus()
        {
            _lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (!Hub.IsInitialized)
            {
                _pillState.Set("初始化中…", Theme.Warn);
                return;
            }

            var station = Hub.Station;
            var (stateText, stateColor) = StateVisual.Of(station.State);
            _pillState.Set($"{stateText} · {StateVisual.PhaseText(station.Phase)}", stateColor);

            var lineState = Hub.Line.State switch
            {
                Simulation.LineState.Running => ("输送中", Theme.Ok),
                Simulation.LineState.Holding => ("停位待检", Theme.Warn),
                _ => ("静止", Theme.Muted)
            };
            _pillLine.Set(lineState.Item1, lineState.Item2);

            var recipe = Hub.CurrentRecipe;
            _pillRecipe.Set(recipe.HasTemplate ? recipe.Name : "无模板", recipe.HasTemplate ? Theme.Text : Theme.Ng);

            var stats = station.Stats.Clone();
            _pillYield.Set($"{stats.YieldPct:F2} %", stats.Total == 0 ? Theme.Muted : stats.YieldPct >= 98 ? Theme.Ok : stats.YieldPct >= 95 ? Theme.Warn : Theme.Ng);
            _pillTotal.Set($"{stats.Total} / {stats.Ng}", Theme.Text);
            _pillCt.Set($"{stats.AvgCycleMs:F0} ms", Theme.Cyan);
            _pillUser.Set($"{Auth.Current.DisplayName} · {Auth.Current.Role}", Theme.Purple);
            _lblUserName.Text = Auth.Current.DisplayName;
            _lblUserRole.Text = Auth.Current.Role switch
            {
                UserRole.Admin => "管理员 · 全权限",
                UserRole.Engineer => "设备工程师",
                _ => "操作员"
            };

            if (station.State == StationState.Alarm && !string.IsNullOrEmpty(station.LastAlarm))
                ShowAlarm(station.LastAlarm);
            else if (station.State != StationState.Alarm)
                HideAlarm();
        }

        private void ShowAlarm(string message)
        {
            if (_alarmBar.Visible && _lblAlarm.Text == "⚠  " + message) return;
            _lblAlarm.Text = "⚠  " + message;
            _alarmBar.Height = 34;
            _alarmBar.Visible = true;
        }

        private void HideAlarm()
        {
            if (!_alarmBar.Visible) return;
            _alarmBar.Visible = false;
            _alarmBar.Height = 0;
        }

        private void OnLogEntry(LogEntry entry)
        {
            // 日志可能来自任意线程，节流后 marshal 回 UI
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastLogTick) < 60) return;
            Interlocked.Exchange(ref _lastLogTick, now);

            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    _lblLog.ForeColor = entry.Level switch
                    {
                        LogLevel.Error or LogLevel.Alarm => Theme.Ng,
                        LogLevel.Warn => Theme.Warn,
                        _ => Theme.Muted
                    };
                    _lblLog.Text = $"系统日志：{entry.Time:HH:mm:ss}  {entry.Message}";
                });
            }
            catch { }
        }

        // ─────────────────────────── 生命周期 ───────────────────────────

        private void Logout()
        {
            if (Hub.IsInitialized && Hub.Station.IsRunning &&
                MessageBox.Show(this, "生产正在运行，退出将停止产线。确定继续？", "切换账号",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            try { Hub.Station.Stop("用户切换账号"); } catch { }
            Hide();
            using var login = new LoginForm();
            if (login.ShowDialog() == DialogResult.OK)
            {
                // 角色可能变了：重建导航 + 回到监控页
                BuildNavItems();
                ShowView(typeof(MonitorView));
                Show();
                Log.Info("系统", $"{Auth.Current.DisplayName} 重新登录，权限：{Auth.Current.Role}");
            }
            else Close();
        }

        private void OnClosing(object? sender, FormClosingEventArgs e)
        {
            if (Hub.IsInitialized && Hub.Station.IsRunning)
            {
                var r = MessageBox.Show(this, "生产运行中，退出将立即停止产线。确定退出？", "退出确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }

            _timer.Stop();
            Log.EntryWritten -= OnLogEntry;
            try
            {
                if (Hub.IsInitialized)
                {
                    Hub.Station.Stop("程序退出");
                    Hub.Dispose();
                }
            }
            catch (Exception ex) { Log.Error("系统", "退出清理异常", ex); }

            Log.Info("系统", "程序退出");
            Log.Shutdown();
        }
    }
}

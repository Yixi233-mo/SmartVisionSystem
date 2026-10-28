using SmartVisionSystem.Core;
using SmartVisionSystem.Hardware;
using SmartVisionSystem.Infrastructure;
using Size = System.Drawing.Size;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 生产监控（操作员主界面）：正中大画面 + GDI 叠加 + 右侧看板 + 底部大按钮。
    /// 设计原则来自需求：戴手套的操作员只需要看清一件事——OK 还是 NG，然后按启动/停止。
    /// </summary>
    public sealed class MonitorView : UserControl
    {
        private readonly VideoPanel _video = new();
        private readonly KpiCard _kTotal = new("累计产量", Theme.Text, 20F);
        private readonly KpiCard _kYield = new("良率", Theme.Ok, 20F);
        private readonly KpiCard _kOk = new("OK 数", Theme.Ok, 20F);
        private readonly KpiCard _kNg = new("NG 数", Theme.Ng, 20F);
        private readonly KpiCard _kCt = new("平均节拍", Theme.Cyan, 20F);
        private readonly KpiCard _kUph = new("产能 件/时", Theme.Purple, 20F);

        private readonly Label _lblSn = new();
        private readonly Label _lblScore = new();
        private readonly Label _lblAngle = new();
        private readonly Label _lblOffset = new();
        private readonly Label _lblArea = new();
        private readonly Label _lblSurface = new();
        private readonly Label _lblCt = new();
        private readonly Label _lblVerdict = new();
        private readonly ListBox _alarms = new();

        // 控制栏按钮：字号、留白、图标风格全部统一。
        // 此前是 13/10/9 三档字号 + 150/130 两种宽度，125% 缩放下列宽不够，
        // 把"报警复位/模拟拔网线"截成了"报警复/模拟拔网"。
        private readonly Button _btnStart = Theme.Btn("▶  启动", Theme.Ok, Color.White, 0, 0, 10.5F);
        private readonly Button _btnStop = Theme.Btn("■  停止", Theme.Ng, Color.White, 0, 0, 10.5F);
        private readonly Button _btnReset = Theme.Btn("↺  报警复位", Theme.Card, Theme.Text, 0, 0, 10.5F);
        private readonly Button _btnInspect = Theme.Btn("◉  单次检测", Theme.Accent, Color.White, 0, 0, 10.5F);
        private readonly Button _btnDropNet = Theme.Btn("⚡  模拟拔网线", Color.FromArgb(70, 74, 84), Theme.SubText, 0, 0, 10.5F);

        private readonly Button[] _actionButtons;
        private Panel? _controlBar;
        private Panel? _stateBlock;
        private FlowLayoutPanel? _btnFlow;

        private readonly Label _lblStateBig = new();
        private readonly Label _lblPhase = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };

        public MonitorView()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;

            _actionButtons = new[] { _btnStart, _btnStop, _btnReset, _btnInspect, _btnDropNet };

            // 注意：Dock=Fill 的控件必须最先加入 Controls，否则会被后加入的停靠面板挤压/遮挡
            BuildVideo();
            BuildRightPanel();
            BuildControlBar();

            _timer.Tick += (_, _) => RefreshStats();
            _timer.Start();
            Bind();
            RefreshStats();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 句柄创建后当前 DPI 才确定，此时量文字才准（125% 缩放下必须走这一遍）
            Theme.Equalize(_actionButtons, minWidth: 132, padX: 34);
            if (_controlBar != null)
            {
                _controlBar.Height = _actionButtons[0].Height + _controlBar.Padding.Vertical;
                _lblPhase.Height = Theme.LineHeight(_lblPhase.Font);
                FitControlBar();
            }
        }

        private static DeviceHub Hub => DeviceHub.I;

        // ─────────────────────────── 构建 ───────────────────────────

        private void BuildVideo()
        {
            _video.Dock = DockStyle.Fill;
            _video.Placeholder = "相机未就绪 —— 正在连接虚拟相机…";
            Controls.Add(_video);
        }

        private void BuildRightPanel()
        {
            // 右栏整体可滚动：窗口高度不足时可以滑，而不是把下面的内容切掉
            var right = new Panel { Dock = DockStyle.Right, Width = 344, BackColor = Theme.Bg, Padding = new Padding(8, 0, 10, 0), AutoScroll = true };

            const int innerW = 300;   // 预留竖直滚动条宽度，避免出现横向滚动条
            int cardH = KpiCard.PreferredHeight(20F);
            int gap = 6;
            int colW = (innerW - 8) / 2;

            var inner = new Panel { Location = new Point(0, 0), Width = innerW, BackColor = Theme.Bg };

            // ── KPI 六宫格（绝对坐标，行高由字体推导）──
            var cards = new Control[] { _kTotal, _kYield, _kOk, _kNg, _kCt, _kUph };
            for (int i = 0; i < cards.Length; i++)
            {
                var c = cards[i];
                c.Dock = DockStyle.None;
                c.Size = new Size(colW, cardH);
                c.Location = new Point((i % 2) * (colW + 8), (i / 2) * (cardH + gap));
                inner.Controls.Add(c);
            }
            int y = 3 * (cardH + gap) + 2;

            // ── 本拍检测详情 ──
            int keyH = Theme.LineHeight(Theme.F(9F)) + 4;
            int valH = Theme.LineHeight(Theme.Mono(9.5F)) + 4;
            int bigH = Theme.LineHeight(Theme.F(13F, FontStyle.Bold)) + 4;
            int rowH = Math.Max(keyH, valH);
            int titleH = Theme.LineHeight(Theme.F(9F, FontStyle.Bold)) + 8;
            int detailH = titleH + 7 * rowH + Math.Max(bigH, rowH) + 12;

            var detail = new Panel { Location = new Point(0, y), Size = new Size(innerW, detailH), BackColor = Theme.Card };
            detail.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, detail.Width - 1, detail.Height - 1);
            };

            var title = new Label
            {
                Text = "本拍检测详情",
                Location = new Point(14, 8),
                Height = titleH,
                Width = 200,
                AutoSize = false,
                ForeColor = Theme.SubText,
                Font = Theme.F(9F, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            detail.Controls.Add(title);

            int rowY = titleH + 2;
            AddRow(detail, "流水号", _lblSn, ref rowY, Theme.Text);
            AddRow(detail, "判定", _lblVerdict, ref rowY, Theme.Ok, true);
            AddRow(detail, "匹配得分", _lblScore, ref rowY, Theme.Cyan);
            AddRow(detail, "旋转角度", _lblAngle, ref rowY, Theme.Text);
            AddRow(detail, "偏差 X / Y", _lblOffset, ref rowY, Theme.Warn);
            AddRow(detail, "材料面积", _lblArea, ref rowY, Theme.Text);
            AddRow(detail, "表面缺陷率", _lblSurface, ref rowY, Theme.Text);
            AddRow(detail, "视觉耗时", _lblCt, ref rowY, Theme.Purple);

            inner.Controls.Add(detail);
            y += detailH + 8;

            // ── 报警与事件 ──
            int alarmH = 190;
            var alarms = new Panel { Location = new Point(0, y), Size = new Size(innerW, alarmH), BackColor = Theme.Card, Padding = new Padding(10, 8, 10, 10) };
            var aTitle = new Label
            {
                Text = "报警与事件",
                Dock = DockStyle.Top,
                Height = titleH,
                ForeColor = Theme.SubText,
                Font = Theme.F(9F, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            _alarms.Dock = DockStyle.Fill;
            _alarms.BorderStyle = BorderStyle.None;
            _alarms.BackColor = Color.FromArgb(34, 38, 45);
            _alarms.ForeColor = Theme.SubText;
            _alarms.Font = Theme.Mono(8.5F);
            _alarms.IntegralHeight = false;
            alarms.Controls.Add(_alarms);
            alarms.Controls.Add(aTitle);
            inner.Controls.Add(alarms);
            y += alarmH;

            inner.Height = y + 8;
            right.Controls.Add(inner);
            Controls.Add(right);
        }

        private static void AddRow(Control parent, string key, Label value, ref int y, Color color, bool big = false)
        {
            var keyFont = Theme.F(9F);
            var valFont = big ? Theme.F(13F, FontStyle.Bold) : Theme.Mono(9.5F);

            int keyH = Theme.LineHeight(keyFont) + 4;
            int valH = Theme.LineHeight(valFont) + 4;
            int rowH = Math.Max(keyH, valH);

            var k = new Label
            {
                Text = key,
                Location = new Point(14, y + (rowH - keyH) / 2),
                Width = 108,
                Height = keyH,
                ForeColor = Theme.Muted,
                Font = keyFont,
                BackColor = Color.Transparent
            };
            value.Text = "--";
            value.Location = new Point(120, y + (rowH - valH) / 2);
            value.Width = 190;
            value.Height = valH;
            value.ForeColor = color;
            value.Font = valFont;
            value.BackColor = Color.Transparent;
            value.TextAlign = ContentAlignment.MiddleLeft;

            parent.Controls.Add(k);
            parent.Controls.Add(value);
            y += rowH;
        }

        private void BuildControlBar()
        {
            // 初始高度按构造期字体估算；句柄创建后 OnHandleCreated 会用当前 DPI 重新统一一次
            int btnH = Theme.LineHeight(Theme.F(10.5F, FontStyle.Bold)) + 20;
            _controlBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = btnH + 32,
                BackColor = Theme.Panel,
                Padding = new Padding(14, 16, 14, 16)
            };
            var bar = _controlBar;
            bar.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
            };

            _btnFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            _btnStart.Margin = new Padding(0, 0, 8, 0);
            _btnStop.Margin = new Padding(0, 0, 8, 0);
            _btnReset.Margin = new Padding(0, 0, 8, 0);
            _btnInspect.Margin = new Padding(0, 0, 8, 0);
            _btnDropNet.Margin = new Padding(0, 0, 0, 0);
            _btnFlow.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnReset, _btnInspect, _btnDropNet });

            _btnStart.Click += (_, _) => DoStart();
            _btnStop.Click += (_, _) => DoStop();
            _btnReset.Click += (_, _) => DoReset();
            _btnInspect.Click += (_, _) => DoInspectOnce();
            _btnDropNet.Click += (_, _) => DoDropNetwork();

            // 状态块压成一个窄列（此前两个 200px 的右停靠标签把 5 个按钮挤出了可视区）
            _lblStateBig.Text = "空闲";
            _lblStateBig.ForeColor = Theme.Muted;
            _lblStateBig.Font = Theme.F(13F, FontStyle.Bold);
            _lblStateBig.AutoSize = false;
            _lblStateBig.Dock = DockStyle.Fill;   // 剩余空间全部给大状态字，绝不与下面一行重叠
            _lblStateBig.TextAlign = ContentAlignment.MiddleRight;

            _lblPhase.Text = "—";
            _lblPhase.ForeColor = Theme.Muted;
            _lblPhase.Font = Theme.F(8F);
            _lblPhase.AutoSize = false;
            _lblPhase.Dock = DockStyle.Bottom;
            _lblPhase.Height = Theme.LineHeight(_lblPhase.Font);
            _lblPhase.TextAlign = ContentAlignment.MiddleRight;

            _stateBlock = new Panel { Dock = DockStyle.Right, Width = 124, BackColor = Color.Transparent };
            _stateBlock.Controls.Add(_lblStateBig);
            _stateBlock.Controls.Add(_lblPhase);   // 后加入 → 先停靠，Fill 才能正确让位

            bar.Controls.Add(_btnFlow);
            bar.Controls.Add(_stateBlock);
            bar.SizeChanged += (_, _) => FitControlBar();
            Controls.Add(bar);
        }

        /// <summary>
        /// 窄窗口下若按钮 + 状态块放不下，隐藏状态块而不是让最后一个按钮被裁掉。
        /// （设备状态在顶栏胶囊里也有显示，这里只是补充）
        /// </summary>
        private void FitControlBar()
        {
            if (_controlBar == null || _btnFlow == null || _stateBlock == null) return;

            int need = _btnFlow.PreferredSize.Width + _stateBlock.Width + _controlBar.Padding.Horizontal + 6;
            _stateBlock.Visible = _controlBar.Width >= need;
        }

        // ─────────────────────────── 交互 ───────────────────────────

        private void DoStart()
        {
            if (!Auth.Current.Can(Permission.OperateStation)) return;

            var recipe = Hub.CurrentRecipe;
            if (!recipe.Validate(out var err))
            {
                MessageBox.Show(FindForm(), "无法启动：\n" + err, "启动前置检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Hub.Station.Start(out var error))
            {
                MessageBox.Show(FindForm(), "启动失败：\n" + error, "启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            AddAlarm("生产已启动");
        }

        private void DoStop()
        {
            if (Hub.Station.State is StationState.Stopped or StationState.Idle)
            {
                AddAlarm("当前已是停止状态");
                return;
            }
            Hub.Station.Stop("操作员点击停止");
            AddAlarm("生产已停止");
        }

        private void DoReset()
        {
            if (Hub.Station.ResetAlarm(out var err)) AddAlarm("报警已复位，设备重新就绪");
            else MessageBox.Show(FindForm(), "复位失败：" + err, "复位", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void DoInspectOnce()
        {
            if (!Auth.Current.Can(Permission.OperateStation)) return;
            var result = Hub.Station.InspectOnce(out var error);
            if (result == null)
            {
                MessageBox.Show(FindForm(), "单次检测失败：\n" + error, "单次检测", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            UpdateDetail(result, "SINGLE");
            AddAlarm($"单次检测 → {(result.IsOk ? "OK" : "NG：" + result.NgReason)}（得分 {result.Score:F3}，CT {result.CtMs:F1}ms）");
        }

        private void DoDropNetwork()
        {
            if (!Auth.Current.Can(Permission.SimulateFault))
            {
                MessageBox.Show(FindForm(), "当前角色无权限模拟设备故障。", "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Hub.SimulateNetworkDrop(3000);
            AddAlarm("已模拟拔网线，3 秒后自动恢复（观察系统是否进入报警并自动重连）");
        }

        // ─────────────────────────── 事件绑定 ───────────────────────────

        private void Bind()
        {
            var station = Hub.Station;
            station.FrameReady += OnFrame;
            station.CycleCompleted += OnCycleCompleted;
            station.StateChanged += OnStateChanged;
            station.AlarmRaised += OnAlarmRaised;
            RefreshButtons();
        }

        private void OnFrame(FrameReadyEventArgs e)
        {
            if (IsDisposed) { e.Image.Dispose(); return; }
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) { e.Image.Dispose(); return; }
                    _video.SetFrame(e.Image, e.Result, e.Recipe);
                    if (e.Result != null) UpdateDetail(e.Result, e.SN);
                });
            }
            catch { e.Image.Dispose(); }
        }

        private void OnCycleCompleted(CycleCompletedEventArgs e) => RefreshStats();

        private void OnStateChanged(StationState s)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(RefreshButtons); } catch { }
        }

        private void OnAlarmRaised(string message, bool fatal)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => AddAlarm((fatal ? "【报警】" : "【提示】") + message)); } catch { }
        }

        private void RefreshButtons()
        {
            var s = Hub.Station.State;
            bool running = s is StationState.Running or StationState.Homing;
            _btnStart.Enabled = !running;
            _btnStop.Enabled = s is not (StationState.Idle or StationState.Stopped);
            _btnReset.Enabled = s is StationState.Alarm or StationState.Stopped;
            _btnInspect.Enabled = !running;

            var (text, color) = StateVisual.Of(s);
            _lblStateBig.Text = text;
            _lblStateBig.ForeColor = color;
            _lblPhase.Text = "阶段 " + StateVisual.PhaseText(Hub.Station.Phase);
        }

        private void RefreshStats()
        {
            if (!Hub.IsInitialized) return;
            var st = Hub.Station.Stats.Clone();
            _kTotal.SetValue(st.Total.ToString());
            _kYield.SetValue($"{st.YieldPct:F1}%");
            _kOk.SetValue(st.Ok.ToString());
            _kNg.SetValue(st.Ng.ToString());
            _kCt.SetValue($"{st.AvgCycleMs:F0} ms");
            _kUph.SetValue($"{st.Uph:F0}");

            _kYield.SetValue($"{st.YieldPct:F1}%");
            _lblPhase.Text = "阶段 " + StateVisual.PhaseText(Hub.Station.Phase);
            _btnInspect.Enabled = Hub.Station.State is not (StationState.Running or StationState.Homing);
        }

        private void UpdateDetail(VisionResult r, string sn)
        {
            _lblSn.Text = sn;
            _lblVerdict.Text = r.IsOk ? "OK" : "NG";
            _lblVerdict.ForeColor = r.IsOk ? Theme.Ok : Theme.Ng;
            _lblScore.Text = $"{r.Score:F4}";
            _lblAngle.Text = $"{r.AngleDeg:+0.0;-0.0} °";
            _lblOffset.Text = $"{r.OffsetXmm:+0.000;-0.000} / {r.OffsetYmm:+0.000;-0.000} mm";
            _lblArea.Text = $"{r.MaterialAreaPx:F0} px²";
            _lblSurface.Text = $"{r.SurfaceRatio * 100:F2} %";
            _lblCt.Text = $"{r.CtMs:F1} ms";
            if (!r.IsOk) AddAlarm($"{sn} NG：{r.NgReason}");
        }

        private void AddAlarm(string message)
        {
            _alarms.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (_alarms.Items.Count > 200) _alarms.Items.RemoveAt(_alarms.Items.Count - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                if (Hub.IsInitialized)
                {
                    Hub.Station.FrameReady -= OnFrame;
                    Hub.Station.CycleCompleted -= OnCycleCompleted;
                    Hub.Station.StateChanged -= OnStateChanged;
                    Hub.Station.AlarmRaised -= OnAlarmRaised;
                }
            }
            base.Dispose(disposing);
        }
    }
}

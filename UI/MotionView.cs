using System.Drawing.Drawing2D;
using SmartVisionSystem.Core;
using SmartVisionSystem.Hardware;
using SmartVisionSystem.Infrastructure;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 运动控制（设备工程师界面）：三轴实时位置、点动微调、回零、按图纸点位一键定位。
    /// 所有动作都走真实的运动仿真（梯形速度曲线），位置会连续刷新，而不是"瞬间跳到目标"——
    /// 这样才能验证互锁、超程、到位判定等逻辑是否真的生效。
    /// </summary>
    public sealed class MotionView : UserControl
    {
        /// <summary>
        /// 轴状态卡：完全自绘。
        /// 用 AutoSize 标签串联位置时，125% 缩放下会出现"创建时按 96DPI 量、渲染时已按 120DPI 画"，
        /// 大号数值会被卡片切掉一半。自绘后行高在绘制时实时测量，不会再错位。
        /// </summary>
        private sealed class AxisCard : Control
        {
            private static readonly Font NameFont = new("Microsoft YaHei UI", 10F, FontStyle.Bold);
            private static readonly Font PosFont = new("Consolas", 24F, FontStyle.Bold);
            private static readonly Font SubFont = new("Consolas", 9F);

            private readonly Color _accent;
            public string AxisName { get; }
            public string PosText { get; private set; } = "0.000";
            public string TargetText { get; private set; } = "目标 --";
            public string InfoText { get; private set; } = "mm";

            public AxisCard(string axis, Color accent)
            {
                AxisName = axis;
                _accent = accent;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Card;
                Margin = new Padding(0, 0, 8, 0);
                Dock = DockStyle.Fill;
                Height = PreferredHeight();
            }

            /// <summary>卡片所需高度（按当前 DPI 实时测量）</summary>
            public static int PreferredHeight()
            {
                int nameH = TextRenderer.MeasureText("X 轴", NameFont).Height;
                int posH = TextRenderer.MeasureText("250.000", PosFont).Height;
                int subH = TextRenderer.MeasureText("目标 250.0 mm · 限位 0~800", SubFont).Height;
                return 8 + nameH + 2 + posH + 2 + subH + 1 + subH + 10;
            }

            public void Update(string pos, string target, string info)
            {
                PosText = pos;
                TargetText = target;
                InfoText = info;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);

                int nameH = TextRenderer.MeasureText("X 轴", NameFont).Height;
                int posH = TextRenderer.MeasureText("250.000", PosFont).Height;
                int subH = TextRenderer.MeasureText("目标", SubFont).Height;

                int y = 8;
                TextRenderer.DrawText(g, AxisName + " 轴", NameFont, new Rectangle(12, y, Width - 20, nameH), _accent);
                y += nameH + 2;
                TextRenderer.DrawText(g, PosText, PosFont, new Rectangle(10, y, Width - 16, posH), Theme.Text);
                y += posH + 2;
                TextRenderer.DrawText(g, TargetText, SubFont, new Rectangle(14, y, Width - 20, subH), Theme.SubText);
                y += subH + 1;
                TextRenderer.DrawText(g, InfoText, SubFont, new Rectangle(14, y, Width - 20, subH), Theme.Muted);

                using var bar = new SolidBrush(_accent);
                g.FillRectangle(bar, 0, 0, Width, 3);
                using var pen = new Pen(Theme.Border);
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        private readonly AxisCard _cx = new("X", Theme.Cyan);
        private readonly AxisCard _cy = new("Y", Theme.Ok);
        private readonly AxisCard _cz = new("Z", Theme.Purple);
        private readonly XyMap _map = new();
        private readonly Label _lblStatus = new();
        private readonly ComboBox _cboStep = Field.Combo(new[] { "0.1 mm", "1 mm", "10 mm", "50 mm", "100 mm" }, 110);
        private readonly DarkSlider _speed = new();
        private readonly Label _lblSpeed = new();

        public MotionView()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;

            // 加入顺序决定停靠优先级：Fill 必须最先加，其余按 由内到外 依次加入
            BuildMap();
            BuildControl();
            BuildStatusBar();
            BuildAxisRow();

            Hub.Motion.Updated += OnMotionUpdated;
            Hub.Station.StateChanged += OnStationStateChanged;
            Refresh();
        }

        private static DeviceHub Hub => DeviceHub.I;

        // ─────────────────────────── 布局 ───────────────────────────

        private void BuildAxisRow()
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = AxisCard.PreferredHeight() + 8,
                ColumnCount = 3,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 0, 8)
            };
            for (int i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            row.Controls.Add(_cx, 0, 0);
            row.Controls.Add(_cy, 1, 0);
            row.Controls.Add(_cz, 2, 0);
            Controls.Add(row);
        }

        private void BuildMap()
        {
            _map.Dock = DockStyle.Fill;
            Controls.Add(_map);
        }

        private void BuildControl()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = 398, BackColor = Theme.Panel, Padding = new Padding(12, 12, 12, 12) };
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };

            int lineH = Theme.LineHeight(Theme.F(9F));
            int btnH = Math.Max(32, lineH + 12);
            const int cardW = 334;

            // ── 手动点动（点动速度并入本卡，避免右栏内容超长出现滚动条把卡片切断）──
            var grpJog = Theme.Card2("手动点动 (Jog)", 0);
            grpJog.Dock = DockStyle.None;
            grpJog.Location = new Point(0, 0);
            grpJog.Width = cardW;

            int cy = 38;

            var lblStep = Field.Label("步距");
            lblStep.Location = new Point(14, cy);
            lblStep.Width = 52;
            _cboStep.Location = new Point(70, cy - 2);
            _cboStep.Width = 110;
            grpJog.Controls.Add(lblStep);
            grpJog.Controls.Add(_cboStep);
            cy += Math.Max(lblStep.Height, _cboStep.Height) + 10;

            foreach (var axis in new[] { "X", "Y", "Z" })
            {
                var lbl = new Label
                {
                    Text = axis,
                    Location = new Point(16, cy + (btnH - lineH) / 2),
                    Width = 22,
                    AutoSize = false,
                    Height = lineH,
                    ForeColor = Theme.SubText,
                    Font = Theme.F(10F, FontStyle.Bold),
                    BackColor = Color.Transparent
                };
                var minus = Theme.Btn("−", Color.FromArgb(52, 57, 66), Theme.Text, 74, btnH, 12F);
                minus.Location = new Point(44, cy);
                var plus = Theme.Btn("+", Color.FromArgb(52, 57, 66), Theme.Text, 74, btnH, 12F);
                plus.Location = new Point(126, cy);

                var a = axis;
                minus.Click += async (_, _) => await JogAsync(a, -1);
                plus.Click += async (_, _) => await JogAsync(a, +1);

                grpJog.Controls.Add(lbl);
                grpJog.Controls.Add(minus);
                grpJog.Controls.Add(plus);
                cy += btnH + 6;
            }

            cy += 4;
            var lblSpd = Field.Label("点动速度");
            lblSpd.Location = new Point(14, cy);
            lblSpd.Width = 70;
            _speed.Minimum = 1;
            _speed.Maximum = 100;
            _speed.Value = 15;
            _speed.Location = new Point(88, cy);
            _speed.Width = 152;
            _speed.BackColor = Theme.Card;
            _speed.ValueChanged += (_, _) => _lblSpeed.Text = $"{JogSpeed():F0} mm/s";
            _lblSpeed.Text = "30 mm/s";
            _lblSpeed.Location = new Point(244, cy + 4);
            _lblSpeed.Width = 80;
            _lblSpeed.AutoSize = false;
            _lblSpeed.Height = lineH;
            _lblSpeed.ForeColor = Theme.Cyan;
            _lblSpeed.Font = Theme.Mono(9F);
            _lblSpeed.BackColor = Color.Transparent;
            grpJog.Controls.Add(lblSpd);
            grpJog.Controls.Add(_speed);
            grpJog.Controls.Add(_lblSpeed);
            cy += Math.Max(_speed.Height, lineH) + 8;

            grpJog.Height = cy + 6;

            // ── 按图纸点位定位（两列，四行压成两行）──
            var grpMove = Theme.Card2("按图纸点位定位", 0);
            grpMove.Dock = DockStyle.None;
            grpMove.Location = new Point(0, grpJog.Bottom + 8);
            grpMove.Width = cardW;

            var presets = new (string Text, Color Color, Func<(double X, double Y, double Z)> Get)[]
            {
                ("取料基准点", Theme.Ok, () => (Hub.CurrentRecipe.RobotBaseX, Hub.CurrentRecipe.RobotBaseY, Hub.CurrentRecipe.ZSafe)),
                ("放料点 (良品)", Theme.Accent, () => (Hub.CurrentRecipe.PlaceX, Hub.CurrentRecipe.PlaceY, Hub.CurrentRecipe.ZSafe)),
                ("放料点 (NG)", Theme.Ng, () => (Hub.CurrentRecipe.PlaceNgX, Hub.CurrentRecipe.PlaceNgY, Hub.CurrentRecipe.ZSafe)),
                ("安全待机点", Theme.Purple, () => (Hub.CurrentRecipe.RobotBaseX, Hub.CurrentRecipe.RobotBaseY, Hub.CurrentRecipe.ZSafe)),
            };

            int colW = (cardW - 24) / 2;
            for (int i = 0; i < presets.Length; i++)
            {
                var (text, color, get) = presets[i];
                var b = Theme.Btn(text, Theme.Card, color, colW, btnH, 9F);
                b.Location = new Point(12 + (i % 2) * (colW + 4), 38 + (i / 2) * (btnH + 8));
                var g = get;
                b.Click += async (_, _) => await MoveToAsync(g(), text);
                grpMove.Controls.Add(b);
            }
            grpMove.Height = 38 + 2 * (btnH + 8) + 4;

            scroll.Controls.Add(grpMove);
            scroll.Controls.Add(grpJog);

            // 底部动作：固定在栏底、不进滚动区。
            // 否则滚动条一出现，"停止"按钮就会被滚动视口切掉半截。
            int actBtnH = Theme.LineHeight(Theme.F(10F, FontStyle.Bold)) + 22;
            var grpAct = new Panel { Dock = DockStyle.Bottom, Width = 334, BackColor = Color.Transparent };

            var btnHome = Theme.Btn("⌂  全轴回零", Theme.Warn, Color.Black, 0, 0, 10F);
            btnHome.Location = new Point(0, 0);
            btnHome.Click += async (_, _) => await HomeAsync();

            var btnStop = Theme.Btn("■  停止", Theme.Ng, Color.White, 0, 0, 10F);
            btnStop.Click += (_, _) => Hub.Motion.StopAll();

            Theme.Equalize(new[] { btnHome, btnStop }, minWidth: 160, padX: 34, padY: 22);
            btnStop.Left = btnHome.Right + 14;

            var lblLimit = new Label
            {
                Text = "行程：X[0,800]  Y[0,600]  Z[0,150] mm\nZ 低于安全高度时禁止 XY 平移（互锁）",
                Location = new Point(0, btnHome.Height + 8),
                Width = 334,
                Height = 2 * Theme.LineHeight(Theme.F(8.5F)) + 6,
                ForeColor = Theme.Muted,
                Font = Theme.F(8.5F),
                BackColor = Color.Transparent
            };
            grpAct.Height = btnHome.Height + lblLimit.Height + 18;
            grpAct.Controls.AddRange(new Control[] { btnHome, btnStop, lblLimit });

            panel.Controls.Add(scroll);
            panel.Controls.Add(grpAct);     // 后加入 → 先停靠，Fill 的滚动区自动让位
            Controls.Add(panel);
        }

        private void BuildStatusBar()
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = Theme.LineHeight(Theme.Mono(9F)) + 12, BackColor = Theme.Panel };
            _lblStatus.Dock = DockStyle.Fill;
            _lblStatus.ForeColor = Theme.SubText;
            _lblStatus.Font = Theme.Mono(9F);
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding = new Padding(12, 0, 0, 0);
            bar.Controls.Add(_lblStatus);
            Controls.Add(bar);
        }

        // ─────────────────────────── 动作 ───────────────────────────

        private double JogSpeed() => 5 + _speed.Value * 3.0;

        private double StepMm() => _cboStep.SelectedIndex switch
        {
            0 => 0.1,
            1 => 1,
            2 => 10,
            3 => 50,
            _ => 100
        };

        private async Task JogAsync(string axis, int dir)
        {
            if (!Auth.Current.Can(Permission.JogMotion)) return;
            if (GuardRunning()) return;

            var original = Hub.Settings.MaxVelMmS;
            try
            {
                Hub.Motion.SetVelocity(JogSpeed(), 3000);
                await Hub.Motion.JogAsync(axis, dir, StepMm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "点动失败：" + ex.Message, "运动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Hub.Motion.SetVelocity(original, Hub.Settings.AccelMmS2);
            }
        }

        private async Task MoveToAsync((double X, double Y, double Z) target, string label)
        {
            if (GuardRunning()) return;
            try
            {
                Hub.Motion.SetVelocity(Hub.Settings.MaxVelMmS, Hub.Settings.AccelMmS2);
                await Hub.Motion.MoveToAsync(target.X, target.Y, target.Z);
                Log.Info("运动", $"定位到【{label}】X{target.X:F1} Y{target.Y:F1} Z{target.Z:F1}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "定位失败：" + ex.Message, "运动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task HomeAsync()
        {
            if (GuardRunning()) return;
            if (MessageBox.Show(FindForm(), "回零将移动全部三轴，请确认机械手区域无人无障碍。是否执行？",
                    "安全确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                Hub.Motion.SetVelocity(Hub.Settings.MaxVelMmS, Hub.Settings.AccelMmS2);
                await Hub.Motion.HomeAllAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "回零失败：" + ex.Message, "运动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>防呆：生产运行中禁止手动干预轴</summary>
        private bool GuardRunning()
        {
            if (Hub.Station.State is not (StationState.Running or StationState.Homing)) return false;
            MessageBox.Show(FindForm(), "生产正在运行，禁止手动操作轴。\n请先停止生产再调试。", "互锁保护",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Log.Warn("运动", "运行中尝试手动干预轴，已拦截");
            return true;
        }

        // ─────────────────────────── 刷新 ───────────────────────────

        private void OnMotionUpdated(MotionSnapshot snap)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    _cx.Update(snap.X.ToString("F3"), $"目标 {Hub.Motion.PosX:F1} mm · 限位 0~800", snap.Moving ? "运动中…" : "到位");
                    _cy.Update(snap.Y.ToString("F3"), $"目标 {Hub.Motion.PosY:F1} mm · 限位 0~600", snap.Homed ? "已回零" : "未回零");
                    _cz.Update(snap.Z.ToString("F3"), $"目标 {Hub.Motion.PosZ:F1} mm · 限位 0~150", snap.Z < Hub.Motion.SafeZ ? "低于安全高度" : "安全高度以上");

                    _lblStatus.Text = $"状态：{(snap.Moving ? "运动中" : "静止")}   |   回零：{(snap.Homed ? "是" : "否")}   |   " +
                                      $"安全高度 {Hub.Motion.SafeZ:F0}mm   |   最大速度 {Hub.Settings.MaxVelMmS:F0}mm/s   加速度 {Hub.Settings.AccelMmS2:F0}mm/s²";

                    _map.SetState(snap, Hub.CurrentRecipe);
                });
            }
            catch { }
        }

        private void OnStationStateChanged(StationState state)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    bool running = state is StationState.Running or StationState.Homing;
                    _lblStatus.ForeColor = running ? Theme.Warn : Theme.SubText;
                    if (running) _lblStatus.Text = "⚠ 生产运行中：手动轴操作已被互锁禁止";
                });
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Hub.IsInitialized)
            {
                Hub.Motion.Updated -= OnMotionUpdated;
                Hub.Station.StateChanged -= OnStationStateChanged;
            }
            base.Dispose(disposing);
        }

        // ─────────────────────────── XY 俯视图 ───────────────────────────

        private sealed class XyMap : Control
        {
            private MotionSnapshot _snap;
            private Recipe? _recipe;
            private readonly List<PointF> _trail = new();

            public XyMap()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Panel;
            }

            public void SetState(MotionSnapshot snap, Recipe recipe)
            {
                _snap = snap;
                _recipe = recipe;
                _trail.Add(new PointF((float)snap.X, (float)snap.Y));
                if (_trail.Count > 400) _trail.RemoveRange(0, 100);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Panel);

                const double maxX = 800, maxY = 600;
                int pad = 34;
                var plot = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2);
                if (plot.Width < 50 || plot.Height < 50) return;

                PointF Map(double x, double y)
                {
                    float px = plot.Left + (float)(x / maxX * plot.Width);
                    float py = plot.Bottom - (float)(y / maxY * plot.Height);
                    return new PointF(px, py);
                }

                // 网格
                using var grid = new Pen(Color.FromArgb(46, 51, 60), 1);
                for (int x = 0; x <= maxX; x += 100)
                {
                    var p1 = Map(x, 0); var p2 = Map(x, maxY);
                    g.DrawLine(grid, p1, p2);
                    using var f = Theme.Mono(7.5F);
                    using var b = new SolidBrush(Theme.Muted);
                    g.DrawString(x.ToString(), f, b, p1.X - 8, plot.Bottom + 4);
                }
                for (int y = 0; y <= maxY; y += 100)
                {
                    var p1 = Map(0, y); var p2 = Map(maxX, y);
                    g.DrawLine(grid, p1, p2);
                    using var f = Theme.Mono(7.5F);
                    using var b = new SolidBrush(Theme.Muted);
                    g.DrawString(y.ToString(), f, b, 6, p1.Y - 7);
                }

                // 工作区边框
                using var border = new Pen(Theme.Border, 1);
                g.DrawRectangle(border, plot);

                if (_recipe != null)
                {
                    DrawPoint(g, Map, _recipe.RobotBaseX, _recipe.RobotBaseY, "取料基准", Theme.Ok);
                    DrawPoint(g, Map, _recipe.PlaceX, _recipe.PlaceY, "放料 OK", Theme.Accent);
                    DrawPoint(g, Map, _recipe.PlaceNgX, _recipe.PlaceNgY, "放料 NG", Theme.Ng);
                }

                // 轨迹
                if (_trail.Count > 1)
                {
                    using var trailPen = new Pen(Color.FromArgb(90, Theme.Cyan), 1.2F);
                    for (int i = 1; i < _trail.Count; i++)
                        g.DrawLine(trailPen, Map(_trail[i - 1].X, _trail[i - 1].Y), Map(_trail[i].X, _trail[i].Y));
                }

                // 当前位姿
                var cur = Map(_snap.X, _snap.Y);
                using var glow = new SolidBrush(Color.FromArgb(60, Theme.Cyan));
                g.FillEllipse(glow, cur.X - 12, cur.Y - 12, 24, 24);
                using var dot = new SolidBrush(Theme.Cyan);
                g.FillEllipse(dot, cur.X - 5, cur.Y - 5, 10, 10);
                using var cf = Theme.Mono(8.5F);
                using var cb = new SolidBrush(Theme.Text);
                g.DrawString($"({_snap.X:F0}, {_snap.Y:F0})  Z {_snap.Z:F0}", cf, cb, cur.X + 10, cur.Y - 18);

                // 标题
                using var tf = Theme.F(9F, FontStyle.Bold);
                using var tb = new SolidBrush(Theme.SubText);
                g.DrawString("机械手 XY 俯视图（mm）", tf, tb, 8, 8);
            }

            private static void DrawPoint(Graphics g, Func<double, double, PointF> map, double x, double y, string label, Color color)
            {
                var p = map(x, y);
                using var pen = new Pen(color, 1.4F);
                g.DrawLine(pen, p.X - 6, p.Y, p.X + 6, p.Y);
                g.DrawLine(pen, p.X, p.Y - 6, p.X, p.Y + 6);
                g.DrawEllipse(pen, p.X - 9, p.Y - 9, 18, 18);
                using var f = Theme.F(8F);
                using var b = new SolidBrush(color);
                g.DrawString(label, f, b, p.X + 11, p.Y - 16);
            }
        }
    }
}

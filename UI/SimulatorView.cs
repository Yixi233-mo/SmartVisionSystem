using System.Drawing.Drawing2D;
using SmartVisionSystem.Core;
using SmartVisionSystem.Hardware;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.Simulation;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 产线仿真（设备工程师界面）：这是"没有真实产线也能验收系统"的核心页面。
    /// 左侧调物理参数（线速/间距/偏差/缺陷率…），右侧实时看工件在皮带上的运动、相机工位触发与 PLC 握手信号。
    /// </summary>
    public sealed class SimulatorView : UserControl
    {
        private readonly LineCanvas _canvas = new();
        private readonly IoPanel _io = new();
        private readonly Label _lblHint = new();
        private readonly List<(string Name, Action Apply)> _appliers = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 120 };

        private readonly Button _btnSave = Theme.Btn("保存参数", Theme.Ok, Color.White, 0, 0, 10F);
        private readonly Button _btnReset = Theme.Btn("恢复默认", Theme.Card, Theme.Text, 0, 0, 10F);
        private readonly Button _btnDropNet = Theme.Btn("⚡  模拟拔网线 3 秒", Theme.Ng, Color.White, 0, 0, 10F);
        private readonly Button[] _toolButtons;
        private Panel? _toolbar;

        public SimulatorView()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;

            _toolButtons = new[] { _btnSave, _btnReset, _btnDropNet };

            var paramPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 470,
                BackColor = Theme.Panel,
                Padding = new Padding(10, 8, 8, 8),
                AutoScroll = true
            };
            BuildParamPanel(paramPanel);

            _io.Dock = DockStyle.Right;
            _io.Width = 236;
            _canvas.Dock = DockStyle.Fill;

            // 停靠按 z 逆序执行：最后加入的最先停靠，所以 Fill 必须先加、工具栏最后加
            Controls.Add(_canvas);
            Controls.Add(paramPanel);
            Controls.Add(_io);
            Controls.Add(BuildToolbar());

            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 句柄创建后 DPI 才确定，此时量文字才准
            Theme.Equalize(_toolButtons, minWidth: 150);
            if (_toolbar != null) _toolbar.Height = _toolButtons[0].Height + _toolbar.Padding.Vertical;
        }

        private static DeviceHub Hub => DeviceHub.I;

        // ─────────────────────────── 顶部动作栏 ───────────────────────────

        private Panel BuildToolbar()
        {
            int btnH = Theme.LineHeight(Theme.F(10F, FontStyle.Bold)) + 20;
            _toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = btnH + 20,
                BackColor = Theme.Bg,
                Padding = new Padding(10, 10, 10, 10)
            };

            var flow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };

            _btnSave.Margin = new Padding(0, 0, 10, 0);
            _btnReset.Margin = new Padding(0, 0, 10, 0);
            _btnDropNet.Margin = new Padding(0, 0, 0, 0);

            _btnSave.Click += (_, _) => { Hub.ApplySettings(); Log.Info("仿真", "仿真参数已保存"); };
            _btnReset.Click += (_, _) => ResetDefaults();
            _btnDropNet.Click += (_, _) =>
            {
                if (!Auth.Current.Can(Permission.SimulateFault)) return;
                Hub.SimulateNetworkDrop(3000);
            };

            flow.Controls.AddRange(new Control[] { _btnSave, _btnReset, _btnDropNet });

            _lblHint.Text = "参数实时生效，可边跑边调";
            _lblHint.AutoSize = false;
            _lblHint.Dock = DockStyle.Right;
            _lblHint.Width = 260;
            _lblHint.Height = btnH;
            _lblHint.ForeColor = Theme.Muted;
            _lblHint.Font = Theme.F(8.5F);
            _lblHint.TextAlign = ContentAlignment.MiddleRight;

            _toolbar.Controls.Add(flow);
            _toolbar.Controls.Add(_lblHint);
            return _toolbar;
        }

        // ─────────────────────────── 参数面板 ───────────────────────────

        /// <summary>
        /// 参数卡统一走"双列 + 高度由内容推导"：
        /// 之前把卡片按 58px 硬步长排、底部再横排一圈卡片，底部会被父容器压到，最后一行滑块被切掉。
        /// </summary>
        private void BuildParamPanel(Panel host)
        {
            const int cols = 2;
            int innerW = host.Width - host.Padding.Horizontal - 20;   // 预留竖直滚动条宽度
            var inner = new Panel { Location = new Point(0, 0), Width = innerW, BackColor = Theme.Panel };

            int y = 0;
            y = AddSliderCard(inner, innerW, cols, y, "产线物理参数", new SliderSpec[]
            {
                new("线速度", 50, 600, () => Hub.Settings.LineSpeedMmS, v => Hub.Settings.LineSpeedMmS = v, "mm/s"),
                new("来料间距", 150, 600, () => Hub.Settings.PartPitchMm, v => Hub.Settings.PartPitchMm = v, "mm"),
                new("偏差幅度 ±", 0, 80, () => Hub.Settings.OffsetAmpPx, v => Hub.Settings.OffsetAmpPx = v, "px"),
                new("旋转幅度 ±", 0, 20, () => Hub.Settings.AngleAmpDeg, v => Hub.Settings.AngleAmpDeg = v, "deg"),
                new("缺陷率", 0, 100, () => Hub.Settings.DefectRatePct, v => Hub.Settings.DefectRatePct = v, "%"),
                new("工件尺寸", 20, 90, () => Hub.Settings.PartSizeMm, v => Hub.Settings.PartSizeMm = v, "mm"),
            });

            y = AddSliderCard(inner, innerW, cols, y, "相机 / 运动仿真", new SliderSpec[]
            {
                new("取图延迟", 0, 40, () => Hub.Settings.GrabLatencyMs, v => Hub.Settings.GrabLatencyMs = v, "ms"),
                new("丢帧率", 0, 40, () => Hub.Settings.DropFrameRatePct, v => Hub.Settings.DropFrameRatePct = v, "%"),
                new("轴最大速度", 100, 900, () => Hub.Settings.MaxVelMmS, v => Hub.Settings.MaxVelMmS = v, "mm/s"),
                new("轴加速度", 500, 9000, () => Hub.Settings.AccelMmS2, v => Hub.Settings.AccelMmS2 = v, "mm/s2"),
            });

            y = AddCheckCard(inner, innerW, cols, y, "缺陷注入类型", new CheckSpec[]
            {
                new("漏打孔（面积↑）", () => Hub.Settings.EnableMissingHole, v => Hub.Settings.EnableMissingHole = v),
                new("崩边缺料（面积↓）", () => Hub.Settings.EnableChip, v => Hub.Settings.EnableChip = v),
                new("多料毛刺（面积↑）", () => Hub.Settings.EnableExtraMaterial, v => Hub.Settings.EnableExtraMaterial = v),
                new("表面划伤（表面↑）", () => Hub.Settings.EnableScratch, v => Hub.Settings.EnableScratch = v),
            });

            y = AddCheckCard(inner, innerW, 1, y, "调试开关", new CheckSpec[]
            {
                new("极端件（大偏移）", () => Hub.Settings.EnableOutlier, v => Hub.Settings.EnableOutlier = v),
                new("暂停缺陷注入", () => Hub.Settings.PauseDefectInjection, v => Hub.Settings.PauseDefectInjection = v),
            });

            inner.Height = y;
            host.Controls.Add(inner);
        }

        /// <summary>
        /// 自建参数卡：不用 Theme.Card2 —— Card2 的内置标题标签用绝对定位，
        /// 一旦外部调整卡片 Padding，标签会被顶到负坐标（实测 Y=-26）而不可见。
        /// </summary>
        private static Panel MakeCard(string title, int width, int height, int y)
        {
            var card = new Panel
            {
                BackColor = Theme.Card,
                Location = new Point(0, y),
                Size = new Size(width, height),
                Padding = new Padding(0)
            };
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                using var bar = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(bar, 0, 0, 3, Math.Min(28, card.Height));
            };

            var f = Theme.F(10F, FontStyle.Bold);
            var lbl = new Label
            {
                Text = title,
                Location = new Point(12, 8),
                Size = new Size(width - 24, Theme.LineHeight(f)),
                AutoSize = false,
                ForeColor = Theme.SubText,
                Font = f,
                BackColor = Color.Transparent
            };
            card.Controls.Add(lbl);
            return card;
        }

        private readonly record struct SliderSpec(string Label, int Min, int Max,
                                                 Func<double> Get, Action<double> Set, string Unit);

        private readonly record struct CheckSpec(string Label, Func<bool> Get, Action<bool> Set);

        private int AddSliderCard(Control parent, int width, int cols, int y, string title, SliderSpec[] items)
        {
            int lineH = Theme.LineHeight(Theme.F(8.5F));
            int sliderH = LineHeightOfSlider();
            int rowH = lineH + 2 + sliderH + 12;
            int rows = (items.Length + cols - 1) / cols;

            var card = MakeCard(title, width, 34 + rows * rowH + 6, y);

            int cellW = (width - 8) / cols;
            for (int i = 0; i < items.Length; i++)
            {
                int cx = (i % cols) * cellW;
                int cy = 34 + (i / cols) * rowH;
                PlaceSlider(card, items[i], cx, cy, cellW);
            }

            parent.Controls.Add(card);
            return y + card.Height + 8;
        }

        private int AddCheckCard(Control parent, int width, int cols, int y, string title, CheckSpec[] items)
        {
            var f = Theme.F(9F);
            int lineH = Theme.LineHeight(f);
            int rowH = lineH + 12;
            int rows = (items.Length + cols - 1) / cols;

            var card = MakeCard(title, width, 34 + rows * rowH + 8, y);

            int cellW = (width - 8) / cols;
            for (int i = 0; i < items.Length; i++)
            {
                var spec = items[i];
                var cb = new CheckBox
                {
                    Text = spec.Label,
                    Checked = spec.Get(),
                    Location = new Point((i % cols) * cellW + 12, 34 + (i / cols) * rowH),
                    Width = cellW - 20,
                    Height = lineH + 6,
                    AutoSize = false,
                    ForeColor = Theme.Text,
                    Font = f,
                    BackColor = Color.Transparent,
                    FlatStyle = FlatStyle.Flat
                };
                cb.CheckedChanged += (_, _) => spec.Set(cb.Checked);
                card.Controls.Add(cb);
                _appliers.Add((spec.Label, () => cb.Checked = spec.Get()));
            }

            parent.Controls.Add(card);
            return y + card.Height + 8;
        }

        private static int LineHeightOfSlider() => Theme.LineHeight(Theme.F(9F)) + 12;

        private void PlaceSlider(Control card, SliderSpec spec, int x, int y, int cellW)
        {
            int lineH = Theme.LineHeight(Theme.F(8.5F));

            var lbl = new Label
            {
                Text = spec.Label,
                Location = new Point(x + 12, y),
                Width = Math.Max(60, cellW - 110),
                Height = lineH,
                AutoSize = false,
                ForeColor = Theme.SubText,
                Font = Theme.F(8.5F),
                BackColor = Color.Transparent
            };
            var val = new Label
            {
                Text = $"{spec.Get():F0} {spec.Unit}",
                Location = new Point(x + cellW - 104, y),
                Width = 92,
                Height = lineH,
                AutoSize = false,
                ForeColor = Theme.Cyan,
                Font = Theme.Mono(8.5F),
                TextAlign = ContentAlignment.TopRight,
                BackColor = Color.Transparent
            };
            var bar = new DarkSlider
            {
                Minimum = spec.Min,
                Maximum = spec.Max,
                Value = (int)Math.Clamp(spec.Get(), spec.Min, spec.Max),
                Location = new Point(x + 8, y + lineH + 2),
                Width = cellW - 24,
                BackColor = Theme.Card
            };
            bar.ValueChanged += (_, _) =>
            {
                spec.Set(bar.Value);
                val.Text = $"{spec.Get():F0} {spec.Unit}";
            };

            card.Controls.Add(lbl);
            card.Controls.Add(val);
            card.Controls.Add(bar);
            _appliers.Add((spec.Label, () =>
            {
                bar.Value = (int)Math.Clamp(spec.Get(), spec.Min, spec.Max);
                val.Text = $"{spec.Get():F0} {spec.Unit}";
            }));
        }

        private void ResetDefaults()
        {
            var d = new SimulatorSettings();
            var s = Hub.Settings;
            s.LineSpeedMmS = d.LineSpeedMmS;
            s.PartPitchMm = d.PartPitchMm;
            s.OffsetAmpPx = d.OffsetAmpPx;
            s.AngleAmpDeg = d.AngleAmpDeg;
            s.DefectRatePct = d.DefectRatePct;
            s.PartSizeMm = d.PartSizeMm;
            s.GrabLatencyMs = d.GrabLatencyMs;
            s.DropFrameRatePct = d.DropFrameRatePct;
            s.MaxVelMmS = d.MaxVelMmS;
            s.AccelMmS2 = d.AccelMmS2;
            s.EnableChip = s.EnableMissingHole = s.EnableExtraMaterial = s.EnableScratch = true;
            s.EnableOutlier = false;
            s.PauseDefectInjection = false;

            foreach (var (_, apply) in _appliers) apply();
            Hub.ApplySettings();
            Log.Info("仿真", "仿真参数已恢复默认");
        }

        // ─────────────────────────── 刷新 ───────────────────────────

        private void Tick()
        {
            if (!Hub.IsInitialized) return;
            _canvas.Update(Hub.Line.Snapshot(), Hub.Line.StationMm, Hub.Settings.LineLengthMm,
                           Hub.Settings.PartSizeMm, Hub.Line.State);
            _io.RefreshValues();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                if (Hub.IsInitialized) Hub.Settings.Save();
            }
            base.Dispose(disposing);
        }

        // ─────────────────────────── 产线可视化 ───────────────────────────

        private sealed class LineCanvas : Control
        {
            private IReadOnlyList<LinePart> _parts = Array.Empty<LinePart>();
            private double _stationMm = 2000, _lengthMm = 3600, _partSizeMm = 40;
            private LineState _state = LineState.Stopped;

            public LineCanvas()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Panel;
            }

            public void Update(IReadOnlyList<LinePart> parts, double stationMm, double lengthMm, double partSizeMm, LineState state)
            {
                _parts = parts;
                _stationMm = stationMm;
                _lengthMm = Math.Max(1000, lengthMm);
                _partSizeMm = partSizeMm;
                _state = state;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Panel);

                int padL = 30, padR = 30;
                int beltH = 76;
                // 相机盒画在皮带上方 96px 处，画布太矮时要保证上方留白够，否则相机盒会被裁掉
                int beltY = Math.Max(Height / 2, 140);
                if (beltY > Height - 42) beltY = Height - 42;
                var belt = new Rectangle(padL, beltY - beltH / 2, Width - padL - padR, beltH);
                if (belt.Width < 100) return;

                double leftMm = _stationMm - _lengthMm / 2;
                float MapX(double mm) => (float)(belt.Left + (mm - leftMm) / _lengthMm * belt.Width);

                // 皮带
                using (var bb = new LinearGradientBrush(belt, Color.FromArgb(52, 55, 62), Color.FromArgb(36, 39, 45), 90f))
                    g.FillRectangle(bb, belt);
                using (var pen = new Pen(Theme.Border))
                    g.DrawRectangle(pen, belt);

                // 皮带纹理（随线体相位滚动，体现"线在动"）
                double phase = Hub.Line.BeltPhaseMm % 40;
                for (double mm = leftMm - phase; mm < leftMm + _lengthMm; mm += 40)
                {
                    float x = MapX(mm);
                    if (x < belt.Left || x > belt.Right) continue;
                    using var sp = new Pen(Color.FromArgb(28, 255, 255, 255));
                    g.DrawLine(sp, x, belt.Top + 3, x, belt.Bottom - 3);
                }

                // 刻度
                using (var tick = new Pen(Color.FromArgb(60, Theme.Muted)))
                using (var f = Theme.Mono(7.5F))
                using (var b = new SolidBrush(Theme.Muted))
                {
                    for (double mm = leftMm; mm <= leftMm + _lengthMm; mm += 500)
                    {
                        float x = MapX(mm);
                        g.DrawLine(tick, x, belt.Bottom + 4, x, belt.Bottom + 12);
                        g.DrawString($"{mm - _stationMm:F0}", f, b, x - 10, belt.Bottom + 14);
                    }
                }

                // 相机工位
                float sx = MapX(_stationMm);
                using (var sp = new Pen(Color.FromArgb(160, Theme.Cyan), 1.5F) { DashStyle = DashStyle.Dash })
                    g.DrawLine(sp, sx, belt.Top - 58, sx, belt.Bottom + 6);
                var camBox = new RectangleF(sx - 34, belt.Top - 96, 68, 34);
                using (var cb = new SolidBrush(Color.FromArgb(220, 22, 26, 32)))
                    g.FillRectangle(cb, camBox);
                using (var cp = new Pen(Theme.Cyan, 1.4F))
                    g.DrawRectangle(cp, camBox.X, camBox.Y, camBox.Width, camBox.Height);
                using (var cf = Theme.F(8.5F, FontStyle.Bold))
                using (var cbrush = new SolidBrush(Theme.Cyan))
                    g.DrawString("相机工位", cf, cbrush, camBox.X + 21, camBox.Y + 9);
                using (var cone = new SolidBrush(Color.FromArgb(40, Theme.Cyan)))
                    g.FillPolygon(cone, new[]
                    {
                        new PointF(sx - 30, belt.Top - 62),
                        new PointF(sx + 30, belt.Top - 62),
                        new PointF(sx + 8, belt.Top + 4),
                        new PointF(sx - 8, belt.Top + 4),
                    });

                // 工件
                float sizePx = (float)Math.Max(18, Math.Min(46, _partSizeMm * 1.05));
                foreach (var p in _parts)
                {
                    float x = MapX(p.PositionMm);
                    if (x < belt.Left - sizePx || x > belt.Right + sizePx) continue;

                    var rect = new RectangleF(x - sizePx / 2, beltY - sizePx / 2, sizePx, sizePx);
                    Color fill = p.Stage switch
                    {
                        PartStage.Done => p.InspectedOk == true ? Theme.Ok : Theme.Ng,
                        PartStage.AtStation or PartStage.Inspecting => Theme.Cyan,
                        _ => Color.FromArgb(150, 160, 172)
                    };
                    using (var b = new SolidBrush(Color.FromArgb(210, fill)))
                        g.FillRectangle(b, rect);
                    using (var pen = new Pen(fill, 1.6F))
                        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

                    // 内孔示意
                    using (var hole = new SolidBrush(Color.FromArgb(120, 20, 22, 26)))
                    {
                        float r = sizePx / 8;
                        g.FillEllipse(hole, rect.Left + sizePx * 0.18f, rect.Top + sizePx * 0.18f, r * 2, r * 2);
                        g.FillEllipse(hole, rect.Right - sizePx * 0.18f - r * 2, rect.Bottom - sizePx * 0.18f - r * 2, r * 2, r * 2);
                    }

                    if (p.InspectedOk == false)
                    {
                        using var f = Theme.F(9F, FontStyle.Bold);
                        using var b = new SolidBrush(Theme.Ng);
                        g.DrawString("NG", f, b, rect.X - 2, rect.Bottom + 2);
                    }
                }

                // 状态
                using (var f = Theme.F(9F, FontStyle.Bold))
                using (var b = new SolidBrush(_state switch
                {
                    LineState.Running => Theme.Ok,
                    LineState.Holding => Theme.Warn,
                    _ => Theme.Muted
                }))
                {
                    string text = _state switch
                    {
                        LineState.Running => "● 输送中",
                        LineState.Holding => "● 停位待检",
                        _ => "● 静止"
                    };
                    g.DrawString(text, f, b, 12, 10);
                }

                using (var f = Theme.F(8.5F))
                using (var b = new SolidBrush(Theme.Muted))
                {
                    g.DrawString($"线速 {Hub.Settings.LineSpeedMmS:F0} mm/s   间距 {Hub.Settings.PartPitchMm:F0} mm   在线 {_parts.Count} 件", f, b, 12, 30);
                }
            }
        }

        // ─────────────────────────── IO 监视 ───────────────────────────

        private sealed class IoPanel : Control
        {
            public IoPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Panel;
            }

            public void RefreshValues() => Invalidate();

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Panel);

                using (var f = Theme.F(9F, FontStyle.Bold))
                using (var b = new SolidBrush(Theme.SubText))
                    g.DrawString("PLC IO 监视", f, b, 12, 10);

                int y = 36;
                foreach (var tag in PlcTags.All)
                {
                    bool on = Hub.Plc.Read(tag);
                    var dot = new RectangleF(16, y + 5, 10, 10);
                    using (var glow = new SolidBrush(on ? Color.FromArgb(90, Theme.Ok) : Color.FromArgb(20, 255, 255, 255)))
                        g.FillEllipse(glow, dot.X - 4, dot.Y - 4, 18, 18);
                    using (var b = new SolidBrush(on ? Theme.Ok : Color.FromArgb(60, 66, 76)))
                        g.FillEllipse(b, dot);
                    using (var f = Theme.F(9F))
                    using (var tb = new SolidBrush(on ? Theme.Text : Theme.Muted))
                        g.DrawString(tag, f, tb, 34, y + 1);
                    y += 26;
                }

                using (var pen = new Pen(Theme.Border))
                    g.DrawLine(pen, 10, y + 4, Width - 12, y + 4);

                using (var f = Theme.F(8.5F))
                using (var b = new SolidBrush(Hub.Network.IsUp ? Theme.Muted : Theme.Ng))
                {
                    g.DrawString(Hub.Network.IsUp ? "通讯链路：正常" : "通讯链路：中断", f, b, 12, y + 12);
                    g.DrawString($"相机取图 {Hub.Camera.GrabCount} 次 / 丢帧 {Hub.Camera.DropCount}", f, b, 12, y + 32);
                    g.DrawString($"入库记录 {Hub.Db.WrittenCount} 条", f, b, 12, y + 52);
                }
            }
        }
    }
}
